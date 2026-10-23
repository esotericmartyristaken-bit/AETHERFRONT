import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import https from 'node:https';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import * as contract from '../contract.mjs';
import { statusZip } from '../public-status.mjs';
import { CHANNEL, REPOSITORY, SIGNED_READBACK_URL, ControllerError, requestPinnedStatus,
  readStatus, readDiagnostics, publishControl, recoverControl, observeControl, runCli, fetchGithubCurrentReadback } from '../controller.mjs';

const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const origin = 'https://controller-test.invalid', pin = 'a'.repeat(64);
const bytes = Buffer.from('controller reliability fixture; never a production bundle');
const descriptor = version => ({ schemaVersion: 1, transactionVersion: 1, version,
  releaseTag: `host-${version}`, assetName: `Aetherfront-Control-${version}.zip`,
  sha256: hash(bytes), sizeBytes: bytes.length, minimumBootstrap: 1 });

test('current readback uses only the fixed authenticated GET and verifies exact blob bytes', async () => {
  const bytes = Buffer.from('{"fixture":true}\n');
  const file = {type:'file',path:'readback.json',encoding:'base64',size:bytes.length,
    sha:crypto.createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex'),content:bytes.toString('base64')};
  const gh = async (args, input, limits) => {
    assert.deepEqual(args.slice(0,5),['api','--hostname','github.com','--method','GET']);
    assert.match(args[5],/^repos\/esotericmartyristaken-bit\/AETHERFRONT\/contents\/readback\.json\?ref=host-observation&observation=[0-9]+$/);
    assert.deepEqual(args.slice(6),['-H','Cache-Control: no-cache']);
    assert.equal(input,undefined);assert.equal(limits.timeoutMs,20_000);
    return JSON.stringify(file);
  };
  assert.deepEqual(await fetchGithubCurrentReadback({gh}),bytes);
  await assert.rejects(fetchGithubCurrentReadback({gh:async()=>{throw new ControllerError('GITHUB_FAILED');}}),{code:'STATUS_UNREACHABLE'});
  for(const change of [{path:'other.json'},{type:'symlink'},{sha:'a'.repeat(40)},{size:128*1024+1},{content:'invalid!'}]){
    await assert.rejects(fetchGithubCurrentReadback({gh:async()=>JSON.stringify({...file,...change})}),/INVALID_STATUS/);
  }
});
function report(control = {}) {
  const now = Date.now();
  return { schemaVersion: 1, contractVersion: 1, bootstrapVersion: 1,
    observedAt: new Date(now - 100).toISOString(), expiresAt: new Date(now + 60_000).toISOString(),
    capabilities: [...contract.CAPABILITIES],
    control: { version: 'control-7', sequence: 7, sha256: hash('active-7'), status: 'healthy', phase: 'idle',
      pendingVersion: null, lastSuccess: 'control-7', rollback: null, failure: null, ...control },
    application: { clientVersion: 'kesas-rc31', packVersion: 'kesas-rc31', status: 'idle', phase: 'idle',
      pendingVersion: null, health: 'healthy', lastSuccess: 'kesas-rc31', failure: null },
    components: Object.fromEntries(['watcher', 'nativeRunner', 'publisher', 'verifier', 'packService', 'serverRuntime',
      'serverPlugins', 'clientBootstrap', 'clientUpdater', 'clientHelper', 'catalog', 'publicMedia', 'dependencies',
      'updateTool', 'certificateTool', 'bootstrap'].map(name => [name, hash(name)])),
    runtime: { node: '24.19.0', powershell: '7.5.3', dotnet: '8.0.29' } };
}
async function fixture(t, settings = {}) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'controller-reliability-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const candidate = descriptor('control-8'), archive = path.join(directory, candidate.assetName);
  await fs.writeFile(archive, bytes);
  const remote = { head: '1'.repeat(40), fileSha: 'a'.repeat(40), descriptor: descriptor('control-7'),
    release: settings.missingRelease ? null : { id: 8, tag_name: candidate.releaseTag, draft: false, prerelease: true },
    assets: [], uploads: 0, creates: 0, puts: 0, reads: 0, sleeps: [], calls: [] };
  const asset = () => ({ name: candidate.assetName, state: 'uploaded', size: candidate.sizeBytes,
    digest: `sha256:${candidate.sha256}`,
    browser_download_url: `https://github.com/${REPOSITORY}/releases/download/${candidate.releaseTag}/${candidate.assetName}` });
  const remember = new Map();
  const gh = async (args, input) => {
    remote.calls.push(args);
    assert.equal(args.includes('--clobber'), false);
    if (args[0] === 'release') {
      assert.deepEqual(args, ['release', 'upload', candidate.releaseTag, archive, '--repo', `github.com/${REPOSITORY}`]);
      remote.uploads++;
      if (settings.interruptedAlways || (settings.interrupted && remote.uploads === 1)) throw Error('upload interrupted');
      remote.assets = [asset()];
      if (settings.uploadLost) throw Error('lost upload acknowledgement');
      return '';
    }
    assert.deepEqual(args.slice(0, 4), ['api', '--hostname', 'github.com', '--method']);
    const method = args[4], endpoint = args[5].slice(`repos/${REPOSITORY}`.length).replace(/^\//, '');
    if (method === 'GET') remote.reads++;
    if (settings.authFailure) throw Error('private authentication failure');
    if (!endpoint) return JSON.stringify({ full_name: REPOSITORY, private: false, archived: false, disabled: false });
    if (endpoint === 'git/ref/heads/deploy') {
      remember.set(remote.head, { descriptor: remote.descriptor, fileSha: remote.fileSha });
      return JSON.stringify({ object: { sha: remote.head } });
    }
    if (endpoint === `git/trees/${'d'.repeat(40)}`)
      return JSON.stringify({ tree: [{ path: 'control-plane.json', type: 'blob', mode: '100644', sha: remote.fileSha }] });
    if (endpoint.startsWith('git/trees/'))
      return JSON.stringify({ tree: [{ path: 'deployment', type: 'tree', sha: 'd'.repeat(40) }] });
    if (endpoint.startsWith(`contents/${CHANNEL}?ref=`)) {
      const saved = remember.get(endpoint.split('?ref=')[1]);
      return JSON.stringify({ sha: saved.fileSha, encoding: 'base64', content: Buffer.from(JSON.stringify(saved.descriptor)).toString('base64') });
    }
    if (endpoint.startsWith('releases?')) return JSON.stringify(remote.release ? [remote.release] : []);
    if (endpoint === 'releases' && method === 'POST') {
      remote.creates++;
      if (settings.createInterrupted && remote.creates === 1) throw Error('create did not land');
      remote.release = { id: 8, tag_name: candidate.releaseTag, draft: false, prerelease: true };
      if (settings.createLost) throw Error('lost release acknowledgement');
      return JSON.stringify(remote.release);
    }
    if (endpoint === 'releases/8/assets?per_page=100') {
      if (settings.readbackLost && remote.uploads && !remote.readbackLost) {
        remote.readbackLost = true; throw Error('lost asset readback');
      }
      return JSON.stringify(remote.assets);
    }
    if (endpoint === `contents/${CHANNEL}` && method === 'PUT') {
      remote.puts++;
      const body = JSON.parse(input);
      assert.equal(body.branch, 'deploy'); assert.equal(body.sha, remote.fileSha);
      if (!settings.refusePut) {
        remote.descriptor = JSON.parse(Buffer.from(body.content, 'base64'));
        remote.head = '2'.repeat(40); remote.fileSha = 'b'.repeat(40);
      }
      if (settings.putLost || settings.refusePut) throw Error('lost channel acknowledgement');
      return '{}';
    }
    assert.fail(`Unexpected ${method} ${endpoint}`);
  };
  const options = { archive, version: candidate.version, origin, pin, contract, gh,
    fetchStatus: async () => statusZip(report()), verifyArchive: async () => {},
    sleep: async milliseconds => { remote.sleeps.push(milliseconds); },
    fetchArtifactDigest: async () => {
      if (settings.concurrentChange) { remote.head = '3'.repeat(40); remote.fileSha = 'c'.repeat(40); }
      return candidate.sha256;
    } };
  return { options, remote, candidate, asset };
}

test('interrupted absent upload retries without clobber; channel follows digest verification', async t => {
  const f = await fixture(t, { interrupted: true });
  assert.equal((await publishControl(f.options)).status, 'published');
  assert.equal(f.remote.uploads, 2); assert.equal(f.remote.puts, 1);
  assert.deepEqual(f.remote.sleeps, [250]);
});
test('lost release, upload and channel acknowledgements reconcile without replay', async t => {
  const f = await fixture(t, { missingRelease: true, createLost: true, uploadLost: true, putLost: true, readbackLost: true });
  assert.equal((await publishControl(f.options)).ok, true);
  assert.equal(f.remote.creates, 1); assert.equal(f.remote.uploads, 1); assert.equal(f.remote.puts, 1);
});
test('absent release retries only after readback confirms absence', async t => {
  const f = await fixture(t, { missingRelease: true, createInterrupted: true });
  assert.equal((await publishControl(f.options)).ok, true);
  assert.equal(f.remote.creates, 2);
});
test('upload retry exhaustion bounded; controller resume keeps exact version and bytes', async t => {
  const settings = { interruptedAlways: true }, f = await fixture(t, settings);
  await assert.rejects(publishControl(f.options), /PUBLICATION_UNCONFIRMED/);
  assert.equal(f.remote.uploads, 3); assert.equal(f.remote.puts, 0);
  assert.deepEqual(f.remote.sleeps, [250, 500]);
  settings.interruptedAlways = false;
  assert.equal((await publishControl(f.options)).status, 'published');
  assert.equal(f.remote.creates, 0); assert.deepEqual(f.remote.descriptor, f.candidate);
  assert.equal((await publishControl(f.options)).status, 'already-published');
  assert.equal(f.remote.uploads, 4); assert.equal(f.remote.puts, 1);
});
test('incomplete or conflicting immutable assets never overwritten or deleted', async t => {
  for (const change of [{ state: 'starter', size: 0 }, { digest: `sha256:${'f'.repeat(64)}` }]) {
    const f = await fixture(t); f.remote.assets = [{ ...f.asset(), ...change }];
    await assert.rejects(publishControl(f.options), /IMMUTABLE_ASSET_CONFLICT/);
    assert.equal(f.remote.uploads, 0); assert.equal(f.remote.puts, 0);
    assert.ok(f.remote.calls.every(args => !args.includes('DELETE')));
  }
});
test('conflicting channel and unconfirmed PUT never trigger channel write retries', async t => {
  const changed = await fixture(t, { concurrentChange: true });
  await assert.rejects(publishControl(changed.options), /CHANNEL_CHANGED/);
  assert.equal(changed.remote.puts, 0);
  const refused = await fixture(t, { refusePut: true });
  await assert.rejects(publishControl(refused.options), /PUBLICATION_UNCONFIRMED/);
  assert.equal(refused.remote.puts, 1);
});
test('authentication errors bounded, sanitized, never interpreted as missing release', async t => {
  const f = await fixture(t, { authFailure: true });
  const result = await runCli(['publish-control'], f.options);
  assert.deepEqual(JSON.parse(result.output), { ok: false, error: 'GITHUB_FAILED' });
  assert.equal(f.remote.reads, 3); assert.equal(f.remote.creates + f.remote.uploads + f.remote.puts, 0);
});
test('freshness rechecked before retry; interrupted upload cannot outlive pinned preflight', async t => {
  const f = await fixture(t, { interrupted: true });
  f.options.fetchStatus = async () => {
    const value = report();
    if (f.remote.uploads) value.expiresAt = new Date(Date.now() - 1).toISOString();
    return statusZip(value);
  };
  await assert.rejects(publishControl(f.options), /STALE_STATUS/);
  assert.equal(f.remote.uploads, 1); assert.equal(f.remote.puts, 0);
});

function observation(extra = {}) {
  return { origin, pin, version: 'control-8', sha256: hash(bytes), contract,
    timeoutMs: 10, pollMs: 1, fetchStatus: async () => statusZip(report()), ...extra };
}
const healthy = () => report({ version: 'control-8', sequence: 8, sha256: hash(bytes), lastSuccess: 'control-8' });
test('read-only observation emits exact terminal version/hash; no GitHub access', async () => {
  const result = await runCli(['observe-control', '--version', 'control-8', '--sha256', hash(bytes)],
    observation({ fetchStatus: async () => statusZip(healthy()), gh: () => assert.fail('observe must not write or read GitHub') }));
  assert.equal(result.exitCode, 0);
  assert.deepEqual(JSON.parse(result.output), { ok: true, status: 'healthy', terminal: true, version: 'control-8',
    sha256: hash(bytes), observedVersion: 'control-8', observedSha256: hash(bytes), phase: 'idle', failure: null });
});
test('active 7 with unattributed failed 8 times out; never invents version 9', async () => {
  const result = await observeControl(observation({ fetchStatus: async () => statusZip(report({ status: 'rolled-back', failure: 'health-failed' })) }));
  assert.equal(result.ok, false); assert.equal(result.status, 'timeout');
  assert.equal(result.version, 'control-8'); assert.equal(result.observedVersion, 'control-7');
});
test('observation tolerates transport interruption and waits through pending target', async () => {
  let reads = 0;
  const result = await observeControl(observation({ timeoutMs: 1000, fetchStatus: async () => {
    reads++;
    if (reads === 1) throw Error('status transport interrupted');
    return statusZip(reads === 2 ? report({ pendingVersion: 'control-8', status: 'waiting-existing-guard' }) : healthy());
  } }));
  assert.equal(result.status, 'healthy'); assert.equal(reads, 3);
});

test('fresh degraded fallback during broker startup is not a terminal transaction failure', async () => {
  let reads=0;
  const result=await observeControl(observation({timeoutMs:1000,fetchStatus:async()=>statusZip(++reads===1?
    report({version:'control-8',sequence:8,sha256:hash(bytes),status:'blocked',phase:'broker-unavailable',failure:'broker-report-unavailable'}):healthy())}));
  assert.equal(result.ok,true);assert.equal(reads,2);
});
test('observation distinguishes target hash conflict, exact failure and supersession', async () => {
  for (const [control, expected] of [
    [{ version: 'control-8', sequence: 8 }, 'hash-conflict'],
    [{ version: 'control-8', sequence: 8, sha256: hash(bytes), status: 'blocked', failure: 'health-failed' }, 'blocked'],
    [{ version: 'control-9', sequence: 9 }, 'superseded'],
  ]) {
    const result = await observeControl(observation({ fetchStatus: async () => statusZip(report(control)) }));
    assert.equal(result.status, expected); assert.equal(result.ok, false);
  }
});
test('wait resumes already-published channel and produces machine-readable completion', async t => {
  const f = await fixture(t);
  await publishControl(f.options);
  f.options.fetchStatus = async () => statusZip(healthy());
  const result = await runCli(['publish-control', '--wait', '--timeout-ms', '100', '--poll-ms', '1'], f.options);
  const value = JSON.parse(result.output);
  assert.equal(result.exitCode, 0); assert.equal(value.status, 'healthy');
  assert.equal(value.publicationStatus, 'already-published'); assert.equal(value.version, 'control-8');
  assert.equal(value.sha256, f.candidate.sha256); assert.equal(f.remote.puts, 1);
});
test('publish --wait timeout returns nonzero with immutable publication identity', async t => {
  const f = await fixture(t);
  const result = await runCli(['publish-control', '--wait', '--timeout-ms', '1', '--poll-ms', '1'], f.options);
  const value = JSON.parse(result.output);
  assert.equal(result.exitCode, 1); assert.equal(value.status, 'timeout');
  assert.equal(value.publicationStatus, 'published'); assert.deepEqual(value.descriptor, f.candidate);
  assert.equal(value.version, f.candidate.version); assert.equal(value.sha256, f.candidate.sha256);
  assert.equal(f.remote.puts, 1);
});
test('observation preserves pin, freshness and strict CLI guards', async () => {
  await assert.rejects(observeControl(observation({ pin: 'bad' })), /INVALID_ARGUMENT/);
  const stale = await observeControl(observation({ fetchStatus: async () => {
    const value = healthy(); value.expiresAt = new Date(Date.now() - 1).toISOString(); return statusZip(value);
  } }));
  assert.equal(stale.status, 'timeout'); assert.equal(stale.observedVersion, null);
  for (const argv of [['observe-control', '--wait'], ['publish-control', '--wait', '--wait'],
    ['observe-control', '--timeout-ms', '0'], ['observe-control', '--poll-ms', 'Infinity']])
    assert.equal((await runCli(argv, observation())).exitCode, 1);
});

function failedRequest(code = 'STATUS_UNREACHABLE') {
  return () => {
    const req = new EventEmitter();
    req.setTimeout = () => req;
    req.destroy = () => {};
    req.end = () => queueMicrotask(() => req.emit('error', new ControllerError(code)));
    return req;
  };
}

test('injected requests never acquire implicit internet fallback; HTTPS disables fallback', async () => {
  await assert.rejects(requestPinnedStatus({ origin, pin, request: failedRequest() }), /STATUS_UNREACHABLE/);
  await assert.rejects(requestPinnedStatus({ origin, pin, transport: 'github', request: failedRequest() }), /STATUS_UNREACHABLE/);
  let reads = 0;
  await assert.rejects(requestPinnedStatus({ origin, pin, transport: 'https', request: failedRequest(),
    fetchSignedReadback: () => { reads++; assert.fail('HTTPS must not fall back'); } }), /STATUS_UNREACHABLE/);
  assert.equal(reads, 0);
});

test('only unreachable HTTPS triggers one explicitly injected signed readback', async () => {
  let reads = 0;
  const fetchSignedReadback = async () => { reads++; return Buffer.from('{}'); };
  await assert.rejects(requestPinnedStatus({ origin, pin, request: failedRequest(), fetchSignedReadback }), /INVALID_STATUS/);
  assert.equal(reads, 1);
  for (const code of ['TLS_PIN_MISMATCH', 'INVALID_STATUS', 'INVALID_STATUS_ZIP', 'STALE_STATUS']) {
    await assert.rejects(requestPinnedStatus({ origin, pin, request: failedRequest(code), fetchSignedReadback }), { code });
    assert.equal(reads, 1);
  }
});

test('explicit GitHub skips origin request; invalid and oversized readbacks fail closed without gh writes', async () => {
  for (const envelope of [Buffer.from('{}'), Buffer.from('not signed JSON'), Buffer.alloc(128 * 1024 + 1)]) {
    let reads = 0;
    await assert.rejects(readStatus({ origin, pin, contract, transport: 'github',
      request: () => assert.fail('GitHub mode must not contact origin'),
      gh: () => assert.fail('readback must not invoke gh'),
      fetchSignedReadback: async () => { reads++; return envelope; } }), /INVALID_STATUS/);
    assert.equal(reads, 1);
  }
});

test('fallback preserves pin and stale transport errors; never masks them with a second read', async () => {
  for (const code of ['TLS_PIN_MISMATCH', 'STALE_STATUS', 'INVALID_STATUS']) {
    let reads = 0;
    await assert.rejects(requestPinnedStatus({ origin, pin, request: failedRequest(),
      fetchSignedReadback: async () => { reads++; throw new ControllerError(code); } }), { code });
    assert.equal(reads, 1);
  }
});

function assertReadbackUrl(value) {
  const url = new URL(value);
  assert.equal(url.origin, 'https://raw.githubusercontent.com');
  assert.equal(url.pathname, '/esotericmartyristaken-bit/AETHERFRONT/host-observation/readback.json');
  assert.equal(url.username, ''); assert.equal(url.password, ''); assert.equal(url.hash, '');
  assert.deepEqual([...url.searchParams.keys()], ['observation']);
  assert.match(url.search, /^\?observation=[0-9]+$/);
  assert.equal(`${url.origin}${url.pathname}`, SIGNED_READBACK_URL);
  return url;
}

test('fixed anonymous raw download rejects redirects and body limits without alternate requests', async t => {
  const timestamp = 1_792_738_800_000;
  t.mock.method(Date, 'now', () => timestamp);
  for (const settings of [{ status: 302 }, { declared: 128 * 1024 + 1 }, { size: 128 * 1024 + 1 }, { size: 2, declared: 3 }]) {
    let requests = 0;
    const mocked = t.mock.method(https, 'request', (url, options, callback) => {
      requests++;
      assert.equal(assertReadbackUrl(url).searchParams.get('observation'), String(timestamp));
      assert.equal(options.method, 'GET'); assert.equal(options.rejectUnauthorized, true);
      assert.equal(options.agent, false);
      assert.deepEqual(Object.keys(options.headers).sort(), ['Accept', 'Accept-Encoding', 'Cache-Control']);
      assert.equal(options.headers['Cache-Control'], 'no-cache');
      const req = new EventEmitter(); req.destroy = () => {};
      req.setTimeout = milliseconds => { assert.equal(milliseconds, 20_000); return req; };
      req.end = () => queueMicrotask(() => {
        const response = new PassThrough(); response.statusCode = settings.status ?? 200;
        response.headers = settings.declared ? { 'content-length': String(settings.declared) } : {};
        callback(response); response.end(Buffer.alloc(settings.size ?? 2));
      });
      return req;
    });
    await assert.rejects(requestPinnedStatus({ origin, pin, transport: 'github' }), /INVALID_STATUS/);
    assert.equal(requests, 1); mocked.mock.restore();
  }
});

test('default real-network path falls back once; raw deadline remains bounded at 20 seconds', async t => {
  const urls = [];
  t.mock.method(https, 'request', (url, options, callback) => {
    urls.push(String(url));
    const req = new EventEmitter(); req.destroy = () => {}; req.setTimeout = () => req;
    req.end = () => queueMicrotask(() => {
      if (new URL(url).hostname !== 'raw.githubusercontent.com') { req.emit('error', Error('offline fixture')); return; }
      const response = new PassThrough(); response.statusCode = 200; response.headers = {};
      callback(response); response.end('{}');
    });
    return req;
  });
  await assert.rejects(requestPinnedStatus({ origin, pin }), /INVALID_STATUS/);
  assert.equal(urls.length, 2);
  assert.equal(urls[0], `${origin}/kesas-host-status.zip`); assertReadbackUrl(urls[1]);
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let destroyed = false;
  t.mock.method(https, 'request', () => {
    const req = new EventEmitter(); req.end = () => {}; req.setTimeout = () => req;
    req.destroy = () => { destroyed = true; }; return req;
  });
  const pending = requestPinnedStatus({ origin, pin, transport: 'github' });
  const rejected = assert.rejects(pending, /STATUS_UNREACHABLE/);
  t.mock.timers.tick(20_000);
  await rejected; assert.equal(destroyed, true);
});

test('each raw read generates its own timestamp; caller-supplied URLs and query sources are ignored', async t => {
  let timestamp = 1_792_738_800_000;
  t.mock.method(Date, 'now', () => timestamp);
  const observations = [];
  t.mock.method(https, 'request', (url, options, callback) => {
    observations.push(assertReadbackUrl(url).searchParams.get('observation'));
    assert.equal(options.headers['Cache-Control'], 'no-cache');
    const req = new EventEmitter(); req.destroy = () => {}; req.setTimeout = () => req;
    req.end = () => queueMicrotask(() => {
      const response = new PassThrough(); response.statusCode = 200; response.headers = {};
      callback(response); response.end('{}');
    });
    return req;
  });
  for (let read = 0; read < 2; read++) {
    await assert.rejects(requestPinnedStatus({ origin, pin, transport: 'github',
      readbackUrl: 'https://untrusted.invalid/other?token=private', observation: 'caller-value',
      query: '?observation=caller-value&token=private' }), /INVALID_STATUS/);
    timestamp++;
  }
  assert.deepEqual(observations, ['1792738800000', '1792738800001']);
});

test('observation retries stale readback then succeeds; wrong pin and invalid status remain terminal', async () => {
  let reads = 0;
  const result = await observeControl(observation({ timeoutMs: 1000, fetchStatus: async () => {
    reads++;
    if (reads === 1) throw new ControllerError('STALE_STATUS');
    return statusZip(healthy());
  } }));
  assert.equal(result.status, 'healthy'); assert.equal(reads, 2);
  for (const code of ['TLS_PIN_MISMATCH', 'INVALID_STATUS', 'INVALID_STATUS_ZIP']) {
    let attempts = 0;
    await assert.rejects(observeControl(observation({ fetchStatus: async () => {
      attempts++; throw new ControllerError(code);
    } })), { code });
    assert.equal(attempts, 1);
  }
});

test('CLI accepts auto/https/github across readback commands; rejects other or repeated transports', async () => {
  for (const command of ['status', 'diagnostics', 'preflight', 'publish-control', 'observe-control', 'recover']) {
    for (const transport of ['auto', 'https', 'github']) {
      let called = false;
      const result = await runCli([command, '--transport', transport], observation({
        fetchStatus: async options => { called = true; assert.equal(options.transport, transport); throw new ControllerError('TLS_PIN_MISMATCH'); },
        fetchDiagnostics: async options => { called = true; assert.equal(options.transport, transport); throw new ControllerError('TLS_PIN_MISMATCH'); },
      }));
      assert.equal(called, true); assert.equal(JSON.parse(result.output).error, 'TLS_PIN_MISMATCH');
    }
  }
  for (const argv of [['status', '--transport', 'http'], ['status', '--transport', 'auto', '--transport', 'github'], ['read', '--transport', 'github']]) {
    const result = await runCli(argv, observation());
    assert.equal(JSON.parse(result.output).error, 'INVALID_ARGUMENT');
  }
});

const failedTransaction = hash('failed transaction 8');
function failedAttempt(extra = {}) {
  return { version: 'control-8', sha256: hash('failed control-8'), transactionId: failedTransaction,
    phase: 'blocked', stage: 'health', failure: 'health-failed', updatedAt: new Date().toISOString(), ...extra };
}

test('attributed blocked/rolled-back target terminates while active remains older; wrong hash conflicts', async () => {
  for (const phase of ['blocked', 'rolled-back']) {
    const attempted = failedAttempt({ sha256: hash(bytes), phase });
    const result = await observeControl(observation({
      fetchStatus: async () => statusZip(report({ attempted })) }));
    assert.equal(result.status, phase); assert.equal(result.observedVersion, 'control-7');
    assert.equal(result.attemptedVersion, 'control-8'); assert.equal(result.attemptedSha256, hash(bytes));
    assert.equal(result.attemptedTransactionId, failedTransaction); assert.equal(result.failure, 'health-failed');
  }
  const result = await observeControl(observation({
    fetchStatus: async () => statusZip(report({ attempted: failedAttempt() })) }));
  assert.equal(result.status, 'hash-conflict');
});

test('control recovery rejects missing history, mismatched identity, nonterminal or pending attempts before GitHub access', async () => {
  for (const control of [{}, { lastFailure: failedAttempt({ version: 'control-9' }) },
    { lastFailure: failedAttempt({ transactionId: hash('other') }) },
    { lastFailure: failedAttempt({ phase: 'applying' }) },
    { lastFailure: failedAttempt({ failure: null }) },
    { lastFailure: failedAttempt(), pendingVersion: 'control-10' },
    { lastFailure: failedAttempt({ version: 'control-9' }), attempted: failedAttempt() }]) {
    let calls = 0;
    await assert.rejects(recoverControl({ origin, pin, version: 'control-10', failedVersion: 'control-8', failedTransaction,
      contract, fetchStatus: async () => statusZip(report(control)),
      gh: () => { calls++; assert.fail('invalid recovery must not contact GitHub'); } }), /CONTROL_NOT_HEALTHY/);
    assert.equal(calls, 0);
  }
});

test('control recovery cannot reset or replay failed version and requires exact CLI failure flags', async () => {
  for (const version of ['control-7', 'control-8'])
    await assert.rejects(recoverControl({ version, failedVersion: 'control-8', failedTransaction }), /VERSION_NOT_NEWER/);
  for (const argv of [['recover-control'], ['recover-control', '--failed-version', 'control-8'],
    ['recover-control', '--failed-transaction', failedTransaction]]) {
    const result = await runCli(argv, observation());
    assert.equal(JSON.parse(result.output).error, 'INVALID_ARGUMENT');
  }
});

test('control recovery publishes only reviewed newer bundle through normal channel and always waits', async t => {
  for (const historyKey of ['lastFailure', 'attempted']) {
    const f = await fixture(t), history = failedAttempt({ version: 'control-7' });
    let observing = false;
    f.options.fetchStatus = async () => {
      observing = f.remote.puts > 0;
      return statusZip(observing ? healthy() : report({ [historyKey]: history }));
    };
    const result = await runCli(['recover-control', '--failed-version', 'control-7', '--failed-transaction',
      failedTransaction, '--timeout-ms', '1000', '--poll-ms', '1'], { ...f.options, wait: false });
    const value = JSON.parse(result.output);
    assert.equal(result.exitCode, 0); assert.equal(value.status, 'healthy'); assert.equal(observing, true);
    assert.equal(value.action, 'publish-reviewed-newer-control'); assert.equal(value.failedVersion, 'control-7');
    assert.equal(value.failedTransaction, failedTransaction); assert.equal(f.remote.puts, 1);
    assert.equal(f.remote.descriptor.version, 'control-8');
    assert.ok(f.remote.calls.every(args => !args.includes('DELETE') && !args.includes('--clobber')));
  }
});

test('control recovery rechecks failure identity before mutation; stale authorization stops publication', async t => {
  const f = await fixture(t); let reads = 0;
  f.options.fetchStatus = async () => statusZip(report({ lastFailure: failedAttempt({ version: 'control-7',
    transactionId: ++reads <= 2 ? failedTransaction : hash('new failure') }) }));
  await assert.rejects(recoverControl({ ...f.options, failedVersion: 'control-7', failedTransaction }), /CONTROL_NOT_HEALTHY/);
  assert.equal(f.remote.uploads + f.remote.puts + f.remote.creates, 0);
});

function signedEnvelope(status = report(), keys = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })) {
  const der = (tag, ...parts) => {
    const body = Buffer.concat(parts), length = body.length;
    const encoded = length < 128 ? [length] : length < 256 ? [0x81, length] : [0x82, length >> 8, length & 255];
    return Buffer.concat([Buffer.from([tag, ...encoded]), body]);
  };
  const { privateKey, publicKey } = keys;
  const algorithm = Buffer.from('300d06092a864886f70d01010b0500', 'hex');
  const name = der(0x30, der(0x31, der(0x30, Buffer.from('0603550403', 'hex'), der(0x0c, Buffer.from('offline fixture')))));
  const tbs = der(0x30, der(0x02, Buffer.from([1])), algorithm, name,
    der(0x30, der(0x17, Buffer.from('200101000000Z')), der(0x17, Buffer.from('490101000000Z'))), name,
    publicKey.export({ type: 'spki', format: 'der' }));
  const certificate = der(0x30, tbs, algorithm, der(0x03, Buffer.from([0]), crypto.sign('sha256', tbs, privateKey)));
  const diagnostics = { schemaVersion: 1, observedAt: status.observedAt, expiresAt: status.expiresAt,
    controlVersion: status.control.version, bootstrap: { nativeAgentSha256: null, publisherWrapperSha256: null, nativeRecoveryVersion: 0 },
    watcher: { stage: null, reason: null, version: null, sourceCommit: null, manifestSha256: null, archiveSha256: null }, native: null, recovery: null };
  const payload = Buffer.from(JSON.stringify({ schemaVersion: 1, status, diagnostics }));
  const signature = crypto.sign('sha256', Buffer.concat([Buffer.from('AETHERFRONT CONTROL READBACK v1\n'), payload]), privateKey);
  return { pin: hash(certificate), bytes: Buffer.from(JSON.stringify({ schemaVersion: 1, payload: payload.toString('base64'),
    certificate: certificate.toString('base64'), signature: signature.toString('base64') })) };
}

test('stale CDN then transient authenticated API failure is retried by observer with the same host pin', async t => {
  const keys=crypto.generateKeyPairSync('rsa',{modulusLength:2048}),now=Date.now();
  const value=healthy();value.observedAt=new Date(now-100).toISOString();value.expiresAt=new Date(now+29000).toISOString();
  const fresh=signedEnvelope(value,keys);
  const stale=signedEnvelope({...value,observedAt:new Date(now-60000).toISOString(),expiresAt:new Date(now-30000).toISOString()},keys);
  assert.equal(stale.pin,fresh.pin);
  t.mock.method(https,'request',(_url,_options,callback)=>{
    const req=new EventEmitter();req.destroy=()=>{};req.setTimeout=()=>req;
    req.end=()=>queueMicrotask(()=>{const response=new PassThrough();response.statusCode=200;response.headers={};callback(response);response.end(stale.bytes);});
    return req;
  });
  let reads=0;
  const result=await observeControl(observation({pin:fresh.pin,transport:'github',fetchStatus:undefined,timeoutMs:2000,
    gh:async()=>{
      if(++reads===1)throw new ControllerError('GITHUB_FAILED');
      return JSON.stringify({type:'file',path:'readback.json',encoding:'base64',size:fresh.bytes.length,
        sha:crypto.createHash('sha1').update(`blob ${fresh.bytes.length}\0`).update(fresh.bytes).digest('hex'),content:fresh.bytes.toString('base64')});
    }}));
  assert.equal(result.ok,true);assert.equal(reads,2);
});

test('real signed fallback verifies certificate pin, signature, freshness and both resource ZIPs offline', async () => {
  const status = report(); status.expiresAt = new Date(Date.parse(status.observedAt) + 30_000).toISOString();
  const envelope = signedEnvelope(status);
  const options = { origin, pin: envelope.pin, contract, request: failedRequest(), fetchSignedReadback: async () => envelope.bytes,
    gh: () => assert.fail('signed reads must not call gh') };
  assert.deepEqual(await readStatus(options), status);
  assert.equal((await readDiagnostics(options)).controlVersion, status.control.version);
  await assert.rejects(readStatus({ ...options, pin: hash('wrong certificate') }), /TLS_PIN_MISMATCH/);
  const staleStatus = { ...status, observedAt: new Date(Date.now() - 60_000).toISOString(), expiresAt: new Date(Date.now() - 30_000).toISOString() };
  const stale = signedEnvelope(staleStatus);
  await assert.rejects(readStatus({ ...options, pin: stale.pin, fetchSignedReadback: async () => stale.bytes }), /STALE_STATUS/);
  const tampered = JSON.parse(envelope.bytes); tampered.signature = Buffer.alloc(256).toString('base64');
  await assert.rejects(readStatus({ ...options, fetchSignedReadback: async () => Buffer.from(JSON.stringify(tampered)) }), /INVALID_STATUS/);
});
