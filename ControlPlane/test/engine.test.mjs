import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { EventEmitter } from 'node:events';
import { ControlEngine, verifyRelease, validateDependencyLock } from '../engine.mjs';
import { Supervisor } from '../bootstrap.mjs';
import { GitHubChannel } from '../github.mjs';
import { writeJson, readJson, digest } from '../public-status.mjs';
import { validateControlDescriptor, validateHostReport, CAPABILITIES } from '../contract.mjs';
import { publishControl } from '../controller.mjs';
import { repositoryFixture, reportFixture } from './publication-fixture.mjs';
import { statusZip } from '../public-status.mjs';
import { pathToFileURL } from 'node:url';

const execute=promisify(execFile),repo=path.resolve(import.meta.dirname,'../..');
const powershell=process.env.APF_TEST_POWERSHELL||'pwsh.exe';
let prepared;
async function bundles() {
  if(!prepared)prepared=(async()=>{
    const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-control-bundles-'));
    const rows={};
    for(const version of ['control-1','control-2','control-5']) {
      const source=path.join(root,version);
      for(const relative of ['APFDiscordBot/src','APFDiscordBot/scripts','KESAS-VPS-Server']) {
        await fs.cp(path.join(repo,relative),path.join(source,relative),{recursive:true});
      }
      await fs.mkdir(path.join(source,'ControlPlane'),{recursive:true});
      for(const name of ['worker','engine','contract','github','public-status','native-agent','broker-host','host-reporter','control-evidence','signed-readback','observation-publisher','supervision'])await fs.copyFile(path.join(repo,'ControlPlane',`${name}.mjs`),path.join(source,'ControlPlane',`${name}.mjs`));
      for(const name of ['Read-ClientInventory.ps1','Invoke-NativePublisher.ps1'])await fs.copyFile(path.join(repo,'ControlPlane',name),path.join(source,'ControlPlane',name));
      for(const name of ['package.json','package-lock.json'])await fs.copyFile(path.join(repo,'APFDiscordBot',name),path.join(source,'APFDiscordBot',name));
      if(version==='control-1') {
        for(const relative of ['KESAS-VPS-Server/Publish-Release.ps1','APFDiscordBot/scripts/Verify-KesasRelease.ps1','APFDiscordBot/src/github-deployment.js']) {
          const {stdout}=await execute('git',['show',`7b28642:${relative}`],{cwd:repo,maxBuffer:1024*1024});await fs.writeFile(path.join(source,relative),stdout);
        }
      }
      if(version==='control-5')await fs.appendFile(path.join(source,'ControlPlane','engine.mjs'),'\nexport const fixtureInstallerRevision=5;\n');
      const archive=path.join(root,`Aetherfront-Control-${version}.zip`);
      const {stdout}=await execute(powershell,['-NoProfile','-NonInteractive','-File',path.join(repo,'ControlPlane','Build-ControlBundle.ps1'),'-SourceRoot',source,'-Version',version,'-Output',archive],{windowsHide:true,maxBuffer:65536,timeout:60000});
      rows[version]={descriptor:JSON.parse(stdout),bytes:await fs.readFile(archive)};
    }
    return {root,rows};
  })();
  return prepared;
}
test.after(async()=>{if(prepared){const bundle=await prepared;await fs.rm(bundle.root,{recursive:true,force:true});}});
async function fixture(t) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-control-engine-'));
  t.after(()=>fs.rm(root,{recursive:true,force:true}));
  const settings={root,botRoot:path.join(root,'bot'),nativeRoot:path.join(root,'native'),node:process.execPath,powershell,npm:'unused-test-only',runtime:{node:process.versions.node,powershell:'7.6.5',dotnet:'8.0.30'}};
  await fs.mkdir(path.join(root,'bootstrap'),{recursive:true});
  await fs.copyFile(path.join(repo,'ControlPlane','Expand-ControlBundle.ps1'),path.join(root,'bootstrap','Expand-ControlBundle.ps1'));
  await fs.mkdir(path.join(settings.botRoot,'state'),{recursive:true});
  await writeJson(path.join(settings.botRoot,'state','kesas-update-state.json'),{schemaVersion:1,status:'idle'});
  const bundle=(await bundles()).rows;let desired='control-1',idle=true,handoffs=0,quiescences=0,prepares=0;
  const channel={intent:async()=>({descriptor:bundle[desired].descriptor,commit:'a'.repeat(40),descriptorSha256:digest(Buffer.from(JSON.stringify(bundle[desired].descriptor)))}),archive:async descriptor=>bundle[descriptor.version].bytes};
  const options={channel,nativeIdle:async()=>idle,quiesce:async()=>{quiescences++;return true;},handoff:async()=>{handoffs++;},prepare:async stage=>{prepares++;await fs.mkdir(path.join(stage,'APFDiscordBot','node_modules'),{recursive:true});}};
  const engine=new ControlEngine(settings,options);
  const intent=await channel.intent();const seed=await engine.stage({descriptor:intent.descriptor,commit:intent.commit,schemaVersion:1,transactionVersion:1,id:intent.descriptorSha256,phase:'accepted',from:null,to:null});
  await writeJson(path.join(root,'active.json'),seed);
  const seedJournal=await engine.state();await engine.phase(seedJournal,'healthy',{to:seed,from:seed});
  return {root,settings,bundle,engine,options,seed,channel,setDesired:value=>{desired=value;},setIdle:value=>{idle=value;},counts:()=>({handoffs,quiescences,prepares})};
}
async function activate(f) {
  const supervisor=new Supervisor(f.settings,{idle:async()=>true,healthMs:1,startTimeout:50,launch:pointer=>{
    const child=new EventEmitter();child.send=()=>{};child.kill=()=>child.emit('exit',1);
    setTimeout(()=>child.emit('message',{type:'ready',version:pointer.version,observedAt:Date.now()}),1);
    setTimeout(()=>child.emit('message',{type:'heartbeat',version:pointer.version,observedAt:Date.now()}),20);
    setTimeout(()=>child.emit('exit',0),400);
    return child;
  }});
  await supervisor.once();return supervisor;
}
test('contract rejects unknown fields, schemas, unsupported runtimes and unpinned dependencies',()=>{
  const base={schemaVersion:1,transactionVersion:1,version:'control-2',releaseTag:'host-control-2',assetName:'Aetherfront-Control-control-2.zip',sha256:'a'.repeat(64),sizeBytes:10,minimumBootstrap:1};
  assert.equal(validateControlDescriptor(base),base);
  for(const mutation of [{schemaVersion:2},{transactionVersion:2},{minimumBootstrap:2},{url:'https://private.invalid'},{sizeBytes:'10'},{version:'../outside'}])assert.throws(()=>validateControlDescriptor({...base,...mutation}));
  const pkg={dependencies:{safe:'1.0.0'}},lock={lockfileVersion:3,packages:{'':{dependencies:{safe:'1.0.0'}},'node_modules/safe':{version:'1.0.0',resolved:'https://registry.npmjs.org/safe/-/safe-1.0.0.tgz',integrity:'sha512-YQ=='}}};
  assert.doesNotThrow(()=>validateDependencyLock(pkg,lock));
  assert.throws(()=>validateDependencyLock({dependencies:{safe:'^1.0.0'}},lock));
  assert.throws(()=>validateDependencyLock(pkg,{...lock,packages:{...lock.packages,'node_modules/safe':{...lock.packages['node_modules/safe'],resolved:'https://private.invalid/package'}}}));
});
test('versioned upgrade, self-installer second update, skipped versions and duplicate submission', {timeout:120000},async t=>{
  const f=await fixture(t);const before=await fs.readFile(path.join(f.root,'releases',f.seed.directory,'ControlPlane','engine.mjs'));
  f.setDesired('control-2');assert.equal((await f.engine.tick()).status,'handoff');
  assert.deepEqual(await fs.readFile(path.join(f.root,'releases',f.seed.directory,'ControlPlane','engine.mjs')),before,'running installer code is untouched');
  await activate(f);assert.equal((await f.engine.active()).version,'control-2');
  await t.test('activated publisher deploys a signed application with new public media',{skip:!process.env.APF_TEST_UPDATE_TOOL&&'Requires privately provisioned APF_TEST_UPDATE_TOOL and adjacent public ClientTemplate.zip'},async()=>{
    const {stdout}=await execute(powershell,['-NoProfile','-NonInteractive','-File',path.join(repo,'ControlPlane','test','Invoke-NativeMediaFixture.ps1'),'-ControlReleaseRoot',path.join(f.root,'releases',(await f.engine.active()).directory),'-TestRoot',path.join(f.root,'native-publication-fixture'),'-UpdateToolRoot',process.env.APF_TEST_UPDATE_TOOL],{windowsHide:true,maxBuffer:1024*1024,timeout:90000});
    assert.match(stdout,/PASS real native publisher/);
  });
  assert.equal((await f.engine.tick()).status,'current');assert.equal(f.counts().handoffs,1);
  // A new engine is imported from the newly activated immutable directory.
  const nextModule=await import(new URL(`file:///${path.join(f.root,'releases',(await f.engine.active()).directory,'ControlPlane','engine.mjs').replaceAll('\\','/')}`));
  const second=new nextModule.ControlEngine(f.settings,f.options);f.setDesired('control-5');
  assert.equal((await second.tick()).status,'handoff');await activate(f);
  assert.equal((await second.active()).version,'control-5');assert.equal(f.counts().handoffs,2);
  assert.equal((await second.tick()).status,'current');
  assert.equal((await readJson(path.join(f.root,'fallback.json'))).version,'control-2');
  const fifth=await import(pathToFileURL(path.join(f.root,'releases',(await second.active()).directory,'ControlPlane','engine.mjs')));
  assert.equal(fifth.fixtureInstallerRevision,5);
  assert.equal((await new fifth.ControlEngine(f.settings,f.options).tick()).status,'current');
});
test('existing native guard serializes activation; competing intent cannot replace accepted transaction', {timeout:120000},async t=>{
  const f=await fixture(t);f.setIdle(false);f.setDesired('control-2');
  assert.equal((await f.engine.tick()).status,'waiting-existing-guard');
  assert.equal((await f.engine.active()).version,'control-1');assert.equal(f.counts().quiescences,0);
  f.setDesired('control-5');f.setIdle(true);assert.equal((await f.engine.tick()).status,'handoff');
  assert.equal((await f.engine.state()).descriptor.version,'control-2');await activate(f);
  assert.equal((await f.engine.tick()).status,'handoff');assert.equal((await f.engine.state()).descriptor.version,'control-5');
});
for(const point of ['staging','activation'])test(`process death during ${point} recovers from durable journal without deleting it`, {timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');let interrupted=false;
  const crashing=new ControlEngine(f.settings,{...f.options,fault:async phase=>{if(phase===point&&!interrupted){interrupted=true;throw Object.assign(new Error('fixture process death'),{simulatedCrash:true});}}});
  await assert.rejects(crashing.tick());const id=(await crashing.state()).id;
  if(point==='staging') {const resumed=new ControlEngine(f.settings,f.options);assert.equal((await resumed.tick()).status,'handoff');}
  await activate(f);
  assert.equal((await f.engine.active()).version,'control-2');assert.equal((await readJson(path.join(f.root,'transactions',`${id}.json`))).phase,'healthy');
  const reboot=new ControlEngine(f.settings,f.options);assert.equal((await reboot.tick()).status,'current');
});
test('failed replacement health rolls back automatically; same release never reactivates', {timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');await f.engine.tick();
  const supervisor=new Supervisor(f.settings,{idle:async()=>true,startTimeout:5,healthMs:1,launch:()=>{const child=new EventEmitter();child.send=()=>{};child.kill=()=>child.emit('exit',1);setTimeout(()=>child.emit('exit',1),10);return child;}});
  assert.equal(await supervisor.once(),'rolled-back');assert.equal((await f.engine.active()).version,'control-1');
  assert.equal((await f.engine.state()).phase,'rolled-back');assert.equal((await f.engine.tick()).status,'ignored-older-or-completed');
});
test('bad archive authentication/hash leaves old bytes, settings, Universe, identities and unrelated process unchanged', {timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');
  const sentinels=['native/Server/Universe/save.txt','native/Server/Config/operator.xml','native/secrets/identity.xml','native/secrets/signing-private.xml','vanilla/process.txt'];
  for(const name of sentinels){const file=path.join(f.root,name);await fs.mkdir(path.dirname(file),{recursive:true});await fs.writeFile(file,'preserved-fixture');}
  const hashes=await Promise.all(sentinels.map(name=>fs.readFile(path.join(f.root,name))));
  const bad=new ControlEngine(f.settings,{...f.options,channel:{...f.channel,archive:async()=>Buffer.from('unauthorized content')}});
  assert.equal((await bad.tick()).status,'blocked');assert.equal((await bad.active()).version,'control-1');assert.equal(f.counts().handoffs,0);
  for(let i=0;i<sentinels.length;i++)assert.deepEqual(await fs.readFile(path.join(f.root,sentinels[i])),hashes[i]);
  await verifyRelease(f.root,f.seed);
});
test('GitHub authority is fixed repository, pinned commit and no credential redirects',async()=>{
  const requests=[];const descriptor={schemaVersion:1,transactionVersion:1,version:'control-2',releaseTag:'host-control-2',assetName:'Aetherfront-Control-control-2.zip',sha256:'a'.repeat(64),sizeBytes:10,minimumBootstrap:1};
  const channel=new GitHubChannel(async(url,options)=>{requests.push(url);assert.equal(options.headers.Authorization,undefined);if(url.includes('/git/ref/'))return Response.json({ref:'refs/heads/deploy',object:{type:'commit',sha:'b'.repeat(40)}});return Response.json(descriptor);});
  assert.equal((await channel.intent()).commit,'b'.repeat(40));assert(requests.some(url=>url.includes(`/${'b'.repeat(40)}/deployment/control-plane.json`)));
  const malicious=new GitHubChannel(async()=>new Response(null,{status:302,headers:{location:'http://private.invalid/config'}}));await assert.rejects(malicious.intent());
});
test('crash between accepted history and current journal resumes the same transaction',{timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');let crashed=false;
  const engine=new ControlEngine(f.settings,{...f.options,fault:async(point,value)=>{if(point==='history-written'&&value.phase==='accepted'&&!crashed){crashed=true;throw Object.assign(new Error('fixture crash'),{simulatedCrash:true});}}});
  await assert.rejects(engine.tick());assert.equal((await f.engine.active()).version,'control-1');
  assert.equal((await new ControlEngine(f.settings,f.options).tick()).status,'handoff');await activate(f);
  assert.equal((await f.engine.active()).version,'control-2');assert.equal(f.counts().handoffs,1);
});
test('crash after rollback pointer cannot reactivate the rejected candidate',{timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');await f.engine.tick();
  const supervisor=new Supervisor(f.settings,{idle:async()=>true,fault:async point=>{if(point==='rollback-pointer-written')throw new Error('fixture reboot during rollback');}});
  const transaction=await supervisor.recoverActivation();await assert.rejects(supervisor.rollback(transaction,'control-startup-failed'));
  assert.equal((await f.engine.active()).version,'control-1');
  const recovered=new Supervisor(f.settings,{idle:async()=>true});assert.equal(await recovered.recoverActivation(),null);
  assert.equal((await f.engine.active()).version,'control-1');assert.equal((await f.engine.state()).phase,'rolled-back');
});
test('same controller publication path upgrades old host then guarded native application with new media',{
  timeout:180000,skip:!process.env.APF_TEST_UPDATE_TOOL&&'Requires private update tool/public template prerequisites'
},async t=>{
  const f=await fixture(t);
  const prepared=await execute(powershell,['-NoProfile','-NonInteractive','-File',path.join(repo,'ControlPlane','test','Invoke-NativeMediaFixture.ps1'),'-ControlReleaseRoot',repo,'-TestRoot',path.join(f.root,'application'),'-UpdateToolRoot',process.env.APF_TEST_UPDATE_TOOL,'-PrepareOnly'],{windowsHide:true,maxBuffer:65536,timeout:90000});
  const application=JSON.parse(prepared.stdout.trim());
  await assert.rejects(execute(powershell,['-NoProfile','-NonInteractive','-File',path.join(f.root,'releases',f.seed.directory,'APFDiscordBot','scripts','Verify-KesasRelease.ps1'),'-Archive',application.archive,'-ExpectedVersion',application.version],{windowsHide:true,maxBuffer:65536,timeout:30000}));
  const remote=repositoryFixture();
  const publication=await publishControl({origin:'https://fixture.invalid:18866',pin:'a'.repeat(64),version:'control-2',archive:path.join((await bundles()).root,'Aetherfront-Control-control-2.zip'),gh:remote.gh,fetchStatus:async()=>statusZip(await reportFixture(f)),fetchArtifactDigest:remote.artifactDigest});
  assert.equal(publication.status,'published');assert.equal(remote.state.writes,1);
  const engine=new ControlEngine(f.settings,{...f.options,channel:new GitHubChannel(remote.fetcher)});
  assert.equal((await engine.tick()).status,'handoff');await activate(f);
  const active=await engine.active();assert.equal(active.version,'control-2');
  const release=path.join(f.root,'releases',active.directory);
  const report=await reportFixture(f);assert(report.capabilities.includes('media-payload-v1'));
  await writeJson(path.join(f.root,'host-status.json'),report);
  const {UpdateManager}=await import(pathToFileURL(path.join(release,'APFDiscordBot','src','update-manager.js')));
  const {GitHubDeployment}=await import(pathToFileURL(path.join(release,'APFDiscordBot','src','github-deployment.js')));
  let publications=0;
  const controller={root:path.join(application.root,'Server'),status:async()=>({online:true,ready:true,bridge:{ready:true},playerCount:0,connectedCount:0}),publishRelease:async(archive,notes)=>{
    publications++;
    await execute(powershell,['-NoProfile','-NonInteractive','-File',path.join(release,'KESAS-VPS-Server','Publish-Release.ps1'),'-DeploymentRoot',application.root,'-ReleaseZip',archive,'-Notes',notes],{windowsHide:true,maxBuffer:1024*1024,timeout:90000});
    return {rollbackRoot:path.join(application.root,'Backups','updates')};
  }};
  const config={__root:f.settings.botRoot,__codeRoot:path.join(release,'APFDiscordBot'),__controlRoot:f.root,lmp:{deploymentRoot:application.root},updates:{powerShellExecutable:powershell,stateFile:'state/application-native-state.json',requirePreDeployBackup:false,githubDeployment:{enabled:true,repository:'esotericmartyristaken-bit/AETHERFRONT',branch:'deploy',manifestPath:'deployment/production.json',pollIntervalMs:60000}}};
  const manager=new UpdateManager(config,controller,null);
  const descriptor={schemaVersion:2,version:application.version,releaseTag:`host-${application.version}`,assetName:`Aetherfront-Release-${application.version}.zip`,sha256:application.sha256,sizeBytes:application.sizeBytes,notes:'Fixture publication',requirements:{contractVersion:1,capabilities:['application-v1','media-payload-v1','guard-15m-empty-v1']}};
  const appBytes=await fs.readFile(application.archive);
  const watcher=new GitHubDeployment(config,manager,{activeVersion:async()=>{
    const feed=await readJson(path.join(application.root,'Updates','APFClientUpdate.json'),null);return feed?.version||'kesas-rc30';
  },fetch:async url=>url.includes('/git/ref/')?Response.json({object:{sha:'c'.repeat(40)}}):url.includes('/releases/download/')?new Response(appBytes):Response.json(descriptor)});
  assert.equal((await watcher.tick()).status,'queued');
  assert.equal((await manager.reconcile(await controller.status())).ok,true);assert.equal(publications,1);
  assert.equal((await watcher.tick()).status,'current');assert.equal(publications,1);
  const feed=await readJson(path.join(application.root,'Updates','APFClientUpdate.json'));
  assert.equal(feed.version,application.version);
  assert((await fs.readdir(path.join(application.root,'Server','Config'))).some(name=>/^kesas-potato-marker-[a-f0-9]{64}\.zip$/.test(name)));
});
test('terminal journal recovery removes only its owned stranded maintenance marker',{timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');await f.engine.tick();await activate(f);
  const transaction=await f.engine.state();
  await writeJson(path.join(f.root,'maintenance.json'),{schemaVersion:1,transactionId:transaction.id});
  await new Supervisor(f.settings,{idle:async()=>true}).recoverActivation();
  assert.equal(await fs.stat(path.join(f.root,'maintenance.json')).catch(()=>null),null);
  await writeJson(path.join(f.root,'maintenance.json'),{schemaVersion:1,transactionId:'f'.repeat(64)});
  assert.equal((await f.engine.tick()).status,'current');
  assert.equal((await readJson(path.join(f.root,'maintenance.json'))).transactionId,'f'.repeat(64));
});
test('repeated cached heartbeat cannot satisfy replacement startup health',{timeout:120000},async t=>{
  const f=await fixture(t);f.setDesired('control-2');await f.engine.tick();
  const supervisor=new Supervisor(f.settings,{idle:async()=>true,startTimeout:50,healthMs:1,launch:pointer=>{
    const child=new EventEmitter();child.send=()=>{};child.kill=()=>child.emit('exit',1);const observedAt=Date.now();
    setTimeout(()=>child.emit('message',{type:'ready',version:pointer.version,observedAt}),1);
    setTimeout(()=>child.emit('message',{type:'heartbeat',version:pointer.version,observedAt}),20);
    return child;
  }});
  assert.equal(await supervisor.once(),'rolled-back');assert.equal((await f.engine.active()).version,'control-1');
  assert.equal(await readJson(path.join(f.root,'verified-active.json'),null),null);
});
