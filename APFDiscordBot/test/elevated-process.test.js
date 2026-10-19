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
    powershellJson: async () => ++calls === 1 ? [] : observation
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
    if (++calls === 1) return [];
    f.ready.startedAt = new Date(f.now - 10000).toISOString();
    await f.save();
    return f.observation;
  };
  assert.equal(await f.controller.processInfo(), null);
});
