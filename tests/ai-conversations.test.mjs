import { emailRegister } from './email-helper.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

test('project conversations persist, isolate owners/sessions and bound model history', { timeout: 90000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-conversations-'))
  const secret = 'test-conversation-secret-01234567890123456789'
  const received = []
  let child, base, release
  const agent = createServer(async (req, res) => {
    let bytes = ''; for await (const part of req) bytes += part
    const input = JSON.parse(bytes); received.push(input)
    if (input.prompt === 'hold') await new Promise(resolve => { release = resolve; res.once('close', resolve) })
    if (!res.destroyed) { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ output: 'reply:' + input.prompt })) }
  })
  await new Promise(resolve => agent.listen(0, '127.0.0.1', resolve))
  const stop = async () => { if (child?.exitCode === null) await new Promise(resolve => { child.once('exit', resolve); child.kill() }) }
  async function start() {
    const apiDir = resolve('TomCat.Api')
    child = spawn('dotnet', [join(apiDir, 'bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], { cwd: apiDir, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail'), Agent__Url: `http://127.0.0.1:${agent.address().port}`, Agent__Secret: secret } })
    base = await new Promise((resolve, reject) => {
      let output = ''; const timer = setTimeout(() => reject(new Error(output)), 15000)
      const read = data => { output += data; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.once('error', reject)
    })
  }
  const cookies = {}
  const request = (path, method = 'GET', body, user = 'alice') => fetch(base + path, { method, headers: { Cookie: cookies[user] || '', 'Content-Type': 'application/json', 'X-TomCat-Request': '1' }, body: body === undefined ? undefined : JSON.stringify(body) })
  const wait = async read => { for (let i = 0; i < 250; i++) { const result = await read(); if (result) return result; await new Promise(resolve => setTimeout(resolve, 20)) } throw new Error('Timed out') }
  try {
    await start()
    for (const user of ['alice', 'bob']) {
      const response = await emailRegister((path, body) => request('/v1' + path, 'POST', body, user), directory, user, 'conversation-password-123')
      assert.equal(response.status, 200); cookies[user] = response.headers.get('set-cookie').split(';')[0]
    }
    const createProject = async name => (await request('/v1/projects', 'POST', { name, template: '2D' })).json()
    const a = await createProject('A'), b = await createProject('B')
    const root = `/v1/projects/${a.id}/ai-sessions/`
    const s1 = await (await request(root, 'POST')).json(), s2 = await (await request(root, 'POST')).json()
    assert.notEqual(s1.sessionId, s2.sessionId)
    assert.equal((await request(root, 'GET', undefined, 'bob')).status, 404)
    assert.equal((await request(root, 'POST', undefined, 'bob')).status, 404)
    assert.equal((await request(root + s1.sessionId, 'GET', undefined, 'bob')).status, 404)
    assert.equal((await request(`/v1/projects/${b.id}/ai-sessions/${s1.sessionId}`)).status, 404)
    assert.deepEqual(await (await request(`/v1/projects/${b.id}/ai-sessions/`)).json(), [])
    const lease = async (project = a.id) => `/v1/editor-sessions/${(await (await request('/v1/editor-sessions/', 'POST', { projectId: project, engineCommit: '0'.repeat(40) })).json()).editorSessionId}`
    const run = async (sessionId, prompt, runId = crypto.randomUUID()) => {
      const live = await lease()
      const body = { runId, sessionId, prompt }
      const accepted = await request(live + '/agent-runs', 'POST', body)
      assert.equal(accepted.status, 202, await accepted.text())
      assert.equal((await request(live + '/agent-runs', 'POST', body)).status, 202)
      await wait(async () => { const result = await (await request(live + '/agent-runs/' + runId)).json(); return result.state === 'succeeded' })
      await request(live, 'DELETE')
      await wait(async () => (await (await request(root + sessionId)).json()).turns.some(turn => turn.runId === runId && turn.state === 'succeeded'))
      return runId
    }
    const firstRun = await run(s1.sessionId, 'make it blue')
    await run(s1.sessionId, 'what color?')
    assert.deepEqual(received[0].history, [])
    assert.deepEqual(received[1].history, [{ role: 'user', content: 'make it blue' }, { role: 'assistant', content: 'reply:make it blue' }])
    await run(s2.sessionId, 'another conversation')
    assert.deepEqual(received[2].history, [])
    const wrongProjectLease = await lease(b.id)
    assert.equal((await request(wrongProjectLease + '/agent-runs', 'POST', { runId: 'foreign', prompt: 'x', sessionId: s1.sessionId })).status, 404)
    await request(wrongProjectLease, 'DELETE')
    const duplicateLease = await lease()
    assert.equal((await request(duplicateLease + '/agent-runs', 'POST', { runId: firstRun, prompt: 'make it blue', sessionId: s1.sessionId })).status, 409)
    await request(duplicateLease, 'DELETE')
    const holdLease = await lease(), otherLease = await lease()
    await request(holdLease + '/agent-runs', 'POST', { runId: 'held', sessionId: s1.sessionId, prompt: 'hold' })
    await wait(() => release)
    assert.equal((await request(otherLease + '/agent-runs', 'POST', { runId: 'parallel', sessionId: s1.sessionId, prompt: 'x' })).status, 409)
    release()
    await wait(async () => (await (await request(root + s1.sessionId)).json()).turns.some(turn => turn.runId === 'held' && turn.state === 'succeeded'))
    await request(holdLease, 'DELETE'); await request(otherLease, 'DELETE')
    for (let i = 0; i < 30; i++) await run(s1.sessionId, `turn ${i}`)
    assert.equal(received.at(-1).history.length, 40)
    const latest = await (await request(root + s1.sessionId)).json()
    assert.equal(latest.turns.length, 30); assert.equal(latest.hasMore, true)
    const older = await (await request(root + s1.sessionId + `?before=${latest.nextBefore}`)).json()
    assert.equal(older.turns.length, 3); assert.equal(older.hasMore, false)
    assert.equal(new Set([...older.turns, ...latest.turns].map(turn => turn.id)).size, 33)
    await stop(); await start()
    assert.equal((await (await request(root + s1.sessionId)).json()).turns.length, 30)
    assert.equal((await (await request(root)).json()).length, 2)
    const restartLease = await lease()
    release = undefined
    await request(restartLease + '/agent-runs', 'POST', { runId: 'restart', sessionId: s1.sessionId, prompt: 'hold' })
    await wait(() => release)
    await stop(); release(); await start()
    const recovered = (await (await request(root + s1.sessionId)).json()).turns.at(-1)
    assert.equal(recovered.state, 'failed')
    assert.match(recovered.error, /停止|中断|重启/)
    await run(s1.sessionId, 'continue after restart')
    assert.ok(received.at(-1).history.every(message => message.content !== 'hold'), 'interrupted input is not included in model history')
    assert.equal((await request(`/v1/projects/${a.id}`, 'DELETE')).status, 204)
    assert.equal((await request(root + s1.sessionId)).status, 404)
  } finally {
    release?.(); await stop(); await new Promise(resolve => agent.close(resolve))
    await rm(directory, { recursive: true, force: true })
  }
})
