import fs from 'node:fs/promises';
import path from 'node:path';
import { fork } from 'node:child_process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { ControlEngine, validatePointer, inventory } from './engine.mjs';
import { CAPABILITIES, validateHostReport } from './contract.mjs';
import { atomic, writeJson, readJson, fileHash, digest, statusZip, diagnosticsZip, noLinks, publicMediaInventory } from './public-status.mjs';

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
const diagnosticMatch=(value,pattern)=>typeof value==='string'&&pattern.exec(value)?.[0]===value;
const diagnosticHash=value=>diagnosticMatch(value,/^[a-f0-9]{64}$/);
const diagnosticVersion=value=>diagnosticMatch(value,/^kesas-rc[1-9][0-9]{0,14}$/);
const diagnosticCommit=value=>value===null||diagnosticMatch(value,/^[a-f0-9]{40}$/);
const diagnosticObject=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
const nativeDiagnosticStages=['request-validation','intent-authentication','archive-copy','fixed-verifier','publisher-validation','publisher-execution','completion-health','awaiting-new-guard','guard-admission','preparation-signing','media-publication','native-activation','github-completion'];
const watcherDiagnosticStages=['channel-observation','requirements','active-health','intent-authentication','archive-download','archive-staging','queue-admission'];
async function bootstrapDiagnostics() {
  const directory=path.join(settings.root,'bootstrap');
  const nativeAgentSha256=await hashIfFile(path.join(directory,'native-agent.mjs')).catch(()=>null);
  const publisherWrapperSha256=await hashIfFile(path.join(directory,'Invoke-NativePublisher.ps1')).catch(()=>null);
  const capabilities=await readJson(path.join(settings.root,'native-capabilities.json'),null).catch(()=>null);
  const supported=diagnosticObject(capabilities)&&Object.keys(capabilities).length===4&&capabilities.schemaVersion===1&&capabilities.recoveryVersion===1&&
    diagnosticHash(nativeAgentSha256)&&diagnosticHash(publisherWrapperSha256)&&capabilities.nativeAgentSha256===nativeAgentSha256&&capabilities.publisherWrapperSha256===publisherWrapperSha256;
  return {nativeAgentSha256,publisherWrapperSha256,nativeRecoveryVersion:supported?1:0};
}
async function watcherDiagnostics() {
  const state=await readJson(path.join(settings.botRoot,'state','github-deployment-state.json'),null).catch(()=>null);
  const value=diagnosticObject(state?.diagnostic)&&state.diagnostic.schemaVersion===1?state.diagnostic:null;
  const stage=watcherDiagnosticStages.includes(value?.stage)?value.stage:null;
  return {stage,reason:stage!==null&&value?.reason===`${stage}-failed`?value.reason:null,
    version:diagnosticVersion(value?.version)?value.version:null,
    sourceCommit:diagnosticCommit(value?.sourceCommit)?value.sourceCommit:null,
    manifestSha256:diagnosticHash(value?.manifestSha256)?value.manifestSha256:null,
    archiveSha256:diagnosticHash(value?.archiveSha256)?value.archiveSha256:null};
}
async function recoveryDiagnostics() {
  const value=await readJson(path.join(settings.root,'native-recovery-status.json'),null).catch(()=>null);
  if(!diagnosticObject(value)||!diagnosticHash(value.recoveryId)||!diagnosticHash(value.requestId)||!diagnosticCommit(value.commit)||
      !['received','authorized','blocked'].includes(value.status)||![null,'recovery-not-authorized'].includes(value.reason))return null;
  // This public mirror is observation only; it never authorizes a recovery.
  return {recoveryId:value.recoveryId,requestId:value.requestId,commit:value.commit,status:value.status,reason:value.reason};
}
async function nativeResultSnapshot(file) {
  await noLinks(file);
  const handle=await fs.open(file,'r');
  try {
    const maximum=1024*1024,stat=await handle.stat();
    if(!stat.isFile()||stat.size>maximum)return null;
    // One bounded snapshot supplies both JSON identity and its raw-byte digest.
    const bytes=Buffer.alloc(maximum+1);
    let length=0;
    while(length<bytes.length) {
      const {bytesRead}=await handle.read(bytes,length,bytes.length-length,null);
      if(bytesRead===0)break;
      length+=bytesRead;
    }
    return length>maximum?null:bytes.subarray(0,length);
  } finally {await handle.close();}
}
async function nativeDiagnostics(native,feed) {
  // Follow the pending native version, otherwise the currently published feed.
  const version=diagnosticVersion(native.version)?native.version:diagnosticVersion(feed.version)?feed.version:null;
  if(version===null)return null;
  const directory=path.join(settings.root,'native-results');
  await noLinks(directory);
  const candidates=[];
  for(const entry of await fs.readdir(directory,{withFileTypes:true})) {
    if(!entry.isFile()||!diagnosticMatch(entry.name,/^[a-f0-9]{64}\.json$/))continue;
    const file=path.join(directory,entry.name),id=entry.name.slice(0,-5);
    try {
      const bytes=await nativeResultSnapshot(file);
      if(bytes===null)continue;
      const value=JSON.parse(bytes.toString('utf8'));
      if(!diagnosticObject(value)||value.schemaVersion!==1||value.transactionVersion!==1||value.id!==id||value.version!==version||
          !diagnosticHash(value.sha256)||!diagnosticCommit(value.sourceCommit)||
          !['accepted','recovery-authorized','applying','healthy','blocked'].includes(value.phase)||value.status!==value.phase)continue;
      const stage=value.stage===undefined&&value.phase==='accepted'?'request-validation':value.stage;
      const attempt=value.attempt===undefined?1:value.attempt,exitCode=value.exitCode===undefined?null:value.exitCode;
      const reason=value.failure===undefined?null:value.failure;
      const updatedAt=typeof value.updatedAt==='string'&&value.updatedAt.length<=40?Date.parse(value.updatedAt):NaN;
      if(!nativeDiagnosticStages.includes(stage)||!Number.isInteger(attempt)||attempt<1||attempt>1000000||
          !(exitCode===null||(Number.isInteger(exitCode)&&exitCode>=-2147483648&&exitCode<=2147483647))||
          ![null,'native-publication-blocked'].includes(reason)||!Number.isFinite(updatedAt))continue;
      candidates.push({updatedAt,publication:{requestId:id,version,archiveSha256:value.sha256,sourceCommit:value.sourceCommit,
        resultSha256:digest(bytes),phase:value.phase,stage,reason,exitCode,attempt,
        // This is only a candidate. The host rechecks its private journal, lease and hashes.
        recoveryAction:value.phase==='blocked'&&nativeDiagnosticStages.slice(0,5).includes(stage)&&value.nativePid===null?'retry-preactivation':null}});
    } catch {}
  }
  candidates.sort((a,b)=>b.updatedAt-a.updatedAt||a.publication.requestId.localeCompare(b.publication.requestId));
  return candidates[0]?.publication||null;
}
async function publishDiagnostics(report,native,feed) {
  const [bootstrap,watcher,publication,recovery]=await Promise.all([
    bootstrapDiagnostics().catch(()=>({nativeAgentSha256:null,publisherWrapperSha256:null,nativeRecoveryVersion:0})),
    watcherDiagnostics().catch(()=>({stage:null,reason:null,version:null,sourceCommit:null,manifestSha256:null,archiveSha256:null})),
    nativeDiagnostics(native,feed).catch(()=>null),
    recoveryDiagnostics().catch(()=>null)
  ]);
  await atomic(path.join(settings.nativeRoot,'Server','Config','kesas-host-diagnostics.zip'),diagnosticsZip({schemaVersion:1,
    observedAt:report.observedAt,expiresAt:report.expiresAt,controlVersion:report.control.version,bootstrap,watcher,native:publication,recovery}));
}
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
  await publishDiagnostics(report,native,feed).catch(()=>{});
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
