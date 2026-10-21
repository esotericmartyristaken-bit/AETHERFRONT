import fs from 'node:fs/promises';
import path from 'node:path';
import { fork } from 'node:child_process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { ControlEngine, validatePointer, inventory } from './engine.mjs';
import { CAPABILITIES, validateHostReport } from './contract.mjs';
import { atomic, writeJson, readJson, fileHash, digest, statusZip, noLinks, publicMediaInventory } from './public-status.mjs';

const settings=await readJson(process.argv[process.argv.indexOf('--settings')+1]);
const pointer=validatePointer(await readJson(path.join(settings.root,'active.json')));
const releaseRoot=path.join(settings.root,'releases',pointer.directory);
let child=null,ready=false,closed=false,health='unknown',healthObservedAt=0,quiescence=null,exitRequested=false;
let reporting=false,collecting=false,componentVersion=null,coreComponentVersion=null;
const execute=promisify(execFile);
const components=Object.fromEntries(['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'].map(label=>[label,null]));

async function launchBot() {
  await noLinks(path.join(settings.botRoot,'state','bot-runtime.log'));
  const log=await fs.open(path.join(settings.botRoot,'state','bot-runtime.log'),'a');
  try {
    child=fork(path.join(releaseRoot,'APFDiscordBot','src','index.js'),[],{cwd:settings.botRoot,execPath:settings.node,windowsHide:true,stdio:['ignore',log.fd,log.fd,'ipc'],env:{...process.env,APF_BOT_ROOT:settings.botRoot,APF_CONTROL_ROOT:settings.root,APF_CONTROL_RELEASE_ROOT:releaseRoot,APF_NATIVE_ROOT:settings.nativeRoot}});
  } finally {await log.close();}
  child.on('message',message=>{
    if(message?.type==='control-ready'){ready=true;health=message.health;healthObservedAt=message.observedAt;process.send?.({type:'ready',version:pointer.version,observedAt:message.observedAt});}
    if(message?.type==='control-health'){health=message.health;healthObservedAt=message.observedAt;process.send?.({type:'heartbeat',version:pointer.version,observedAt:message.observedAt});}
    if(message?.type==='control-progress')process.send?.({type:'progress',version:pointer.version});
    if(message?.type==='control-health-failed'){health='unknown';healthObservedAt=0;process.send?.({type:'health-failed',version:pointer.version});}
    if(message?.type==='control-quiesced'){quiescence?.ack();}
    if(message?.type==='control-busy'){quiescence?.busy();}
  });
  child.on('error',()=>{closed=true;quiescence?.busy();});
  child.on('exit',()=>{closed=true;quiescence?.exit();if(!quiescence&&!exitRequested)process.exitCode=1;});
}
async function quiesce() {
  if(!child || closed)return false;
  return new Promise(resolve=>{
    let acknowledged=false;
    const timer=setTimeout(()=>{quiescence=null;child.send?.({type:'resume'});resolve(false);},30000);
    quiescence={ack:()=>{acknowledged=true;},busy:()=>{clearTimeout(timer);quiescence=null;resolve(false);},exit:()=>{clearTimeout(timer);quiescence=null;resolve(acknowledged);}};
    child.send({type:'quiesce'});
  });
}
const engine=new ControlEngine(settings,{quiesce,handoff:async()=>{exitRequested=true;process.send?.({type:'handoff'});await publishStatus();process.exit(75);}});
async function hashIfFile(file) {try{return await fileHash(file);}catch(error){if(error.code==='ENOENT')return null;throw error;}}
async function collectComponents() {
  if(collecting)return;
  collecting=true;
  try {
  const code=path.join(releaseRoot,'APFDiscordBot');const native=path.join(releaseRoot,'KESAS-VPS-Server');
  for(const [label,file] of Object.entries({watcher:path.join(code,'src','github-deployment.js'),nativeRunner:path.join(code,'src','update-manager.js'),publisher:path.join(native,'Publish-Release.ps1'),verifier:path.join(code,'scripts','Verify-KesasRelease.ps1'),packService:path.join(settings.nativeRoot,'PackHost','ApfPackHost.exe'),serverRuntime:path.join(settings.nativeRoot,'Server','Server.exe'),clientBootstrap:path.join(settings.nativeRoot,'KESAS-Client-Bootstrap.zip'),catalog:path.join(settings.nativeRoot,'Server','Config','APFPack.json')}))components[label]=await hashIfFile(file);
  components.dependencies=await hashIfFile(path.join(code,'package-lock.json'));
  components.dependencies=digest(Buffer.from(JSON.stringify(await inventory(path.join(code,'node_modules')))));
  for(const [label,name] of [['packService','PackHost'],['updateTool','UpdateTool'],['certificateTool','CertificateTool']])components[label]=digest(Buffer.from(JSON.stringify(await inventory(path.join(settings.nativeRoot,name)))));
  const server=path.join(settings.nativeRoot,'Server');const runtimeRows=[];
  for(const name of (await fs.readdir(server)).filter(name=>/\.(dll|exe|config)$|\.(deps|runtimeconfig)\.json$/i.test(name)).sort()){
    const file=path.join(server,name);const stat=await fs.stat(file);if(stat.isFile())runtimeRows.push({path:name,sha256:await fileHash(file),sizeBytes:stat.size});
  }
  components.serverRuntime=digest(Buffer.from(JSON.stringify(runtimeRows)));
  components.bootstrap=await hashIfFile(path.join(settings.root,'bootstrap','bootstrap.mjs'));
  const pluginRoot=path.join(settings.nativeRoot,'Server','Plugins');
  components.serverPlugins=await fs.stat(pluginRoot).then(async()=>digest(Buffer.from(JSON.stringify(await inventory(pluginRoot))))).catch(error=>{if(error.code==='ENOENT')return null;throw error;});
  const feed=await readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'));
  const client=await execute(settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(releaseRoot,'ControlPlane','Read-ClientInventory.ps1'),'-Archive',path.join(settings.nativeRoot,'Updates',feed.version,`APFClient-${feed.version}.zip`),'-Feed',path.join(settings.nativeRoot,'Updates','APFClientUpdate.json')],{windowsHide:true,maxBuffer:16384,timeout:60000});
  const clientHashes=JSON.parse(client.stdout);components.clientUpdater=clientHashes.clientUpdater;components.clientHelper=clientHashes.clientHelper;
  components.catalog=digest(Buffer.from(JSON.stringify({pack:components.catalog,installCatalog:clientHashes.catalog})));
  coreComponentVersion=feed.version;
  const cacheFile=path.join(settings.root,'media-hash-cache.json');
  const media=await publicMediaInventory(path.join(settings.nativeRoot,'Server','Config'),await readJson(cacheFile,{}));
  await writeJson(cacheFile,media.cache);
  components.publicMedia=media.sha256;
  componentVersion=feed.version;
  } finally {collecting=false;}
}
async function publishStatus() {
  if(reporting)return;
  reporting=true;
  try {
  const native=await readJson(path.join(settings.botRoot,'state','kesas-update-state.json'));
  if(!Number.isFinite(healthObservedAt)||Date.now()-healthObservedAt>=90000)health='unknown';
  if(native.schemaVersion!==1)throw new Error('Unsupported native status schema');
  const feed=await readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'));
  const pack=await readJson(path.join(settings.nativeRoot,'Server','Config','APFPack.json'));
  const transaction=await engine.state();
  const observation=await readJson(path.join(settings.root,'channel-observation.json'),null);
  const proof=await readJson(path.join(settings.root,'verified-active.json'),null);
  const proven=proof?.schemaVersion===1&&proof.version===pointer.version&&proof.bundleSha256===pointer.bundleSha256&&Number.isFinite(Date.parse(proof.verifiedAt));
  const previousProven=proof?.schemaVersion===1&&proof.version===transaction?.from?.version&&proof.bundleSha256===transaction?.from?.bundleSha256&&Number.isFinite(Date.parse(proof.verifiedAt));
  const controlStatus=!proven?'applying':observation?.status==='blocked'?'blocked':!transaction?'healthy':({accepted:'accepted',staging:'staging','waiting-existing-guard':'waiting-existing-guard',activating:'applying','health-check':'applying',healthy:'healthy','rolled-back':'rolled-back',blocked:'blocked'}[transaction.phase]||'blocked');
  const appStatus=({idle:'idle',queued:'waiting-existing-guard',deploying:'applying',failed:'blocked'}[native.status]||'unknown');
  const terminal=transaction&&['healthy','rolled-back','blocked'].includes(transaction.phase);
  const consistent=feed.version===pack.packVersion && pack.requiredClientVersion===feed.version;
  let applicationSuccess=await readJson(path.join(settings.root,'application-last-success.json'),null);
  if(native.status==='idle'&&consistent&&health==='healthy') {
    applicationSuccess={schemaVersion:1,version:feed.version};
    await writeJson(path.join(settings.root,'application-last-success.json'),applicationSuccess);
  }
  if(native.status==='idle'&&consistent&&componentVersion!==feed.version&&!collecting)collectComponents().catch(()=>{});
  const report=validateHostReport({
    schemaVersion:1,contractVersion:1,bootstrapVersion:1,
    observedAt:new Date().toISOString(),expiresAt:new Date(Date.now()+150000).toISOString(),
    capabilities:coreComponentVersion===feed.version&&Object.entries(components).every(([name,value])=>name==='publicMedia'||value!==null)?[...CAPABILITIES]:[],
    control:{
      version:pointer.version,sequence:pointer.sequence,sha256:pointer.bundleSha256,
      status:controlStatus,phase:transaction?.phase||null,
      pendingVersion:transaction&&!terminal?transaction.descriptor.version:null,
      lastSuccess:proven||previousProven?proof.version:null,
      rollback:transaction?.phase==='rolled-back'?transaction.descriptor.version:null,
      failure:transaction?.failure||null
    },
    application:{
      clientVersion:feed.version,packVersion:pack.packVersion,status:consistent?appStatus:'blocked',
      phase:/^[a-z][a-z0-9-]{0,79}$/.test(native.phase||'')?native.phase:null,
      pendingVersion:native.version||null,
      health:consistent&&['healthy','offline','unknown'].includes(health)?health:'unknown',
      lastSuccess:native.lastDeployment?.status==='cancelled'?null:native.lastDeployment?.version||null,
      failure:!consistent?'application-version-mismatch':native.status==='failed'?'native-deployment-failed':null
    },components,runtime:settings.runtime
  });
  if(observation?.status==='blocked'&&!report.control.failure)report.control.failure='control-channel-invalid';
  if(applicationSuccess?.schemaVersion===1)report.application.lastSuccess=applicationSuccess.version;
  validateHostReport(report);
  await writeJson(path.join(settings.root,'host-status.json'),report);
  await atomic(path.join(settings.nativeRoot,'Server','Config','kesas-host-status.zip'),statusZip(report));
  } finally {reporting=false;}
}
let shutdownPending=false;
process.on('message',message=>{if(message?.type==='shutdown-when-idle')shutdownPending=true;});
process.on('disconnect',()=>{shutdownPending=true;});
process.on('SIGTERM',()=>{shutdownPending=true;});
process.on('SIGINT',()=>{shutdownPending=true;});
await launchBot();
collectComponents().catch(()=>{});
const reportTimer=setInterval(()=>{if(ready)publishStatus().catch(()=>{});},10000);
reportTimer.unref();
let nextTick=0;
while(!exitRequested) {
  if(closed)break;
  if(shutdownPending && await engine.isNativeIdle()) {if(await quiesce()){exitRequested=true;break;}}
  if(ready && Date.now()>=nextTick) {
    nextTick=Date.now()+60000;
    const outcome=await engine.tick();
    await writeJson(path.join(settings.root,'channel-observation.json'),{status:outcome.status,checkedAt:new Date().toISOString()});
    await publishStatus().catch(()=>{});
  }
  await new Promise(resolve=>setTimeout(resolve,250));
}
if(!exitRequested)process.exitCode=1;
clearInterval(reportTimer);
