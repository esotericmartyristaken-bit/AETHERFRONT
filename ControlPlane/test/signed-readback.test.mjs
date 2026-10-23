import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { constants, createHash, generateKeyPairSync, sign, X509Certificate } from 'node:crypto';
import { OBSERVATION_BRANCH, OBSERVATION_PATH, SIGNING_CONTEXT, verifyReadbackEnvelope } from '../signed-readback.mjs';
import { statusZip, diagnosticsZip } from '../public-status.mjs';

const openssl = 'C:/Program Files/Git/usr/bin/openssl.exe';
const available = existsSync(openssl);
const now = Date.UTC(2026, 9, 23, 12);
let directory, certificate, privateKey, pin;
before(async () => {
  if (!available) return;
  directory = await mkdtemp(path.join(os.tmpdir(), 'apf-signed-readback-test-'));
  execFileSync(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-days', '1',
    '-subj', '/CN=isolated-readback-test', '-keyout', path.join(directory, 'key.pem'),
    '-out', path.join(directory, 'certificate.der'), '-outform', 'DER'],
  { stdio: 'ignore', timeout: 30_000, windowsHide: true });
  certificate = await readFile(path.join(directory, 'certificate.der'));
  privateKey = await readFile(path.join(directory, 'key.pem'));
  pin = createHash('sha256').update(new X509Certificate(certificate).raw).digest('hex');
});
after(async () => { if (directory) await rm(directory, { recursive: true, force: true }); });

function payload() {
  const observedAt = new Date(now - 1_000).toISOString(), expiresAt = new Date(now + 29_000).toISOString();
  return { schemaVersion: 1, status: {
    schemaVersion: 1, contractVersion: 1, bootstrapVersion: 1, observedAt, expiresAt, capabilities: [],
    control: { version: 'control-1', sequence: 1, sha256: 'a'.repeat(64), status: 'healthy', phase: 'idle',
      pendingVersion: null, lastSuccess: 'control-1', rollback: null, failure: null },
    application: { clientVersion: 'kesas-rc33', packVersion: 'kesas-rc33', status: 'idle', phase: 'idle',
      pendingVersion: null, health: 'healthy', lastSuccess: 'kesas-rc33', failure: null },
    components: Object.fromEntries(['watcher', 'nativeRunner', 'publisher', 'verifier', 'packService', 'serverRuntime',
      'serverPlugins', 'clientBootstrap', 'clientUpdater', 'clientHelper', 'catalog', 'publicMedia', 'dependencies',
      'updateTool', 'certificateTool', 'bootstrap'].map(name => [name, null])),
    runtime: { node: '24.19.0', powershell: '7.5.3', dotnet: '8.0.29' },
  }, diagnostics: {
    schemaVersion: 1, observedAt, expiresAt, controlVersion: 'control-1',
    bootstrap: { nativeAgentSha256: null, publisherWrapperSha256: null, nativeRecoveryVersion: 0 },
    watcher: { stage: null, reason: null, version: null, sourceCommit: null, manifestSha256: null, archiveSha256: null },
    native: null, recovery: null,
  } };
}
function envelope(value = payload(), key = privateKey, context = SIGNING_CONTEXT) {
  const bytes = Buffer.isBuffer(value) ? value : Buffer.from(JSON.stringify(value));
  return { schemaVersion: 1, payload: bytes.toString('base64'), certificate: certificate.toString('base64'),
    signature: sign('sha256', Buffer.concat([context, bytes]), { key, padding: constants.RSA_PKCS1_PADDING }).toString('base64') };
}
const encode = value => Buffer.from(JSON.stringify(value));
function invalid(bytes, code = 'SIGNED_READBACK_INVALID', options = {}) {
  assert.throws(() => verifyReadbackEnvelope(bytes, { pin, now, ...options }), error => {
    assert.equal(error.code, code); assert.equal(error.message, code);
    assert.equal(error.cause, undefined); assert.deepEqual(Object.keys(error), ['code']); return true;
  });
}
function check(name, run) { test(name, { skip: available ? false : 'Git OpenSSL unavailable' }, run); }

check('exports fixed observation identity and returns exact shared STORE ZIPs without new timestamps', () => {
  assert.equal(OBSERVATION_BRANCH, 'host-observation'); assert.equal(OBSERVATION_PATH, 'readback.json');
  assert.equal(SIGNING_CONTEXT.toString(), 'AETHERFRONT CONTROL READBACK v1\n');
  const value = payload(), bytes = encode(envelope(value));
  assert.deepEqual(verifyReadbackEnvelope(bytes, { pin, now }), statusZip(value.status));
  assert.deepEqual(verifyReadbackEnvelope(bytes, { pin, now, resource: 'diagnostics' }), diagnosticsZip(value.diagnostics));
  assert.deepEqual(verifyReadbackEnvelope(new Uint8Array(bytes), { pin: pin.toUpperCase(), now }), statusZip(value.status));
});
check('shared serializer validates schema 2 diagnostics, including unselected resource', () => {
  const value = payload(); value.diagnostics.schemaVersion = 2;
  value.diagnostics.host = { stage: 'reporting', reason: null, worker: 'running', broker: 'ready', inventory: 'complete',
    leaseActive: false, journalPhase: null, exitCode: null };
  assert.deepEqual(verifyReadbackEnvelope(encode(envelope(value)), { pin, now, resource: 'diagnostics' }), diagnosticsZip(value.diagnostics));
  value.diagnostics.host.worker = 'PRIVATE-INVALID'; invalid(encode(envelope(value)));
  const badStatus = payload(); badStatus.status.runtime.node = 'PRIVATE-INVALID';
  invalid(encode(envelope(badStatus)), 'SIGNED_READBACK_INVALID', { resource: 'diagnostics' });
});
check('rejects tampered payload, signatures, wrong signing context, forged key and PSS', () => {
  const changed = envelope(); changed.payload = Buffer.from(JSON.stringify(payload()).replace('kesas-rc33', 'kesas-rc34')).toString('base64');
  invalid(encode(changed));
  const corrupted = envelope(), signature = Buffer.from(corrupted.signature, 'base64'); signature[0] ^= 1;
  corrupted.signature = signature.toString('base64'); invalid(encode(corrupted));
  invalid(encode(envelope(payload(), privateKey, Buffer.alloc(0))));
  invalid(encode(envelope(payload(), generateKeyPairSync('rsa', { modulusLength: 2048 }).privateKey)));
  const pss = envelope(); pss.signature = sign('sha256', Buffer.concat([SIGNING_CONTEXT, Buffer.from(pss.payload, 'base64')]),
    { key: privateKey, padding: constants.RSA_PKCS1_PSS_PADDING }).toString('base64'); invalid(encode(pss));
});
check('rejects wrong or absent pins without CA trust fallback', () => {
  const bytes = encode(envelope());
  for (const wrong of ['0'.repeat(64), '', undefined, `${pin}\n`, pin.match(/../g).join(':')]) invalid(bytes, 'TLS_PIN_MISMATCH', { pin: wrong });
});
check('rejects stale, overlong, reversed and excessively future observations', () => {
  for (const [observed, expires] of [[now - 30_000, now], [now - 1_001, now + 29_000],
    [now + 5_001, now + 30_000], [now + 1_000, now + 1_000], [now + 2_000, now + 1_000]]) {
    const value = payload();
    for (const report of [value.status, value.diagnostics]) {
      report.observedAt = new Date(observed).toISOString(); report.expiresAt = new Date(expires).toISOString();
    }
    invalid(encode(envelope(value)), 'STALE_STATUS');
  }
  const boundary = payload();
  for (const report of [boundary.status, boundary.diagnostics]) {
    report.observedAt = new Date(now + 5_000).toISOString(); report.expiresAt = new Date(now + 35_000).toISOString();
  }
  assert.deepEqual(verifyReadbackEnvelope(encode(envelope(boundary)), { pin, now }), statusZip(boundary.status));
});
check('rejects status/diagnostics timestamp and control-version mismatches', () => {
  for (const [field, value] of [['observedAt', new Date(now).toISOString()], ['expiresAt', new Date(now + 20_000).toISOString()],
    ['controlVersion', 'control-2']]) { const data = payload(); data.diagnostics[field] = value; invalid(encode(envelope(data))); }
});
check('rejects extra/missing envelope and payload fields and unsupported versions', () => {
  const extra = envelope(); extra.private = 'PRIVATE-SENTINEL'; invalid(encode(extra));
  const missing = envelope(); delete missing.signature; invalid(encode(missing));
  const version = envelope(); version.schemaVersion = 2; invalid(encode(version));
  for (const change of [value => { value.private = 'PRIVATE-SENTINEL'; }, value => { delete value.diagnostics; },
    value => { value.schemaVersion = 2; }, value => { value.status.private = 'PRIVATE-SENTINEL'; }]) {
    const value = payload(); change(value); invalid(encode(envelope(value)));
  }
});
check('rejects duplicate and escaped duplicate keys at every JSON layer', () => {
  const normal = JSON.stringify(envelope());
  invalid(Buffer.from(normal.replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1')));
  invalid(Buffer.from(normal.replace('"schemaVersion":1', '"schemaVersion":1,"schema\\u0056ersion":1')));
  const raw = JSON.stringify(payload());
  invalid(encode(envelope(Buffer.from(raw.replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1')))));
  invalid(encode(envelope(Buffer.from(raw.replace('"node":"24.19.0"', '"node":"24.19.0","n\\u006fde":"24.19.0"')))));
});
check('rejects noncanonical base64, invalid UTF-8, BOM and malformed JSON', () => {
  for (const field of ['payload', 'certificate', 'signature']) {
    const value = envelope(); value[field] += '\n'; invalid(encode(value));
    const empty = envelope(); empty[field] = ''; invalid(encode(empty));
  }
  for (const noncanonical of ['Zg', 'Zh==', 'Zg===', 'Zg-_']) { const value = envelope(); value.payload = noncanonical; invalid(encode(value)); }
  invalid(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), encode(envelope())]));
  invalid(Buffer.concat([encode(envelope()).subarray(0, -1), Buffer.from([0xff]), Buffer.from('}') ]));
  invalid(encode(envelope(Buffer.from([0xff]))));
  invalid(encode(envelope(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(JSON.stringify(payload()))]))));
  invalid(Buffer.from('{')); invalid(Buffer.from('{} trailing')); invalid(Buffer.from('null'));
});
check('bounds envelope, decoded fields, nesting and tokens before expensive parsing', () => {
  invalid(Buffer.alloc(160 * 1024 + 1));
  for (const [field, size] of [['payload', 96 * 1024 + 1], ['certificate', 16 * 1024 + 1], ['signature', 1025]]) {
    const value = envelope(); value[field] = Buffer.alloc(size).toString('base64'); invalid(encode(value));
  }
  invalid(encode(envelope(Buffer.from('['.repeat(34) + '0' + ']'.repeat(34)))));
  invalid(encode(envelope(Buffer.from('[' + '0,'.repeat(16384) + '0]'))));
});
check('requires exact DER and allowlisted resources; sanitizes all errors', () => {
  const trailing = envelope(); trailing.certificate = Buffer.concat([certificate, Buffer.from('PRIVATE-SENTINEL')]).toString('base64');
  invalid(encode(trailing));
  const malformed = envelope(); malformed.certificate = Buffer.from('PRIVATE-SENTINEL').toString('base64'); invalid(encode(malformed));
  for (const resource of ['../status', 'host-status.json', 'STATUS', '', null, {}]) invalid(encode(envelope()), 'SIGNED_READBACK_INVALID', { resource });
  for (const time of [NaN, Infinity, -Infinity, '2026-10-23']) invalid(encode(envelope()), 'SIGNED_READBACK_INVALID', { now: time });
  invalid('PRIVATE-SENTINEL'); invalid(Buffer.from('PRIVATE-SENTINEL')); invalid(Buffer.alloc(0));
  assert.throws(() => verifyReadbackEnvelope(encode(envelope()), null), { code: 'SIGNED_READBACK_INVALID', message: 'SIGNED_READBACK_INVALID' });
});
