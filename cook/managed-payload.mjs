// 服务端 C# 载荷构建：把项目里的用户脚本编译成 Assembly-CSharp.dll，并取出源生成器
// 嵌进程序集的脚本清单（ScriptManifest），供 tc_web_player_set_cook_payload 注入。
//
// 上游的桌面实现（Editor/TomCatInut/src/Scripting/ScriptProjectCompiler.cpp）整段在
// #ifdef TC_PLATFORM_WINDOWS 里，非 Windows 直接返回 "Script compilation is not
// implemented on this platform"。这里在 Linux 上复刻同一套契约：
//   Library/ScriptProject/Build/<id>/Assembly-CSharp.csproj + ScriptAssets.json
//   Library/ScriptAssemblies/Build/<id>/Assembly-CSharp.dll(.pdb)
// 源生成器 TomCat.ScriptGenerator 作为 Analyzer 运行，清单因此与桌面构建逐字一致。
//
// 清单本身不从零构造：引擎要求「注入的清单必须与程序集内嵌的清单完全相同」
// （AssetManager::ValidateManagedPackagePayload），所以这里按上游
// ExtractEmbeddedScriptManifest 的算法，从 DLL 的 UTF-16LE 字符串堆里把清单原样取出。

import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { join, relative, sep } from 'node:path'

// 与上游 AssetManager.cpp 的常量保持一致。
export const MANIFEST_MARKER = '{"version":1,"scripts":'
const MAXIMUM_MANIFEST_SIZE = 16 * 1024 * 1024
const DEPENDENCIES_PROJECT = 'TomCat.Dependencies.csproj'
// 源生成器只接受「一个 ScriptAssets.json 附加文件」；清空包源可保证离线 restore。
const NUGET_CONFIG = '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n  </packageSources>\n</configuration>\n'

const toPosix = value => value.split(sep).join('/')

function escapeXml(value) {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

// 上游 readHandle 的等价实现：.tcmeta 是 YAML，Handle 是 uint64 十进制字符串。
export function readScriptHandle(metaText) {
  const match = /^\s*Handle:\s*(\d+)\s*$/m.exec(metaText)
  return match && match[1] !== '0' ? match[1] : undefined
}

// 与前端 src/engine/runtime.ts 的 scriptAssetsJson 同构：手工拼 JSON 并保留十进制原文，
// 绝不经过 Number（uint64 超过 2^53 会丢精度，源生成器用 GetUInt64() 精确解析）。
export function scriptAssetsJson(scripts) {
  const entries = []
  for (const script of scripts) {
    if (!script.handle) continue
    if (!/^[1-9]\d*$/.test(script.handle)) throw new Error(`脚本 Handle 必须是十进制 uint64：${script.path}`)
    if (BigInt(script.handle) > 18446744073709551615n) throw new Error(`脚本 Handle 超出 uint64：${script.path}`)
    entries.push(`${JSON.stringify(script.path)}:${script.handle}`)
  }
  return `{"version":1,"assets":{${entries.join(',')}}}`
}

// 收集 Assets 下的全部 .cs，路径按项目相对路径排序（与桌面 EnumerateSources 一致）。
export function collectScripts(projectRoot) {
  const scripts = []
  const walk = directory => {
    let entries
    try { entries = readdirSync(directory, { withFileTypes: true }) } catch { return }
    for (const entry of entries) {
      const absolute = join(directory, entry.name)
      if (entry.isDirectory()) { walk(absolute); continue }
      if (!entry.isFile() || !entry.name.toLowerCase().endsWith('.cs')) continue
      let handle
      try { handle = readScriptHandle(readFileSync(`${absolute}.tcmeta`, 'utf8')) }
      catch { handle = undefined }
      scripts.push({ path: toPosix(relative(projectRoot, absolute)), absolutePath: absolute, handle })
    }
  }
  walk(join(projectRoot, 'Assets'))
  scripts.sort((left, right) => (left.path < right.path ? -1 : left.path > right.path ? 1 : 0))
  return scripts
}

// 上游 ExtractEmbeddedScriptManifest 的等价实现：清单以 C# 字符串字面量存在元数据
// #US 堆里，即每个字符后跟一个 0 字节的 UTF-16LE 序列；按大括号配对取出完整对象。
export function extractEmbeddedManifest(assembly) {
  for (let offset = 0; offset + MANIFEST_MARKER.length * 2 <= assembly.length; offset += 1) {
    let matches = true
    for (let index = 0; index < MANIFEST_MARKER.length; index += 1) {
      if (assembly[offset + index * 2] !== MANIFEST_MARKER.charCodeAt(index) || assembly[offset + index * 2 + 1] !== 0) {
        matches = false
        break
      }
    }
    if (!matches) continue
    let candidate = ''
    let depth = 0
    let inString = false
    let escaped = false
    for (let cursor = offset; cursor + 1 < assembly.length; cursor += 2) {
      if (assembly[cursor + 1] !== 0) break
      const character = String.fromCharCode(assembly[cursor])
      candidate += character
      if (inString) {
        if (escaped) escaped = false
        else if (character === '\\') escaped = true
        else if (character === '"') inString = false
        continue
      }
      if (character === '"') inString = true
      else if (character === '{' || character === '[') depth += 1
      else if (character === '}' || character === ']') {
        depth -= 1
        if (depth === 0) return candidate
        if (depth < 0) break
      }
      if (candidate.length > MAXIMUM_MANIFEST_SIZE) break
    }
  }
  return null
}

export function sourceHash(scripts) {
  const hash = createHash('sha256')
  hash.update('TomCat.ScriptProject.v3-dependencies\n')
  for (const script of scripts) {
    hash.update(script.path)
    hash.update('\n')
    hash.update(script.handle ?? '0')
    hash.update('\n')
    hash.update(readFileSync(script.absolutePath))
    hash.update('\n')
  }
  return hash.digest('hex')
}

// 生成 Assembly-CSharp.csproj。内容与桌面 WriteGeneratedProject 对应，只去掉
// TomCat.Dependencies.csproj 分支（容器打包器暂不支持项目内依赖工程，见下），
// 并且不设置 RuntimeIdentifier：载荷由引擎标记为 portable。
export function generatedProjectXml(options) {
  const {
    projectRoot, buildDirectory, objectDirectory,
    disabledImportPath, scripts, managedApiPath, generatorPath, scriptAssetsPath,
  } = options
  const lines = []
  lines.push('<Project>')
  lines.push('  <!-- Dependency policy: set these before Sdk.props can discover any project-local or per-user imports. -->')
  lines.push('  <PropertyGroup>')
  for (const property of [
    'ImportDirectoryBuildProps', 'ImportDirectoryBuildTargets', 'ImportDirectoryPackagesProps',
    'ImportProjectExtensionProps', 'ImportProjectExtensionTargets',
    'ImportUserLocationsByWildcardBeforeMicrosoftCommonProps',
    'ImportUserLocationsByWildcardAfterMicrosoftCommonProps',
    'ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets',
    'ImportUserLocationsByWildcardAfterMicrosoftCommonTargets',
    'ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets',
    'ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets',
    'ImportByWildcardBeforeMicrosoftCommonProps', 'ImportByWildcardAfterMicrosoftCommonProps',
    'ImportByWildcardBeforeMicrosoftCommonTargets', 'ImportByWildcardAfterMicrosoftCommonTargets',
    'ImportByWildcardBeforeMicrosoftCSharpTargets', 'ImportByWildcardAfterMicrosoftCSharpTargets',
  ]) lines.push(`    <${property}>false</${property}>`)
  lines.push('    <RestoreEnableGlobalPackageReference>false</RestoreEnableGlobalPackageReference>')
  lines.push('    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>')
  lines.push('    <CustomBeforeDirectoryBuildProps />')
  lines.push('    <CustomAfterDirectoryBuildProps />')
  lines.push('    <CustomBeforeDirectoryBuildTargets />')
  lines.push('    <CustomAfterDirectoryBuildTargets />')
  const disabled = escapeXml(toPosix(disabledImportPath))
  for (const name of ['CustomBeforeMicrosoftCommonProps', 'CustomAfterMicrosoftCommonProps',
    'CustomBeforeMicrosoftCommonTargets', 'CustomAfterMicrosoftCommonTargets',
    'CustomBeforeMicrosoftCSharpTargets', 'CustomAfterMicrosoftCSharpTargets']) {
    lines.push(`    <${name}>${disabled}.${name.includes('Targets') ? 'targets' : 'props'}</${name}>`)
  }
  lines.push(`    <BaseIntermediateOutputPath>${escapeXml(toPosix(objectDirectory))}/</BaseIntermediateOutputPath>`)
  lines.push(`    <MSBuildProjectExtensionsPath>${escapeXml(toPosix(objectDirectory))}/</MSBuildProjectExtensionsPath>`)
  lines.push('  </PropertyGroup>')
  lines.push('  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />')
  lines.push('  <PropertyGroup>')
  lines.push('    <TargetFramework>net10.0</TargetFramework>')
  lines.push('    <SelfContained>false</SelfContained>')
  lines.push('    <AssemblyName>Assembly-CSharp</AssemblyName>')
  lines.push('    <RootNamespace>Game</RootNamespace>')
  lines.push('    <OutputType>Library</OutputType>')
  lines.push('    <LangVersion>latest</LangVersion>')
  lines.push('    <Nullable>enable</Nullable>')
  lines.push('    <ImplicitUsings>disable</ImplicitUsings>')
  lines.push('    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>')
  lines.push('    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>')
  lines.push('    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>')
  lines.push('    <Deterministic>true</Deterministic>')
  lines.push('    <DebugType>portable</DebugType>')
  lines.push('    <DebugSymbols>true</DebugSymbols>')
  lines.push('    <Optimize>false</Optimize>')
  lines.push('    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>')
  lines.push('    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>')
  lines.push(`    <OutputPath>${escapeXml(toPosix(buildDirectory))}/</OutputPath>`)
  lines.push(`    <IntermediateOutputPath>${escapeXml(toPosix(objectDirectory))}/</IntermediateOutputPath>`)
  lines.push(`    <PathMap>${escapeXml(toPosix(projectRoot))}=.</PathMap>`)
  lines.push('  </PropertyGroup>')
  lines.push('  <ItemGroup>')
  lines.push(`    <Reference Include="TomCat.Managed"><HintPath>${escapeXml(toPosix(managedApiPath))}</HintPath><Private>false</Private><TomCatTrustedReference>true</TomCatTrustedReference></Reference>`)
  lines.push(`    <Analyzer Include="${escapeXml(toPosix(generatorPath))}" />`)
  lines.push(`    <AdditionalFiles Include="${escapeXml(toPosix(scriptAssetsPath))}" />`)
  for (const script of scripts) {
    lines.push(`    <Compile Include="${escapeXml(toPosix(script.absolutePath))}" Link="${escapeXml(script.path)}" />`)
  }
  lines.push('  </ItemGroup>')
  lines.push('  <ItemGroup>')
  lines.push('    <_TomCatBlockedPackageItem Include="@(PackageReference);@(PackageDownload);@(GlobalPackageReference);@(DotNetCliToolReference)" />')
  lines.push('    <_TomCatBlockedReference Include="@(Reference)" />')
  lines.push('    <_TomCatBlockedReference Remove="@(_TomCatBlockedReference->WithMetadataValue(\'TomCatTrustedReference\', \'true\'))" />')
  lines.push('  </ItemGroup>')
  lines.push('  <Target Name="TomCatValidateBuildInputs" BeforeTargets="_GenerateRestoreProjectSpec;CollectPackageReferences;ResolveReferences;CoreCompile">')
  lines.push('    <Error Code="TCSP0021" Condition="\'@(_TomCatBlockedPackageItem)\' != \'\'" Text="NuGet PackageReference and package download items are disabled for TomCat scripts." />')
  lines.push('    <Error Code="TCSP0022" Condition="\'@(_TomCatBlockedReference)\' != \'\'" Text="Local or third-party managed references are disabled for TomCat scripts." />')
  lines.push('  </Target>')
  lines.push('  <Target Name="TomCatEmbedDependencies" BeforeTargets="AssignTargetPaths" DependsOnTargets="ResolveReferences">')
  lines.push('    <Error Code="TCSP0023" Condition="\'@(NativeCopyLocalItems)\' != \'\'" Text="TomCat currently supports managed dependencies only; native package assets cannot be loaded." />')
  lines.push('    <Error Code="TCSP0024" Condition="\'@(_ContentCopyLocalItems)\' != \'\'" Text="TomCat cannot bundle package content files; use managed DLL resources instead." />')
  lines.push('    <ItemGroup>')
  lines.push('      <EmbeddedResource Include="@(ReferenceCopyLocalPaths)" Condition="\'%(Extension)\' == \'.dll\' And \'%(Filename)\' != \'TomCat.Managed\' And \'%(Filename)\' != \'TomCat.ScriptGenerator\'">')
  lines.push('        <LogicalName>TomCat.Dependency/%(ReferenceCopyLocalPaths.DestinationSubDirectory)%(ReferenceCopyLocalPaths.Filename)%(ReferenceCopyLocalPaths.Extension)</LogicalName>')
  lines.push('        <WithCulture>false</WithCulture>')
  lines.push('      </EmbeddedResource>')
  lines.push('    </ItemGroup>')
  lines.push('  </Target>')
  lines.push('  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />')
  lines.push('</Project>')
  lines.push('')
  return lines.join('\n')
}

// 与桌面 RunRestrictedDotNetBuild 一致：这些是命令行全局属性，环境变量或导入的
// 工程都无法把它们改回来，保证用户脚本目录不能引入任意 MSBuild 扩展点。
const LOCKED_PROPERTIES = [
  'ImportDirectoryBuildProps=false', 'ImportDirectoryBuildTargets=false',
  'ImportDirectoryPackagesProps=false', 'ImportProjectExtensionProps=false',
  'ImportProjectExtensionTargets=false', 'RestoreEnableGlobalPackageReference=false',
  'ManagePackageVersionsCentrally=false',
  'ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false',
  'ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false',
  'ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false',
  'ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false',
  'ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false',
  'ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false',
  'ImportByWildcardBeforeMicrosoftCommonProps=false',
  'ImportByWildcardAfterMicrosoftCommonProps=false',
  'ImportByWildcardBeforeMicrosoftCommonTargets=false',
  'ImportByWildcardAfterMicrosoftCommonTargets=false',
  'ImportByWildcardBeforeMicrosoftCSharpTargets=false',
  'ImportByWildcardAfterMicrosoftCSharpTargets=false',
]

function tail(text, limit = 4000) {
  const trimmed = (text || '').trim()
  return trimmed.length <= limit ? trimmed : `…${trimmed.slice(-limit)}`
}

export function runDotNetBuild(dotnet, projectPath, workingDirectory, timeoutMs = 600000) {
  const result = spawnSync(dotnet, [
    'build', projectPath, '--configuration', 'Release', '--nologo', '--verbosity', 'minimal',
    '-nodeReuse:false', '-p:UseSharedCompilation=false',
    ...LOCKED_PROPERTIES.map(property => `-p:${property}`),
  ], {
    cwd: workingDirectory,
    encoding: 'utf8',
    timeout: timeoutMs,
    maxBuffer: 32 * 1024 * 1024,
    killSignal: 'SIGKILL',
    windowsHide: true,
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1' },
  })
  const output = `${result.stdout || ''}${result.stderr || ''}${result.error ? `\n${result.error.message}` : ''}`
  return { status: result.status ?? -1, output }
}

// 编译用户脚本并返回可直接注入的载荷。项目没有 C# 脚本时返回 null。
export function buildManagedPayload(options) {
  const { projectRoot, managedDirectory, dotnet, log } = options
  const scripts = collectScripts(projectRoot)
  if (scripts.length === 0) return null
  if (existsSync(join(projectRoot, DEPENDENCIES_PROJECT))) {
    throw new Error(`容器打包器暂不支持项目内依赖工程 ${DEPENDENCIES_PROJECT}；请在桌面 Editor 中打包，或先移除该文件。`)
  }
  const managedApiPath = join(managedDirectory, 'TomCat.Managed.dll')
  const generatorPath = join(managedDirectory, 'TomCat.ScriptGenerator.dll')
  for (const path of [managedApiPath, generatorPath]) {
    if (!existsSync(path)) {
      throw new Error(`缺少托管工具链程序集：${path}（设置 TOMCAT_MANAGED_DIR 指向镜像内的 Managed 目录）`)
    }
  }

  // BuildID 只需满足引擎的 IsSafeBuildID（字母数字/-/_/. ，≤128），用源码哈希前 32 位即可。
  const sourceHashHex = sourceHash(scripts)
  const buildId = sourceHashHex.slice(0, 32)
  const scriptProjectDirectory = join(projectRoot, 'Library', 'ScriptProject', 'Build', buildId)
  const assembliesDirectory = join(projectRoot, 'Library', 'ScriptAssemblies')
  const buildDirectory = join(assembliesDirectory, 'Build', buildId)
  const objectDirectory = join(scriptProjectDirectory, 'obj', buildId)
  const disabledImportPath = join(scriptProjectDirectory, `TomCat.Imports.Disabled.${buildId}`)
  mkdirSync(objectDirectory, { recursive: true })
  mkdirSync(buildDirectory, { recursive: true })

  const scriptAssetsPath = join(scriptProjectDirectory, 'ScriptAssets.json')
  writeFileSync(scriptAssetsPath, scriptAssetsJson(scripts))
  writeFileSync(join(scriptProjectDirectory, 'NuGet.Config'), NUGET_CONFIG)
  const projectPath = join(scriptProjectDirectory, 'Assembly-CSharp.csproj')
  writeFileSync(projectPath, generatedProjectXml({
    projectRoot, buildDirectory, objectDirectory,
    disabledImportPath, scripts, managedApiPath, generatorPath, scriptAssetsPath,
  }))

  log(`编译 ${scripts.length} 个 C# 脚本（build ${buildId}）`)
  const { status, output } = runDotNetBuild(dotnet, projectPath, scriptProjectDirectory)
  if (status !== 0) throw new Error(`dotnet build 失败（退出码 ${status}）：\n${tail(output)}`)
  const assemblyPath = join(buildDirectory, 'Assembly-CSharp.dll')
  if (!existsSync(assemblyPath)) {
    throw new Error(`dotnet build 未产出 Assembly-CSharp.dll：\n${tail(output)}`)
  }
  const assembly = new Uint8Array(readFileSync(assemblyPath))
  const manifestJson = extractEmbeddedManifest(assembly)
  if (!manifestJson) {
    throw new Error('Assembly-CSharp.dll 中没有源生成器写出的脚本清单（ScriptManifest.Json）。')
  }
  const pdbPath = join(buildDirectory, 'Assembly-CSharp.pdb')
  const hasPdb = existsSync(pdbPath)
  // 引擎 cook 的发现路径（AssetManager::LoadProjectManagedPayload）从
  // Library/ScriptAssemblies/last-good.json 读取上次成功构建：worker 无法用
  // tc_web_player_set_cook_payload 预注入（tc_web_player_cook 内部的
  // SetProject→Initialize→Shutdown 会清掉覆写），因此必须按桌面布局落盘。
  writeFileSync(join(assembliesDirectory, 'last-good.json'), JSON.stringify({
    version: 1,
    sourceHash: sourceHashHex.slice(0, 16),
    buildId,
    assembly: `Build/${buildId}/Assembly-CSharp.dll`,
    ...(hasPdb ? { pdb: `Build/${buildId}/Assembly-CSharp.pdb` } : {}),
  }))
  // 发现路径还会校验 Library/ScriptProject/ScriptAssets.json 的 Handle 映射。
  mkdirSync(join(projectRoot, 'Library', 'ScriptProject'), { recursive: true })
  writeFileSync(join(projectRoot, 'Library', 'ScriptProject', 'ScriptAssets.json'), scriptAssetsJson(scripts))
  return {
    buildId,
    manifestJson,
    assemblyPath,
    assemblyBytes: assembly,
    pdbBytes: hasPdb ? new Uint8Array(readFileSync(pdbPath)) : new Uint8Array(0),
    scriptCount: scripts.length,
    assemblySize: statSync(assemblyPath).size,
  }
}
