import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn, execFileSync } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, dirname, resolve, basename } from 'node:path'
import { createServer } from 'node:net'
import { createHash } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'

const executable = process.env.TEST_REDIS_SERVER
const apiDir = resolve('TomCat.Api')
async function start(command, args, cwd, env, pattern) {
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
  let output = ''
  await new Promise((yes, no) => {
    const timer = setTimeout(() => { child.kill(); no(new Error(output)) }, 20000)
    const read = bytes => { output += bytes; if (pattern.test(output)) { clearTimeout(timer); yes() } }
    child.stdout.on('data', read); child.stderr.on('data', read)
    child.once('error', error => { clearTimeout(timer); no(error) })
    child.once('exit', code => { clearTimeout(timer); no(new Error(`Exit ${code}: ${output}`)) })
  })
  return { child, output: () => output, async stop() {
    if (child.exitCode === null && child.signalCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() })
  } }
}
async function freePort() {
  const server = createServer()
  await new Promise(yes => server.listen(0, '127.0.0.1', yes))
  const port = server.address().port
  await new Promise(yes => server.close(yes))
  return port
}

test('Redis working snapshots and SQLite checkpoints', { timeout: 90000, skip: !executable && 'Set TEST_REDIS_SERVER to a Redis server executable' }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-redis-integration-'))
  const port = await freePort()
  const cliPath = join(dirname(executable), process.platform === 'win32' ? 'redis-cli.exe' : 'redis-cli')
  const cli = (...args) => execFileSync(cliPath, ['-h', '127.0.0.1', '-p', String(port), '--raw', ...args], { windowsHide: true, encoding: 'utf8' }).trim()
  let redis, api, base
  const startRedis = () => start(executable, ['--bind', '127.0.0.1', '--port', String(port), '--appendonly', 'yes', '--appendfsync', 'always', '--maxmemory-policy', 'noeviction'], directory, {}, /Ready to accept connections/i)
  const startApi = async (interval = '3600') => {
    api = await start('dotnet', [join(apiDir, 'bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], apiDir,
      { ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Redis__ConnectionString: `127.0.0.1:${port},connectTimeout=1000,asyncTimeout=1000`, Redis__KeyPrefix: 'test:', Redis__FlushIntervalSeconds: interval }, /Now listening on:/)
    base = api.output().match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/)[1]
  }
  const cookies = new Map()
  const request = async (path, { method = 'GET', body, bytes, user = 'alice', headers = {} } = {}) => {
    const response = await fetch(base + '/v1' + path, { method, headers: { 'X-TomCat-Request': '1', 'Content-Type': bytes ? 'application/octet-stream' : 'application/json', ...(cookies.has(user) ? { Cookie: cookies.get(user) } : {}), ...headers }, body: bytes ?? (body === undefined ? undefined : JSON.stringify(body)) })
    if (response.headers.has('set-cookie')) cookies.set(user, response.headers.get('set-cookie').split(';')[0])
    return response
  }
  const count = () => { const db = new DatabaseSync(join(directory, 'tomcat.db')); try { return db.prepare('SELECT COUNT(*) AS n FROM revisions').get().n } finally { db.close() } }
  let project, manifest, token
  const put = (archive, etag = token, extra = {}) => request(`/projects/${project.id}/working-state`, { method: 'PUT', body: { ...manifest, archive }, headers: etag ? { 'If-Match': etag } : { 'If-None-Match': '*' }, ...extra })
  try {
    redis = await startRedis(); await startApi()
    for (const user of ['alice', 'bob']) assert.equal((await request('/auth/register', { method: 'POST', user, body: { username: user, password: 'integration-test-password' } })).status, 201)
    project = await (await request('/projects', { method: 'POST', body: { name: 'Realtime', template: '2D' } })).json()
    const bytes = Buffer.from('{}'), hash = createHash('sha256').update(bytes).digest('hex')
    const upload = await (await request(`/projects/${project.id}/uploads/${hash}`, { method: 'PUT', bytes })).json()
    manifest = { schemaVersion: 2, engineCommit: 'a'.repeat(40), sceneHandle: '123', archive: 'first', files: ['Project.tcproj', 'ProjectSettings/BuildSettings.json', 'ProjectSettings/ProjectSettings.json', 'ProjectSettings/PlayerSettings.json'].map(path => ({ path, ...upload })) }

    await t.test('working state is recoverable before any database revision exists', async () => {
      assert.equal((await (await request('/projects/sync-config')).json()).enabled, true)
      const response = await put('first'); assert.equal(response.status, 202); token = response.headers.get('etag')
      assert.equal(count(), 0)
      assert.equal((await (await request(`/projects/${project.id}/working-state`)).json()).archive, 'first')
      assert.equal((await (await request('/projects')).json())[0].currentRevisionId, token.slice(1, -1))
      assert.equal((await (await request(`/projects/${project.id}/sync-status`)).json()).persisted, false)
      assert.equal(cli('SCARD', 'test:dirty'), '1')
    })
    await t.test('ownership, preconditions and resource references are enforced', async () => {
      assert.equal((await put('intrusion', token, { user: 'bob' })).status, 404)
      assert.equal((await request(`/projects/${project.id}/working-state`, { user: 'bob' })).status, 404)
      assert.equal((await request(`/projects/${project.id}/uploads/by-hash/${hash}`, { user: 'bob' })).status, 404)
      assert.equal((await put('missing precondition', token, { headers: {} })).status, 428)
      assert.equal((await put('stale', '"wrong"')).status, 412)
      const forged = { ...manifest, files: manifest.files.map(f => ({ ...f, uploadId: '0'.repeat(32) })) }
      assert.equal((await put('forged', token, { body: forged })).status, 400)
      const responses = await Promise.all([put('race-a'), put('race-b')])
      assert.deepEqual(responses.map(r => r.status).sort(), [202, 412]); token = responses.find(r => r.status === 202).headers.get('etag')
    })
    await t.test('API and Redis restart retain acknowledged working data through AOF', async () => {
      await api.stop(); await redis.stop(); redis = await startRedis(); await startApi()
      const response = await request(`/projects/${project.id}/working-state`)
      assert.equal(response.headers.get('etag'), token); assert.equal(count(), 0)
    })
    await t.test('periodic checkpoint keeps the same ETag and clears only committed work', async () => {
      const pendingSnapshot = cli('GET', `test:project:${project.id}:state`)
      await api.stop(); await startApi('1')
      const deadline = Date.now() + 10000
      while (count() === 0 && Date.now() < deadline) await new Promise(yes => setTimeout(yes, 100))
      assert.equal(count(), 1)
      const status = await (await request(`/projects/${project.id}/sync-status`)).json()
      assert.equal(status.etag, token); assert.equal(status.persisted, true); assert.equal(cli('SCARD', 'test:dirty'), '0')
      // Simulate a crash after SQLite committed but before Redis cleanup.
      cli('SET', `test:project:${project.id}:state`, pendingSnapshot); cli('SADD', 'test:dirty', project.id)
      const retryDeadline = Date.now() + 10000
      while (cli('SCARD', 'test:dirty') !== '0' && Date.now() < retryDeadline) await new Promise(yes => setTimeout(yes, 100))
      assert.equal(cli('SCARD', 'test:dirty'), '0'); assert.equal(count(), 1)
      await api.stop(); await startApi()
    })
    await t.test('manual save rejects stale state and immediately persists latest snapshot', async () => {
      const old = token
      const update = await put('manual-latest'); token = update.headers.get('etag')
      assert.equal((await request(`/projects/${project.id}/revisions`, { method: 'POST', body: manifest, headers: { 'If-Match': old } })).status, 412)
      const aiCheckpoint = { runId: 'c'.repeat(32), phase: 'start', sceneVersion: '123:5' }
      assert.equal((await request(`/projects/${project.id}/working-state`, { method: 'PUT', body: { ...manifest, aiCheckpoint }, headers: { 'If-Match': token } })).status, 400)
      const response = await request(`/projects/${project.id}/revisions`, { method: 'POST', body: { ...manifest, archive: 'manual-final', aiCheckpoint }, headers: { 'If-Match': token } })
      assert.equal(response.status, 201); token = response.headers.get('etag'); assert.equal(count(), 2)
      assert.equal((await (await request(`/projects/${project.id}/working-state`)).json()).archive, 'manual-final')
      const checkpoint = (await (await request(`/projects/${project.id}/revisions`)).json()).find(r => r.revisionId === token.slice(1, -1))
      assert.deepEqual(checkpoint.aiCheckpoint, aiCheckpoint)
      assert.equal((await (await request(`/projects/${project.id}/sync-status`)).json()).persisted, true)
    })
    await t.test('Redis outage fails explicitly without silently writing stale data', async () => {
      await redis.stop()
      assert.equal((await put('offline')).status, 503); assert.equal(count(), 2)
      redis = await startRedis()
      await api.stop(); await startApi()
      assert.equal((await request(`/projects/${project.id}/working-state`)).headers.get('etag'), token)
    })
    await t.test('deletion removes pending state and cannot be resurrected by worker', async () => {
      assert.equal((await put('before-delete')).status, 202)
      assert.equal((await request(`/projects/${project.id}`, { method: 'DELETE' })).status, 204)
      assert.equal(cli('EXISTS', `test:project:${project.id}:state`), '0'); assert.equal(cli('SCARD', 'test:dirty'), '0')
      assert.equal(count(), 0)
    })
  } finally {
    await api?.stop(); await redis?.stop()
    // Only this test's mkdtemp directory is removed.
    assert.equal(dirname(resolve(directory)), resolve(tmpdir()))
    assert.ok(basename(directory).startsWith('tomcat-redis-integration-'))
    await rm(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 })
  }
})
