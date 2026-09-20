import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm, readFile } from 'node:fs/promises'
import { createHash } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const apiDirectory = fileURLToPath(new URL('../TomCat.Api/', import.meta.url))
const assembly = resolve(apiDirectory, 'bin/Release/net10.0/TomCat.Api.dll')

async function start(directory) {
  const process = spawn('dotnet', [assembly, '--urls', 'http://127.0.0.1:0'], {
    cwd: apiDirectory,
    env: { ...globalThis.process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  })
  let output = ''
  const baseUrl = await new Promise((resolveUrl, reject) => {
    const timer = setTimeout(() => { process.kill(); reject(new Error(`API startup timed out: ${output}`)) }, 20000)
    const fail = error => { clearTimeout(timer); reject(error) }
    process.once('error', fail)
    process.once('exit', code => fail(new Error(`API exited (${code}): ${output}`)))
    const read = chunk => {
      output += chunk.toString()
      const address = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/)
      if (address) { clearTimeout(timer); resolveUrl(address[1]) }
    }
    process.stdout.on('data', read)
    process.stderr.on('data', read)
  })
  return {
    baseUrl,
    diagnostics: () => output,
    async stop() {
      if (process.exitCode !== null || process.signalCode !== null) return
      await new Promise(resolveStop => { process.once('exit', resolveStop); process.kill() })
    },
  }
}

test('ASP.NET + SQLite HTTP lifecycle', { timeout: 90000 }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-api-test-'))
  let api
  try {
    const legacyDb = new DatabaseSync(join(directory, 'tomcat.db'))
    legacyDb.exec(await readFile(resolve(apiDirectory, 'Migrations/001_initial.sql'), 'utf8'))
    legacyDb.exec("INSERT INTO users VALUES('migration-user','legacy','unused','before'); INSERT INTO projects VALUES('migration-project','migration-user','legacy','','2D','migration-revision','before','before'); INSERT INTO revisions VALUES('migration-revision','migration-project','{\"schemaVersion\":1}','before');")
    legacyDb.close()
    api = await start(directory)
    const cookies = new Map()
    async function request(path, { user = 'alice', method = 'GET', body, bytes, headers = {} } = {}) {
      const response = await fetch(`${api.baseUrl}${path}`, {
        method,
        redirect: 'manual',
        headers: {
          'Content-Type': bytes === undefined ? 'application/json' : 'application/octet-stream',
          'X-TomCat-Request': '1',
          ...(cookies.has(user) ? { Cookie: cookies.get(user) } : {}),
          ...headers,
        },
        body: bytes ?? (body === undefined ? undefined : JSON.stringify(body)),
      })
      const cookie = response.headers.get('set-cookie')
      if (cookie) cookies.set(user, cookie.split(';')[0])
      if (response.status >= 500) throw new Error(`API ${response.status}: ${api.diagnostics()}`)
      return response
    }
    const payload = { schemaVersion: 1, project: 'SchemaVersion: 4', settings: { 'BuildSettings.json': '{}' }, scenes: { '18446744073709551615': 'Scene: Main' }, assets: [] }
    let project
    let first
    let current
    let fullRevision
    let manifest
    const fileBytes = new Map()
    const hash = bytes => createHash('sha256').update(bytes).digest('hex')

    await t.test('v1 database upgrades without changing existing revisions', async () => {
      const db = new DatabaseSync(join(directory, 'tomcat.db'))
      assert.equal(db.prepare('PRAGMA user_version').get().user_version, 2)
      assert.equal(db.prepare("SELECT payload FROM revisions WHERE id='migration-revision'").get().payload, '{"schemaVersion":1}')
      db.close()
    })

    await t.test('authentication, CSRF header, password validation and duplicate accounts', async () => {
      assert.equal((await request('/v1/projects')).status, 401)
      assert.equal((await request('/v1/auth/register', { method: 'POST', headers: { 'X-TomCat-Request': '' }, body: {} })).status, 403)
      assert.equal((await request('/v1/auth/register', { method: 'POST', body: { username: 'alice', password: 'short' } })).status, 400)
      for (const user of ['alice', 'bob']) {
        const response = await request('/v1/auth/register', { user, method: 'POST', body: { username: user, password: 'test-password-12345' } })
        assert.equal(response.status, 201)
        assert.match(response.headers.get('set-cookie'), /httponly/i)
        assert.match(response.headers.get('set-cookie'), /samesite=strict/i)
      }
      assert.equal((await request('/v1/auth/register', { method: 'POST', body: { username: 'ALICE', password: 'test-password-12345' } })).status, 409)
      assert.equal((await request('/v1/auth/login', { user: 'anonymous', method: 'POST', body: { username: 'alice', password: 'incorrect-password' } })).status, 401)
      assert.equal((await request('/v1/auth/me')).status, 200)
    })

    await t.test('projects persist and are isolated by owner', async () => {
      const response = await request('/v1/projects', { method: 'POST', body: { name: '测试项目', description: "quote ' remains data", template: '2D' } })
      assert.equal(response.status, 201)
      project = await response.json()
      assert.equal((await (await request('/v1/projects')).json()).length, 1)
      assert.deepEqual(await (await request('/v1/projects', { user: 'bob' })).json(), [])
      for (const method of ['GET', 'PUT', 'DELETE']) {
        assert.equal((await request(`/v1/projects/${project.id}`, { user: 'bob', method, ...(method === 'PUT' ? { body: { name: 'hijacked' } } : {}) })).status, 404)
      }
      assert.equal((await request(`/v1/projects/${project.id}`, { method: 'PUT', body: { name: '重命名', description: 'saved' } })).status, 200)
    })

    await t.test('revision preconditions and immutable payload round trip', async () => {
      const path = `/v1/projects/${project.id}/revisions`
      assert.equal((await request(path, { method: 'POST', body: payload })).status, 428)
      assert.equal((await request(path, { method: 'POST', body: { ...payload, schemaVersion: '1' }, headers: { 'If-None-Match': '*' } })).status, 400)
      const response = await request(path, { method: 'POST', body: payload, headers: { 'If-None-Match': '*' } })
      assert.equal(response.status, 201)
      first = await response.json()
      assert.equal(response.headers.get('etag'), first.etag)
      const second = await request(path, { method: 'POST', body: { ...payload, project: 'second' }, headers: { 'If-Match': first.etag } })
      assert.equal(second.status, 201)
      current = await second.json()
      assert.deepEqual(await (await request(`${path}/${first.revisionId}`)).json(), payload)
      const stale = await request(path, { method: 'POST', body: payload, headers: { 'If-Match': first.etag } })
      assert.equal(stale.status, 412)
      assert.equal(stale.headers.get('etag'), current.etag)
      assert.equal((await request(path, { user: 'bob', method: 'POST', body: payload, headers: { 'If-Match': current.etag } })).status, 404)
      assert.equal((await request(`${path}/${first.revisionId}`, { user: 'bob' })).status, 404)
    })

    await t.test('two writers with one ETag produce exactly one new revision', async () => {
      const path = `/v1/projects/${project.id}/revisions`
      const responses = await Promise.all([1, 2].map(n => request(path, { method: 'POST', body: { ...payload, project: `writer-${n}` }, headers: { 'If-Match': current.etag } })))
      assert.deepEqual(responses.map(response => response.status).sort(), [201, 412])
      current = await responses.find(response => response.status === 201).json()
      assert.equal((await (await request(path)).json()).length, 3)
    })

    await t.test('verified, idempotent binary uploads and project ownership', async () => {
      for (const [path, bytes] of [
        ['Project.tcproj', Buffer.from('Project:\n  AssetDirectory: Assets\nSchemaVersion: 4')],
        ['ProjectSettings/BuildSettings.json', Buffer.from('{"EntryScene":"18446744073709551615"}')],
        ['ProjectSettings/ProjectSettings.json', Buffer.from('{"name":"完整配置"}')],
        ['ProjectSettings/PlayerSettings.json', Buffer.from('{"width":1234}')],
        ['Assets/WebImports/pixel.tga', Buffer.from([0, 0, 2, 255])],
        ['Assets/WebImports/pixel.tga.tcmeta', Buffer.from('Handle: 18446744073709551615')],
      ]) fileBytes.set(path, bytes)
      const files = []
      for (const [path, bytes] of fileBytes) {
        const url = `/v1/projects/${project.id}/uploads/${hash(bytes)}`
        const result = await request(url, { method: 'PUT', bytes })
        assert.equal(result.status, 201)
        const upload = await result.json()
        assert.equal(upload.contentHash, hash(bytes)); assert.equal(upload.size, bytes.length)
        const repeated = await request(url, { method: 'PUT', bytes })
        assert.equal(repeated.status, 200); assert.deepEqual(await repeated.json(), upload)
        assert.equal((await request(url, { user: 'bob', method: 'PUT', bytes })).status, 404)
        assert.equal((await request(url, { user: 'anonymous', method: 'PUT', bytes })).status, 401)
        assert.equal((await request(url, { method: 'PUT', bytes, headers: { 'X-TomCat-Request': '' } })).status, 403)
        assert.equal((await request(`/v1/projects/${project.id}/uploads/${upload.uploadId}`, { user: 'bob' })).status, 404)
        files.push({ path, ...upload })
      }
      assert.equal((await request(`/v1/projects/${project.id}/uploads/${'0'.repeat(64)}`, { method: 'PUT', bytes: Buffer.from('mismatch') })).status, 400)
      assert.equal((await request(`/v1/projects/${project.id}/uploads/${'0'.repeat(64)}`, { method: 'PUT', bytes: Buffer.alloc(8 * 1024 * 1024 + 1) })).status, 413)
      assert.equal((await (await request(`/v1/projects/${project.id}`)).json()).etag, current.etag)
      manifest = { schemaVersion: 2, engineCommit: '0a731be0d56352d5ae5785ffaece3edd834238ab', sceneHandle: '18446744073709551615', archive: 'Scene: Complete', files }
    })

    await t.test('incomplete, forged and cross-project manifests cannot advance the project', async () => {
      const other = await (await request('/v1/projects', { method: 'POST', body: { name: 'other', template: '2D' } })).json()
      const bytes = fileBytes.get('Project.tcproj')
      const foreign = await (await request(`/v1/projects/${other.id}/uploads/${hash(bytes)}`, { method: 'PUT', bytes })).json()
      const variants = [
        { ...manifest, files: manifest.files.slice(1) },
        { ...manifest, files: manifest.files.filter(file => !file.path.endsWith('.tcmeta')) },
        { ...manifest, files: [...manifest.files, manifest.files[0]] },
        ...[
          { path: 'Assets/../Project.tcproj' },
          { path: 'Assets\\bad.png' },
          { uploadId: 'f'.repeat(32) },
          { uploadId: foreign.uploadId },
          { contentHash: 'f'.repeat(64) },
          { size: 12345 },
          { size: '1' },
        ].map(changed => ({ ...manifest, files: [{ ...manifest.files[0], ...changed }, ...manifest.files.slice(1)] })),
      ]
      for (const body of variants) assert.equal((await request(`/v1/projects/${project.id}/revisions`, { method: 'POST', body, headers: { 'If-Match': current.etag } })).status, 400)
      assert.equal((await (await request(`/v1/projects/${project.id}`)).json()).etag, current.etag)
      assert.equal((await (await request(`/v1/projects/${project.id}/revisions`)).json()).length, 3)
      await request(`/v1/projects/${other.id}`, { method: 'DELETE' })
    })

    await t.test('complete revisions pin bytes and concurrent saves still conflict', async () => {
      const responses = await Promise.all([1, 2].map(() => request(`/v1/projects/${project.id}/revisions`, { method: 'POST', body: manifest, headers: { 'If-Match': current.etag } })))
      assert.deepEqual(responses.map(r => r.status).sort(), [201, 412])
      fullRevision = await responses.find(r => r.status === 201).json(); current = fullRevision
      const original = await (await request(`/v1/projects/${project.id}/revisions/${fullRevision.revisionId}`)).json()
      assert.deepEqual(original, manifest)
      for (const file of manifest.files) {
        const response = await request(`/v1/projects/${project.id}/uploads/${file.uploadId}`)
        assert.equal(response.status, 200)
        assert.equal(response.headers.get('etag'), `"${file.contentHash}"`)
        assert.deepEqual(Buffer.from(await response.arrayBuffer()), fileBytes.get(file.path))
      }
      const changed = Buffer.from('new pixels')
      const uploaded = await request(`/v1/projects/${project.id}/uploads/${hash(changed)}`, { method: 'PUT', bytes: changed })
      assert.equal(uploaded.status, 201)
      assert.deepEqual(await (await request(`/v1/projects/${project.id}/revisions/${fullRevision.revisionId}`)).json(), manifest)
    })

    await t.test('database and session survive service restart', async () => {
      await api.stop()
      api = await start(directory)
      assert.equal((await request('/v1/auth/me')).status, 200)
      const restored = await (await request(`/v1/projects/${project.id}`)).json()
      assert.equal(restored.name, '重命名')
      assert.equal(restored.etag, current.etag)
      assert.deepEqual(await (await request(`/v1/projects/${project.id}/revisions/${fullRevision.revisionId}`)).json(), manifest)
      for (const file of manifest.files) assert.deepEqual(Buffer.from(await (await request(`/v1/projects/${project.id}/uploads/${file.uploadId}`)).arrayBuffer()), fileBytes.get(file.path))
      assert.deepEqual(await (await request(`/v1/projects/${project.id}/revisions/${first.revisionId}`)).json(), payload)
    })

    await t.test('logout, login and deletion', async () => {
      assert.equal((await request('/v1/auth/logout', { method: 'POST' })).status, 204)
      assert.equal((await request('/v1/auth/me')).status, 401)
      assert.equal((await request('/v1/auth/login', { method: 'POST', body: { username: 'alice', password: 'test-password-12345' } })).status, 200)
      assert.equal((await request(`/v1/projects/${project.id}`, { method: 'DELETE' })).status, 204)
      assert.equal((await request(`/v1/projects/${project.id}/revisions/${first.revisionId}`)).status, 404)
      assert.equal((await request(`/v1/projects/${project.id}/uploads/${manifest.files[0].uploadId}`)).status, 404)
      assert.deepEqual(await (await request('/v1/projects')).json(), [])
    })
  } finally {
    await api?.stop()
    await rm(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 })
  }
})
