// Read-only evidence collection, independent of the limited worker and native agent loop.
import fs from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { validatePointer, validateTransaction } from './engine.mjs';
import { CAPABILITIES, validateHostReport } from './contract.mjs';
import { atomic, writeJson, readJson, noLinks, fileHash, digest, statusZip, diagnosticsZip, RECOVERY_REASONS } from './public-status.mjs';

const execute=promisify(execFile);
const labels=['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'];
const nativeStages=['request-validation','intent-authentication','archive-copy','fixed-verifier','publisher-validation','publisher-execution','completion-health','awaiting-new-guard','guard-admission','preparation-signing','media-publication','native-activation','github-completion'];
const watcherStages=['channel-observation','requirements','active-health','intent-authentication','archive-download','archive-staging','queue-admission'];
const nativePhases=['accepted','recovery-authorized','applying','healthy','blocked'];
// These are wire codes, never exception text, paths, URLs or child output.
export const HOST_DIAGNOSTIC_CODES=Object.freeze({
  stage:Object.freeze(['reporting','broker','worker','inventory','application','native','control']),
  reason:Object.freeze([null,'broker-not-ready','broker-child-failed','worker-missing','worker-stale','worker-mismatch','worker-invalid','worker-not-ready','worker-failed','inventory-incomplete','inventory-failed','inventory-mismatch','application-observation-failed','application-version-mismatch','bridge-unavailable','native-observation-failed','native-completion-unproven','native-publication-blocked','health-unavailable','control-observation-failed']),
  worker:Object.freeze(['missing','stale','mismatch','invalid','starting','running','draining','stopped','failed']),
  broker:Object.freeze(['ready','starting','failed']),
  inventory:Object.freeze(['unknown','pending','collecting','complete','failed','mismatch']),
  journalPhase:Object.freeze([null,'native-activation','github-completion','unknown'])
});
const match=(value,pattern)=>typeof value==='string'&&pattern.exec(value)?.[0]===value;
const hash=value=>match(value,/^[a-f0-9]{64}$/);
const version=value=>match(value,/^kesas-rc[1-9][0-9]{0,14}$/);
const controlVersion=value=>match(value,/^control-[1-9][0-9]{0,8}$/);
const commit=value=>value===null||match(value,/^[a-f0-9]{40}$/);
const object=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
const exitCode=value=>value===null||(Number.isInteger(value)&&value>=-2147483648&&value<=2147483647);
const date=value=>match(value,/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/)&&Number.isFinite(Date.parse(value));
const fresh=(value,now,maximum)=>date(value)&&now-Date.parse(value)>=0&&now-Date.parse(value)<=maximum;
const emptyComponents=()=>Object.fromEntries(labels.map(label=>[label,null]));
const emptyWatcher=()=>({stage:null,reason:null,version:null,sourceCommit:null,manifestSha256:null,archiveSha256:null});
const inFlight=new Map(),archiveCache=new Map();
let archiveWork=Promise.resolve(),pendingArchives=0;

// Ignore access time (the read can change it), but compare exact file identity,
// metadata and nanosecond content timestamps, including the final named path.
const sameEvidenceStat=(left,right)=>['dev','ino','mode','nlink','uid','gid','rdev','size','mtimeNs','ctimeNs','birthtimeNs'].every(key=>left[key]===right[key]);
async function rawSnapshot(file,maximum) {
  await noLinks(file);
  const handle=await fs.open(file,'r');
  try {
    const before=await handle.stat({bigint:true});
    if(!before.isFile()||before.nlink!==1n||before.size<1n||before.size>BigInt(maximum))throw new Error('Invalid report evidence');
    const bytes=Buffer.alloc(maximum+1);
    let length=0;
    while(length<bytes.length) {
      const {bytesRead}=await handle.read(bytes,length,bytes.length-length,null);
      if(!bytesRead)break;
      length+=bytesRead;
    }
    const after=await handle.stat({bigint:true});
    await noLinks(file);
    const named=await fs.lstat(file,{bigint:true});
    if(length>maximum||BigInt(length)!==before.size||!sameEvidenceStat(before,after)||!sameEvidenceStat(after,named))throw new Error('Report evidence changed');
    const content=bytes.subarray(0,length);
    return {content,sha256:digest(content)};
  } finally {await handle.close();}
}
// JSON evidence reuses the same bounded bytes and digest. Only native bridge
// files may have a UTF-8 BOM; executable code is never parsed as JSON.
async function snapshot(file,maximum=65536,bridgeBom=false) {
  const {content,sha256}=await rawSnapshot(file,maximum);
  const offset=bridgeBom&&content[0]===0xef&&content[1]===0xbb&&content[2]===0xbf?3:0;
  return {value:JSON.parse(content.subarray(offset).toString('utf8')),sha256};
}
const stamp=stat=>[stat.dev,stat.ino,stat.size,stat.mtimeMs,stat.ctimeMs].join(':');
async function archiveVerified(settings,result) {
  const file=path.join(settings.root,'native-cache','applications',result.id+'.zip');
  try {
    await noLinks(file);
    const stat=await fs.stat(file);
    if(!stat.isFile()||stat.nlink!==1||stat.size!==result.sizeBytes)return false;
    const identity=stamp(stat),previous=archiveCache.get(file);
    if(previous?.identity===identity)return previous.sha256===result.sha256;
    if(previous?.pending===identity)return false;
    if(pendingArchives>=2||(archiveCache.size>=256&&!previous))return false;
    pendingArchives++;
    const row={pending:identity};archiveCache.set(file,row);
    // Large immutable archives are hashed once in serialized background work,
    // never awaited or rescanned by a report heartbeat.
    archiveWork=archiveWork.catch(()=>{}).then(async()=>{
      await noLinks(file);
      if(stamp(await fs.stat(file))!==identity)throw new Error('Archive changed');
      const sha256=await fileHash(file);
      await noLinks(file);
      if(stamp(await fs.stat(file))!==identity)throw new Error('Archive changed');
      if(archiveCache.get(file)===row)archiveCache.set(file,{identity,sha256});
    }).catch(()=>{if(archiveCache.get(file)===row)archiveCache.delete(file);}).finally(()=>{pendingArchives--;});
  } catch {}
  return false;
}
async function completed(settings,result) {
  if(!result||result.phase!=='healthy'||result.health!=='healthy')return false;
  const receipt=await readJson(path.join(settings.root,'native-cache','diagnostics',result.id,String(result.attempt),'receipt.json'),null).catch(()=>null);
  return receipt?.schemaVersion===1&&receipt.requestId===result.id&&receipt.attempt===result.attempt&&receipt.status==='succeeded'&&receipt.exitCode===0&&
    typeof receipt.completedAt==='string'&&Number.isFinite(Date.parse(receipt.completedAt))&&await archiveVerified(settings,result);
}
async function nativeEvidence(settings,current) {
  const directory=path.join(settings.root,'native-cache','results'),rows=[];
  await noLinks(directory);
  let entries=0,files=0;
  const stream=await fs.opendir(directory);
  for await(const entry of stream) {
    if(++entries>512)throw new Error('Native evidence directory exceeds limit');
    if(!match(entry.name,/^[a-f0-9]{64}\.json$/))continue;
    if(!entry.isFile()||++files>128)throw new Error('Invalid native evidence inventory');
    const {value,sha256}=await snapshot(path.join(directory,entry.name));
    const attempt=value?.attempt===undefined?1:value.attempt;
    const stage=value?.stage===undefined&&value?.phase==='accepted'?'request-validation':value?.stage;
    if(!object(value)||value.schemaVersion!==1||value.transactionVersion!==1||value.id!==entry.name.slice(0,-5)||!version(value.version)||
      !hash(value.sha256)||!commit(value.sourceCommit)||!Number.isSafeInteger(value.sizeBytes)||value.sizeBytes<1||value.sizeBytes>512*1024*1024||
      !nativePhases.includes(value.phase)||value.status!==value.phase||!nativeStages.includes(stage)||!date(value.updatedAt)||
      !Number.isInteger(attempt)||attempt<1||attempt>1000000||!exitCode(value.exitCode??null)||
      ![undefined,null,'native-publication-blocked'].includes(value.failure)||
      !(value.nativePid===undefined||value.nativePid===null||(Number.isSafeInteger(value.nativePid)&&value.nativePid>0)))throw new Error('Invalid private native result');
    rows.push({...value,attempt,stage,resultSha256:sha256});
  }
  rows.sort((a,b)=>{const left=BigInt(a.version.slice(8)),right=BigInt(b.version.slice(8));return left===right?Date.parse(b.updatedAt)-Date.parse(a.updatedAt)||a.id.localeCompare(b.id):left>right?-1:1;});
  const active=rows.find(row=>row.phase==='applying')||
    rows.find(row=>['accepted','recovery-authorized','blocked'].includes(row.phase)&&(!version(current)||BigInt(row.version.slice(8))>BigInt(current.slice(8))))||
    rows.find(row=>row.version===current)||null;
  let lastSuccess=null;
  // At most two archives can be queued for background verification. Bounded
  // receipt reads still find an older proven version behind an unproven result.
  const healthy=rows.filter(row=>row.phase==='healthy'&&(!version(current)||BigInt(row.version.slice(8))<=BigInt(current.slice(8))));
  for(const row of healthy)if(await completed(settings,row)){lastSuccess=row.version;break;}
  const currentResult=rows.find(row=>row.version===current)||null;
  const currentCompleted=currentResult?.version===lastSuccess&&currentResult?.phase==='healthy'&&await completed(settings,currentResult);
  return {active,currentResult,currentVersion:current,currentCompleted,lastSuccess,publication:active?{
    requestId:active.id,version:active.version,archiveSha256:active.sha256,sourceCommit:active.sourceCommit,resultSha256:active.resultSha256,
    phase:active.phase,stage:active.stage,reason:active.failure??null,exitCode:active.exitCode??null,attempt:active.attempt,
    recoveryAction:active.phase==='blocked'&&nativeStages.slice(0,5).includes(active.stage)&&active.nativePid===null?'retry-preactivation':null
  }:null};
}
async function workerEvidence(settings,pointer,now) {
  const [status,diagnostic]=await Promise.all(['worker-status.json','worker-diagnostics.json'].map(name=>readJson(path.join(settings.root,name),null)));
  if(!status)return {state:'missing',inventory:'unknown',status:null,diagnostic:null};
  if(!object(status)||!object(diagnostic)||status.schemaVersion!==1||diagnostic.schemaVersion!==1||typeof status.ready!=='boolean'||
    !['healthy','offline','unknown'].includes(status.health)||!date(status.observedAt)||!date(status.expiresAt)||!date(diagnostic.observedAt)||
    !['starting','running','draining','stopped','failed'].includes(diagnostic.stage)||!exitCode(diagnostic.exitCode)||
    !object(status.inventory)||!['pending','collecting','complete','failed'].includes(status.inventory.status)||
    !object(status.components)||Object.keys(status.components).length!==labels.length||!labels.every(label=>status.components[label]===null||hash(status.components[label])))throw new Error('Invalid worker evidence');
  if(status.controlVersion!==pointer.version||status.controlSha256!==pointer.bundleSha256||diagnostic.controlVersion!==pointer.version||diagnostic.controlSha256!==pointer.bundleSha256)return {state:'mismatch',inventory:'unknown',status:null,diagnostic:null};
  if(!fresh(status.observedAt,now,30000)||!fresh(diagnostic.observedAt,now,30000)||Date.parse(status.expiresAt)<=now||Date.parse(status.expiresAt)-Date.parse(status.observedAt)>30000)return {state:'stale',inventory:'unknown',status:null,diagnostic:null};
  return {state:diagnostic.stage,inventory:status.inventory.status,status,diagnostic};
}
function watcherEvidence(worker) {
  const value=worker.diagnostic?.watcher,stage=watcherStages.includes(value?.stage)?value.stage:null;
  return {stage,reason:stage&&value.reason===stage+'-failed'?value.reason:null,version:version(value?.version)?value.version:null,
    sourceCommit:commit(value?.sourceCommit)?value.sourceCommit:null,manifestSha256:hash(value?.manifestSha256)?value.manifestSha256:null,archiveSha256:hash(value?.archiveSha256)?value.archiveSha256:null};
}
async function bridgeFresh(settings,now) {
  const root=path.join(settings.nativeRoot,'Server','APFDiscordBridge');
  const [ready,live]=await Promise.all(['ready.json','status.json'].map(async name=>(await snapshot(path.join(root,name),1024*1024,true)).value));
  const updated=typeof live?.updatedAt==='string'&&live.updatedAt.length<=40?Date.parse(live.updatedAt):NaN;
  const started=typeof ready?.startedAt==='string'&&ready.startedAt.length<=40?Date.parse(ready.startedAt):NaN;
  return Number.isSafeInteger(ready?.pid)&&ready.pid>0&&live?.serverPid===ready.pid&&Number.isFinite(started)&&started<=updated&&
    Number.isFinite(updated)&&now-updated>=0&&now-updated<=10000&&Number.isInteger(live.playerCount)&&live.playerCount>=0&&
    Number.isInteger(live.connectedCount)&&live.connectedCount>=live.playerCount&&Array.isArray(live.players)&&live.players.length===live.playerCount;
}
async function leaseActive(settings,active) {
  let child=false;
  if(active?.phase==='applying'&&active.nativePid)try{process.kill(active.nativePid,0);child=true;}catch(error){if(error.code!=='ESRCH')child=true;}
  if(child)return true;
  try {
    await noLinks(path.join(settings.nativeRoot,'.publish.lock'));
    // Lease only: an abandoned journal is not evidence of a running helper.
    // The timeout affects only this probe process, never a publisher.
    const probe="$ErrorActionPreference='Stop';$reportLease=$null;try{$reportRoot=[Environment]::GetEnvironmentVariable('APF_REPORT_LEASE_ROOT');$reportLease=[IO.FileStream]::new([IO.Path]::Combine($reportRoot,'.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);Write-Output 'idle'}catch [IO.IOException]{Write-Output 'busy'}finally{if($reportLease){$reportLease.Dispose()}}";
    const value=await execute(settings.powershell,['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(probe,'utf16le').toString('base64')],{env:{...process.env,APF_REPORT_LEASE_ROOT:settings.nativeRoot},windowsHide:true,timeout:5000,maxBuffer:1024});
    return value.stdout.trim()==='idle'?false:value.stdout.trim()==='busy'?true:null;
  } catch {return null;}
}
async function recoveryEvidence(settings) {
  const mirror=await readJson(path.join(settings.root,'native-recovery-status.json'),null);
  if(!hash(mirror?.recoveryId)||!hash(mirror?.requestId))return null;
  const value=await readJson(path.join(settings.root,'native-cache','recoveries','remote-'+mirror.recoveryId+'.json'),null);
  if(value?.schemaVersion!==1||value.recoveryId!==mirror.recoveryId||value.requestId!==mirror.requestId||!commit(value.commit??null)||
    !['received','authorized','blocked'].includes(value.status)||!RECOVERY_REASONS.includes(value.reason??null))return null;
  return {recoveryId:value.recoveryId,requestId:value.requestId,commit:value.commit??null,status:value.status,reason:value.reason??null};
}
export async function bootstrapEvidence(directory,ready) {
  const read=async name=>directory?(await rawSnapshot(path.join(directory,'ControlPlane',name),1024*1024).catch(()=>null))?.sha256??null:null;
  const [nativeAgentSha256,publisherWrapperSha256]=await Promise.all([read('native-agent.mjs'),read('Invoke-NativePublisher.ps1')]);
  return {nativeAgentSha256,publisherWrapperSha256,nativeRecoveryVersion:ready&&hash(nativeAgentSha256)&&hash(publisherWrapperSha256)?1:0};
}
async function controlEvidence(settings,pointer) {
  const root=path.join(settings.root,'managed');
  let value=await readJson(path.join(root,'transaction.json'),null);
  if(value) {
    validateTransaction(value);
    const history=await readJson(path.join(root,'transactions',value.id+'.json'),null);
    if(history&&(history.revision||0)>(value.revision||0)){validateTransaction(history);value=history;}
  }
  const proof=await readJson(path.join(root,'verified-active.json'),null);
  const lastSuccess=proof?.schemaVersion===1&&controlVersion(proof.version)&&hash(proof.bundleSha256)&&date(proof.verifiedAt)&&
    (proof.version!==pointer.version||proof.bundleSha256===pointer.bundleSha256)?proof.version:null;
  return {value,lastSuccess};
}
async function reportOnce(settings,options) {
  const pointer=Object.freeze({...validatePointer(options.pointer)}),now=Date.now();
  const expected=path.resolve(settings.root,'managed','releases',pointer.directory);
  const directory=typeof options.brokerDirectory==='string'&&path.resolve(options.brokerDirectory)===expected?expected:null;
  const brokerReady=options.brokerReady===true&&directory!==null;
  const host={stage:'reporting',reason:null,worker:'missing',broker:brokerReady?'ready':options.anchorReason==='broker-child-failed'?'failed':'starting',
    inventory:'unknown',leaseActive:null,journalPhase:null,exitCode:exitCode(options.exitCode??null)?options.exitCode??null:null};
  const problem=(stage,reason)=>{if(host.reason===null){host.stage=stage;host.reason=reason;}};
  if(!brokerReady)problem('broker',options.anchorReason==='broker-child-failed'?'broker-child-failed':'broker-not-ready');
  const [worker,feed,pack,bridge,native,control,journal,recovery,bootstrap,provision]=await Promise.all([
    workerEvidence(settings,pointer,now).catch(()=>({state:'invalid',inventory:'unknown',status:null,diagnostic:null})),
    readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'),null).catch(()=>null),
    readJson(path.join(settings.nativeRoot,'Server','Config','APFPack.json'),null).catch(()=>null),
    bridgeFresh(settings,now).catch(()=>false),
    // Feed identity is read again inside this branch rather than borrowed from worker evidence.
    readJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'),null).then(value=>nativeEvidence(settings,version(value?.version)?value.version:null)).catch(()=>null),
    controlEvidence(settings,pointer).catch(()=>null),
    readJson(path.join(settings.nativeRoot,'.update-transaction.json'),null).catch(()=>({phase:'unknown'})),
    recoveryEvidence(settings).catch(()=>null),
    bootstrapEvidence(directory,brokerReady),
    readJson(path.join(settings.root,'bootstrap','unattended-host.json'),null).catch(()=>null)
  ]);
  if(bootstrap.nativeRecoveryVersion!==1)problem('broker','broker-not-ready');
  host.worker=worker.state;host.inventory=worker.inventory;
  if(!['running','draining'].includes(worker.state))problem('worker',['missing','stale','mismatch','invalid'].includes(worker.state)?'worker-'+worker.state:worker.state==='failed'||worker.state==='stopped'?'worker-failed':'worker-not-ready');
  else if(worker.state==='running'&&(worker.status?.ready!==true||worker.status?.health!=='healthy'))problem('worker','worker-not-ready');
  if(host.exitCode===null&&worker.diagnostic?.exitCode!==null)host.exitCode=worker.diagnostic?.exitCode??null;
  const clientVersion=version(feed?.version)?feed.version:null,packVersion=version(pack?.packVersion)?pack.packVersion:null;
  const consistent=clientVersion!==null&&packVersion===clientVersion&&pack.requiredClientVersion===clientVersion;
  if(!clientVersion||!packVersion)problem('application','application-observation-failed');
  else if(!consistent)problem('application','application-version-mismatch');
  if(!bridge)problem('application','bridge-unavailable');
  const observedHealth=consistent&&bridge&&['healthy','offline','unknown'].includes(options.health)?options.health:'unknown';
  if(observedHealth!=='healthy')problem('application','health-unavailable');
  if(journal)host.journalPhase=['native-activation','github-completion'].includes(journal.phase)?journal.phase:'unknown';
  host.leaseActive=await leaseActive(settings,native?.active);
  if(!native)problem('native','native-observation-failed');
  const inventoryMatches=worker.inventory==='complete'&&worker.status?.applicationVersion===clientVersion&&worker.status.inventory.version===clientVersion&&
    labels.every(label=>hash(worker.status.components[label]));
  if(worker.inventory==='complete'&&!inventoryMatches){host.inventory='mismatch';problem('inventory','inventory-mismatch');}
  else if(worker.inventory!=='complete')problem('inventory',worker.inventory==='failed'?'inventory-failed':'inventory-incomplete');
  if(!control)problem('control','control-observation-failed');
  const transaction=control?.value,terminal=transaction&&['healthy','rolled-back','blocked'].includes(transaction.phase);
  const pendingVersion=transaction&&!terminal?transaction.descriptor.version:null;
  const status=brokerReady?({accepted:'accepted',staging:'staging','waiting-existing-guard':'waiting-existing-guard',activating:'applying','health-check':'applying','rolling-back':'applying',healthy:'healthy','rolled-back':'rolled-back',blocked:'blocked'}[transaction?.phase]||'healthy'):'unknown';
  const active=native?.active,applying=active?.phase==='applying'||host.leaseActive===true||journal!==null;
  const currentCompleted=native?.currentVersion===clientVersion&&native.currentCompleted===true;
  const applicationStatus=applying?'applying':active?.phase==='blocked'?'blocked':active&&['accepted','recovery-authorized'].includes(active.phase)?'waiting-existing-guard':currentCompleted?'idle':'unknown';
  let applicationFailure=!consistent?'application-version-mismatch':active?.phase==='blocked'?'native-deployment-failed':null;
  if(active?.phase==='blocked')problem('native','native-publication-blocked');
  else if(!currentCompleted&&!applying)problem('native','native-completion-unproven');
  if(host.stage==='native'&&exitCode(active?.exitCode??null))host.exitCode=active?.exitCode??host.exitCode;
  if(native?.publication&&(host.leaseActive!==false||journal!==null))native.publication.recoveryAction=null;
  const minimum=['control-plane-v1','host-report-v1','versioned-broker-v1','independent-report-v1'];
  const unattended=provision?.schemaVersion===1&&provision.ready===true&&provision.broker==='SYSTEM'&&provision.bot==='S4U-Limited';
  const full=brokerReady&&control!==null&&native!==null&&host.journalPhase!=='unknown'&&bootstrap.nativeRecoveryVersion===1&&
    worker.state==='running'&&worker.status?.ready===true&&worker.status.health==='healthy'&&inventoryMatches&&observedHealth==='healthy';
  const capabilities=(full?[...CAPABILITIES]:minimum).filter(name=>name!=='unattended-host-v1'||unattended);
  if(unattended&&!capabilities.includes('unattended-host-v1'))capabilities.push('unattended-host-v1');
  const report=validateHostReport({
    schemaVersion:1,contractVersion:1,bootstrapVersion:1,observedAt:new Date(now).toISOString(),expiresAt:new Date(now+30000).toISOString(),capabilities,
    control:{version:pointer.version,sequence:pointer.sequence,sha256:pointer.bundleSha256,status,phase:transaction?.phase??null,pendingVersion,
      lastSuccess:control?.lastSuccess??null,rollback:transaction?.phase==='rolled-back'?transaction.descriptor.version:null,
      failure:transaction?.phase==='blocked'?'control-transaction-blocked':null},
    application:{clientVersion,packVersion,status:consistent?applicationStatus:'blocked',phase:active?.stage??null,
      pendingVersion:active&&active.phase!=='healthy'?active.version:null,health:observedHealth,lastSuccess:native?.lastSuccess??null,failure:applicationFailure},
    components:worker.status?Object.fromEntries(labels.map(label=>[label,hash(worker.status.components[label])?worker.status.components[label]:null])):emptyComponents(),
    runtime:settings.runtime
  });
  const diagnostics={schemaVersion:2,observedAt:report.observedAt,expiresAt:report.expiresAt,controlVersion:pointer.version,
    bootstrap,watcher:worker.diagnostic?watcherEvidence(worker):emptyWatcher(),native:native?.publication??null,recovery,host};
  // Independent writes: a failed status path never suppresses diagnostics, or vice versa.
  const config=path.join(settings.nativeRoot,'Server','Config');
  const writes=await Promise.allSettled([
    writeJson(path.join(settings.root,'host-status.json'),report),
    Promise.resolve().then(()=>atomic(path.join(config,'kesas-host-status.zip'),statusZip(report))),
    Promise.resolve().then(()=>atomic(path.join(config,'kesas-host-diagnostics.zip'),diagnosticsZip(diagnostics)))
  ]);
  if(writes.some(value=>value.status==='rejected'))throw new Error('Host report publication failed');
  return {report,diagnostics};
}

// The caller supplies actual pinned health, never NativeAgent.health() progress.
// Failures reject with a fixed code; callers keep their independent interval alive.
export async function publishHostReport(settings,options={}) {
  const key=path.resolve(settings.root);
  if(inFlight.has(key))return inFlight.get(key);
  const work=reportOnce(settings,options);
  inFlight.set(key,work);
  try {return await work;} finally {if(inFlight.get(key)===work)inFlight.delete(key);}
}
