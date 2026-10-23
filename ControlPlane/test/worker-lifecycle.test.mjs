import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import vm from 'node:vm';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readJson, noLinks } from '../public-status.mjs';

const source = await fs.readFile(new URL('../worker.mjs', import.meta.url), 'utf8');
const helperStart = source.indexOf('function workerShutdownRequested(');
const helperEnd = source.indexOf('\nasync function launchBot(', helperStart);
assert.ok(helperStart >= 0 && helperEnd > helperStart, 'Worker lifecycle helpers must remain extractable');
const helperSource = source.slice(helperStart, helperEnd);
const sha = 'a'.repeat(64);

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-worker-lifecycle-'));
  t.after(async () => {
    assert.equal(path.dirname(root), path.resolve(os.tmpdir()));
    assert.match(path.basename(root), /^apf-worker-lifecycle-/);
    await fs.rm(root, { recursive: true, force: true });
  });
  const settings = { root, botRoot: path.join(root, 'bot') };
  await fs.mkdir(path.join(settings.botRoot, 'state'), { recursive: true });
  return { settings, ownerFile: path.join(root, 'worker-owner.json'),
    botFile: path.join(settings.botRoot, 'state', 'bot-instance.lock') };
}

function helpers(settings, options = {}) {
  let elapsed = 0;
  const waits = [], probes = [];
  const context = {
    fs, path, crypto, readJson, noLinks, settings, parent: process.pid,
    process: { pid: process.pid, kill(pid, signal) {
      assert.equal(signal, 0, 'Lifecycle helpers may only probe fixture processes');
      probes.push(pid);
      return options.kill ? options.kill(pid, signal) : process.kill(pid, signal);
    } },
    Date: { now: () => elapsed },
    setTimeout(callback, milliseconds) {
      waits.push(milliseconds);
      elapsed += milliseconds;
      return options.pause ? options.pause(callback, milliseconds) : setImmediate(callback);
    }
  };
  const functions = vm.runInNewContext(`${helperSource}\n({acquireWorkerOwner,workerShutdownRequested,parentAlive,waitForPriorBot})`, context,
    { timeout: 1000, filename: 'worker-lifecycle-helpers.vm' });
  return { ...functions, waits, probes, elapsed: () => elapsed };
}

async function liveProcess(t) {
  const child = spawn(process.execPath, ['-e',
    "process.on('message', message => { if (message === 'stop') process.exit(0); }); process.send('ready'); setInterval(() => {}, 1000);"],
  { windowsHide: true, stdio: ['ignore', 'ignore', 'ignore', 'ipc'] });
  const exited = once(child, 'exit');
  const stop = async () => {
    if (child.exitCode === null && child.signalCode === null) child.send('stop');
    await exited;
  };
  t.after(stop);
  const [message] = await once(child, 'message');
  assert.equal(message, 'ready');
  return { pid: child.pid, stop };
}

test('worker owner is claimed exclusively with a PID and unique token, then released', async t => {
  const f = await fixture(t), h = helpers(f.settings);
  const release = await h.acquireWorkerOwner();
  const first = await readJson(f.ownerFile);
  assert.equal(first.pid, process.pid);
  assert.match(first.token, /^[a-f0-9-]{36}$/);
  await release();
  await assert.rejects(fs.stat(f.ownerFile), { code: 'ENOENT' });
  const releaseAgain = await h.acquireWorkerOwner();
  assert.notEqual((await readJson(f.ownerFile)).token, first.token);
  await releaseAgain();
});

test('a live fixture owner prevents a second writer for the entire 120-second bound', async t => {
  const f = await fixture(t), prior = await liveProcess(t), h = helpers(f.settings);
  const previous = JSON.stringify({ pid: prior.pid, token: 'live' });
  await fs.writeFile(f.ownerFile, previous);
  await assert.rejects(h.acquireWorkerOwner(), /Prior worker remains alive/);
  assert.equal(await fs.readFile(f.ownerFile, 'utf8'), previous);
  assert.equal(h.elapsed(), 120_000);
  assert.equal(h.waits.length, 480);
  assert.ok(h.waits.every(milliseconds => milliseconds === 250));
  assert.ok(h.probes.includes(prior.pid));
  assert.equal(process.kill(prior.pid, 0), true);
});

test('a successor waits for the live fixture owner to exit before claiming', async t => {
  const f = await fixture(t), prior = await liveProcess(t);
  const previous = JSON.stringify({ pid: prior.pid, token: 'drain' });
  await fs.writeFile(f.ownerFile, previous);
  let resume, waiting;
  const reachedWait = new Promise(resolve => { waiting = resolve; });
  const h = helpers(f.settings, { pause(callback) { resume = callback; waiting(); } });
  let acquired = false;
  const pending = h.acquireWorkerOwner().then(release => { acquired = true; return release; });
  await reachedWait;
  assert.equal(acquired, false);
  assert.equal(await fs.readFile(f.ownerFile, 'utf8'), previous);
  await prior.stop();
  resume();
  const release = await pending;
  assert.equal((await readJson(f.ownerFile)).pid, process.pid);
  assert.notEqual((await readJson(f.ownerFile)).token, 'drain');
  await release();
});

test('a confirmed dead fixture owner is reclaimed without waiting', async t => {
  const f = await fixture(t), prior = await liveProcess(t);
  await prior.stop();
  await fs.writeFile(f.ownerFile, JSON.stringify({ pid: prior.pid, token: 'dead' }));
  const h = helpers(f.settings), release = await h.acquireWorkerOwner();
  assert.equal((await readJson(f.ownerFile)).pid, process.pid);
  assert.equal(h.waits.length, 0);
  await release();
});

test('owner cleanup never removes a replacement token and tolerates an absent owner', async t => {
  const f = await fixture(t), h = helpers(f.settings), release = await h.acquireWorkerOwner();
  const own = await readJson(f.ownerFile), replacement = { pid: process.pid, token: 'new' };
  await fs.writeFile(f.ownerFile, JSON.stringify(replacement));
  await release();
  assert.deepEqual(await readJson(f.ownerFile), replacement);
  await fs.writeFile(f.ownerFile, JSON.stringify(own));
  await release();
  await release();
  await assert.rejects(fs.stat(f.ownerFile), { code: 'ENOENT' });
});

test('uncertain or malformed worker ownership is rejected without replacing evidence', async t => {
  const f = await fixture(t), h = helpers(f.settings);
  for (const previous of ['{', '{}', '{"pid":0,"token":"fixture"}',
    '{"pid":1.5,"token":"fixture"}', '{"pid":123,"token":""}', '{"pid":"123","token":"fixture"}']) {
    await fs.writeFile(f.ownerFile, previous);
    await assert.rejects(h.acquireWorkerOwner());
    assert.equal(await fs.readFile(f.ownerFile, 'utf8'), previous);
  }
  assert.equal(h.waits.length, 0);
});

test('an uncertain liveness probe cannot reclaim a worker owner', async t => {
  const f = await fixture(t), prior = await liveProcess(t);
  const previous = JSON.stringify({ pid: prior.pid, token: 'guard' });
  await fs.writeFile(f.ownerFile, previous);
  const h = helpers(f.settings, { kill(pid, signal) {
    if (pid === prior.pid) throw Object.assign(new Error('fixture probe denied'), { code: 'EPERM' });
    return process.kill(pid, signal);
  } });
  await assert.rejects(h.acquireWorkerOwner(), { code: 'EPERM' });
  assert.equal(await fs.readFile(f.ownerFile, 'utf8'), previous);
});

test('parent death prevents an ownership claim and requests shutdown', async t => {
  const f = await fixture(t), h = helpers(f.settings, { kill() {
    throw Object.assign(new Error('fixture parent gone'), { code: 'ESRCH' });
  } });
  assert.equal(h.parentAlive(), false);
  assert.equal(h.workerShutdownRequested(false, null, sha, h.parentAlive()), true);
  await assert.rejects(h.acquireWorkerOwner(), /Worker parent exited/);
  await assert.rejects(fs.stat(f.ownerFile), { code: 'ENOENT' });
});

test('shutdown combines the matching drain marker with sticky requests and parent liveness', () => {
  const h = helpers({});
  assert.equal(h.workerShutdownRequested(false, null, sha, true), false);
  assert.equal(h.workerShutdownRequested(false, { bundleSha256: 'b'.repeat(64) }, sha, true), false);
  assert.equal(h.workerShutdownRequested(false, { bundleSha256: sha }, sha, true), true);
  assert.equal(h.workerShutdownRequested(false, null, sha, true), false);
  assert.equal(h.workerShutdownRequested(true, { bundleSha256: sha }, sha, true), true);
  assert.equal(h.workerShutdownRequested(true, null, sha, true), true);
  assert.equal(h.workerShutdownRequested(false, null, sha, false), true);
});

test('prior bot wait accepts absent and confirmed-dead fixture locks without deleting them', async t => {
  const f = await fixture(t), h = helpers(f.settings);
  await h.waitForPriorBot();
  const prior = await liveProcess(t);
  await prior.stop();
  const previous = JSON.stringify({ pid: prior.pid });
  await fs.writeFile(f.botFile, previous);
  await h.waitForPriorBot();
  assert.equal(await fs.readFile(f.botFile, 'utf8'), previous);
  assert.equal(h.waits.length, 0);
});

test('a live prior bot blocks for at most 120 seconds without killing it or changing its lock', async t => {
  const f = await fixture(t), prior = await liveProcess(t), h = helpers(f.settings);
  const previous = JSON.stringify({ pid: prior.pid });
  await fs.writeFile(f.botFile, previous);
  await assert.rejects(h.waitForPriorBot(), /Prior bot remains alive/);
  assert.equal(h.elapsed(), 120_000);
  assert.equal(await fs.readFile(f.botFile, 'utf8'), previous);
  assert.equal(process.kill(prior.pid, 0), true);
});

test('prior bot wait rejects uncertain ownership and parent loss', async t => {
  const f = await fixture(t), h = helpers(f.settings);
  for (const previous of ['{', '{}', '{"pid":0}', '{"pid":"123"}']) {
    await fs.writeFile(f.botFile, previous);
    await assert.rejects(h.waitForPriorBot());
    assert.equal(await fs.readFile(f.botFile, 'utf8'), previous);
  }
  const orphan = helpers(f.settings, { kill() {
    throw Object.assign(new Error('fixture parent gone'), { code: 'ESRCH' });
  } });
  await assert.rejects(orphan.waitForPriorBot(), /Worker parent exited/);
});

test('worker entry sequence waits for in-flight reports and releases ownership after final publication', async () => {
  const start = source.indexOf('const releaseWorkerOwner=await acquireWorkerOwner();');
  assert.ok(start >= 0);
  const events = [];
  const context = {
    acquireWorkerOwner: async () => { events.push('owner'); return async () => { events.push('release'); }; },
    waitForPriorBot: async () => { events.push('prior-bot'); },
    publishStatus: async () => { events.push('publish'); },
    setInterval: () => { events.push('timer'); return { unref() {} }; },
    clearInterval: () => { events.push('clear'); },
    setTimeout(callback, milliseconds) {
      assert.equal(milliseconds, 25);
      events.push('wait');
      context.reporting = false;
      return setImmediate(callback);
    },
    launchBot: async () => { events.push('launch'); },
    collectComponents: () => { events.push('inventory'); },
    reporting: true, exitRequested: true, process: { exitCode: 0 }
  };
  await vm.runInNewContext(`(async()=>{${source.slice(start)}\n})()`, context,
    { timeout: 1000, filename: 'worker-lifecycle-entry.vm' });
  assert.deepEqual(events, ['owner', 'prior-bot', 'publish', 'timer', 'launch', 'inventory', 'clear', 'wait', 'publish', 'release']);
});
