import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { UpdateManager } from '../src/update-manager.js';
import { GitHubDeployment } from '../src/github-deployment.js';
import { LmpController } from '../src/lmp.js';

const reason = 'host-deployment-local-only';
const localError = error => error.code === reason && /local-only/.test(error.message);
const live = { online: true, ready: true, playerCount: 0, connectedCount: 0, bridge: { ready: true } };

async function fixture(t, { localOnly = true, kesas = true } = {}) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-local-only-updates-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const config = {
    __root: root,
    lmp: kesas ? { deploymentRoot: path.join(root, 'native') } : {},
    updates: { requirePreDeployBackup: false, ...(localOnly ? { hostDeploymentMode: 'local-only' } : {}) }
  };
  const calls = [];
  const forbidden = name => async () => { calls.push(name); throw new Error(`Unexpected ${name}`); };
  const controller = Object.fromEntries(['status', 'processInfo', 'publishRelease', 'stop', 'startExternal']
    .map(name => [name, forbidden(name)]));
  controller.root = path.join(root, 'native', 'Server');
  const manager = new UpdateManager(config, controller, { createBackup: forbidden('backup') }, { runTool: forbidden('runTool') });
  const seed = async (status = 'idle') => {
    const state = { ...manager.state, status, packageId: status === 'idle' ? null : 'kesas-rc99',
      version: status === 'idle' ? null : 'kesas-rc99', phase: status === 'idle' ? null : 'installing',
      force: status !== 'idle', countdownStartedAt: '2020-01-01T00:00:00.000Z',
      deployAt: '2020-01-01T00:15:00.000Z', countdownMinutes: 15, countdownAnnouncements: [900000] };
    const bytes = JSON.stringify(state, null, 2);
    await fs.mkdir(path.dirname(manager.statePath), { recursive: true });
    await fs.writeFile(manager.statePath, bytes);
    await manager.refreshFromDisk();
    return bytes;
  };
  return { root, config, manager, calls, seed };
}

test('KESAS local-only stage entry points reject before validation, tooling, or staging writes', async t => {
  const f = await fixture(t);
  const before = structuredClone(f.manager.state);
  f.manager.load = async () => assert.fail('Staging must reject before loading state');
  for (const action of [
    () => f.manager.stage(path.join(f.root, 'missing-directory')),
    () => f.manager.stage(path.join(f.root, 'kesas-rc99.zip'), { version: 'kesas-rc99' }),
    () => f.manager.stageKesas(path.join(f.root, 'kesas-rc99.zip'), { version: 'kesas-rc99' })
  ]) await assert.rejects(action, localError);
  assert.deepEqual(f.manager.state, before);
  assert.deepEqual(f.calls, []);
  assert.deepEqual(await fs.readdir(f.root), []);
});

for (const status of ['idle', 'queued', 'deploying', 'failed']) {
  test(`KESAS local-only refuses manual and automatic update mutations from ${status}`, async t => {
    const f = await fixture(t);
    f.config.__controlRoot = path.join(f.root, 'control');
    const bytes = await f.seed(status);
    const before = structuredClone(f.manager.state);
    const hooks = Object.fromEntries(['onProgress', 'onAnnouncement', 'onDeployed']
      .map(name => [name, () => assert.fail(`Unexpected ${name}`)]));
    f.manager.controlMaintenance = async () => assert.fail('Local-only rejection must precede native maintenance');
    f.manager.readManifest = async () => assert.fail('Local-only rejection must precede manifest or broker access');
    for (const action of [
      () => f.manager.queue('kesas-rc99'),
      () => f.manager.queue('kesas-rc99', 'github', { onlyIfIdle: true }),
      () => f.manager.force(),
      () => f.manager.cancel()
    ]) assert.deepEqual(await action(), { ok: false, reason });
    for (const action of [
      () => f.manager.reconcile(live, hooks),
      () => f.manager.reconcile({ ...live, online: false }, hooks),
      () => f.manager.deploy(live, hooks),
      () => f.manager.recoverNativePublication(live)
    ]) assert.deepEqual(await action(), { ok: false, deferred: true, reason });
    assert.deepEqual(f.manager.state, before);
    assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), bytes);
    assert.equal(f.manager.lastObservation, null);
    assert.equal(f.manager.operation, null);
    assert.deepEqual(f.calls, []);
    assert.deepEqual(await fs.readdir(f.root), ['state']);
  });
}

test('KESAS local-only closes direct persistence, recovery, phase, announcement, apply and rollback bypasses', async t => {
  const f = await fixture(t);
  const bytes = await f.seed('failed');
  const before = structuredClone(f.manager.state);
  const revision = f.manager.stateRevision;
  const hooks = { onProgress: () => assert.fail('Unexpected progress'), onAnnouncement: () => assert.fail('Unexpected warning') };
  for (const action of [
    () => f.manager.save(),
    () => f.manager.acknowledgeReviewedRollback('a'.repeat(64), 'b'.repeat(64)),
    () => f.manager.notify(hooks, 'Not authorized'),
    () => f.manager.notifyAnnouncement(hooks, 'Not authorized'),
    () => f.manager.announceCountdown(Date.now(), hooks),
    () => f.manager.setPhase('installing', hooks),
    () => f.manager.applyManifest('not-a-package', { files: [] }),
    () => f.manager.rollback({ changed: [] })
  ]) await assert.rejects(action, localError);
  assert.deepEqual(f.manager.state, before);
  assert.equal(f.manager.stateRevision, revision);
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), bytes);
  assert.deepEqual(f.calls, []);
  assert.deepEqual(await fs.readdir(f.root), ['state']);
});

test('KESAS local-only status remains read-only, preserves pending evidence and observes operator completion', async t => {
  const f = await fixture(t);
  const missing = await f.manager.status();
  assert.equal(missing.status, 'idle');
  assert.deepEqual(missing.staged, []);
  assert.deepEqual(await fs.readdir(f.root), []);
  const bytes = await f.seed('deploying');
  assert.equal((await f.manager.status()).status, 'deploying');
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), bytes);
  const completed = { ...f.manager.state, status: 'idle', packageId: null, version: null,
    lastDeployment: { version: 'kesas-rc99', provider: 'local-operator' } };
  await fs.writeFile(f.manager.statePath, JSON.stringify(completed));
  assert.deepEqual((await f.manager.status()).lastDeployment, completed.lastDeployment);
  assert.deepEqual(await fs.readdir(f.root), ['state']);
  assert.deepEqual(f.calls, []);
});

test('enabling local-only while queue validation is suspended prevents queue commit', async t => {
  const f = await fixture(t, { localOnly: false });
  const bytes = await f.seed();
  const before = structuredClone(f.manager.state);
  let release;
  let entered;
  const enteredPromise = new Promise(resolve => { entered = resolve; });
  f.manager.readManifest = async () => { entered(); return new Promise(resolve => { release = resolve; }); };
  const queued = f.manager.queue('kesas-rc99');
  await enteredPromise;
  f.config.updates.hostDeploymentMode = 'local-only';
  release({ manifest: { id: 'kesas-rc99', version: 'kesas-rc99' } });
  assert.deepEqual(await queued, { ok: false, reason });
  assert.deepEqual(f.manager.state, before);
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), bytes);
});

test('enabling local-only during native verifier wait prevents staging writes', async t => {
  const f = await fixture(t, { localOnly: false });
  let release;
  let entered;
  const enteredPromise = new Promise(resolve => { entered = resolve; });
  f.manager.runTool = async () => { entered(); return new Promise(resolve => { release = resolve; }); };
  const staging = f.manager.stageKesas(path.join(f.root, 'kesas-rc99.zip'), { version: 'kesas-rc99' });
  await enteredPromise;
  f.config.updates.hostDeploymentMode = 'local-only';
  release();
  await assert.rejects(staging, localError);
  assert.deepEqual(await fs.readdir(f.root), []);
});

for (const existingStage of [false, true]) {
  test(`GitHub ticks remain observable but cannot ${existingStage ? 'queue an existing' : 'stage a new'} local-only native release`, async t => {
    const f = await fixture(t);
    const saved = await f.seed();
    f.config.updates.githubDeployment = { enabled: true, repository: 'esotericmartyristaken-bit/AETHERFRONT',
      branch: 'deploy', manifestPath: 'deployment/production.json' };
    const bytes = Buffer.from('hermetic GitHub release');
    const sha256 = crypto.createHash('sha256').update(bytes).digest('hex');
    const manifest = { schemaVersion: 1, version: 'kesas-rc99', releaseTag: 'host-kesas-rc99',
      assetName: 'Aetherfront-Release-kesas-rc99.zip', sha256, sizeBytes: bytes.length, notes: 'Fixture release.' };
    const id = `github-kesas-rc99-${sha256.slice(0, 12)}`;
    const packageDirectory = path.join(f.manager.stagingRoot, id);
    if (existingStage) {
      await fs.mkdir(packageDirectory, { recursive: true });
      await fs.writeFile(path.join(packageDirectory, 'manifest.json'), JSON.stringify({ schemaVersion: 1,
        provider: 'kesas-release', id, version: manifest.version, notes: manifest.notes,
        files: [{ source: 'payload/release.zip', destination: 'release.zip', sha256, sizeBytes: bytes.length }] }));
    }
    let stages = 0;
    let queues = 0;
    const stage = f.manager.stage.bind(f.manager);
    const queue = f.manager.queue.bind(f.manager);
    f.manager.stage = (...args) => { stages++; return stage(...args); };
    f.manager.queue = (...args) => { queues++; return queue(...args); };
    const urls = [];
    const watcher = new GitHubDeployment(f.config, f.manager, { activeVersion: async () => 'kesas-rc98',
      fetch: async url => {
        urls.push(url);
        if (url.includes('/git/ref/heads/deploy')) return Response.json({ object: { sha: 'a'.repeat(40) } });
        if (url.includes('/releases/download/')) return new Response(bytes);
        return Response.json(manifest);
      } });
    const result = await watcher.tick();
    assert.equal(watcher.enabled, true);
    assert(urls.length > 0);
    assert.equal(result.status, existingStage ? 'waiting-native' : 'error');
    assert.equal(stages, existingStage ? 0 : 1);
    assert.equal(queues, existingStage ? 1 : 0);
    assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), saved);
    assert.equal((await f.manager.status()).status, 'idle');
    assert.deepEqual(f.calls, []);
    if (!existingStage) await assert.rejects(fs.stat(f.manager.stagingRoot), { code: 'ENOENT' });
  });
}

test('KESAS deployment remains available when local-only is not configured', async t => {
  const f = await fixture(t, { localOnly: false });
  const archive = path.join(f.root, 'kesas-rc99.zip');
  await fs.writeFile(archive, 'hermetic archive; verifier mocked');
  f.manager.runTool = async () => { f.calls.push('verify'); };
  const staged = await f.manager.stage(archive, { version: 'kesas-rc99' });
  assert.equal(staged.provider, 'kesas-release');
  assert.equal((await f.manager.queue(staged.id)).ok, true);
  f.manager.controller.status = async () => live;
  f.manager.controller.publishRelease = async () => { f.calls.push('publish'); return { rollbackRoot: f.root }; };
  assert.equal((await f.manager.reconcile(live)).ok, true);
  assert.deepEqual(f.calls, ['verify', 'publish']);
  assert.equal((await f.manager.status()).status, 'idle');
});

test('local-only host setting does not disable generic non-KESAS updates', async t => {
  const f = await fixture(t, { kesas: false });
  const source = path.join(f.root, 'source');
  await fs.mkdir(source);
  await fs.writeFile(path.join(source, 'Server.exe'), 'new');
  await fs.mkdir(f.manager.controller.root, { recursive: true });
  await fs.writeFile(path.join(f.manager.controller.root, 'Server.exe'), 'old');
  const staged = await f.manager.stage(source, { id: 'generic', version: '1.0.0' });
  assert.equal((await f.manager.queue(staged.id)).ok, true);
  let online = true;
  f.manager.controller.status = async () => ({ ...live, online });
  f.manager.controller.processInfo = async () => online ? { pid: 7 } : null;
  f.manager.controller.stop = async () => { online = false; return { ok: true }; };
  f.manager.controller.startExternal = async () => { online = true; return { ok: true }; };
  assert.equal((await f.manager.reconcile(live)).ok, true);
  assert.equal(await fs.readFile(path.join(f.manager.controller.root, 'Server.exe'), 'utf8'), 'new');
  assert.equal((await f.manager.status()).status, 'idle');
});

for (const mode of ['observe', 'supervise']) {
  test(`KESAS local-only blocks every lifecycle entry point in ${mode} mode before process or console access`, async t => {
    const f = await fixture(t);
    f.config.lmp.serverRoot = path.join(f.config.lmp.deploymentRoot, 'Server');
    f.config.lmp.control = { mode, allowExternalProcessStart: true, allowExternalProcessStop: true, allowExternalProcessRestart: true };
    const controller = new LmpController(f.config);
    controller.processInfo = async () => assert.fail('Lifecycle rejection must precede process access');
    controller.logCursor = async () => assert.fail('Lifecycle rejection must precede log access');
    Object.defineProperty(controller, 'deploymentRoot', { get: () => assert.fail('No native launcher or publication may be accessed') });
    for (const action of [
      () => controller.start(), () => controller.stop(), () => controller.restart(),
      () => controller.startExternal(), () => controller.stopExternal({ pid: 7 }),
      () => controller.runSafeCommand('/ReStArTsErVeR'),
      () => controller.runSafeCommand('restartserver'),
      () => controller.sendBridgeCommand('restartserver'),
      () => controller.sendBridgeCommand('shutdown'),
      () => controller.sendConsoleLine('  /RESTARTSERVER  '),
      () => controller.sendConsoleLine('/shutdown'),
      () => controller.publishRelease(path.join(f.root, 'missing.zip'))
    ]) {
      const result = await action();
      assert.equal(result.ok, false);
      assert.match(result.reason, /^host-deployment-local-only: use the reviewed local host operator workflow$/);
    }
    assert.deepEqual(await fs.readdir(f.root), []);
  });
}

test('KESAS local-only say remains available through owned stdin and external announcement bridge', async t => {
  const f = await fixture(t);
  f.config.lmp.serverRoot = path.join(f.config.lmp.deploymentRoot, 'Server');
  f.config.security = {};
  const controller = new LmpController(f.config);
  const lines = [];
  controller.processInfo = async () => ({ pid: 7, owned: true });
  controller.child = { stdin: { writable: true, write: line => lines.push(line) } };
  assert.equal((await controller.say('Server will restart in 15 minutes.')).ok, true);
  assert.deepEqual(lines, ['Server will restart in 15 minutes.\n']);
  controller.processInfo = async () => ({ pid: 7, owned: false });
  const messages = [];
  controller.sendBridgeMessage = async (...args) => { messages.push(args); return { ok: true }; };
  assert.equal((await controller.say('Server is back online.')).ok, true);
  assert.deepEqual(messages, [['Server is back online.', 'Discord', { announcement: true }]]);
});

test('local-only setting leaves generic non-KESAS lifecycle commands unchanged', async t => {
  const f = await fixture(t, { kesas: false });
  f.config.lmp.serverRoot = path.join(f.root, 'generic');
  const controller = new LmpController(f.config);
  controller.processInfo = async () => ({ pid: 7, owned: true });
  const calls = [];
  controller.child = { stdin: { writable: true, write: line => calls.push(line) }, kill: signal => calls.push(signal) };
  assert.equal((await controller.restart()).ok, true);
  assert.equal((await controller.stop()).ok, true);
  assert.deepEqual(calls, ['/restartserver\n', 'SIGTERM']);
});

async function operationFixture(t) {
  const f = await fixture(t);
  const saved = await f.seed('failed');
  const markerPath = path.join(f.root, 'state', 'local-host-operation.json');
  const now = Date.now();
  const marker = { schemaVersion: 1, id: 'a'.repeat(32), ownerPid: 12345, version: 'kesas-rc100',
    createdAt: new Date(now - 1000).toISOString().replace('Z', '0000Z'),
    expiresAt: new Date(now + 60_000).toISOString() };
  const write = value => fs.writeFile(markerPath, JSON.stringify(value));
  return { ...f, saved, markerPath, marker, write };
}

test('valid live local operation overlays notification status without changing failed state or persisted bytes', async t => {
  const f = await operationFixture(t);
  const state = structuredClone(f.manager.state);
  const revision = f.manager.stateRevision;
  const probes = [];
  t.mock.method(process, 'kill', (pid, signal) => { probes.push([pid, signal]); return true; });
  await f.write(f.marker);
  const status = await f.manager.status();
  assert.equal(status.status, 'deploying');
  assert.equal(status.version, f.marker.version);
  assert.equal(status.phase, 'local-operator');
  assert.equal(status.packageId, state.packageId);
  assert.deepEqual(f.manager.state, state);
  assert.equal(f.manager.stateRevision, revision);
  assert.equal(f.manager.operation, null);
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), f.saved);
  assert.deepEqual(probes, [[f.marker.ownerPid, 0]]);
});

test('dead local operation owner does not suppress ordinary outage notification status', async t => {
  const f = await operationFixture(t);
  t.mock.method(process, 'kill', () => { throw Object.assign(new Error('dead'), { code: 'ESRCH' }); });
  await f.write(f.marker);
  assert.equal((await f.manager.status()).status, 'failed');
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), f.saved);
});

test('EPERM owner probe counts as existing for notification-only local operation view', async t => {
  const f = await operationFixture(t);
  t.mock.method(process, 'kill', () => { throw Object.assign(new Error('limited token'), { code: 'EPERM' }); });
  await f.write(f.marker);
  assert.equal((await f.manager.status()).phase, 'local-operator');
  assert.equal(f.manager.state.status, 'failed');
});

test('expired, future, overlong and invalid-calendar local operation windows are ignored', async t => {
  const f = await operationFixture(t);
  t.mock.method(process, 'kill', () => assert.fail('Invalid timestamps must not probe any process'));
  const now = Date.now();
  for (const changes of [
    { expiresAt: new Date(now - 1).toISOString() },
    { createdAt: new Date(now + 30_000).toISOString() },
    { expiresAt: new Date(now + 45 * 60_000).toISOString() },
    { expiresAt: f.marker.createdAt },
    { createdAt: '2026-02-30T12:00:00.000Z' },
    { createdAt: 'not-a-date' }, { expiresAt: 'not-a-date' },
    { createdAt: Date.now() }, { expiresAt: '2026-11-04T12:00:00' }
  ]) {
    await f.write({ ...f.marker, ...changes });
    assert.equal((await f.manager.status()).status, 'failed');
  }
});

test('local operation markers exceeding 4096 bytes are ignored before process probing', async t => {
  const f = await operationFixture(t);
  t.mock.method(process, 'kill', () => assert.fail('Oversized marker must not probe any process'));
  await fs.writeFile(f.markerPath, JSON.stringify(f.marker).padEnd(4097, ' '));
  assert.equal((await f.manager.status()).status, 'failed');
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), f.saved);
});

test('missing, malformed and bad-field local operation markers never alter notification status', async t => {
  const f = await operationFixture(t);
  t.mock.method(process, 'kill', () => assert.fail('Invalid marker must not probe any process'));
  assert.equal((await f.manager.status()).status, 'failed');
  for (const value of [null, [], {}, { ...f.marker, schemaVersion: 2 }, { ...f.marker, schemaVersion: '1' },
    { ...f.marker, id: 'not-a-guid' }, { ...f.marker, ownerPid: 0 }, { ...f.marker, ownerPid: -1 },
    { ...f.marker, ownerPid: '12345' }, { ...f.marker, ownerPid: 1.5 },
    { ...f.marker, version: '../kesas-rc100' }, { ...f.marker, version: 'kesas-rc0' },
    { ...f.marker, extraAuthority: true }]) {
    await f.write(value);
    assert.equal((await f.manager.status()).status, 'failed');
  }
  await fs.writeFile(f.markerPath, '{');
  assert.equal((await f.manager.status()).status, 'failed');
  assert.deepEqual(f.calls, []);
});

test('local operation marker grants no update authority and removal immediately restores failed notification view', async t => {
  const f = await operationFixture(t);
  t.mock.method(process, 'kill', () => true);
  await f.write(f.marker);
  assert.equal((await f.manager.status()).status, 'deploying');
  assert.deepEqual(await f.manager.queue('kesas-rc100'), { ok: false, reason });
  assert.deepEqual(await f.manager.deploy(live, {}), { ok: false, deferred: true, reason });
  assert.deepEqual(await f.manager.recoverNativePublication(live), { ok: false, deferred: true, reason });
  assert.equal(await fs.readFile(f.manager.statePath, 'utf8'), f.saved);
  await fs.unlink(f.markerPath);
  assert.equal((await f.manager.status()).status, 'failed');
  f.config.updates.hostDeploymentMode = 'remote';
  await f.write(f.marker);
  assert.equal((await f.manager.status()).status, 'failed');
  assert.deepEqual(f.calls, []);
});
