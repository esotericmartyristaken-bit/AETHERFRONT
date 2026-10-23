import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { publishHostReport } from '../host-reporter.mjs';
import { CAPABILITIES } from '../contract.mjs';

const labels=['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'];
const base=Date.parse('2026-10-23T12:00:00.000Z');
const iso=value=>new Date(value).toISOString();
const write=(file,value)=>fs.writeFile(file,JSON.stringify(value));

async function fixture(t) {
  const temporary=path.resolve(os.tmpdir()),root=await fs.mkdtemp(path.join(temporary,'apf-coherent-report-'));
  t.after(async()=>{
    const relative=path.relative(temporary,root);
    assert.ok(relative&&!path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep));
    await fs.rm(root,{recursive:true,force:true});
  });
  let clock=base;
  t.mock.method(Date,'now',()=>clock);
  const state=path.join(root,'state'),nativeRoot=path.join(root,'native');
  const pointer={schemaVersion:1,version:'control-5',sequence:5,directory:'control-5-'+'a'.repeat(64),bundleSha256:'a'.repeat(64),manifestSha256:'b'.repeat(64),receiptSha256:'c'.repeat(64)};
  const brokerDirectory=path.join(state,'managed','releases',pointer.directory);
  const bridge=path.join(nativeRoot,'Server','APFDiscordBridge'),results=path.join(state,'native-cache','results');
  for(const directory of [bridge,results,path.join(brokerDirectory,'ControlPlane'),path.join(nativeRoot,'Updates'),path.join(nativeRoot,'Server','Config')])await fs.mkdir(directory,{recursive:true});
  await fs.writeFile(path.join(brokerDirectory,'ControlPlane','native-agent.mjs'),'export const fixture = true;');
  await fs.writeFile(path.join(brokerDirectory,'ControlPlane','Invoke-NativePublisher.ps1'),'# fixture');
  // Reject the lease path before launching any process. No live host is touched.
  const lease=path.join(nativeRoot,'.publish.lock');
  await fs.writeFile(lease,'fixture');
  await fs.link(lease,path.join(root,'lease-link'));
  await write(path.join(nativeRoot,'Updates','APFClientUpdate.json'),{version:'kesas-rc1'});
  await write(path.join(nativeRoot,'Server','Config','APFPack.json'),{packVersion:'kesas-rc1',requiredClientVersion:'kesas-rc1'});
  const ready=path.join(bridge,'ready.json'),live=path.join(bridge,'status.json');
  const status=path.join(state,'worker-status.json'),diagnostic=path.join(state,'worker-diagnostics.json');
  const identity={schemaVersion:1,observedAt:iso(base),controlVersion:pointer.version,controlSha256:pointer.bundleSha256};
  const worker={...identity,expiresAt:iso(base+30000),ready:true,health:'healthy',applicationVersion:'kesas-rc1',inventory:{status:'complete',version:'kesas-rc1',observedAt:iso(base)},components:Object.fromEntries(labels.map(label=>[label,'d'.repeat(64)]))};
  const detail={...identity,stage:'running',exitCode:null};
  const bridgeStatus={serverPid:42,updatedAt:iso(base),playerCount:0,connectedCount:0,players:[]};
  await write(ready,{pid:42,startedAt:iso(base-60000)});
  await fs.writeFile(live,'\ufeff'+JSON.stringify(bridgeStatus));
  await write(status,worker);
  await write(diagnostic,detail);
  const settings={root:state,nativeRoot,powershell:'test-must-not-launch',runtime:{node:process.versions.node,powershell:'7.0.0',dotnet:'8.0.0'}};
  const options={pointer,brokerDirectory,brokerReady:true,health:'healthy'};
  let sequence=0;
  const replace=async(file,value)=>{
    const incoming=file+'.fixture-'+(++sequence);
    await write(incoming,value);
    await fs.rename(incoming,file);
  };
  return {root,state,ready,live,status,diagnostic,worker,detail,bridgeStatus,results,settings,options,replace,
    time:value=>{clock=value;},publish:()=>publishHostReport(settings,options)};
}

function healthy(result) {
  assert.equal(result.report.application.health,'healthy');
  assert.equal(result.diagnostics.host.worker,'running');
  assert.equal(result.diagnostics.host.inventory,'complete');
  assert.equal(result.diagnostics.schemaVersion,2);
  assert.deepEqual(result.report.capabilities,CAPABILITIES.filter(name=>name!=='unattended-host-v1'));
}

test('bridge and worker samples written after report start are not future observations',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  let replaced=false;
  t.mock.method(fs,'open',async(file,...args)=>{
    if(file===f.live&&!replaced) {
      replaced=true;f.time(base+1);
      await f.replace(f.live,{...f.bridgeStatus,updatedAt:iso(base+1)});
      await f.replace(f.status,{...f.worker,observedAt:iso(base+1),expiresAt:iso(base+30001)});
      await f.replace(f.diagnostic,{...f.detail,observedAt:iso(base+1)});
    }
    return open(file,...args);
  });
  const result=await f.publish();
  healthy(result);
  assert.equal(result.report.observedAt,iso(base+1));
});

for(const name of ['ready','live'])test(`normal atomic ${name} replacement during snapshot retries coherently`,async t=>{
  const f=await fixture(t),lstat=fs.lstat.bind(fs);
  let replacements=0;
  t.mock.method(fs,'lstat',async(file,options)=>{
    if(file===f[name]&&options?.bigint&&replacements++===0)await f.replace(file,name==='ready'?{pid:42,startedAt:iso(base-60000)}:f.bridgeStatus);
    return lstat(file,options);
  });
  healthy(await f.publish());
  assert.ok(replacements>=2);
});

test('independently atomic worker files retry a mixed observation generation',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  f.time(base+1);
  await f.replace(f.status,{...f.worker,observedAt:iso(base+1),expiresAt:iso(base+30001)});
  let opens=0;
  t.mock.method(fs,'open',async(file,...args)=>{
    if(file===f.diagnostic&&++opens===2)await f.replace(file,{...f.detail,observedAt:iso(base+1)});
    return open(file,...args);
  });
  healthy(await f.publish());
  assert.equal(opens,2);
});

test('persistent worker generation mismatch fails closed after bounded retries',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  f.time(base+1);
  await f.replace(f.status,{...f.worker,observedAt:iso(base+1),expiresAt:iso(base+30001)});
  let opens=0;
  t.mock.method(fs,'open',async(file,...args)=>{if(file===f.status)opens++;return open(file,...args);});
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,'worker-invalid');
  assert.equal(opens,3);
});

test('slow native scan precedes short-lived samples and does not mint freshness',async t=>{
  const f=await fixture(t),opendir=fs.opendir.bind(fs),open=fs.open.bind(fs);
  let scanned=false;
  t.mock.method(fs,'opendir',async(directory,...args)=>{
    if(directory===f.results) {
      f.time(base+40000);
      await f.replace(f.live,{...f.bridgeStatus,updatedAt:iso(base+40000)});
      await f.replace(f.status,{...f.worker,observedAt:iso(base+40000),expiresAt:iso(base+70000)});
      await f.replace(f.diagnostic,{...f.detail,observedAt:iso(base+40000)});
      scanned=true;
    }
    return opendir(directory,...args);
  });
  t.mock.method(fs,'open',async(file,...args)=>{
    if([f.live,f.status,f.diagnostic].includes(file))assert.equal(scanned,true);
    return open(file,...args);
  });
  const result=await f.publish();
  healthy(result);
  assert.equal(result.report.observedAt,iso(base+40000));
});

test('worker evidence that expires while its bridge peer is read remains stale',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  let delayed=false;
  t.mock.method(fs,'open',async(file,...args)=>{
    if(file===f.live&&!delayed) {
      delayed=true;
      f.time(base+30001);
      await f.replace(f.live,{...f.bridgeStatus,updatedAt:iso(base+30001)});
    }
    return open(file,...args);
  });
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,'worker-stale');
  assert.equal(result.report.observedAt,iso(base+30001));
});

for(const [name,age] of [['bridge',10001],['worker',30001]])test(`real stale ${name} evidence remains rejected`,async t=>{
  const f=await fixture(t);
  if(name==='bridge')await f.replace(f.live,{...f.bridgeStatus,updatedAt:iso(base-age)});
  else {
    await f.replace(f.status,{...f.worker,observedAt:iso(base-age),expiresAt:iso(base-1)});
    await f.replace(f.diagnostic,{...f.detail,observedAt:iso(base-age)});
  }
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,name==='bridge'?'bridge-unavailable':'worker-stale');
  assert.ok(result.report.capabilities.length<CAPABILITIES.length-1);
});

for(const name of ['bridge','worker'])test(`genuinely future ${name} sample remains rejected`,async t=>{
  const f=await fixture(t);
  if(name==='bridge')await f.replace(f.live,{...f.bridgeStatus,updatedAt:iso(base+1)});
  else {
    await f.replace(f.status,{...f.worker,observedAt:iso(base+1),expiresAt:iso(base+30001)});
    await f.replace(f.diagnostic,{...f.detail,observedAt:iso(base+1)});
  }
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,name==='bridge'?'bridge-unavailable':'worker-stale');
});

test('forged worker identity cannot grant capabilities',async t=>{
  const f=await fixture(t);
  await f.replace(f.status,{...f.worker,controlSha256:'e'.repeat(64)});
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,'worker-mismatch');
  assert.ok(result.report.capabilities.length<CAPABILITIES.length-1);
});

test('continuous bridge replacement fails closed after three attempts',async t=>{
  const f=await fixture(t),lstat=fs.lstat.bind(fs);
  let replacements=0;
  t.mock.method(fs,'lstat',async(file,options)=>{
    if(file===f.live&&options?.bigint){replacements++;await f.replace(file,f.bridgeStatus);}
    return lstat(file,options);
  });
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,'bridge-unavailable');
  assert.equal(replacements,3);
});

test('hardlinked bridge data is not retried or admitted',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  await fs.link(f.live,path.join(f.root,'bridge-link'));
  let opens=0;
  t.mock.method(fs,'open',async(file,...args)=>{if(file===f.live)opens++;return open(file,...args);});
  assert.equal((await f.publish()).diagnostics.host.reason,'bridge-unavailable');
  assert.equal(opens,0);
});

test('absent bridge status with replacement remnants is unavailable, not fresh',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  await fs.unlink(f.live);
  await write(f.live+'.tmp',f.bridgeStatus);
  await write(path.join(path.dirname(f.live),'~RF123.TMP'),f.bridgeStatus);
  let opens=0;
  t.mock.method(fs,'open',async(file,...args)=>{if(file===f.live)opens++;return open(file,...args);});
  const result=await f.publish();
  assert.equal(result.diagnostics.host.reason,'bridge-unavailable');
  assert.equal(result.report.application.health,'unknown');
  assert.equal(opens,3);
});

test('concurrent publication shares one report and independently atomic output files',async t=>{
  const f=await fixture(t),open=fs.open.bind(fs);
  let publications=0;
  t.mock.method(fs,'open',async(file,...args)=>{
    if(String(file).startsWith(path.join(f.state,'host-status.json.'))&&args[0]==='wx')publications++;
    return open(file,...args);
  });
  const [left,right]=await Promise.all([f.publish(),f.publish()]);
  assert.strictEqual(left,right);
  healthy(left);
  assert.equal(publications,1);
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(f.state,'host-status.json'),'utf8')),left.report);
  for(const name of ['kesas-host-status.zip','kesas-host-diagnostics.zip']) {
    const bytes=await fs.readFile(path.join(f.settings.nativeRoot,'Server','Config',name));
    assert.equal(bytes.readUInt32LE(0),0x04034b50);
    const length=bytes.readUInt32LE(18),offset=30+bytes.readUInt16LE(26);
    const value=JSON.parse(bytes.subarray(offset,offset+length));
    assert.equal(value.observedAt,left.report.observedAt);
  }
});

for(const code of ['EBUSY','EPERM'])test(`HTTP ZIP ${code} cannot suppress coherent independent observation or mark a live broker failed`,async t=>{
  const f=await fixture(t),rename=fs.rename.bind(fs);
  let failures=0;
  t.mock.method(fs,'rename',async(from,to)=>{
    if(['kesas-host-status.zip','kesas-host-diagnostics.zip'].some(name=>to===path.join(f.settings.nativeRoot,'Server','Config',name))){
      failures++;throw Object.assign(new Error('isolated HTTP ZIP sharing failure'),{code});
    }
    return rename(from,to);
  });
  const first=await f.publish();healthy(first);
  // The existing atomic writer tries each destination at most nine times.
  assert.equal(failures,18);
  const observation=JSON.parse(await fs.readFile(path.join(f.state,'host-observation.json'),'utf8'));
  assert.deepEqual(observation.status,first.report);
  assert.deepEqual(observation.diagnostics,first.diagnostics);
  assert.notEqual(observation.diagnostics.host.broker,'failed');
  f.time(base+1);
  const second=await f.publish();healthy(second);
  assert.equal(second.report.observedAt,iso(base+1));assert.equal(failures,36);
  assert.equal(JSON.parse(await fs.readFile(path.join(f.state,'host-observation.json'),'utf8')).status.observedAt,iso(base+1));
});
