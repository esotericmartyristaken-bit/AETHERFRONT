import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, randomUUID, sign } from 'node:crypto';
import { copyFile, mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequest } from '../LocalReleaseRequest.mjs';
import { pollGitHub } from '../GitHubReleaseWorker.mjs';
import { prepareSource, runOnce } from '../Worker.mjs';

const archive = Buffer.from('native release fixture');
const commit = 'a'.repeat(40);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const context = Buffer.from('AETHERFRONT VPS LOCAL RELEASE APPROVAL v3\n');

async function fixture({ failReceipt = false, status = 'verified', archiveBytes = archive, sourceCommit = commit } = {}) {
  const root = await mkdtemp(join(tmpdir(), 'apf-github-transport-'));
  const keys = generateKeyPairSync('ed25519');
  const remote = {
    publicKeyPath: join(root, 'key.pem'), inboxes: join(root, 'inboxes'),
    attempts: join(root, 'attempts'), outbox: join(root, 'outbox'), sourceTrees: {}
  };
  const config = { host: 'vmi3254434', githubIntake: remote, statusRoot: join(root, 'status'),
    activeFeedPath: join(root, 'feed.json') };
  for (const path of [remote.inboxes, remote.attempts, remote.outbox, config.statusRoot]) await mkdir(path);
  await writeFile(remote.publicKeyPath, keys.publicKey.export({ type: 'spki', format: 'pem' }));
  await writeFile(config.activeFeedPath, '{"version":"kesas-rc52"}');
  const now = Date.now();
  const id = randomUUID().replaceAll('-', '');
  const identity = { requestId: id, version: 'kesas-rc55', action: 'verify', sourceCommit, archiveSha256: hash(archiveBytes) };
  const bytes = createRequest({ ...identity, host: config.host,
    issuedAt: new Date(now).toISOString(), expiresAt: new Date(now + 600000).toISOString() });
  const signed = sign(null, Buffer.concat([context, bytes]), keys.privateKey);
  const envelope = Buffer.from(JSON.stringify({ request: bytes.toString('base64'), signature: signed.toString('base64') }));
  const asset = (id, name, body) => ({ id, name, size: body.length, digest: 'sha256:' + hash(body),
    state: 'uploaded', created_at: new Date(now).toISOString() });
  const assets = [asset(1, 'v3-request-' + id + '.json', envelope), asset(2, hash(archiveBytes) + '.zip', archiveBytes)];
  let executions = 0, receipt;
  const io = {
    token: 'fixture-token',
    async fetch(url, options) {
      if (options?.method === 'POST') {
        if (failReceipt) { failReceipt = false; throw new Error('Fixture GitHub connection lost'); }
        receipt = JSON.parse(options.body);
        const uploaded = asset(3, 'v3-receipt-' + id + '.json', options.body);
        assets.push(uploaded);
        return Response.json(uploaded);
      }
      if (String(url).endsWith('/438071888')) return Response.json({ id: 438071888, draft: true, tag_name: 'apf-operator-inbox', assets });
      if (String(url).endsWith('/assets/1')) return new Response(envelope);
      if (String(url).endsWith('/assets/2')) return new Response(archiveBytes);
      throw new Error('Unexpected fixture URL');
    },
    async dispatch(worker) {
      executions++;
      assert.equal(worker.publicKeyPath, remote.publicKeyPath);
      assert.deepEqual(await readFile(join(worker.inbox, hash(archiveBytes) + '.zip')), archiveBytes);
      await writeFile(join(config.statusRoot, id + '.json'), JSON.stringify({ ...identity, status }));
      if (status === 'failed') await writeFile(join(config.statusRoot, id + '.evidence.json'),
        JSON.stringify({ stage: 'preflight', error: 'An existing native activation/completion journal requires review first', excerpts: ['private data'] }));
    }
  };
  return { config, io, identity, root, receipt: () => receipt, executions: () => executions };
}

test('signed verify transfers exact archive and retries a lost receipt without rerunning', async () => {
  const f = await fixture({ failReceipt: true });
  await assert.rejects(pollGitHub(f.config, f.io), /Fixture GitHub connection lost/);
  assert.equal(f.executions(), 1);
  assert.deepEqual(await pollGitHub(f.config, f.io), { status: 'idle' });
  assert.equal(f.executions(), 1);
  assert.equal(f.receipt().status, 'verified');
  assert.equal(f.receipt().activeVersion, 'kesas-rc52');
  assert.equal(f.receipt().archiveSha256, f.identity.archiveSha256);
});

test('guard failure returns a bounded reason and keeps protected logs on the host', async () => {
  const f = await fixture({ status: 'failed' });
  await pollGitHub(f.config, f.io);
  assert.equal(f.receipt().code, 'activation-journal-present');
  assert.equal(f.receipt().stage, 'preflight');
  assert(!JSON.stringify(f.receipt()).includes('private data'));
});

test('interrupted claim is reported and never dispatched again', async () => {
  const f = await fixture();
  await writeFile(join(f.config.githubIntake.attempts, 'in-flight.json'), JSON.stringify(f.identity));
  await pollGitHub(f.config, f.io);
  assert.equal(f.executions(), 0);
  assert.equal(f.receipt().status, 'interrupted');
});

test('remote publisher rejects a changed protected source tree', async () => {
  const root = await mkdtemp(join(tmpdir(), 'apf-source-pin-'));
  const protectedPaths = ['KESAS-VPS-Server', 'APFDiscordBot', 'ControlPlane', 'Aetherfront-RC44-Work/Deployment'];
  await assert.rejects(prepareSource({
    repoRoot: root, gitPath: 'git.exe', remoteSourceTrees: Object.fromEntries(protectedPaths.map(name => [name, commit]))
  }, { sourceCommit: commit }, { GH_TOKEN: 'fixture' }, (_exe, args) => {
    if (args.includes(commit + ':KESAS-VPS-Server')) return 'b'.repeat(40);
    return args.includes('rev-parse') ? commit : '';
  }), /protected publisher source/);
});

test('RC55 archive verifies through real PowerShell and a clean Git fetch', {
  skip: !process.env.APF_NATIVE_VERIFY_ARCHIVE
}, async () => {
  const sourceCommit = '7f466ddb93aee768e854bde53632be5ca88ca384';
  const archiveBytes = await readFile(process.env.APF_NATIVE_VERIFY_ARCHIVE);
  const f = await fixture({ archiveBytes, sourceCommit });
  const config = f.config;
  Object.assign(config, {
    schemaVersion: 3, root: f.root, inbox: config.githubIntake.inboxes,
    powerShell: process.env.APF_NATIVE_VERIFY_PWSH, gitPath: process.env.APF_NATIVE_VERIFY_GIT,
    nodePath: process.execPath, githubCliPath: process.env.APF_NATIVE_VERIFY_GH,
    moduleSearch: [], publicKeyPath: config.githubIntake.publicKeyPath,
    releaseVerifier: join(f.root, 'Assert-LocalRelease.ps1'),
    credentialDecryptor: join(f.root, 'unused.ps1')
  });
  for (const key of ['privateJobs', 'claims', 'rejected', 'repoRoot', 'nativeEvidenceRoot']) {
    config[key] = join(f.root, key);
    await mkdir(config[key]);
  }
  config.githubIntake.sourceTrees = {
    'KESAS-VPS-Server': '80f021a098924ed9912c7bee683467d30a99c2e0',
    APFDiscordBot: '69ab7350400a6eacb0156f3fc11da8c1dc258675',
    ControlPlane: '68d6b6f579965ef610fa78238c6ead095ecf885a',
    'Aetherfront-RC44-Work/Deployment': '91449318933a1ca59a43a4867c6805ffd9715897'
  };
  await copyFile(fileURLToPath(new URL('../Assert-LocalRelease.ps1', import.meta.url)), config.releaseVerifier);
  await copyFile(fileURLToPath(new URL('../../../APFDiscordBot/scripts/Verify-KesasRelease.ps1', import.meta.url)),
    join(f.root, 'Verify-KesasRelease.ps1'));
  const result = await pollGitHub(config, {
    ...f.io,
    dispatch: worker => runOnce(worker, { publish: () => assert.fail('Verify must not launch a publisher') })
  });
  assert.equal(result.status, 'processed');
  const job = JSON.parse(await readFile(join(config.statusRoot, f.identity.requestId + '.json'), 'utf8'));
  assert.equal(job.status, 'verified', job.code);
  assert.equal(job.productionActivated, false);
  assert.equal(f.receipt().status, 'verified');
  assert.equal(f.receipt().activeVersion, 'kesas-rc52');
});
