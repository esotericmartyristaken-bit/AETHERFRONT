import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import * as contract from '../contract.mjs';
import { CHANNEL, REPOSITORY, MAX_STATUS_BYTES, crc32, readStatusZip, parseStrictJson,
  normalizePin, statusUrl, verifyPinnedCertificate, requestPinnedStatus, readStatus,
  preflight, readControl, publishControl, runCli } from '../controller.mjs';

const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const certificate = { raw: Buffer.from('test-only certificate bytes') };
const pin = hash(certificate.raw);
const origin = 'https://test.invalid:18866';
function report(overrides = {}) {
  const now = Date.now();
  return {
    schemaVersion: 1, contractVersion: 1, bootstrapVersion: 1,
    observedAt: new Date(now - 1_000).toISOString(), expiresAt: new Date(now + 60_000).toISOString(),
    capabilities: [...contract.CAPABILITIES],
    control: { version: 'control-1', sequence: 1, sha256:hash('control-1'), status: 'healthy', phase: 'idle', pendingVersion: null,
      lastSuccess: 'control-1', rollback: null, failure: null },
    application: { clientVersion: 'kesas-rc31', packVersion: 'kesas-rc31', status: 'idle', phase: 'idle',
      pendingVersion: null, health: 'healthy', lastSuccess: 'kesas-rc31', failure: null },
    components: Object.fromEntries(['watcher','nativeRunner','publisher','verifier','packService','serverRuntime',
      'serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'].map(name => [name, hash(name)])),
    runtime: { node: '24.19.0', powershell: '7.5.3', dotnet: '8.0.29' }, ...overrides,
  };
}
function zip(input) {
  const data = Buffer.from(typeof input === 'string' ? input : JSON.stringify(input));
  const name = Buffer.from('host-status.json'), local = Buffer.alloc(30), central = Buffer.alloc(46), end = Buffer.alloc(22);
  local.writeUInt32LE(0x04034b50); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc32(data), 14);
  local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(name.length, 26);
  central.writeUInt32LE(0x02014b50); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
  central.writeUInt32LE(crc32(data), 16); central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24);
  central.writeUInt16LE(name.length, 28);
  end.writeUInt32LE(0x06054b50); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length + name.length, 12); end.writeUInt32LE(local.length + name.length + data.length, 16);
  return Buffer.concat([local, name, data, central, name, end]);
}
function statusOptions(value = report(), extra = {}) {
  return { origin, pin, contract, fetchStatus: async () => zip(value), ...extra };
}
function descriptor(version = 'control-2', bytes = Buffer.from('test-only control archive')) {
  return { schemaVersion: 1, transactionVersion: 1, version, releaseTag: `host-${version}`,
    assetName: `Aetherfront-Control-${version}.zip`, sha256: hash(bytes), sizeBytes: bytes.length, minimumBootstrap: 1 };
}
async function fixture(t, settings = {}) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-controller-test-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const bytes = Buffer.from('test-only control archive'), candidate = descriptor('control-2', bytes);
  const archive = path.join(directory, candidate.assetName); await fs.writeFile(archive, bytes);
  const remote = { descriptor: Object.hasOwn(settings, 'current') ? settings.current : descriptor('control-1'), head: '1'.repeat(40), fileSha: 'a'.repeat(40),
    release: settings.missingRelease ? null : { id: 9, tag_name: candidate.releaseTag, draft: false, prerelease: true },
    assets: [], calls: [], snapshots: new Map(), writes: [], downloads: 0 };
  if (settings.current === null) remote.fileSha = null;
  const asset = () => ({ name: candidate.assetName, size: candidate.sizeBytes, state: 'uploaded', digest: `sha256:${candidate.sha256}`,
    browser_download_url: `https://github.com/${REPOSITORY}/releases/download/${candidate.releaseTag}/${candidate.assetName}` });
  if (settings.existingAsset) remote.assets = [asset()];
  const remember = () => remote.snapshots.set(remote.head, { descriptor: remote.descriptor, fileSha: remote.fileSha });
  remember();
  const gh = async (args, input) => {
    remote.calls.push({ args, input });
    if (args[0] === 'release') {
      assert.deepEqual(args.slice(0, 3), ['release', 'upload', candidate.releaseTag]);
      assert.equal(args[3], archive); assert.equal(args.includes('--clobber'), false);
      remote.assets = [asset()];
      if (settings.uploadThrows) throw new Error('private upload stderr C:\\private ghp_DO_NOT_PRINT');
      return '';
    }
    assert.deepEqual(args.slice(0, 5), ['api', '--hostname', 'github.com', '--method', args[4]]);
    const base = `repos/${REPOSITORY}`;
    assert.ok(args[5] === base || args[5].startsWith(`${base}/`));
    const method = args[4], endpoint = args[5] === base ? '' : args[5].slice(base.length + 1), body = input ? JSON.parse(input) : null;
    if (settings.apiFailure) throw new Error('private API stderr C:\\private github_pat_DO_NOT_PRINT');
    if (endpoint === '') {
      assert.equal(args[5], base, 'GitHub repository root must not have a trailing slash');
      return JSON.stringify({ full_name: REPOSITORY, private: false, archived: false, disabled: false });
    }
    if (endpoint === 'git/ref/heads/deploy') { remember(); return JSON.stringify({ object: { sha: remote.head } }); }
    if (endpoint === 'git/trees/' + 'd'.repeat(40)) {
      return JSON.stringify({ truncated: false, tree: remote.descriptor ? [{ path: 'control-plane.json', type: 'blob', mode: '100644', sha: remote.fileSha }] : [] });
    }
    if (endpoint.startsWith('git/trees/')) {
      const saved = remote.snapshots.get(endpoint.slice(10).split('?')[0]);
      assert.ok(saved);
      return JSON.stringify({ truncated: false, tree: [{ path: 'deployment', type: 'tree', sha: 'd'.repeat(40) }] });
    }
    if (endpoint.startsWith(`contents/${CHANNEL}?ref=`)) {
      const saved = remote.snapshots.get(endpoint.split('?ref=')[1]);
      return JSON.stringify({ sha: saved.fileSha, encoding: 'base64', content: Buffer.from(JSON.stringify(saved.descriptor)).toString('base64') });
    }
    if (endpoint.startsWith('releases?')) return JSON.stringify(remote.release ? [remote.release] : []);
    if (endpoint === 'releases' && method === 'POST') {
      remote.writes.push({ endpoint, body });
      assert.equal(body.prerelease, true); assert.equal(body.make_latest, 'false');
      remote.release = { id: 9, tag_name: body.tag_name, draft: false, prerelease: true };
      if (settings.createThrows) throw new Error('lost create response');
      return JSON.stringify(remote.release);
    }
    if (endpoint === 'releases/9/assets?per_page=100') return JSON.stringify(remote.assets);
    if (endpoint === `contents/${CHANNEL}` && method === 'PUT') {
      assert.equal(body.branch, 'deploy'); assert.equal(body.sha ?? null, remote.fileSha);
      remote.writes.push({ endpoint, body });
      if (!settings.refusePut) {
        remote.descriptor = JSON.parse(Buffer.from(body.content, 'base64')); remote.head = '2'.repeat(40); remote.fileSha = 'b'.repeat(40); remember();
      }
      if (settings.putThrows || settings.refusePut) throw new Error('lost PUT response');
      return '{}';
    }
    assert.fail(`Unexpected mock call ${method} ${endpoint}`);
  };
  const options = statusOptions(settings.report || report(), { archive, version: 'control-2', gh, verifyArchive:async()=>{},
    fetchArtifactDigest: async (url, size) => {
      remote.downloads++; assert.equal(url, asset().browser_download_url); assert.equal(size, candidate.sizeBytes);
      if (settings.concurrentChange) { remote.head = '3'.repeat(40); remember(); }
      if (settings.assetDigestMismatch) return 'f'.repeat(64);
      return candidate.sha256;
    } });
  return { options, remote, candidate, asset };
}

test('STORE readback matches shared writer and CRC32 standard vector', async () => {
  assert.equal(crc32(Buffer.from('123456789')), 0xcbf43926);
  const value = report(); assert.deepEqual(JSON.parse(readStatusZip(zip(value))), value);
  const { statusZip } = await import('../public-status.mjs');
  assert.deepEqual(JSON.parse(readStatusZip(statusZip(value))), value);
});
test('ZIP bounds, CRC, compression, path, flags, offsets and entry-count fail closed', async t => {
  const mutations = {
    crc: b => { b[46] ^= 1; }, compression: b => b.writeUInt16LE(8, 8),
    encrypted: b => b.writeUInt16LE(1, 6), descriptor: b => b.writeUInt16LE(8, 6),
    path: b => { b[30] = 46; }, extra: b => b.writeUInt16LE(1, 28),
    duplicate: b => b.writeUInt16LE(2, b.length - 12), comment: b => b.writeUInt16LE(1, b.length - 2),
    centralOffset: b => b.writeUInt32LE(0xffffffff, b.length - 6),
    localOffset: b => b.writeUInt32LE(1, b.readUInt32LE(b.length - 6) + 42),
    symlink: b => b.writeUInt32LE(0xa0000000, b.readUInt32LE(b.length - 6) + 38),
    centralName: b => { b[b.readUInt32LE(b.length - 6) + 46] = 46; },
  };
  for (const [name, mutate] of Object.entries(mutations)) await t.test(name, () => {
    const bytes = zip(report()); mutate(bytes); assert.throws(() => readStatusZip(bytes), /INVALID_STATUS_ZIP/);
  });
  assert.throws(() => readStatusZip(Buffer.alloc(MAX_STATUS_BYTES + 1)), /INVALID_STATUS_ZIP/);
  assert.throws(() => readStatusZip(Buffer.from('not ZIP')), /INVALID_STATUS_ZIP/);
  assert.throws(() => readStatusZip(Buffer.concat([zip(report()), Buffer.from('trailer')])), /INVALID_STATUS_ZIP/);
});
test('JSON rejects duplicate keys, invalid UTF-8, deep nesting and trailing documents', async () => {
  for (const input of ['{"a":1,"a":2}', '{"a":{"x":1,"x":2}}', '{}{}', '['.repeat(30) + ']'.repeat(30), '{"a":1,}']) {
    await assert.rejects(parseStrictJson(Buffer.from(input)), /INVALID_STATUS/);
  }
  await assert.rejects(parseStrictJson(Buffer.from([0xff])), /INVALID_STATUS/);
});
test('pin normalization accepts only exact SHA256 and compares raw certificate bytes', () => {
  assert.equal(normalizePin(pin.toUpperCase().match(/../g).join(':')), pin);
  verifyPinnedCertificate(certificate, pin);
  assert.throws(() => verifyPinnedCertificate({ raw: Buffer.from('different') }, pin), /TLS_PIN_MISMATCH/);
  assert.throws(() => verifyPinnedCertificate({ fingerprint256: pin }, pin), /TLS_PIN_MISMATCH/);
  for (const invalid of ['', 'sha256:' + pin, pin + '00', pin.slice(2), pin + '\n']) assert.throws(() => normalizePin(invalid), /INVALID_ARGUMENT/);
});
test('status origin is HTTPS root only, with fixed ZIP route', () => {
  assert.equal(statusUrl(origin).href, `${origin}/kesas-host-status.zip`);
  for (const invalid of ['http://test.invalid', 'https://user:password@test.invalid', `${origin}/other`, `${origin}/?token=private`, `${origin}/#private`]) {
    assert.throws(() => statusUrl(invalid), /INVALID_ARGUMENT/);
  }
});
function mockRequest({ wrongPin = false, noHandshake = false, redirect = false, oversized = false } = {}) {
  return (_url, options, callback) => {
    assert.equal(options.rejectUnauthorized, false); assert.equal(options.agent, false);
    assert.equal(typeof options.checkServerIdentity, 'function');
    const req = new EventEmitter(); req.setTimeout = () => req;
    req.destroy = error => { req.destroyed = true; queueMicrotask(() => req.emit('error', error)); };
    req.end = () => queueMicrotask(() => {
      const socket = new EventEmitter(); socket.getPeerCertificate = () => wrongPin ? { raw: Buffer.from('wrong') } : certificate;
      req.emit('socket', socket); if (!noHandshake) socket.emit('secureConnect');
      if (req.destroyed) return;
      const response = new PassThrough(); response.statusCode = redirect ? 302 : 200;
      const bytes = zip(report()); response.headers = { 'content-length': String(oversized ? MAX_STATUS_BYTES + 1 : bytes.length) };
      callback(response); response.end(bytes);
    });
    return req;
  };
}
test('pinned HTTPS succeeds without CA trust only after exact secureConnect pin', async () => {
  assert.ok(await requestPinnedStatus({ origin, pin, request: mockRequest() }));
});
test('TLS failure never falls back; redirects and oversized status rejected', async () => {
  await assert.rejects(requestPinnedStatus({ origin, pin, request: mockRequest({ wrongPin: true }) }), /TLS_PIN_MISMATCH/);
  await assert.rejects(requestPinnedStatus({ origin, pin, request: mockRequest({ noHandshake: true }) }), /TLS_PIN_MISMATCH/);
  for (const settings of [{ redirect: true }, { oversized: true }]) await assert.rejects(requestPinnedStatus({ origin, pin, request: mockRequest(settings) }), /INVALID_STATUS/);
});
test('real contract accepts public report; stale or private report fails closed', async () => {
  assert.equal((await readStatus(statusOptions())).application.clientVersion, 'kesas-rc31');
  await assert.rejects(readStatus(statusOptions(report({ expiresAt: new Date(Date.now() - 1).toISOString() }))), /STALE_STATUS/);
  await assert.rejects(readStatus(statusOptions(report({ privatePath: 'C:\\private' }))), /INVALID_STATUS/);
  const value = report(); value.application.failure = 'C:\\private';
  await assert.rejects(readStatus(statusOptions(value)), /INVALID_STATUS/);
});
test('application preflight verifies capabilities, versions, bootstrap and readiness', async () => {
  assert.equal((await preflight(statusOptions(report(), { version: 'kesas-rc31', requirements: ['application-v1'] }))).ok, true);
  await assert.rejects(preflight(statusOptions(report(), { requirements: ['unknown-future-v99'] })), /UNSUPPORTED_REQUIREMENT/);
  await assert.rejects(preflight(statusOptions(report({ capabilities: [] }), { requirements: ['application-v1'] })), /UNSUPPORTED_REQUIREMENT/);
  await assert.rejects(preflight(statusOptions(report({ capabilities: [] }))), /UNSUPPORTED_REQUIREMENT/);
  await assert.rejects(preflight(statusOptions(report({ capabilities: ['application-v1'] }), { purpose: 'control-publication' })), /UNSUPPORTED_REQUIREMENT/);
  await assert.rejects(preflight(statusOptions(report(), { minimumBootstrap: 2 })), /INCOMPATIBLE_CONTRACT/);
  await assert.rejects(preflight(statusOptions(report(), { version: 'kesas-rc32' })), /VERSION_MISMATCH/);
  const value = report(); value.application.packVersion = 'kesas-rc30';
  await assert.rejects(preflight(statusOptions(value)), /VERSION_MISMATCH/);
});
test('busy/unhealthy application and unhealthy controller block application preflight', async () => {
  for (const field of ['pendingVersion', 'health', 'status', 'failure']) {
    const value = report(); value.application[field] = { pendingVersion: 'kesas-rc32', health: 'offline', status: 'applying', failure: 'activation-failed' }[field];
    await assert.rejects(preflight(statusOptions(value)), /APPLICATION_NOT_READY/);
  }
  const value = report(); value.control.pendingVersion = 'control-2';
  await assert.rejects(preflight(statusOptions(value)), /CONTROL_NOT_HEALTHY/);
});
test('read resolves fixed branch at immutable commit without writes', async t => {
  const f = await fixture(t); const value = await readControl(f.options);
  assert.equal(value.commit, '1'.repeat(40)); assert.equal(value.descriptor.version, 'control-1');
  assert.equal(f.remote.writes.length, 0);
  const content = f.remote.calls.find(call => call.args[5]?.includes('contents/'));
  assert.ok(content.args[5].endsWith('?ref=' + '1'.repeat(40)));
});
test('control publication creates non-latest prerelease, verifies public bytes, updates channel last', async t => {
  const f = await fixture(t, { missingRelease: true, current: null });
  assert.equal((await publishControl(f.options)).status, 'published');
  assert.equal(f.remote.downloads, 1); assert.equal(f.remote.writes.length, 2);
  assert.equal(f.remote.writes.at(-1).endpoint, `contents/${CHANNEL}`);
  assert.equal(Object.hasOwn(f.remote.writes.at(-1).body, 'sha'), false);
  assert.deepEqual(f.remote.descriptor, f.candidate);
  assert.ok(f.remote.calls.every(call => !call.args.some(arg => arg.includes('production.json'))));
});
test('publish-control can stage while application busy; application preflight cannot', async t => {
  const value = report(); value.application.status = 'applying'; value.application.pendingVersion = 'kesas-rc32';
  const f = await fixture(t, { report: value });
  await assert.rejects(preflight(f.options), /APPLICATION_NOT_READY/);
  assert.equal((await publishControl(f.options)).ok, true);
});
test('expired status prevents every GitHub call', async t => {
  const f = await fixture(t, { report: report({ expiresAt: new Date(Date.now() - 1).toISOString() }) });
  await assert.rejects(publishControl(f.options), /STALE_STATUS/); assert.equal(f.remote.calls.length, 0);
});
test('idempotent repeat verifies immutable asset without channel PUT', async t => {
  const f = await fixture(t, { current: descriptor(), existingAsset: true });
  assert.equal((await publishControl(f.options)).status, 'already-published');
  assert.equal(f.remote.writes.length, 0); assert.equal(f.remote.downloads, 1);
  assert.equal(f.remote.calls.some(call => call.args[0] === 'release'), false);
});
test('older candidate, existing conflicting version, non-prerelease and extra assets rejected', async t => {
  const old = await fixture(t, { current: descriptor('control-3') });
  await assert.rejects(publishControl(old.options), /VERSION_NOT_NEWER/);
  const conflict = await fixture(t, { current: { ...descriptor(), sha256: 'f'.repeat(64) } });
  await assert.rejects(publishControl(conflict.options), /IMMUTABLE_ASSET_CONFLICT/);
  const release = await fixture(t); release.remote.release.prerelease = false;
  await assert.rejects(publishControl(release.options), /IMMUTABLE_ASSET_CONFLICT/);
  const assets = await fixture(t, { existingAsset: true }); assets.remote.assets.push(assets.asset());
  await assert.rejects(publishControl(assets.options), /IMMUTABLE_ASSET_CONFLICT/);
});
test('remote digest conflicts cannot update channel', async t => {
  const f = await fixture(t, { assetDigestMismatch: true });
  await assert.rejects(publishControl(f.options), /IMMUTABLE_ASSET_CONFLICT/);
  assert.equal(f.remote.writes.length, 0);
});
test('concurrent deploy head change prevents PUT', async t => {
  const f = await fixture(t, { concurrentChange: true });
  await assert.rejects(publishControl(f.options), /CHANNEL_CHANGED/); assert.equal(f.remote.writes.length, 0);
});
test('lost create/upload/PUT responses reconciled by readback; no overwrite retry', async t => {
  const f = await fixture(t, { missingRelease: true, createThrows: true, uploadThrows: true, putThrows: true });
  assert.equal((await publishControl(f.options)).ok, true);
  assert.equal(f.remote.calls.filter(call => call.args[4] === 'PUT').length, 1);
  assert.equal(f.remote.calls.filter(call => call.args[0] === 'release').length, 1);
});
test('unconfirmed PUT is failure, not success or force retry', async t => {
  const f = await fixture(t, { refusePut: true });
  await assert.rejects(publishControl(f.options), /PUBLICATION_UNCONFIRMED/);
  assert.equal(f.remote.calls.filter(call => call.args[4] === 'PUT').length, 1);
});
test('CLI JSON sanitizes private exceptions; duplicate and unknown flags rejected', async t => {
  const f = await fixture(t, { apiFailure: true });
  const result = await runCli(['read'], f.options);
  assert.equal(result.exitCode, 1); assert.deepEqual(JSON.parse(result.output), { ok: false, error: 'GITHUB_FAILED' });
  for (const argv of [[], ['unknown'], ['status', '--token', 'private'], ['status', '--pin', pin, '--pin', pin], ['preflight', '--requires', 'application-v1,']]) {
    assert.equal((await runCli(argv, f.options)).exitCode, 1);
  }
  const status = await runCli(['status', '--origin', origin, '--pin', pin], statusOptions());
  assert.equal(status.exitCode, 0); assert.equal(JSON.parse(status.output).application.clientVersion, 'kesas-rc31');
});
