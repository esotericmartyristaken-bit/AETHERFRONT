import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { LmpController } from '../src/lmp.js';
import { UpdateManager } from '../src/update-manager.js';
import { publicStatusEmbed } from '../src/bot.js';
import { writeJsonAtomic } from '../src/util.js';

test('KESAS presence accepts only fresh bridge data from its running process', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'kesas-presence-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const controller = new LmpController({ __root: root, lmp: { serverRoot: root, requireLivePlayerSnapshot: true } });
  controller.processInfo = async () => ({ pid: 42 });
  const status = { serverPid: 42, updatedAt: new Date().toISOString(), playerCount: 1, connectedCount: 2, players: [{ name: 'Pilot' }] };
  const file = path.join(controller.bridgeRoot, 'status.json');
  await writeJsonAtomic(file, status);
  assert.deepEqual(await controller.playerSnapshotUnlocked(), { count: 1, connected: 2, players: status.players, source: 'bridge' });
  for (const invalid of [
    { serverPid: 41 }, { updatedAt: new Date(Date.now() - 60000).toISOString() },
    { playerCount: 0 }, { connectedCount: 0 }, { players: [] }
  ]) {
    await writeJsonAtomic(file, { ...status, ...invalid });
    const snapshot = await controller.playerSnapshotUnlocked();
    assert.equal(snapshot.count, null);
    assert.equal(snapshot.connected, null);
  }
  controller.processInfo = async () => null;
  assert.equal((await controller.playerSnapshotUnlocked()).count, 0);
});

async function staged(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'kesas-stage-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const controller = { root: path.join(root, 'Server'), status: async () => ({ online: true, ready: true, bridge: { ready: true }, playerCount: 0, connectedCount: 0 }) };
  const manager = new UpdateManager({ __root: root, lmp: { deploymentRoot: root }, updates: { requirePreDeployBackup: false, pollIntervalMs: 1 } }, controller, null);
  const packageDir = path.join(manager.stagingRoot, 'test-rc15');
  await fs.mkdir(packageDir, { recursive: true });
  const bytes = Buffer.from('fixture release');
  await fs.writeFile(path.join(packageDir, 'release.zip'), bytes);
  await writeJsonAtomic(path.join(packageDir, 'manifest.json'), { schemaVersion: 1, provider: 'kesas-release', id: 'test-rc15', version: 'kesas-rc15', notes: 'Global chat.', files: [{ source: 'release.zip', destination: 'release.zip', sizeBytes: bytes.length, sha256: crypto.createHash('sha256').update(bytes).digest('hex') }] });
  const now = Date.now();
  await manager.queue('test-rc15', 'test', { now, countdownMinutes: 15 });
  return { manager, controller, now, packageDir };
}

test('KESAS uses signed-release publisher, not raw binary replacement', async t => {
  const { manager, controller } = await staged(t);
  let calls = 0;
  controller.publishRelease = async (archive, notes) => { calls++; assert.match(archive, /release\.zip$/); assert.equal(notes, 'Global chat.'); return { rollbackRoot: 'publisher-backup' }; };
  controller.stop = () => { throw new Error('generic stop must not run'); };
  manager.applyManifest = () => { throw new Error('generic file replacement must not run'); };
  assert.equal((await manager.reconcile(await controller.status())).ok, true);
  assert.equal(calls, 1);
  assert.equal((await manager.status()).lastDeployment.version, 'kesas-rc15');
});

test('KESAS unknown counts cannot trigger empty or expired countdown deployment', async t => {
  const { manager, now } = await staged(t);
  manager.deploy = () => { throw new Error('unknown is not empty'); };
  for (const counts of [{ playerCount: null, connectedCount: null }, { playerCount: 0, connectedCount: null }]) {
    const result = await manager.reconcile({ online: true, ...counts }, {}, now + 16 * 60000);
    assert.equal(result.deferred, true);
    assert.equal(manager.state.force, false);
  }
});

test('KESAS failed warning is retried, not recorded or used to arm restart', async t => {
  const { manager, now } = await staged(t);
  const occupied = { online: true, playerCount: 1, connectedCount: 1 };
  const hooks = { onAnnouncement: async () => { throw new Error('bridge unavailable'); } };
  await assert.rejects(manager.reconcile(occupied, hooks, now), /bridge unavailable/);
  assert.deepEqual(manager.state.countdownAnnouncements, []);
  await assert.rejects(manager.reconcile(occupied, hooks, now + 15 * 60000), /bridge unavailable/);
  assert.equal(manager.state.force, false);
  await manager.reconcile(occupied, { onAnnouncement: async () => {} }, now);
  assert.deepEqual(manager.state.countdownAnnouncements, [15 * 60000]);
});

test('KESAS staged archive cannot change after verification', async t => {
  const { manager, controller, packageDir } = await staged(t);
  await fs.writeFile(path.join(packageDir, 'release.zip'), 'tampered');
  controller.publishRelease = () => { throw new Error('changed archive must not publish'); };
  const result = await manager.reconcile(await controller.status());
  assert.equal(result.ok, false);
  assert.match(result.reason, /hash changed|verification failed/);
});

test('KESAS gives players 15 minutes after a delayed first warning', async t => {
  const { manager, controller, now } = await staged(t);
  const late = now + 20 * 60000;
  const notices = [];
  const hooks = { onAnnouncement: async message => notices.push(message) };
  const occupied = { online: true, playerCount: 1, connectedCount: 1 };
  await manager.reconcile(occupied, hooks, late);
  assert.equal(Date.parse(manager.state.deployAt), late + 15 * 60000);
  assert.match(notices[0], /in 15 minutes/);
  assert.equal(manager.state.force, false);
  let published = false;
  controller.publishRelease = async () => { published = true; return { rollbackRoot: 'backup' }; };
  await manager.reconcile(occupied, hooks, late + 15 * 60000);
  assert.equal(published, true);
  assert.match(notices.at(-1), /restarting now/);
});

test('KESAS public status omits vanilla password and disabled master-list warning', () => {
  const embed = publicStatusEmbed({ online: true, ready: true, playerCount: 0, settings: { name: 'KESAS Science', password: false, register: false }, master: { visible: false } }, null, Date.now(), null, 'old-password').toJSON();
  assert.match(embed.title, /KESAS Science SERVER ONLINE/);
  assert.doesNotMatch(JSON.stringify(embed), /old-password|Server Password/);
});
