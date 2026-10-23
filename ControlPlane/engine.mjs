import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { sequence, exact, HASH, validateControlDescriptor, validateControlManifest, requireValue } from './contract.mjs';
import { digest, noLinks, atomic, writeJson, readJson, fileHash } from './public-status.mjs';
import { GitHubChannel, parseStrictJson } from './github.mjs';

const execute = promisify(execFile);
const TERMINAL = new Set(['healthy','rolled-back','blocked']);
const STAGE_CODES = new Set(['download','archive-write','extract','dependencies','inventory','verify','activate','startup']);
const OUTPUT_LIMIT = 16 * 1024;
function sanitized(value) {
  const source=Buffer.isBuffer(value)?value.subarray(0,OUTPUT_LIMIT*4).toString('utf8'):String(value??'').slice(0,OUTPUT_LIMIT*4);
  const clean=source.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g,'').replace(/[\x00-\x08\x0b-\x1f\x7f]/g,'')
    .replace(/(?:https?:\/\/|file:\/\/)[^\s"'<>]+/gi,'[redacted-url]')
    .replace(/\b(?:[A-Z]:[\\/]|\\\\)[^\r\n"'<>]*/gi,'[redacted-path]')
    .replace(/(^|[\s"'=])\/(?:[^\s"'<>]+\/)[^\s"'<>]*/g,'$1[redacted-path]')
    .replace(/\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b/g,'[redacted-token]')
    .replace(/\b(authorization|token|password|secret)\s*[:=]\s*[^\r\n]+/gi,'$1=[redacted]');
  let text=Buffer.from(clean).subarray(0,OUTPUT_LIMIT).toString('utf8');
  while(Buffer.byteLength(text)>OUTPUT_LIMIT)text=text.slice(0,-1);
  return text;
}
function extractionBusy(error) {
  if(error?.killed||error?.signal||['ETIMEDOUT','ERR_CHILD_PROCESS_STDIO_MAXBUFFER'].includes(error?.code))return false;
  return ['EBUSY','EAGAIN'].includes(error?.code)||
    ((error?.code===75||error?.exitCode===75)&&/(?:^|\r?\n)CONTROL_EXTRACT_IO_BUSY(?:\r?\n|$)/.test(String(error?.stderr||'').trim()));
}
export function validateTransaction(value) {
  const required=['schemaVersion','transactionVersion','id','descriptor','commit','phase','from','to'];
  const allowed=[...required,'failure','acceptedAt','updatedAt','completedAt','stage','stageCode','revision'];
  requireValue(value&&typeof value==='object'&&!Array.isArray(value)&&required.every(key=>Object.hasOwn(value,key))&&Object.keys(value).every(key=>allowed.includes(key)),'Unsupported transaction journal fields');
  requireValue(value.schemaVersion===1&&value.transactionVersion===1&&HASH.test(value.id)&&/^[a-f0-9]{40}$/.test(value.commit),'Unsupported transaction journal');
  validateControlDescriptor(value.descriptor);
  requireValue(['accepted','staging','waiting-existing-guard','activating','health-check','rolling-back','healthy','rolled-back','blocked'].includes(value.phase),'Unsupported transaction phase');
  if(value.from!==null)validatePointer(value.from);
  if(value.to!==null)validatePointer(value.to);
  requireValue(value.revision===undefined||(Number.isSafeInteger(value.revision)&&value.revision>0),'Invalid journal revision');
  requireValue(value.stageCode===undefined||STAGE_CODES.has(value.stageCode),'Invalid transaction stage code');
  return value;
}
export function validatePointer(value) {
  exact(value,['schemaVersion','version','sequence','directory','bundleSha256','manifestSha256','receiptSha256'],'control pointer');
  requireValue(value.schemaVersion===1 && value.sequence===sequence(value.version) && value.directory===`${value.version}-${value.bundleSha256}` && [value.bundleSha256,value.manifestSha256,value.receiptSha256].every(item=>typeof item==='string'&&HASH.test(item)), 'Invalid control pointer');
  return value;
}
export async function inventory(directory, relative='') {
  await noLinks(directory);
  const rows=[];
  for(const entry of await fs.readdir(directory,{withFileTypes:true})) {
    const name=relative?`${relative}/${entry.name}`:entry.name;
    requireValue(!/[<>:"|?*\x00-\x1f]|[. ]$/.test(entry.name),'Unsafe installed dependency path');
    const full=path.join(directory,entry.name);await noLinks(full);
    if(entry.isDirectory()) rows.push(...await inventory(full,name));
    else {requireValue(entry.isFile(),'Unsafe installed dependency');const stat=await fs.stat(full);rows.push({path:name,sha256:await fileHash(full),sizeBytes:stat.size});}
    requireValue(rows.length<=20000,'Dependency inventory too large');
  }
  return rows.sort((a,b)=>a.path===b.path?0:a.path<b.path?-1:1);
}
export function validateDependencyLock(pkg, lock) {
  requireValue(lock.lockfileVersion===3 && lock.packages && lock.packages[''],'Unsupported dependency lock');
  requireValue(!pkg.devDependencies && !pkg.optionalDependencies && !pkg.workspaces,'Unsupported dependency provisioning');
  const dependencies=pkg.dependencies||{};
  requireValue(JSON.stringify(dependencies)===JSON.stringify(lock.packages[''].dependencies||{}),'Dependency lock differs from package');
  for(const [name,version] of Object.entries(dependencies)) {
    requireValue(/^(@[a-z0-9._-]+\/)?[a-z0-9][a-z0-9._-]*$/.test(name) && /^\d+\.\d+\.\d+$/.test(version),'Dependencies must be exact pinned versions');
  }
  for(const [name,entry] of Object.entries(lock.packages)) {
    if(!name) continue;
    requireValue(name.startsWith('node_modules/') && !name.split('/').some(item=>item==='..'||item==='.'||!item) && !entry.link && typeof entry.version==='string','Invalid locked dependency');
    const url=new URL(entry.resolved);
    requireValue(url.protocol==='https:' && url.hostname==='registry.npmjs.org' && !url.port && !url.username && !url.password && !url.hash && !url.search && /^sha512-[A-Za-z0-9+/]+={0,2}$/.test(entry.integrity||''),'Dependency must use integrity-pinned npm registry archive');
  }
}
export async function verifyRelease(root,pointer) {
  validatePointer(pointer);
  const directory=path.join(root,'releases',pointer.directory);await noLinks(directory);
  await noLinks(path.join(directory,'CONTROL.json'));
  const raw=await fs.readFile(path.join(directory,'CONTROL.json'));
  requireValue(digest(raw)===pointer.manifestSha256,'Installed control manifest changed');
  const descriptor={version:pointer.version};const manifest=validateControlManifest(parseStrictJson(raw.toString('utf8')),descriptor);
  for(const file of manifest.files) {
    const target=path.join(directory,...file.path.split('/'));await noLinks(target);
    requireValue((await fs.stat(target)).size===file.sizeBytes && await fileHash(target)===file.sha256,'Installed control code changed');
  }
  await noLinks(path.join(directory,'.verified.json'));
  const receiptRaw=await fs.readFile(path.join(directory,'.verified.json'));
  requireValue(digest(receiptRaw)===pointer.receiptSha256,'Installed dependency receipt changed');
  const receipt=parseStrictJson(receiptRaw.toString('utf8'));
  exact(receipt,['schemaVersion','version','bundleSha256','manifestSha256','commit','dependencies'],'verification receipt');
  requireValue(receipt.schemaVersion===1 && receipt.version===pointer.version && receipt.bundleSha256===pointer.bundleSha256 && receipt.manifestSha256===pointer.manifestSha256 && /^[a-f0-9]{40}$/.test(receipt.commit),'Invalid verification receipt');
  requireValue(JSON.stringify(await inventory(path.join(directory,'APFDiscordBot','node_modules')))===JSON.stringify(receipt.dependencies),'Installed dependencies changed');
  return {directory,manifest,receipt};
}
export class ControlEngine {
  constructor(settings,options={}) {
    this.settings=settings;this.root=settings.root;this.channel=options.channel||new GitHubChannel();
    this.execute=options.execute||execute;this.prepare=options.prepare||null;
    this.diagnosticsRoot=options.diagnosticsRoot||settings.diagnosticsRoot||path.join(settings.root,'diagnostics');
    this.quiesce=options.quiesce||(async()=>false);this.handoff=options.handoff||(async()=>{});
    this.nativeIdle=options.nativeIdle||(()=>this.isNativeIdle());this.fault=options.fault||(async()=>{});
    this.busy=false;
  }
  async state() {
    const current=await readJson(path.join(this.root,'transaction.json'),null);
    if(!current)return null;validateTransaction(current);
    const history=await readJson(path.join(this.root,'transactions',`${current.id}.json`),null);
    if(history && (history.revision||0)>(current.revision||0)) {
      validateTransaction(history);requireValue(history.id===current.id,'Conflicting control journal');
      await writeJson(path.join(this.root,'transaction.json'),history);
      return history;
    }
    return current;
  }
  async active() { return validatePointer(await readJson(path.join(this.root,'active.json'))); }
  async save(value) {
    validateTransaction(value);
    const previous=await readJson(path.join(this.root,'transactions',`${value.id}.json`),null);
    value={...value,revision:Math.max(previous?.revision||0,value.revision||0)+1};
    await writeJson(path.join(this.root,'transactions',`${value.id}.json`),value);
    await this.fault('history-written',value);
    await writeJson(path.join(this.root,'transaction.json'),value);
    return value;
  }
  async phase(transaction,phase,extra={}) { return this.save({...transaction,...extra,phase,updatedAt:new Date().toISOString()}); }
  async step(transaction,stageCode,operation,attempt=1) {
    requireValue(STAGE_CODES.has(stageCode)&&HASH.test(transaction.id),'Invalid diagnostic step');
    Object.assign(transaction,await this.phase(transaction,transaction.phase,{stageCode}));
    const directory=path.join(this.diagnosticsRoot,transaction.id);
    await noLinks(directory);await fs.mkdir(directory,{recursive:true});
    const startedAt=new Date().toISOString(),started=performance.now();
    const name=`${Date.now()}-${crypto.randomUUID()}-${stageCode}`;
    const record=async(status,extra={})=>{
      const file=path.join(directory,`${name}-${status}.json`);await noLinks(file);
      await fs.writeFile(file,JSON.stringify({schemaVersion:1,transactionId:transaction.id,stageCode,attempt,status,startedAt,elapsedMs:Math.max(0,Math.round(performance.now()-started)),...extra}),{flag:'wx',mode:0o600});
    };
    await record('started');
    try {
      const result=await operation();
      await record('completed',{completedAt:new Date().toISOString(),stdout:sanitized(result?.stdout),stderr:sanitized(result?.stderr)});
      return result;
    } catch(thrown) {
      const error=thrown instanceof Error?thrown:new Error(String(thrown));
      const code=typeof error.code==='number'||/^[A-Za-z0-9_-]{1,64}$/.test(error.code||'')?error.code:null;
      await record('failed',{failedAt:new Date().toISOString(),message:sanitized(error.message),code,exitCode:Number.isInteger(error.exitCode)?error.exitCode:Number.isInteger(error.code)?error.code:null,signal:/^[A-Z0-9]{1,32}$/.test(error.signal||'')?error.signal:null,killed:error.killed===true,stdout:sanitized(error.stdout),stderr:sanitized(error.stderr)}).catch(()=>{});
      error.controlReason='control-validation-failed';
      throw error;
    }
  }
  async isNativeIdle() {
    const state=await readJson(path.join(this.settings.botRoot,'state','kesas-update-state.json'));
    if(state.schemaVersion!==1 || !['idle','failed'].includes(state.status)) return false;
    const journal=await fs.stat(path.join(this.settings.nativeRoot,'.update-transaction.json')).catch(error=>{if(error.code==='ENOENT')return null;throw error;});
    if(journal) return false;
    // Probe the existing OS-owned publisher lease. Never terminate its owner.
    const probe=path.join(this.root,'bootstrap','Probe-NativeIdle.ps1');
    const result=await this.execute(this.settings.powershell,['-NoProfile','-NonInteractive','-File',probe,'-NativeRoot',this.settings.nativeRoot],{windowsHide:true,maxBuffer:16384,timeout:15000});
    return result.stdout.trim()==='idle';
  }
  async clearMaintenance(id) {
    const file=path.join(this.root,'maintenance.json');const marker=await readJson(file,null);
    if(marker?.transactionId===id) {await noLinks(file);await fs.unlink(file);}
  }
  async stage(transaction) {
    const {descriptor}=transaction;validateControlDescriptor(descriptor);
    const final=path.join(this.root,'releases',`${descriptor.version}-${descriptor.sha256}`);
    let existing=await fs.stat(final).catch(error=>{if(error.code==='ENOENT')return null;throw error;});
    if(existing) {
      return this.step(transaction,'verify',async()=>{
        requireValue(transaction.to,'Unverified control directory conflict');
        await verifyRelease(this.root,transaction.to);return transaction.to;
      });
    }
    let stage=path.join(this.root,'staging',`${transaction.id}-${crypto.randomUUID()}`);
    await noLinks(stage);await fs.mkdir(path.dirname(stage),{recursive:true});
    transaction=await this.phase(transaction,'staging',{stage:path.basename(stage)});
    const bytes=await this.step(transaction,'download',async()=>{
      const bytes=await this.channel.archive(descriptor);
      // Authenticated fixed-repository descriptor is mandatory even on recovery.
      requireValue(bytes.length===descriptor.sizeBytes && digest(bytes)===descriptor.sha256,'Invalid authenticated control bytes');
      return bytes;
    });
    const archive=`${stage}.zip`;
    await this.step(transaction,'archive-write',async()=>{await atomic(archive,bytes);await this.fault('staging');});
    for(let attempt=1;attempt<=3;attempt++) {
      if(attempt>1) {
        stage=path.join(this.root,'staging',`${transaction.id}-${crypto.randomUUID()}`);
        await noLinks(stage);
        transaction=await this.phase(transaction,'staging',{stage:path.basename(stage)});
      }
      try {
        await this.step(transaction,'extract',()=>this.execute(this.settings.powershell,['-NoProfile','-NonInteractive','-File',this.settings.extractor||path.join(this.root,'bootstrap','Expand-ControlBundle.ps1'),'-Archive',archive,'-Destination',stage,'-ExpectedVersion',descriptor.version,'-ExpectedSha256',descriptor.sha256,'-ExpectedSize',String(descriptor.sizeBytes)],{windowsHide:true,maxBuffer:65536,timeout:60000}),attempt);
        break;
      } catch(error) {
        if(error?.simulatedCrash||attempt===3||!extractionBusy(error))throw error;
        await new Promise(resolve=>setTimeout(resolve,100*attempt));
      }
    }
    const raw=await this.step(transaction,'verify',async()=>{
      const raw=await fs.readFile(path.join(stage,'CONTROL.json'));
      const manifest=validateControlManifest(parseStrictJson(raw.toString('utf8')),descriptor);
      requireValue(Number(process.versions.node.split('.')[0])===manifest.runtime.nodeMajor,'Required Node runtime is not provisioned');
      const pkg=parseStrictJson(await fs.readFile(path.join(stage,'APFDiscordBot','package.json'),'utf8'));
      const lock=parseStrictJson(await fs.readFile(path.join(stage,'APFDiscordBot','package-lock.json'),'utf8'));
      validateDependencyLock(pkg,lock);
      return raw;
    });
    await this.step(transaction,'dependencies',()=>this.prepare?this.prepare(stage):this.execute(this.settings.node,[this.settings.npm,'ci','--ignore-scripts','--engine-strict','--no-audit','--no-fund','--registry=https://registry.npmjs.org'],{cwd:path.join(stage,'APFDiscordBot'),windowsHide:true,maxBuffer:65536,timeout:180000}));
    const dependencies=await this.step(transaction,'inventory',()=>inventory(path.join(stage,'APFDiscordBot','node_modules')));
    return this.step(transaction,'verify',async()=>{
      const receipt={schemaVersion:1,version:descriptor.version,bundleSha256:descriptor.sha256,manifestSha256:digest(raw),commit:transaction.commit,dependencies};
      const receiptBytes=Buffer.from(JSON.stringify(receipt));await atomic(path.join(stage,'.verified.json'),receiptBytes);
      const pointer={schemaVersion:1,version:descriptor.version,sequence:sequence(descriptor.version),directory:path.basename(final),bundleSha256:descriptor.sha256,manifestSha256:digest(raw),receiptSha256:digest(receiptBytes)};
      // Persist the intended immutable directory identity before its rename. A
      // process death on either side can verify/reuse it without repeating npm.
      await this.phase(transaction,'staging',{to:pointer});await noLinks(final);
      await fs.mkdir(path.dirname(final),{recursive:true});await fs.rename(stage,final);
      await verifyRelease(this.root,pointer);return pointer;
    });
  }
  async tick() {
    if(this.busy)return {status:'busy'};this.busy=true;
    let transaction;
    try {
      const active=await this.active();transaction=await this.state();
      if(transaction&&TERMINAL.has(transaction.phase))await this.clearMaintenance(transaction.id);
      if(transaction && (transaction.schemaVersion!==1 || transaction.transactionVersion!==1)) throw new Error('Unsupported transaction journal');
      if(transaction && !TERMINAL.has(transaction.phase)) {
        // Bootstrap, not its replacement, owns activation/health recovery.
        if(['activating','health-check','rolling-back'].includes(transaction.phase)) return {status:'waiting-bootstrap'};
        validateControlDescriptor(transaction.descriptor);
      } else {
        const floor=Math.max(active.sequence,transaction?.descriptor?sequence(transaction.descriptor.version):0);
        const intent=await this.channel.intent(floor);
        if(intent.absent)return {status:'no-control-release'};
        validateControlDescriptor(intent.descriptor);
        const next=sequence(intent.descriptor.version);
        if(next<=floor) return {status:next===active.sequence?'current':'ignored-older-or-completed'};
        requireValue(!intent.ignored && /^[a-f0-9]{40}$/.test(intent.commit) && HASH.test(intent.descriptorSha256),'Unauthenticated control intent');
        const previous=await readJson(path.join(this.root,'transactions',`${intent.descriptorSha256}.json`),null);
        if(previous) {
          if(TERMINAL.has(previous.phase))return {status:'already-attempted'};
          requireValue(previous.schemaVersion===1 && previous.transactionVersion===1 && previous.id===intent.descriptorSha256 && previous.from?.bundleSha256===active.bundleSha256,'Incomplete acceptance conflicts with active control');
          transaction=await this.save(previous);
        } else transaction=await this.save({schemaVersion:1,transactionVersion:1,id:intent.descriptorSha256,descriptor:intent.descriptor,commit:intent.commit,phase:'accepted',from:active,to:null,failure:null,acceptedAt:new Date().toISOString(),updatedAt:new Date().toISOString()});
      }
      let pointer=transaction.to;
      if(pointer) {
        const final=path.join(this.root,'releases',pointer.directory);
        const present=await fs.stat(final).catch(error=>{if(error.code==='ENOENT')return null;throw error;});
        if(!present && transaction.phase==='staging' && /^[a-f0-9]{64}-[a-f0-9-]{36}$/.test(transaction.stage||'')) {
          const pending=path.join(this.root,'staging',transaction.stage);
          // The receipt was durable before the final directory rename. Verify
          // it through the same release validator using a temporary fixture
          // root, then finish the single immutable rename.
          await noLinks(pending);await fs.mkdir(path.dirname(final),{recursive:true});
          await fs.rename(pending,final);
        }
        await this.step(transaction,'verify',()=>verifyRelease(this.root,pointer));
      }
      else pointer=await this.stage(transaction);
      transaction=await this.state();
      transaction=await this.phase(transaction,'waiting-existing-guard',{to:pointer});
      if(!await this.nativeIdle()) return {status:'waiting-existing-guard'};
      await writeJson(path.join(this.root,'maintenance.json'),{schemaVersion:1,transactionId:transaction.id});
      if(!await this.nativeIdle() || !await this.quiesce()) {await this.clearMaintenance(transaction.id);return {status:'waiting-existing-guard'};}
      // Quiescence acknowledges that both polling loops and any native helper
      // have drained. The supervisor performs one final OS lease probe.
      if(!await this.nativeIdle()) {await this.clearMaintenance(transaction.id);return {status:'waiting-existing-guard'};}
      transaction=await this.phase(transaction,'activating');
      await this.step(transaction,'activate',async()=>{await this.fault('activation');await this.handoff(transaction);});return {status:'handoff'};
    } catch(error) {
      if(error?.simulatedCrash) throw error;
      if(transaction && !TERMINAL.has(transaction.phase)) {
        const latest=await this.state();
        if(latest?.id===transaction.id)transaction=latest;
        await this.phase(transaction,'blocked',{failure:'control-validation-failed'});
        await this.clearMaintenance(transaction.id);
      }
      return {status:'blocked',failure:'control-validation-failed'};
    } finally {this.busy=false;}
  }
}
