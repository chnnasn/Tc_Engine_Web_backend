import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { emailRegister, mailedCode } from './email-helper.mjs'
async function fixture(run) {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-password-'))
  const child = spawn('dotnet', [resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], { cwd: resolve('TomCat.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail') } })
  let db
  try {
    const base = await new Promise((yes, no) => {
      let output = ''; const timer = setTimeout(() => { child.kill(); no(new Error(output)) }, 20000)
      const read = data => { output += data; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); yes(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.once('error', no)
    })
    let cookie = ''
    const request = async (path, body, session = cookie) => {
      const response = await fetch(base + '/v1' + path, { method: body ? 'POST' : 'GET', headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1', Cookie: session }, body: body ? JSON.stringify(body) : undefined })
      if (response.headers.has('set-cookie')) cookie = response.headers.get('set-cookie').split(';')[0]
      return response
    }
    const email = 'creator@example.com', password = 'original-password-123', next = 'replacement-password-123'
    const response = await emailRegister((path, body) => request(path, body), directory, 'creator', password); assert.equal(response.status, 200)
    const user = await response.json(); db = new DatabaseSync(join(directory, 'tomcat.db'))
    await run({ directory, db, request, email, password, next, user, cookie: () => cookie })
  } finally {
    db?.close(); if (child.exitCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() })
    await rm(directory, { recursive: true, force: true })
  }
}
test('password recovery rotates sessions, resists replay and keeps project ownership', () => fixture(async ({ directory, request, email, password, next, user, cookie }) => {
  const project = await (await request('/projects', { name: 'preserved', template: '2D' })).json()
  const oldCookie = cookie()
  const sent = await request('/auth/forgot-password', { email }); assert.equal(sent.status, 200)
  const { challengeId } = await sent.json(); const code = await mailedCode(directory, email)
  const wrong = code === '000000' ? '111111' : '000000'
  assert.equal((await request('/auth/reset-password', { challengeId, code: wrong, newPassword: next }, '')).status, 400)
  // Registration verification must never accept a password reset challenge.
  assert.equal((await request('/auth/verify-email', { challengeId, code }, '')).status, 400)
  const results = await Promise.all([1, 2].map(() => request('/auth/reset-password', { challengeId, code, newPassword: next }, '')))
  assert.deepEqual(results.map(r => r.status).sort(), [200, 400])
  assert.equal((await request('/auth/me', undefined, oldCookie)).status, 401)
  assert.equal((await request('/auth/login', { email, password }, '')).status, 401)
  assert.equal((await request('/auth/login', { email, password: next }, '')).status, 200)
  assert.equal((await (await request('/auth/me')).json()).id, user.id)
  assert.equal((await request('/projects/' + project.id)).status, 200)
}))
test('password change requires current password and invalidates sessions and pending resets', () => fixture(async ({ directory, request, email, password, next, cookie }) => {
  assert.equal((await request('/auth/change-password', { currentPassword: password, newPassword: next }, '')).status, 401)
  assert.equal((await request('/auth/change-password', { currentPassword: 'incorrect', newPassword: next })).status, 400)
  assert.equal((await request('/auth/change-password', { currentPassword: password, newPassword: password })).status, 400)
  const { challengeId } = await (await request('/auth/forgot-password', { email })).json(); const code = await mailedCode(directory, email)
  const oldCookie = cookie()
  assert.equal((await request('/auth/change-password', { currentPassword: password, newPassword: next })).status, 200)
  assert.equal((await request('/auth/me', undefined, oldCookie)).status, 401)
  assert.equal((await request('/auth/reset-password', { challengeId, code, newPassword: 'another-password-123' }, '')).status, 400)
  assert.equal((await request('/auth/login', { email, password }, '')).status, 401)
  assert.equal((await request('/auth/login', { email, password: next }, '')).status, 200)
}))
test('recovery masks unknown accounts, limits resend and locks incorrect or expired codes', () => fixture(async ({ directory, db, request, email, next }) => {
  const unknown = await request('/auth/forgot-password', { email: 'unknown@example.com' }); assert.equal(unknown.status, 200)
  const first = await request('/auth/forgot-password', { email }); const known = await first.json(); const unknownBody = await unknown.json()
  assert.deepEqual(Object.keys(known).sort(), Object.keys(unknownBody).sort()); assert.equal(known.message, unknownBody.message)
  assert.equal((await request('/auth/forgot-password', { email })).status, 200)
  assert.equal(db.prepare('SELECT COUNT(*) n FROM password_resets').get().n, 1)
  const code = await mailedCode(directory, email); const wrong = code === '000000' ? '111111' : '000000'
  for (let index = 0; index < 5; index++) assert.equal((await request('/auth/reset-password', { challengeId: known.challengeId, code: wrong, newPassword: next })).status, 400)
  assert.equal((await request('/auth/reset-password', { challengeId: known.challengeId, code, newPassword: next })).status, 400)
  db.exec('UPDATE password_resets SET attempts=0,expires_at=0;')
  assert.equal((await request('/auth/reset-password', { challengeId: known.challengeId, code, newPassword: next })).status, 400)
}))
