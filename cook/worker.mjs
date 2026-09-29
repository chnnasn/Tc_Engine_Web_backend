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
// 取出程序集内嵌的脚本清单，经 tc_web_player_set_cook_payload 注入后再 cook。这条链路
// 替代了桌面的 CompileManaged（上游那份实现只在 Windows 下可用）。细节见 managed-payload.mjs。

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

// 项目根下的构建产物/缓存不属于资产，镜像进 MEMFS 只会白占内存（C# 编译产物就在 Library 下）。
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

// 解析要注入的托管载荷：
//   - 显式配置（TOMCAT_COOK_ASSEMBLY / _MANIFEST / _BUILD_ID / 可选 _PDB）优先，便于离线调试；
//   - 否则项目里有 C# 脚本时在容器内编译（dotnet build + 源生成器）；
//   - 项目没有 C# 脚本时返回 null，表示不需要注入。
// 返回 undefined 表示失败（调用方据此非零退出）。
function resolveManagedPayload(projectRoot, log, complain) {
  const assemblyPath = process.env.TOMCAT_COOK_ASSEMBLY
  if (assemblyPath) {
    const manifestPath = process.env.TOMCAT_COOK_MANIFEST
    const buildId = process.env.TOMCAT_COOK_BUILD_ID
    if (!manifestPath || !buildId) {
      complain('显式托管载荷需要同时设置 TOMCAT_COOK_MANIFEST 与 TOMCAT_COOK_BUILD_ID。')
      return undefined
    }
    if (!existsSync(assemblyPath) || !existsSync(manifestPath)) {
      complain(`托管载荷文件缺失：${assemblyPath} / ${manifestPath}`)
      return undefined
    }
    const pdbPath = process.env.TOMCAT_COOK_PDB
    return {
      buildId,
      assemblyBytes: new Uint8Array(readFileSync(assemblyPath)),
      manifestJson: readFileSync(manifestPath, 'utf8'),
      pdbBytes: pdbPath && existsSync(pdbPath) ? new Uint8Array(readFileSync(pdbPath)) : new Uint8Array(0),
    }
  }
  try {
    return buildManagedPayload({ projectRoot, managedDirectory: MANAGED_DIRECTORY, dotnet: DOTNET, log })
  } catch (error) {
    complain(`C# 脚本编译失败：${error?.message || error}`)
    return undefined
  }
}

// 经公开覆写注入已校验的托管载荷，随后的 cook 会优先使用它。
function injectManagedPayload(module, payload, complain) {
  const assembly = payload.assemblyBytes
  const pdb = payload.pdbBytes
  const assemblyPointer = module._malloc(assembly.length)
  const pdbPointer = pdb && pdb.length ? module._malloc(pdb.length) : 0
  try {
    module.HEAPU8.set(assembly, assemblyPointer)
    if (pdbPointer) module.HEAPU8.set(pdb, pdbPointer)
    const code = module.ccall('tc_web_player_set_cook_payload', 'number',
      ['number', 'number', 'string', 'string', 'number', 'number'],
      [assemblyPointer, assembly.length, payload.manifestJson, payload.buildId, pdbPointer, pdb ? pdb.length : 0])
    if (code !== 0) {
      complain(`托管载荷被拒绝（返回 ${code}）：${playerError(module)}`)
      return false
    }
    log(`已注入托管载荷 build ${payload.buildId}（程序集 ${assembly.length} 字节，清单 ${payload.manifestJson.length} 字节）`)
    return true
  } finally {
    module._free(assemblyPointer)
    if (pdbPointer) module._free(pdbPointer)
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
    const payload = resolveManagedPayload(root, log, complain)
    if (payload === undefined) return EXIT_INFRASTRUCTURE
    if (payload && !injectManagedPayload(module, payload, complain)) return EXIT_INFRASTRUCTURE
    stage(module.FS, root, toPosix(root))
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
