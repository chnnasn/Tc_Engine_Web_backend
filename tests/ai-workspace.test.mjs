import { emailRegister } from './email-helper.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

test('knowledge CAS, experiment provenance and durable tool evidence isolate owners and survive restart', { timeout: 90000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-workspace-'))
  const secret = 'workspace-tests-secret-01234567890123456789'
  let child, base, token, finish
  const agent = createServer(async (req, res) => {
    let raw = ''; for await (const chunk of req) raw += chunk
    token = JSON.parse(raw).sessionToken
    await new Promise(resolve => { finish = resolve; res.once('close', resolve) })
    if (!res.destroyed) { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ output: 'done' })) }
  })
  await new Promise(resolve => agent.listen(0, '127.0.0.1', resolve))
  const stop = async () => { if (child?.exitCode === null) await new Promise(resolve => { child.once('exit', resolve); child.kill() }) }
  async function start() {
    const cwd = resolve('TomCat.Api')
    child = spawn('dotnet', [join(cwd, 'bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], { cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail'), Agent__Url: `http://127.0.0.1:${agent.address().port}`, Agent__Secret: secret } })
    base = await new Promise((resolve, reject) => {
      let output = ''; const timer = setTimeout(() => reject(new Error(output)), 15000)
      const read = bytes => { output += bytes; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.once('error', reject)
    })
  }
  const cookies = {}
  const request = (path, method = 'GET', body, user = 'alice', headers = {}) => fetch(base + path, { method, headers: { Cookie: cookies[user] || '', 'Content-Type': 'application/json', 'X-TomCat-Request': '1', ...headers }, body: body === undefined ? undefined : JSON.stringify(body) })
  const wait = async read => { for (let i = 0; i < 150; i++) { const value = await read(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 30)) } throw new Error('Timed out') }
  try {
    await start()
    for (const user of ['alice', 'bob']) {
      const response = await emailRegister((path, body) => request('/v1' + path, 'POST', body, user), directory, user, 'workspace-password-12345')
      assert.equal(response.status, 200); cookies[user] = response.headers.get('set-cookie').split(';')[0]
    }
    const project = await (await request('/v1/projects', 'POST', { name: 'workspace', template: '2D' })).json()
    const root = `/v1/projects/${project.id}`
    const note = { key: 'movement', content: 'Speed in world units per second.', expectedVersion: 0 }
    assert.equal((await request(root + '/ai-knowledge', 'PUT', note, 'bob')).status, 404)
    assert.equal((await request(root + '/ai-knowledge', 'PUT', note)).status, 200)
    assert.equal((await request(root + '/ai-knowledge', 'PUT', note)).status, 409)
    assert.equal((await request(root + '/ai-knowledge', 'PUT', { ...note, expectedVersion: 1, content: 'Updated rule' })).status, 200)
    const content = await (await request(root + '/ai-knowledge')).json()
    assert.equal(content.notes[0].version, 2)
    assert.equal((await request(root + '/ai-knowledge', 'GET', undefined, 'bob')).status, 404)
    const payload = { schemaVersion: 1, project: 'SchemaVersion: 4', settings: { 'BuildSettings.json': '{}' }, scenes: { '18446744073709551615': 'Scene: Main' }, assets: [] }
    const saved = await request(root + '/revisions', 'POST', payload, 'alice', { 'If-None-Match': '*' })
    assert.equal(saved.status, 201)
    const revision = (await saved.json()).etag.slice(1, -1)
    const forkInput = { name: 'trial', baseRevisionId: revision }
    assert.equal((await request(root + '/experiments', 'POST', forkInput, 'bob')).status, 404)
    assert.equal((await request(root + '/experiments', 'POST', { ...forkInput, baseRevisionId: 'wrong' })).status, 404)
    const fork = await (await request(root + '/experiments', 'POST', forkInput)).json()
    const metadata = await (await request(`/v1/projects/${fork.id}/experiment`)).json()
    assert.deepEqual(metadata, { sourceProjectId: project.id, baseRevisionId: revision })
    assert.equal((await request(`/v1/projects/${fork.id}/experiment`, 'GET', undefined, 'bob')).status, 404)
    assert.deepEqual((await (await request(`/v1/projects/${fork.id}/ai-knowledge`)).json()).notes, [])
    const session = await (await request(root + '/ai-sessions/', 'POST')).json()
    const lease = await (await request('/v1/editor-sessions/', 'POST', { projectId: project.id, engineCommit: '0'.repeat(40) })).json()
    const live = `/v1/editor-sessions/${lease.editorSessionId}`
    assert.equal((await request(live + '/agent-runs', 'POST', { runId: 'evidence-test', sessionId: session.sessionId, prompt: 'work' })).status, 202)
    await wait(() => token)
    const call = body => request('/internal/editor-session/call', 'POST', body, 'anonymous', { Authorization: `Bearer ${token}` })
    const save = { requestId: 'knowledge-1', name: 'project_knowledge_save', arguments: { key: 'observed', content: 'Observation', expected_version: 0 } }
    const first = await (await call(save)).json()
    assert.equal(first.ok, true)
    assert.deepEqual(await (await call({ ...save, isRetry: true })).json(), first)
    const pending = call({ requestId: 'patch-1', name: 'scene_apply_patch', arguments: { label: 'test', scene_version: '1:1', operations: [] } })
    const command = await (await request(live + '/commands')).json()
    assert.equal(command.name, 'scene_apply_patch')
    assert.equal((await request(live + '/commands/patch-1/result', 'POST', { ok: false, error: { code: 'INVALID_ARGUMENT', message: 'Empty batch' } })).status, 204)
    assert.equal((await (await pending).json()).ok, false)
    const evidencePath = root + '/ai-runs/evidence-test/events'
    let evidence = await (await request(evidencePath)).json()
    assert.equal(evidence.events.length, 2, 'retry does not duplicate durable events')
    assert.equal(evidence.events[0].state, 'succeeded'); assert.equal(evidence.events[1].state, 'failed')
    assert.equal((await request(evidencePath, 'GET', undefined, 'bob')).status, 404)
    assert.equal((await (await request(evidencePath + '?after=' + evidence.events[0].id)).json()).events.length, 1)
    finish()
    await wait(async () => (await (await request(live + '/agent-runs/evidence-test')).json()).state === 'succeeded')
    await stop(); await start()
    evidence = await (await request(evidencePath)).json()
    assert.equal(evidence.events.length, 2)
    assert.equal((await (await request(root + '/ai-knowledge')).json()).notes.find(n => n.key === 'observed').sourceRunId, 'evidence-test')
    assert.equal((await request(root, 'DELETE')).status, 204)
    assert.equal((await request(evidencePath)).status, 404)
    assert.equal((await (await request(`/v1/projects/${fork.id}/experiment`)).json()).sourceProjectId, null)
  } finally {
    finish?.(); await stop(); await new Promise(resolve => agent.close(resolve))
    await rm(directory, { recursive: true, force: true })
  }
})
