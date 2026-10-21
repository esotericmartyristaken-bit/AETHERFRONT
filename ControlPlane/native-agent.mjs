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

// No execFile timeout, shell, kill, or inherited output. A detached publisher
// retains its native OS lease if this agent dies; recovery only observes it.
export async function runChild(file, args, options = {}, spawnChild = spawn) {
  const {onSpawn, captureOutput = true, ...settings} = options;
  const child = spawnChild(file, args, {...settings, windowsHide:true, shell:false, stdio:['ignore',captureOutput ? 'pipe' : 'ignore','ignore']});
  let stdout = '', overflow = false;
  child.stdout?.on('data', bytes => {
    if (Buffer.byteLength(stdout) + bytes.length > 16384) overflow = true;
    else stdout += bytes.toString('utf8');
  });
  let started = Promise.resolve();
  const exited = new Promise((resolve, reject) => {
    child.once('error', () => reject(new Error(FAILURE)));
    child.once('close', (code, signal) => resolve({code,signal}));
    if (onSpawn) started = Promise.resolve().then(() => onSpawn(child.pid)).then(() => null, () => new Error(FAILURE));
  });
  const result = await exited;
  const startError = await started;
  requireValue(!startError && result.code === 0 && !result.signal && !overflow, FAILURE);
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
async function strictFile(file, maximum = 65536, absent = undefined) {
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
    return parseStrictJson(bytes.subarray(0,length).toString('utf8'));
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
  }
  async initialize() {
    // Installer owns ACLs (Administrators High + SYSTEM). Do not create an
    // unprotected cache or modify host identities/permissions at runtime.
    await noLinks(this.cache);
    requireValue((await fs.stat(this.cache)).isDirectory(), 'Protected native cache missing');
    for (const directory of [path.join(this.cache,'results'),path.join(this.cache,'applications'),path.join(this.cache,'control'),path.join(this.root,'native-results')]) {
      await noLinks(directory); await fs.mkdir(directory,{recursive:true});
    }
  }
  async result(id) {
    requireValue(typeof id === 'string' && HASH.test(id),'Invalid native result identity');
    const value = await strictFile(path.join(this.cache,'results',`${id}.json`),65536,null);
    if (value) requireValue(value.schemaVersion === 1 && value.transactionVersion === 1 && value.id === id &&
      ['accepted','applying','healthy','blocked'].includes(value.phase) && value.status === value.phase,'Invalid native result');
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
  async publisher() {
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
      const bridge = await strictFile(path.join(this.settings.nativeRoot,'Server','APFDiscordBridge','status.json'),4*1024*1024);
      const age = now - timestamp(bridge.updatedAt);
      if (!Number.isFinite(age) || age < 0 || age > 10000 || !Number.isSafeInteger(bridge.playerCount) || bridge.playerCount < 0 ||
          !Number.isSafeInteger(bridge.connectedCount) || bridge.connectedCount < bridge.playerCount) return [];
      return ['-AllowActivePlayers'];
    } catch { return []; }
  }
  async recover(result) {
    // Never kill or replay a publisher, including after parent/process death.
    // A recorded child may still be starting, before acquiring the OS lease.
    if (Number.isSafeInteger(result.nativePid) && result.nativePid > 0 && await this.isAlive(result.nativePid)) return this.mirror(result);
    if (!await this.idle()) return this.mirror(result);
    if (!await this.activeMatches(result.version)) return this.save(result,'blocked',{failure:FAILURE,health:'unknown'});
    const health = await this.health();
    requireValue(await this.idle() && await this.activeMatches(result.version),'Native state changed during health check');
    return this.save(result,'healthy',{failure:null,health,completedAt:new Date().toISOString()});
  }
  async handle(id) {
    let result = await this.result(id);
    if (result && TERMINAL.has(result.phase)) return this.mirror(result);
    try {
      if (result?.phase === 'applying') return await this.recover(result);
      const request = result ? validateRequest(Object.fromEntries(REQUEST_FIELDS.map(key => [key,result[key]])),id) :
        validateRequest(await strictFile(path.join(this.root,'native-requests',`${id}.json`),16384),id);
      if (!result) result = await this.save({...request,acceptedAt:new Date().toISOString(),health:'unknown',failure:null},'accepted');
      const intent = await authenticateApplication(this.channel,request);
      if (!await this.idle()) return this.mirror(result);
      requireValue(!await this.journalPresent(),'Interrupted native publication needs review');
      const archive = await this.copyApplication(request);
      const verifier = path.join(this.bootstrap,'Verify-KesasRelease.ps1'); await noLinks(verifier);
      await this.run(this.settings.powershell,['-NoProfile','-NonInteractive','-File',verifier,'-Archive',archive,'-ExpectedVersion',request.version]);
      const publisher = await this.publisher();
      // Recheck after downloads/validation, before making publication durable.
      if (!await this.idle()) return this.mirror(result);
      requireValue(!await this.journalPresent() && await fileHash(archive) === request.sha256,'Native pre-publication state changed');
      result = await this.save(result,'applying',{authenticatedCommit:intent.commit});
      await this.fault('applying',result);
      const guard = await this.guardArgs(request);
      await this.run(this.settings.powershell,['-NoProfile','-NonInteractive','-File',publisher,
        '-DeploymentRoot',this.settings.nativeRoot,'-ReleaseZip',archive,'-Notes',request.notes,...guard],{
        // No parent-owned pipes: parent death must not break helper output.
        detached:true, captureOutput:false, env:{...process.env,APF_SERVER_TASK:'KESAS Server',APF_CONTROL_ROOT:this.root}, onSpawn:async pid => {
          if (Number.isSafeInteger(pid) && pid > 0) result = await this.save(result,'applying',{nativePid:pid});
        }
      });
      await this.fault('published',result);
      return await this.recover(result);
    } catch (error) {
      if (error?.simulatedCrash) throw error;
      // Failure details can contain private paths, tokens, or native stderr.
      return this.save(result || {schemaVersion:1,transactionVersion:1,id,version:null,sha256:null},'blocked',{failure:FAILURE,health:'unknown'});
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
      for (const name of saved) {
        const value = await this.result(name.slice(0,-5));
        if (value.phase === 'accepted') return await this.handle(value.id);
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
    requireValue(argv.length === 2 && argv[0] === '--settings' && path.isAbsolute(argv[1]),'Expected --settings privatehost.json');
    const settings = validateSettings(await strictFile(argv[1],32768));
    const agent = new NativeAgent(settings,options);
    const stop = () => { agent.stop = true; };
    process.on('SIGINT',stop); process.on('SIGTERM',stop);
    try { await agent.runForever(); }
    finally { process.removeListener('SIGINT',stop); process.removeListener('SIGTERM',stop); }
    return 0;
  } catch { (options.stderr || process.stderr).write('Native publication agent blocked; journals preserved.\n'); return 1; }
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = await runCli();
