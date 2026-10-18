import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { GitHubDeployment, validateDeployment, releaseNumber } from '../src/github-deployment.js';
import { UpdateManager } from '../src/update-manager.js';

const bytes = Buffer.from('synthetic native release archive');
const manifest = () => ({ schemaVersion: 1, version: 'kesas-rc29', releaseTag: 'host-kesas-rc29', assetName: 'Aetherfront-Release-kesas-rc29.zip', sha256: crypto.createHash('sha256').update(bytes).digest('hex'), sizeBytes: bytes.length, notes: 'Test release.' });
const commit = 'a'.repeat(40);

async function fixture(t, options = {}) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-github-deployment-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const settings = { enabled: true, repository: 'esotericmartyristaken-bit/AETHERFRONT', branch: 'deploy', manifestPath: 'deployment/production.json', pollIntervalMs: 60000 };
  const config = { __root: root, lmp: { deploymentRoot: path.join(root, 'KESAS') }, updates: { githubDeployment: settings } };
  const state = { status: 'idle', lastDeployment: { version: 'kesas-rc28' } };
  const staged = new Map();
  const calls = { stage: 0, queue: 0, urls: [] };
  let active = 'kesas-rc28';
  const manager = {
    status: async () => structuredClone(state),
    readManifest: async id => { if (!staged.has(id)) { const error = new Error('absent'); error.code = 'ENOENT'; throw error; } return staged.get(id); },
    stage: async (archive, info) => {
      calls.stage++;
      assert.deepEqual(await fs.readFile(archive), bytes);
      staged.set(info.id, { manifest: { provider: 'kesas-release', version: info.version, notes: info.notes, files: [{ sha256: manifest().sha256, sizeBytes: bytes.length }] } });
    },
    queue: async (id, by, opts) => {
      calls.queue++;
      assert.equal(by, 'github-deploy-branch');
      assert.equal(opts.countdownMinutes, 15);
      assert.equal(opts.onlyIfIdle, true);
      assert.equal(opts.force, undefined);
      if (state.status !== 'idle') return { ok: false };
      state.status = 'queued'; state.packageId = id; state.version = 'kesas-rc29';
      return { ok: true };
    }
  };
  const fetcher = async (url, request) => {
    calls.urls.push(url);
    assert.equal(request.redirect, 'manual');
    assert.equal(request.headers.Authorization, undefined);
    if (options.fetch) { const response = await options.fetch(url, { calls, state }); if (response) return response; }
    if (url.includes('/git/ref/heads/deploy')) return Response.json({ object: { sha: commit } });
    if (url.includes('/releases/download/')) return new Response(options.archive || bytes);
    return Response.json(options.manifest || manifest());
  };
  const watcher = new GitHubDeployment(config, manager, { fetch: fetcher, activeVersion: async () => active });
  return { root, config, state, manager, watcher, calls, staged, setActive: value => { active = value; } };
}

test('channel metadata rejects unsafe or unsupported instructions', () => {
  assert.equal(validateDeployment(manifest()).version, 'kesas-rc29');
  assert.equal(releaseNumber('kesas-rc100'), 100n);
  for (const change of [
    { schemaVersion: 2 }, { version: 'kesas-rc0' }, { version: '../oops' }, { version: 'kesas-rc9999999999999999' },
    { releaseTag: 'kesas-rc29' }, { assetName: '../../Server.exe' }, { sha256: 'bad' }, { sizeBytes: 0 },
    { sizeBytes: 512 * 1024 * 1024 + 1 }, { notes: 'line\nbreak' }, { notes: 'x'.repeat(501) },
    { force: true }, { url: 'https://example.invalid/archive' }, { command: 'restart' }
  ]) assert.throws(() => validateDeployment({ ...manifest(), ...change }));
});

test('new immutable commit verified then queued once with normal native guards', async t => {
  const f = await fixture(t);
  assert.equal((await f.watcher.tick()).status, 'queued');
  assert.equal(f.calls.stage, 1); assert.equal(f.calls.queue, 1);
  assert(f.calls.urls.some(url => url.includes(`/${commit}/deployment/production.json`)));
  assert.equal((await f.watcher.tick()).status, 'waiting-native');
  assert.equal(f.calls.queue, 1);
  f.state.status = 'idle'; f.state.lastDeployment = { version: 'kesas-rc29' }; f.setActive('kesas-rc29');
  assert.equal((await f.watcher.tick()).status, 'current');
  assert.equal(f.calls.queue, 1);
});

test('current and older versions never download, stage or restart', async t => {
  const f = await fixture(t);
  f.setActive('kesas-rc29'); assert.equal((await f.watcher.tick()).status, 'current');
  f.setActive('kesas-rc30'); assert.equal((await f.watcher.tick()).status, 'ignored-older-version');
  assert.equal(f.calls.stage, 0); assert.equal(f.calls.queue, 0);
  assert(!f.calls.urls.some(url => url.includes('/releases/download/')));
});

for (const status of ['queued', 'deploying', 'failed']) test(`native ${status} is never overwritten`, async t => {
  const f = await fixture(t); f.state.status = status;
  assert.equal((await f.watcher.tick()).status, status === 'failed' ? 'held-native-failure' : 'waiting-native');
  assert.equal(f.calls.stage, 0); assert.equal(f.calls.queue, 0);
});

test('cancelled version and lost native outcome require review, not duplicate restart', async t => {
  const f = await fixture(t);
  f.state.lastDeployment = { version: 'kesas-rc29', status: 'cancelled' };
  assert.equal((await f.watcher.tick()).status, 'held-cancelled');
  f.state.lastDeployment = null;
  assert.equal((await f.watcher.tick()).status, 'queued');
  f.state.status = 'idle';
  assert.equal((await f.watcher.tick()).status, 'held-previous-attempt');
  assert.equal(f.calls.queue, 1);
});

test('archive size/hash failures never stage or queue; partial files removed', async t => {
  for (const archive of [Buffer.from('bad'), Buffer.alloc(bytes.length + 1)]) {
    const f = await fixture(t, { archive });
    assert.equal((await f.watcher.tick()).status, 'error');
    assert.equal(f.calls.stage, 0); assert.equal(f.calls.queue, 0);
    assert.deepEqual(await fs.readdir(f.watcher.cache), []);
  }
});

test('same-size corrupt archive is rejected', async t => {
  const f = await fixture(t, { archive: Buffer.alloc(bytes.length) });
  assert.equal((await f.watcher.tick()).status, 'error'); assert.equal(f.calls.queue, 0);
});

test('untrusted redirect never receives a request', async t => {
  const f = await fixture(t, { fetch: async url => url.includes('/releases/download/') ? new Response(null, { status: 302, headers: { location: 'https://example.invalid/leak' } }) : null });
  assert.equal((await f.watcher.tick()).status, 'error');
  assert(!f.calls.urls.some(url => url.includes('example.invalid')));
  assert.equal(f.calls.queue, 0);
});

test('trusted GitHub asset redirect works without credentials', async t => {
  const f = await fixture(t, { fetch: async url => {
    if (url.includes('/releases/download/')) return new Response(null, { status: 302, headers: { location: 'https://release-assets.githubusercontent.com/test' } });
    if (url.includes('release-assets.githubusercontent.com')) return new Response(bytes);
    return null;
  } });
  assert.equal((await f.watcher.tick()).status, 'queued');
});

test('channel change during commit pinning defers without staging', async t => {
  const f = await fixture(t, { fetch: async url => url.includes(`/${commit}/`) ? Response.json({ ...manifest(), notes: 'changed' }) : null });
  assert.equal((await f.watcher.tick()).status, 'channel-changed-during-check');
  assert.equal(f.calls.stage, 0); assert.equal(f.calls.queue, 0);
});

test('channel change after download cannot queue superseded artifact', async t => {
  let reads = 0;
  const f = await fixture(t, { fetch: async url => {
    if (url.includes('/refs/heads/deploy/')) { reads++; if (reads > 1) return Response.json({ ...manifest(), notes: 'new intent' }); }
    return null;
  } });
  assert.equal((await f.watcher.tick()).status, 'superseded-before-queue');
  assert.equal(f.calls.queue, 0);
});

test('native activity arriving during download is not overwritten', async t => {
  const f = await fixture(t, { fetch: async (url, { state }) => {
    if (url.includes('/releases/download/')) state.status = 'deploying';
    return null;
  } });
  assert.equal((await f.watcher.tick()).status, 'waiting-native');
  assert.equal(f.state.status, 'deploying');
});

test('network outage cannot queue and later recovery resumes normally', async t => {
  let offline = true;
  const f = await fixture(t, { fetch: async () => { if (offline) throw new Error('network offline'); return null; } });
  assert.equal((await f.watcher.tick()).status, 'error'); assert.equal(f.calls.queue, 0);
  offline = false; assert.equal((await f.watcher.tick()).status, 'queued');
});

test('cancellation during download is held before queue', async t => {
  const f = await fixture(t, { fetch: async (url, { state }) => {
    if (url.includes('/releases/download/')) state.lastDeployment = { status: 'cancelled', version: 'kesas-rc29' };
    return null;
  } });
  assert.equal((await f.watcher.tick()).status, 'held-cancelled');
  assert.equal(f.calls.queue, 0);
});

test('uncertain queue-requested crash outcome is held for review', async t => {
  const f = await fixture(t);
  await fs.mkdir(path.dirname(f.watcher.statePath), { recursive: true });
  await fs.writeFile(f.watcher.statePath, JSON.stringify({ schemaVersion: 1, attempt: {
    manifestSha256: crypto.createHash('sha256').update(JSON.stringify(manifest())).digest('hex'), status: 'queue-requested'
  } }));
  assert.equal((await f.watcher.tick()).status, 'held-previous-attempt');
  assert.equal(f.calls.queue, 0);
});

test('crash after native queue retains pending outcome without requeue', async t => {
  const f = await fixture(t); await f.watcher.tick();
  const restarted = new GitHubDeployment(f.config, f.manager, { fetch: f.watcher.fetch, activeVersion: async () => 'kesas-rc28' });
  assert.equal((await restarted.tick()).status, 'waiting-native'); assert.equal(f.calls.queue, 1);
});

test('corrupt durable state fails closed', async t => {
  const f = await fixture(t); await fs.mkdir(path.dirname(f.watcher.statePath), { recursive: true });
  await fs.writeFile(f.watcher.statePath, '{broken');
  await assert.rejects(f.watcher.tick()); assert.equal(f.calls.queue, 0);
});

test('oversized metadata rejected before artifact requests', async t => {
  const f = await fixture(t, { fetch: async () => new Response(' '.repeat(65537)) });
  assert.equal((await f.watcher.tick()).status, 'error'); assert.equal(f.calls.queue, 0);
});

test('overlapping ticks use only one preparation', async t => {
  const f = await fixture(t);
  const first = f.watcher.tick();
  assert.equal((await f.watcher.tick()).status, 'disabled-or-busy');
  await first; assert.equal(f.calls.queue, 1);
});

test('disabled or wrong deployment target cannot stage', async t => {
  const f = await fixture(t); f.watcher.enabled = false;
  assert.equal((await f.watcher.tick()).status, 'disabled-or-busy');
  assert.throws(() => new GitHubDeployment({ ...f.config, lmp: {} }, f.manager));
});

test('real UpdateManager onlyIfIdle refuses a queued/failed/deploying native operation', async t => {
  const f = await fixture(t);
  const manager = new UpdateManager({ __root: f.root, updates: {} }, null, null);
  manager.readManifest = async () => ({ manifest: { id: 'fixture', version: 'kesas-rc29' } });
  for (const status of ['queued', 'failed', 'deploying']) {
    manager.state.status = status; manager.loaded = true; await manager.save();
    const result = await manager.queue('fixture', 'test', { onlyIfIdle: true });
    assert.equal(result.ok, false); assert.equal(manager.state.status, status);
  }
});

test('real idle-only native queue checks cancellation and already-deployed versions atomically', async t => {
  const f = await fixture(t);
  const manager = new UpdateManager({ __root: f.root, updates: {} }, null, null);
  manager.readManifest = async () => ({ manifest: { id: 'fixture', version: 'kesas-rc29' } });
  manager.loaded = true;
  for (const lastDeployment of [{ version: 'kesas-rc29', status: 'cancelled' }, { version: 'kesas-rc29' }, { version: 'kesas-rc30' }]) {
    manager.state.status = 'idle'; manager.state.lastDeployment = lastDeployment; await manager.save();
    const result = await manager.queue('fixture', 'test', { onlyIfIdle: true });
    assert.equal(result.ok, false); assert.equal(manager.state.status, 'idle');
  }
});

test('suspended native disk refresh cannot erase a newer operator queue and cancellation', async t => {
  const f = await fixture(t);
  const manager = new UpdateManager({ __root: f.root, updates: {} }, null, null);
  manager.readManifest = async () => ({ manifest: { id: 'fixture', version: 'kesas-rc29' } });
  await manager.load(); await manager.save();
  const staleIdle = structuredClone(manager.state);
  let releaseRead;
  let readStarted;
  const started = new Promise(resolve => { readStarted = resolve; });
  const suspended = new Promise(resolve => { releaseRead = resolve; });
  let reads = 0;
  manager.readSavedState = async () => {
    if (++reads === 1) { readStarted(); await suspended; return staleIdle; }
    return JSON.parse(await fs.readFile(manager.statePath, 'utf8'));
  };
  const watcherQueue = manager.queue('fixture', 'watcher', { onlyIfIdle: true });
  await started;
  assert.equal((await manager.queue('fixture', 'operator')).ok, true);
  assert.equal((await manager.cancel()).ok, true);
  releaseRead();
  const outcome = await watcherQueue;
  assert.equal(outcome.ok, false);
  assert.equal(outcome.reason, 'update-was-cancelled');
  assert.equal(manager.state.lastDeployment.status, 'cancelled');
  assert.equal(manager.state.status, 'idle');
});

test('native refresh waits for an in-flight cancellation save before reading disk', async t => {
  const f = await fixture(t);
  const manager = new UpdateManager({ __root: f.root, updates: {} }, null, null);
  manager.readManifest = async () => ({ manifest: { id: 'fixture', version: 'kesas-rc29' } });
  await manager.load(); await manager.save();
  manager.state.lastDeployment = { status: 'cancelled', version: 'kesas-rc29' };
  manager.stateRevision++;
  let completeSave;
  manager.pendingSave = new Promise(resolve => { completeSave = resolve; });
  let reads = 0;
  manager.readSavedState = async () => { reads++; return JSON.parse(await fs.readFile(manager.statePath, 'utf8')); };
  const pending = manager.queue('fixture', 'watcher', { onlyIfIdle: true });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(reads, 0);
  await fs.writeFile(manager.statePath, JSON.stringify(manager.state));
  completeSave();
  assert.equal((await pending).reason, 'update-was-cancelled');
  assert.equal(manager.state.lastDeployment.status, 'cancelled');
});
