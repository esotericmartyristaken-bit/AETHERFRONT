import { LmpController } from './lmp.js';

const root = process.env.APF_LIFECYCLE_ROOT;
if (!root) throw new Error('APF_LIFECYCLE_ROOT is required; refusing an unspecified lifecycle target.');

const controller = new LmpController({ lmp: { serverRoot: root, executable: 'Server.exe', masterListUrl: '', control: { mode: 'supervise', visibleTerminal: false } }, security: {} });
const waitFor = async (predicate, timeoutMs = 30_000) => {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return true;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  return false;
};

const started = await controller.start();
if (!started.ok) throw new Error(`Lifecycle start failed: ${JSON.stringify(started)}`);
const online = await waitFor(async () => Boolean(await controller.processInfo()));
if (!online) throw new Error('Lifecycle smoke process did not become discoverable.');
const stopped = await controller.stop();
if (!stopped.ok) throw new Error(`Lifecycle stop failed: ${JSON.stringify(stopped)}`);
const offline = await waitFor(async () => !(await controller.processInfo()));
if (!offline) throw new Error('Lifecycle smoke process did not exit after stop.');
console.log(JSON.stringify({ started, online, stopped, offline }, null, 2));
