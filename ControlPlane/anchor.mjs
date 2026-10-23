// Fixed trust anchor: fixed repository, archive validator, lease gate and loader.
// Business logic runs only from independently authenticated protected releases.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import {fork,execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {ControlEngine,validatePointer,verifyRelease} from './engine.mjs';
import {GitHubChannel} from './github.mjs';
import {readJson,writeJson,noLinks,atomic,statusZip,diagnosticsZip} from './public-status.mjs';
import {validateHostReport} from './contract.mjs';
import {controlHistory} from './control-evidence.mjs';
import {brokerRecoveryState,stopIdleControlChild,workerRecoveryState} from './supervision.mjs';
const execute=promisify(execFile),pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const settings=await readJson(process.argv[process.argv.indexOf('--settings')+1]);
const managed=path.join(settings.root,'managed'),channel=new GitHubChannel();
const engine=new ControlEngine({...settings,root:managed,extractor:path.join(settings.root,'bootstrap','Expand-ControlBundle.ps1'),diagnosticsRoot:path.join(settings.root,'native-cache','control-diagnostics')});
let child=null,childExit=null,heartbeat=0,childReady=false,observations=0,pointer=null,stopping=false,polling=false,pendingPoll=null,reason=null,currentTransaction=null;
let childStarted=0,restarts=0,nextRecovery=0,recovering=false,fallbackReporting=false,publisherChild=null,publisherExit=null,publisherStarted=0,publisherVersion=null,publisherSeed=false;
const failedPublisherVersions=new Set();
let preferredPublisher=null,nextPublisher=0,publisherRestarts=0;
let nextWorkerCheck=0,workerStarted=Date.now(),workerRestarts=0,workerRetryAt=0;
function requireRunning(){if(stopping)throw new Error('Anchor is stopping');}
const verified=new Set();
async function verify(value){
  const key=JSON.stringify(validatePointer(value));
  if(!verified.has(key)){await verifyRelease(managed,value);verified.add(key);}
}
const safeRead=(file,fallback=null)=>readJson(file,fallback).catch(()=>fallback);
async function privateError(stage,error){
  const record={stage,at:new Date().toISOString(),message:String(error?.stack||error).replace(/(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})/g,'[REDACTED]').slice(0,12000)};
  await writeJson(path.join(settings.root,'native-cache',`anchor-${stage}-error.json`),record).catch(()=>{});
  if(stage==='control-publication'){
    const transaction=await engine.state().catch(()=>null);
    if(transaction){
      const directory=path.join(settings.root,'native-cache','control-diagnostics',transaction.id);
      await noLinks(directory);await fs.mkdir(directory,{recursive:true});
      const file=path.join(directory,'anchor-first-error.json');
      try{const handle=await fs.open(file,'wx');try{await handle.writeFile(JSON.stringify(record));await handle.sync();}finally{await handle.close();}}catch(writeError){if(writeError.code!=='EEXIST')throw writeError;}
    }
  }
}
async function idle(ignoreBrokerOwner=false){
  for(const name of (await fs.readdir(path.join(settings.root,'native-cache','results'))).filter(name=>/^[a-f0-9]{64}\.json$/.test(name))){
    const result=await readJson(path.join(settings.root,'native-cache','results',name));
    if(result.phase==='applying'&&Number.isSafeInteger(result.nativePid)&&result.nativePid>0){
      try{process.kill(result.nativePid,0);return false;}catch(error){if(error.code!=='ESRCH')return false;}
    }
  }
  const owner=await safeRead(path.join(settings.root,'native-cache','owner.json'));
  if(owner&&(!ignoreBrokerOwner||owner.pid!==child?.pid)){try{process.kill(owner.pid,0);return false;}catch(error){if(error.code!=='ESRCH')return false;}}
  const {stdout}=await execute(settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(settings.root,'bootstrap','Probe-NativeIdle.ps1'),'-NativeRoot',settings.nativeRoot],{windowsHide:true,timeout:15000,maxBuffer:16384});
  // A failed application journal blocks replay, not replacement of idle control code.
  // Probe-NativeIdle also checks journal; for that case use the fixed lease-only probe.
  if(stdout.trim()==='idle')return true;
  const result=await execute(settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(settings.root,'bootstrap','Manage-ControlWorker.ps1'),'-Operation','lease'],{windowsHide:true,timeout:15000,maxBuffer:16384});
  return result.stdout.trim()==='idle';
}
async function task(operation){
  return execute(settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(settings.root,'bootstrap','Manage-ControlWorker.ps1'),'-Operation',operation],{windowsHide:true,timeout:20000,maxBuffer:16384});
}
async function launch(value){
  requireRunning();
  if(child&&childExit===null)throw new Error('Previous broker has not drained');
  await verify(value);
  const file=path.join(managed,'releases',value.directory,'ControlPlane','broker-host.mjs');await noLinks(file);
  requireRunning();
  childExit=null;heartbeat=0;childReady=false;observations=0;
  childStarted=Date.now();
  let output='';
  child=fork(file,['--settings',path.join(settings.root,'host.json')],{execPath:settings.node,cwd:settings.botRoot,windowsHide:true,stdio:['ignore','pipe','pipe','ipc'],env:{...process.env,APF_SERVER_TASK:'KESAS Server'}});
  for(const stream of [child.stdout,child.stderr])stream.on('data',bytes=>{const remaining=16384-Buffer.byteLength(output);if(remaining>0)output+=bytes.subarray(0,remaining).toString('utf8');});
  child.on('message',message=>{if(message?.type==='broker-health'&&message.version===value.version&&message.sha256===value.bundleSha256){heartbeat=Date.now();childReady=message.ready===true;if(childReady)observations++;}});
  child.on('error',error=>{if(!child.pid)childExit=-1;privateError('broker-startup',error).catch(()=>{});});
  child.on('exit',code=>{childExit=Number.isInteger(code)?code:-1;childReady=false;privateError('broker-exit',new Error(`exit=${childExit}; ${output}`)).catch(()=>{});});
}
async function drain(value){
  const state=await readJson(path.join(settings.botRoot,'state','kesas-update-state.json'));
  if(state.schemaVersion!==1||!['idle','failed'].includes(state.status))return false;
  await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:value.bundleSha256});
  let brokerDrain=false;
  const deadline=Date.now()+120000;
  while(Date.now()<deadline){
    const worker=await task('state');
    const current=await readJson(path.join(settings.botRoot,'state','kesas-update-state.json'));
    if(!['idle','failed'].includes(current.status)&&!brokerDrain){
      await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:null});return false;
    }
    if(worker.stdout.trim()!=='Running'&&!brokerDrain){brokerDrain=true;child?.send?.({type:'drain'});}
    if(worker.stdout.trim()!=='Running'&&(!child||childExit!==null)&&await idle())return true;
    await pause(500);
  }
  await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:null});
  return false; // Never terminate a helper or guess that an owner is dead.
}
async function recoverChild(){
  if(recovering||polling||stopping||Date.now()<nextRecovery)return;
  const state=brokerRecoveryState({alive:!!child,exitCode:childExit,ready:childReady,heartbeat,startedAt:childStarted,restarts,now:Date.now(),retryAt:nextRecovery});
  if(!['start','drain'].includes(state.action))return;
  recovering=true;
  try{
    if(state.action==='drain'){
      const outcome=await stopIdleControlChild({
        idle:()=>idle(true),
        drain:async()=>{await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:pointer.bundleSha256});if(child?.connected)child.send({type:'drain'});await pause(2000);},
        stopWorker:async()=>{await task('stop');return (await task('state')).stdout.trim()!=='Running';},
        terminate:async()=>{if(child&&childExit===null){child.kill('SIGTERM');const deadline=Date.now()+10000;while(childExit===null&&Date.now()<deadline)await pause(100);if(childExit===null)throw new Error('Control broker did not exit');}}
      });
      if(outcome!=='control-child-stopped'){reason=outcome;nextRecovery=Date.now()+30000;return;}
    }
    if(!await idle())return;
    restarts++;nextRecovery=Date.now()+Math.min(300000,5000*2**Math.min(restarts,6));
    await startup(pointer);reason=null;
  }catch(error){reason='broker-recovery-failed';await privateError('broker-recovery',error);nextRecovery=Date.now()+30000;}
  finally{recovering=false;}
}
async function recoverWorker(){
  if(stopping||polling||recovering||Date.now()<nextWorkerCheck)return;
  nextWorkerCheck=Date.now()+15000;
  try{
    const running=(await task('state')).stdout.trim()==='Running';
    const [status,diagnostics]=await Promise.all(['worker-status.json','worker-diagnostics.json'].map(name=>safeRead(path.join(settings.root,name))));
    const policy=workerRecoveryState({running,status,diagnostics,version:pointer.version,sha256:pointer.bundleSha256,startedAt:workerStarted,restarts:workerRestarts,retryAt:workerRetryAt});
    if(policy.action==='wait')return;
    workerRetryAt=Date.now()+policy.delay;
    if(!await idle(true))return;
    if(policy.action==='restart')await task('stop');
    if(!await idle(true))return;
    await task('start');workerStarted=Date.now();workerRestarts++;
    await writeJson(path.join(settings.root,'native-cache','worker-recovery.json'),{at:new Date().toISOString(),version:pointer.version,action:policy.action,restarts:workerRestarts});
  }catch(error){workerRetryAt=Date.now()+30000;await privateError('worker-recovery',error);}
}
async function stopObservationTransport(){
  if(!publisherChild||publisherExit!==null)return true;
  if(publisherChild.connected)publisherChild.disconnect();
  let deadline=Date.now()+20000;
  while(publisherExit===null&&Date.now()<deadline)await pause(100);
  if(publisherExit===null){publisherChild.kill('SIGTERM');deadline=Date.now()+10000;while(publisherExit===null&&Date.now()<deadline)await pause(100);}
  return publisherExit!==null;
}
async function observationTransport(forceSeed=false){
  if(stopping||!pointer)return;
  forceSeed=forceSeed||failedPublisherVersions.has(pointer.bundleSha256);
  const different=publisherVersion!==pointer.bundleSha256;
  if(!different&&Date.now()<nextPublisher)return;
  if(publisherChild&&publisherExit===null&&!different&&!forceSeed)return;
  if(publisherChild&&publisherExit===null){
    if(!await stopObservationTransport())throw new Error('Observer shutdown remains blocked');
  }
  let file=path.join(settings.root,'bootstrap','observation-publisher.mjs');publisherSeed=true;
  if(!forceSeed){
    const release=path.join(managed,'releases',pointer.directory);
    const manifest=await safeRead(path.join(release,'CONTROL.json'));
    if(manifest?.files?.some(row=>row.path==='ControlPlane/observation-publisher.mjs')){await verify(pointer);file=path.join(release,'ControlPlane','observation-publisher.mjs');publisherSeed=false;}
  }
  await noLinks(file);publisherExit=null;publisherStarted=Date.now();publisherVersion=pointer.bundleSha256;nextPublisher=0;
  publisherChild=fork(file,['--settings',path.join(settings.root,'host.json')],{execPath:settings.node,cwd:settings.root,windowsHide:true,stdio:['ignore','ignore','ignore','ipc'],env:{...process.env}});
  publisherChild.on('error',error=>{if(!publisherChild.pid)publisherExit=-1;privateError('observation-process',error).catch(()=>{});});publisherChild.on('exit',code=>{publisherExit=Number.isInteger(code)?code:-1;publisherRestarts++;nextPublisher=Date.now()+Math.min(300000,5000*2**Math.min(publisherRestarts,6));});
}
async function startup(value){
  requireRunning();
  await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:null});
  if(!child||childExit!==null)await launch(value);
  requireRunning();
  // A worker launch failure cannot disable the independent broker/repair path.
  // Retrying startup must not fork additional brokers beside the healthy one.
  await task('start').catch(error=>privateError('worker-startup',error));
  workerStarted=Date.now();
  const deadline=Date.now()+90000;
  while(Date.now()<deadline){
    requireRunning();
    if(childExit!==null)return false;
    if(childReady&&observations>=2&&Date.now()-heartbeat<15000){
      await writeJson(path.join(managed,'verified-active.json'),{schemaVersion:1,version:value.version,bundleSha256:value.bundleSha256,verifiedAt:new Date().toISOString()});return true;
    }
    await pause(500);
  }
  return false;
}
async function save(value){
  value.updatedAt=new Date().toISOString();await engine.save(value);return value;
}
async function finishStage(transaction){
  if(!transaction.to){const value=await engine.stage(transaction);verified.add(JSON.stringify(value));return value;}
  const final=path.join(managed,'releases',transaction.to.directory);
  if(!await fs.stat(final).catch(error=>{if(error.code==='ENOENT')return null;throw error;})){
    if(!/^[a-f0-9]{64}-[a-f0-9-]{36}$/.test(transaction.stage||''))throw new Error('Incomplete protected staging identity');
    const staged=path.join(managed,'staging',transaction.stage);await noLinks(staged);await noLinks(final);
    await fs.rename(staged,final);
  }
  await verify(transaction.to);return transaction.to;
}
async function rollback(transaction){
  requireRunning();
  const selected=validatePointer(await readJson(path.join(managed,'active.json')));
  if(!await drain(selected)){reason='broker-drain-blocked';return;}
  await verify(transaction.from);
  requireRunning();
  transaction.phase='rolling-back';await save(transaction);
  await writeJson(path.join(managed,'active.json'),transaction.from);pointer=transaction.from;
  transaction.phase='rolled-back';transaction.failure='broker-startup-failed';await save(transaction);
  await startup(pointer);reason='broker-startup-failed';
}
async function poll(){
  if(polling||recovering||stopping)return;polling=true;
  try{
    let transaction=await engine.state();
    if(transaction&&['activating','health-check','rolling-back'].includes(transaction.phase)){
      // A loader death cannot replay an application. Finish only this control pointer.
      if(!transaction.from||!transaction.to)throw new Error('Incomplete broker activation');
      pointer=validatePointer(await readJson(path.join(managed,'active.json')));
      if(pointer.bundleSha256!==transaction.from.bundleSha256&&pointer.bundleSha256!==transaction.to.bundleSha256)throw new Error('Broker pointer conflicts with journal');
      // Recording two fresh startup observations changes no native application
      // state and must not race the broker's short-lived request-observation lease.
      if(transaction.phase!=='rolling-back'&&pointer.bundleSha256===transaction.to.bundleSha256&&childReady&&observations>=2&&Date.now()-heartbeat<15000){transaction.phase='healthy';transaction.failure=null;transaction.completedAt=new Date().toISOString();await save(transaction);return;}
      if(!await idle())return;
      if(transaction.phase==='rolling-back'){await rollback(transaction);return;}
      if(pointer.bundleSha256===transaction.to.bundleSha256){await rollback(transaction);return;}
    }
    if(!transaction||['healthy','blocked','rolled-back'].includes(transaction.phase)){
      const floor=Math.max(pointer.sequence,transaction?.descriptor?Number(transaction.descriptor.version.slice(8)):0);
      const intent=await channel.intent(floor);if(intent.absent||intent.ignored)return;
      transaction={schemaVersion:1,transactionVersion:1,id:intent.descriptorSha256,descriptor:intent.descriptor,commit:intent.commit,phase:'accepted',from:pointer,to:null,failure:null,acceptedAt:new Date().toISOString()};
      const previous=await safeRead(path.join(managed,'transactions',`${transaction.id}.json`));
      if(previous){if(['healthy','blocked','rolled-back'].includes(previous.phase))return;transaction=previous;}
    }
    currentTransaction=transaction;await save(transaction);
    const next=await finishStage(transaction);await verify(next);
    requireRunning();
    transaction=await engine.state();transaction.to=next;transaction.phase='waiting-existing-guard';await save(transaction);
    if(!await drain(pointer)){reason='broker-drain-blocked';return;}
    requireRunning();
    transaction.phase='activating';await save(transaction);
    await writeJson(path.join(managed,'fallback.json'),pointer);
    await writeJson(path.join(managed,'active.json'),next);pointer=next;
    transaction.phase='health-check';await save(transaction);
    if(!await startup(next)){await rollback(transaction);return;}
    transaction.phase='healthy';transaction.failure=null;transaction.completedAt=new Date().toISOString();await save(transaction);
    await writeJson(path.join(managed,'verified-active.json'),{schemaVersion:1,version:next.version,bundleSha256:next.bundleSha256,verifiedAt:new Date().toISOString()});reason=null;
  }catch(error){
    if(stopping)return; // Preserve the durable phase for the replacement loader.
    reason=error.controlReason||'broker-update-failed';await privateError('control-publication',error);
    const durable=await engine.state().catch(()=>null);
    if(durable&&!['activating','health-check','rolling-back'].includes(durable.phase))
      await save({...durable,phase:'blocked',failure:error.controlReason||'broker-update-failed'}).catch(()=>{});
  }
  finally{polling=false;}
}
async function fallback(){
  if(fallbackReporting)return;fallbackReporting=true;
  try{
    if(!pointer)return;
    const existing=await safeRead(path.join(settings.root,'host-observation.json'));
    if(childReady&&Date.now()-heartbeat<25000&&Date.parse(existing?.status?.expiresAt)>Date.now()+5000)return;
    const labels=['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'];
    const feed=await safeRead(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'));
    const pack=await safeRead(path.join(settings.nativeRoot,'Server','Config','APFPack.json'));
    const transaction=await engine.state().catch(()=>null),history=await controlHistory(managed,transaction).catch(()=>({attempted:null,lastFailure:null,recoveryAction:null}));
    const proof=await safeRead(path.join(managed,'verified-active.json'));
    const currentWorker=await safeRead(path.join(settings.root,'worker-status.json'));
    const workerFresh=currentWorker?.controlSha256===pointer.bundleSha256&&Date.parse(currentWorker.expiresAt)>Date.now()&&Date.now()-Date.parse(currentWorker.observedAt)<=30000;
    const policy=brokerRecoveryState({alive:!!child,exitCode:childExit,ready:childReady,heartbeat,startedAt:childStarted,restarts,now:Date.now(),retryAt:nextRecovery});
    const processState={brokerState:child?policy.brokerState:'starting',brokerExitCode:childExit,restarts,lastHeartbeatAt:heartbeat?new Date(heartbeat).toISOString():null,retryAt:nextRecovery?new Date(nextRecovery).toISOString():null};
    const version=value=>typeof value==='string'&&/^kesas-rc[1-9][0-9]{0,14}$/.test(value)?value:null;
    const report={schemaVersion:1,contractVersion:1,bootstrapVersion:1,observedAt:new Date().toISOString(),expiresAt:new Date(Date.now()+30000).toISOString(),capabilities:[],control:{},
      application:{clientVersion:version(feed?.version),packVersion:version(pack?.packVersion),status:'unknown',phase:null,pendingVersion:null,health:'unknown',lastSuccess:null,failure:null},
      components:Object.fromEntries(labels.map(label=>[label,null])),runtime:settings.runtime};
    report.control={version:pointer.version,sequence:pointer.sequence,sha256:pointer.bundleSha256,status:'blocked',phase:'broker-unavailable',pendingVersion:transaction&&!['healthy','blocked','rolled-back'].includes(transaction.phase)?transaction.descriptor.version:null,lastSuccess:proof?.version===pointer.version&&proof.bundleSha256===pointer.bundleSha256?pointer.version:null,rollback:null,failure:reason||'broker-report-unavailable',...history,process:processState};
    report.application.health='unknown';report.capabilities=['control-plane-v1','host-report-v1','versioned-broker-v1','independent-report-v1'];
    for(const key of Object.keys(report.components))report.components[key]=null;
    const diagnostics={schemaVersion:2,observedAt:report.observedAt,expiresAt:report.expiresAt,controlVersion:pointer.version,bootstrap:{nativeAgentSha256:null,publisherWrapperSha256:null,nativeRecoveryVersion:0},watcher:{stage:null,reason:null,version:null,sourceCommit:null,manifestSha256:null,archiveSha256:null},native:null,recovery:null,
      host:{stage:'broker',reason:childExit!==null?'broker-child-failed':'broker-not-ready',worker:workerFresh?'running':'stale',broker:childExit!==null?'failed':'starting',inventory:'unknown',leaseActive:null,journalPhase:null,exitCode:childExit}};
    if(childReady&&Date.now()-heartbeat<25000&&Date.parse((await safeRead(path.join(settings.root,'host-observation.json')))?.status?.expiresAt)>Date.now()+5000)return;
    await writeJson(path.join(settings.root,'host-observation.json'),{schemaVersion:1,status:report,diagnostics});
    await writeJson(path.join(settings.root,'host-status.json'),report);
    const results=await Promise.allSettled([
      atomic(path.join(settings.nativeRoot,'Server','Config','kesas-host-status.zip'),statusZip(report)),
      atomic(path.join(settings.nativeRoot,'Server','Config','kesas-host-diagnostics.zip'),diagnosticsZip(diagnostics))
    ]);
    if(results.some(value=>value.status==='rejected'))await privateError('http-fallback-publication',new Error('HTTP_PUBLICATION_UNAVAILABLE'));
  }catch(error){await privateError('fallback-report',error);}
  finally{fallbackReporting=false;}
}
// The wrapper owns the OS lease. A process-owned protected lease additionally
// prevents an orphaned Node process and a replacement task from supervising twice.
const ownerFile=path.join(managed,'anchor-owner.json'),owner={pid:process.pid,token:crypto.randomUUID()};
await noLinks(ownerFile);
const previousOwner=await safeRead(ownerFile);
if(previousOwner){
  if(!Number.isSafeInteger(previousOwner.pid)||previousOwner.pid<=0)throw new Error('Invalid anchor owner');
  try{process.kill(previousOwner.pid,0);throw new Error('Anchor already running');}catch(error){if(error.code!=='ESRCH')throw error;}
  await fs.unlink(ownerFile);
}
const ownerHandle=await fs.open(ownerFile,'wx');try{await ownerHandle.writeFile(JSON.stringify(owner));await ownerHandle.sync();}finally{await ownerHandle.close();}
pointer=validatePointer(await safeRead(path.join(managed,'active.json'))||await readJson(path.join(settings.root,'active.json')));
const timer=setInterval(()=>fallback().catch(()=>{}),10000);
const parent=process.ppid;
const parentTimer=setInterval(()=>{try{process.kill(parent,0);}catch(error){if(error.code==='ESRCH'){stopping=true;child?.send?.({type:'drain'});}}},1000);
process.on('SIGTERM',()=>{stopping=true;});process.on('SIGINT',()=>{stopping=true;});
async function initialize(){
  let selected=await safeRead(path.join(managed,'active.json'));
  if(!selected){
    let transaction=await engine.state();
    if(!transaction){
      const intent=await channel.intent(0);
      if(intent.absent||intent.ignored)throw new Error('Initial authenticated control intent is unavailable');
      transaction={schemaVersion:1,transactionVersion:1,id:intent.descriptorSha256,descriptor:intent.descriptor,commit:intent.commit,phase:'accepted',from:null,to:null,acceptedAt:new Date().toISOString()};
    }
    selected=await finishStage(transaction);await verify(selected);
    requireRunning();
    await writeJson(path.join(managed,'active.json'),selected);
    transaction=await engine.state();transaction.to=selected;transaction.from=selected;transaction.phase='health-check';await save(transaction);
  }
  pointer=validatePointer(selected);await startup(pointer);
}
let nextPoll=0,nextInitialize=0,initialized=false;
await observationTransport(true).catch(error=>privateError('observation-startup',error));
try{
  while(!stopping){
    if(!initialized&&!polling&&Date.now()>=nextInitialize){
      nextInitialize=Date.now()+30000;
      try{await initialize();initialized=true;}catch(error){reason='broker-startup-failed';await privateError('initialization',error);}
    }
    if(!polling&&Date.now()>=nextPoll){nextPoll=Date.now()+60000;pendingPoll=poll().catch(error=>privateError('poll',error));}
    if(!polling)await recoverChild();
    if(initialized&&!polling)await recoverWorker();
    if(publisherExit!==null&&!publisherSeed&&publisherVersion)failedPublisherVersions.add(publisherVersion);
    if(initialized&&preferredPublisher!==pointer.bundleSha256){
      preferredPublisher=pointer.bundleSha256;
      const manifest=await safeRead(path.join(managed,'releases',pointer.directory,'CONTROL.json'));
      if(publisherSeed&&!failedPublisherVersions.has(pointer.bundleSha256)&&manifest?.files?.some(row=>row.path==='ControlPlane/observation-publisher.mjs'))publisherVersion=null;
    }
    if(publisherVersion!==pointer.bundleSha256||publisherExit!==null)await observationTransport().catch(error=>privateError('observation-startup',error));
    else if(!publisherSeed&&Date.now()-publisherStarted>90000){
      const transport=await safeRead(path.join(settings.root,'native-cache','observation-transport.json'));
      if(transport?.published!==true||Date.now()-Date.parse(transport.observedAt)>60000){failedPublisherVersions.add(pointer.bundleSha256);await observationTransport(true).catch(error=>privateError('observation-fallback',error));}
    }
    await pause(500);
  }
}finally{
  stopping=true;
  // Staging may still be awaiting GitHub/npm when the wrapper disappears.
  // Keep ownership until that operation stops, then drain its last broker.
  if(pendingPoll)await pendingPoll;
  await drain(pointer);
  // Do not release ownership while an orphaned observer can still sign or PUT.
  while(!await stopObservationTransport()){await privateError('observation-stop-blocked',new Error('Observer remains alive; ownership retained'));await pause(10000);}
  clearInterval(timer);clearInterval(parentTimer);
  if((await safeRead(ownerFile))?.token===owner.token)await fs.unlink(ownerFile);
}
