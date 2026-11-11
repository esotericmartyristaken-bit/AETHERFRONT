import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { access, readFile, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { verifyRequest } from './LocalReleaseRequest.mjs';

const api = 'https://api.github.com/repos/esotericmartyristaken-bit/AETHERFRONT/releases';
export const draftId = 438071888;
const draftTag = 'apf-operator-inbox';
const assetHosts = new Set(['release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com']);
const envelopeLimit = 2048;
const archiveLimit = 2 * 1024 * 1024 * 1024 - 1;
const requestName = /^v3-request-([a-f0-9]{32})\.json$/;

async function readBounded(response, limit) {
  if (!response.ok || !response.body) throw new Error('GitHub inbox read failed');
  const buffers = [];
  let length = 0;
  for await (const bytes of Readable.fromWeb(response.body)) {
    length += bytes.length;
    if (length > limit) throw new Error('GitHub inbox response exceeds limit');
    buffers.push(bytes);
  }
  return Buffer.concat(buffers, length);
}

function assetValid(asset, maximum) {
  return asset.state === 'uploaded' && Number.isSafeInteger(asset.id) && asset.id > 0 &&
    Number.isSafeInteger(asset.size) && asset.size > 0 && asset.size <= maximum &&
    typeof asset.digest === 'string' && /^sha256:[a-f0-9]{64}$/.test(asset.digest);
}

function assetResponse(asset, token, signal, request) {
  return request(`${api}/assets/${asset.id}`, {
    signal, redirect: 'manual', headers: { Authorization: `Bearer ${token}`, Accept: 'application/octet-stream' }
  }).then(async response => {
    if (response.status === 200) return response;
    const location = response.headers.get('location');
    if (response.body) await response.body.cancel();
    if (response.status !== 302 || !location) throw new Error('Unexpected GitHub asset response');
    const url = new URL(location);
    if (url.protocol !== 'https:' || !assetHosts.has(url.hostname) || url.port || url.username || url.password) {
      throw new Error('Unexpected GitHub asset redirect');
    }
    // The signed storage URL authorizes this GET. GitHub credentials stay on api.github.com.
    return request(url, { signal, redirect: 'error' });
  });
}

function decodeEnvelope(bytes) {
  const value = JSON.parse(bytes.toString('utf8'));
  if (!value || typeof value !== 'object' || Array.isArray(value) ||
      typeof value.request !== 'string' || typeof value.signature !== 'string' ||
      !Buffer.from(JSON.stringify({ request: value.request, signature: value.signature })).equals(bytes)) {
    throw new Error('Invalid inbox envelope');
  }
  const request = Buffer.from(value.request, 'base64');
  const signature = Buffer.from(value.signature, 'base64');
  if (request.toString('base64') !== value.request || signature.toString('base64') !== value.signature) {
    throw new Error('Invalid inbox envelope encoding');
  }
  return { request, signature };
}

async function claimed(claims, id) {
  try {
    await access(join(claims, id + '.json'));
    return true;
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    return false;
  }
}

async function readDraft(token, request) {
  const headers = { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json' };
  const options = { headers, redirect: 'error', signal: AbortSignal.timeout(15000) };
  const release = JSON.parse(await readBounded(await request(`${api}/${draftId}`, options), 1024 * 1024));
  if (release.id !== draftId || release.draft !== true || release.tag_name !== draftTag ||
      !Array.isArray(release.assets) || release.assets.length > 1000) throw new Error('Draft inbox boundary differs');
  return release;
}

export async function readDraftRequest(token, settings, claims, request = fetch) {
  const release = await readDraft(token, request);
  const oldest = (settings.now ?? Date.now()) - 660000;
  const envelopes = release.assets.filter(asset => assetValid(asset, envelopeLimit) &&
    requestName.test(asset.name) && Date.parse(asset.created_at) >= oldest)
    .sort((left, right) => Date.parse(right.created_at) - Date.parse(left.created_at));
  for (const envelope of envelopes.slice(0, 10)) {
    const id = requestName.exec(envelope.name)[1];
    if (await claimed(claims, id)) continue;
    const response = await assetResponse(envelope, token, AbortSignal.timeout(10000), request);
    const bytes = await readBounded(response, envelopeLimit);
    if (bytes.length !== envelope.size || 'sha256:' + createHash('sha256').update(bytes).digest('hex') !== envelope.digest) {
      throw new Error('Request envelope differs from draft metadata');
    }
    let authorization, body;
    try {
      body = decodeEnvelope(bytes);
      authorization = verifyRequest(body.request, body.signature, settings);
    } catch {
      continue; // Includes expired requests and requests for a different host.
    }
    if (authorization.requestId !== id || authorization.credentialSha256 !== '') continue;
    const assets = release.assets.filter(asset => asset.name === authorization.archiveSha256 + '.zip');
    if (assets.length !== 1 || !assetValid(assets[0], archiveLimit) ||
        assets[0].digest !== 'sha256:' + authorization.archiveSha256) {
      throw new Error('Authorized archive missing or differs from draft metadata');
    }
    return { ...body, authorization, archive: assets[0] };
  }
  return null;
}

export async function publishReceipt(token, id, path, request = fetch) {
  if (!/^[a-f0-9]{32}$/.test(id)) throw new Error('Invalid receipt identifier');
  const bytes = await readFile(path);
  if (bytes.length > 4096) throw new Error('Receipt exceeds limit');
  const name = 'v3-receipt-' + id + '.json';
  const digest = 'sha256:' + createHash('sha256').update(bytes).digest('hex');
  const release = await readDraft(token, request);
  const matches = release.assets.filter(asset => asset.name === name);
  if (matches.length) {
    if (matches.length !== 1 || !assetValid(matches[0], 4096) ||
        matches[0].digest !== digest || matches[0].size !== bytes.length) {
      throw new Error('Existing receipt differs');
    }
    return;
  }
  const url = 'https://uploads.github.com/repos/esotericmartyristaken-bit/AETHERFRONT/releases/' +
    draftId + '/assets?name=' + name;
  const response = await request(url, {
    method: 'POST', redirect: 'error', signal: AbortSignal.timeout(15000),
    headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', Accept: 'application/vnd.github+json' },
    body: bytes
  });
  const uploaded = JSON.parse(await readBounded(response, 16384));
  if (uploaded.name !== name || !assetValid(uploaded, 4096) || uploaded.size !== bytes.length || uploaded.digest !== digest) {
    throw new Error('Receipt upload could not be verified');
  }
}

// Destination must be a new file in a protected directory. Archive bytes do
// not reach the publisher until the complete download matches authorization.
export async function fetchDraftArchive(token, selected, destination, request = fetch) {
  const response = await assetResponse(selected.archive, token, AbortSignal.timeout(180000), request);
  if (!response.ok || !response.body) throw new Error('GitHub release archive request failed');
  const expected = selected.archive.size;
  const hash = createHash('sha256');
  let length = 0;
  const check = new Transform({
    transform(bytes, _encoding, callback) {
      length += bytes.length;
      if (length > expected) return callback(new Error('GitHub release archive exceeded declared size'));
      hash.update(bytes);
      callback(null, bytes);
    }
  });
  const output = createWriteStream(destination, { flags: 'wx', mode: 0o600 });
  let created = false;
  output.once('open', () => { created = true; });
  try {
    await pipeline(Readable.fromWeb(response.body), check, output);
    if (length !== expected || hash.digest('hex') !== selected.authorization.archiveSha256) {
      throw new Error('Downloaded archive does not match signed authorization');
    }
  } catch (error) {
    if (created) await unlink(destination);
    throw error;
  }
}
