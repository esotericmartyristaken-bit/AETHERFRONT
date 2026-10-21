// One-time installed, fixed contract-1 safety kernel. Remote bundles cannot
// replace this supervisor or its frozen validation dependencies.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fork, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { verifyRelease, validatePointer, validateTransaction } from './engine.mjs';
import { readJson, writeJson, noLinks, atomic, statusZip } from './public-status.mjs';
import { validateHostReport } from './contract.mjs';

const execute=promisify(execFile);
const pause=milliseconds=>new Promise(resolve=>setTimeout(resolve,milliseconds));
export class Supervisor {
  constructor(settings,options={}) {this.settings=settings;this.root=settings.root;this.launch=options.launch||this.launchWorker.bind(this);this.idle=options.idle||this.nativeIdle.bind(this);this.healthMs=options.healthMs??10000;this.startTimeout=options.startTimeout??90000;this.fault=options.fault||(async()=>{});this.stop=false;}
  async save(transaction) {
    validateTransaction(transaction);
    const previous=await readJson(path.join(this.root,'transactions',`${transaction.id}.json`),null);
    transaction.revision=Math.max(previous?.revision||0,transaction.revision||0)+1;
    transaction.updatedAt=new Date().toISOString();
    await writeJson(path.join(this.root,'transactions',`${transaction.id}.json`),transaction);
    await writeJson(path.join(this.root,'transaction.json'),transaction);
  }
  async nativeIdle() {
    const native=await readJson(path.join(this.settings.botRoot,'state','kesas-update-state.json'));
    if(native.schemaVersion!==1||native.status!=='idle')return false;
    const {stdout}=await execute(this.settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(this.root,'bootstrap','Probe-NativeIdle.ps1'),'-NativeRoot',this.settings.nativeRoot],{windowsHide:true,timeout:15000,maxBuffer:16384});
    return stdout.trim()==='idle';
  }
  async clearMaintenance(transaction) {
    const file=path.join(this.root,'maintenance.json');
    const marker=await readJson(file,null);
    if(marker?.transactionId===transaction.id){await noLinks(file);await fs.unlink(file);}
  }
  async recoverActivation() {
    let transaction=await readJson(path.join(this.root,'transaction.json'),null);
    if(transaction)validateTransaction(transaction);
    if(transaction) {
      const history=await readJson(path.join(this.root,'transactions',`${transaction.id}.json`),null);
      if(history&&(history.revision||0)>(transaction.revision||0)) {
        validateTransaction(history);transaction=history;await writeJson(path.join(this.root,'transaction.json'),transaction);
      }
    }
    if(transaction&&['healthy','rolled-back','blocked'].includes(transaction.phase)) {
      const active=validatePointer(await readJson(path.join(this.root,'active.json')));
      const expected=transaction.phase==='healthy'?transaction.to:transaction.from;
      if(expected&&active.bundleSha256===expected.bundleSha256)await this.clearMaintenance(transaction);
      return null;
    }
    if(!transaction || !['activating','health-check','rolling-back'].includes(transaction.phase))return null;
    if(transaction.schemaVersion!==1 || transaction.transactionVersion!==1)throw new Error('Unsupported activation journal');
    validatePointer(transaction.from);validatePointer(transaction.to);
    if(!await this.idle())return {waiting:true};
    if(transaction.phase==='rolling-back'){await this.rollback(transaction,transaction.failure||'control-startup-failed');return null;}
    const active=validatePointer(await readJson(path.join(this.root,'active.json')));
    if(active.bundleSha256!==transaction.from.bundleSha256 && active.bundleSha256!==transaction.to.bundleSha256)throw new Error('Activation pointer conflicts with journal');
    try {await verifyRelease(this.root,transaction.to);}
    catch {await this.rollback(transaction,'control-verification-failed');return null;}
    transaction.phase='health-check';await this.save(transaction);
    // Journal always precedes the one-file pointer replacement. On restart,
    // the journal distinguishes either side without re-running publication.
    if(active.bundleSha256!==transaction.to.bundleSha256)await writeJson(path.join(this.root,'active.json'),transaction.to);
    return transaction;
  }
  async rollback(transaction,failure) {
    await verifyRelease(this.root,transaction.from);
    transaction.phase='rolling-back';transaction.failure=failure;
    await this.save(transaction);
    await writeJson(path.join(this.root,'active.json'),transaction.from);
    await this.fault('rollback-pointer-written');
    transaction.phase='rolled-back';transaction.failure=failure;
    await this.save(transaction);await this.clearMaintenance(transaction);
  }
  launchWorker(pointer) {
    const file=path.join(this.root,'releases',pointer.directory,'ControlPlane','worker.mjs');
    return fork(file,['--settings',path.join(this.root,'host.json')],{cwd:this.settings.botRoot,execPath:this.settings.node,windowsHide:true,stdio:['ignore','ignore','ignore','ipc'],env:{...process.env,APF_CONTROL_ROOT:this.root}});
  }
  async waitOldBot() {
    // A coordinator death can leave its bot/helper alive. Never kill an
    // applying helper. Its IPC disconnect handler drains before releasing.
    const file=path.join(this.settings.botRoot,'state','bot-instance.lock');
    let reported=0;
    for(;;) {
      const owner=await readJson(file,null);
      if(!owner)return;
      if(!Number.isInteger(owner.pid)||owner.pid<=0)throw new Error('Uncertain bot ownership');
      try {process.kill(owner.pid,0);}
      catch(error) {if(error.code==='ESRCH')return;throw error;}
      if(Date.now()-reported>10000){await this.reportBlocked('waiting-native-helper').catch(()=>{});reported=Date.now();}
      if(this.stop)return;await pause(1000);
    }
  }
  async reportBlocked(reason) {
    const previous=validateHostReport(await readJson(path.join(this.root,'host-status.json')));
    previous.observedAt=new Date().toISOString();previous.expiresAt=new Date(Date.now()+150000).toISOString();
    previous.control.status='blocked';previous.control.phase=reason;previous.control.failure=reason;
    previous.application.status='applying';previous.application.health='unknown';
    validateHostReport(previous);
    await writeJson(path.join(this.root,'host-status.json'),previous);
    await atomic(path.join(this.settings.nativeRoot,'Server','Config','kesas-host-status.zip'),statusZip(previous));
  }
  async once() {
    await this.waitOldBot();
    const activation=await this.recoverActivation();if(activation?.waiting)return 'waiting';
    const pointer=validatePointer(await readJson(path.join(this.root,'active.json')));
    await verifyRelease(this.root,pointer);
    const child=this.launch(pointer);
    const started=Date.now();let readyAt=null,lastHeartbeat=0,firstObservation=0,lastObservation=0,handoff=false,exit=null,validated=false;
    child.on('message',message=>{
      const observation=message?.observedAt;
      const fresh=Number.isFinite(observation)&&observation>=started-5000&&observation<=Date.now()+5000&&Date.now()-observation<90000;
      if(message?.type==='ready' && message.version===pointer.version&&fresh){readyAt??=Date.now();firstObservation ||= observation;lastObservation=Math.max(lastObservation,observation);lastHeartbeat=Date.now();}
      if(message?.type==='heartbeat' && message.version===pointer.version&&fresh){lastHeartbeat=Date.now();lastObservation=Math.max(lastObservation,observation);}
      if(message?.type==='progress'&&message.version===pointer.version&&validated)lastHeartbeat=Date.now();
      if(message?.type==='health-failed'){lastHeartbeat=Date.now();if(!validated){readyAt=null;firstObservation=lastObservation=0;}}
      if(message?.type==='handoff')handoff=true;
    });
    child.on('error',()=>{exit={code:-1};});child.on('exit',code=>{exit={code};});
    while(!exit && !this.stop) {
      if(readyAt && Date.now()-lastHeartbeat<15000 && !validated && lastObservation>firstObservation && Date.now()-readyAt>=this.healthMs) {
        await writeJson(path.join(this.root,'verified-active.json'),{schemaVersion:1,version:pointer.version,bundleSha256:pointer.bundleSha256,verifiedAt:new Date().toISOString(),observedAt:new Date(lastObservation).toISOString()});
        if(activation) {
          activation.phase='healthy';activation.failure=null;activation.completedAt=new Date().toISOString();
          await writeJson(path.join(this.root,'fallback.json'),activation.from);
          await this.save(activation);await this.clearMaintenance(activation);
        }
        validated=true;
      }
      if((!validated && Date.now()-started>this.startTimeout) || (readyAt && Date.now()-lastHeartbeat>30000)) {
        // A worker may be doing an existing application transaction. Request a
        // drained exit; do not forcibly terminate it or its publisher.
        child.send?.({type:'shutdown-when-idle'});
        if(!await this.idle()){await this.reportBlocked('native-status-unavailable').catch(()=>{});await pause(1000);continue;}
        // This is our own coordinator child, not a game or publisher. Once
        // native work is provably idle, a non-responsive replacement can be
        // stopped so rollback does not depend on its cooperation. Its bot
        // receives IPC disconnect and drains independently.
        child.kill();
        if(Date.now()-started>this.startTimeout+120000)break;
      }
      await pause(250);
    }
    if(this.stop) {child.send?.({type:'shutdown-when-idle'});return 'stopping';}
    if(!exit)throw new Error('Control worker did not drain; operator review required');
    await this.waitOldBot();
    if(activation && !validated){await this.rollback(activation,'control-startup-failed');return 'rolled-back';}
    if(handoff && exit.code===75)return 'handoff';
    return 'restart';
  }
  async run() {while(!this.stop){const result=await this.once();if(result==='waiting'||result==='restart')await pause(5000);}}
}
async function acquireOwner(root) {
  const file=path.join(root,'supervisor-owner.json');await noLinks(file);
  for(let attempt=0;attempt<2;attempt++) {
    try {const handle=await fs.open(file,'wx');try{await handle.writeFile(JSON.stringify({pid:process.pid}));await handle.sync();}finally{await handle.close();}return async()=>{const owner=await readJson(file);if(owner.pid===process.pid)await fs.unlink(file);};}
    catch(error) {
      if(error.code!=='EEXIST')throw error;
      const owner=await readJson(file);
      if(!Number.isInteger(owner.pid)||owner.pid<=0)throw new Error('Uncertain supervisor lease');
      try{process.kill(owner.pid,0);throw new Error('Supervisor already running');}
      catch(probe){if(probe.code!=='ESRCH')throw probe;}
      await fs.unlink(file);
    }
  }
  throw new Error('Supervisor lease unavailable');
}
if(process.argv[1] && path.resolve(process.argv[1])===path.resolve(import.meta.filename)) {
  if(Number(process.env.APF_SUPERVISOR_PARENT_PID)!==process.ppid)throw new Error('Start supervisor through its OS-lease PowerShell wrapper');
  const settings=await readJson(process.argv[process.argv.indexOf('--settings')+1]);
  const release=await acquireOwner(settings.root);const supervisor=new Supervisor(settings);
  process.on('SIGINT',()=>{supervisor.stop=true;});process.on('SIGTERM',()=>{supervisor.stop=true;});
  try {await supervisor.run();}
  catch {process.stderr.write('Control supervisor blocked; journals preserved.\n');process.exitCode=1;}
  finally {await release();}
}
