import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseArguments } from '../cook/worker.mjs'

// The container cook worker is driven by CookWorker as
//   <Cook:CliPath> <Cook:CliArgs with {project}/{output} substituted>
// so its argument contract and its failure reporting are what the publish chain
// depends on. The end-to-end cook needs the Emscripten player artifacts from the
// image; here we pin the pure-Node contract that must hold everywhere.

const worker = fileURLToPath(new URL('../cook/worker.mjs', import.meta.url))
const missingPlayerDirectory = join(tmpdir(), 'tomcat-player-absent')

function run(args, environment = {}) {
  return new Promise(resolve => {
    const child = spawn(process.execPath, [worker, ...args], {
      env: { ...process.env, TOMCAT_PLAYER_DIR: missingPlayerDirectory, ...environment },
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    })
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', chunk => { stdout += chunk.toString() })
    child.stderr.on('data', chunk => { stderr += chunk.toString() })
    child.once('error', error => resolve({ code: -1, stdout, stderr: String(error) }))
    child.once('exit', code => resolve({ code, stdout, stderr }))
  })
}

test('worker accepts positional and CLI-shaped arguments', () => {
  const positional = ['node', 'worker.mjs', 'Project.tcproj', 'Game.tcpak']
  assert.deepEqual(parseArguments(positional), { project: 'Project.tcproj', output: 'Game.tcpak' })
  const cliShaped = ['node', 'worker.mjs', 'cook', '--project', 'Project.tcproj', '--output', 'Game.tcpak']
  assert.deepEqual(parseArguments(cliShaped), { project: 'Project.tcproj', output: 'Game.tcpak' })
  assert.deepEqual(parseArguments(['node', 'worker.mjs']), { project: undefined, output: undefined })
})

test('worker reports missing arguments instead of cooking', { timeout: 30000 }, async () => {
  const result = await run([])
  assert.equal(result.code, 4)
  assert.match(result.stderr, /用法/)
})

test('worker reports a missing project file', { timeout: 30000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-cook-worker-'))
  try {
    const result = await run([join(directory, 'Absent.tcproj'), join(directory, '.cook', 'Game.tcpak')])
    assert.equal(result.code, 4)
    assert.match(result.stderr, /项目文件不存在/)
  } finally { await rm(directory, { recursive: true, force: true }) }
})

test('worker reports a missing Web Player module', { timeout: 30000 }, async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-cook-worker-'))
  try {
    await mkdir(join(directory, 'ProjectSettings'), { recursive: true })
    await writeFile(join(directory, 'Project.tcproj'), 'Project:\n  Name: worker-test\n')
    const result = await run([join(directory, 'Project.tcproj'), join(directory, '.cook', 'Game.tcpak')])
    assert.equal(result.code, 4)
    assert.match(result.stderr, /找不到 Web Player 模块/)
  } finally { await rm(directory, { recursive: true, force: true }) }
})
