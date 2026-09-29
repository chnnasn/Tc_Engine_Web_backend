// 本地冒烟:用 wasm-tools 的 Emscripten pack 从 TomCat_Engine 构建
// tomcat_player,供 cook/worker.mjs 在本机(无需 Docker)做真实 cook 验证。
// 环境变量设置与前端 scripts/build-engine.mjs 的 pack 分支保持一致。
// 用法:node cook/local-build-player.mjs [引擎检出目录] [输出目录]
import { spawnSync } from 'node:child_process'
import { existsSync, readdirSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

const engine = resolve(process.argv[2] || 'E:/Github/TomCat_Engine')
const out = resolve(process.argv[3] || join(engine, 'build', 'web'))
const cmake = 'D:/Microsoft Visual Studio/Versions/2026 Pro/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
const ninja = 'D:/Microsoft Visual Studio/Versions/2026 Pro/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe'

const dotnetRoot = dirname(spawnSync('where', ['dotnet'], { encoding: 'utf8' }).stdout.split(/\r?\n/)[0])
const packs = join(dotnetRoot, 'packs')
const subdirectories = parent => existsSync(parent)
  ? readdirSync(parent, { withFileTypes: true }).filter(entry => entry.isDirectory()).map(entry => join(parent, entry.name))
  : []
const newest = dirs => [...dirs].sort().pop()
const pack = name => {
  const directory = join(packs, name)
  if (!existsSync(directory)) throw new Error(`缺少 Emscripten pack:${directory}`)
  return directory
}
const tools = join(newest(subdirectories(pack('Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.win-x64'))), 'tools')
const emcmake = join(tools, 'emscripten', 'emcmake.py')
const python = join(newest(subdirectories(pack('Microsoft.NET.Runtime.Emscripten.3.1.56.Python.win-x64'))), 'tools', 'python.exe')
if (!existsSync(emcmake)) throw new Error(`找不到 emcmake.py:${emcmake}`)
const env = {
  ...process.env,
  PATH: `${dirname(cmake)};${process.env.PATH}`,
  EMSDK_PATH: tools,
  EMSDK_PYTHON: python,
  DOTNET_EMSCRIPTEN_LLVM_ROOT: join(tools, 'bin'),
  DOTNET_EMSCRIPTEN_NODE_JS: join(newest(subdirectories(pack('Microsoft.NET.Runtime.Emscripten.3.1.56.Node.win-x64'))), 'tools', 'bin', 'node.exe'),
  DOTNET_EMSCRIPTEN_BINARYEN_ROOT: tools,
  EM_CACHE: join(newest(subdirectories(pack('Microsoft.NET.Runtime.Emscripten.3.1.56.Cache.win-x64'))), 'tools', 'emscripten', 'cache'),
  FROZEN_CACHE: 'true',
}

const run = (command, args) => {
  console.log(`+ ${command} ${args.join(' ')}`)
  const result = spawnSync(command, args, { cwd: engine, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] })
  if (result.status !== 0) throw new Error(`${command} 退出码 ${result.status}`)
}

run(python, [emcmake, cmake, '-S', join(engine, 'Web'), '-B', out, '-G', 'Ninja', `-DCMAKE_MAKE_PROGRAM=${ninja}`, '-DCMAKE_BUILD_TYPE=Release'])
run(cmake, ['--build', out, '--target', 'tomcat_player', '--parallel', String(process.env.CMAKE_BUILD_PARALLEL_LEVEL || 8)])
if (!existsSync(join(out, 'tomcat_player.js'))) throw new Error('未产出 tomcat_player.js')
console.log(`PASS: tomcat_player 构建完成 → ${out}`)
