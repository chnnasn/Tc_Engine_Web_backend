import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { resolve } from 'node:path'

export const buildSecret = 'test-only-build-service-secret-32-characters'
export async function freePort() {
  const server = createServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  await new Promise(resolve => server.close(resolve))
  return port
}
export async function startService(project, environment = {}, port = 0) {
  const child = spawn('dotnet', [resolve(`${project}/bin/Release/net10.0/${project}.dll`), '--urls', `http://127.0.0.1:${port}`], {
    cwd: resolve(project), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, PORT: '', ASPNETCORE_ENVIRONMENT: 'Development', ...environment },
  })
  let output = ''
  const baseUrl = await new Promise((resolveUrl, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error(output || 'Service startup timed out')) }, 20000)
    const fail = error => { clearTimeout(timer); reject(error) }
    child.once('error', fail)
    child.once('exit', code => fail(new Error(`Service exited ${code}: ${output}`)))
    const read = chunk => {
      output += chunk
      const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/)
      if (match) { clearTimeout(timer); resolveUrl(match[1]) }
    }
    child.stdout.on('data', read); child.stderr.on('data', read)
  })
  return {
    baseUrl, child, diagnostics: () => output,
    async stop() {
      if (child.exitCode !== null || child.signalCode !== null) return
      await new Promise(done => { child.once('exit', done); child.kill() })
    },
  }
}
export const startBuildWorker = (apiUrl, port, fakeCli) => startService('TomCat.BuildWorker', {
  Build__ApiUrl: apiUrl, Build__Secret: buildSecret,
  Cook__CliPath: process.execPath, Cook__CliArgs: `"${fakeCli}" cook --project "{project}" --output "{output}"`,
}, port)
