import { constants, createHash, timingSafeEqual, verify, X509Certificate } from 'node:crypto';
import { TextDecoder } from 'node:util';
import { validateHostReport } from './contract.mjs';
import { statusZip, diagnosticsZip } from './public-status.mjs';

export const OBSERVATION_BRANCH = 'host-observation';
export const OBSERVATION_PATH = 'readback.json';
export const SIGNING_CONTEXT = Buffer.from('AETHERFRONT CONTROL READBACK v1\n');
// Keep verification independent of mutation of the exported Buffer.
const context = Buffer.from(SIGNING_CONTEXT);
const MAX_ENVELOPE_BYTES = 128 * 1024;
const MAX_PAYLOAD_BYTES = 64 * 1024;
const MAX_CERTIFICATE_BYTES = 8 * 1024;
const MAX_SIGNATURE_BYTES = 1024;
const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

class ReadbackError extends Error {
  constructor(code) { super(code); this.code = code; }
}
function fail(code = 'SIGNED_READBACK_INVALID') { throw new ReadbackError(code); }
function exact(value, fields) {
  if (!value || typeof value !== 'object' || Array.isArray(value) ||
      Object.keys(value).length !== fields.length || !fields.every(key => Object.hasOwn(value, key))) fail();
}
function base64(value, limit) {
  if (typeof value !== 'string' || !value.length || value.length > 4 * Math.ceil(limit / 3) ||
      value.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) fail();
  const bytes = Buffer.from(value, 'base64');
  if (bytes.length > limit || bytes.toString('base64') !== value) fail();
  return bytes;
}

// JSON.parse alone silently accepts duplicate keys. Scan every object before
// parsing, including escaped names, with bounded recursion and token counts.
function strictJson(bytes) {
  const text = decoder.decode(bytes);
  let position = 0, tokens = 0;
  const whitespace = () => { while (/[\x20\t\r\n]/.test(text[position] || '\0')) position++; };
  function string() {
    const start = position++;
    while (position < text.length) {
      const character = text[position++];
      if (character === '"') return JSON.parse(text.slice(start, position));
      if (character === '\\') position++;
    }
    fail();
  }
  function value(depth) {
    if (depth > 32 || ++tokens > 16384) fail();
    whitespace();
    const character = text[position];
    if (character === '{' || character === '[') {
      position++;
      whitespace();
      const object = character === '{', end = object ? '}' : ']', keys = new Set();
      if (text[position] === end) { position++; return; }
      for (;;) {
        whitespace();
        if (object) {
          if (text[position] !== '"') fail();
          const key = string();
          if (keys.has(key)) fail();
          keys.add(key);
          whitespace();
          if (text[position++] !== ':') fail();
        }
        value(depth + 1);
        whitespace();
        if (text[position] === end) { position++; return; }
        if (text[position++] !== ',') fail();
      }
    }
    if (character === '"') { string(); return; }
    const start = position;
    while (position < text.length && !/[\x20\t\r\n,}\]]/.test(text[position])) position++;
    if (start === position) fail();
    JSON.parse(text.slice(start, position));
  }
  value(0);
  whitespace();
  if (position !== text.length) fail();
  return JSON.parse(text);
}

/** Verify one signed observation and return the existing public STORE ZIP. */
export function verifyReadbackEnvelope(bytes, options = {}) {
  try {
    const { pin, resource = 'status', now = Date.now() } = options;
    if (!['status', 'diagnostics'].includes(resource) || !Number.isFinite(now) ||
        !(bytes instanceof Uint8Array) || !bytes.byteLength || bytes.byteLength > MAX_ENVELOPE_BYTES) fail();
    const envelope = strictJson(bytes);
    exact(envelope, ['schemaVersion', 'payload', 'certificate', 'signature']);
    if (envelope.schemaVersion !== 1) fail();
    const payloadBytes = base64(envelope.payload, MAX_PAYLOAD_BYTES);
    const certificateBytes = base64(envelope.certificate, MAX_CERTIFICATE_BYTES);
    const signature = base64(envelope.signature, MAX_SIGNATURE_BYTES);
    const certificate = new X509Certificate(certificateBytes);
    // PEM, trailing bytes and concatenated certificates are not DER envelopes.
    if (!certificate.raw.equals(certificateBytes)) fail();
    if (typeof pin !== 'string' || !/^[a-fA-F0-9]{64}$/.test(pin)) fail('TLS_PIN_MISMATCH');
    if (!timingSafeEqual(createHash('sha256').update(certificate.raw).digest(), Buffer.from(pin, 'hex'))) fail('TLS_PIN_MISMATCH');
    const publicKey = certificate.publicKey;
    if (publicKey.asymmetricKeyType !== 'rsa' || (publicKey.asymmetricKeyDetails?.modulusLength??0)<2048 ||
        !verify('sha256', Buffer.concat([context, payloadBytes]),
          { key: publicKey, padding: constants.RSA_PKCS1_PADDING }, signature)) fail();
    const payload = strictJson(payloadBytes);
    exact(payload, ['schemaVersion', 'status', 'diagnostics']);
    if (payload.schemaVersion !== 1) fail();
    const status = validateHostReport(payload.status), diagnostics = payload.diagnostics;
    // The shared serializer owns diagnostics schema evolution and redaction.
    const diagnosticBytes = diagnosticsZip(diagnostics), statusBytes = statusZip(status);
    if (status.observedAt !== diagnostics.observedAt || status.expiresAt !== diagnostics.expiresAt ||
        status.control.version !== diagnostics.controlVersion) fail();
    const observed = Date.parse(status.observedAt), expires = Date.parse(status.expiresAt);
    if (!Number.isFinite(observed) || !Number.isFinite(expires) ||
        new Date(observed).toISOString() !== status.observedAt || new Date(expires).toISOString() !== status.expiresAt ||
        expires <= observed || expires - observed > 30_000 || expires <= now || observed > now + 5_000) fail('STALE_STATUS');
    return resource === 'status' ? statusBytes : diagnosticBytes;
  } catch (error) {
    if (error instanceof ReadbackError) throw error;
    // Never retain causes, parser input, certificate material or private fields.
    fail();
  }
}
