import test from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createHmac } from 'node:crypto'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
test('signed Netlify client IP survives rotating proxy addresses and forged signatures fail closed', async () => {
 const directory = await mkdtemp(join(tmpdir(), 'tomcat-proxy-'))
 const secret = 'test-proxy-signing-secret-not-production'
 const child = spawn('dotnet',[resolve('TomCat.Api/bin/Release/net10.0/TomCat.Api.dll'),'--urls','http://127.0.0.1:0'],{cwd:resolve('TomCat.Api'),windowsHide:true,stdio:['ignore','pipe','pipe'],env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',Storage__Directory:directory,Proxy__Secret:secret}})
 try {
  const base = await new Promise((yes,no)=>{let output='';const timer=setTimeout(()=>{child.kill();no(new Error(output))},20000);const read=data=>{output+=data;const match=output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/);if(match){clearTimeout(timer);yes(match[1])}};child.stdout.on('data',read);child.stderr.on('data',read);child.once('error',no)})
  const sign = (key,expiry) => {const encoded=[Buffer.from(JSON.stringify({alg:'HS256'})).toString('base64url'),Buffer.from(JSON.stringify({iss:'netlify',exp:expiry})).toString('base64url')].join('.');return encoded+'.'+createHmac('sha256',key).update(encoded).digest('base64url')}
  const signature=sign(secret,Math.floor(Date.now()/1000)+600)
  const login=(ip,index,token=signature)=>fetch(base+'/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json','X-TomCat-Request':'1','X-Forwarded-For':`192.0.2.${index+1}`,'x-nf-client-connection-ip':ip,'x-nf-sign':token},body:JSON.stringify({email:'missing@example.com',password:'invalid-password-123'})})
  for(let i=0;i<24;i++)assert.equal((await login('203.0.113.1',i)).status,i<20?401:429)
  assert.equal((await login('203.0.113.2',0)).status,401)
  assert.equal((await login('203.0.113.3',0,sign('wrong',Math.floor(Date.now()/1000)+600))).status,403)
  assert.equal((await login('203.0.113.3',0,sign(secret,1))).status,403)
  assert.equal((await login('203.0.113.3',0,'')).status,403)
 }finally{if(child.exitCode===null)await new Promise(yes=>{child.once('exit',yes);child.kill()});await rm(directory,{recursive:true,force:true})}
})
