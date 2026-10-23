import test from 'node:test';
import assert from 'node:assert/strict';
import { fork } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import vm from 'node:vm';
import { brokerRecoveryState, stopIdleControlChild, workerRecoveryState } from '../supervision.mjs';
import { projectControlAttempt, controlHistory } from '../control-evidence.mjs';

for(const phase of ['healthy','blocked','rolled-back'])test(`GitHub observation failure cannot rewrite retained ${phase} transaction history`,async()=>{
  const source=await fs.readFile(new URL('../anchor.mjs',import.meta.url),'utf8');
  const begin=source.indexOf('async function poll(){'),end=source.indexOf('async function fallback(){',begin);
  assert.ok(begin>=0&&end>begin);
  const transaction={phase,descriptor:{version:'control-8'}};let writes=0;
  const context=vm.createContext({polling:false,recovering:false,stopping:false,pointer:{sequence:7},reason:null,
    engine:{state:async()=>transaction},channel:{intent:async()=>{throw new Error('isolated transport failure');}},
    privateError:async()=>{},save:async()=>{writes++;}});
  vm.runInContext(source.slice(begin,end),context);
  await context.poll();
  assert.equal(writes,0);assert.equal(context.polling,false);assert.equal(transaction.phase,phase);
});

test('independent worker recovery detects stopped tasks and stalled evidence, not inventory or generation refreshes',()=>{
  const now=Date.now(),identity={controlVersion:'control-9',controlSha256:'a'.repeat(64)};
  const status={...identity,observedAt:new Date(now).toISOString(),expiresAt:new Date(now+30000).toISOString(),childObservedAt:now,inventory:{status:'collecting'}};
  const diagnostics={...identity,observedAt:new Date(now-1).toISOString(),stage:'running'};
  const base={running:true,status,diagnostics,version:'control-9',sha256:'a'.repeat(64),startedAt:now-90001,now};
  assert.equal(workerRecoveryState(base).action,'wait');
  assert.equal(workerRecoveryState({...base,running:false}).action,'start');
  assert.equal(workerRecoveryState({...base,status:{...status,expiresAt:new Date(now-1).toISOString()}}).action,'restart');
  assert.equal(workerRecoveryState({...base,diagnostics:{...diagnostics,stage:'failed'}}).action,'restart');
  assert.equal(workerRecoveryState({...base,status:null,startedAt:now-89999}).action,'wait');
  assert.equal(workerRecoveryState({...base,status:null,retryAt:now+1}).action,'wait');
  assert.equal(workerRecoveryState({...base,status:null,restarts:999}).delay,300000);
  assert.equal(workerRecoveryState({...base,status:{...status,controlSha256:'b'.repeat(64)}}).action,'restart');
  assert.equal(workerRecoveryState({...base,status:{...status,observedAt:new Date(now+1).toISOString()}}).action,'restart');
});

test('fresh wrapper reports cannot extend the 90-second child startup deadline',()=>{
  const startedAt=Date.parse('2026-10-23T10:00:00.000Z'),identity={controlVersion:'control-9',controlSha256:'a'.repeat(64)};
  for(const age of [89999,90000,90001,300000]){
    const now=startedAt+age;
    const status={...identity,observedAt:new Date(now).toISOString(),expiresAt:new Date(now+30000).toISOString(),childObservedAt:now};
    const diagnostics={...identity,observedAt:new Date(now-1).toISOString(),stage:'starting'};
    const base={running:true,status,diagnostics,version:'control-9',sha256:identity.controlSha256,startedAt,now};
    assert.equal(workerRecoveryState(base).action,age<90000?'wait':'restart',`startup age ${age}`);
    assert.equal(workerRecoveryState({...base,running:false}).action,'start');
    assert.equal(workerRecoveryState({...base,retryAt:now+1}).action,'wait');
  }
});

test('child IPC freshness, not application health, governs running worker recovery',()=>{
  const now=Date.parse('2026-10-23T10:05:00.000Z'),identity={controlVersion:'control-9',controlSha256:'a'.repeat(64)};
  const status={...identity,observedAt:new Date(now).toISOString(),expiresAt:new Date(now+30000).toISOString(),ready:true};
  const diagnostics={...identity,observedAt:new Date(now-1).toISOString(),stage:'running'};
  const base={running:true,status,diagnostics,version:'control-9',sha256:identity.controlSha256,startedAt:now-300000,now};
  for(const health of ['healthy','offline','unknown']){
    for(const age of [0,89999,90000,90001]){
      const input={...base,status:{...status,health,childObservedAt:now-age}};
      assert.equal(workerRecoveryState(input).action,age<=90000?'wait':'restart',`${health}; heartbeat age ${age}`);
      assert.equal(workerRecoveryState({...input,running:false}).action,'start');
    }
  }
  for(const childObservedAt of [undefined,null,0,NaN,Infinity,now+1,'invalid']){
    const input={...base,status:{...status,health:'healthy',childObservedAt}};
    assert.equal(workerRecoveryState(input).action,'restart');
    assert.equal(workerRecoveryState({...input,startedAt:now-89999}).action,'wait');
    assert.equal(workerRecoveryState({...input,startedAt:now-90000}).action,'restart');
    assert.equal(workerRecoveryState({...input,retryAt:now+1}).action,'wait');
  }
});

test('legacy control-7 without child evidence receives startup grace only while task is running',()=>{
  const now=Date.parse('2026-10-23T10:05:00.000Z'),identity={controlVersion:'control-7',controlSha256:'7'.repeat(64)};
  const status={...identity,observedAt:new Date(now).toISOString(),expiresAt:new Date(now+30000).toISOString()};
  for(const stage of ['starting','running','draining']){
    const diagnostics={...identity,observedAt:new Date(now-1).toISOString(),stage};
    const base={running:true,status,diagnostics,version:'control-7',sha256:identity.controlSha256,startedAt:now-89999,now};
    assert.equal(workerRecoveryState(base).action,'wait');
    assert.equal(workerRecoveryState({...base,startedAt:now-90000}).action,'restart');
    assert.equal(workerRecoveryState({...base,running:false}).action,'start');
  }
});

test('previously ready legacy worker remains stable until authenticated upgrade; new workers cannot omit their child clock',()=>{
  const now=Date.now(),identity={controlVersion:'control-7',controlSha256:'7'.repeat(64)};
  const status={...identity,ready:true,observedAt:new Date(now).toISOString(),expiresAt:new Date(now+30000).toISOString()};
  const base={running:true,status,diagnostics:{...identity,stage:'running'},version:identity.controlVersion,sha256:identity.controlSha256,startedAt:now-300000,now};
  assert.equal(workerRecoveryState(base).action,'wait');
  assert.equal(workerRecoveryState({...base,status:{...status,childObservedAt:null}}).action,'restart');
  assert.equal(workerRecoveryState({...base,status:{...status,ready:false}}).action,'restart');
  assert.equal(workerRecoveryState({...base,version:'control-9',status:{...status,controlVersion:'control-9'},diagnostics:{...base.diagnostics,controlVersion:'control-9'}}).action,'restart');
});

test('protected worker status projects actual child IPC time without refreshing it or using app health',async()=>{
  // Execute only the status producer with isolated dependencies, never the worker entry point.
  const source=await fs.readFile(new URL('../worker.mjs',import.meta.url),'utf8');
  const begin=source.indexOf('async function publishStatus() {'),end=source.indexOf("process.on('message'",begin);
  assert.ok(begin>=0&&end>begin);
  const now=Date.parse('2026-10-23T10:05:00.000Z'),writes=new Map();
  const context=vm.createContext({path,Date:class extends Date{static now(){return now;}},reporting:false,ready:true,closed:false,
    health:'unknown',healthObservedAt:now-12345,inventoryStatus:'collecting',componentVersion:null,inventoryObservedAt:null,components:{},
    collecting:true,nextInventory:0,workerStage:'running',workerReason:null,workerExitCode:null,CAPABILITIES:[],
    pointer:{version:'control-9',bundleSha256:'a'.repeat(64)},settings:{root:'fixture-only',nativeRoot:'fixture-native'},
    readJson:async()=>({version:'kesas-rc1'}),watcherDiagnostics:async()=>({}),version:()=>true,
    writeJson:async(file,value)=>writes.set(path.basename(file),value)});
  vm.runInContext(source.slice(begin,end),context);
  for(const healthObservedAt of [now-12345,now-90001,0,undefined]){
    context.healthObservedAt=healthObservedAt;
    await context.publishStatus();
    const status=writes.get('worker-status.json');
    assert.equal(status.childObservedAt,healthObservedAt>0?healthObservedAt:null);
    assert.equal(status.observedAt,new Date(now).toISOString());
    assert.equal(status.health,'unknown');
  }
});

// Only this generated program is forked. No production entry point, service,
// application operation, privileged process query, or machine-wide kill runs.
const childProgram = `
import fs from 'node:fs/promises';
const [role, stateFile, evidenceModule] = process.argv.slice(2);
let helperActive = false;
let heartbeat;
const send = value => process.connected && process.send(value);
process.on('disconnect', () => process.exit(0));
process.on('message', message => {
  const { id, command } = message;
  if (command === 'crash') process.exit(23);
  if (command === 'stop') process.exit(0);
  if (command === 'stall') clearInterval(heartbeat);
  if (command === 'helper-active') helperActive = true;
  if (command === 'helper-idle') helperActive = false;
  if (command === 'stop-worker' && message.lateHelper) helperActive = true;
  send({ type: 'reply', id, idle: !helperActive, stopped: true, pid: process.pid });
});
if (stateFile) {
  const state = JSON.parse(await fs.readFile(stateFile, 'utf8'));
  const { controlHistory } = await import(evidenceModule);
  send({ type: 'restored', state, history: await controlHistory(state.root, state.transaction), pid: process.pid });
}
const pulse = () => send({ type: 'heartbeat', role, pid: process.pid, observedAt: Date.now() });
pulse();
heartbeat = setInterval(pulse, 20);
`;

async function fixture(t) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'supervision-recovery-'));
  const children = new Set();
  const program = path.join(directory, 'child.mjs');
  await fs.writeFile(program, childProgram);
  t.after(async () => {
    // ChildProcess handles identify only children created by this fixture.
    await Promise.all([...children].map(async ({ child, exited }) => {
      if (child.exitCode === null && child.signalCode === null) child.kill();
      await exited;
    }));
    await fs.rm(directory, { recursive: true, force: true });
  });
  async function launch(role, stateFile = '') {
    const child = fork(program, [role, stateFile, new URL('../control-evidence.mjs', import.meta.url).href], {
      cwd: directory, execArgv: [], stdio: ['ignore', 'ignore', 'pipe', 'ipc'],
      env: { ...process.env, NODE_OPTIONS: '' }
    });
    const messages = [], listeners = new Set();
    let stderr = '', failure, requestId = 0;
    child.stderr.on('data', data => { stderr += data; });
    child.on('message', message => {
      messages.push(message);
      for (const listener of listeners) listener();
    });
    child.on('error', error => { failure = error; for (const listener of listeners) listener(); });
    const exited = new Promise(resolve => child.once('exit', (code, signal) => {
      resolve({ code, signal });
      for (const listener of listeners) listener();
    }));
    children.add({ child, exited });
    function next(predicate) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => finish(new Error(`Fixture IPC timeout: ${role}; ${stderr}`)), 5000);
        function finish(error, message) {
          clearTimeout(timer); listeners.delete(check);
          if (error) reject(error); else resolve(message);
        }
        function check() {
          const index = messages.findIndex(predicate);
          if (index !== -1) return finish(null, messages.splice(index, 1)[0]);
          if (failure) return finish(failure);
          if (child.exitCode !== null || child.signalCode !== null)
            finish(new Error(`Fixture exited before IPC: ${role}; ${stderr}`));
        }
        listeners.add(check); check();
      });
    }
    function request(command, extra = {}) {
      const id = ++requestId;
      const response = next(message => message.type === 'reply' && message.id === id);
      child.send({ id, command, ...extra });
      return response;
    }
    const firstHeartbeat = await next(message => message.type === 'heartbeat');
    assert.equal(firstHeartbeat.pid, child.pid);
    assert.notEqual(child.pid, process.pid);
    return { child, exited, next, request, firstHeartbeat };
  }
  return { directory, launch };
}

function snapshot(instance, overrides = {}) {
  return { alive: instance.child.exitCode === null && instance.child.signalCode === null,
    exitCode: instance.child.exitCode, ready: true, heartbeat: instance.firstHeartbeat.observedAt,
    startedAt: instance.firstHeartbeat.observedAt, now: instance.firstHeartbeat.observedAt, ...overrides };
}

test('isolated worker and broker crashes are observed by parent recovery policy', { timeout: 15000 }, async t => {
  const { launch } = await fixture(t);
  for (const role of ['worker', 'broker']) {
    const instance = await launch(role);
    assert.deepEqual(brokerRecoveryState(snapshot(instance)), { brokerState: 'ready', action: 'wait' });
    instance.child.send({ command: 'crash' });
    assert.deepEqual(await instance.exited, { code: 23, signal: null });
    assert.deepEqual(brokerRecoveryState(snapshot(instance)), {
      brokerState: 'exited', action: 'start', delay: 5000
    });
  }
});

test('live stalled child drains after heartbeat deadline; startup grace is bounded', { timeout: 10000 }, async t => {
  const { launch } = await fixture(t);
  const instance = await launch('broker');
  await instance.request('stall');
  const heartbeat = instance.firstHeartbeat.observedAt;
  assert.equal(instance.child.exitCode, null);
  assert.deepEqual(brokerRecoveryState(snapshot(instance, { now: heartbeat + 45000 })), {
    brokerState: 'ready', action: 'wait'
  });
  assert.deepEqual(brokerRecoveryState(snapshot(instance, { now: heartbeat + 45001 })), {
    brokerState: 'stale', action: 'drain'
  });
  assert.deepEqual(brokerRecoveryState(snapshot(instance, { heartbeat: 0, ready: false, now: heartbeat + 89999 })), {
    brokerState: 'starting', action: 'wait'
  });
  assert.deepEqual(brokerRecoveryState(snapshot(instance, { heartbeat: 0, ready: false, now: heartbeat + 90000 })), {
    brokerState: 'stale', action: 'drain'
  });
});

test('crash retry backoff stays bounded at 300 seconds and never starts before retryAt', () => {
  const delays = [];
  for (const restarts of [0, 1, 2, 3, 4, 5, 6, 7, 100, 1000000]) {
    const state = { alive: false, exitCode: 23, ready: false, heartbeat: 0,
      startedAt: 0, restarts, retryAt: 400000, now: 399999 };
    const pending = brokerRecoveryState(state);
    assert.equal(pending.action, 'wait');
    assert.equal(pending.brokerState, 'exited');
    assert.ok(Number.isFinite(pending.delay) && pending.delay >= 5000 && pending.delay <= 300000);
    delays.push(pending.delay);
    assert.deepEqual(brokerRecoveryState({ ...state, now: state.retryAt }), { ...pending, action: 'start' });
  }
  assert.deepEqual(delays, [5000, 10000, 20000, 40000, 80000, 160000, 300000, 300000, 300000, 300000]);
});

test('applying helper blocks recovery before drain; no control child termination or restart', { timeout: 10000 }, async t => {
  const { launch } = await fixture(t);
  const instance = await launch('broker');
  await instance.request('stall');
  await instance.request('helper-active');
  assert.equal(brokerRecoveryState(snapshot(instance, {
    now: instance.firstHeartbeat.observedAt + 45001
  })).action, 'drain');
  const calls = [];
  const result = await stopIdleControlChild({
    idle: async () => { calls.push('idle'); return (await instance.request('inspect')).idle; },
    drain: async () => { calls.push('drain'); await instance.request('drain'); },
    stopWorker: async () => { calls.push('stopWorker'); return true; },
    terminate: async () => { calls.push('terminate'); instance.child.send({ command: 'stop' }); await instance.exited; }
  });
  assert.equal(result, 'native-helper-active');
  assert.deepEqual(calls, ['idle']);
  assert.equal(instance.child.exitCode, null);
  assert.equal(brokerRecoveryState(snapshot(instance, {
    now: instance.firstHeartbeat.observedAt + 45001
  })).action, 'drain', 'A live protected child must not become a restart candidate.');
});

test('double idle guard protects helper appearing during drain or worker stop', { timeout: 15000 }, async t => {
  const { launch } = await fixture(t);
  for (const lateAt of ['drain', 'stopWorker']) {
    const instance = await launch('broker');
    await instance.request('stall');
    const calls = [];
    const result = await stopIdleControlChild({
      idle: async () => { calls.push('idle'); return (await instance.request('inspect')).idle; },
      drain: async () => {
        calls.push('drain'); await instance.request('drain');
        if (lateAt === 'drain') await instance.request('helper-active');
      },
      stopWorker: async () => {
        calls.push('stopWorker');
        return (await instance.request('stop-worker', { lateHelper: lateAt === 'stopWorker' })).stopped;
      },
      terminate: async () => { calls.push('terminate'); instance.child.send({ command: 'stop' }); await instance.exited; }
    });
    assert.equal(result, 'native-helper-active');
    assert.deepEqual(calls, ['idle', 'drain', 'stopWorker', 'idle']);
    assert.equal(instance.child.exitCode, null);
    assert.equal(brokerRecoveryState(snapshot(instance, {
      now: instance.firstHeartbeat.observedAt + 45001
    })).action, 'drain');
  }
});

test('stop callbacks run in order; unsuccessful worker stop never terminates broker', async () => {
  for (const stopped of [false, true]) {
    const calls = [];
    const result = await stopIdleControlChild({
      idle: async () => { calls.push('idle'); return true; },
      drain: async () => { calls.push('drain'); },
      stopWorker: async () => { calls.push('stopWorker'); return stopped; },
      terminate: async () => { calls.push('terminate'); }
    });
    assert.equal(result, stopped ? 'control-child-stopped' : 'worker-not-stopped');
    assert.deepEqual(calls, stopped ? ['idle', 'drain', 'stopWorker', 'idle', 'terminate'] : ['idle', 'drain', 'stopWorker']);
  }
});

test('PID ownership simulation terminates only fixture child; parent remains alive', { timeout: 10000 }, async t => {
  const { launch } = await fixture(t);
  const instance = await launch('broker');
  const ownerPid = process.pid, childPid = instance.child.pid, calls = [];
  assert.equal(await stopIdleControlChild({
    idle: async () => { calls.push('idle'); return (await instance.request('inspect')).idle; },
    drain: async () => { calls.push('drain'); await instance.request('drain'); },
    stopWorker: async () => { calls.push('stopWorker'); return (await instance.request('stop-worker')).stopped; },
    terminate: async () => {
      calls.push('terminate');
      assert.notEqual(childPid, ownerPid);
      assert.equal(instance.child.pid, childPid);
      instance.child.send({ command: 'stop' });
      assert.deepEqual(await instance.exited, { code: 0, signal: null });
    }
  }), 'control-child-stopped');
  assert.deepEqual(calls, ['idle', 'drain', 'stopWorker', 'idle', 'terminate']);
  assert.equal(process.pid, ownerPid);
});

test('supervisor/controller restart preserves durable failure 8 when attempt 9 becomes healthy', { timeout: 15000 }, async t => {
  const { directory, launch } = await fixture(t);
  const root = path.join(directory, 'control'), transactions = path.join(root, 'transactions');
  await fs.mkdir(transactions, { recursive: true });
  const attempt = (number, phase) => ({ id: String(number).repeat(64),
    descriptor: { version: `control-${number}`, sha256: String(number).repeat(64) },
    phase, stageCode: phase, failure: phase === 'blocked' ? 'worker-failed' : null,
    updatedAt: `2026-10-23T10:00:0${number}.000Z` });
  const failed8 = attempt(8, 'blocked'), healthy9 = attempt(9, 'healthy');
  const failureFile = path.join(transactions, `${failed8.id}.json`);
  const stateFile = path.join(directory, 'durable-state.json');
  const failureBytes = JSON.stringify(failed8);
  await fs.writeFile(failureFile, failureBytes);
  await fs.writeFile(stateFile, JSON.stringify({ root, transaction: failed8, lastFailure: projectControlAttempt(failed8) }));
  const before = await launch('supervisor', stateFile);
  const original = await before.next(message => message.type === 'restored');
  assert.deepEqual(original.history.lastFailure, projectControlAttempt(failed8));
  before.child.send({ command: 'crash' });
  assert.equal((await before.exited).code, 23);
  // All durable data is test-owned. A healthy newer attempt is not a rewrite
  // of the previous failure journal and does not replay application work.
  await fs.writeFile(path.join(transactions, `${healthy9.id}.json`), JSON.stringify(healthy9));
  await fs.writeFile(stateFile, JSON.stringify({ root, transaction: healthy9, lastFailure: original.history.lastFailure }));
  for (const role of ['supervisor-restarted', 'controller-restarted']) {
    const restarted = await launch(role, stateFile);
    const restored = await restarted.next(message => message.type === 'restored');
    assert.notEqual(restored.pid, before.child.pid);
    assert.deepEqual(restored.history.attempted, projectControlAttempt(healthy9));
    assert.equal(restored.history.attempted.phase, 'healthy');
    assert.deepEqual(restored.history.lastFailure, original.history.lastFailure);
    assert.deepEqual(restored.state.lastFailure, original.history.lastFailure);
    assert.equal(restored.history.lastFailure.version, 'control-8');
    assert.equal(restored.history.recoveryAction, null, 'healthy newer control needs no recovery action');
  }
  const reread = await controlHistory(root, healthy9);
  assert.equal(reread.attempted.version, 'control-9');
  assert.equal(reread.lastFailure.version, 'control-8');
  assert.equal(await fs.readFile(failureFile, 'utf8'), failureBytes);
});
