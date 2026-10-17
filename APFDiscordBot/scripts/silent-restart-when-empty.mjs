import { loadConfig } from '../src/config.js';
import { LmpController } from '../src/lmp.js';
import path from 'node:path';
import { writeJsonAtomic } from '../src/util.js';

const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const config = await loadConfig();
const controller = new LmpController(config);
const statePath = path.join(config.__root, 'state', 'silent-cheats-restart.json');
const record = state => writeJsonAtomic(statePath, { updatedAt: new Date().toISOString(), ...state });
let zeroChecks = 0;

for (;;) {
  const status = await controller.status();
  const players = Number(status.playerCount);
  const connections = Number(status.connectedCount);
  const empty = status.online && players === 0 && connections === 0;
  zeroChecks = empty ? zeroChecks + 1 : 0;
  await record({ status: 'waiting', pid: status.pid, players, connections, zeroChecks });
  console.log(JSON.stringify({
    at: new Date().toISOString(), phase: 'waiting', pid: status.pid,
    players, connections, zeroChecks
  }));

  if (zeroChecks >= 2) {
    const oldPid = status.pid;
    await record({ status: 'restarting', oldPid, players, connections, zeroChecks });
    const result = await controller.sendBridgeCommand('restartserver');
    console.log(JSON.stringify({
      at: new Date().toISOString(), phase: 'restart-requested', oldPid, result
    }));
    if (!result.ok) {
      await record({ status: 'failed', oldPid, reason: result.reason || 'restart-command-failed' });
      process.exit(4);
    }

    const deadline = Date.now() + 180_000;
    while (Date.now() < deadline) {
      await sleep(3_000);
      const next = await controller.status();
      console.log(JSON.stringify({
        at: new Date().toISOString(), phase: 'verifying', pid: next.pid,
        ready: next.ready, registration: next.registration,
        master: next.master?.visible, bridge: next.bridge?.ready
      }));
      if (next.online && next.pid !== oldPid && next.ready && next.registration &&
          next.master?.visible && next.bridge?.ready) {
        await record({ status: 'complete', oldPid, pid: next.pid, ready: true,
          registration: true, master: true, bridge: true });
        process.exit(0);
      }
    }
    await record({ status: 'failed', oldPid, reason: 'readiness-timeout' });
    process.exit(5);
  }

  await sleep(15_000);
}
