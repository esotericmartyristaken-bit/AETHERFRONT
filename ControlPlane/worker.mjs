import fs from 'node:fs/promises';
import path from 'node:path';
import { fork, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { ControlEngine, validatePointer, inventory } from './engine.mjs';
import { CAPABILITIES } from './contract.mjs';
import { writeJson, readJson, fileHash, digest, noLinks, publicMediaInventory } from './public-status.mjs';

const settings=await readJson(process.argv[process.argv.indexOf('--settings')+1]);
const managed=process.env.APF_MANAGED_CONTROL==='1';
const controlRoot=managed?path.join(settings.root,'managed'):settings.root;
const pointer=validatePointer(await readJson(path.join(controlRoot,'active.json')));
const releaseRoot=path.join(controlRoot,'releases',pointer.directory);
const execute=promisify(execFile);
const labels=['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'];
let components=Object.fromEntries(labels.map(label=>[label,null]));
let child=null,ready=false,closed=false,health='unknown',healthObservedAt=0,quiescence=null,exitRequested=false;
let reporting=false,collecting=false,componentVersion=null,inventoryObservedAt=null,inventoryStatus='pending',mediaHashCache={},nextInventory=0;
let shutdownPending=false,workerStage='starting',workerReason=null,workerExitCode=null;

async function launchBot() {
  await noLinks(path.join(settings.botRoot,'state','bot-runtime.log'));
  const log=await fs.open(path.join(settings.botRoot,'state','bot-runtime.log'),'a');
  try {
    child=fork(path.join(releaseRoot,'APFDiscordBot','src','index.js'),[],{cwd:settings.botRoot,execPath:settings.node,windowsHide:true,stdio:['ignore',log.fd,log.fd,'ipc'],env:{...process.env,APF_BOT_ROOT:settings.botRoot,APF_CONTROL_ROOT:settings.root,APF_CONTROL_RELEASE_ROOT:releaseRoot,APF_NATIVE_ROOT:settings.nativeRoot}});
  } finally {await log.close();}
  child.on('message',message=>{
    if(message?.type==='control-ready'){ready=true;workerStage='running';health=message.health;healthObservedAt=message.observedAt;process.send?.({type:'ready',version:pointer.version,observedAt:message.observedAt});}
    if(message?.type==='control-health'){health=message.health;healthObservedAt=message.observedAt;process.send?.({type:'heartbeat',version:pointer.version,observedAt:message.observedAt});}
    if(message?.type==='control-progress')process.send?.({type:'progress',version:pointer.version});
    if(message?.type==='control-health-failed'){health='unknown';healthObservedAt=0;process.send?.({type:'health-failed',version:pointer.version});}
    if(message?.type==='control-quiesced')quiescence?.ack();
    if(message?.type==='control-busy')quiescence?.busy();
  });
  child.on('error',()=>{closed=true;ready=false;workerStage='failed';workerReason='bot-start-failed';quiescence?.busy();});
  child.on('exit',code=>{closed=true;ready=false;workerExitCode=Number.isInteger(code)?code:null;workerStage=shutdownPending||exitRequested?'stopped':'failed';workerReason=workerStage==='failed'?'bot-stopped':null;quiescence?.exit();if(!exitRequested)process.exitCode=1;});
}
async function quiesce() {
  if(!child||closed)return false;
  return new Promise(resolve=>{
    let acknowledged=false;
    const timer=setTimeout(()=>{quiescence=null;child.send?.({type:'resume'});resolve(false);},30000);
    quiescence={ack:()=>{acknowledged=true;},busy:()=>{clearTimeout(timer);quiescence=null;resolve(false);},exit:()=>{clearTimeout(timer);quiescence=null;resolve(acknowledged);}};
    child.send({type:'quiesce'});
  });
}
const engine=new ControlEngine(settings,{quiesce,handoff:async()=>{exitRequested=true;workerStage='draining';await publishStatus();process.send?.({type:'handoff'});process.exit(75);}});
async function hashIfFile(file) {try{return await fileHash(file);}catch(error){if(error.code==='ENOENT')return null;throw error;}}
const match=(value,pattern)=>typeof value==='string'&&pattern.exec(value)?.[0]===value;
const hash=value=>match(value,/^[a-f0-9]{64}$/);
const version=value=>match(value,/^kesas-rc[1-9][0-9]{0,14}$/);
const watcherStages=['channel-observation','requirements','active-health','intent-authentication','archive-download','archive-staging','queue-admission'];
async function watcherDiagnostics() {
  const state=await readJson(path.join(settings.botRoot,'state','github-deployment-state.json'),null).catch(()=>null);
  const value=state?.diagnostic?.schemaVersion===1?state.diagnostic:null;
  const stage=watcherStages.includes(value?.stage)?value.stage:null;
  return {stage,reason:stage&&value?.reason===`${stage}-failed`?value.reason:null,
    version:version(value?.version)?value.version:null,
    sourceCommit:match(value?.sourceCommit,/^[a-f0-9]{40}$/)?value.sourceCommit:null,
    manifestSha256:hash(value?.manifestSha256)?value.manifestSha256:null,
    archiveSha256:hash(value?.archiveSha256)?value.archiveSha256:null};
}
async function collectComponents() {
  if(collecting)return;
  collecting=true;
  // A same-release refresh does not invalidate its completed inventory. The
  // feed identity below and the reporter's version match still gate admission;
  // failures explicitly withdraw capabilities instead of retaining old health.
  if(inventoryStatus!=='complete')inventoryStatus='collecting';
  try {
    const next=Object.fromEntries(labels.map(label=>[label,null]));
    const code=path.join(releaseRoot,'APFDiscordBot'),native=path.join(releaseRoot,'KESAS-VPS-Server');
    const feed=await readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'));
    if(!version(feed.version))throw new Error('Invalid application inventory version');
    if(componentVersion!==feed.version)inventoryStatus='collecting';
    for(const [label,file] of Object.entries({watcher:path.join(code,'src','github-deployment.js'),nativeRunner:path.join(code,'src','update-manager.js'),publisher:path.join(native,'Publish-Release.ps1'),verifier:path.join(code,'scripts','Verify-KesasRelease.ps1'),packService:path.join(settings.nativeRoot,'PackHost','ApfPackHost.exe'),clientBootstrap:path.join(settings.nativeRoot,'KESAS-Client-Bootstrap.zip'),catalog:path.join(settings.nativeRoot,'Server','Config','APFPack.json')}))next[label]=await hashIfFile(file);
    next.dependencies=digest(Buffer.from(JSON.stringify(await inventory(path.join(code,'node_modules')))));
    for(const [label,name] of [['packService','PackHost'],['updateTool','UpdateTool'],['certificateTool','CertificateTool']])next[label]=digest(Buffer.from(JSON.stringify(await inventory(path.join(settings.nativeRoot,name)))));
    const server=path.join(settings.nativeRoot,'Server'),runtimeRows=[];
    await noLinks(server);
    for(const name of (await fs.readdir(server)).filter(name=>/\.(dll|exe|config)$|\.(deps|runtimeconfig)\.json$/i.test(name)).sort()){
      const file=path.join(server,name);await noLinks(file);const stat=await fs.stat(file);if(stat.isFile())runtimeRows.push({path:name,sha256:await fileHash(file),sizeBytes:stat.size});
    }
    next.serverRuntime=digest(Buffer.from(JSON.stringify(runtimeRows)));
    next.bootstrap=await hashIfFile(path.join(settings.root,'bootstrap',managed?'anchor.mjs':'bootstrap.mjs'));
    const pluginRoot=path.join(server,'Plugins');
    next.serverPlugins=await fs.stat(pluginRoot).then(async()=>digest(Buffer.from(JSON.stringify(await inventory(pluginRoot))))).catch(error=>{if(error.code==='ENOENT')return null;throw error;});
    const client=await execute(settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(releaseRoot,'ControlPlane','Read-ClientInventory.ps1'),'-Archive',path.join(settings.nativeRoot,'Updates',feed.version,`APFClient-${feed.version}.zip`),'-Feed',path.join(settings.nativeRoot,'Updates','APFClientUpdate.json')],{windowsHide:true,maxBuffer:16384,timeout:60000});
    const clientHashes=JSON.parse(client.stdout);next.clientUpdater=clientHashes.clientUpdater;next.clientHelper=clientHashes.clientHelper;
    next.catalog=digest(Buffer.from(JSON.stringify({pack:next.catalog,installCatalog:clientHashes.catalog})));
    const media=await publicMediaInventory(path.join(settings.nativeRoot,'Server','Config'),mediaHashCache);
    mediaHashCache=media.cache;next.publicMedia=media.sha256;
    if((await readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'))).version!==feed.version||!labels.every(label=>hash(next[label])))throw new Error('Incomplete application inventory');
    components=next;componentVersion=feed.version;inventoryObservedAt=new Date().toISOString();inventoryStatus='complete';
  } catch(error) {
    inventoryStatus='failed';
    await writeJson(path.join(settings.botRoot,'state','control-inventory-error.json'),{at:new Date().toISOString(),reason:'inventory-failed',detail:String(error?.stack||error).slice(0,12000)}).catch(()=>{});
  }
  finally {collecting=false;nextInventory=Date.now()+(inventoryStatus==='failed'?60000:300000);}
}
async function publishStatus() {
  if(reporting)return;
  reporting=true;
  try {
    const [feed,watcher]=await Promise.all([
      readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'),null).catch(()=>null),watcherDiagnostics()
    ]);
    const now=Date.now(),observedAt=new Date(now).toISOString();
    // Match the bot's bounded observation lifetime. Worker evidence still
    // refreshes every ten seconds; the independent reporter separately requires
    // a fresh bridge and pinned native health before enabling app publication.
    const currentHealth=ready&&!closed&&Number.isFinite(healthObservedAt)&&now-healthObservedAt>=0&&now-healthObservedAt<=90000&&['healthy','offline'].includes(health)?health:'unknown';
    const matching=inventoryStatus==='complete'&&version(feed?.version)&&componentVersion===feed.version;
    const identity={schemaVersion:1,observedAt,controlVersion:pointer.version,controlSha256:pointer.bundleSha256};
    // Evidence only. The privileged independent reporter owns every public file.
    await writeJson(path.join(settings.root,'worker-status.json'),{...identity,expiresAt:new Date(now+30000).toISOString(),ready:ready&&!closed,health:currentHealth,
      capabilities:ready&&!closed&&matching&&currentHealth==='healthy'?[...CAPABILITIES]:[],
      applicationVersion:version(feed?.version)?feed.version:null,
      inventory:{status:inventoryStatus,version:componentVersion,observedAt:inventoryObservedAt},components});
    await writeJson(path.join(settings.root,'worker-diagnostics.json'),{...identity,stage:workerStage,reason:workerReason||(inventoryStatus==='failed'?'inventory-failed':null),exitCode:workerExitCode,watcher});
    if(ready&&!closed&&!collecting&&version(feed?.version)&&(now>=nextInventory||(componentVersion!==feed.version&&inventoryStatus!=='failed')))void collectComponents();
  } finally {reporting=false;}
}
process.on('message',message=>{if(message?.type==='shutdown-when-idle')shutdownPending=true;});
process.on('disconnect',()=>{shutdownPending=true;});
process.on('SIGTERM',()=>{shutdownPending=true;});
process.on('SIGINT',()=>{shutdownPending=true;});
await publishStatus().catch(()=>{});
const reportTimer=setInterval(()=>{void publishStatus().catch(()=>{});},10000);
reportTimer.unref();
let nextTick=0;
try {
  await launchBot();
  void collectComponents();
  while(!exitRequested) {
    if(closed)break;
    if(managed) {
      const drain=await readJson(path.join(controlRoot,'drain.json'),null).catch(()=>null);
      shutdownPending=drain?.bundleSha256===pointer.bundleSha256;
    }
    if(shutdownPending){workerStage='draining';if((managed||await engine.isNativeIdle())&&await quiesce()){exitRequested=true;workerStage='stopped';break;}}
    if(!managed&&ready&&Date.now()>=nextTick) {
      nextTick=Date.now()+60000;
      const outcome=await engine.tick();
      await writeJson(path.join(settings.root,'channel-observation.json'),{status:outcome.status,checkedAt:new Date().toISOString()});
      await publishStatus().catch(()=>{});
    }
    await new Promise(resolve=>setTimeout(resolve,250));
  }
} catch {workerStage='failed';workerReason='worker-loop-failed';process.exitCode=1;}
finally {
  if(!exitRequested)process.exitCode=1;
  clearInterval(reportTimer);
  await publishStatus().catch(()=>{});
}
