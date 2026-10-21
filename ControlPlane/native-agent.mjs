// Installed once in the protected bootstrap directory, beside frozen modules.
// Never import JavaScript from the bot, active control release, or application.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { validatePointer } from './engine.mjs';
import { GitHubChannel, parseStrictJson } from './github.mjs';
import { CAPABILITIES, REPOSITORY, HASH, exact, requireValue, validateControlDescriptor,
  validateControlManifest } from './contract.mjs';
import { digest, noLinks, writeJson, fileHash } from './public-status.mjs';

export const MAX_APPLICATION_BYTES = 512 * 1024 * 1024;
const COMMIT = /^[a-f0-9]{40}$/;
const REQUEST_FIELDS = ['schemaVersion','transactionVersion','id','version','sha256','sizeBytes','notes','sourceCommit'];
const TERMINAL = new Set(['healthy','blocked']);
const FAILURE = 'native-publication-blocked';
const PREACTIVATION_STAGES = new Set(['request-validation','intent-authentication','archive-copy','fixed-verifier','publisher-validation']);
const RECOVERY_FIELDS = ['schemaVersion','recoveryId','action','requestId','version','archiveSha256','sourceCommit','expectedResultSha256','issuedAt','expiresAt'];
const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const bootstrapDirectory = path.dirname(fileURLToPath(import.meta.url));

function applicationFields(value) {
  requireValue(typeof value.version === 'string' && /^kesas-rc[1-9][0-9]{0,14}$/.test(value.version), 'Invalid application version');
  requireValue(typeof value.sha256 === 'string' && HASH.test(value.sha256) && Number.isSafeInteger(value.sizeBytes) &&
    value.sizeBytes >= 1 && value.sizeBytes <= MAX_APPLICATION_BYTES && typeof value.notes === 'string' &&
    value.notes.length <= 500 && !/[\p{Cc}\p{Cf}]/u.test(value.notes), 'Invalid application metadata');
}

// This fixed property order is shared with the unprivileged request writer.
export function requestId(value) {
  return digest(Buffer.from(JSON.stringify({schemaVersion:value.schemaVersion,transactionVersion:value.transactionVersion,
    version:value.version,sha256:value.sha256,sizeBytes:value.sizeBytes,notes:value.notes,sourceCommit:value.sourceCommit})));
}
export function validateRequest(value, expectedId = value?.id) {
  exact(value, REQUEST_FIELDS, 'native request');
  applicationFields(value);
  requireValue(value.schemaVersion === 1 && value.transactionVersion === 1 && typeof value.id === 'string' &&
    HASH.test(value.id) && value.id === expectedId && (value.sourceCommit === null ||
      (typeof value.sourceCommit === 'string' && COMMIT.test(value.sourceCommit))) && requestId(value) === value.id, 'Invalid native request identity');
  return value;
}
export function validateDeployment(value) {
  const fields = ['schemaVersion','version','releaseTag','assetName','sha256','sizeBytes','notes'];
  if (value?.schemaVersion === 2) fields.push('requirements');
  exact(value, fields, 'application descriptor');
  requireValue([1,2].includes(value.schemaVersion), 'Unsupported application schema');
  applicationFields(value);
  requireValue(value.releaseTag === `host-${value.version}` && value.assetName === `Aetherfront-Release-${value.version}.zip`, 'Invalid application artifact');
  if (value.schemaVersion === 2) {
    exact(value.requirements, ['contractVersion','capabilities'], 'application requirements');
    const capabilities = value.requirements.capabilities;
    requireValue(value.requirements.contractVersion === 1 && Array.isArray(capabilities) && capabilities.length > 0 &&
      new Set(capabilities).size === capabilities.length && capabilities.every(item => CAPABILITIES.includes(item)), 'Unsupported application capabilities');
  }
  return value;
}
export async function authenticateApplication(channel, request) {
  validateRequest(request);
  let commit = request.sourceCommit;
  if (commit === null) {
    const reference = parseStrictJson((await channel.bytes(`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`, 16384)).toString('utf8'));
    commit = reference.object?.sha;
    requireValue(reference.ref === 'refs/heads/deploy' && reference.object?.type === 'commit' && typeof commit === 'string' && COMMIT.test(commit), 'Invalid application reference');
  }
  const raw = await channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/${commit}/deployment/production.json`, 65536);
  const descriptor = validateDeployment(parseStrictJson(raw.toString('utf8')));
  requireValue(['version','sha256','sizeBytes','notes'].every(field => descriptor[field] === request[field]), 'Application intent mismatch');
  return {commit, descriptor};
}

// No execFile timeout, shell or kill. A publisher retains its native OS lease
// if this agent dies; recovery only observes it. Output uses protected files.
export async function runChild(file, args, options = {}, spawnChild = spawn) {
  const {onSpawn, captureOutput = true, outputHandles, ...settings} = options;
  const child = spawnChild(file, args, {...settings, windowsHide:true, shell:false, stdio:['ignore',outputHandles?.stdout ?? (captureOutput ? 'pipe' : 'ignore'),outputHandles?.stderr ?? (captureOutput ? 'pipe' : 'ignore')]});
  let stdout = '', stderr = '', overflow = false;
  child.stdout?.on('data', bytes => {
    if (Buffer.byteLength(stdout) + bytes.length > 16384) overflow = true;
    else stdout += bytes.toString('utf8');
  });
  child.stderr?.on('data', bytes => { if (Buffer.byteLength(stderr) + bytes.length <= 16384) stderr += bytes.toString('utf8'); });
  let started = Promise.resolve();
  const exited = new Promise((resolve, reject) => {
    child.once('error', () => reject(new Error(FAILURE)));
    child.once('close', (code, signal) => resolve({code,signal}));
    if (onSpawn) started = Promise.resolve().then(() => onSpawn(child.pid)).then(() => null, () => new Error(FAILURE));
  });
  const result = await exited;
  const startError = await started;
  if (startError || result.code !== 0 || result.signal || overflow) {
    const error = new Error(FAILURE);
    error.exitCode = Number.isInteger(result.code) ? result.code : null;
    error.signal = result.signal || null;
    error.privateDetail = {stdout,stderr};
    throw error;
  }
  return {stdout, pid:child.pid};
}

// Unlike the control-plane idle guard, recovery must distinguish an abandoned
// journal from a live publisher. Probe only the OS lease here, not the journal.
const LEASE_PROBE = `$ErrorActionPreference='Stop'
$lease=$null
try {
  $root=[Environment]::GetEnvironmentVariable('APF_NATIVE_LEASE_ROOT')
  $lease=[IO.FileStream]::new([IO.Path]::Combine($root,'.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  Write-Output 'idle'
} catch [IO.IOException] {Write-Output 'busy'}
finally {if($lease){$lease.Dispose()}}`;
export async function probeNativeIdle(settings, run = runChild) {
  await noLinks(path.join(settings.nativeRoot, '.publish.lock'));
  const result = await run(settings.powershell, ['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(LEASE_PROBE,'utf16le').toString('base64')],
    {env:{...process.env,APF_NATIVE_LEASE_ROOT:settings.nativeRoot}});
  requireValue(['idle','busy'].includes(result.stdout.trim()), 'Invalid native lease response');
  return result.stdout.trim() === 'idle';
}

function alive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) { if (error.code === 'ESRCH') return false; if (error.code === 'EPERM') return true; throw error; }
}
async function strictFile(file, maximum = 65536, absent = undefined, bridgeBom = false) {
  await noLinks(file);
  let handle;
  try {
    handle = await fs.open(file, 'r');
    const stat = await handle.stat();
    requireValue(stat.isFile() && stat.nlink === 1 && stat.size <= maximum, 'Invalid native state file');
    // Bounded even when an unprivileged writer races the initial stat.
    const bytes = Buffer.alloc(maximum + 1);
    let length = 0;
    while (length < bytes.length) {
      const read = await handle.read(bytes, length, bytes.length - length, null);
      if (!read.bytesRead) break;
      length += read.bytesRead;
    }
    requireValue(length <= maximum, 'Native state exceeds limit');
    await noLinks(file);
    const content=bytes.subarray(0,length);
    const offset=bridgeBom&&content.length>=3&&content[0]===0xef&&content[1]===0xbb&&content[2]===0xbf?3:0;
    return parseStrictJson(content.subarray(offset).toString('utf8'));
  } catch (error) { if (error.code === 'ENOENT' && absent !== undefined) return absent; throw error; }
  finally { await handle?.close(); }
}
export async function acquireOwner(cache, options = {}) {
  const file = path.join(cache,'owner.json'), recovery = path.join(cache,'owner-recovery.json');
  const isAlive = options.isAlive || alive;
  const owner = {pid:process.pid,token:crypto.randomUUID()};
  const create = async target => {
    await noLinks(target);
    const handle = await fs.open(target,'wx');
    try { await handle.writeFile(JSON.stringify(owner)); await handle.sync(); } finally { await handle.close(); }
  };
  const release = async target => {
    if ((await strictFile(target,4096,null))?.token === owner.token) await fs.unlink(target);
  };
  try { await create(file); }
  catch (error) {
    if (error.code !== 'EEXIST') throw error;
    const previous = await strictFile(file,4096);
    requireValue(Number.isSafeInteger(previous.pid) && previous.pid > 0 && typeof previous.token === 'string', 'Uncertain native owner');
    if (await isAlive(previous.pid)) return null;
    // Serialize stale-owner reclamation. An interrupted reclamation fails
    // closed rather than risking removal of a new owner's lock.
    try { await create(recovery); } catch (error) { if (error.code === 'EEXIST') return null; throw error; }
    try {
      const current = await strictFile(file,4096,null);
      if (current && (current.token !== previous.token || await isAlive(current.pid))) return null;
      if (current) await fs.unlink(file);
      try { await create(file); } catch (error) { if (error.code === 'EEXIST') return null; throw error; }
    } finally { await release(recovery); }
  }
  return () => release(file);
}

export function validateSettings(value) {
  exact(value,['root','botRoot','nativeRoot','powershell','node','npm','runtime'],'native settings');
  for (const field of ['root','botRoot','nativeRoot','powershell','node','npm']) {
    requireValue(typeof value[field] === 'string' && path.isAbsolute(value[field]) && !/^[\\/]{2}/.test(value[field]) && !/[\x00-\x1f]/.test(value[field]), 'Invalid native host path');
  }
  exact(value.runtime,['node','powershell','dotnet'],'native runtime');
  requireValue(Object.values(value.runtime).every(item => typeof item === 'string' && /^\d+\.\d+\.\d+(?:\.\d+)?$/.test(item)), 'Invalid native runtime');
  return value;
}

export class NativeAgent {
  constructor(settings, options = {}) {
    this.settings = validateSettings(settings);
    this.root = settings.root;
    this.cache = path.join(this.root,'native-cache');
    this.bootstrap = options.bootstrapDirectory || bootstrapDirectory;
    this.channel = options.channel || new GitHubChannel(options.fetch);
    this.run = options.run || ((file,args,settings) => runChild(file,args,settings,options.spawn || spawn));
    this.idle = options.probeNativeIdle || (() => probeNativeIdle(this.settings,this.run));
    this.isAlive = options.isAlive || alive;
    this.fault = options.fault || (async () => {});
    this.busy = false;
    this.stop = false;
    this.nextRecoveryPoll = 0;
    this.capabilitiesPublished = false;
  }
  async initialize() {
    // Installer owns ACLs (Administrators High + SYSTEM). Do not create an
    // unprotected cache or modify host identities/permissions at runtime.
    await noLinks(this.cache);
    requireValue((await fs.stat(this.cache)).isDirectory(), 'Protected native cache missing');
    for (const directory of [path.join(this.cache,'results'),path.join(this.cache,'applications'),path.join(this.cache,'control'),path.join(this.cache,'diagnostics'),path.join(this.cache,'recoveries'),path.join(this.root,'native-results')]) {
      await noLinks(directory); await fs.mkdir(directory,{recursive:true});
    }
    if(!this.capabilitiesPublished) {
      await writeJson(path.join(this.root,'native-capabilities.json'),{schemaVersion:1,recoveryVersion:1,
        nativeAgentSha256:await fileHash(path.join(this.bootstrap,'native-agent.mjs')),
        publisherWrapperSha256:await fileHash(path.join(this.bootstrap,'Invoke-NativePublisher.ps1'))});
      this.capabilitiesPublished=true;
    }
  }
  async result(id) {
    requireValue(typeof id === 'string' && HASH.test(id),'Invalid native result identity');
    const value = await strictFile(path.join(this.cache,'results',`${id}.json`),65536,null);
    if (value) requireValue(value.schemaVersion === 1 && value.transactionVersion === 1 && value.id === id &&
      ['accepted','recovery-authorized','applying','healthy','blocked'].includes(value.phase) && value.status === value.phase,'Invalid native result');
    return value;
  }
  async mirror(value) { await writeJson(path.join(this.root,'native-results',`${value.id}.json`),value); return value; }
  async save(value, phase, extra = {}) {
    const result = {...value,...extra,phase,status:phase,updatedAt:new Date().toISOString()};
    await writeJson(path.join(this.cache,'results',`${result.id}.json`),result);
    return this.mirror(result);
  }
  async copyApplication(request) {
    const source = path.join(this.root,'native-requests',`${request.id}.zip`);
    const destination = path.join(this.cache,'applications',`${request.id}.zip`);
    const temporary = `${destination}.${crypto.randomUUID()}.incoming`;
    await noLinks(source); await noLinks(destination);
    const input = await fs.open(source,'r');
    let output;
    try {
      const stat = await input.stat();
      requireValue(stat.isFile() && stat.nlink === 1 && stat.size === request.sizeBytes,'Application size mismatch');
      output = await fs.open(temporary,'wx');
      const hash = crypto.createHash('sha256'); let length = 0;
      for await (const bytes of input.createReadStream({autoClose:false})) {
        length += bytes.length;
        requireValue(length <= request.sizeBytes,'Application exceeds size');
        hash.update(bytes); await output.writeFile(bytes);
      }
      requireValue(length === request.sizeBytes && hash.digest('hex') === request.sha256,'Application hash mismatch');
      await output.sync(); await output.close(); output = null;
      await noLinks(source); await noLinks(destination);
      await fs.rename(temporary,destination);
      return destination;
    } finally {
      await input.close(); await output?.close();
      await fs.rm(temporary,{force:true}).catch(() => {});
    }
  }
  async selectPublisher(file) {
    const directory = path.dirname(file);
    const sha256 = await fileHash(path.join(directory,'Start-Server.ps1'));
    await writeJson(path.join(this.cache,'current-native.json'),{directory,sha256});
    return file;
  }
  async reviewedPublisher(id, priorHash, manifestHash) {
    const file=path.join(this.cache,'recoveries',id,`publisher-repair-${priorHash}.json`);
    requireValue(await fileHash(file)===manifestHash,'Reviewed publisher manifest changed');
    const manifest=await strictFile(file,16384);
    exact(manifest,['schemaVersion','requestId','priorResultSha256','files'],'reviewed publisher manifest');
    const names=['Publish-Release.ps1','Update.Common.ps1','GitHub.Publish.ps1','Export-Client.ps1','Start-Server.ps1','Stop-Server.ps1'];
    requireValue(manifest.schemaVersion===1&&manifest.requestId===id&&manifest.priorResultSha256===priorHash&&Array.isArray(manifest.files)&&manifest.files.length===names.length,'Reviewed publisher identity mismatch');
    const directory=path.join(this.cache,'control',`host-repair-${id}-${manifestHash}`,'KESAS-VPS-Server');
    const seen=new Set();
    for(const row of manifest.files) {
      exact(row,['name','sha256','sizeBytes'],'reviewed publisher file');
      requireValue(names.includes(row.name)&&!seen.has(row.name)&&typeof row.sha256==='string'&&HASH.test(row.sha256)&&Number.isSafeInteger(row.sizeBytes)&&row.sizeBytes>0&&row.sizeBytes<=2*1024*1024,'Invalid reviewed publisher inventory');
      seen.add(row.name);
      const target=path.join(directory,row.name);await noLinks(target);
      requireValue((await fs.stat(target)).size===row.sizeBytes&&await fileHash(target)===row.sha256,'Reviewed publisher bytes changed');
    }
    return path.join(directory,'Publish-Release.ps1');
  }
  async publisher(request, result) {
    if(result?.recovery?.action==='host-reviewed-rollback'&&result.recovery.publisherRepairSha256) {
      return this.selectPublisher(await this.reviewedPublisher(request.id,result.recovery.priorResultSha256,result.recovery.publisherRepairSha256));
    }
    const pointer = validatePointer(await strictFile(path.join(this.root,'active.json')));
    const seed = await strictFile(path.join(this.bootstrap,'seed.json'));
    validatePointer(seed.pointer);
    if (Object.keys(pointer).every(key => pointer[key] === seed.pointer[key])) {
      const file = path.join(this.bootstrap,'native','Publish-Release.ps1');
      await noLinks(file); requireValue((await fs.stat(file)).isFile(),'Frozen publisher missing');
      return this.selectPublisher(file);
    }
    // The active receipt identifies an immutable GitHub commit, not trusted
    // executable code. Authenticate its pointer against that commit first.
    const receiptFile = path.join(this.root,'releases',pointer.directory,'.verified.json');
    const receipt = await strictFile(receiptFile,1024*1024);
    requireValue(await fileHash(receiptFile) === pointer.receiptSha256,'Control receipt changed');
    exact(receipt,['schemaVersion','version','bundleSha256','manifestSha256','commit','dependencies'],'control receipt');
    requireValue(receipt.schemaVersion === 1 && receipt.version === pointer.version && receipt.bundleSha256 === pointer.bundleSha256 &&
      receipt.manifestSha256 === pointer.manifestSha256 && typeof receipt.commit === 'string' && COMMIT.test(receipt.commit),'Invalid active control provenance');
    const raw = await this.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/${receipt.commit}/deployment/control-plane.json`,16384);
    const descriptor = validateControlDescriptor(parseStrictJson(raw.toString('utf8')));
    requireValue(descriptor.version === pointer.version && descriptor.sha256 === pointer.bundleSha256,'Control intent mismatch');
    const bytes = await this.channel.archive(descriptor);
    requireValue(bytes.length === descriptor.sizeBytes && digest(bytes) === descriptor.sha256,'Control archive hash mismatch');
    const archive = path.join(this.cache,'control',`${crypto.randomUUID()}.zip`);
    const directory = path.join(this.cache,'control',`${pointer.directory}-${crypto.randomUUID()}`);
    // Archive is already authenticated; use the frozen extractor, never a
    // release-provided JavaScript extractor or live bot validation module.
    const handle = await fs.open(archive,'wx');
    try { await handle.writeFile(bytes); await handle.sync(); } finally { await handle.close(); }
    const extractor = path.join(this.bootstrap,'Expand-ControlBundle.ps1'); await noLinks(extractor);
    await this.run(this.settings.powershell,['-NoProfile','-NonInteractive','-File',extractor,'-Archive',archive,'-Destination',directory,
      '-ExpectedVersion',descriptor.version,'-ExpectedSha256',descriptor.sha256,'-ExpectedSize',String(descriptor.sizeBytes)]);
    const manifestFile = path.join(directory,'CONTROL.json');
    const manifest = validateControlManifest(await strictFile(manifestFile),descriptor);
    requireValue(await fileHash(manifestFile) === pointer.manifestSha256,'Control manifest mismatch');
    for (const file of manifest.files) {
      const target = path.join(directory,...file.path.split('/')); await noLinks(target);
      const stat = await fs.stat(target);
      requireValue(stat.isFile() && stat.size === file.sizeBytes && await fileHash(target) === file.sha256,'Protected control payload mismatch');
    }
    return this.selectPublisher(path.join(directory,'KESAS-VPS-Server','Publish-Release.ps1'));
  }
  async journalPresent() {
    const file = path.join(this.settings.nativeRoot,'.update-transaction.json'); await noLinks(file);
    try { await fs.lstat(file); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  }
  async activeMatches(version) {
    if (await this.journalPresent()) return false;
    const feed = await strictFile(path.join(this.settings.nativeRoot,'Updates','APFClientUpdate.json'),1024*1024);
    const pack = await strictFile(path.join(this.settings.nativeRoot,'Server','Config','APFPack.json'),1024*1024);
    return feed.version === version && pack.packVersion === version && pack.requiredClientVersion === version;
  }
  async health() {
    // Read only the two required private configuration fields. Never persist
    // the deployment document, pin, health URL, child output, or exceptions.
    const configuration = await strictFile(path.join(this.settings.nativeRoot,'deployment.json'),32768);
    const pin = configuration.certificateSha256, port = configuration.packPort;
    requireValue(typeof pin === 'string' && /^[a-fA-F0-9]{64}$/.test(pin) && Number.isSafeInteger(port) && port >= 1024 && port <= 65535,'Invalid pinned health settings');
    const tool = path.join(this.settings.nativeRoot,'CertificateTool','ApfCertificateTool.exe'); await noLinks(tool);
    await this.run(tool,['--probe-server',`https://127.0.0.1:${port}/health`,pin]);
    return 'healthy';
  }
  async guardArgs(request, now = Date.now()) {
    // A request cannot supply a force flag. Only the existing, completed
    // 15-minute live countdown can authorize occupied-server publication.
    try {
      const state = await strictFile(path.join(this.settings.botRoot,'state','kesas-update-state.json'));
      const timestamp = value => {
        if (typeof value !== 'string') return NaN;
        const parsed = Date.parse(value);
        return Number.isFinite(parsed) && new Date(parsed).toISOString() === value ? parsed : NaN;
      };
      const started = timestamp(state.countdownStartedAt), deadline = timestamp(state.deployAt);
      if (state.schemaVersion !== 1 || state.status !== 'deploying' || state.version !== request.version || state.force !== true ||
          state.countdownMinutes !== 15 || !Number.isFinite(started) || !Number.isFinite(deadline) ||
          deadline - started < 900000 || !Number.isFinite(now) || now < deadline ||
          !Array.isArray(state.countdownAnnouncements) || !state.countdownAnnouncements.includes(900000)) return [];
      const bridge = await strictFile(path.join(this.settings.nativeRoot,'Server','APFDiscordBridge','status.json'),4*1024*1024,undefined,true);
      const age = now - timestamp(bridge.updatedAt);
      if (!Number.isFinite(age) || age < 0 || age > 10000 || !Number.isSafeInteger(bridge.playerCount) || bridge.playerCount < 0 ||
          !Number.isSafeInteger(bridge.connectedCount) || bridge.connectedCount < bridge.playerCount) return [];
      return ['-AllowActivePlayers'];
    } catch { return []; }
  }
  async authorizeReviewedRetry(id, expectedHash, options = {}) {
    requireValue(HASH.test(id) && HASH.test(expectedHash),'Invalid reviewed recovery identity');
    await this.initialize();
    const release = options.ownerHeld ? async()=>{} : await acquireOwner(this.cache,{isAlive:this.isAlive});
    requireValue(release,'Native agent is busy; no recovery transition');
    try {
      const file=path.join(this.cache,'results',`${id}.json`);
      requireValue(await fileHash(file)===expectedHash,'Protected result changed; obtain fresh evidence');
      const previous=await this.result(id);
      requireValue(previous?.phase==='blocked' && (!previous.nativePid || !await this.isAlive(previous.nativePid)) && await this.idle() && !await this.journalPresent(),'Recovery requires a blocked, exited, lease-idle preactivation attempt');
      let rollback=null;
      if(options.rollbackEvidenceSha256) {
        requireValue(HASH.test(options.rollbackEvidenceSha256)&&!options.remote,'Rollback review is host-operator only');
        const evidence=path.join(this.cache,'recoveries',id,`rollback-review-${expectedHash}.json`);
        requireValue(await fileHash(evidence)===options.rollbackEvidenceSha256,'Protected rollback evidence changed');
        rollback=await strictFile(evidence,65536);
        requireValue(rollback.schemaVersion===1&&rollback.action==='host-reviewed-rollback'&&rollback.requestId===id&&rollback.priorResultSha256===expectedHash&&
          rollback.archiveSha256===previous.sha256&&rollback.version===previous.version&&HASH.test(rollback.failedStateSha256)&&HASH.test(rollback.preparedRecordSha256)&&
          typeof rollback.requiredControlVersion==='string'&&/^control-[1-9][0-9]{0,8}$/.test(rollback.requiredControlVersion),'Rollback evidence identity mismatch');
        if(rollback.publisherRepairSha256) {
          requireValue(typeof rollback.publisherRepairSha256==='string'&&HASH.test(rollback.publisherRepairSha256),'Invalid reviewed publisher digest');
          await this.reviewedPublisher(id,expectedHash,rollback.publisherRepairSha256);
        }
      }
      if(options.remote) {
        requireValue(PREACTIVATION_STAGES.has(previous.stage)&&previous.nativePid===null,'Remote recovery cannot replay a publisher that may have started');
        requireValue(previous.version===options.remote.version&&previous.sha256===options.remote.archiveSha256&&previous.sourceCommit===options.remote.sourceCommit,'Remote recovery immutable identity mismatch');
      }
      const request=validateRequest(Object.fromEntries(REQUEST_FIELDS.map(key=>[key,previous[key]])),id);
      await authenticateApplication(this.channel,request);
      const archive=path.join(this.cache,'applications',`${id}.zip`);
      if(options.remote)await this.copyApplication(request);
      requireValue(await fileHash(archive)===request.sha256,'Protected archive changed');
      for(const relative of [`Updates/${request.version}`,...(rollback?[]:[`.github-prepared/${request.version}`])]) {
        const target=path.join(this.settings.nativeRoot,relative);await noLinks(target);
        requireValue(!await fs.stat(target).then(()=>true,error=>{if(error.code==='ENOENT')return false;throw error;}),'Prepared or activated version requires separate recovery review');
      }
      const feed=await strictFile(path.join(this.settings.nativeRoot,'Updates','APFClientUpdate.json'),1024*1024);
      const pack=await strictFile(path.join(this.settings.nativeRoot,'Server','Config','APFPack.json'),1024*1024);
      requireValue(/^kesas-rc[1-9][0-9]*$/.test(feed.version) && feed.version===pack.packVersion && feed.version===pack.requiredClientVersion && BigInt(feed.version.slice(8))<BigInt(request.version.slice(8)),'Active release is inconsistent or not older');
      if(rollback)requireValue(rollback.activeVersion===feed.version&&await fileHash(path.join(this.settings.nativeRoot,'.github-prepared',request.version,'prepared.json'))===rollback.preparedRecordSha256,'Reviewed rollback baseline or prepared release changed');
      await this.health();
      requireValue(await this.idle() && !await this.journalPresent(),'Native state changed during recovery review');
      const directory=path.join(this.cache,'recoveries',id);await noLinks(directory);await fs.mkdir(directory,{recursive:true});
      const history=path.join(directory,`${expectedHash}.json`);
      try {const handle=await fs.open(history,'wx');try{await handle.writeFile(JSON.stringify(previous));await handle.sync();}finally{await handle.close();}}
      catch(error){if(error.code!=='EEXIST')throw error;throw new Error('Recovery authorization already consumed; preserve history');}
      const authorization={schemaVersion:1,action:rollback?'host-reviewed-rollback':options.remote?'github-reviewed-preactivation':'host-reviewed-preactivation',priorResultSha256:expectedHash,authorizedAt:new Date().toISOString(),activeVersion:feed.version,
        ...(options.remote?{recoveryId:options.remote.recoveryId,recoveryCommit:options.remote.recoveryCommit}:{}),...(rollback?{rollbackEvidenceSha256:options.rollbackEvidenceSha256,
          ...(rollback.publisherRepairSha256?{publisherRepairSha256:rollback.publisherRepairSha256}:{minimumControlVersion:rollback.requiredControlVersion})}:{} )};
      await writeJson(path.join(directory,`${expectedHash}.authorization.json`),authorization);
      if(rollback) {
        // Inherit the fixed bootstrap's protected-write/readable permissions.
        // The limited queue may consume this approval, never create one.
        const approvals=path.join(this.bootstrap,'recovery-approvals');await noLinks(approvals);await fs.mkdir(approvals,{recursive:true});
        await writeJson(path.join(approvals,`${id}-${expectedHash}.json`),{schemaVersion:1,action:'host-reviewed-rollback',requestId:id,priorResultSha256:expectedHash,
          version:request.version,activeVersion:feed.version,failedStateSha256:rollback.failedStateSha256,evidenceSha256:options.rollbackEvidenceSha256,authorizedAt:authorization.authorizedAt});
      }
      return await this.save({...previous,attempt:(previous.attempt||1)+1,recovery:authorization},'recovery-authorized',{nativePid:null,failure:null,stage:'awaiting-new-guard',exitCode:null});
    } catch(error) {await this.privateFailure({id},'recovery-authorization',error).catch(()=>{});throw error;
    } finally {await release();}
  }
  async pollRecovery() {
    if(Date.now()<this.nextRecoveryPoll)return;
    this.nextRecoveryPoll=Date.now()+60000;
    let intent,ledger;
    try {
      let observed;
      try{observed=await this.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/deployment/recovery.json`,16384);}
      catch(error){if(error.httpStatus===404)return;throw error;}
      intent=parseStrictJson(observed.toString('utf8'));
      exact(intent,RECOVERY_FIELDS,'recovery intent');
      requireValue([intent.recoveryId,intent.requestId,intent.archiveSha256,intent.expectedResultSha256].every(value=>typeof value==='string'&&HASH.test(value))&&
        typeof intent.sourceCommit==='string'&&COMMIT.test(intent.sourceCommit)&&typeof intent.version==='string'&&
        typeof intent.issuedAt==='string'&&typeof intent.expiresAt==='string','Invalid recovery field types');
      const now=Date.now(),issued=Date.parse(intent.issuedAt),expires=Date.parse(intent.expiresAt);
      requireValue(intent.schemaVersion===1&&intent.action==='retry-preactivation'&&HASH.test(intent.recoveryId)&&HASH.test(intent.requestId)&&
        HASH.test(intent.archiveSha256)&&HASH.test(intent.expectedResultSha256)&&COMMIT.test(intent.sourceCommit)&&
        /^kesas-rc[1-9][0-9]{0,14}$/.test(intent.version)&&Number.isFinite(issued)&&Number.isFinite(expires)&&
        new Date(issued).toISOString()===intent.issuedAt&&new Date(expires).toISOString()===intent.expiresAt&&
        issued<=now+5000&&expires>now&&expires>issued&&expires-issued<=3600000,'Invalid or expired recovery intent');
      const candidateLedger=path.join(this.cache,'recoveries',`remote-${intent.recoveryId}.json`);
      if(await strictFile(candidateLedger,16384,null))return;
      const reference=parseStrictJson((await this.channel.bytes(`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`,16384)).toString('utf8'));
      const commit=reference.object?.sha;
      requireValue(reference.ref==='refs/heads/deploy'&&reference.object?.type==='commit'&&COMMIT.test(commit),'Invalid recovery reference');
      const pinned=await this.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/${commit}/deployment/recovery.json`,16384);
      requireValue(digest(observed)===digest(pinned)&&Date.now()<expires,'Recovery channel changed or expired');
      ledger=candidateLedger;
      // Consume before acting. Interrupted/ambiguous authorization stays held;
      // never replay a consumed nonce or clear an original result/journal.
      await writeJson(ledger,{schemaVersion:1,recoveryId:intent.recoveryId,requestId:intent.requestId,commit,status:'received',observedAt:new Date().toISOString()});
      await writeJson(path.join(this.root,'native-recovery-status.json'),{recoveryId:intent.recoveryId,requestId:intent.requestId,commit,status:'received',reason:null});
      await this.authorizeReviewedRetry(intent.requestId,intent.expectedResultSha256,{ownerHeld:true,remote:{...intent,recoveryCommit:commit}});
      await writeJson(ledger,{schemaVersion:1,recoveryId:intent.recoveryId,requestId:intent.requestId,commit,status:'authorized',observedAt:new Date().toISOString()});
      await writeJson(path.join(this.root,'native-recovery-status.json'),{recoveryId:intent.recoveryId,requestId:intent.requestId,commit,status:'authorized',reason:null});
    } catch(error) {
      // Details remain private. No failed network poll can stop the agent.
      if(typeof intent?.requestId==='string'&&HASH.test(intent.requestId))await this.privateFailure({id:intent.requestId},'remote-recovery',error).catch(()=>{});
      if(ledger){
        await writeJson(ledger,{schemaVersion:1,recoveryId:intent.recoveryId,requestId:intent.requestId,status:'blocked',reason:'recovery-not-authorized',observedAt:new Date().toISOString()}).catch(()=>{});
        await writeJson(path.join(this.root,'native-recovery-status.json'),{recoveryId:intent.recoveryId,requestId:intent.requestId,commit:null,status:'blocked',reason:'recovery-not-authorized'}).catch(()=>{});
      }
    }
  }
  async recoveryAdmitted(result) {
    try {
      if(result.recovery.minimumControlVersion) {
        const pointer=validatePointer(await strictFile(path.join(this.root,'active.json')));
        const proof=await strictFile(path.join(this.root,'verified-active.json'),65536,null);
        if(pointer.sequence<Number(result.recovery.minimumControlVersion.slice(8))||proof?.version!==pointer.version||proof.bundleSha256!==pointer.bundleSha256||!Number.isFinite(Date.parse(proof.verifiedAt)))return false;
      }
      const state=await strictFile(path.join(this.settings.botRoot,'state','kesas-update-state.json'));
      const started=Date.parse(state.countdownStartedAt),authorized=Date.parse(result.recovery.authorizedAt),deadline=Date.parse(state.deployAt);
      if(state.status!=='deploying'||state.phase!=='installing'||state.version!==result.version||state.notes!==result.notes||
        !Number.isFinite(started)||!Number.isFinite(authorized)||!Number.isFinite(deadline)||started<authorized||started>Date.now()+5000||
        state.countdownMinutes!==15||deadline-started<900000)return false;
      if((await this.guardArgs(result)).length)return true;
      const bridge=await strictFile(path.join(this.settings.nativeRoot,'Server','APFDiscordBridge','status.json'),4*1024*1024,undefined,true);
      const age=Date.now()-Date.parse(bridge.updatedAt);
      return state.force===false && Number.isFinite(age) && age>=0 && age<=10000 && bridge.playerCount===0 && bridge.connectedCount===0;
    } catch {return false;}
  }
  async privateFailure(result,stage,error) {
    const attempt=Number.isInteger(result.attempt)?result.attempt:1;
    const directory=path.join(this.cache,'diagnostics',result.id,`agent-${attempt}`);
    await noLinks(directory);await fs.mkdir(directory,{recursive:true});
    await writeJson(path.join(directory,`${stage}.json`),{at:new Date().toISOString(),stage,exitCode:Number.isInteger(error?.exitCode)?error.exitCode:null,signal:error?.signal||null,message:String(error?.stack||error).slice(0,8192),output:error?.privateDetail||null});
  }
  async recover(result) {
    // Never kill or replay a publisher, including after parent/process death.
    // A recorded child may still be starting, before acquiring the OS lease.
    if (Number.isSafeInteger(result.nativePid) && result.nativePid > 0 && await this.isAlive(result.nativePid)) return this.mirror(result);
    if (!await this.idle()) return this.mirror(result);
    const receipt=Number.isInteger(result.attempt)?await strictFile(path.join(this.cache,'diagnostics',result.id,String(result.attempt),'receipt.json'),4096,null):null;
    const proved=receipt?.requestId===result.id&&receipt.attempt===result.attempt&&receipt.status==='succeeded'&&receipt.exitCode===0&&
      await fileHash(path.join(this.cache,'applications',`${result.id}.zip`))===result.sha256;
    if (!proved||!await this.activeMatches(result.version)) return this.save(result,'blocked',{failure:FAILURE,health:'unknown'});
    const health = await this.health();
    requireValue(await this.idle() && await this.activeMatches(result.version),'Native state changed during health check');
    return this.save(result,'healthy',{failure:null,health,completedAt:new Date().toISOString()});
  }
  async handle(id) {
    let result = await this.result(id);
    if (result && TERMINAL.has(result.phase)) return this.mirror(result);
    if(result?.phase==='recovery-authorized'&&!await this.recoveryAdmitted(result))return this.mirror(result);
    let stage='request-validation';
    try {
      if (result?.phase === 'applying') return await this.recover(result);
      const request = result ? validateRequest(Object.fromEntries(REQUEST_FIELDS.map(key => [key,result[key]])),id) :
        validateRequest(await strictFile(path.join(this.root,'native-requests',`${id}.json`),16384),id);
      if (!result) result = await this.save({...request,acceptedAt:new Date().toISOString(),health:'unknown',failure:null,nativePid:null},'accepted');
      stage='intent-authentication';
      result=await this.save(result,result.phase,{stage});
      const intent = await authenticateApplication(this.channel,request);
      if (!await this.idle()) return this.mirror(result);
      requireValue(!await this.journalPresent(),'Interrupted native publication needs review');
      stage='archive-copy';
      result=await this.save(result,result.phase,{stage});
      const archive = await this.copyApplication(request);
      stage='fixed-verifier';
      result=await this.save(result,result.phase,{stage});
      const verifier = path.join(this.bootstrap,'Verify-KesasRelease.ps1'); await noLinks(verifier);
      await this.run(this.settings.powershell,['-NoProfile','-NonInteractive','-File',verifier,'-Archive',archive,'-ExpectedVersion',request.version]);
      stage='publisher-validation';
      result=await this.save(result,result.phase,{stage});
      const publisher = await this.publisher(request,result);
      // Recheck after downloads/validation, before making publication durable.
      if (!await this.idle()) return this.mirror(result);
      requireValue(!await this.journalPresent() && await fileHash(archive) === request.sha256,'Native pre-publication state changed');
      result = await this.save(result,'applying',{authenticatedCommit:intent.commit,attempt:result.attempt||1,stage:'publisher-execution',exitCode:null});
      await this.fault('applying',result);
      const guard = await this.guardArgs(request);
      stage='publisher-execution';
      const wrapper=path.join(this.bootstrap,'Invoke-NativePublisher.ps1');await noLinks(wrapper);
      const invocationArgs=['-NoProfile','-NonInteractive','-File',wrapper,
        '-Settings',path.join(this.root,'host.json'),'-RequestId',id,'-Attempt',String(result.attempt),'-Publisher',publisher,'-Archive',archive,'-Notes',request.notes,...guard];
      const launchDirectory=path.join(this.cache,'diagnostics',id,`agent-${result.attempt}`);
      await noLinks(launchDirectory);await fs.mkdir(launchDirectory,{recursive:true});
      await writeJson(path.join(launchDirectory,'launch.json'),{file:this.settings.powershell,args:invocationArgs,wrapperSha256:await fileHash(wrapper),createdAt:new Date().toISOString()});
      const outputFiles=[];
      try {
      for(const name of ['startup.stdout.log','startup.stderr.log'])outputFiles.push(await fs.open(path.join(launchDirectory,name),'wx'));
      await this.run(this.settings.powershell,invocationArgs,{
        // No parent-owned pipes: parent death must not break helper output.
        // DETACHED_PROCESS causes this Windows console host to exit before
        // executing -File. Ordinary Windows children outlive a terminated
        // parent; file handles, durable PID and OS lease preserve recovery.
        detached:process.platform!=='win32', captureOutput:false, outputHandles:{stdout:outputFiles[0].fd,stderr:outputFiles[1].fd},env:{...process.env,APF_SERVER_TASK:'KESAS Server',APF_CONTROL_ROOT:this.root}, onSpawn:async pid => {
          if (Number.isSafeInteger(pid) && pid > 0) result = await this.save(result,'applying',{nativePid:pid});
        }
      });
      } finally {
        for(const handle of outputFiles){
          try{const stat=await handle.stat();if(stat.size>65536){const marker=Buffer.from('\n[startup output truncated at 65536 bytes]\n');await handle.write(marker,0,marker.length,65536-marker.length);await handle.truncate(65536);}}finally{await handle.close().catch(()=>{});}
        }
      }
      const receipt=await strictFile(path.join(this.cache,'diagnostics',id,String(result.attempt),'receipt.json'),4096,null);
      requireValue(receipt?.requestId===id&&receipt.attempt===result.attempt&&receipt.status==='succeeded'&&receipt.exitCode===0,'Publisher did not produce a successful private receipt');
      result=await this.save(result,'applying',{stage:'completion-health',exitCode:0});
      await this.fault('published',result);
      stage='completion-health';
      return await this.recover(result);
    } catch (error) {
      if (error?.simulatedCrash) throw error;
      // Failure details can contain private paths, tokens, or native stderr.
      await this.privateFailure(result||{id},stage,error).catch(()=>{});
      if(stage==='publisher-execution'&&result?.attempt) {
        const receipt=await strictFile(path.join(this.cache,'diagnostics',id,String(result.attempt),'receipt.json'),4096,null).catch(()=>null);
        if(receipt?.requestId===id&&receipt.attempt===result.attempt&&['guard-admission','preparation-signing','media-publication','native-activation','github-completion'].includes(receipt.stage))stage=receipt.stage;
      }
      if(['publisher-execution','guard-admission','preparation-signing','media-publication','native-activation','github-completion'].includes(stage)&&result?.phase==='applying'&&(!result.nativePid||!await this.isAlive(result.nativePid))&&await this.idle()&&await this.activeMatches(result.version)) {
        try{return await this.recover(result);}catch{}
      }
      return this.save(result || {schemaVersion:1,transactionVersion:1,id,version:null,sha256:null},'blocked',{failure:FAILURE,stage,exitCode:Number.isInteger(error?.exitCode)?error.exitCode:null,health:'unknown'});
    }
  }
  async tick() {
    if (this.busy) return {status:'busy'};
    this.busy = true; let release;
    try {
      await this.initialize();
      release = await acquireOwner(this.cache,{isAlive:this.isAlive});
      if (!release) return {status:'busy'};
      // Protected journals are authoritative. A writable request/result inbox
      // cannot forge completion, reset a failure, or hide an orphaned helper.
      const saved = (await fs.readdir(path.join(this.cache,'results'))).filter(name => /^[a-f0-9]{64}\.json$/.test(name)).sort();
      let completed = null;
      for (const name of saved) {
        const value = await this.result(name.slice(0,-5));
        if (value.phase === 'applying') return await this.handle(value.id);
      }
      await this.pollRecovery();
      for (const name of saved) {
        const value = await this.result(name.slice(0,-5));
        if(value.phase==='recovery-authorized'&&!await this.recoveryAdmitted(value))continue;
        if (value.phase === 'accepted' || value.phase === 'recovery-authorized') return await this.handle(value.id);
      }
      const requests = path.join(this.root,'native-requests'); await noLinks(requests);
      const names = await fs.readdir(requests).catch(error => { if (error.code === 'ENOENT') return []; throw error; });
      for (const name of names.filter(name => /^[a-f0-9]{64}\.json$/.test(name)).sort()) {
        const id = name.slice(0,-5), value = await this.result(id);
        if (!value) return await this.handle(id);
        completed = await this.mirror(value);
      }
      return completed || {status:'idle'};
    } finally { try { await release?.(); } finally { this.busy = false; } }
  }
  async runForever(interval = 1000) {
    while (!this.stop) { await this.tick(); if (!this.stop) await pause(interval); }
  }
}

export async function runCli(argv = process.argv.slice(2), options = {}) {
  try {
    const recovering=argv.length===6&&argv[2]==='--authorize-reviewed-retry'&&argv[4]==='--expected-result-sha256';
    const rollback=argv.length===8&&argv[2]==='--authorize-reviewed-rollback-retry'&&argv[4]==='--expected-result-sha256'&&argv[6]==='--expected-rollback-evidence-sha256';
    requireValue((argv.length === 2||recovering||rollback) && argv[0] === '--settings' && path.isAbsolute(argv[1]),'Expected fixed native agent settings or reviewed retry');
    const settings = validateSettings(await strictFile(argv[1],32768));
    const agent = new NativeAgent(settings,options);
    if(recovering||rollback){await agent.authorizeReviewedRetry(argv[3],argv[5],rollback?{rollbackEvidenceSha256:argv[7]}:{});return 0;}
    const stop = () => { agent.stop = true; };
    process.on('SIGINT',stop); process.on('SIGTERM',stop);
    try { await agent.runForever(); }
    finally { process.removeListener('SIGINT',stop); process.removeListener('SIGTERM',stop); }
    return 0;
  } catch { (options.stderr || process.stderr).write('Native publication agent blocked; journals preserved.\n'); return 1; }
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = await runCli();
