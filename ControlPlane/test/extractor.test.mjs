import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';
import { CAPABILITIES, REQUIRED_CONTROL_FILES, MAX_CONTROL_BYTES, allowedControlPath } from '../contract.mjs';

const script = fileURLToPath(new URL('../Expand-ControlBundle.ps1', import.meta.url));
const localPwsh = path.resolve(path.dirname(script), '../../tools/powershell7/pwsh.exe');
const pwsh = process.env.PWSH || process.env.POWERSHELL_EXE || (existsSync(localPwsh) ? localPwsh : 'pwsh');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
  return (~crc) >>> 0;
}
// Tiny fixture writer: central/local metadata can be mutated independently.
function zip(rows) {
  const local = [], central = [];
  let offset = 0;
  for (const row of rows) {
    const name = Buffer.from(row.name), localName = Buffer.from(row.localName ?? row.name);
    const bytes = Buffer.from(row.bytes ?? ''), packed = row.deflate ? deflateRawSync(bytes) : bytes;
    const extra = row.extra ?? Buffer.alloc(0), checksum = row.crc ?? crc32(bytes);
    const header = Buffer.alloc(30);
    header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4); header.writeUInt16LE(row.flags ?? 0x800, 6);
    header.writeUInt16LE(row.deflate ? 8 : 0, 8); header.writeUInt32LE(checksum, 14);
    header.writeUInt32LE(packed.length, 18); header.writeUInt32LE(row.declaredSize ?? bytes.length, 22);
    header.writeUInt16LE(localName.length, 26); header.writeUInt16LE(extra.length, 28);
    const index = Buffer.alloc(46);
    index.writeUInt32LE(0x02014b50); index.writeUInt16LE(0x314, 4); index.writeUInt16LE(20, 6);
    index.writeUInt16LE(row.flags ?? 0x800, 8); index.writeUInt16LE(row.deflate ? 8 : 0, 10);
    index.writeUInt32LE(checksum, 16); index.writeUInt32LE(packed.length, 20);
    index.writeUInt32LE(row.declaredSize ?? bytes.length, 24); index.writeUInt16LE(name.length, 28);
    index.writeUInt16LE(extra.length, 30); index.writeUInt32LE((row.attributes ?? 0x81a40000) >>> 0, 38);
    index.writeUInt32LE(offset, 42);
    local.push(header, localName, extra, packed); central.push(index, name, extra);
    offset += header.length + localName.length + extra.length + packed.length;
  }
  const directory = Buffer.concat(central), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50); end.writeUInt16LE(rows.length, 8); end.writeUInt16LE(rows.length, 10);
  end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, directory, end]);
}
function fixture({ editManifest, editRows, rawManifest, additional = [] } = {}) {
  const rows = [...REQUIRED_CONTROL_FILES, ...additional].map(name => ({ name, bytes: Buffer.from(`fixture ${name}\n`), deflate: true }));
  const manifest = {
    schemaVersion: 1, transactionVersion: 1, version: 'control-1',
    runtime: { nodeMajor: 24, powerShellMajor: 7, dotnetMajor: 8 }, capabilities: [...CAPABILITIES],
    files: rows.map(row => ({ path: row.name, sha256: digest(row.bytes), sizeBytes: row.bytes.length })),
  };
  editManifest?.(manifest);
  rows.push({ name: 'CONTROL.json', bytes: Buffer.from(rawManifest ? rawManifest(manifest) : JSON.stringify(manifest)), deflate: true });
  editRows?.(rows);
  return { archive: zip(rows), manifest, rows };
}
async function sandbox(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'control-extractor-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  return root;
}
async function run(root, bundle, overrides = {}) {
  const archive = path.join(root, 'input.zip'), destination = path.join(root, 'destination');
  await fs.writeFile(archive, bundle.archive);
  const options = { Archive: archive, Destination: destination, ExpectedVersion: 'control-1', ExpectedSha256: digest(bundle.archive), ExpectedSize: String(bundle.archive.length), ...overrides };
  const args = ['-NoProfile', '-NonInteractive', '-File', script, ...Object.entries(options).flatMap(([key, value]) => [`-${key}`, String(value)])];
  const result = spawnSync(pwsh, args, { encoding: 'utf8', timeout: 30000, windowsHide: true });
  assert.ifError(result.error);
  return { ...result, destination };
}
function rejected(result) {
  assert.equal(result.status, 1, result.stderr);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr.trim(), 'Control bundle rejected.');
}
async function noDestination(result) { assert.equal(existsSync(result.destination), false); }

test('real PowerShell extracts the complete verified inventory; executes nothing', async t => {
  const root = await sandbox(t);
  const bundle = fixture({ editRows(rows) {
    // This payload would throw if the extractor attempted to import or execute it.
    const row = rows.find(row => row.name === 'ControlPlane/worker.mjs');
    row.bytes = Buffer.from('throw new Error("PAYLOAD EXECUTED");');
    const manifest = JSON.parse(rows.at(-1).bytes);
    const file = manifest.files.find(file => file.path === row.name);
    file.sha256 = digest(row.bytes); file.sizeBytes = row.bytes.length;
    rows.at(-1).bytes = Buffer.from(JSON.stringify(manifest));
  } });
  const result = await run(root, bundle);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'verified control-1'); assert.equal(result.stderr, '');
  for (const row of bundle.rows) assert.deepEqual(await fs.readFile(path.join(result.destination, row.name)), row.bytes);
  assert.equal((await fs.readdir(root)).some(name => name.startsWith('.control-extract-')), false);
});

test('existing empty destination is accepted; nonempty sentinel is never overwritten', async t => {
  const root = await sandbox(t), destination = path.join(root, 'destination');
  await fs.mkdir(destination);
  assert.equal((await run(root, fixture())).status, 0);
  const sentinel = path.join(destination, 'sentinel'); await fs.writeFile(sentinel, 'keep');
  rejected(await run(root, fixture())); assert.equal(await fs.readFile(sentinel, 'utf8'), 'keep');
});

const expectations = [
  ['bad archive hash', { ExpectedSha256: '0'.repeat(64) }],
  ['bad archive size', { ExpectedSize: '1' }],
  ['oversized expected archive', { ExpectedSize: String(MAX_CONTROL_BYTES + 1) }],
  ['invalid expected version', { ExpectedVersion: 'control-0' }],
  ['different expected version', { ExpectedVersion: 'control-2' }],
  ['string hash injection', { ExpectedSha256: 'x\n' }],
  ['network destination', { Destination: '\\\\invalid-host\\share\\destination' }],
];
for (const [name, options] of expectations) test(name, async t => {
  const root = await sandbox(t); const result = await run(root, fixture(), options); rejected(result);
  assert.equal(existsSync(path.join(root, 'destination')), false);
});

const schemas = [
  ['schema version', m => { m.schemaVersion = 2; }],
  ['schema string type', m => { m.schemaVersion = '1'; }],
  ['transaction version', m => { m.transactionVersion = 2; }],
  ['extra field', m => { m.extra = true; }],
  ['missing field', m => { delete m.version; }],
  ['runtime extra field', m => { m.runtime.extra = 1; }],
  ['runtime wrong type', m => { m.runtime.nodeMajor = '24'; }],
  ['runtime wrong major', m => { m.runtime.dotnetMajor = 9; }],
  ['capability missing', m => { m.capabilities = m.capabilities.filter(name => name !== 'application-v1'); }],
  ['capability duplicate', m => { m.capabilities[1] = m.capabilities[0]; }],
  ['files not array', m => { m.files = {}; }],
  ['file extra field', m => { m.files[0].extra = 1; }],
  ['file size string', m => { m.files[0].sizeBytes = String(m.files[0].sizeBytes); }],
  ['file size fraction', m => { m.files[0].sizeBytes = 0.5; }],
  ['file size negative', m => { m.files[0].sizeBytes = -1; }],
  ['file digest mismatch', m => { m.files.at(-1).sha256 = '0'.repeat(64); }],
  ['file size mismatch', m => { m.files.at(-1).sizeBytes++; }],
  ['file duplicate declaration', m => { m.files[1] = { ...m.files[0] }; }],
  ['file digest type', m => { m.files[0].sha256 = 1; }],
];
for (const [name, editManifest] of schemas) test(`strict JSON: ${name}`, async t => {
  const result = await run(await sandbox(t), fixture({ editManifest })); rejected(result); await noDestination(result);
});
for (const [name, rawManifest] of [
  ['root duplicate', m => JSON.stringify(m).replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1')],
  ['escaped duplicate', m => JSON.stringify(m).replace('"schemaVersion":1', '"schemaVersion":1,"schema\\u0056ersion":1')],
  ['nested duplicate', m => JSON.stringify(m).replace('"nodeMajor":24', '"nodeMajor":24,"nodeMajor":24')],
  ['comment', m => `/* comment */${JSON.stringify(m)}`],
  ['trailing comma', m => JSON.stringify(m).replace('"schemaVersion":1', '"schemaVersion":1,')],
]) test(`strict JSON: ${name}`, async t => {
  const result = await run(await sandbox(t), fixture({ rawManifest })); rejected(result); await noDestination(result);
});

const paths = ['../outside', '/outside', 'APFDiscordBot/src/../outside.js', 'APFDiscordBot\\src\\index.js',
  'APFDiscordBot/src/con.js', 'APFDiscordBot/src/lpt1.js', 'APFDiscordBot/config.json', 'secrets/private.pem',
  'KESAS-VPS-Server/Server.runtimeconfig.json', 'ControlPlane/Expand-ControlBundle.ps1', 'APFDiscordBot/src/index.js:stream',
  'APFDiscordBot/src/index.js\n', 'APFDiscordBot/src/'];
for (const name of paths) test(`path rejected: ${JSON.stringify(name)}`, async t => {
  const result = await run(await sandbox(t), fixture({ editRows: rows => rows.push({ name, bytes: Buffer.from('bad') }) }));
  rejected(result); await noDestination(result);
});

const corruptions = [
  ['duplicate entry', rows => rows.push({ ...rows[0] })],
  ['case collision', rows => rows.push({ ...rows[0], name: rows[0].name.toUpperCase() })],
  ['undeclared allowed file', rows => rows.push({ name: 'APFDiscordBot/src/unlisted.js', bytes: Buffer.from('bad') })],
  ['missing required file', rows => { rows.splice(0, 1); }],
  ['missing required declaration and entry', rows => {
    rows.splice(0, 1); const manifest = JSON.parse(rows.at(-1).bytes); manifest.files.shift(); rows.at(-1).bytes = Buffer.from(JSON.stringify(manifest));
  }],
  ['missing manifest', rows => { rows.pop(); }],
  ['symlink attrs', rows => { rows[0].attributes = 0xa1ff0000; }],
  ['directory attrs', rows => { rows[0].attributes = 0x41ed0010; }],
  ['reparse attrs', rows => { rows[0].attributes = 0x81a40400; }],
  ['Unix hardlink metadata', rows => { rows[0].extra = Buffer.from([0x0d, 0, 0, 0]); }],
  ['ASi link metadata', rows => { rows[0].extra = Buffer.from([0x6e, 0x75, 0, 0]); }],
  ['local filename differs', rows => { rows[0].localName = '../outside'; }],
  ['CRC differs', rows => { rows[0].crc = 0; }],
  ['encrypted flags', rows => { rows[0].flags = 0x801; }],
  ['expanded size limit', rows => { rows[0].declaredSize = MAX_CONTROL_BYTES + 1; }],
  ['entry limit', rows => { for (let i = 0; i < 202; i++) rows.push({ name: `APFDiscordBot/src/extra-${i}.js`, bytes: Buffer.alloc(0) }); }],
];
for (const [name, editRows] of corruptions) test(`whole archive: ${name}`, async t => {
  const result = await run(await sandbox(t), fixture({ editRows })); rejected(result); await noDestination(result);
});

test('late invalid entry preserves an existing empty destination without partial output', async t => {
  const root = await sandbox(t), destination = path.join(root, 'destination'); await fs.mkdir(destination);
  const result = await run(root, fixture({ editManifest: m => { m.files.at(-1).sha256 = '0'.repeat(64); } }));
  rejected(result); assert.deepEqual(await fs.readdir(destination), []);
  await fs.writeFile(path.join(destination, 'sentinel'), 'untouched');
  rejected(await run(root, fixture({ editRows: rows => rows.push({ name: '../sentinel', bytes: Buffer.from('changed') }) })));
  assert.equal(await fs.readFile(path.join(destination, 'sentinel'), 'utf8'), 'untouched');
  assert.deepEqual((await fs.readdir(root)).sort(), ['destination', 'input.zip']);
});

test('reparse destination and ancestors are rejected without touching their targets', async t => {
  const root = await sandbox(t), outside = path.join(root, 'outside'), link = path.join(root, 'link');
  await fs.mkdir(outside); await fs.writeFile(path.join(outside, 'sentinel'), 'keep');
  await fs.symlink(outside, link, process.platform === 'win32' ? 'junction' : 'dir');
  rejected(await run(root, fixture(), { Destination: link }));
  rejected(await run(root, fixture(), { Destination: path.join(link, 'new') }));
  assert.equal(await fs.readFile(path.join(outside, 'sentinel'), 'utf8'), 'keep');
  assert.equal(existsSync(path.join(outside, 'new')), false);
});

test('allowlist accepts every contract family, but never bootstrap scripts or runtime state', async t => {
  const additional = ['APFDiscordBot/src/extra-module.js', 'APFDiscordBot/scripts/Stop-KesasGracefully.ps1', 'APFDiscordBot/scripts/Stop-APFLmpGracefully.ps1'];
  assert.ok([...REQUIRED_CONTROL_FILES, ...additional].every(allowedControlPath));
  const result = await run(await sandbox(t), fixture({ additional })); assert.equal(result.status, 0, result.stderr);
});
