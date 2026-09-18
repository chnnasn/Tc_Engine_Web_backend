import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
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
    api = await start(directory)
    const cookies = new Map()
    async function request(path, { user = 'alice', method = 'GET', body, headers = {} } = {}) {
      const response = await fetch(`${api.baseUrl}${path}`, {
        method,
        redirect: 'manual',
        headers: {
          'Content-Type': 'application/json',
          'X-TomCat-Request': '1',
          ...(cookies.has(user) ? { Cookie: cookies.get(user) } : {}),
          ...headers,
        },
        body: body === undefined ? undefined : JSON.stringify(body),
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

    await t.test('database and session survive service restart', async () => {
      await api.stop()
      api = await start(directory)
      assert.equal((await request('/v1/auth/me')).status, 200)
      const restored = await (await request(`/v1/projects/${project.id}`)).json()
      assert.equal(restored.name, '重命名')
      assert.equal(restored.etag, current.etag)
      assert.deepEqual(await (await request(`/v1/projects/${project.id}/revisions/${first.revisionId}`)).json(), payload)
    })

    await t.test('logout, login and deletion', async () => {
      assert.equal((await request('/v1/auth/logout', { method: 'POST' })).status, 204)
      assert.equal((await request('/v1/auth/me')).status, 401)
      assert.equal((await request('/v1/auth/login', { method: 'POST', body: { username: 'alice', password: 'test-password-12345' } })).status, 200)
      assert.equal((await request(`/v1/projects/${project.id}`, { method: 'DELETE' })).status, 204)
      assert.equal((await request(`/v1/projects/${project.id}/revisions/${first.revisionId}`)).status, 404)
      assert.deepEqual(await (await request('/v1/projects')).json(), [])
    })
  } finally {
    await api?.stop()
    await rm(directory, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 })
  }
})
