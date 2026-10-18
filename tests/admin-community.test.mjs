import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { mailedCode } from './email-helper.mjs'

test('verified administrator, public community, moderation, session revocation and persistence', { timeout: 90000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-admin-'))
  let child, db, base, output = ''
  const cookies = new Map()
  const password = 'admin-test-password-123'
  async function start() {
    output = ''
    child = spawn('dotnet', [resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
      cwd: resolve('TomCat.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail'), Cook__CliPath: process.execPath, Cook__PollSeconds: '60' },
    })
    base = await new Promise((yes, no) => {
      const timer = setTimeout(() => { child.kill(); no(new Error(output)) }, 20000)
      const fail = cause => { clearTimeout(timer); no(cause) }
      child.once('error', fail); child.once('exit', () => fail(new Error(output)))
      const read = chunk => { output += chunk; const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); yes(match[1]) } }
      child.stdout.on('data', read); child.stderr.on('data', read)
    })
  }
  async function stop() { if (child && child.exitCode === null && child.signalCode === null) await new Promise(yes => { child.once('exit', yes); child.kill() }) }
  async function request(path, { actor = 'guest', method = 'GET', body, header = true } = {}) {
    const response = await fetch(base + '/v1' + path, { method, headers: { 'Content-Type': 'application/json', ...(header ? { 'X-TomCat-Request': '1' } : {}), Cookie: cookies.get(actor) || '' }, body: body === undefined ? undefined : JSON.stringify(body) })
    if (response.headers.has('set-cookie')) cookies.set(actor, response.headers.get('set-cookie').split(';')[0])
    assert.ok(response.status < 500, `${response.status}: ${output}`)
    return response
  }
  const post = (path, body, actor = 'guest') => request(path, { actor, method: 'POST', body })
  const moderate = (path, body, method = 'PUT', actor = 'admin') => request('/admin' + path, { actor, method, body })
  async function register(actor, email) {
    const sent = await post('/auth/register', { email, password, isAdmin: true })
    assert.equal(sent.status, 200)
    const { challengeId } = await sent.json()
    // Merely requesting an admin mailbox must not produce an account or session.
    assert.equal((await request('/admin/overview', { actor })).status, 401)
    const verified = await post('/auth/verify-email', { challengeId, code: await mailedCode(directory, email.toLowerCase()) })
    const { token } = await verified.json()
    const completed = await post('/auth/complete-registration', { token, isAdmin: true }, actor)
    assert.equal(completed.status, 200)
    return completed.json()
  }
  try {
    await start()
    db = new DatabaseSync(join(directory, 'tomcat.db'))
    const user = await register('user', 'regular@example.com')
    const admin = await register('admin', 'CHNNASN@gmail.com')
    assert.equal(user.isAdmin, false, 'body must not grant privileges')
    assert.equal(admin.isAdmin, true)
    assert.equal((await (await request('/auth/me', { actor: 'admin' })).json()).isAdmin, true)
    for (const resource of ['overview', 'users', 'topics', 'replies', 'publications', 'audit']) {
      assert.equal((await request('/admin/' + resource)).status, 401)
      assert.equal((await request('/admin/' + resource, { actor: 'user' })).status, 403)
      assert.equal((await request('/admin/' + resource, { actor: 'admin' })).status, 200)
    }
    assert.equal((await post('/community/topics', { title: 'spam', content: 'test', category: '综合讨论' })).status, 401)
    assert.equal((await post('/community/topics', { title: 'x', content: 'x', category: 'invalid' }, 'user')).status, 400)
    assert.equal((await post('/community/topics', { title: 'x'.repeat(81), content: 'x', category: '综合讨论' }, 'user')).status, 400)
    const topicResponse = await post('/community/topics', { title: '真实社区话题', content: '<script>unsafe()</script>\n正文', category: '综合讨论' }, 'user')
    assert.equal(topicResponse.status, 201)
    const topic = await topicResponse.json()
    const reply = await (await post(`/community/topics/${topic.id}/replies`, { content: '待处理回复' }, 'user')).json()
    assert.equal((await (await request('/community/topics')).json()).total, 1)
    assert.equal((await (await request(`/community/topics/${topic.id}`)).json()).replies, 1)
    assert.ok(!JSON.stringify(await (await request('/community/topics')).json()).includes('regular@example.com'), 'public content must not reveal account emails')
    assert.equal((await (await request('/community/topics?q=不存在')).json()).total, 0)
    assert.equal((await (await request('/community/topics?offset=50')).json()).items.length, 0)
    assert.equal((await moderate(`/topics/${topic.id}/pin`, { pinned: true, reason: '置顶公告' }, 'PUT', 'user')).status, 403)
    assert.equal((await moderate(`/topics/${topic.id}/pin`, { pinned: true, reason: '' })).status, 400)
    assert.equal((await request(`/admin/topics/${topic.id}/pin`, { actor: 'admin', method: 'PUT', header: false, body: { pinned: true, reason: 'CSRF' } })).status, 403)
    assert.equal((await moderate(`/topics/${topic.id}/pin`, { pinned: true, reason: '置顶公告' })).status, 204)
    assert.equal((await (await request(`/community/topics/${topic.id}`)).json()).pinned, true)
    assert.equal((await moderate(`/replies/${reply.id}`, { reason: '删除违规回复' }, 'DELETE')).status, 204)
    assert.equal((await (await request(`/community/topics/${topic.id}`)).json()).replies, 0)
    await post(`/community/topics/${topic.id}/replies`, { content: '级联删除测试' }, 'user')
    assert.equal((await moderate(`/users/${admin.id}/suspension`, { suspended: true, reason: '禁止封禁管理员' })).status, 409)
    assert.equal((await moderate(`/users/${user.id}/suspension`, { suspended: true, reason: '恶意发帖' })).status, 204)
    const staleCookie = cookies.get('user')
    assert.equal((await request('/auth/me', { actor: 'user' })).status, 401)
    cookies.set('user', staleCookie)
    assert.equal((await post(`/community/topics/${topic.id}/replies`, { content: '封禁后不能回复' }, 'user')).status, 401)
    assert.equal((await post('/auth/login', { email: user.email, password }, 'user')).status, 401)
    assert.equal((await moderate(`/users/${user.id}/suspension`, { suspended: false, reason: '解除封禁' })).status, 204)
    cookies.set('user', staleCookie)
    assert.equal((await request('/auth/me', { actor: 'user' })).status, 401, 'old cookie stays invalid after restoration')
    assert.equal((await post('/auth/login', { email: user.email, password }, 'user')).status, 200)

    const project = await (await post('/projects', { name: '待审核作品', template: '2D' }, 'user')).json()
    const now = new Date().toISOString()
    db.prepare('INSERT INTO revisions VALUES(?,?,?,?)').run('moderation-revision', project.id, JSON.stringify({ engineCommit: 'test' }), now)
    db.prepare('UPDATE projects SET current_revision_id=? WHERE id=?').run('moderation-revision', project.id)
    db.prepare("INSERT INTO publications VALUES(?,?,?,?,?,'published','',?,?,?, ?,?)").run(project.id, 'moderation-revision', 'test', '作品', '', Buffer.from('game'), 4, 'a'.repeat(64), now, now)
    assert.equal((await request(`/games/published/${project.id}/package`)).status, 200)
    assert.equal((await moderate(`/publications/${project.id}/moderation`, { blocked: true, reason: '违规作品' })).status, 204)
    assert.equal((await request(`/games/published/${project.id}`)).status, 404)
    assert.equal((await request(`/games/published/${project.id}/package`)).status, 404)
    assert.equal((await (await request('/games/published')).json()).length, 0)
    assert.equal((await moderate(`/publications/${project.id}/moderation`, { blocked: false, reason: '复核恢复' })).status, 204)
    assert.equal((await request(`/games/published/${project.id}/package`)).status, 200)
    await moderate(`/publications/${project.id}/moderation`, { blocked: true, reason: '禁止重新发布' })
    assert.equal((await request(`/projects/${project.id}/publish`, { actor: 'user', method: 'DELETE' })).status, 204)
    assert.equal((await post(`/projects/${project.id}/publish`, { title: '绕过审核' }, 'user')).status, 403)
    assert.equal((await moderate(`/topics/${topic.id}`, { reason: '移除违规话题及回复' }, 'DELETE')).status, 204)
    assert.equal((await request(`/community/topics/${topic.id}`)).status, 404)
    assert.equal((await request(`/community/topics/${topic.id}/replies`)).status, 404)
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM community_replies').get().n, 0)
    const overview = await (await request('/admin/overview', { actor: 'admin' })).json()
    assert.equal(overview.users, 2); assert.equal(overview.onlineUsers, 0)
    for (const hidden of ['projects', 'replies', 'uploadBytes']) assert.equal(Object.hasOwn(overview, hidden), false)
    const audit = await (await request('/admin/audit', { actor: 'admin' })).json()
    assert.equal(audit.total, 8)
    assert.ok(audit.items.every(item => item.actor === admin.email && item.reason && item.targetId))
    const users = await (await request('/admin/users', { actor: 'admin' })).json()
    assert.ok(!JSON.stringify(users).includes('password'))

    // Privilege changes must affect an already signed-in admin on the next request.
    db.prepare('UPDATE users SET is_admin=0 WHERE id=?').run(admin.id)
    assert.equal((await request('/admin/overview', { actor: 'admin' })).status, 403)
    await stop(); await start()
    // Startup bootstraps an existing verified account without altering its credentials.
    assert.equal((await request('/admin/overview', { actor: 'admin' })).status, 200)
    assert.equal((await (await request('/admin/audit', { actor: 'admin' })).json()).total, 8)
    assert.equal((await post('/auth/login', { email: admin.email, password }, 'admin')).status, 200)
  } finally {
    await stop(); db?.close()
    await rm(directory, { recursive: true, force: true })
  }
})
