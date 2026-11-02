import fs from 'node:fs/promises';
import crypto from 'node:crypto';
import path from 'node:path';
import { validateHostReport } from './contract.mjs';

export const digest = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
export function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
  return (crc ^ 0xffffffff) >>> 0;
}
// A single STORE entry avoids compression libraries and bounds controller parsing.
export function statusZip(report) {
  const data = Buffer.from(JSON.stringify(validateHostReport(report)));
  if (data.length > 32768) throw new Error('Host status exceeds limit');
  return singleEntryZip('host-status.json',data);
}
const DIAGNOSTIC_NATIVE_STAGES = ['request-validation','intent-authentication','archive-copy','fixed-verifier','publisher-validation','publisher-execution','completion-health','awaiting-new-guard','guard-admission','preparation-signing','media-publication','native-activation','github-completion'];
const DIAGNOSTIC_WATCHER_STAGES = ['channel-observation','requirements','active-health','intent-authentication','archive-download','archive-staging','queue-admission'];
const diagnosticMatch = (value,pattern) => typeof value === 'string' && pattern.exec(value)?.[0] === value;
const diagnosticHash = value => diagnosticMatch(value,/^[a-f0-9]{64}$/);
const diagnosticCommit = value => value === null || diagnosticMatch(value,/^[a-f0-9]{40}$/);
const diagnosticVersion = value => diagnosticMatch(value,/^kesas-rc[1-9][0-9]{0,14}$/);
const diagnosticDate = value => diagnosticMatch(value,/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/) && Number.isFinite(Date.parse(value));
const diagnosticObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export const RECOVERY_REASONS=[null,'recovery-not-authorized','recovery-result-changed','recovery-helper-active','recovery-activation-ambiguous','recovery-identity-mismatch','recovery-baseline-invalid','recovery-already-consumed','recovery-intent-invalid'];
export function publicModeStatus(value) {
  if(value===null||value===undefined)return null;
  if(!diagnosticObject(value)||value.schemaVersion!==1||!diagnosticMatch(value.id,/^[a-f0-9]{32}$/)||
    !['claimed','waiting','prepared','applying','healthy','blocked'].includes(value.phase)||
    ![null,'native-busy','native-journal-present','players-present-or-status-unavailable','mode-activation-needs-review','mode-start-needs-review','mode-preparation-needs-review'].includes(value.reason)||
    ![0,1].includes(value.restartCount)||!diagnosticDate(value.updatedAt)||
    ![null,'Science'].includes(value.mode)||
    !(value.profileId===null||diagnosticMatch(value.profileId,/^[a-zA-Z0-9_.-]{1,120}-science$/))||
    !(value.saveNamespace===null||diagnosticMatch(value.saveNamespace,/^APF\/[a-zA-Z0-9_.-]{1,120}\/Science$/))||
    (value.phase==='healthy'&&(value.mode!=='Science'||value.profileId===null||value.saveNamespace===null||value.restartCount!==1)))throw new Error('Invalid mode observation');
  let evidence;
  if(Object.hasOwn(value,'failureEvidence')){
    const e=value.failureEvidence;
    if(!diagnosticObject(e)||!diagnosticHash(e.receiptSha256)||![0,1,2].includes(e.backupCount)||
      !['profile-baseline-invalid','profile-policy-field-invalid','bridge-file-busy','application-version-changed','players-resumed','runtime-property-missing','unclassified-preparation-error','optional-profile-file-absent'].includes(e.errorCode)||
      !['Sandbox','Science','Career','unknown'].includes(e.currentMode)||!['Sandbox','Science','Career','unknown'].includes(e.profileMode)||
      !(e.progressionSchemaVersion===null||(Number.isSafeInteger(e.progressionSchemaVersion)&&e.progressionSchemaVersion>=0))||
      ![null,true,false].includes(e.resetOnPackChange))throw new Error('Invalid mode failure evidence');
    if(Object.hasOwn(e,'profileSource')&&!['file','automatic'].includes(e.profileSource))throw new Error('Invalid mode profile source');
    evidence={receiptSha256:e.receiptSha256,backupCount:e.backupCount,errorCode:e.errorCode,currentMode:e.currentMode,profileMode:e.profileMode,progressionSchemaVersion:e.progressionSchemaVersion,resetOnPackChange:e.resetOnPackChange,...(Object.hasOwn(e,'profileSource')?{profileSource:e.profileSource}:{})};
  }
  if(Object.hasOwn(value,'evidenceProblem')&&!['evidence-json-invalid','evidence-file-missing','evidence-access-denied','evidence-invalid'].includes(value.evidenceProblem))throw new Error('Invalid mode evidence problem');
  return {schemaVersion:1,id:value.id,phase:value.phase,reason:value.reason,mode:value.mode,profileId:value.profileId,saveNamespace:value.saveNamespace,restartCount:value.restartCount,updatedAt:value.updatedAt,...(evidence?{failureEvidence:evidence}:{}),...(Object.hasOwn(value,'evidenceProblem')?{evidenceProblem:value.evidenceProblem}:{})};
}
export function publicDiagnostics(report) {
  const bootstrap = report?.bootstrap, watcher = report?.watcher, native = report?.native, recovery = report?.recovery;
  if (!diagnosticObject(report) || ![1,2].includes(report.schemaVersion) || !diagnosticDate(report.observedAt) || !diagnosticDate(report.expiresAt) ||
      !diagnosticMatch(report.controlVersion,/^control-[1-9][0-9]{0,8}$/) || !diagnosticObject(bootstrap) || !diagnosticObject(watcher) ||
      ![bootstrap.nativeAgentSha256,bootstrap.publisherWrapperSha256].every(value => value === null || diagnosticHash(value)) ||
      ![0,1].includes(bootstrap.nativeRecoveryVersion) || (bootstrap.nativeRecoveryVersion === 1 && (!diagnosticHash(bootstrap.nativeAgentSha256) || !diagnosticHash(bootstrap.publisherWrapperSha256))) ||
      !(watcher.stage === null || DIAGNOSTIC_WATCHER_STAGES.includes(watcher.stage)) ||
      !(watcher.reason === null || (watcher.stage !== null && watcher.reason === `${watcher.stage}-failed`)) ||
      !(watcher.version === null || diagnosticVersion(watcher.version)) || !diagnosticCommit(watcher.sourceCommit) ||
      ![watcher.manifestSha256,watcher.archiveSha256].every(value => value === null || diagnosticHash(value))) throw new Error('Invalid host diagnostics');
  let publication = null;
  if (native !== null) {
    if (!diagnosticObject(native) || !diagnosticHash(native.requestId) || !diagnosticVersion(native.version) ||
        !diagnosticHash(native.archiveSha256) || !diagnosticCommit(native.sourceCommit) || !diagnosticHash(native.resultSha256) ||
        !['accepted','recovery-authorized','applying','healthy','blocked'].includes(native.phase) || !DIAGNOSTIC_NATIVE_STAGES.includes(native.stage) ||
        ![null,'native-publication-blocked'].includes(native.reason) ||
        !(native.exitCode === null || (Number.isInteger(native.exitCode) && native.exitCode >= -2147483648 && native.exitCode <= 2147483647)) ||
        !Number.isInteger(native.attempt) || native.attempt < 1 || native.attempt > 1000000 ||
        !(native.recoveryAction === null || (native.recoveryAction === 'retry-preactivation' && native.phase === 'blocked' && [...DIAGNOSTIC_NATIVE_STAGES.slice(0,5),'guard-admission'].includes(native.stage)))) throw new Error('Invalid host diagnostics');
    publication = {requestId:native.requestId,version:native.version,archiveSha256:native.archiveSha256,sourceCommit:native.sourceCommit,
      resultSha256:native.resultSha256,phase:native.phase,stage:native.stage,reason:native.reason,exitCode:native.exitCode,attempt:native.attempt,recoveryAction:native.recoveryAction};
  }
  let recoveryObservation = null;
  if (recovery !== null) {
    if (!diagnosticObject(recovery) || !diagnosticHash(recovery.recoveryId) || !diagnosticHash(recovery.requestId) ||
        !diagnosticCommit(recovery.commit) || !['received','authorized','blocked'].includes(recovery.status) ||
        !RECOVERY_REASONS.includes(recovery.reason)) throw new Error('Invalid host diagnostics');
    recoveryObservation = {recoveryId:recovery.recoveryId,requestId:recovery.requestId,commit:recovery.commit,status:recovery.status,reason:recovery.reason};
  }
  // Project every public field explicitly; private journal extensions never escape.
  let host=null;
  if(report.schemaVersion===2){
    host=report.host;
    const fields=['stage','reason','worker','broker','inventory','leaseActive','journalPhase','exitCode'];
    if(!diagnosticObject(host)||Object.keys(host).length!==fields.length||!fields.every(key=>Object.hasOwn(host,key))||
      !['reporting','broker','worker','inventory','application','native','control'].includes(host.stage)||
      ![null,'broker-not-ready','broker-child-failed','worker-missing','worker-stale','worker-mismatch','worker-invalid','worker-not-ready','worker-failed','inventory-incomplete','inventory-failed','inventory-mismatch','application-observation-failed','application-version-mismatch','bridge-unavailable','native-observation-failed','native-completion-unproven','native-publication-blocked','health-unavailable','control-observation-failed'].includes(host.reason)||
      !['missing','stale','mismatch','invalid','starting','running','draining','stopped','failed'].includes(host.worker)||
      !['ready','starting','failed'].includes(host.broker)||!['unknown','pending','collecting','complete','failed','mismatch'].includes(host.inventory)||
      ![null,true,false].includes(host.leaseActive)||![null,'native-activation','github-completion','unknown'].includes(host.journalPhase)||
      !(host.exitCode===null||(Number.isInteger(host.exitCode)&&host.exitCode>=-2147483648&&host.exitCode<=2147483647)))throw new Error('Invalid host diagnostics');
  }
  return {schemaVersion:report.schemaVersion,observedAt:report.observedAt,expiresAt:report.expiresAt,controlVersion:report.controlVersion,
    bootstrap:{nativeAgentSha256:bootstrap.nativeAgentSha256,publisherWrapperSha256:bootstrap.publisherWrapperSha256,nativeRecoveryVersion:bootstrap.nativeRecoveryVersion},
    watcher:{stage:watcher.stage,reason:watcher.reason,version:watcher.version,sourceCommit:watcher.sourceCommit,manifestSha256:watcher.manifestSha256,archiveSha256:watcher.archiveSha256},
    native:publication,recovery:recoveryObservation,...(host?{host}:{} ),...(Object.hasOwn(report,'gameMode')?{gameMode:publicModeStatus(report.gameMode)}:{})};
}
export function diagnosticsZip(report) {
  const data = Buffer.from(JSON.stringify(publicDiagnostics(report)));
  if (data.length > 32768) throw new Error('Host diagnostics exceeds limit');
  return singleEntryZip('host-diagnostics.json',data);
}
function singleEntryZip(filename,data) {
  const name = Buffer.from(filename);
  const local = Buffer.alloc(30); local.writeUInt32LE(0x04034b50); local.writeUInt16LE(20,4);
  local.writeUInt32LE(crc32(data),14); local.writeUInt32LE(data.length,18); local.writeUInt32LE(data.length,22); local.writeUInt16LE(name.length,26);
  const central = Buffer.alloc(46); central.writeUInt32LE(0x02014b50); central.writeUInt16LE(20,4); central.writeUInt16LE(20,6);
  central.writeUInt32LE(crc32(data),16); central.writeUInt32LE(data.length,20); central.writeUInt32LE(data.length,24); central.writeUInt16LE(name.length,28);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50); end.writeUInt16LE(1,8); end.writeUInt16LE(1,10);
  end.writeUInt32LE(central.length+name.length,12); end.writeUInt32LE(local.length+name.length+data.length,16);
  return Buffer.concat([local,name,data,central,name,end]);
}
export async function noLinks(target) {
  for (let cursor = path.resolve(target);;) {
    const stat = await fs.lstat(cursor).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
    if (stat?.isSymbolicLink() || (stat?.isFile() && stat.nlink !== 1)) throw new Error('Unsafe control path');
    const parent = path.dirname(cursor); if (parent === cursor) return; cursor = parent;
  }
}
export async function atomic(file, bytes) {
  await noLinks(file); await fs.mkdir(path.dirname(file),{recursive:true});
  const temporary = `${file}.${crypto.randomUUID()}.incoming`;
  const handle = await fs.open(temporary,'wx');
  try { await handle.writeFile(bytes); await handle.sync(); } finally { await handle.close(); }
  try {
    await noLinks(file);
    for(let attempt=0;;attempt++) {
      try {await fs.rename(temporary,file);break;}
      catch(error){if(!['EACCES','EBUSY','EPERM'].includes(error.code)||attempt===8)throw error;await new Promise(resolve=>setTimeout(resolve,Math.min(25*2**attempt,800)));}
    }
  }
  finally { await fs.rm(temporary,{force:true}).catch(()=>{}); }
}
export const writeJson = (file,value) => atomic(file,Buffer.from(JSON.stringify(value)));
export async function readJson(file, absent = undefined) {
  await noLinks(file);
  try { const stat=await fs.stat(file); if(stat.size>1024*1024) throw new Error('Control state too large'); return JSON.parse(await fs.readFile(file,'utf8')); }
  catch(error) { if(error.code==='ENOENT' && absent !== undefined) return absent; throw error; }
}
export async function fileHash(file) {
  await noLinks(file);const hash=crypto.createHash('sha256');const handle=await fs.open(file,'r');
  try {for await(const chunk of handle.createReadStream())hash.update(chunk);return hash.digest('hex');}
  finally {await handle.close().catch(()=>{});}
}

// The publisher throws this exact static message before Wait-UpdateServerStopped
// if a player joins during preparation. Unknown guard errors remain ambiguous.
// Only protected SYSTEM artifacts can establish this proof; no request field,
// worker mirror or public report authorizes its own recovery.
export async function guardDeferralProof(settings,result,{verifyFiles=false,isAlive=async pid=>{try{process.kill(pid,0);return true;}catch(error){return error.code!=='ESRCH';}}}={}) {
  try {
    if(result?.phase!=='blocked'||result.stage!=='guard-admission'||!diagnosticHash(result.id)||!diagnosticVersion(result.version)||
       !diagnosticHash(result.sha256)||!Number.isInteger(result.attempt)||result.attempt<1||result.attempt>1000000||
       typeof result.notes!=='string'||result.notes.length>500||result.nativePid&&await isAlive(result.nativePid))return null;
    const read=async(file,maximum)=>{
      await noLinks(file);const handle=await fs.open(file,'r');
      try {
        const before=await handle.stat({bigint:true});
        if(!before.isFile()||before.nlink!==1n||before.size<1n||before.size>BigInt(maximum))throw new Error('Invalid protected guard evidence');
        const bytes=await handle.readFile(),after=await handle.stat({bigint:true});await noLinks(file);
        const named=await fs.lstat(file,{bigint:true});
        const stamp=s=>[s.dev,s.ino,s.size,s.mtimeNs,s.ctimeNs].join(':');
        if(bytes.length>maximum||stamp(before)!==stamp(after)||stamp(after)!==stamp(named))throw new Error('Protected guard evidence changed');
        return {value:JSON.parse(bytes.toString('utf8')),sha256:digest(bytes)};
      }finally{await handle.close();}
    };
    const directory=path.join(settings.root,'native-cache','diagnostics',result.id,String(result.attempt));
    const receipt=await read(path.join(directory,'receipt.json'),4096),error=await read(path.join(directory,'error.json'),32768);
    if(receipt.value.schemaVersion!==1||receipt.value.requestId!==result.id||receipt.value.attempt!==result.attempt||
       receipt.value.stage!=='guard-admission'||receipt.value.status!=='failed'||receipt.value.exitCode!==1||
       !Number.isFinite(Date.parse(receipt.value.completedAt))||
       error.value.Message!=='Player activity resumed or authoritative status is stale/invalid. Prepared release retained; no services stopped. Requeue when empty.'||
       typeof error.value.ScriptStackTrace!=='string'||!/(?:^|\r?\n)at Assert-PublisherEmpty, [^\r\n]*Publish-Release\.ps1: line [0-9]+(?:\r?\n|$)/.test(error.value.ScriptStackTrace))return null;
    for(const relative of ['.update-transaction.json',`Updates/${result.version}`]) {
      const file=path.join(settings.nativeRoot,relative);await noLinks(file);
      if(await fs.lstat(file).then(()=>true,e=>{if(e.code==='ENOENT')return false;throw e;}))return null;
    }
    const prepared=path.join(settings.nativeRoot,'.github-prepared',result.version);
    const record=await read(path.join(prepared,'prepared.json'),65536),value=record.value;
    if(value.schema!==1||value.version!==result.version||value.fingerprint?.releaseZipSha256!==result.sha256||value.fingerprint?.notes!==result.notes||!Array.isArray(value.files))return null;
    const names=['Published/APFClientUpdate.json',`Published/APFClient-${result.version}.zip`,'KESAS-Client-Bootstrap.zip','KESAS-Client-Bootstrap.zip.sha256',`Aetherfront-Starter-${result.version}.zip`,'install-catalog.json'];
    if(value.files.length!==names.length||new Set(value.files.map(row=>row.path)).size!==names.length)return null;
    for(const row of value.files) {
      if(!names.includes(row.path)||!diagnosticHash(row.sha256)||!Number.isSafeInteger(row.sizeBytes)||row.sizeBytes<1||row.sizeBytes>64*1024*1024)return null;
      const file=path.join(prepared,...row.path.split('/'));await noLinks(file);const stat=await fs.lstat(file);
      if(!stat.isFile()||stat.nlink!==1||stat.size!==row.sizeBytes||verifyFiles&&await fileHash(file)!==row.sha256)return null;
    }
    return {preparedRecordSha256:record.sha256,proofSha256:digest(Buffer.from([receipt.sha256,error.sha256,record.sha256].join(':')))};
  }catch{return null;}
}

// Public ZIPs are deliberately hardlinked to the immutable pack cache. This
// read-only inventory must not relax the no-hardlinks rule for control code,
// journals or writes. Reject reparse paths and verify file identity and content
// timestamps before and after reading through one open handle.
export async function publicMediaInventory(directory, previous = {}) {
  await noLinks(directory);
  const stamp = stat => `${stat.dev}:${stat.ino}:${stat.size}:${stat.mtimeMs}:${stat.ctimeMs}`;
  const regular = stat => { if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('Unsafe public archive'); return stat; };
  const next = {};
  const names = (await fs.readdir(directory)).filter(name => /^kesas-.*\.zip$/.test(name) && !['kesas-host-status.zip','kesas-host-diagnostics.zip'].includes(name)).sort();
  for (const name of names) {
    const file = path.join(directory, name);
    const before = regular(await fs.lstat(file));
    const identity = stamp(before);
    let sha256 = previous[name]?.stamp === identity && /^[a-f0-9]{64}$/.test(previous[name]?.sha256 || '') ? previous[name].sha256 : null;
    if (!sha256) {
      const hash = crypto.createHash('sha256');
      const handle = await fs.open(file, 'r');
      try {
        if (stamp(regular(await handle.stat())) !== identity) throw new Error('Public archive changed during inventory');
        const buffer = Buffer.alloc(1024 * 1024);
        for (;;) {
          const { bytesRead } = await handle.read(buffer, 0, buffer.length, null);
          if (!bytesRead) break;
          hash.update(buffer.subarray(0, bytesRead));
        }
        if (stamp(regular(await handle.stat())) !== identity) throw new Error('Public archive changed during inventory');
        sha256 = hash.digest('hex');
      } finally { await handle.close(); }
    }
    if (stamp(regular(await fs.lstat(file))) !== identity) throw new Error('Public archive changed during inventory');
    next[name] = { stamp: identity, sha256 };
  }
  await noLinks(directory);
  return { cache: next, sha256: digest(Buffer.from(JSON.stringify(Object.entries(next).map(([name, row]) => ({ name, sha256: row.sha256 }))))) };
}
