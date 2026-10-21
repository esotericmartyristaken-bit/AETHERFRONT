import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { writeJsonAtomic } from './util.js';
import { assertCapabilities } from '../../ControlPlane/contract.mjs';
import { parseStrictJson } from '../../ControlPlane/github.mjs';

const REPOSITORY = 'esotericmartyristaken-bit/AETHERFRONT';
const MANIFEST_PATH = 'deployment/production.json';
const MAX_ARCHIVE = 512 * 1024 * 1024;
const MAX_TEXT = 64 * 1024;
const digest = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

export function releaseNumber(version) {
  if (typeof version !== 'string' || !/^kesas-rc[1-9][0-9]{0,14}$/.test(version)) throw new Error('Invalid deployment version');
  return BigInt(version.slice(8));
}

export function validateDeployment(value) {
  if (!value || ![1, 2].includes(value.schemaVersion)) throw new Error('Invalid deployment manifest schema');
  releaseNumber(value.version);
  if (value.releaseTag !== `host-${value.version}` || value.assetName !== `Aetherfront-Release-${value.version}.zip` ||
      !/^[a-f0-9]{64}$/.test(value.sha256 || '') || !Number.isSafeInteger(value.sizeBytes) ||
      value.sizeBytes < 1 || value.sizeBytes > MAX_ARCHIVE || typeof value.notes !== 'string' ||
      value.notes.length > 500 || /[\p{Cc}\p{Cf}]/u.test(value.notes)) throw new Error('Invalid deployment artifact metadata');
  // No caller-controlled URL, command, filesystem destination or force flag.
  const allowed = new Set(['schemaVersion', 'version', 'releaseTag', 'assetName', 'sha256', 'sizeBytes', 'notes']);
  if (value.schemaVersion === 2) {
    allowed.add('requirements');
    const requirements = value.requirements;
    if (!requirements || Object.keys(requirements).length !== 2 || requirements.contractVersion !== 1 ||
        !Array.isArray(requirements.capabilities) || !requirements.capabilities.length ||
        new Set(requirements.capabilities).size !== requirements.capabilities.length ||
        requirements.capabilities.some(item => typeof item !== 'string' || !/^[a-z][a-z0-9-]{0,79}$/.test(item)) ||
        Object.keys(requirements).some(key => !['contractVersion', 'capabilities'].includes(key))) throw new Error('Unsupported deployment requirements');
  }
  if (Object.keys(value).some(key => !allowed.has(key))) throw new Error('Unsupported deployment manifest field');
  return value;
}

function allowedUrl(raw, release = false) {
  const url = new URL(raw);
  const hosts = release
    ? new Set(['github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com'])
    : new Set(['raw.githubusercontent.com', 'api.github.com']);
  if (url.protocol !== 'https:' || url.port || url.username || url.password || url.hash || !hosts.has(url.hostname)) {
    throw new Error('Untrusted deployment download location');
  }
  return url.href;
}

async function responseFor(fetcher, raw, signal, release) {
  let location = allowedUrl(raw, release);
  for (let redirects = 0; redirects < 5; redirects++) {
    const response = await fetcher(location, { signal, redirect: 'manual', headers: { 'User-Agent': 'Aetherfront-Deployment', Accept: 'application/json, application/octet-stream' } });
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const next = response.headers.get('location');
      await response.body?.cancel();
      if (!next) throw new Error('Deployment redirect has no location');
      location = allowedUrl(new URL(next, location).href, release);
      continue;
    }
    if (!response.ok) { await response.body?.cancel(); throw new Error(`GitHub deployment HTTP ${response.status}`); }
    return response;
  }
  throw new Error('Too many deployment redirects');
}

async function boundedBytes(response, maximum) {
  const chunks = [];
  let size = 0;
  try {
    for await (const chunk of response.body) {
      size += chunk.length;
      if (size > maximum) throw new Error('Deployment metadata exceeds size limit');
      chunks.push(Buffer.from(chunk));
    }
    return Buffer.concat(chunks);
  } catch (error) { await response.body?.cancel().catch(() => {}); throw error; }
}

async function noLinks(target) {
  let cursor = path.resolve(target);
  while (true) {
    const stat = await fs.lstat(cursor).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
    if (stat?.isSymbolicLink() || (stat?.isFile() && stat.nlink !== 1)) throw new Error('Deployment paths cannot be links');
    const parent = path.dirname(cursor);
    if (parent === cursor) return;
    cursor = parent;
  }
}

async function fileMatches(file, manifest) {
  await noLinks(file);
  const stat = await fs.stat(file).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
  if (!stat) return false;
  if (!stat.isFile() || stat.size !== manifest.sizeBytes) throw new Error('Cached deployment size mismatch');
  const handle = await fs.open(file, 'r');
  const hash = crypto.createHash('sha256');
  try {
    for await (const chunk of handle.createReadStream()) hash.update(chunk);
    return hash.digest('hex') === manifest.sha256;
  } finally { await handle.close().catch(() => {}); }
}

async function readState(file) {
  try {
    const value = JSON.parse(await fs.readFile(file, 'utf8'));
    if (!value || value.schemaVersion !== 1) throw new Error('Invalid deployment state; operator review required');
    return value;
  } catch (error) { if (error.code === 'ENOENT') return {}; throw error; }
}

export class GitHubDeployment {
  constructor(config, manager, options = {}) {
    this.config = config;
    this.manager = manager;
    this.settings = config.updates?.githubDeployment || {};
    this.enabled = this.settings.enabled === true;
    if (this.enabled && (!config.lmp?.deploymentRoot || this.settings.repository !== REPOSITORY ||
        this.settings.branch !== 'deploy' || this.settings.manifestPath !== MANIFEST_PATH)) throw new Error('GitHub deployment must target the configured KESAS repository/channel');
    this.fetch = options.fetch || globalThis.fetch;
    this.onEvent = options.onEvent || (() => {});
    this.statePath = path.join(config.__root, 'state', 'github-deployment-state.json');
    this.cache = path.join(config.__root, 'state', 'github-deployment-cache');
    this.activeVersion = options.activeVersion || (async () => {
      const root = config.lmp.deploymentRoot;
      const feed = JSON.parse(await fs.readFile(path.join(root, 'Updates', 'APFClientUpdate.json'), 'utf8'));
      const pack = JSON.parse(await fs.readFile(path.join(root, 'Server', 'Config', 'APFPack.json'), 'utf8'));
      if (pack.requiredClientVersion !== feed.version || pack.packVersion !== feed.version) throw new Error('Active KESAS release is inconsistent; deployment held');
      releaseNumber(feed.version);
      return feed.version;
    });
    this.pollMs = Math.max(60_000, Number(this.settings.pollIntervalMs) || 60_000);
    this.busy = false;
    this.stopped = false;
    this.timer = null;
  }

  async save(state) {
    await noLinks(this.statePath);
    const previous = await readState(this.statePath);
    const next = { ...previous, ...state, schemaVersion: 1, checkedAt: new Date().toISOString() };
    await writeJsonAtomic(this.statePath, next);
    if (previous.status !== next.status || previous.version !== next.version || previous.error !== next.error) {
      await this.onEvent({ status: next.status, version: next.version || null, error: next.error || null });
    }
    return next;
  }

  async json(url) {
    const response = await responseFor(this.fetch, url, AbortSignal.timeout(20_000), false);
    const bytes = await boundedBytes(response, MAX_TEXT);
    return { value: parseStrictJson(bytes.toString('utf8')), sha256: digest(bytes) };
  }

  async download(manifest) {
    await noLinks(this.cache);
    await fs.mkdir(this.cache, { recursive: true });
    const target = path.join(this.cache, `${manifest.sha256}.zip`);
    if (await fileMatches(target, manifest)) return target;
    const temp = `${target}.${crypto.randomUUID()}.part`;
    const file = await fs.open(temp, 'wx');
    const hash = crypto.createHash('sha256');
    let size = 0;
    try {
      const url = `https://github.com/${REPOSITORY}/releases/download/${manifest.releaseTag}/${manifest.assetName}`;
      const response = await responseFor(this.fetch, url, AbortSignal.timeout(15 * 60_000), true);
      try {
        for await (const chunk of response.body) {
          size += chunk.length;
          if (size > manifest.sizeBytes) throw new Error('Deployment archive exceeds declared size');
          hash.update(chunk);
          await file.writeFile(chunk);
        }
      } finally { await response.body?.cancel().catch(() => {}); }
      if (size !== manifest.sizeBytes || hash.digest('hex') !== manifest.sha256) throw new Error('Deployment archive size or SHA256 mismatch');
      await file.sync();
      await file.close();
      await noLinks(target);
      // Content-addressed names are immutable. The bot owns the single poller.
      await fs.rename(temp, target);
      return target;
    } finally { await file.close().catch(() => {}); await fs.rm(temp, { force: true }).catch(() => {}); }
  }

  async tick() {
    if (!this.enabled || this.stopped || this.busy) return { status: 'disabled-or-busy' };
    this.busy = true;
    try {
      if(this.config.__controlRoot&&typeof this.manager.controlMaintenance==='function'&&await this.manager.controlMaintenance())return {status:'waiting-control-health'};
      await noLinks(this.statePath);
      const saved = await readState(this.statePath);
      const observation = await this.json(`https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/${MANIFEST_PATH}`);
      let manifest = validateDeployment(observation.value);
      if (manifest.schemaVersion === 2) {
        if (!this.config.__controlRoot) throw new Error('Required control-plane capability report is unavailable');
        const report = JSON.parse(await fs.readFile(path.join(this.config.__controlRoot, 'host-status.json'), 'utf8'));
        assertCapabilities(report, manifest.requirements.capabilities);
      }
      const active = await this.activeVersion();
      const native = await this.manager.status();
      if (['queued', 'deploying', 'failed'].includes(native.status)) {
        return await this.save({ status: native.status === 'failed' ? 'held-native-failure' : 'waiting-native', version: manifest.version, activeVersion: active, error: native.status === 'failed' ? 'Native deployment requires operator review' : null });
      }
      if (releaseNumber(manifest.version) <= releaseNumber(active)) {
        return await this.save({ status: manifest.version === active ? 'current' : 'ignored-older-version', version: manifest.version, activeVersion: active, manifestSha256: observation.sha256, error: null });
      }
      if (native.lastDeployment?.status === 'cancelled' && native.lastDeployment.version === manifest.version) {
        return await this.save({ status: 'held-cancelled', version: manifest.version, error: 'Deployment was cancelled; publish a new version or obtain operator review' });
      }
      if (saved.attempt?.manifestSha256 === observation.sha256 && ['queue-requested', 'queued', 'rejected'].includes(saved.attempt.status)) {
        return await this.save({ status: 'held-previous-attempt', version: manifest.version, error: 'Previous attempt requires operator review; automatic retry disabled' });
      }
      // Poll raw content without consuming an API request per minute. For a new
      // deployment, resolve and re-read one immutable commit before downloading.
      const reference = await this.json(`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`);
      const commit = reference.value?.object?.sha;
      if (!/^[a-f0-9]{40}$/.test(commit || '')) throw new Error('Invalid deployment branch commit');
      const pinned = await this.json(`https://raw.githubusercontent.com/${REPOSITORY}/${commit}/${MANIFEST_PATH}`);
      if (pinned.sha256 !== observation.sha256) return await this.save({ status: 'channel-changed-during-check', version: manifest.version, error: null });
      manifest = validateDeployment(pinned.value);
      await this.save({ status: 'downloading', version: manifest.version, commit, manifestSha256: pinned.sha256, error: null });
      const archive = await this.download(manifest);
      if (this.stopped) return { status: 'stopped' };
      const id = `github-${manifest.version}-${manifest.sha256.slice(0, 12)}`;
      const staged = await this.manager.readManifest(id).catch(error => { if (error.code === 'ENOENT' || error.message === 'invalid update manifest') return null; throw error; });
      if (staged) {
        const row = staged.manifest.files[0];
        if (staged.manifest.provider !== 'kesas-release' || staged.manifest.version !== manifest.version || staged.manifest.notes !== manifest.notes ||
            staged.manifest.files.length !== 1 || row.sha256 !== manifest.sha256 || Number(row.sizeBytes) !== manifest.sizeBytes) throw new Error('Existing staged deployment differs from channel');
      } else {
        await this.manager.stage(archive, { id, version: manifest.version, notes: manifest.notes, sourceCommit: commit });
      }
      // Re-read channel intent and active version after a potentially long fetch.
      const latest = await this.json(`https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/${MANIFEST_PATH}`);
      if (latest.sha256 !== pinned.sha256 || releaseNumber(await this.activeVersion()) >= releaseNumber(manifest.version)) {
        return await this.save({ status: 'superseded-before-queue', version: manifest.version, error: null });
      }
      const beforeQueue = await this.manager.status();
      if (beforeQueue.lastDeployment?.status === 'cancelled' && beforeQueue.lastDeployment.version === manifest.version) {
        return await this.save({ status: 'held-cancelled', version: manifest.version, error: 'Deployment was cancelled during preparation; operator review required' });
      }
      if (this.stopped) return { status: 'stopped' };
      const attempt = { manifestSha256: pinned.sha256, status: 'queue-requested', version: manifest.version, packageId: id };
      await this.save({ attempt, status: 'queue-requested', version: manifest.version, error: null });
      const result = await this.manager.queue(id, 'github-deploy-branch', { countdownMinutes: 15, changelog: manifest.notes, onlyIfIdle: true });
      if (!result.ok) return await this.save({ status: 'waiting-native', version: manifest.version, error: null });
      attempt.status = 'queued';
      return await this.save({ attempt, status: 'queued', version: manifest.version, error: null });
    } catch (error) {
      // Network/metadata errors cannot stop a server. Native failures/cancellations
      // remain held by the native state and are never retried automatically.
      return await this.save({ status: 'error', error: String(error?.message || 'GitHub deployment check failed').replace(/[\x00-\x1f]/g, ' ').slice(0, 240) });
    } finally { this.busy = false; }
  }

  start() {
    if (!this.enabled) return;
    this.stopped = false;
    const run = async () => {
      try { await this.tick(); } catch (error) { console.error('GitHub deployment state could not be saved:', error.message); }
      if (!this.stopped) { this.timer = setTimeout(run, this.pollMs); this.timer.unref?.(); }
    };
    this.running = run();
  }

  stop() { this.stopped = true; if (this.timer) clearTimeout(this.timer); }
}
