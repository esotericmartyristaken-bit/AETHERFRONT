import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileHash, writeJson } from '../public-status.mjs';
const execute=promisify(execFile),script=path.resolve(import.meta.dirname,'../Start-NativeServer.ps1');
async function fixture(t,{journal=false,permit=false,mutation={}}={}) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-server-router-'));t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const nativeRoot=path.join(root,'native'),directory=path.join(root,'bootstrap','native');
  await fs.mkdir(nativeRoot,{recursive:true});await fs.mkdir(directory,{recursive:true});
  await fs.writeFile(path.join(directory,'Start-Server.ps1'),`param([string]$DeploymentRoot,[string]$LauncherLog,[switch]$PublicationActivation)
if(-not $PublicationActivation -and (Test-Path -LiteralPath (Join-Path $DeploymentRoot '.update-transaction.json'))){throw 'Fixture refuses interrupted native activation'}
[IO.File]::WriteAllText((Join-Path $DeploymentRoot 'observed.json'),('{"publication":'+([bool]$PublicationActivation).ToString().ToLowerInvariant()+'}'))
`);
  const selected={directory,sha256:await fileHash(path.join(directory,'Start-Server.ps1'))};
  await writeJson(path.join(root,'native-cache','current-native.json'),selected);
  await writeJson(path.join(root,'host.json'),{root,nativeRoot});
  const journalFile=path.join(nativeRoot,'.update-transaction.json');if(journal)await fs.writeFile(journalFile,'preserved fixture native journal');
  const value={schemaVersion:1,purpose:'native-publication-start',id:'a'.repeat(32),createdAt:new Date().toISOString(),nativeRoot,journalSha256:journal?await fileHash(journalFile):null,launcherSha256:selected.sha256,...mutation};
  if(permit)await writeJson(path.join(root,'native-cache','server-start-permit.json'),value);
  return {root,nativeRoot,value,run:()=>execute('pwsh.exe',['-NoProfile','-NonInteractive','-File',script,'-Settings',path.join(root,'host.json')],{windowsHide:true,timeout:15000,maxBuffer:16384})};
}
test('normal SYSTEM task start does not bypass interrupted-publication checks',async t=>{
  const safe=await fixture(t);await safe.run();assert.deepEqual(JSON.parse(await fs.readFile(path.join(safe.nativeRoot,'observed.json'),'utf8')),{publication:false});
  const interrupted=await fixture(t,{journal:true});await assert.rejects(interrupted.run());assert.equal(await fs.readFile(path.join(interrupted.nativeRoot,'.update-transaction.json'),'utf8'),'preserved fixture native journal');
});
test('authenticated publication start permit is journal-bound and single-use',async t=>{
  const f=await fixture(t,{journal:true,permit:true});await f.run();
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.nativeRoot,'observed.json'),'utf8')),{publication:true});
  assert.equal(await fs.stat(path.join(f.root,'native-cache','server-start-permit.json')).catch(()=>null),null);
  await assert.rejects(f.run());
});
for(const [name,mutation] of Object.entries({expired:{createdAt:new Date(Date.now()-120000).toISOString()},wrongJournal:{journalSha256:'b'.repeat(64)},wrongLauncher:{launcherSha256:'b'.repeat(64)},unknownSchema:{schemaVersion:2}}))test(`router refuses ${name} permit without launching anything`,async t=>{
  const f=await fixture(t,{journal:true,permit:true,mutation});await assert.rejects(f.run());
  assert.equal(await fs.stat(path.join(f.nativeRoot,'observed.json')).catch(()=>null),null);
});
