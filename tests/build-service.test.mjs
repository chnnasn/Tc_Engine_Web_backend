import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { mkdtemp, writeFile, readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { createHash, randomUUID } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import { buildSecret, freePort, startService, startBuildWorker } from './build-helper.mjs'
import { currentEngineCommit } from './engine-version.mjs'

const id = () => randomUUID().replaceAll('-', '')
const sha = bytes => createHash('sha256').update(bytes).digest('hex')
async function until(check, timeout = 15000) {
  const end = Date.now() + timeout
  while (Date.now() < end) { const value = await check(); if (value) return value; await new Promise(r => setTimeout(r, 100)) }
  throw new Error('Expected build state did not arrive')
}
function savedProject(db) {
  const project = id(), revision = id(), now = new Date().toISOString(), files = []
  db.prepare('INSERT OR IGNORE INTO users(id,username,password_hash,created_at) VALUES(?,?,?,?)').run('owner', 'owner', 'test', now)
  db.prepare('INSERT INTO projects VALUES(?,?,?,?,?,?,?,?)').run(project, 'owner', 'Build test', '', '2D', revision, now, now)
  for (const path of ['Project.tcproj', 'ProjectSettings/BuildSettings.json', 'ProjectSettings/ProjectSettings.json', 'ProjectSettings/PlayerSettings.json']) {
    const bytes = Buffer.from(path + project), uploadId = id()
    db.prepare('INSERT INTO uploads VALUES(?,?,?,?,?,?)').run(uploadId, project, sha(bytes), bytes.length, bytes, now)
    files.push({ path, uploadId, size: bytes.length, contentHash: sha(bytes) })
  }
  const payload = { schemaVersion: 2, engineCommit: currentEngineCommit, sceneHandle: '12007672766582721512', archive: 'SchemaVersion: 11\nSceneName: test\nEntities: []\n', files }
  db.prepare('INSERT INTO revisions VALUES(?,?,?,?)').run(revision, project, JSON.stringify(payload), now)
  for (const file of files) db.prepare('INSERT INTO revision_files VALUES(?,?,?)').run(revision, file.path, file.uploadId)
  return { project, revision, files }
}
function enqueue(db, project) {
  const jobId = id(), now = new Date().toISOString()
  db.prepare('DELETE FROM publications WHERE project_id=?').run(project.project)
  db.prepare(`INSERT INTO publications(project_id,revision_id,engine_commit,title,status,requested_at,build_job_id) VALUES(?,?,?,'Build test','pending',?,?)`).run(project.project, project.revision, currentEngineCommit, now, jobId)
  db.prepare("INSERT INTO build_jobs(id,project_id,revision_id,status,created_at) VALUES(?,?,?,'queued',?)").run(jobId, project.project, project.revision, now)
  return jobId
}
const internal = (base, event, path, body, headers = {}, method = 'POST') => fetch(`${base}/internal/builds/${event.jobId}/${path}`, {
  method, headers: { Authorization: `Bearer ${buildSecret}`, 'X-TomCat-Build-Lease': event.leaseToken, ...headers }, body,
})
const complete = (base, event, bytes, hash = sha(bytes)) => internal(base, event, 'complete', bytes, { 'Content-Type': 'application/octet-stream', 'X-TomCat-Package-Sha256': hash })

test('durable events: backpressure, restart, lease recovery, scoped inputs and idempotent callbacks', { timeout: 60000 }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-build-events-'))
  const events = []; let busy = false
  const receiver = createServer(async (request, response) => {
    assert.equal(request.headers.authorization, `Bearer ${buildSecret}`)
    let body = ''; for await (const bytes of request) body += bytes
    if (!busy) events.push(JSON.parse(body))
    response.writeHead(busy ? 503 : 202).end()
  })
  await new Promise(r => receiver.listen(0, '127.0.0.1', r))
  const apiPort = await freePort()
  const env = { Storage__Directory: directory, Build__Secret: buildSecret, Build__WorkerUrl: `http://127.0.0.1:${receiver.address().port}`, Build__RetrySeconds: '1', Build__LeaseSeconds: '5' }
  let api = await startService('TomCat.Api', env, apiPort)
  const db = new DatabaseSync(join(directory, 'tomcat.db')); db.exec('PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;')
  t.after(async () => { await api.stop(); db.close(); receiver.closeAllConnections(); await new Promise(r => receiver.close(r)) })
  const first = savedProject(db), other = savedProject(db)
  const jobId = enqueue(db, first)
  const event = await until(() => events.find(e => e.jobId === jobId))
  assert.equal((await fetch(`${api.baseUrl}/internal/builds/${jobId}/input`)).status, 401)
  assert.equal((await internal(api.baseUrl, event, 'input', undefined, { Origin: 'http://example.test' }, 'GET')).status, 401)
  assert.equal((await internal(api.baseUrl, { ...event, leaseToken: '0'.repeat(64) }, 'input', undefined, {}, 'GET')).status, 409)
  assert.equal((await internal(api.baseUrl, event, 'input', undefined, {}, 'GET')).status, 200)
  assert.equal((await internal(api.baseUrl, event, `files/${other.files[0].uploadId}`, undefined, {}, 'GET')).status, 404)
  assert.equal((await internal(api.baseUrl, event, `files/${first.files[0].uploadId}`, undefined, {}, 'GET')).status, 200)
  // An acknowledged event whose worker vanished is delivered again with a new lease.
  const recovered = await until(() => events.find(e => e.jobId === jobId && e.leaseToken !== event.leaseToken))
  assert.equal((await complete(api.baseUrl, event, Buffer.from('stale'))).status, 409)
  const bytes = Buffer.alloc(17 * 1024 * 1024, 7)
  assert.equal((await complete(api.baseUrl, recovered, bytes, '0'.repeat(64))).status, 400)
  assert.equal((await complete(api.baseUrl, recovered, bytes)).status, 204, api.diagnostics())
  assert.equal((await complete(api.baseUrl, recovered, bytes)).status, 204)
  const publication = db.prepare('SELECT status,byte_length,sha256 FROM publications WHERE project_id=?').get(first.project)
  assert.deepEqual({ ...publication }, { status: 'published', byte_length: bytes.length, sha256: sha(bytes) })
  // Re-publishing the exact same revision must still reject the previous job's result.
  const replacementId = enqueue(db, first)
  const replacement = await until(() => events.find(e => e.jobId === replacementId))
  assert.equal((await complete(api.baseUrl, recovered, Buffer.from('obsolete result'))).status, 409)
  const failure = JSON.stringify({ error: 'compiler error' })
  assert.equal((await internal(api.baseUrl, replacement, 'failed', failure, { 'Content-Type': 'application/json' })).status, 204)
  assert.equal((await internal(api.baseUrl, replacement, 'failed', failure, { 'Content-Type': 'application/json' })).status, 204)
  assert.equal((await complete(api.baseUrl, replacement, Buffer.from('late'))).status, 409)
  // A queued event survives API restart, and explicit worker backpressure consumes no attempts.
  busy = true
  const waitingId = enqueue(db, other)
  await until(() => db.prepare('SELECT next_attempt FROM build_jobs WHERE id=?').get(waitingId).next_attempt > 0)
  assert.equal(db.prepare('SELECT attempts FROM build_jobs WHERE id=?').get(waitingId).attempts, 0)
  await api.stop(); api = await startService('TomCat.Api', env, apiPort)
  busy = false
  const resumed = await until(() => events.find(e => e.jobId === waitingId))
  for (let index = 0; index < 3; index++) {
    assert.equal((await internal(api.baseUrl, resumed, 'heartbeat')).status, 204)
    await new Promise(r => setTimeout(r, 2000))
  }
  assert.equal(events.filter(e => e.jobId === waitingId).length, 1, 'heartbeats prevent redelivery')
  db.prepare('DELETE FROM publications WHERE project_id=?').run(other.project)
  assert.equal((await internal(api.baseUrl, resumed, 'heartbeat')).status, 409)
  assert.equal((await complete(api.baseUrl, resumed, Buffer.from('cancelled'))).status, 409)
})

test('worker deduplicates events, bounds concurrency and kills a cancelled build', { timeout: 30000 }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-build-worker-'))
  const counter = join(directory, 'started.txt'), finished = join(directory, 'finished.txt'), cli = join(directory, 'cli.mjs')
  await writeFile(cli, `import {appendFileSync,writeFileSync} from 'node:fs'; appendFileSync(${JSON.stringify(counter)},'run\\n'); await new Promise(r=>setTimeout(r,8000)); writeFileSync(${JSON.stringify(finished)},'done'); writeFileSync(process.argv.at(-1),'package');`)
  const api = await startService('TomCat.Api', { Storage__Directory: directory, Build__Secret: buildSecret })
  const worker = await startBuildWorker(api.baseUrl, await freePort(), cli)
  const db = new DatabaseSync(join(directory, 'tomcat.db'))
  t.after(async () => { await worker.stop(); await api.stop(); db.close() })
  const project = savedProject(db), jobId = enqueue(db, project), leaseToken = 'a'.repeat(64), now = Math.floor(Date.now() / 1000)
  db.prepare("UPDATE build_jobs SET status='running',lease_token=?,lease_until=?,deadline=? WHERE id=?").run(leaseToken, now + 5, now + 90, jobId)
  const event = { protocol: 'tomcat.build.v1', jobId, leaseToken, leaseSeconds: 5, timeoutSeconds: 30 }
  const send = (value = event, auth = buildSecret) => fetch(`${worker.baseUrl}/internal/builds/events`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${auth}` }, body: JSON.stringify(value) })
  assert.equal((await send(event, 'wrong')).status, 401)
  assert.equal((await send()).status, 202)
  assert.equal((await send()).status, 202)
  assert.equal((await send({ ...event, jobId: id() })).status, 503)
  await until(() => readFile(counter, 'utf8').catch(() => undefined))
  assert.equal(await readFile(counter, 'utf8'), 'run\n')
  assert.equal((await fetch(api.baseUrl + '/health')).status, 200)
  db.prepare('DELETE FROM publications WHERE project_id=?').run(project.project)
  await new Promise(r => setTimeout(r, 9000))
  assert.equal(await readFile(finished, 'utf8').catch(() => null), null, 'revoked lease must kill the compiler process')
})

test('schema 9 pending publications migrate to durable build jobs', { timeout: 30000 }, async t => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-build-migration-'))
  let api = await startService('TomCat.Api', { Storage__Directory: directory })
  await api.stop()
  const db = new DatabaseSync(join(directory, 'tomcat.db'))
  const project = savedProject(db); enqueue(db, project)
  db.exec('DROP TABLE build_jobs; ALTER TABLE publications DROP COLUMN build_job_id; PRAGMA user_version=9;')
  db.close()
  api = await startService('TomCat.Api', { Storage__Directory: directory })
  t.after(() => api.stop())
  const upgraded = new DatabaseSync(join(directory, 'tomcat.db'))
  try {
    assert.equal(upgraded.prepare('PRAGMA user_version').get().user_version, 10)
    const pending = upgraded.prepare('SELECT p.status,j.status AS job_status,j.revision_id FROM publications p JOIN build_jobs j ON p.build_job_id=j.id').get()
    assert.deepEqual({ ...pending }, { status: 'pending', job_status: 'queued', revision_id: project.revision })
  } finally { upgraded.close() }
})
