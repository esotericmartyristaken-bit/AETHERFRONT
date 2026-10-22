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
const execute=promisify(execFile),pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const settings=await readJson(process.argv[process.argv.indexOf('--settings')+1]);
const managed=path.join(settings.root,'managed'),channel=new GitHubChannel();
const engine=new ControlEngine({...settings,root:managed,extractor:path.join(settings.root,'bootstrap','Expand-ControlBundle.ps1')});
let child=null,childExit=null,heartbeat=0,childReady=false,observations=0,pointer=null,stopping=false,polling=false,pendingPoll=null,reason=null,currentTransaction=null;
function requireRunning(){if(stopping)throw new Error('Anchor is stopping');}
const verified=new Set();
async function verify(value){
  const key=JSON.stringify(validatePointer(value));
  if(!verified.has(key)){await verifyRelease(managed,value);verified.add(key);}
}
const safeRead=(file,fallback=null)=>readJson(file,fallback).catch(()=>fallback);
async function privateError(stage,error){
  await writeJson(path.join(settings.root,'native-cache','anchor-error.json'),{stage,at:new Date().toISOString(),message:String(error?.stack||error).slice(0,12000)}).catch(()=>{});
}
async function idle(){
  for(const name of (await fs.readdir(path.join(settings.root,'native-cache','results'))).filter(name=>/^[a-f0-9]{64}\.json$/.test(name))){
    const result=await readJson(path.join(settings.root,'native-cache','results',name));
    if(result.phase==='applying'&&Number.isSafeInteger(result.nativePid)&&result.nativePid>0){
      try{process.kill(result.nativePid,0);return false;}catch(error){if(error.code!=='ESRCH')return false;}
    }
  }
  const owner=await safeRead(path.join(settings.root,'native-cache','owner.json'));
  if(owner){try{process.kill(owner.pid,0);return false;}catch(error){if(error.code!=='ESRCH')return false;}}
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
  child=fork(file,['--settings',path.join(settings.root,'host.json')],{execPath:settings.node,cwd:settings.botRoot,windowsHide:true,stdio:['ignore','ignore','ignore','ipc'],env:{...process.env,APF_SERVER_TASK:'KESAS Server'}});
  child.on('message',message=>{if(message?.type==='broker-health'&&message.version===value.version&&message.sha256===value.bundleSha256){heartbeat=Date.now();childReady=message.ready===true;if(childReady)observations++;}});
  child.on('error',error=>{childExit=-1;privateError('broker-startup',error);});
  child.on('exit',code=>{childExit=Number.isInteger(code)?code:-1;childReady=false;});
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
    if(worker.stdout.trim()!=='Running'&&childExit!==null&&await idle())return true;
    await pause(500);
  }
  await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:null});
  return false; // Never terminate a helper or guess that an owner is dead.
}
async function startup(value){
  requireRunning();
  await writeJson(path.join(managed,'drain.json'),{schemaVersion:1,bundleSha256:null});
  if(!child||childExit!==null)await launch(value);
  requireRunning();
  // A worker launch failure cannot disable the independent broker/repair path.
  // Retrying startup must not fork additional brokers beside the healthy one.
  await task('start').catch(error=>privateError('worker-startup',error));
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
  if(polling||stopping)return;polling=true;
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
    reason='broker-update-failed';await privateError('control-publication',error);
    const durable=await engine.state().catch(()=>null);
    if(durable&&!['activating','health-check','rolling-back'].includes(durable.phase))
      await save({...durable,phase:'blocked',failure:'broker-update-failed'}).catch(()=>{});
  }
  finally{polling=false;}
}
async function fallback(){
  if(childReady&&Date.now()-heartbeat<25000)return;
  try{
    if(!pointer)return;
    const labels=['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'];
    const feed=await safeRead(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'));
    const pack=await safeRead(path.join(settings.nativeRoot,'Server','Config','APFPack.json'));
    const version=value=>typeof value==='string'&&/^kesas-rc[1-9][0-9]{0,14}$/.test(value)?value:null;
    const report={schemaVersion:1,contractVersion:1,bootstrapVersion:1,observedAt:new Date().toISOString(),expiresAt:new Date(Date.now()+30000).toISOString(),capabilities:[],control:{},
      application:{clientVersion:version(feed?.version),packVersion:version(pack?.packVersion),status:'unknown',phase:null,pendingVersion:null,health:'unknown',lastSuccess:null,failure:null},
      components:Object.fromEntries(labels.map(label=>[label,null])),runtime:settings.runtime};
    report.control={version:pointer.version,sequence:pointer.sequence,sha256:pointer.bundleSha256,status:'blocked',phase:'broker-unavailable',pendingVersion:null,lastSuccess:null,rollback:null,failure:reason||'broker-unavailable'};
    report.application.health='unknown';report.capabilities=['control-plane-v1','host-report-v1','versioned-broker-v1','independent-report-v1'];
    for(const key of Object.keys(report.components))report.components[key]=null;
    await writeJson(path.join(settings.root,'host-status.json'),report);
    await atomic(path.join(settings.nativeRoot,'Server','Config','kesas-host-status.zip'),statusZip(report));
    await atomic(path.join(settings.nativeRoot,'Server','Config','kesas-host-diagnostics.zip'),diagnosticsZip({schemaVersion:2,observedAt:report.observedAt,expiresAt:report.expiresAt,controlVersion:pointer.version,bootstrap:{nativeAgentSha256:null,publisherWrapperSha256:null,nativeRecoveryVersion:0},watcher:{stage:null,reason:null,version:null,sourceCommit:null,manifestSha256:null,archiveSha256:null},native:null,recovery:null,
      host:{stage:'broker',reason:'broker-child-failed',worker:'stale',broker:'failed',inventory:'unknown',leaseActive:null,journalPhase:null,exitCode:childExit}}));
  }catch(error){await privateError('fallback-report',error);}
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
let nextPoll=0,nextRestart=0,initialized=false;
try{
  while(!stopping){
    if(!initialized){
      try{await initialize();initialized=true;}catch(error){reason='broker-startup-failed';await privateError('initialization',error);await pause(5000);continue;}
    }
    if(!polling&&Date.now()>=nextPoll){nextPoll=Date.now()+60000;pendingPoll=poll().catch(error=>privateError('poll',error));}
    if(childExit!==null&&!polling&&Date.now()>=nextRestart&&await idle()){nextRestart=Date.now()+30000;await startup(pointer);}
    await pause(500);
  }
}finally{
  stopping=true;
  // Staging may still be awaiting GitHub/npm when the wrapper disappears.
  // Keep ownership until that operation stops, then drain its last broker.
  if(pendingPoll)await pendingPoll;
  await drain(pointer);
  clearInterval(timer);clearInterval(parentTimer);
  if((await safeRead(ownerFile))?.token===owner.token)await fs.unlink(ownerFile);
}
