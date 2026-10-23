import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { ControlEngine, validateTransaction, verifyRelease } from '../engine.mjs';
import { CAPABILITIES, REQUIRED_CONTROL_FILES } from '../contract.mjs';
import { digest, writeJson, readJson } from '../public-status.mjs';

function bundle(version) {
  const files=new Map(REQUIRED_CONTROL_FILES.map(name=>[name,Buffer.from('fixture only')]));
  files.set('APFDiscordBot/package.json',Buffer.from(JSON.stringify({dependencies:{}})));
  files.set('APFDiscordBot/package-lock.json',Buffer.from(JSON.stringify({lockfileVersion:3,packages:{'':{dependencies:{}}}})));
  const manifest={schemaVersion:1,transactionVersion:1,version,runtime:{nodeMajor:24,powerShellMajor:7,dotnetMajor:8},capabilities:CAPABILITIES,files:[...files].map(([name,bytes])=>({path:name,sha256:digest(bytes),sizeBytes:bytes.length}))};
  files.set('CONTROL.json',Buffer.from(JSON.stringify(manifest)));
  const bytes=Buffer.from(`authenticated fixture ${version}`);
  const descriptor={schemaVersion:1,transactionVersion:1,version,releaseTag:`host-${version}`,assetName:`Aetherfront-Control-${version}.zip`,sha256:digest(bytes),sizeBytes:bytes.length,minimumBootstrap:1};
  return {files,bytes,descriptor};
}
async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-staging-recovery-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const bundles=Object.fromEntries([7,8,9].map(n=>[`control-${n}`,bundle(`control-${n}`)]));
  let desired='control-7',extractFailure=null,prepares=0,handoffs=0;
  const calls=[];
  const channel={intent:async()=>({descriptor:bundles[desired].descriptor,descriptorSha256:digest(Buffer.from(JSON.stringify(bundles[desired].descriptor))),commit:'a'.repeat(40)}),archive:async descriptor=>bundles[descriptor.version].bytes};
  const settings={root,botRoot:path.join(root,'bot'),nativeRoot:path.join(root,'native'),node:process.execPath,npm:'fixture-npm',powershell:'fixture-powershell'};
  const options={channel,nativeIdle:async()=>true,quiesce:async()=>true,handoff:async()=>{handoffs++;},prepare:async stage=>{prepares++;await fs.mkdir(path.join(stage,'APFDiscordBot','node_modules'),{recursive:true});},execute:async(command,args,execution)=>{
    assert.equal(command,'fixture-powershell','No application or arbitrary payload command executes');
    assert.equal(execution.timeout,60000);
    const destination=args[args.indexOf('-Destination')+1],version=args[args.indexOf('-ExpectedVersion')+1];
    calls.push({destination,version,execution});
    await fs.mkdir(destination,{recursive:true});
    await fs.writeFile(path.join(destination,'partial.txt'),'preserve partial extraction');
    const error=extractFailure?.(calls.filter(call=>call.version===version).length,version);
    if(error)throw error;
    await fs.unlink(path.join(destination,'partial.txt'));
    for(const [name,bytes] of bundles[version].files) {
      const target=path.join(destination,...name.split('/'));await fs.mkdir(path.dirname(target),{recursive:true});await fs.writeFile(target,bytes);
    }
    return {stdout:'extracted',stderr:''};
  }};
  const engine=new ControlEngine(settings,options),intent=await channel.intent();
  const seed=await engine.stage({schemaVersion:1,transactionVersion:1,id:intent.descriptorSha256,descriptor:intent.descriptor,commit:intent.commit,phase:'accepted',from:null,to:null});
  await writeJson(path.join(root,'active.json'),seed);
  await engine.phase(await engine.state(),'healthy',{to:seed,from:seed});
  desired='control-8';calls.length=0;
  return {root,settings,options,engine,seed,calls,setDesired:value=>{desired=value;},fail:value=>{extractFailure=value;},counts:()=>({prepares,handoffs})};
}
async function diagnostics(engine,id) {
  const directory=path.join(engine.diagnosticsRoot,id);
  const names=(await fs.readdir(directory)).sort();
  return Promise.all(names.map(async name=>({name,...await readJson(path.join(directory,name))})));
}
const busy=()=>Object.assign(new Error('Extractor contention'),{code:75,stderr:'CONTROL_EXTRACT_IO_BUSY\r\n',stdout:'partial output'});

test('stageCode is optional, fixed allowlist only; diagnostics root precedence',async t=>{
  const f=await fixture(t),journal=await f.engine.state();
  const {stageCode,...legacy}=journal;assert.doesNotThrow(()=>validateTransaction(legacy));
  for(const code of ['download','archive-write','extract','dependencies','inventory','verify','activate','startup'])assert.doesNotThrow(()=>validateTransaction({...journal,stageCode:code}));
  for(const code of [null,'../outside','timeout','extract-command',32])assert.throws(()=>validateTransaction({...journal,stageCode:code}));
  assert.equal(f.engine.diagnosticsRoot,path.join(f.root,'diagnostics'));
  assert.equal(new ControlEngine({...f.settings,diagnosticsRoot:'settings-diagnostics'},f.options).diagnosticsRoot,'settings-diagnostics');
  assert.equal(new ControlEngine({...f.settings,diagnosticsRoot:'settings-diagnostics'},{...f.options,diagnosticsRoot:'option-diagnostics'}).diagnosticsRoot,'option-diagnostics');
});

test('injected extraction rejection preserves blocked 8, original exception, active bytes; normal 9 succeeds',async t=>{
  const f=await fixture(t),activeBytes=await fs.readFile(path.join(f.root,'active.json'));
  const injected=Object.assign(new Error('original injected extraction failure'),{code:1,stdout:'original stdout',stderr:'original stderr'});
  f.fail(()=>injected);
  assert.deepEqual(await f.engine.tick(),{status:'blocked',failure:'control-validation-failed'});
  assert.equal(injected.controlReason,'control-validation-failed');
  const blocked=await f.engine.state();assert.equal(blocked.descriptor.version,'control-8');assert.equal(blocked.to,null);assert.equal(blocked.stageCode,'extract');
  assert.equal(f.calls.length,1);assert.deepEqual(await fs.readFile(path.join(f.root,'active.json')),activeBytes);await verifyRelease(f.root,f.seed);
  const records=await diagnostics(f.engine,blocked.id),failure=records.find(row=>row.status==='failed');
  assert.equal(failure.message,injected.message);assert.equal(failure.code,1);assert.equal(failure.exitCode,1);assert.equal(failure.stderr,'original stderr');
  assert(Number.isFinite(failure.elapsedMs));assert(failure.startedAt);assert(failure.failedAt);
  assert(records.some(row=>row.stageCode==='download'&&row.status==='completed'));
  assert(records.some(row=>row.stageCode==='archive-write'&&row.status==='completed'));
  const historyFile=path.join(f.root,'transactions',`${blocked.id}.json`),history=await fs.readFile(historyFile);
  assert.equal((await f.engine.tick()).status,'ignored-older-or-completed');assert.equal(f.calls.length,1);
  f.setDesired('control-9');f.fail(null);
  assert.equal((await f.engine.tick()).status,'handoff');assert.equal((await f.engine.state()).descriptor.version,'control-9');
  assert.deepEqual(await fs.readFile(historyFile),history);assert.deepEqual(await diagnostics(f.engine,blocked.id),records);
  assert.deepEqual(await fs.readFile(path.join(f.root,'active.json')),activeBytes);assert.equal(f.counts().handoffs,1);
});

test('killed extraction timeout records signal/output, no retry, no timeout increase',async t=>{
  const f=await fixture(t);f.fail(()=>Object.assign(new Error('fixture timed out'),{code:'ETIMEDOUT',signal:'SIGTERM',killed:true,stderr:'CONTROL_EXTRACT_IO_BUSY'}));
  assert.equal((await f.engine.tick()).status,'blocked');assert.equal(f.calls.length,1);assert.equal(f.calls[0].execution.timeout,60000);
  const failure=(await diagnostics(f.engine,(await f.engine.state()).id)).find(row=>row.status==='failed');
  assert.equal(failure.killed,true);assert.equal(failure.signal,'SIGTERM');assert.equal(failure.code,'ETIMEDOUT');assert.equal(f.counts().prepares,1);
});

test('diagnostic output sanitized and bounded to 16 KiB per stream, first failure never overwritten',async t=>{
  const credentialUrl=new URL('https://host.invalid/file');credentialUrl.username='user';credentialUrl.password='password';
  const f=await fixture(t);f.fail(()=>Object.assign(new Error(`failed at ${f.root}`),{code:1,stdout:`token=private-value\nC:\\private\\file.txt\n${credentialUrl}\n${'界'.repeat(20000)}`,stderr:`/private/tree/secret.txt\n${'x'.repeat(40000)}`}));
  await f.engine.tick();const journal=await f.engine.state(),records=await diagnostics(f.engine,journal.id),failure=records.find(row=>row.status==='failed');
  assert(Buffer.byteLength(failure.stdout)<=16384);assert(Buffer.byteLength(failure.stderr)<=16384);
  for(const secret of ['private-value','C:\\private','user:password','/private/tree',f.root])assert(!JSON.stringify(failure).includes(secret));
  assert.equal(new Set(records.map(row=>row.name)).size,records.length);
  await new ControlEngine(f.settings,f.options).tick();assert.deepEqual(await diagnostics(f.engine,journal.id),records);
});

for(const code of ['IO_BUSY','EBUSY','EAGAIN'])test(`transient ${code} retries fresh extraction, preserves partial directories and failure`,async t=>{
  const f=await fixture(t);f.fail(attempt=>attempt<3?(code==='IO_BUSY'?busy():Object.assign(new Error('transport filesystem busy'),{code})):null);
  assert.equal((await f.engine.tick()).status,'handoff');assert.equal(f.calls.length,3);
  assert.equal(new Set(f.calls.map(call=>call.destination)).size,3);
  for(const call of f.calls.slice(0,2))assert.equal(await fs.readFile(path.join(call.destination,'partial.txt'),'utf8'),'preserve partial extraction');
  const journal=await f.engine.state(),records=await diagnostics(f.engine,journal.id),failures=records.filter(row=>row.status==='failed');
  assert.equal(failures.length,2);assert.deepEqual(failures.map(row=>row.attempt).sort(),[1,2]);
  assert.equal(records.filter(row=>row.stageCode==='extract'&&row.status==='completed').length,1);
  for(const stageCode of ['dependencies','inventory','verify','activate'])assert(records.some(row=>row.stageCode===stageCode&&row.status==='completed'));
  assert.equal(journal.stageCode,'activate');await verifyRelease(f.root,journal.to);
});

test('persistent extraction busy capped at 3, no subsequent tick retries same accepted 8',async t=>{
  const f=await fixture(t);f.fail(busy);
  assert.equal((await f.engine.tick()).status,'blocked');assert.equal(f.calls.length,3);
  const journal=await f.engine.state();assert.equal(journal.stageCode,'extract');assert.equal(journal.to,null);
  const failures=(await diagnostics(f.engine,journal.id)).filter(row=>row.status==='failed');assert.equal(failures.length,3);
  for(const call of f.calls)assert.equal(await fs.readFile(path.join(call.destination,'partial.txt'),'utf8'),'preserve partial extraction');
  await new ControlEngine(f.settings,f.options).tick();assert.equal(f.calls.length,3);assert.equal(f.counts().prepares,1);
});

for(const error of [Object.assign(new Error('validation'),{code:1,stderr:'CONTROL_EXTRACT_IO_BUSY'}),Object.assign(new Error('exit without marker'),{code:75}),Object.assign(new Error('access denied'),{code:'EACCES'}),Object.assign(new Error('false marker'),{code:75,stderr:'NOT_CONTROL_EXTRACT_IO_BUSY'})])test(`generic extraction failure not retried: ${error.message}`,async t=>{
  const f=await fixture(t);f.fail(()=>error);assert.equal((await f.engine.tick()).status,'blocked');assert.equal(f.calls.length,1);
});

test('dependency execution retains 180 s bound, no extraction replay on npm failure',async t=>{
  const f=await fixture(t),npm=[];
  const engine=new ControlEngine(f.settings,{...f.options,prepare:null,execute:async(command,args,execution)=>{
    if(command==='fixture-powershell')return f.options.execute(command,args,execution);
    npm.push({command,args,execution});throw Object.assign(new Error('npm failed'),{code:1,stderr:'dependency failure'});
  }});
  assert.equal((await engine.tick()).status,'blocked');assert.equal(f.calls.length,1);assert.equal(npm.length,1);
  assert.equal(npm[0].command,process.execPath);assert.equal(npm[0].execution.timeout,180000);
  assert.deepEqual(npm[0].args,['fixture-npm','ci','--ignore-scripts','--engine-strict','--no-audit','--no-fund','--registry=https://registry.npmjs.org']);
  assert.equal((await engine.state()).stageCode,'dependencies');
});
