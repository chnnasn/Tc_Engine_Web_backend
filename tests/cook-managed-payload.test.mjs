import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  buildManagedPayload, collectScripts, extractEmbeddedManifest,
  generatedProjectXml, readScriptHandle, scriptAssetsJson, sourceHash,
} from '../cook/managed-payload.mjs'

// The container cooker replaces the desktop CompileManaged, which upstream only
// implements for Windows. These tests pin the pure contract: .tcmeta handle
// parsing, the ScriptAssets.json the source generator reads, the generated
// Assembly-CSharp.csproj, and the byte-exact extraction of the manifest the
// generator embeds in the assembly. The real dotnet build is exercised in the
// image; it is not required here.

const scriptMeta = handle => `SchemaVersion: 2
Asset:
  Handle: ${handle}
  Type: CSharpScript
  ImportSettings:
    {}
  SubAssets:
    []
`

function utf16le(text) {
  const bytes = new Uint8Array(text.length * 2)
  for (let index = 0; index < text.length; index += 1) {
    bytes[index * 2] = text.charCodeAt(index) & 0xff
    bytes[index * 2 + 1] = (text.charCodeAt(index) >> 8) & 0xff
  }
  return bytes
}

function concatenate(...parts) {
  const total = parts.reduce((sum, part) => sum + part.length, 0)
  const bytes = new Uint8Array(total)
  let offset = 0
  for (const part of parts) { bytes.set(part, offset); offset += part.length }
  return bytes
}

test('reads the AssetHandle out of a .tcmeta file', () => {
  assert.equal(readScriptHandle(scriptMeta('5478837881518915558')), '5478837881518915558')
  assert.equal(readScriptHandle(scriptMeta('0')), undefined)
  assert.equal(readScriptHandle('SchemaVersion: 2\nAsset:\n  Type: CSharpScript\n'), undefined)
})

test('emits ScriptAssets.json without losing uint64 precision', () => {
  const json = scriptAssetsJson([{ path: 'Assets/Scripts/Spinner.cs', handle: '5478837881518915558' }])
  assert.equal(json, '{"version":1,"assets":{"Assets/Scripts/Spinner.cs":5478837881518915558}}')
  // The handle must survive as a decimal literal, never through Number.
  assert.equal(JSON.parse(json).assets['Assets/Scripts/Spinner.cs'], 5478837881518915584)
  assert.equal(json.includes('5478837881518915558'), true)
  assert.throws(() => scriptAssetsJson([{ path: 'a.cs', handle: '12x' }]), /十进制 uint64/)
  assert.throws(() => scriptAssetsJson([{ path: 'a.cs', handle: '18446744073709551616' }]), /超出 uint64/)
  assert.equal(scriptAssetsJson([{ path: 'a.cs' }]), '{"version":1,"assets":{}}')
})

test('collects Assets scripts in project-relative order', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-scripts-'))
  try {
    await mkdir(join(directory, 'Assets', 'Scripts', 'Nested'), { recursive: true })
    await mkdir(join(directory, 'Assets', 'Textures'), { recursive: true })
    await writeFile(join(directory, 'Assets', 'Scripts', 'Beta.cs'), 'class Beta {}\n')
    await writeFile(join(directory, 'Assets', 'Scripts', 'Beta.cs.tcmeta'), scriptMeta('2002'))
    await writeFile(join(directory, 'Assets', 'Scripts', 'Nested', 'Alpha.cs'), 'class Alpha {}\n')
    await writeFile(join(directory, 'Assets', 'Scripts', 'Nested', 'Alpha.cs.tcmeta'), scriptMeta('2001'))
    await writeFile(join(directory, 'Assets', 'Scripts', 'Unmapped.cs'), 'class Unmapped {}\n')
    await writeFile(join(directory, 'Assets', 'Textures', 'logo.png'), 'not a script')
    const scripts = collectScripts(directory)
    assert.deepEqual(scripts.map(script => script.path), [
      'Assets/Scripts/Beta.cs', 'Assets/Scripts/Nested/Alpha.cs', 'Assets/Scripts/Unmapped.cs',
    ])
    assert.deepEqual(scripts.map(script => script.handle), ['2002', '2001', undefined])
    assert.equal(sourceHash(scripts).length, 64)
  } finally { await rm(directory, { recursive: true, force: true }) }
})

test('extracts the UTF-16LE script manifest embedded in the assembly', () => {
  const manifest = '{"version":1,"scripts":[{"assetHandle":7,"typeName":"Game.A}B","fields":[]}]}'
  const assembly = concatenate(new Uint8Array([0x4d, 0x5a, 0x00, 0x11]), utf16le(manifest), utf16le('trailing text'))
  assert.equal(extractEmbeddedManifest(assembly), manifest)
  assert.equal(extractEmbeddedManifest(utf16le(manifest)), manifest)
  assert.equal(extractEmbeddedManifest(new Uint8Array([1, 2, 3, 4])), null)
  // A truncated manifest must not be reported as a match.
  const truncated = concatenate(utf16le('{"version":1,"scripts":[{'), new Uint8Array([0x41, 0x00, 0x00, 0x01]))
  assert.equal(extractEmbeddedManifest(truncated), null)
})

test('generates the same Assembly-CSharp contract as the desktop compiler', () => {
  const xml = generatedProjectXml({
    projectRoot: '/tmp/project', buildDirectory: '/tmp/project/Library/ScriptAssemblies/Build/id',
    objectDirectory: '/tmp/project/Library/ScriptProject/Build/id/obj/id',
    disabledImportPath: '/tmp/project/Library/ScriptProject/Build/id/TomCat.Imports.Disabled.id',
    scripts: [{ path: 'Assets/Scripts/Spinner.cs', absolutePath: '/tmp/project/Assets/Scripts/Spinner.cs', handle: '42' }],
    managedApiPath: '/app/managed/TomCat.Managed.dll',
    generatorPath: '/app/managed/TomCat.ScriptGenerator.dll',
    scriptAssetsPath: '/tmp/project/Library/ScriptProject/Build/id/ScriptAssets.json',
  })
  assert.match(xml, /<TargetFramework>net10\.0<\/TargetFramework>/)
  assert.match(xml, /<AssemblyName>Assembly-CSharp<\/AssemblyName>/)
  assert.match(xml, /<EnableDefaultCompileItems>false<\/EnableDefaultCompileItems>/)
  assert.match(xml, /<Analyzer Include="\/app\/managed\/TomCat\.ScriptGenerator\.dll" \/>/)
  assert.match(xml, /<AdditionalFiles Include="[^"]*ScriptAssets\.json" \/>/)
  assert.match(xml, /<Compile Include="\/tmp\/project\/Assets\/Scripts\/Spinner\.cs" Link="Assets\/Scripts\/Spinner\.cs" \/>/)
  // The trusted reference must be exempted from the injection guard, or the build always fails.
  assert.match(xml, /<TomCatTrustedReference>true<\/TomCatTrustedReference>/)
  assert.match(xml, /_TomCatBlockedReference Remove=/)
  assert.match(xml, /Code="TCSP0021"/)
  assert.match(xml, /Code="TCSP0022"/)
  // The payload is marked portable by the engine, so the project must not pin a desktop RID.
  assert.doesNotMatch(xml, /<RuntimeIdentifier>/)
  assert.match(xml, /<AppendRuntimeIdentifierToOutputPath>false<\/AppendRuntimeIdentifierToOutputPath>/)
})

test('skips the payload when the project has no C# scripts', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-noscripts-'))
  try {
    await mkdir(join(directory, 'Assets'), { recursive: true })
    const payload = buildManagedPayload({
      projectRoot: directory, managedDirectory: join(directory, 'absent'), dotnet: 'dotnet', log: () => {},
    })
    assert.equal(payload, null)
  } finally { await rm(directory, { recursive: true, force: true }) }
})

test('reports a missing managed toolchain instead of cooking', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'tomcat-notoolchain-'))
  try {
    await mkdir(join(directory, 'Assets', 'Scripts'), { recursive: true })
    await writeFile(join(directory, 'Assets', 'Scripts', 'Spinner.cs'), 'class Spinner {}\n')
    await writeFile(join(directory, 'Assets', 'Scripts', 'Spinner.cs.tcmeta'), scriptMeta('42'))
    assert.throws(() => buildManagedPayload({
      projectRoot: directory, managedDirectory: join(directory, 'absent'), dotnet: 'dotnet', log: () => {},
    }), /缺少托管工具链程序集/)
  } finally { await rm(directory, { recursive: true, force: true }) }
})
