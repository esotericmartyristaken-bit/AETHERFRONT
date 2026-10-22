import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { bootstrapEvidence, publishHostReport } from '../host-reporter.mjs';

const maximum=1024*1024;
const sources={
  'native-agent.mjs':Buffer.from('// Native agent fixture: \u03bb\r\nexport const recoveryVersion = 1;\r\n'),
  'Invoke-NativePublisher.ps1':Buffer.from('\ufeff# Native publisher fixture\r\nparam([string]$Version)\r\nWrite-Output $Version\r\n')
};
const fields={'native-agent.mjs':'nativeAgentSha256','Invoke-NativePublisher.ps1':'publisherWrapperSha256'};
const sha256=bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const absent={nativeAgentSha256:null,publisherWrapperSha256:null,nativeRecoveryVersion:0};

async function fixture(t) {
  const temporary=path.resolve(os.tmpdir());
  const root=await fs.mkdtemp(path.join(temporary,'apf-host-reporter-hash-'));
  t.after(async()=>{
    const relative=path.relative(temporary,root);
    assert.ok(relative&&!path.isAbsolute(relative)&&relative!=='..'&&!relative.startsWith('..'+path.sep));
    await fs.rm(root,{recursive:true,force:true});
  });
  await fs.mkdir(path.join(root,'ControlPlane'));
  const file=name=>path.join(root,'ControlPlane',name);
  await Promise.all(Object.entries(sources).map(([name,bytes])=>fs.writeFile(file(name),bytes)));
  return {root,file};
}

test('non-JSON JavaScript and PowerShell publish exact raw SHA-256 evidence',async t=>{
  const f=await fixture(t);
  for(const bytes of Object.values(sources))assert.throws(()=>JSON.parse(bytes.toString('utf8')));
  assert.deepEqual(await bootstrapEvidence(f.root,true),{
    nativeAgentSha256:sha256(sources['native-agent.mjs']),
    publisherWrapperSha256:sha256(sources['Invoke-NativePublisher.ps1']),
    nativeRecoveryVersion:1
  });
  const unready=await bootstrapEvidence(f.root,false);
  assert.equal(unready.nativeRecoveryVersion,0);
  for(const [name,field] of Object.entries(fields))assert.equal(unready[field],sha256(sources[name]));
});

for(const [name,field] of Object.entries(fields)) {
  test(`missing ${name} fails closed without suppressing other code evidence`,async t=>{
    const f=await fixture(t);
    await fs.unlink(f.file(name));
    const result=await bootstrapEvidence(f.root,true);
    assert.equal(result[field],null);
    assert.equal(result.nativeRecoveryVersion,0);
    const other=Object.keys(fields).find(value=>value!==name);
    assert.equal(result[fields[other]],sha256(sources[other]));
  });

  test(`oversized ${name} is rejected before reading bytes`,async t=>{
    const f=await fixture(t),file=f.file(name);
    await fs.writeFile(file,Buffer.alloc(maximum+1,0x61));
    const open=fs.open.bind(fs);
    let reads=0;
    t.mock.method(fs,'open',async(target,...args)=>{
      const handle=await open(target,...args);
      if(target===file)t.mock.method(handle,'read',async()=>{reads++;throw new Error('Oversized code must not be read');});
      return handle;
    });
    const result=await bootstrapEvidence(f.root,true);
    assert.equal(result[field],null);
    assert.equal(result.nativeRecoveryVersion,0);
    assert.equal(reads,0);
  });
}

test('both code files accept exactly the 1 MiB limit',async t=>{
  const f=await fixture(t),bytes=Buffer.alloc(maximum,0x61);
  await Promise.all(Object.keys(sources).map(name=>fs.writeFile(f.file(name),bytes)));
  assert.deepEqual(await bootstrapEvidence(f.root,true),{
    nativeAgentSha256:sha256(bytes),publisherWrapperSha256:sha256(bytes),nativeRecoveryVersion:1
  });
});

test('empty code cannot advertise native recovery',async t=>{
  const f=await fixture(t);
  await fs.writeFile(f.file('native-agent.mjs'),'');
  const result=await bootstrapEvidence(f.root,true);
  assert.equal(result.nativeAgentSha256,null);
  assert.equal(result.nativeRecoveryVersion,0);
});

test('absent broker directory never falls back to another code or private path',async t=>{
  const open=t.mock.method(fs,'open',async()=>{throw new Error('Unexpected file access');});
  assert.deepEqual(await bootstrapEvidence(null,true),absent);
  assert.equal(open.mock.callCount(),0);
});

test('hardlinked code is rejected before opening it',async t=>{
  const f=await fixture(t),file=f.file('native-agent.mjs');
  await fs.link(file,path.join(f.root,'agent-link'));
  const open=fs.open.bind(fs);
  let opens=0;
  t.mock.method(fs,'open',async(target,...args)=>{
    if(target===file)opens++;
    return open(target,...args);
  });
  const result=await bootstrapEvidence(f.root,true);
  assert.equal(result.nativeAgentSha256,null);
  assert.equal(result.nativeRecoveryVersion,0);
  assert.equal(opens,0);
});

test('link substitution observed after reading fails closed',async t=>{
  const f=await fixture(t),file=f.file('native-agent.mjs'),lstat=fs.lstat.bind(fs);
  let checks=0;
  t.mock.method(fs,'lstat',async(target,...args)=>{
    const stat=await lstat(target,...args);
    if(target===file&&++checks===2)stat.isSymbolicLink=()=>true;
    return stat;
  });
  const result=await bootstrapEvidence(f.root,true);
  assert.equal(result.nativeAgentSha256,null);
  assert.equal(result.nativeRecoveryVersion,0);
  assert.equal(checks,2);
});

for(const timestamp of ['mtimeNs','ctimeNs']) {
  test(`same-size code race with a 1 ns ${timestamp} change fails closed`,async t=>{
    const f=await fixture(t),file=f.file('native-agent.mjs'),open=fs.open.bind(fs);
    let stats=0;
    t.mock.method(fs,'open',async(target,...args)=>{
      const handle=await open(target,...args);
      if(target===file) {
        const stat=handle.stat.bind(handle);
        t.mock.method(handle,'stat',async(...options)=>{
          const value=await stat(...options);
          if(++stats===2)value[timestamp]+=1n;
          return value;
        });
      }
      return handle;
    });
    const result=await bootstrapEvidence(f.root,true);
    assert.equal(result.nativeAgentSha256,null);
    assert.equal(result.nativeRecoveryVersion,0);
    assert.equal(stats,2);
  });
}

test('replacement of the named path cannot reuse an unchanged open-handle hash',async t=>{
  const f=await fixture(t),file=f.file('native-agent.mjs'),lstat=fs.lstat.bind(fs);
  let namedChecks=0;
  t.mock.method(fs,'lstat',async(target,options)=>{
    const stat=await lstat(target,options);
    if(target===file&&options?.bigint){stat.ino+=1n;namedChecks++;}
    return stat;
  });
  const result=await bootstrapEvidence(f.root,true);
  assert.equal(result.nativeAgentSha256,null);
  assert.equal(result.nativeRecoveryVersion,0);
  assert.equal(namedChecks,1);
});

test('premature EOF cannot hash fewer bytes than stable stat reports',async t=>{
  const f=await fixture(t),file=f.file('native-agent.mjs'),open=fs.open.bind(fs);
  t.mock.method(fs,'open',async(target,...args)=>{
    const handle=await open(target,...args);
    if(target===file)t.mock.method(handle,'read',async()=>({bytesRead:0}));
    return handle;
  });
  const result=await bootstrapEvidence(f.root,true);
  assert.equal(result.nativeAgentSha256,null);
  assert.equal(result.nativeRecoveryVersion,0);
});

test('missing broker code publishes a fixed reason and withholds full capabilities locally',async t=>{
  const f=await fixture(t),root=path.join(f.root,'state'),nativeRoot=path.join(f.root,'native');
  const bridge=path.join(nativeRoot,'Server','APFDiscordBridge');
  for(const directory of [root,bridge,path.join(nativeRoot,'Updates'),path.join(nativeRoot,'Server','Config')])await fs.mkdir(directory,{recursive:true});
  // A local hardlink makes the lease probe fail closed before any child launch.
  const lease=path.join(nativeRoot,'.publish.lock');
  await fs.writeFile(lease,'test-only');
  await fs.link(lease,path.join(f.root,'lease-link'));
  const now=new Date().toISOString(),write=(file,value)=>fs.writeFile(file,JSON.stringify(value));
  await write(path.join(nativeRoot,'Updates','APFClientUpdate.json'),{version:'kesas-rc1'});
  await write(path.join(nativeRoot,'Server','Config','APFPack.json'),{packVersion:'kesas-rc1',requiredClientVersion:'kesas-rc1'});
  await write(path.join(bridge,'ready.json'),{pid:1,startedAt:now});
  await fs.writeFile(path.join(bridge,'status.json'),'\ufeff'+JSON.stringify({serverPid:1,updatedAt:now,playerCount:0,connectedCount:0,players:[]}));
  const pointer={schemaVersion:1,version:'control-5',sequence:5,directory:'control-5-'+'a'.repeat(64),
    bundleSha256:'a'.repeat(64),manifestSha256:'b'.repeat(64),receiptSha256:'c'.repeat(64)};
  const settings={root,nativeRoot,powershell:'test-must-not-launch',runtime:{node:process.versions.node,powershell:'7.0.0',dotnet:'8.0.0'}};
  const {report,diagnostics}=await publishHostReport(settings,{pointer,brokerReady:true,health:'healthy',
    brokerDirectory:path.join(root,'managed','releases',pointer.directory)});
  assert.deepEqual(diagnostics.bootstrap,absent);
  assert.equal(diagnostics.host.stage,'broker');
  assert.equal(diagnostics.host.reason,'broker-not-ready');
  assert.equal(diagnostics.host.leaseActive,null);
  assert.equal(report.application.health,'healthy');
  assert.deepEqual(report.capabilities,['control-plane-v1','host-report-v1','versioned-broker-v1','independent-report-v1']);
});
