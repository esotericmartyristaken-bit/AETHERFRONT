import { createPublicKey, verify } from 'node:crypto';
import { open } from 'node:fs/promises';
import { join } from 'node:path';

const context = Buffer.from('AETHERFRONT VPS LOCAL RELEASE APPROVAL v3\n', 'ascii');
const fields = ['schemaVersion', 'host', 'requestId', 'action', 'version', 'archiveSha256', 'sourceCommit', 'credentialSha256', 'issuedAt', 'expiresAt'];
const releasePattern = /^kesas-rc([1-9][0-9]{0,8})$/;
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;
const invalid = () => { throw new Error('Invalid local release approval'); };

function timestamp(value) {
  if (typeof value !== 'string' || !timestampPattern.test(value)) invalid();
  const millis = Date.parse(value);
  if (!Number.isFinite(millis) || new Date(millis).toISOString() !== value) invalid();
  return millis;
}

export function createRequest({ host, requestId, action = 'publish', version, archiveSha256, sourceCommit, credentialSha256 = '', issuedAt, expiresAt }) {
  const request = { schemaVersion: 3, host, requestId, action, version, archiveSha256, sourceCommit, credentialSha256, issuedAt, expiresAt };
  return Buffer.from(JSON.stringify(request), 'utf8');
}

export function verifyRequest(bytes, signature, { host, publicKey, activeVersion, now = Date.now() }) {
  if (!Buffer.isBuffer(bytes) || bytes.length === 0 || bytes.length > 1024 ||
      !Buffer.isBuffer(signature) || signature.length !== 64) invalid();
  const key = publicKey?.type === 'public' ? publicKey : createPublicKey(publicKey);
  if (key.asymmetricKeyType !== 'ed25519' ||
      !verify(null, Buffer.concat([context, bytes]), key, signature)) invalid();
  let request;
  try { request = JSON.parse(bytes.toString('utf8')); } catch { invalid(); }
  if (!request || typeof request !== 'object' || Array.isArray(request) ||
      Object.keys(request).length !== fields.length ||
      !fields.every(field => Object.hasOwn(request, field))) invalid();
  const canonical = Object.fromEntries(fields.map(field => [field, request[field]]));
  if (!Buffer.from(JSON.stringify(canonical), 'utf8').equals(bytes)) invalid();
  const next = typeof request.version === 'string' && releasePattern.exec(request.version);
  const current = typeof activeVersion === 'string' && releasePattern.exec(activeVersion);
  if (request.schemaVersion !== 3 || !['publish','verify'].includes(request.action) || typeof host !== 'string' ||
      !/^[a-z][a-z0-9-]{0,62}$/.test(host) || request.host !== host ||
      typeof request.requestId !== 'string' || !/^[a-f0-9]{32}$/.test(request.requestId) ||
      typeof request.archiveSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(request.archiveSha256) ||
      typeof request.sourceCommit !== 'string' || !/^[a-f0-9]{40}$/.test(request.sourceCommit) ||
      typeof request.credentialSha256 !== 'string' || (request.credentialSha256 !== '' && !/^[a-f0-9]{64}$/.test(request.credentialSha256)) ||
      !next || !current || Number(next[1]) < Number(current[1]) ||
      (request.action === 'publish' && Number(next[1]) === Number(current[1])) || !Number.isFinite(now)) invalid();
  const issued = timestamp(request.issuedAt);
  const expires = timestamp(request.expiresAt);
  if (issued > now + 5000 || expires <= now || expires <= issued || expires - issued > 600000) invalid();
  return Object.freeze(canonical);
}

export async function claimRequest(bytes, requestId, claimDirectory) {
  if (!/^[a-f0-9]{32}$/.test(requestId)) invalid();
  const handle = await open(join(claimDirectory, requestId + '.json'), 'wx', 0o600);
  try {
    await handle.writeFile(bytes);
    await handle.sync();
  } finally {
    await handle.close();
  }
}

