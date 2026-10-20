import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { LmpController } from '../src/lmp.js';
import { writeJsonAtomic } from '../src/util.js';

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'kesas-elevated-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const now = Date.now();
  const ready = { pid: 42, startedAt: new Date(now - 20000).toISOString() };
  const live = { serverPid: 42, updatedAt: new Date(now).toISOString(), playerCount: 0, connectedCount: 0, players: [] };
  const process = { ProcessId: 42, Name: 'Server.exe', CreationDate: new Date(now - 30000).toISOString(), ExecutablePath: null, CommandLine: null };
  const observation = { processes: [process], udpOwners: [42] };
  let calls = 0;
  const controller = new LmpController({ lmp: { serverRoot: path.join(root, 'Server'), deploymentRoot: root, port: 18865 } }, {
    now: () => now,
    powershellJson: async script => { calls++; return script.includes('$target=') ? [process] : observation; }
  });
  const save = async () => {
    await writeJsonAtomic(path.join(controller.bridgeRoot, 'ready.json'), ready);
    await writeJsonAtomic(path.join(controller.bridgeRoot, 'status.json'), live);
  };
  await save();
  return { root, now, ready, live, process, observation, controller, save, calls: () => calls };
}

test('elevated KESAS discovery corroborates fresh native bridge with live process and UDP owner', async t => {
  const f = await fixture(t);
  const result = await f.controller.processInfo();
  assert.equal(result.pid, 42);
  assert.equal(result.owned, false);
  assert.equal(result.identitySource, 'native-bridge-udp');
  assert.equal(result.executablePath, null);
  assert.equal(result.executablePathVerified, false);
  assert.equal(f.calls(), 2);
});

test('ordinary exact-path process discovery remains preferred', async t => {
  const f = await fixture(t);
  f.controller.powershellJson = async () => [{ ...f.process, ExecutablePath: f.controller.executablePath }];
  const result = await f.controller.processInfo();
  assert.equal(result.pid, 42);
  assert.equal(result.executablePath, f.controller.executablePath);
  assert.equal(result.identitySource, undefined);
});

for (const [name, mutate] of [
  ['stale status', f => { f.live.updatedAt = new Date(f.now - 60000).toISOString(); }],
  ['future status', f => { f.live.updatedAt = new Date(f.now + 60000).toISOString(); }],
  ['bridge PID mismatch', f => { f.live.serverPid = 43; }],
  ['reused PID from older bridge instance', f => { f.process.CreationDate = new Date(f.now - 1000).toISOString(); }],
  ['non-server process', f => { f.process.Name = 'malware.exe'; }],
  ['different UDP owner', f => { f.observation.udpOwners = [43]; }],
  ['ambiguous UDP ownership', f => { f.observation.udpOwners = [42, 43]; }],
  ['missing process', f => { f.observation.processes = []; }],
  ['visible path points elsewhere', f => { f.process.ExecutablePath = path.join(f.root, 'other', 'Server.exe'); }],
  ['unknown connection count', f => { f.live.connectedCount = null; }],
  ['inconsistent players', f => { f.live.playerCount = 1; f.live.connectedCount = 1; }],
  ['out-of-root bridge', f => { f.controller.config.lmp.bridgeDirectory = path.join(f.root, 'external-bridge'); }],
  ['not a native deployment', f => { delete f.controller.config.lmp.deploymentRoot; }]
]) {
  test(`elevated KESAS discovery rejects ${name}`, async t => {
    const f = await fixture(t);
    mutate(f);
    await f.save();
    assert.equal(await f.controller.processInfo(), null);
  });
}

test('elevated KESAS discovery rejects bridge replacement during OS inspection', async t => {
  const f = await fixture(t);
  let calls = 0;
  f.controller.powershellJson = async () => {
    if (++calls === 1) return [f.process];
    f.ready.startedAt = new Date(f.now - 10000).toISOString();
    await f.save();
    return f.observation;
  };
  assert.equal(await f.controller.processInfo(), null);
});

test('stale bridge is unknown, never proof of an offline server', async t => {
  const f = await fixture(t);
  f.live.updatedAt = new Date(f.now - 60000).toISOString();
  await f.save();
  const observation = await f.controller.observeProcess();
  assert.equal(observation.state, 'unknown');
  assert.equal(observation.reason, 'bridge-status-stale');
  assert.equal(observation.process, null, 'strict lifecycle identity must still reject stale evidence');
});

test('failed OS query and unreadable bridge remain unknown', async t => {
  const f = await fixture(t);
  f.controller.powershellJson = async () => { throw Object.assign(new Error('query unavailable'), { code: 'EACCES' }); };
  const result = await f.controller.observeProcess();
  assert.equal(result.state, 'unknown');
  assert.equal(result.process, null);
  await fs.rm(path.join(f.controller.bridgeRoot, 'status.json'));
  assert.equal((await f.controller.observeProcess()).state, 'unknown');
});

test('only a successful process enumeration proves that the server is absent', async t => {
  const f = await fixture(t);
  f.controller.powershellJson = async () => [];
  assert.deepEqual(await f.controller.observeProcess(), { state: 'offline', reason: 'process-not-found', process: null });
});

test('unknown status exposes no authoritative zero player count', async t => {
  const f = await fixture(t);
  f.live.updatedAt = new Date(f.now - 60000).toISOString();
  await f.save();
  const status = await f.controller.status();
  assert.equal(status.online, null);
  assert.equal(status.processObservation.state, 'unknown');
  assert.equal(status.playerCount, null);
  assert.equal(status.connectedCount, null);
  assert.equal(status.playerCountSource, 'unknown');
  assert.equal(status.healthIssues.some(issue => issue.code === 'server-offline'), false);
});
