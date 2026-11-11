import { access, mkdir, readFile, readdir, rename, rmdir, unlink, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { join, resolve, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';
import { claimRequest } from './LocalReleaseRequest.mjs';
import { readDraftRequest, fetchDraftArchive, publishReceipt } from './GitHubReleaseInbox.mjs';
import { runOnce } from './Worker.mjs';

const versionPattern = /^kesas-rc[1-9][0-9]{0,8}$/;
const finalStatuses = new Set(['verified', 'healthy', 'failed', 'rejected']);
const reasons = new Map([
  ['Release changes protected publisher source and needs host review', 'publisher-source-changed'],
  ['Protected publication credentials unavailable', 'publication-credentials-unavailable'],
  ['An existing native activation/completion journal requires review first', 'activation-journal-present'],
  ['Another bot update operation requires review; no service stopped', 'another-update-active'],
  ['Healthy receipt differs from approval', 'healthy-receipt-mismatch'],
  ['Private source verification failed', 'source-verification-failed']
]);

async function readJson(path) {
  return JSON.parse(await readFile(path, 'utf8'));
}

async function exists(path) {
  try { await access(path); return true; }
  catch (error) { if (error.code !== 'ENOENT') throw error; return false; }
}

async function writeJson(path, value) {
  const temporary = path + '.' + randomUUID() + '.tmp';
  await writeFile(temporary, JSON.stringify(value), { flag: 'wx', mode: 0o600, flush: true });
  await rename(temporary, path);
}

function identity(request) {
  const { requestId, action, version, sourceCommit, archiveSha256 } = request;
  return { requestId, action, version, sourceCommit, archiveSha256 };
}

async function makeReceipt(config, remote, request) {
  const id = request.requestId;
  const result = await readJson(join(config.statusRoot, id + '.json')).catch(() => null);
  const matches = result && Object.entries(identity(request)).every(([key, value]) => result[key] === value);
  const status = matches && finalStatuses.has(result.status) ? result.status : 'interrupted';
  const evidence = status === 'failed'
    ? await readJson(join(config.statusRoot, id + '.evidence.json')).catch(() => null) : null;
  const feed = await readJson(config.activeFeedPath).catch(() => null);
  const stage = ['preflight', 'verifying-server-task', 'verifying-candidate-firewall', 'local-mode-reload',
    'preparing-and-verifying-candidate', 'review-required', 'publisher'].includes(evidence?.stage)
    ? evidence.stage : null;
  const receipt = {
    schemaVersion: 1, host: config.host, ...identity(request), status, stage,
    code: status === 'failed' ? reasons.get(evidence?.error) ?? 'host-review-required' :
      status === 'interrupted' ? 'inspect-host-job' : null,
    activeVersion: versionPattern.test(feed?.version) ? feed.version : null,
    observedAtUtc: new Date().toISOString()
  };
  await writeJson(join(remote.outbox, id + '.json'), receipt);
  return receipt;
}

export async function pollGitHub(config, io = {}) {
  const remote = config.githubIntake;
  if (!remote || ['publicKeyPath', 'inboxes', 'attempts', 'outbox'].some(key => !isAbsolute(remote[key] ?? '')) ||
      !remote.sourceTrees) throw new Error('Protected GitHub intake configuration is missing');
  const token = io.token ?? process.env.GH_TOKEN;
  if (!token) throw new Error('Protected GitHub publication credential unavailable');
  const request = io.fetch ?? fetch;
  const dispatch = io.dispatch ?? runOnce;
  const upload = async id => {
    const path = join(remote.outbox, id + '.json');
    await publishReceipt(token, id, path, request);
    await unlink(path);
  };

  const inFlight = join(remote.attempts, 'in-flight.json');
  if (await exists(inFlight)) {
    const previous = await readJson(inFlight);
    if (!/^[a-f0-9]{32}$/.test(previous.requestId)) throw new Error('Invalid protected attempt record');
    if (!await exists(join(remote.attempts, previous.requestId + '.json'))) {
      await claimRequest(Buffer.from(JSON.stringify(previous)), previous.requestId, remote.attempts);
    }
    if (!await exists(join(remote.outbox, previous.requestId + '.json'))) await makeReceipt(config, remote, previous);
    await unlink(inFlight);
  }
  // Deliver durable results first. A transport failure can retry an upload, never a deployment.
  const queued = (await readdir(remote.outbox)).filter(name => /^[a-f0-9]{32}\.json$/.test(name)).slice(0, 5);
  for (const name of queued) await upload(name.slice(0, -5));
  const activeVersion = (await readJson(config.activeFeedPath)).version;
  const selected = await readDraftRequest(token, {
    host: config.host, activeVersion, publicKey: await readFile(remote.publicKeyPath)
  }, remote.attempts, request);
  if (!selected) return { status: 'idle' };

  const authorization = selected.authorization;
  const id = authorization.requestId;
  const inbox = join(remote.inboxes, id);
  await writeJson(inFlight, identity(authorization));
  await claimRequest(selected.request, id, remote.attempts);
  const archive = join(inbox, authorization.archiveSha256 + '.zip');
  const signature = join(inbox, 'pending.sig');
  const requestFile = join(inbox, 'pending.json');
  let madeInbox = false;
  try {
    await mkdir(inbox, { mode: 0o700 });
    madeInbox = true;
    await fetchDraftArchive(token, selected, archive, request);
    await writeFile(signature, selected.signature, { flag: 'wx', mode: 0o600 });
    await writeFile(requestFile, selected.request, { flag: 'wx', mode: 0o600 });
    await dispatch({
      ...config, inbox, publicKeyPath: remote.publicKeyPath, remoteSourceTrees: remote.sourceTrees
    });
  } finally {
    // The native worker owns its private release archive and recovery evidence.
    if (madeInbox) {
      for (const path of [signature, requestFile, archive]) {
        await unlink(path).catch(error => { if (error.code !== 'ENOENT') throw error; });
      }
      await rmdir(inbox);
    }
    await makeReceipt(config, remote, authorization);
    await unlink(inFlight);
    await upload(id);
  }
  return { status: 'processed', requestId: id };
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  try {
    if (process.argv.length !== 2) throw new Error('Unexpected arguments');
    const config = await readJson(new URL('./runner.json', import.meta.url));
    await pollGitHub(config);
  } catch {
    console.error('GitHub release poll failed. Inspect the protected runner status.');
    process.exitCode = 1;
  }
}
