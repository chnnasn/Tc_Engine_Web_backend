#!/usr/bin/env node
// TomCat 服务端打包 worker（容器内 Cook:CliPath 的落点）。
//
// CookWorker 把一次已保存修订物化到一个一次性临时目录，然后运行这里配置的打包工具。
// 本部署把「打包工具」定为浏览器播放器使用的同一个 Emscripten Web Player，由 Node 驱动：
//
//   node /app/cook/worker.mjs <project.tcproj> <output.tcpak>
//
// 上游 54697ebf 泛化了 cook 入口（tc_web_player_cook），所以服务端不再需要 Windows 桌面 CLI。
// 我们把物化目录镜像进模块的 MEMFS，调用引擎自带的 TCPAK writer，再把产物拷回宿主路径。
// 诊断来自 tc_web_player_error()；进程退出码即打包结果，让发布记录如实反映失败。
//
// 项目带 C# 脚本时，先在本容器里用 dotnet build + 源生成器编译出 Assembly-CSharp.dll，
// 然后按桌面布局写 Library/ScriptAssemblies/last-good.json 与 ScriptAssets.json，
// 由 cook 的发现路径（AssetManager::LoadProjectManagedPayload）读取。之所以不走
// tc_web_player_set_cook_payload 预注入：tc_web_player_cook 内部的
// SetProject→Initialize→Shutdown 会清掉 AssetManager 的载荷覆写（上游 54697ebf 的
// 顺序问题，已在本机正向用例中实测复现），发现路径则是桌面 CLI 同款的官方通道。
// 细节见 managed-payload.mjs。

import { createRequire } from 'node:module'
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync, writeSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { buildManagedPayload } from './managed-payload.mjs'

// 播放器产物目录（Dockerfile 会写入 tomcat_player.js/.wasm/.data 与 package.json）。
const PLAYER_DIRECTORY = resolve(process.env.TOMCAT_PLAYER_DIR || '/app/player')
const PLAYER_MODULE = process.env.TOMCAT_PLAYER_MODULE || 'tomcat_player.js'
// 托管工具链目录（Dockerfile 会写入 TomCat.Managed.dll 与 TomCat.ScriptGenerator.dll）。
const MANAGED_DIRECTORY = resolve(process.env.TOMCAT_MANAGED_DIR || '/app/managed')
const DOTNET = process.env.TOMCAT_DOTNET || 'dotnet'

// 基础设施/用法错误使用固定退出码；真正的打包失败沿用 C 入口的返回码（1/2/3）。
const EXIT_INFRASTRUCTURE = 4

function emit(fd, message) {
  // 同步写入，保证父进程在进程退出前一定读到诊断（stdout/stderr 为管道时异步写入可能丢失）。
  try { writeSync(fd, `${message}\n`) } catch { /* 父进程可能已经关闭管道。 */ }
}
const log = message => emit(1, message)
const complain = message => emit(2, message)

// 同时接受 "<project> <output>" 与 "cook --project <project> --output <output>" 两种形态，
// 这样默认的 Cook:CliArgs 模板无需包装脚本即可直接指向本文件。
export function parseArguments(argv) {
  const rest = argv.slice(2)
  const positional = []
  let project
  let output
  for (let index = 0; index < rest.length; index += 1) {
    const value = rest[index]
    if (value === '--project') { project = rest[index + 1]; index += 1; continue }
    if (value === '--output') { output = rest[index + 1]; index += 1; continue }
    if (value === 'cook') continue
    positional.push(value)
  }
  return { project: project || positional[0], output: output || positional[1] }
}

const toPosix = value => value.replace(/\\/g, '/')

function ensureDirectory(FS, path) {
  if (!path || path === '/') return
  if (typeof FS.mkdirTree === 'function') { FS.mkdirTree(path); return }
  let current = ''
  for (const segment of path.split('/').filter(Boolean)) {
    current += `/${segment}`
    try { FS.mkdir(current) }
    catch (error) { if (error?.errno !== 20 /* EEXIST */) throw error }
  }
}

// 项目根下的构建产物/缓存不属于资产，镜像进 MEMFS 只会白占内存
// （Library 的载荷子树由 stageManagedLibrary 单独按需镜像）。
const STAGE_SKIP = new Set(['Library', 'Build', 'Builds', 'bin', 'obj'])

// 把宿主目录整棵镜像到 MEMFS 的同一绝对路径下：Project::Load 与 CookToPackage 都接受绝对路径，
// 因此无需做路径翻译，物化目录里的 Project.tcproj / Assets / ProjectSettings 原样可见。
function stage(FS, source, target, isRoot = true) {
  ensureDirectory(FS, target)
  for (const entry of readdirSync(source, { withFileTypes: true })) {
    const from = join(source, entry.name)
    const to = `${target}/${entry.name}`
    if (entry.isDirectory()) {
      if (isRoot && (STAGE_SKIP.has(entry.name) || entry.name.startsWith('.'))) continue
      stage(FS, from, to, false)
    } else if (entry.isFile()) FS.writeFile(to, new Uint8Array(readFileSync(from)))
  }
}

function playerError(module) {
  try {
    const message = module.ccall('tc_web_player_error', 'string', [], [])
    return message && message.length ? message : '（引擎未提供诊断信息）'
  } catch { return '（无法读取引擎诊断信息）' }
}

// 解析 C# 载荷的落盘布局。返回 undefined 表示失败（调用方据此非零退出）；
// 返回 null 表示项目没有 C# 脚本，无需任何载荷。
function resolveManagedPayload(projectRoot, log, complain) {
  return buildManagedPayload({ projectRoot, managedDirectory: MANAGED_DIRECTORY, dotnet: DOTNET, log, complain })
}

// cook 的发现路径只从 MEMFS 的 Library 读取载荷（LoadProjectManagedPayload），
// 因此构建产物所在的 Library 子树必须单独镜像（基础镜像跳过整个 Library）。
function stageManagedLibrary(FS, root) {
  const posixRoot = toPosix(root)
  stage(FS, join(root, 'Library', 'ScriptAssemblies'), `${posixRoot}/Library/ScriptAssemblies`, false)
  const scriptAssets = join(root, 'Library', 'ScriptProject', 'ScriptAssets.json')
  if (existsSync(scriptAssets)) {
    ensureDirectory(FS, `${posixRoot}/Library/ScriptProject`)
    FS.writeFile(`${posixRoot}/Library/ScriptProject/ScriptAssets.json`, new Uint8Array(readFileSync(scriptAssets)))
  }
}

async function run() {
  const { project, output } = parseArguments(process.argv)
  if (!project || !output) {
    complain('用法：node worker.mjs <project.tcproj> <output.tcpak>')
    return EXIT_INFRASTRUCTURE
  }
  const hostProject = resolve(project)
  const hostOutput = resolve(output)
  const root = dirname(hostProject)
  if (!existsSync(hostProject)) {
    complain(`项目文件不存在：${hostProject}`)
    return EXIT_INFRASTRUCTURE
  }
  const modulePath = join(PLAYER_DIRECTORY, PLAYER_MODULE)
  if (!existsSync(modulePath)) {
    complain(`找不到 Web Player 模块：${modulePath}（用 TOMCAT_PLAYER_DIR 指向 tomcat_player.js 所在目录）`)
    return EXIT_INFRASTRUCTURE
  }

  const memoryProject = toPosix(hostProject)
  const memoryOutput = toPosix(hostOutput)

  const require = createRequire(import.meta.url)
  let module
  try {
    const factory = require(modulePath)
    module = await factory({ locateFile: name => join(PLAYER_DIRECTORY, name), print: log, printErr: complain })
  } catch (error) {
    complain(`无法加载 Web Player 模块：${error?.message || error}`)
    return EXIT_INFRASTRUCTURE
  }

  try {
    // The standalone Web Player preloads sprites only; text scenes also need
    // the engine fonts when Cook builds their font artifacts.
    const fonts = join(PLAYER_DIRECTORY, 'Packages', 'fonts')
    if (existsSync(fonts)) stage(module.FS, fonts, '/Packages/fonts', false)
    const payload = resolveManagedPayload(root, log, complain)
    if (payload === undefined) return EXIT_INFRASTRUCTURE
    stage(module.FS, root, toPosix(root))
    if (payload) stageManagedLibrary(module.FS, root)
    ensureDirectory(module.FS, dirname(memoryOutput))
    const code = module.ccall('tc_web_player_cook', 'number', ['string', 'string'], [memoryProject, memoryOutput])
    if (code !== 0) {
      complain(`打包失败（tc_web_player_cook 返回 ${code}）：${playerError(module)}`)
      return code
    }
    if (!module.FS.analyzePath(memoryOutput).exists) {
      complain('打包完成但内存文件系统中没有游戏包。')
      return EXIT_INFRASTRUCTURE
    }
    const bytes = module.FS.readFile(memoryOutput)
    if (!bytes.length) {
      complain('打包产出的游戏包为空。')
      return EXIT_INFRASTRUCTURE
    }
    mkdirSync(dirname(hostOutput), { recursive: true })
    writeFileSync(hostOutput, Buffer.from(bytes))
    log(`已打包 ${memoryProject} → ${hostOutput}（${bytes.length} 字节）`)
    return 0
  } catch (error) {
    complain(`打包过程中出错：${error?.message || error}`)
    return EXIT_INFRASTRUCTURE
  } finally {
    // 终止 pthread 池，否则 Node 的事件循环可能被工作线程拖住而不退出。
    try { module.PThread?.terminateAllThreads?.() } catch { /* 尽力而为。 */ }
  }
}

// 仅在作为脚本直接运行时执行；被测试 import 时只导出纯函数。
const invokedDirectly = Boolean(process.argv[1]) && import.meta.url === pathToFileURL(process.argv[1]).href
if (invokedDirectly) process.exit(await run())
