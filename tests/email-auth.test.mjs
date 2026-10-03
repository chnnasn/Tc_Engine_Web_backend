import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { mailedCode } from './email-helper.mjs'

test('email verification, token replay, expiry, legacy binding and ownership preservation', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-email-'))
  const child = spawn('dotnet', [resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
    cwd: resolve('TomCat.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail') },
  })
  let db
  try {
    const base = await new Promise((yes, no) => {
      let output = ''; const timer = setTimeout(() => no(new Error(output)), 20000)
      const read = data => { output += data; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); yes(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.once('error', no)
    })
    let cookie = ''
    const post = async (path, body, session = cookie) => {
      const response = await fetch(base + '/v1/auth/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1', Cookie: session }, body: JSON.stringify(body) })
      if (response.headers.has('set-cookie')) cookie = response.headers.get('set-cookie').split(';')[0]
      return response
    }
    const email = 'new@example.com', password = 'email-test-password-123'
    const sent = await post('register', { email: ' NEW@example.com ', password }); assert.equal(sent.status, 200)
    assert.equal(sent.headers.get('set-cookie'), null)
    const { challengeId } = await sent.json()
    db = new DatabaseSync(join(directory, 'tomcat.db'))
    assert.equal(db.prepare('SELECT COUNT(*) n FROM users').get().n, 0)
    assert.equal((await post('register', { email, password })).status, 429)
    assert.equal((await post('verify-email', { challengeId, code: 'abcdef' })).status, 400)
    const code = await mailedCode(directory, email)
    assert.notEqual(db.prepare('SELECT code_hash FROM email_challenges').get().code_hash, code)
    const verified = await post('verify-email', { challengeId, code }); assert.equal(verified.status, 200)
    assert.equal(verified.headers.get('set-cookie'), null)
    const { token } = await verified.json()
    assert.equal((await post('verify-email', { challengeId, code })).status, 400)
    assert.equal((await post('complete-registration', { token, username: 'bad name' })).status, 400)
    const completed = await post('complete-registration', { token, username: 'creator' }); assert.equal(completed.status, 200)
    assert.equal((await completed.json()).emailVerified, true)
    assert.equal((await post('complete-registration', { token, username: 'replay' })).status, 400)
    assert.equal((await post('login', { email: 'creator', password }, '')).status, 401)
    assert.equal((await post('login', { email: 'NEW@example.com', password }, '')).status, 200)
    // An existing account retains its identifier and projects when binding email.
    db.exec("INSERT INTO users(id,username,password_hash,created_at) SELECT 'legacy-id','legacy',password_hash,created_at FROM users LIMIT 1; INSERT INTO projects(id,owner_id,name,description,template,created_at,updated_at) VALUES('legacy-project','legacy-id','old','','2D','before','before');")
    assert.equal((await post('login', { username: 'legacy', password }, '')).status, 200)
    const binding = await post('bind-email', { email: 'legacy@example.com' }); assert.equal(binding.status, 200)
    const challenge = (await binding.json()).challengeId
    const boundVerify = await post('verify-email', { challengeId: challenge, code: await mailedCode(directory, 'legacy@example.com') }); assert.equal(boundVerify.status, 200)
    const bindToken = (await boundVerify.json()).token
    assert.equal((await post('complete-registration', { token: bindToken, username: 'legacy' }, '')).status, 401)
    const bound = await post('complete-registration', { token: bindToken, username: 'legacy' }); assert.equal(bound.status, 200)
    assert.equal((await bound.json()).id, 'legacy-id')
    assert.equal(db.prepare("SELECT owner_id FROM projects WHERE id='legacy-project'").get().owner_id, 'legacy-id')
    assert.equal((await post('login', { email: 'legacy@example.com', password }, '')).status, 200)
    const expiring = await post('register', { email: 'expired@example.com', password }); const expired = (await expiring.json()).challengeId
    db.prepare('UPDATE email_challenges SET expires_at=0 WHERE id=?').run(expired)
    assert.equal((await post('verify-email', { challengeId: expired, code: await mailedCode(directory, 'expired@example.com') })).status, 400)
  } finally {
    db?.close()
    if (child.exitCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() })
    await rm(directory, { recursive: true, force: true })
  }
})

test('five incorrect codes lock challenge without creating an account', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-email-limits-'))
  const child = spawn('dotnet', [resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
    cwd: resolve('TomCat.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail') },
  })
  try {
    const base = await new Promise((yes, no) => {
      let output = ''; const timer = setTimeout(() => no(new Error(output)), 20000)
      const read = data => { output += data; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); yes(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.once('error', no)
    })
    const post = (path, body) => fetch(base + '/v1/auth/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1' }, body: JSON.stringify(body) })
    const sent = await post('register', { email: 'locked@example.com', password: 'test-password-123' })
    const { challengeId } = await sent.json()
    const code = await mailedCode(directory, 'locked@example.com')
    const wrong = code === '000000' ? '111111' : '000000'
    for (let index = 0; index < 5; index++) assert.equal((await post('verify-email', { challengeId, code: wrong })).status, 400)
    assert.equal((await post('verify-email', { challengeId, code })).status, 400)
    const db = new DatabaseSync(join(directory, 'tomcat.db'))
    assert.equal(db.prepare('SELECT attempts FROM email_challenges').get().attempts, 5)
    assert.equal(db.prepare('SELECT COUNT(*) n FROM users').get().n, 0)
    db.close()
  } finally {
    if (child.exitCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() })
    await rm(directory, { recursive: true, force: true })
  }
})

test('production ignores pickup directory and rejects registration when SMTP is missing', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-email-production-'))
  const child = spawn('dotnet', [resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
    cwd: resolve('TomCat.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', Storage__Directory: directory, Mail__Host: '', Mail__From: '', Mail__PickupDirectory: join(directory, 'mail') },
  })
  try {
    const base = await new Promise((yes, no) => {
      let output = ''; const timer = setTimeout(() => no(new Error(output)), 20000)
      const read = data => { output += data; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); yes(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read); child.once('error', no)
    })
    const response = await fetch(base + '/v1/auth/register', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1' }, body: JSON.stringify({ email: 'unconfigured@example.com', password: 'test-password-123' }) })
    assert.equal(response.status, 503)
    assert.equal(response.headers.get('set-cookie'), null)
    const db = new DatabaseSync(join(directory, 'tomcat.db'))
    assert.equal(db.prepare('SELECT COUNT(*) n FROM users').get().n, 0)
    assert.equal(db.prepare('SELECT COUNT(*) n FROM email_challenges').get().n, 0)
    db.close()
  } finally {
    if (child.exitCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() })
    await rm(directory, { recursive: true, force: true })
  }
})
