import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { constants, createHash, generateKeyPairSync, sign } from 'node:crypto';
import { publishObservation } from '../observation-publisher.mjs';
import { SIGNING_CONTEXT, verifyReadbackEnvelope } from '../signed-readback.mjs';

const openssl = 'C:/Program Files/Git/usr/bin/openssl.exe';
const available = existsSync(openssl);
const repository = 'esotericmartyristaken-bit/AETHERFRONT';
const base = `https://api.github.com/repos/${repository}/`;
const token = 'fixture_observation_not_a_real_credential';
const head = 'a'.repeat(40), fileSha = 'b'.repeat(40), seedSha = 'c'.repeat(40), commit = 'd'.repeat(40);
let directory, certificate, privateKey, pin;

before(async () => {
  if (!available) return;
  directory = await mkdtemp(path.join(os.tmpdir(), 'apf-observation-publisher-certificate-'));
  execFileSync(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-days', '1',
    '-subj', '/CN=isolated-observation-publisher-test', '-keyout', path.join(directory, 'key.pem'),
    '-out', path.join(directory, 'certificate.der'), '-outform', 'DER'],
  { stdio: 'ignore', timeout: 30_000, windowsHide: true });
  certificate = await readFile(path.join(directory, 'certificate.der'));
  privateKey = await readFile(path.join(directory, 'key.pem'));
  pin = createHash('sha256').update(certificate).digest('hex');
});
after(async () => { if (directory) await rm(directory, { recursive: true, force: true }); });

function check(name, run) { test(name, { skip: available ? false : 'Git OpenSSL unavailable' }, run); }
function payload(now = Date.now()) {
  const observedAt = new Date(now - 1_000).toISOString(), expiresAt = new Date(now + 29_000).toISOString();
  return { schemaVersion: 1, status: {
    schemaVersion: 1, contractVersion: 1, bootstrapVersion: 1, observedAt, expiresAt, capabilities: [],
    control: { version: 'control-7', sequence: 7, sha256: 'e'.repeat(64), status: 'healthy', phase: 'idle',
      pendingVersion: null, lastSuccess: 'control-7', rollback: null, failure: null },
    application: { clientVersion: 'kesas-rc33', packVersion: 'kesas-rc33', status: 'idle', phase: 'idle',
      pendingVersion: null, health: 'healthy', lastSuccess: 'kesas-rc33', failure: null },
    components: Object.fromEntries(['watcher', 'nativeRunner', 'publisher', 'verifier', 'packService', 'serverRuntime',
      'serverPlugins', 'clientBootstrap', 'clientUpdater', 'clientHelper', 'catalog', 'publicMedia', 'dependencies',
      'updateTool', 'certificateTool', 'bootstrap'].map(name => [name, null])),
    runtime: { node: '24.19.0', powershell: '7.5.3', dotnet: '8.0.29' },
  }, diagnostics: {
    schemaVersion: 1, observedAt, expiresAt, controlVersion: 'control-7',
    bootstrap: { nativeAgentSha256: null, publisherWrapperSha256: null, nativeRecoveryVersion: 0 },
    watcher: { stage: null, reason: null, version: null, sourceCommit: null, manifestSha256: null, archiveSha256: null },
    native: null, recovery: null,
  } };
}
function signed(value, key = privateKey) {
  const bytes = Buffer.from(JSON.stringify(value));
  return { schemaVersion: 1, payload: bytes.toString('base64'), certificate: certificate.toString('base64'),
    signature: sign('sha256', Buffer.concat([SIGNING_CONTEXT, bytes]),
      { key, padding: constants.RSA_PKCS1_PADDING }).toString('base64') };
}
const response = (value, status = 200) => new Response(JSON.stringify(value), { status });

async function fixture(t, behavior = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'apf-observation-publisher-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const settings = { root, nativeRoot: path.join(root, 'native'), powershell: 'fixture-powershell-never-executed' };
  await mkdir(path.join(settings.nativeRoot, 'secrets'), { recursive: true });
  await writeFile(path.join(settings.nativeRoot, 'secrets', 'pin.txt'), pin.toUpperCase().match(/../g).join(':') + '\n');
  const value = payload();
  await writeFile(path.join(root, 'host-observation.json'), JSON.stringify(value));
  const sentinels = new Map();
  for (const name of ['deployment/control-plane.json', 'application/active.json', 'control-8-failure.json']) {
    const filename = path.join(root, name), bytes = Buffer.from(`unchanged isolated fixture: ${name}`);
    await mkdir(path.dirname(filename), { recursive: true }); await writeFile(filename, bytes); sentinels.set(filename, bytes);
  }
  const state = { calls: [], creates: 0, puts: 0, signs: 0, branchExists: !behavior.missingBranch, envelopes: [] };
  const branch = () => ({ ref: 'refs/heads/host-observation', object: { type: 'commit', sha: head } });
  const fetcher = async (input, options) => {
    const url = new URL(input);
    assert.equal(url.origin, 'https://api.github.com'); assert.equal(url.username, ''); assert.equal(url.password, '');
    assert.equal(url.hash, ''); assert.ok(input.startsWith(base));
    const endpoint = input.slice(base.length), method = options.method;
    const body = options.body ? JSON.parse(options.body) : undefined;
    state.calls.push({ endpoint, method, body });
    assert.equal(options.redirect, 'error'); assert.ok(options.signal instanceof AbortSignal);
    assert.equal(options.headers.Authorization, `Bearer ${token}`);
    assert.equal(options.headers.Accept, 'application/vnd.github+json');
    assert.equal(JSON.stringify(body)?.includes(token) ?? false, false);
    if (behavior.apiFailure?.endpoint === endpoint) return response({ message: `private response ${token}` }, behavior.apiFailure.status);
    if (method === 'GET' && endpoint === 'git/ref/heads/host-observation')
      return state.branchExists ? response(behavior.branch ?? branch()) : response({ message: token }, 404);
    if (method === 'GET' && endpoint === 'git/ref/heads/deploy')
      return response(behavior.seed ?? { object: { type: 'commit', sha: seedSha } });
    if (method === 'POST' && endpoint === 'git/refs') {
      state.creates++;
      assert.deepEqual(body, { ref: 'refs/heads/host-observation', sha: seedSha });
      if (behavior.creation !== 'refused') state.branchExists = true;
      if (['lost-ack', 'refused'].includes(behavior.creation)) throw Error(`private acknowledgement ${token}`);
      if (behavior.creation === 'race') return response({ message: token }, 422);
      return response(branch(), 201);
    }
    if (method === 'GET' && endpoint === `contents/readback.json?ref=${head}`) {
      if (behavior.missingFile) return response({ message: token }, 404);
      return response(behavior.existing ?? { path: 'readback.json', type: 'file', sha: fileSha });
    }
    if (method === 'PUT' && endpoint === 'contents/readback.json') {
      state.puts++;
      assert.equal(body.branch, 'host-observation'); assert.equal(body.message, 'Refresh signed host observation');
      assert.deepEqual(Object.keys(body).sort(), behavior.missingFile
        ? ['branch', 'content', 'message'] : ['branch', 'content', 'message', 'sha']);
      if (!behavior.missingFile) assert.equal(body.sha, fileSha);
      const bytes = Buffer.from(body.content, 'base64');
      assert.equal(bytes.at(-1), 10); assert.equal(bytes.includes(Buffer.from(token)), false);
      verifyReadbackEnvelope(bytes, { pin }); verifyReadbackEnvelope(bytes, { pin, resource: 'diagnostics' });
      state.envelopes.push(bytes);
      return response(behavior.written ?? { content: { path: 'readback.json' }, commit: { sha: commit } }, behavior.putStatus ?? 200);
    }
    assert.fail(`Unexpected isolated publisher request: ${method} ${endpoint}`);
  };
  const execute = async (command, args, options) => {
    state.signs++;
    assert.equal(command, settings.powershell);
    assert.deepEqual(args, ['-NoProfile', '-NonInteractive', '-File', path.join(root, 'bootstrap', 'Sign-HostObservation.ps1'),
      '-Settings', path.join(root, 'host.json')]);
    assert.deepEqual(options, { windowsHide: true, cwd: root, timeout: 15_000, maxBuffer: 131072 });
    const staged = JSON.parse(await readFile(path.join(root, 'observation-payload.json'), 'utf8'));
    assert.deepEqual(Object.keys(staged).sort(), ['diagnostics', 'schemaVersion', 'status']);
    const envelope = behavior.signer ? await behavior.signer(staged) : signed(staged);
    return { stdout: typeof envelope === 'string' ? envelope : JSON.stringify(envelope), stderr: `ignored private stderr ${token}` };
  };
  // The default network must never be reached, even by an accidental omitted injection.
  t.mock.method(globalThis, 'fetch', () => assert.fail('Live network is forbidden in publisher tests'));
  return { settings, options: { token, fetch: fetcher, execute }, state, value,
    setPayload: value => writeFile(path.join(root, 'host-observation.json'), JSON.stringify(value)),
    unchanged: async () => { for (const [filename, bytes] of sentinels) assert.deepEqual(await readFile(filename), bytes); } };
}
async function rejected(f, message) {
  await assert.rejects(publishObservation(f.settings, f.options), error => {
    if (message) assert.equal(error.message, message);
    assert.equal(error.message.includes(token), false); assert.equal(error.cause, undefined);
    return true;
  });
  assert.equal(f.state.puts, 0);
  await f.unchanged();
}

check('updates only fixed repository observation branch/path; signer is fixed and application/deploy state untouched', async t => {
  const f = await fixture(t);
  // Public fields may arrive in a different order, including nested objects.
  f.value.diagnostics = Object.fromEntries(Object.entries(f.value.diagnostics).reverse().map(([key, value]) =>
    [key, value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).reverse()) : value]));
  await f.setPayload(f.value);
  Object.assign(f.settings, { repository: 'untrusted/other', branch: 'deploy', path: 'deployment/control-plane.json' });
  Object.assign(f.options, { repository: 'untrusted/other', branch: 'deploy', path: 'deployment/application.json' });
  const result = await publishObservation(f.settings, f.options);
  assert.deepEqual(result, { published: true, observedAt: f.value.status.observedAt, expiresAt: f.value.status.expiresAt, commit });
  assert.deepEqual(f.state.calls.map(({ method, endpoint }) => [method, endpoint]), [
    ['GET', 'git/ref/heads/host-observation'], ['GET', `contents/readback.json?ref=${head}`], ['PUT', 'contents/readback.json'],
  ]);
  assert.equal(f.state.signs, 1); assert.equal(f.state.puts, 1); assert.equal(f.state.creates, 0);
  assert.equal(JSON.stringify(result).includes(token), false); await f.unchanged();
});

check('absent readback creates only observation file without supplying an invented file SHA', async t => {
  const f = await fixture(t, { missingFile: true });
  assert.equal((await publishObservation(f.settings, f.options)).published, true);
  assert.equal(f.state.puts, 1); assert.equal(f.state.creates, 0); await f.unchanged();
});

check('branch creation uses deploy only as a read-only seed and reconciles before writing', async t => {
  const f = await fixture(t, { missingBranch: true });
  assert.equal((await publishObservation(f.settings, f.options)).published, true);
  assert.deepEqual(f.state.calls.map(({ method, endpoint }) => [method, endpoint]), [
    ['GET', 'git/ref/heads/host-observation'], ['GET', 'git/ref/heads/deploy'], ['POST', 'git/refs'],
    ['GET', 'git/ref/heads/host-observation'], ['GET', `contents/readback.json?ref=${head}`], ['PUT', 'contents/readback.json'],
  ]);
  assert.equal(f.state.creates, 1); await f.unchanged();
});

check('lost branch creation acknowledgement and concurrent creation reconcile without replay', async t => {
  for (const creation of ['lost-ack', 'race']) await t.test(creation, async t => {
    const f = await fixture(t, { missingBranch: true, creation });
    const result = await publishObservation(f.settings, f.options);
    assert.equal(result.published, true); assert.equal(f.state.creates, 1); assert.equal(f.state.puts, 1);
    assert.equal(JSON.stringify(result).includes(token), false); await f.unchanged();
  });
});

check('unconfirmed branch creation fails without publication, force, or repeated creation', async t => {
  const f = await fixture(t, { missingBranch: true, creation: 'refused' });
  await rejected(f, 'OBSERVATION_BRANCH_INVALID');
  assert.equal(f.state.creates, 1); assert.equal(f.state.signs, 0);
});

check('invalid branch, seed, and existing file identities fail before signing or publication', async t => {
  const cases = [
    { branch: { ref: 'refs/heads/deploy', object: { type: 'commit', sha: head } }, error: 'OBSERVATION_BRANCH_INVALID' },
    { branch: { ref: 'refs/heads/host-observation', object: { type: 'tree', sha: head } }, error: 'OBSERVATION_BRANCH_INVALID' },
    { missingBranch: true, seed: { object: { type: 'commit', sha: 'bad' } }, error: 'OBSERVATION_SEED_INVALID' },
    { existing: { path: 'deployment/control-plane.json', type: 'file', sha: fileSha }, error: 'OBSERVATION_PATH_INVALID' },
    { existing: { path: 'readback.json', type: 'symlink', sha: fileSha }, error: 'OBSERVATION_PATH_INVALID' },
    { existing: { path: 'readback.json', type: 'file', sha: 'bad' }, error: 'OBSERVATION_PATH_INVALID' },
  ];
  for (const behavior of cases) await t.test(behavior.error, async t => {
    const f = await fixture(t, behavior); await rejected(f, behavior.error);
    assert.equal(f.state.creates, 0); assert.equal(f.state.signs, 0);
  });
});

check('stale or nearly expired local observations fail before signing', async t => {
  for (const remaining of [-1_000, 4_000]) await t.test(String(remaining), async t => {
    const f = await fixture(t), now = Date.now();
    f.value.status.observedAt = f.value.diagnostics.observedAt = new Date(now - 20_000).toISOString();
    f.value.status.expiresAt = f.value.diagnostics.expiresAt = new Date(now + remaining).toISOString();
    await f.setPayload(f.value); await rejected(f, 'OBSERVATION_NOT_FRESH'); assert.equal(f.state.signs, 0);
  });
});

check('mismatched diagnostics and private status never reach signing or GitHub PUT', async t => {
  for (const change of [value => { value.diagnostics.observedAt = new Date(Date.now() - 2_000).toISOString(); },
    value => { value.status.privateCredential = token; }]) await t.test('invalid payload', async t => {
    const f = await fixture(t); change(f.value); await f.setPayload(f.value);
    await rejected(f); assert.equal(f.state.signs, 0);
  });
});

check('stale signer output is rejected independently of fresh staged payload', async t => {
  const f = await fixture(t, { signer: () => signed(payload(Date.now() - 60_000)) });
  await rejected(f, 'STALE_STATUS'); assert.equal(f.state.signs, 1);
});

check('forged key, invalid signature, malformed envelope and wrong signing context fail closed', async t => {
  const forged = generateKeyPairSync('rsa', { modulusLength: 2048 }).privateKey;
  for (const signer of [value => signed(value, forged), value => ({ ...signed(value), signature: Buffer.alloc(256).toString('base64') }),
    () => 'not JSON', value => {
      const envelope = signed(value), bytes = Buffer.from(envelope.payload, 'base64');
      envelope.signature = sign('sha256', Buffer.concat([Buffer.from('wrong context\n'), bytes]), privateKey).toString('base64');
      return envelope;
    }]) await t.test('untrusted signer output', async t => {
    const f = await fixture(t, { signer }); await rejected(f, 'SIGNED_READBACK_INVALID');
  });
});

check('wrong pinned certificate rejects otherwise valid signed observation', async t => {
  const f = await fixture(t);
  await writeFile(path.join(f.settings.nativeRoot, 'secrets', 'pin.txt'), 'f'.repeat(64));
  await rejected(f, 'TLS_PIN_MISMATCH'); assert.equal(f.state.signs, 1);
});

check('GitHub authentication errors do not echo credentials or private response bodies', async t => {
  for (const status of [401, 403]) await t.test(String(status), async t => {
    const f = await fixture(t, { apiFailure: { endpoint: 'git/ref/heads/host-observation', status } });
    await rejected(f, 'OBSERVATION_GITHUB_UNAVAILABLE'); assert.equal(f.state.signs, 0);
  });
  const f = await fixture(t); f.options.token = '';
  await rejected(f, 'OBSERVATION_CREDENTIAL_UNAVAILABLE'); assert.equal(f.state.calls.length, 0);
});

check('private outer fields and signer stderr never enter staged public payload, uploaded bytes or result', async t => {
  const f = await fixture(t); f.value.privateToken = token; f.value.rawLogs = [`secret ${token}`]; await f.setPayload(f.value);
  const result = await publishObservation(f.settings, f.options);
  const staged = await readFile(path.join(f.settings.root, 'observation-payload.json'), 'utf8');
  assert.equal(staged.includes(token), false); assert.equal(f.state.envelopes[0].includes(Buffer.from(token)), false);
  assert.equal(JSON.stringify(result).includes(token), false); await f.unchanged();
});

check('private diagnostics cannot enter the published signed payload', async t => {
  for (const addPrivate of [diagnostics => { diagnostics.rawLog = token; },
    diagnostics => { diagnostics.bootstrap.privateCredential = token; },
    diagnostics => { diagnostics.watcher.rawLog = token; }]) {
    const f = await fixture(t); addPrivate(f.value.diagnostics); await f.setPayload(f.value);
    await rejected(f, 'OBSERVATION_DIAGNOSTICS_INVALID');
    assert.equal(f.state.signs, 0);
    assert.equal(f.state.envelopes.length, 0);
    await assert.rejects(readFile(path.join(f.settings.root, 'observation-payload.json')), { code: 'ENOENT' });
  }
});

check('file SHA conflicts fail safely without retrying, deleting, or overwriting the competing observation', async t => {
  const f = await fixture(t, { putStatus: 409 });
  await assert.rejects(publishObservation(f.settings, f.options), { message: 'OBSERVATION_GITHUB_UNAVAILABLE' });
  assert.equal(f.state.puts, 1); assert.equal(f.state.creates, 0);
  assert.equal(f.state.calls.at(-1).body.sha, fileSha);
  assert.equal(f.state.calls.some(call => ['PATCH', 'DELETE'].includes(call.method)), false); await f.unchanged();
});

check('unconfirmed or wrong-path write acknowledgements cannot report success or trigger a retry', async t => {
  for (const written of [{}, { content: { path: 'deployment/control-plane.json' }, commit: { sha: commit } },
    { content: { path: 'readback.json' }, commit: { sha: 'bad' } }]) await t.test('unconfirmed write', async t => {
    const f = await fixture(t, { written });
    await assert.rejects(publishObservation(f.settings, f.options), { message: 'OBSERVATION_WRITE_UNCONFIRMED' });
    assert.equal(f.state.puts, 1); await f.unchanged();
  });
});
