import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { mailedCode } from './email-helper.mjs'

async function fixture(run) {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-profile-'))
  const child = spawn('dotnet', [resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], { cwd: resolve('TomCat.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail') } })
  let db, output = ''
  try {
    const base = await new Promise((yes, no) => {
      const timer = setTimeout(() => { child.kill(); no(new Error(output)) }, 20000)
      const fail = error => { clearTimeout(timer); no(error) }
      child.once('error', fail); child.once('exit', () => fail(new Error(output)))
      const read = chunk => { output += chunk; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); yes(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read)
    })
    const jars = new Map()
    async function request(path, { actor = 'guest', method = 'GET', body, bytes, headers = {} } = {}) {
      const jar = jars.get(actor) || new Map(); jars.set(actor, jar)
      const response = await fetch(base + '/v1' + path, { method, headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1', Cookie: [...jar].map(([key, value]) => `${key}=${value}`).join('; '), ...headers }, body: bytes ?? (body === undefined ? undefined : JSON.stringify(body)) })
      for (const cookie of response.headers.getSetCookie()) { const part = cookie.split(';')[0]; const index = part.indexOf('='); const key = part.slice(0, index), value = part.slice(index + 1); if (!value) jar.delete(key); else jar.set(key, value) }
      assert.ok(response.status < 500, `${response.status}: ${output}`)
      return response
    }
    const password = 'profile-test-password-123'
    const post = (path, body, actor = 'guest') => request(path, { actor, method: 'POST', body })
    async function register(actor, email) {
      const sent = await post('/auth/register', { email, password }); assert.equal(sent.status, 200)
      const { challengeId } = await sent.json()
      const verified = await post('/auth/verify-email', { challengeId, code: await mailedCode(directory, email) })
      const { token } = await verified.json()
      const response = await post('/auth/complete-registration', { token }, actor); assert.equal(response.status, 200)
      return response.json()
    }
    const user = await register('user', 'user@example.com'), admin = await register('admin', 'chnnasn@gmail.com')
    db = new DatabaseSync(join(directory, 'tomcat.db'))
    await run({ db, request, post, password, user, admin, jars })
  } finally {
    db?.close()
    if (child.exitCode === null && child.signalCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() })
    await rm(directory, { recursive: true, force: true })
  }
}

test('profile ownership, safe avatar delivery and public author identity', () => fixture(async ({ request, post, user, admin }) => {
  assert.equal((await request('/users/me/profile', { method: 'PUT', body: { displayName: 'forbidden' } })).status, 401)
  assert.equal((await request('/users/me/profile', { actor: 'user', method: 'PUT', body: { displayName: '\n', isAdmin: true } })).status, 400)
  assert.equal((await request('/users/me/profile', { actor: 'user', method: 'PUT', body: { displayName: '改名后的创作者', id: admin.id, isAdmin: true } })).status, 200)
  const profile = await (await request('/users/' + user.id)).json()
  assert.equal(profile.displayName, '改名后的创作者'); assert.equal(profile.isAdmin, false)
  assert.ok(!JSON.stringify(profile).includes(user.email))
  assert.equal((await (await request('/users/' + admin.id)).json()).isAdmin, true)
  assert.equal((await (await request('/auth/me', { actor: 'user' })).json()).displayName, profile.displayName)
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aKS8AAAAASUVORK5CYII=', 'base64')
  const upload = (bytes, type = 'image/png') => request('/users/me/avatar', { actor: 'user', method: 'PUT', bytes, headers: { 'Content-Type': type } })
  assert.equal((await upload(Buffer.from('<svg onload="alert(1)"/>'), 'image/svg+xml')).status, 400)
  assert.equal((await upload(Buffer.from('not png'))).status, 400)
  assert.equal((await upload(Buffer.alloc(512 * 1024 + 1))).status, 413)
  const response = await upload(png); assert.equal(response.status, 200)
  const { avatarUrl } = await response.json()
  const avatar = await request(avatarUrl.slice('/v1'.length)); assert.equal(avatar.status, 200)
  assert.equal(avatar.headers.get('content-type'), 'image/png'); assert.equal(avatar.headers.get('x-content-type-options'), 'nosniff')
  assert.deepEqual(Buffer.from(await avatar.arrayBuffer()), png)
  const topic = await (await post('/community/topics', { title: '身份展示', content: '正文', category: '综合讨论' }, 'user')).json()
  assert.equal(topic.ownerId, user.id); assert.equal(topic.author, profile.displayName); assert.equal(topic.avatarUrl, avatarUrl); assert.equal(topic.isAdmin, false)
  await post(`/community/topics/${topic.id}/replies`, { content: '管理员回复' }, 'admin')
  const replies = await (await request(`/community/topics/${topic.id}/replies`)).json()
  assert.equal(replies.items[0].ownerId, admin.id); assert.equal(replies.items[0].isAdmin, true)
  const myTopics = await (await request(`/users/${user.id}/topics`)).json(); assert.equal(myTopics.total, 1)
  assert.equal((await request('/users/me/avatar', { actor: 'user', method: 'DELETE' })).status, 204)
  assert.equal((await request(avatarUrl.slice(3))).status, 404)
}))

test('opt-in saved accounts support explicit switching and revoke on version changes or removal', () => fixture(async ({ db, request, post, password, user, admin, jars }) => {
  const login = (email, rememberMe) => post('/auth/login', { email, password, rememberMe }, 'browser')
  const sessionOnly = await login(user.email, false)
  const sessionCookie = sessionOnly.headers.getSetCookie().find(cookie => cookie.startsWith('tomcat.session='))
  assert.doesNotMatch(sessionCookie, /expires=/i)
  assert.deepEqual(await (await request('/auth/remembered', { actor: 'browser' })).json(), [])
  const persistent = await login(user.email, true)
  assert.match(persistent.headers.getSetCookie().find(cookie => cookie.startsWith('tomcat.session=')), /expires=/i)
  assert.match(persistent.headers.getSetCookie().find(cookie => cookie.startsWith('tomcat.accounts=')), /httponly/i)
  await login(admin.email, true)
  const saved = await (await request('/auth/remembered', { actor: 'browser' })).json()
  assert.deepEqual(saved.map(item => item.id), [user.id, admin.id])
  assert.ok(saved.every(item => !Object.hasOwn(item, 'password')))
  assert.equal((await post('/auth/switch', { id: user.id }, 'browser')).status, 200)
  assert.equal((await (await request('/auth/me', { actor: 'browser' })).json()).id, user.id)
  assert.equal((await request('/admin/overview', { actor: 'browser' })).status, 403)
  assert.equal((await post('/auth/switch', { id: 'unremembered' }, 'browser')).status, 401)
  jars.set('tampered', new Map([['tomcat.accounts', 'invalid-protected-cookie']]))
  assert.deepEqual(await (await request('/auth/remembered', { actor: 'tampered' })).json(), [])
  // This is the same session-version rotation used by password changes and bans.
  db.prepare('UPDATE users SET session_version=session_version+1 WHERE id=?').run(user.id)
  assert.equal((await post('/auth/switch', { id: user.id }, 'browser')).status, 401)
  assert.deepEqual((await (await request('/auth/remembered', { actor: 'browser' })).json()).map(item => item.id), [admin.id])
  assert.equal((await post('/auth/switch', { id: admin.id }, 'browser')).status, 200)
  jars.set('replay', new Map(jars.get('browser')))
  assert.equal((await request('/auth/remembered/' + admin.id, { actor: 'browser', method: 'DELETE' })).status, 204)
  assert.equal((await request('/auth/me', { actor: 'browser' })).status, 401)
  assert.equal((await post('/auth/switch', { id: admin.id }, 'browser')).status, 401)
  assert.equal((await post('/auth/switch', { id: admin.id }, 'replay')).status, 401, 'removal must revoke a copied saved-account credential on the server')
}))

test('presence counts wall time once across tabs, expires stale leases, and supports unbanning', () => fixture(async ({ db, request, post, user }) => {
  const a = 'a'.repeat(32), b = 'b'.repeat(32)
  assert.equal((await post('/activity/heartbeat', { tabId: a })).status, 401)
  assert.equal((await post('/activity/heartbeat', { tabId: 'invalid' }, 'user')).status, 400)
  assert.equal((await post('/activity/heartbeat', { tabId: a }, 'user')).status, 204)
  let now = Math.floor(Date.now() / 1000)
  db.prepare('UPDATE users SET activity_cursor=? WHERE id=?').run(now - 30, user.id)
  db.prepare('UPDATE activity_presence SET last_heartbeat=? WHERE owner_id=?').run(now - 30, user.id)
  await post('/activity/heartbeat', { tabId: b, seconds: 9999999 }, 'user')
  const first = db.prepare('SELECT online_seconds n FROM users WHERE id=?').get(user.id).n
  assert.ok(first >= 30 && first <= 32)
  await Promise.all([a, b].map(tabId => post('/activity/heartbeat', { tabId }, 'user')))
  assert.ok(db.prepare('SELECT online_seconds n FROM users WHERE id=?').get(user.id).n <= first + 2, 'overlapping tabs must not multiply time')
  await post('/activity/stop', { tabId: a }, 'user')
  let stats = await (await request('/admin/overview', { actor: 'admin' })).json(); assert.equal(stats.onlineUsers, 1)
  await post('/activity/stop', { tabId: b }, 'user')
  stats = await (await request('/admin/overview', { actor: 'admin' })).json(); assert.equal(stats.onlineUsers, 0)
  assert.equal(stats.dailyActiveUsers, 1); assert.equal(stats.users, 2); assert.equal(stats.newUsersToday, 2)
  for (const key of ['projects', 'replies', 'uploadBytes']) assert.equal(Object.hasOwn(stats, key), false)
  const trend = await (await request('/admin/activity', { actor: 'admin' })).json(); assert.equal(trend.length, 14); assert.equal(trend.at(-1).activeUsers, 1)
  now = Math.floor(Date.now() / 1000)
  db.prepare('INSERT INTO activity_presence VALUES(?,?,?)').run(user.id, a, now - 3600)
  db.prepare('UPDATE users SET activity_cursor=? WHERE id=?').run(now - 3600, user.id)
  const before = db.prepare('SELECT online_seconds n FROM users WHERE id=?').get(user.id).n
  await post('/activity/heartbeat', { tabId: b }, 'user')
  const after = db.prepare('SELECT online_seconds n FROM users WHERE id=?').get(user.id).n
  assert.equal(after - before, 60, 'a crashed browser lease counts at most its 60-second expiry')
  for (const suspended of [true, false]) {
    const response = await request(`/admin/users/${user.id}/suspension`, { actor: 'admin', method: 'PUT', body: { suspended, reason: suspended ? '封禁验证' : '解除封禁验证' } }); assert.equal(response.status, 204)
    const filtered = await (await request(`/admin/users?state=${suspended ? 'suspended' : 'active'}`, { actor: 'admin' })).json()
    assert.ok(filtered.items.some(item => item.id === user.id && Boolean(item.suspended) === suspended))
  }
}))
