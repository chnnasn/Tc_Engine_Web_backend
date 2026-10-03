import assert from 'node:assert/strict'
import { readdir, readFile, stat } from 'node:fs/promises'
import { join } from 'node:path'
export async function mailedCode(directory, email) {
  const files = await Promise.all((await readdir(join(directory, 'mail'))).map(async file => ({ file, modified: (await stat(join(directory, 'mail', file))).mtimeMs })))
  for (const { file } of files.sort((a, b) => b.modified - a.modified)) {
    const message = await readFile(join(directory, 'mail', file), 'utf8')
    if (!message.includes(`To: ${email}`)) continue
    const textPart = message.match(/Content-Type: text\/plain[\s\S]*?(?=\r?\n--|$)/i)?.[0] || message
    const body = textPart.split(/\r?\n\r?\n/).slice(1).join('\n')
    const decoded = /Content-Transfer-Encoding: base64/i.test(textPart) ? Buffer.from(body.replace(/\s/g, ''), 'base64').toString('utf8') : body
    const match = decoded.match(/\b[0-9]{6}\b/)
    if (match) return match[0]
  }
  throw new Error('Verification code absent from local mail')
}
export async function emailRegister(post, directory, username, password) {
  const email = `${username}@example.com`
  const sent = await post('/auth/register', { email, password }); assert.equal(sent.status, 200)
  assert.equal(sent.headers.get('set-cookie'), null)
  const { challengeId } = await sent.json()
  const verified = await post('/auth/verify-email', { challengeId, code: await mailedCode(directory, email) }); assert.equal(verified.status, 200)
  const { token } = await verified.json()
  return post('/auth/complete-registration', { token, username })
}
