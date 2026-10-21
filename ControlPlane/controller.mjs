import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import https from 'node:https';
import path from 'node:path';
import os from 'node:os';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { pathToFileURL } from 'node:url';

export const REPOSITORY = 'esotericmartyristaken-bit/AETHERFRONT';
export const CHANNEL = 'deployment/control-plane.json';
export const MAX_STATUS_BYTES = 64 * 1024;
export const MAX_CONTROL_BYTES = 128 * 1024 * 1024;
const STATUS_ENTRY = Buffer.from('host-status.json');
const ERRORS = new Set(['INVALID_ARGUMENT', 'CONTRACT_UNAVAILABLE', 'INCOMPATIBLE_CONTRACT',
  'INVALID_DESCRIPTOR', 'INVALID_STATUS', 'INVALID_STATUS_ZIP', 'STATUS_UNREACHABLE',
  'TLS_PIN_MISMATCH', 'STALE_STATUS', 'UNSUPPORTED_REQUIREMENT', 'CONTROL_NOT_HEALTHY',
  'APPLICATION_NOT_READY', 'VERSION_MISMATCH', 'GITHUB_FAILED', 'INVALID_CHANNEL',
  'CHANNEL_CHANGED', 'VERSION_NOT_NEWER', 'INVALID_ARCHIVE', 'IMMUTABLE_ASSET_CONFLICT',
  'PUBLICATION_UNCONFIRMED']);

export class ControllerError extends Error {
  constructor(code) { super(ERRORS.has(code) ? code : 'INVALID_ARGUMENT'); this.code = this.message; }
}
function fail(code) { throw new ControllerError(code); }
async function checked(code, action) {
  try { return await action(); } catch (error) {
    if (error instanceof ControllerError) throw error;
    fail(code);
  }
}
function object(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function integer(value) { return Number.isSafeInteger(value) && value > 0; }
function same(a, b) { return JSON.stringify(a) === JSON.stringify(b); }
function controlNumber(version) {
  if (typeof version !== 'string' || !/^control-[1-9][0-9]{0,14}$/.test(version)) fail('INVALID_DESCRIPTOR');
  return BigInt(version.slice(8));
}
async function contractFor(options) {
  const contract = options.contract || await checked('CONTRACT_UNAVAILABLE', () => import('./contract.mjs'));
  if (contract.REPOSITORY !== REPOSITORY || !['validateControlDescriptor', 'validateHostReport', 'assertCapabilities']
    .every(name => typeof contract[name] === 'function')) fail('INCOMPATIBLE_CONTRACT');
  return contract;
}
async function descriptorFor(value, options) {
  const contract = await contractFor(options);
  await checked('INVALID_DESCRIPTOR', () => contract.validateControlDescriptor(value));
  const fields = ['schemaVersion', 'transactionVersion', 'version', 'releaseTag', 'assetName', 'sha256', 'sizeBytes', 'minimumBootstrap'];
  if (!object(value) || Object.keys(value).length !== fields.length || fields.some(key => !Object.hasOwn(value, key)) ||
      value.schemaVersion !== 1 || value.transactionVersion !== 1 || !integer(value.minimumBootstrap) ||
      !integer(value.sizeBytes) || value.sizeBytes > MAX_CONTROL_BYTES || !/^[a-f0-9]{64}$/.test(value.sha256) ||
      value.releaseTag !== `host-${value.version}` || value.assetName !== `Aetherfront-Control-${value.version}.zip`) fail('INVALID_DESCRIPTOR');
  controlNumber(value.version);
  return Object.fromEntries(fields.map(key => [key, value[key]]));
}

// Reject duplicate keys before JSON.parse can silently discard them.
export function parseStrictJson(bytes, code = 'INVALID_STATUS') {
  return checked(code, async () => {
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    let cursor = 0;
    const space = () => { while (/\s/.test(text[cursor] || '') && cursor < text.length) cursor++; };
    const string = () => {
      const start = cursor++;
      while (cursor < text.length) {
        if (text[cursor] === '\\') { cursor += 2; continue; }
        if (text[cursor++] === '"') return JSON.parse(text.slice(start, cursor));
      }
      fail(code);
    };
    const value = depth => {
      if (depth > 24) fail(code);
      space();
      const opening = text[cursor];
      if (opening === '"') { string(); return; }
      if (opening === '{' || opening === '[') {
        const closing = opening === '{' ? '}' : ']';
        const keys = new Set(); cursor++; space();
        if (text[cursor] === closing) { cursor++; return; }
        for (let count = 0; count < 4096; count++) {
          if (opening === '{') {
            if (text[cursor] !== '"') fail(code);
            const key = string();
            if (keys.has(key)) fail(code);
            keys.add(key); space();
            if (text[cursor++] !== ':') fail(code);
          }
          value(depth + 1); space();
          if (text[cursor] === closing) { cursor++; return; }
          if (text[cursor++] !== ',') fail(code);
          space();
        }
        fail(code);
      }
      const token = /^(?:null|true|false|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/.exec(text.slice(cursor));
      if (!token) fail(code);
      cursor += token[0].length;
    };
    value(0); space();
    if (cursor !== text.length) fail(code);
    return JSON.parse(text);
  });
}

export function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

// One STORE entry only; no extraction, ZIP64, comments, extras, descriptors or links.
export function readStatusZip(bytes) {
  try {
    if (!Buffer.isBuffer(bytes) || bytes.length > MAX_STATUS_BYTES || bytes.length < 30 + 46 + 22 + STATUS_ENTRY.length * 2) fail('INVALID_STATUS_ZIP');
    const end = bytes.length - 22;
    if (bytes.readUInt32LE(end) !== 0x06054b50 || bytes.readUInt16LE(end + 4) !== 0 ||
        bytes.readUInt16LE(end + 6) !== 0 || bytes.readUInt16LE(end + 8) !== 1 ||
        bytes.readUInt16LE(end + 10) !== 1 || bytes.readUInt16LE(end + 20) !== 0) fail('INVALID_STATUS_ZIP');
    const central = bytes.readUInt32LE(end + 16);
    if (bytes.readUInt32LE(end + 12) !== 46 + STATUS_ENTRY.length || central + 46 + STATUS_ENTRY.length !== end ||
        bytes.readUInt32LE(central) !== 0x02014b50 || bytes.readUInt32LE(0) !== 0x04034b50) fail('INVALID_STATUS_ZIP');
    const flags = bytes.readUInt16LE(6);
    const length = bytes.readUInt32LE(18);
    if (![0, 0x800].includes(flags) || bytes.readUInt16LE(4) > 20 || bytes.readUInt16LE(8) !== 0 ||
        bytes.readUInt32LE(22) !== length || bytes.readUInt16LE(26) !== STATUS_ENTRY.length || bytes.readUInt16LE(28) !== 0 ||
        central !== 30 + STATUS_ENTRY.length + length || bytes.readUInt16LE(central + 6) !== bytes.readUInt16LE(4) ||
        bytes.readUInt16LE(central + 8) !== flags || bytes.readUInt16LE(central + 10) !== 0 ||
        bytes.readUInt32LE(central + 16) !== bytes.readUInt32LE(14) || bytes.readUInt32LE(central + 20) !== length ||
        bytes.readUInt32LE(central + 24) !== length || bytes.readUInt16LE(central + 28) !== STATUS_ENTRY.length ||
        bytes.readUInt16LE(central + 30) !== 0 || bytes.readUInt16LE(central + 32) !== 0 ||
        bytes.readUInt16LE(central + 34) !== 0 || bytes.readUInt32LE(central + 42) !== 0 ||
        ((bytes.readUInt32LE(central + 38) >>> 16) & 0xf000) === 0xa000 ||
        !bytes.subarray(30, 30 + STATUS_ENTRY.length).equals(STATUS_ENTRY) ||
        !bytes.subarray(central + 46, central + 46 + STATUS_ENTRY.length).equals(STATUS_ENTRY)) fail('INVALID_STATUS_ZIP');
    const data = bytes.subarray(30 + STATUS_ENTRY.length, central);
    if (crc32(data) !== bytes.readUInt32LE(14)) fail('INVALID_STATUS_ZIP');
    return data;
  } catch { fail('INVALID_STATUS_ZIP'); }
}

export function normalizePin(pin) {
  if (typeof pin !== 'string' || !/^(?:[a-fA-F0-9]{64}|(?:[a-fA-F0-9]{2}:){31}[a-fA-F0-9]{2})$/.test(pin)) fail('INVALID_ARGUMENT');
  return pin.replaceAll(':', '').toLowerCase();
}
export function statusUrl(origin) {
  try {
    const url = new URL(origin);
    if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || url.pathname !== '/') fail('INVALID_ARGUMENT');
    return new URL('/kesas-host-status.zip', url);
  } catch { fail('INVALID_ARGUMENT'); }
}
export function verifyPinnedCertificate(certificate, pin) {
  if (!Buffer.isBuffer(certificate?.raw) || certificate.raw.length === 0) fail('TLS_PIN_MISMATCH');
  const actual = crypto.createHash('sha256').update(certificate.raw).digest();
  const expected = Buffer.from(normalizePin(pin), 'hex');
  if (!crypto.timingSafeEqual(actual, expected)) fail('TLS_PIN_MISMATCH');
}

export async function requestPinnedStatus({ origin, pin, request = https.request }) {
  const url = statusUrl(origin); pin = normalizePin(pin);
  return checked('STATUS_UNREACHABLE', () => new Promise((resolve, reject) => {
    let verified = false, settled = false, timer, req;
    const finish = (error, bytes) => {
      if (settled) return;
      settled = true; clearTimeout(timer);
      if (error) { req?.destroy(); reject(error); } else resolve(bytes);
    };
    // Node skips checkServerIdentity when CA verification fails. Invoke the SAME
    // pin check at secureConnect as well, before allowing any response body.
    const checkServerIdentity = (_hostname, certificate) => {
      try { verifyPinnedCertificate(certificate, pin); return undefined; } catch { return new ControllerError('TLS_PIN_MISMATCH'); }
    };
    req = request(url, { method: 'GET', agent: false, rejectUnauthorized: false, checkServerIdentity,
      headers: { Accept: 'application/zip', 'Accept-Encoding': 'identity' } }, response => {
      if (!verified) { response.destroy(); finish(new ControllerError('TLS_PIN_MISMATCH')); return; }
      const declared = response.headers['content-length'];
      if (response.statusCode !== 200 || (response.headers['content-encoding'] && response.headers['content-encoding'] !== 'identity') ||
          (declared !== undefined && (!/^[1-9][0-9]*$/.test(declared) || Number(declared) > MAX_STATUS_BYTES))) {
        response.destroy(); finish(new ControllerError('INVALID_STATUS')); return;
      }
      const chunks = []; let size = 0;
      response.on('data', chunk => {
        size += chunk.length;
        if (size > MAX_STATUS_BYTES) { response.destroy(); finish(new ControllerError('INVALID_STATUS')); }
        else chunks.push(chunk);
      });
      response.on('error', () => finish(new ControllerError('STATUS_UNREACHABLE')));
      response.on('end', () => {
        if (declared !== undefined && Number(declared) !== size) finish(new ControllerError('INVALID_STATUS'));
        else finish(null, Buffer.concat(chunks));
      });
    });
    req.on('socket', socket => socket.once('secureConnect', () => {
      try {
        const certificate = socket.getPeerCertificate();
        const error = checkServerIdentity(url.hostname, certificate);
        if (error) throw error;
        verified = true;
      } catch { req.destroy(new ControllerError('TLS_PIN_MISMATCH')); }
    }));
    req.on('error', error => finish(error instanceof ControllerError ? error : new ControllerError('STATUS_UNREACHABLE')));
    timer = setTimeout(() => finish(new ControllerError('STATUS_UNREACHABLE')), 15_000);
    req.setTimeout(15_000, () => req.destroy(new ControllerError('STATUS_UNREACHABLE')));
    req.end();
  }));
}

export async function readStatus(options = {}) {
  const contract = await contractFor(options);
  // Validate origin/pin even with injected transport.
  statusUrl(options.origin); normalizePin(options.pin);
  const bytes = await checked('STATUS_UNREACHABLE', () => (options.fetchStatus || requestPinnedStatus)(options));
  const report = await parseStrictJson(readStatusZip(bytes));
  await checked('INVALID_STATUS', () => contract.validateHostReport(report));
  const observed = Date.parse(report.observedAt), expires = Date.parse(report.expiresAt);
  const now = typeof options.now === 'function' ? options.now() : options.now ?? Date.now();
  if (!Number.isFinite(observed) || !Number.isFinite(expires) || observed > now + 5_000 || expires <= now ||
      expires <= observed || now - observed > 180_000 || expires - observed > 180_000) fail('STALE_STATUS');
  if (report.schemaVersion !== 1 || report.contractVersion !== 1) fail('INCOMPATIBLE_CONTRACT');
  return report;
}

export async function preflight(options = {}) {
  const report = await readStatus(options);
  const contract = await contractFor(options);
  const requested = options.requirements ?? [];
  if (!Array.isArray(requested) || requested.some(x => typeof x !== 'string' || !/^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,79}$/.test(x))) fail('UNSUPPORTED_REQUIREMENT');
  const requirements = [...new Set(['host-report-v1', 'control-plane-v1',
    ...(options.purpose === 'control-publication' ? [] : ['application-v1']), ...requested])];
  // No negotiation-by-guessing: unknown or unadvertised requirements fail closed.
  if (!Array.isArray(report.capabilities) || requirements.some(x => !report.capabilities.includes(x))) {
    const error=new ControllerError('UNSUPPORTED_REQUIREMENT');
    error.missingCapabilities=requirements.filter(item=>contract.CAPABILITIES?.includes(item)&&!report.capabilities.includes(item));
    throw error;
  }
  await checked('UNSUPPORTED_REQUIREMENT', () => contract.assertCapabilities(report, requirements));
  // Shared version 1 contract establishes bootstrap 1, never inferred newer support.
  if ((options.minimumBootstrap ?? 1) !== 1 || report.bootstrapVersion !== 1) fail('INCOMPATIBLE_CONTRACT');
  if (options.purpose !== 'control-publication') {
    const control = report.control, application = report.application;
    if (!object(control) || !['healthy', 'idle'].includes(control.status) || control.pendingVersion || control.failure) fail('CONTROL_NOT_HEALTHY');
    const healthy = application?.health === 'healthy' || application?.health?.status === 'healthy';
    if (!object(application) || application.status !== 'idle' || !healthy || application.pendingVersion || application.failure) fail('APPLICATION_NOT_READY');
    if (application.clientVersion !== application.packVersion || (options.version && application.clientVersion !== options.version)) fail('VERSION_MISMATCH');
  }
  return { ok: true, report };
}

export function runGh(arguments_, input) {
  return checked('GITHUB_FAILED', () => new Promise((resolve, reject) => {
    const child = spawn('gh', arguments_, { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'ignore'] });
    const chunks = []; let length = 0; let finished = false;
    const finish = (error, result) => {
      if (finished) return; finished = true; clearTimeout(timer);
      if (error) reject(new ControllerError('GITHUB_FAILED')); else resolve(result);
    };
    const timer = setTimeout(() => { child.kill(); finish(true); }, 5 * 60_000);
    child.stdout.on('data', chunk => {
      length += chunk.length;
      if (length > 256 * 1024) { child.kill(); finish(true); } else chunks.push(chunk);
    });
    child.on('error', () => finish(true));
    child.stdin.on('error', () => finish(true));
    child.on('close', code => finish(code !== 0, Buffer.concat(chunks).toString('utf8')));
    child.stdin.end(input || '');
  }));
}
async function api(options, endpoint, method = 'GET', body, projection) {
  const args = ['api', '--hostname', 'github.com', '--method', method, `repos/${REPOSITORY}${endpoint ? `/${endpoint}` : ''}`];
  if (body !== undefined) args.push('--input', '-');
  if (projection) args.push('--jq', projection);
  const output = await checked('GITHUB_FAILED', () => (options.gh || runGh)(args, body === undefined ? undefined : JSON.stringify(body)));
  return parseStrictJson(Buffer.from(output), 'GITHUB_FAILED');
}
async function snapshot(options) {
  const reference = await api(options, 'git/ref/heads/deploy');
  const commit = reference.object?.sha;
  if (!/^[a-f0-9]{40}$/.test(commit)) fail('INVALID_CHANNEL');
  // Small, non-recursive trees distinguish absence from auth/network failures,
  // without returning this repository's thousands of application source files.
  const root = await api(options, `git/trees/${commit}`);
  if (!Array.isArray(root.tree) || root.truncated) fail('INVALID_CHANNEL');
  const folders = root.tree.filter(entry => entry.path === 'deployment');
  if (folders.length > 1 || (folders.length && (folders[0].type !== 'tree' || !/^[a-f0-9]{40}$/.test(folders[0].sha)))) fail('INVALID_CHANNEL');
  if (!folders.length) return { commit, fileSha: null, descriptor: null };
  const tree = await api(options, `git/trees/${folders[0].sha}`);
  if (!Array.isArray(tree.tree) || tree.truncated) fail('INVALID_CHANNEL');
  const entries = tree.tree.filter(entry => entry.path === 'control-plane.json');
  if (entries.length > 1 || (entries.length && (entries[0].type !== 'blob' || !['100644', '100755'].includes(entries[0].mode) || !/^[a-f0-9]{40}$/.test(entries[0].sha)))) fail('INVALID_CHANNEL');
  if (!entries.length) return { commit, fileSha: null, descriptor: null };
  const file = await api(options, `contents/${CHANNEL}?ref=${commit}`);
  if (file.sha !== entries[0].sha || file.encoding !== 'base64' || typeof file.content !== 'string' || file.content.length > 16_384) fail('INVALID_CHANNEL');
  const value = await parseStrictJson(Buffer.from(file.content, 'base64'), 'INVALID_CHANNEL');
  return { commit, fileSha: file.sha, descriptor: await descriptorFor(value, options) };
}
export async function readControl(options = {}) {
  await contractFor(options);
  return snapshot(options);
}

async function artifactDigest(url, expectedSize) {
  const original = url; let location = url;
  return checked('IMMUTABLE_ASSET_CONFLICT', async () => {
    for (let redirects = 0; redirects < 5; redirects++) {
      const parsed = new URL(location);
      if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.port || parsed.hash ||
          !['github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com'].includes(parsed.hostname) ||
          (parsed.hostname === 'github.com' && location !== original)) fail('IMMUTABLE_ASSET_CONFLICT');
      const response = await fetch(location, { redirect: 'manual', signal: AbortSignal.timeout(5 * 60_000), headers: { 'Accept-Encoding': 'identity' } });
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        await response.body?.cancel();
        location = new URL(response.headers.get('location'), location).href; continue;
      }
      if (response.status !== 200) { await response.body?.cancel(); fail('IMMUTABLE_ASSET_CONFLICT'); }
      let size = 0; const hash = crypto.createHash('sha256');
      try {
        for await (const chunk of response.body) {
          size += chunk.length; if (size > expectedSize) fail('IMMUTABLE_ASSET_CONFLICT'); hash.update(chunk);
        }
      } finally { await response.body?.cancel().catch(() => {}); }
      if (size !== expectedSize) fail('IMMUTABLE_ASSET_CONFLICT');
      return hash.digest('hex');
    }
    fail('IMMUTABLE_ASSET_CONFLICT');
  });
}
function checkOrder(current, candidate) {
  if (!current) return;
  if (controlNumber(candidate.version) < controlNumber(current.version)) fail('VERSION_NOT_NEWER');
  if (candidate.version === current.version && !same(candidate, current)) fail('IMMUTABLE_ASSET_CONFLICT');
}
async function releaseFor(options, descriptor) {
  // List avoids treating an arbitrary failing tag request as a missing release.
  for (let page = 1; page <= 20; page++) {
    const releases = await api(options, `releases?per_page=100&page=${page}`, 'GET', undefined, '[.[] | {id,tag_name,draft,prerelease}]');
    if (!Array.isArray(releases)) fail('GITHUB_FAILED');
    const matches = releases.filter(item => item.tag_name === descriptor.releaseTag);
    if (matches.length > 1) fail('IMMUTABLE_ASSET_CONFLICT');
    if (matches.length) return matches[0];
    if (releases.length < 100) return null;
  }
  fail('GITHUB_FAILED');
}
async function verifiedAsset(options, release, descriptor) {
  if (!integer(release.id) || release.tag_name !== descriptor.releaseTag || release.draft !== false || release.prerelease !== true) fail('IMMUTABLE_ASSET_CONFLICT');
  const assets = await api(options, `releases/${release.id}/assets?per_page=100`);
  if (!Array.isArray(assets) || assets.length !== 1) fail('IMMUTABLE_ASSET_CONFLICT');
  const asset = assets[0];
  const url = `https://github.com/${REPOSITORY}/releases/download/${descriptor.releaseTag}/${descriptor.assetName}`;
  if (asset.name !== descriptor.assetName || asset.state !== 'uploaded' || asset.size !== descriptor.sizeBytes || asset.browser_download_url !== url) fail('IMMUTABLE_ASSET_CONFLICT');
  if (asset.digest != null && asset.digest !== `sha256:${descriptor.sha256}`) fail('IMMUTABLE_ASSET_CONFLICT');
  // Verify public bytes even when GitHub reports a digest.
  const actual = await (options.fetchArtifactDigest || artifactDigest)(url, descriptor.sizeBytes);
  if (actual !== descriptor.sha256) fail('IMMUTABLE_ASSET_CONFLICT');
}

export async function publishControl(options = {}) {
  // This publication may stage while the application is busy. Activation guards
  // remain exclusively the worker's responsibility; no application writes here.
  await preflight({ ...options, purpose: 'control-publication', minimumBootstrap: options.minimumBootstrap ?? 1 });
  controlNumber(options.version);
  const archive = await checked('INVALID_ARCHIVE', async () => {
    const filename = path.resolve(options.archive);
    if (filename.startsWith('\\\\') || path.basename(filename) !== `Aetherfront-Control-${options.version}.zip`) fail('INVALID_ARCHIVE');
    for (let cursor = filename; ; cursor = path.dirname(cursor)) {
      if ((await fs.lstat(cursor)).isSymbolicLink()) fail('INVALID_ARCHIVE');
      if (path.dirname(cursor) === cursor) break;
    }
    const handle = await fs.open(filename, 'r');
    try {
      const stat = await handle.stat();
      if (!stat.isFile() || stat.size < 1 || stat.size > MAX_CONTROL_BYTES) fail('INVALID_ARCHIVE');
      const buffer = Buffer.alloc(64 * 1024), hash = crypto.createHash('sha256'); let size = 0;
      for (;;) {
        const { bytesRead } = await handle.read(buffer, 0, buffer.length, null);
        if (!bytesRead) break;
        size += bytesRead; if (size > MAX_CONTROL_BYTES) fail('INVALID_ARCHIVE'); hash.update(buffer.subarray(0, bytesRead));
      }
      const final = await handle.stat();
      if (size !== stat.size || final.size !== stat.size || final.mtimeMs !== stat.mtimeMs) fail('INVALID_ARCHIVE');
      return { filename, size, sha256: hash.digest('hex') };
    } finally { await handle.close(); }
  });
  const descriptor = await descriptorFor({ schemaVersion: 1, transactionVersion: 1, version: options.version,
    releaseTag: `host-${options.version}`, assetName: `Aetherfront-Control-${options.version}.zip`,
    sha256: archive.sha256, sizeBytes: archive.size, minimumBootstrap: options.minimumBootstrap ?? 1 }, options);
  await checked('INVALID_ARCHIVE', async () => {
    if (options.verifyArchive) return options.verifyArchive(archive.filename, descriptor);
    const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'aetherfront-control-audit-'));
    try {
      await promisify(execFile)(process.platform==='win32'?'pwsh.exe':'pwsh',['-NoProfile','-NonInteractive','-File',path.join(import.meta.dirname,'Expand-ControlBundle.ps1'),'-Archive',archive.filename,'-Destination',path.join(temporary,'payload'),'-ExpectedVersion',descriptor.version,'-ExpectedSha256',descriptor.sha256,'-ExpectedSize',String(descriptor.sizeBytes)],{windowsHide:true,maxBuffer:16384,timeout:60000});
    } finally {
      if (!path.resolve(temporary).startsWith(path.resolve(os.tmpdir())+path.sep)) fail('INVALID_ARCHIVE');
      await fs.rm(temporary,{recursive:true,force:true});
    }
  });
  const repo = await api(options, '');
  if (repo.full_name !== REPOSITORY || repo.private !== false || repo.archived !== false || repo.disabled !== false) fail('GITHUB_FAILED');
  const before = await snapshot(options); checkOrder(before.descriptor, descriptor);
  let release = await releaseFor(options, descriptor);
  if (!release) {
    try {
      release = await api(options, 'releases', 'POST', { tag_name: descriptor.releaseTag, name: descriptor.releaseTag,
        prerelease: true, draft: false, make_latest: 'false', body: 'Control-plane worker release; not an application release.' });
    } catch {
      release = await releaseFor(options, descriptor);
      if (!release) fail('PUBLICATION_UNCONFIRMED');
    }
  }
  if (!integer(release.id) || release.tag_name !== descriptor.releaseTag || release.draft !== false || release.prerelease !== true) fail('IMMUTABLE_ASSET_CONFLICT');
  const assets = await api(options, `releases/${release.id}/assets?per_page=100`);
  if (!Array.isArray(assets) || assets.length > 1) fail('IMMUTABLE_ASSET_CONFLICT');
  if (!assets.length) {
    try { await (options.gh || runGh)(['release', 'upload', descriptor.releaseTag, archive.filename, '--repo', `github.com/${REPOSITORY}`]); }
    catch { /* Lost response is reconciled by immutable readback below. */ }
  }
  await checked('IMMUTABLE_ASSET_CONFLICT', () => verifiedAsset(options, release, descriptor));
  const after = await snapshot(options); checkOrder(after.descriptor, descriptor);
  if (same(after.descriptor, descriptor)) return { ok: true, status: 'already-published', descriptor, commit: after.commit };
  if (before.commit !== after.commit || before.fileSha !== after.fileSha) fail('CHANNEL_CHANGED');
  await preflight({ ...options, purpose: 'control-publication', minimumBootstrap: descriptor.minimumBootstrap });
  const body = { branch: 'deploy', message: `Deploy ${descriptor.version}`, content: Buffer.from(`${JSON.stringify(descriptor)}\n`).toString('base64') };
  if (before.fileSha) body.sha = before.fileSha;
  try { await api(options, `contents/${CHANNEL}`, 'PUT', body); }
  catch { /* Read back uncertain writes; never force or automatically overwrite. */ }
  const confirmed = await snapshot(options);
  if (!same(confirmed.descriptor, descriptor)) fail('PUBLICATION_UNCONFIRMED');
  return { ok: true, status: 'published', descriptor, commit: confirmed.commit };
}

export async function runCli(argv, dependencies = {}) {
  try {
    const [command, ...arguments_] = argv;
    if (!['read', 'status', 'preflight', 'publish-control'].includes(command)) fail('INVALID_ARGUMENT');
    const allowed = new Set(command === 'read' ? [] : command === 'publish-control'
      ? ['origin', 'pin', 'requires', 'archive', 'version', 'minimum-bootstrap']
      : command === 'preflight' ? ['origin', 'pin', 'requires', 'version', 'minimum-bootstrap'] : ['origin', 'pin']);
    const flags = {};
    for (let index = 0; index < arguments_.length; index += 2) {
      const flag = arguments_[index]; const value = arguments_[index + 1];
      if (!flag?.startsWith('--') || !allowed.has(flag.slice(2)) || Object.hasOwn(flags, flag.slice(2)) || !value || value.startsWith('--')) fail('INVALID_ARGUMENT');
      flags[flag.slice(2)] = value;
    }
    if (flags['minimum-bootstrap'] && !/^[1-9][0-9]*$/.test(flags['minimum-bootstrap'])) fail('INVALID_ARGUMENT');
    const options = { ...dependencies, ...flags, requirements: flags.requires ? flags.requires.split(',') : [],
      minimumBootstrap: flags['minimum-bootstrap'] ? Number(flags['minimum-bootstrap']) : 1 };
    const result = command === 'read' ? await readControl(options) : command === 'status' ? await readStatus(options)
      : command === 'preflight' ? await preflight(options) : await publishControl(options);
    return { exitCode: 0, output: JSON.stringify(result) };
  } catch (error) {
    return { exitCode: 1, output: JSON.stringify({ ok: false, error: error instanceof ControllerError ? error.code : 'INVALID_ARGUMENT', ...(error instanceof ControllerError&&error.missingCapabilities?.length?{missingCapabilities:error.missingCapabilities}:{}) }) };
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const result = await runCli(process.argv.slice(2));
  process.stdout.write(`${result.output}\n`); process.exitCode = result.exitCode;
}
