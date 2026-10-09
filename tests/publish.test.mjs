import { emailRegister, mailedCode } from './email-helper.mjs'
import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises'
import { createHash } from 'node:crypto'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const apiDirectory = fileURLToPath(new URL('../TomCat.Api/', import.meta.url))
const assembly = resolve(apiDirectory, 'bin/Release/net10.0/TomCat.Api.dll')

async function start(directory, extraEnv = {}) {
  const process = spawn('dotnet', [assembly, '--urls', 'http://127.0.0.1:0'], {
    cwd: apiDirectory,
    env: { ...globalThis.process.env, ASPNETCORE_ENVIRONMENT: 'Development', Storage__Directory: directory, Mail__PickupDirectory: join(directory, 'mail'), ...extraEnv },
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

// The cook is exercised through a deterministic fake CLI: the production worker only cares
// that the configured executable runs with cook/--project/--output and leaves a package file.
const fakeCliSource = `import { writeFileSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
const args = process.argv.slice(2)
const output = args[args.indexOf('--output') + 1]
const here = dirname(fileURLToPath(import.meta.url))
if (existsSync(join(here, 'fail-flag'))) process.exit(3)
writeFileSync(output, 'FAKE-TCPAK-V7-TEST-PACKAGE\\n')
`

async function json(response) {
  const text = await response.text()
  try { return JSON.parse(text) }
  catch { throw new Error(`HTTP ${response.status} returned a non-JSON body: ${text.slice(0, 200)}`) }
}

async function client(api, cookies) {
  return async function request(path, { user = 'alice', method = 'GET', body, bytes, headers = {} } = {}) {
    const response = await fetch(`${api.baseUrl}${path}`, {
      method,
      redirect: 'manual',
      headers: {
        'Content-Type': bytes === undefined ? 'application/json' : 'application/octet-stream',
        ...(method === 'GET' ? {} : { 'X-TomCat-Request': '1' }),
        ...(user && cookies.has(user) ? { Cookie: cookies.get(user) } : {}),
        ...headers,
      },
      body: bytes ?? (body === undefined ? undefined : JSON.stringify(body)),
    })
    const cookie = response.headers.get('set-cookie')
    if (cookie) cookies.set(user, cookie.split(';')[0])
    if (response.status >= 500) throw new Error(`API ${response.status}: ${api.diagnostics()}`)
    return response
  }
}

async function register(request, username, directory) {
  const response = await emailRegister((path, body) => request('/v1' + path, { user: username, method: 'POST', body }), directory, username, 'publish-test-1234')
  assert.equal(response.status, 200)
}

async function createSavedProject(request, name, user = 'alice') {
  const project = await json(await request('/v1/projects', { user, method: 'POST', body: { name, description: '测试项目', template: '2D' } }))
  const hash = bytes => createHash('sha256').update(bytes).digest('hex')
  const files = {
    'Project.tcproj': 'Project:\n  Name: web\n  Version: 1.0.0\n  Description: ""\n  EditorVersion: 0.3.0\n  Template: 2D\n  AssetDirectory: Assets\nSchemaVersion: 4\n',
    'ProjectSettings/BuildSettings.json': '{}',
    'ProjectSettings/ProjectSettings.json': '{}',
    'ProjectSettings/PlayerSettings.json': '{}',
  }
  const manifest = []
  for (const [path, text] of Object.entries(files)) {
    const bytes = Buffer.from(text, 'utf8')
    const upload = await json(await request(`/v1/projects/${project.id}/uploads/${hash(bytes)}`, { user, method: 'PUT', bytes }))
    manifest.push({ path, uploadId: upload.uploadId, contentHash: hash(bytes), size: bytes.length })
  }
  const payload = {
    schemaVersion: 2,
    engineCommit: '0b8829a864ff53ad0ad2c7ded56ef433325a836a',
    sceneHandle: '12007672766582721512',
    archive: 'SchemaVersion: 11\nSceneName: web\nEntities: []\n',
    files: manifest,
  }
  const saved = await request(`/v1/projects/${project.id}/revisions`, { user, method: 'POST', headers: { 'If-None-Match': '*' }, body: payload })
  assert.equal(saved.status, 201)
  return { projectId: project.id, revisionId: (await json(saved)).revisionId }
}

test('publish chain: save, publish, cook, play publicly', { timeout: 120000 }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-publish-test-'))
  const tools = join(directory, 'tools')
  await mkdir(tools)
  const fakeCli = join(tools, 'fake-cook.mjs')
  await writeFile(fakeCli, fakeCliSource)
  const api = await start(directory, {
    Cook__CliPath: process.execPath,
    Cook__CliArgs: `"${fakeCli}" cook --project "{project}" --output "{output}"`,
    Cook__PollSeconds: '1',
  })
  const cookies = new Map()
  const request = await client(api, cookies)
  t.after(() => api.stop())
  try {
    await register(request, 'alice', directory)
    const { projectId } = await createSavedProject(request, '夜航')

    // Guests see an empty arcade before the first publish.
    const empty = await fetch(`${api.baseUrl}/v1/games/published`)
    assert.equal(empty.status, 200)
    assert.deepEqual(await empty.json(), [])

    // Publishing requires a session even though playing does not.
    const anonymous = await fetch(`${api.baseUrl}/v1/projects/${projectId}/publish`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1' }, body: JSON.stringify({ title: '夜航' }),
    })
    assert.equal(anonymous.status, 401)

    // A project without a saved revision cannot be published.
    await register(request, 'bob', directory)
    const draft = await json(await request('/v1/projects', { user: 'bob', method: 'POST', body: { name: '空项目', description: '', template: '2D' } }))
    const noRevision = await request(`/v1/projects/${draft.id}/publish`, { user: 'bob', method: 'POST', body: { title: '空' } })
    assert.equal(noRevision.status, 400)
    // Another account cannot see or publish someone else's project.
    assert.equal((await request(`/v1/projects/${projectId}/publish`, { user: 'bob', method: 'GET' })).status, 404)

    const publish = await request(`/v1/projects/${projectId}/publish`, { method: 'POST', body: { title: '夜航', description: '一次测试发布' } })
    assert.equal(publish.status, 202)
    assert.equal((await publish.json()).status, 'pending')
    assert.equal((await request(`/v1/projects/${projectId}/publish`, { method: 'POST', body: { title: '夜航' } })).status, 409)

    let state
    for (let waited = 0; waited < 30000; waited += 500) {
      state = await json(await request(`/v1/projects/${projectId}/publish`))
      if (state.status !== 'pending') break
      await new Promise(resolve => setTimeout(resolve, 500))
    }
    assert.equal(state.status, 'published')
    assert.equal(state.error, '')
    assert.equal(state.revisionId, state.revisionId)
    assert.match(state.etag, /^"[a-f0-9]{64}"$/)

    const list = await json(await fetch(`${api.baseUrl}/v1/games/published`))
    assert.equal(list.length, 1)
    assert.equal(list[0].id, projectId)
    assert.equal(list[0].title, '夜航')
    assert.equal(list[0].description, '一次测试发布')

    const detail = await fetch(`${api.baseUrl}/v1/games/published/${projectId}`)
    assert.equal(detail.status, 200)
    assert.equal((await json(detail)).engineCommit, '0b8829a864ff53ad0ad2c7ded56ef433325a836a')
    assert.equal((await fetch(`${api.baseUrl}/v1/games/published/missing-project`)).status, 404)

    const pack = await fetch(`${api.baseUrl}/v1/games/published/${projectId}/package`)
    assert.equal(pack.status, 200)
    assert.equal(await pack.text(), 'FAKE-TCPAK-V7-TEST-PACKAGE\n')
    assert.equal(pack.headers.get('etag'), state.etag)
    assert.match(pack.headers.get('cache-control'), /immutable/)
    const cached = await fetch(`${api.baseUrl}/v1/games/published/${projectId}/package`, { headers: { 'If-None-Match': state.etag } })
    assert.equal(cached.status, 304)

    // A failed cook is reported honestly and can be retried.
    await writeFile(join(tools, 'fail-flag'), 'x')
    const second = await createSavedProject(request, '迷航')
    await request(`/v1/projects/${second.projectId}/publish`, { method: 'POST', body: { title: '迷航' } })
    let failed
    for (let waited = 0; waited < 30000; waited += 500) {
      failed = await json(await request(`/v1/projects/${second.projectId}/publish`))
      if (failed.status !== 'pending') break
      await new Promise(resolve => setTimeout(resolve, 500))
    }
    assert.equal(failed.status, 'failed')
    assert.match(failed.error, /退出码 3/)
    assert.equal((await fetch(`${api.baseUrl}/v1/games/published/${second.projectId}/package`)).status, 404)

    await rm(join(tools, 'fail-flag'))
    const retry = await request(`/v1/projects/${second.projectId}/publish`, { method: 'POST', body: { title: '迷航', description: '重试' } })
    assert.equal(retry.status, 202)
    let retried
    for (let waited = 0; waited < 30000; waited += 500) {
      retried = await json(await request(`/v1/projects/${second.projectId}/publish`))
      if (retried.status !== 'pending') break
      await new Promise(resolve => setTimeout(resolve, 500))
    }
    assert.equal(retried.status, 'published')

    // Unpublish removes the public game; project deletion cascades too.
    assert.equal((await request(`/v1/projects/${projectId}/publish`, { method: 'DELETE' })).status, 204)
    assert.equal((await fetch(`${api.baseUrl}/v1/games/published/${projectId}/package`)).status, 404)
    const remaining = await json(await fetch(`${api.baseUrl}/v1/games/published`))
    assert.deepEqual(remaining.map(game => game.id), [second.projectId])
    assert.equal((await request(`/v1/projects/${second.projectId}`, { method: 'DELETE' })).status, 204)
    assert.deepEqual(await json(await fetch(`${api.baseUrl}/v1/games/published`)), [])
  } finally { await api.stop() }
})

test('publish is rejected while the cook tool is not configured', { timeout: 60000 }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-publish-uncofig-'))
  const api = await start(directory)
  const cookies = new Map()
  const request = await client(api, cookies)
  t.after(() => api.stop())
  try {
    await register(request, 'carol', directory)
    const { projectId } = await createSavedProject(request, '无工具', 'carol')
    // The helper turns unexpected 5xx responses into errors; this 503 is the expected outcome.
    const response = await fetch(`${api.baseUrl}/v1/projects/${projectId}/publish`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-TomCat-Request': '1', Cookie: cookies.get('carol') },
      body: JSON.stringify({ title: '无工具' }),
    })
    assert.equal(response.status, 503)
    assert.equal((await response.json()).error.includes('Cook:CliPath'), true)
  } finally { await api.stop() }
})
