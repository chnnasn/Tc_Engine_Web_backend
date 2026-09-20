import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

test('editor leases enforce ownership, request identity, result binding and revocation', { timeout: 60000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-editor-test-'))
  let child, token
  const secret = 'test-service-secret-01234567890123456789'
  const agent = createServer(async (req, res) => {
    assert.equal(req.headers.authorization, `Bearer ${secret}`)
    let body = ''; for await (const chunk of req) body += chunk
    token = JSON.parse(body).sessionToken
    res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"output":"test"}')
  })
  await new Promise(ok => agent.listen(0, '127.0.0.1', ok))
  try {
    const apiDir = resolve('TomCat.Api')
    child = spawn('dotnet', [join(apiDir, 'bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
      cwd: apiDir, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Agent__Url: `http://127.0.0.1:${agent.address().port}`, Agent__Secret: secret },
    })
    const base = await new Promise((ok, no) => {
      let output = ''; const timer = setTimeout(() => no(new Error(output)), 20000)
      const read = bytes => { output += bytes; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); ok(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.on('error', no)
    })
    const cookies = {}
    const request = (path, method = 'GET', body, user = 'alice', headers = {}) => fetch(base + path, {
      method, headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1', Cookie: cookies[user] || '', ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
    for (const user of ['alice', 'bob']) {
      const response = await request('/v1/auth/register', 'POST', { username: user, password: 'long-test-password-123' }, user)
      assert.equal(response.status, 201); cookies[user] = response.headers.get('set-cookie').split(';')[0]
    }
    const project = await (await request('/v1/projects', 'POST', { name: 'editor test', template: '2D' })).json()
    const registration = { projectId: project.id, engineCommit: '0'.repeat(40) }
    assert.equal((await request('/v1/editor-sessions/', 'POST', registration, 'bob')).status, 404)
    const registered = await (await request('/v1/editor-sessions/', 'POST', registration)).json()
    assert.equal(registered.token, undefined)
    const root = `/v1/editor-sessions/${registered.editorSessionId}`
    assert.equal((await request(root + '/commands', 'GET', undefined, 'bob')).status, 404)
    assert.equal((await request(root + '/agent', 'POST', { prompt: 'test' }, 'bob')).status, 404)
    assert.equal((await request(root + '/agent', 'POST', { prompt: 'test' })).status, 200)
    assert.equal(token.length, 64)
    const internal = (body, headers = {}) => request('/internal/editor-session/call', 'POST', body, 'anonymous', { Authorization: `Bearer ${token}`, ...headers })
    assert.equal((await internal({ requestId: 'r', name: 'entity_create', arguments: { name: 'P' } }, { Origin: 'https://evil.example' })).status, 401)
    assert.equal((await internal({ requestId: 'r', name: 'shell', arguments: {} })).status, 400)
    const missing = await (await internal({ requestId: 'missing', name: 'entity_create', arguments: { name: 'P' }, isRetry: true })).json()
    assert.equal(missing.error.code, 'REQUEST_EXPIRED')
    const body = { requestId: 'create1', name: 'entity_create', arguments: { name: 'Player', scene_version: '1:2' } }
    const result = { ok: true, data: { entity: { id: '18446744073709551615' } } }
    const pending = internal(body)
    const command = await (await request(root + '/commands')).json()
    assert.equal(command.requestId, 'create1')
    assert.equal((await request(root + '/commands/create1/result', 'POST', result, 'bob')).status, 404)
    assert.equal((await request(root + '/commands/wrong/result', 'POST', result)).status, 404)
    assert.equal((await request(root + '/commands/create1/result', 'POST', result)).status, 204)
    assert.deepEqual(await (await pending).json(), result)
    assert.deepEqual(await (await internal({ ...body, isRetry: true })).json(), result)
    assert.equal((await (await internal({ ...body, arguments: { name: 'Different' } })).json()).error.code, 'REQUEST_ID_REUSED')
    assert.equal((await request(root + '/commands/create1/result', 'POST', result)).status, 409)
    assert.equal((await request(root, 'DELETE')).status, 204)
    assert.equal((await internal(body)).status, 401)
    assert.equal((await request(root + '/commands')).status, 404)
  } finally {
    if (child?.exitCode === null) await new Promise(ok => { child.once('exit', ok); child.kill() })
    await new Promise(ok => agent.close(ok))
    await rm(directory, { recursive: true, force: true })
  }
})
