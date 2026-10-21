import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { digest, fileHash, publicMediaInventory } from '../public-status.mjs';

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-public-media-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const directory = path.join(root, 'Config');
  await fs.mkdir(directory);
  return { root, directory };
}

test('read-only public inventory accepts hardlinked ZIPs without relaxing control hashing', async t => {
  const { root, directory } = await fixture(t);
  const source = path.join(root, 'cache.zip'), archive = path.join(directory, 'kesas-pack.zip');
  const bytes = Buffer.from('public pack fixture');
  await fs.writeFile(source, bytes);
  await fs.link(source, archive);
  const result = await publicMediaInventory(directory);
  assert.equal(result.cache['kesas-pack.zip'].sha256, digest(bytes));
  assert.equal(result.sha256, digest(Buffer.from(JSON.stringify([{ name: 'kesas-pack.zip', sha256: digest(bytes) }]))));
  await assert.rejects(fileHash(archive), /Unsafe control path/);
  assert.deepEqual(await fs.readFile(source), bytes);
  assert.equal((await fs.stat(source)).nlink, 2);
});

test('cached hashes invalidate when an archive changes through another hardlink', async t => {
  const { root, directory } = await fixture(t);
  const source = path.join(root, 'cache.zip');
  await fs.writeFile(source, 'original');
  await fs.link(source, path.join(directory, 'kesas-pack.zip'));
  const first = await publicMediaInventory(directory);
  assert.deepEqual(await publicMediaInventory(directory, first.cache), first);
  await fs.writeFile(source, 'replacement archive bytes');
  const changed = await publicMediaInventory(directory, first.cache);
  assert.notEqual(changed.sha256, first.sha256);
  assert.equal(changed.cache['kesas-pack.zip'].sha256, digest(Buffer.from('replacement archive bytes')));
});

test('ephemeral host status and unrelated files never enter the media digest', async t => {
  const { directory } = await fixture(t);
  await fs.writeFile(path.join(directory, 'kesas-pack.zip'), 'pack');
  const first = await publicMediaInventory(directory);
  await fs.writeFile(path.join(directory, 'kesas-host-status.zip'), 'first status');
  await fs.writeFile(path.join(directory, 'private.zip'), 'not a public media name');
  await fs.writeFile(path.join(directory, 'kesas-staged.zip.incoming'), 'incomplete');
  assert.deepEqual(await publicMediaInventory(directory, first.cache), first);
  await fs.writeFile(path.join(directory, 'kesas-host-status.zip'), 'changed status');
  assert.deepEqual(await publicMediaInventory(directory, first.cache), first);
});

test('junctions or symlinks are rejected both at the directory and archive path', async t => {
  const { root, directory } = await fixture(t);
  const alias = path.join(root, 'alias');
  await fs.symlink(directory, alias, process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(publicMediaInventory(alias), /Unsafe control path/);
  await fs.symlink(root, path.join(directory, 'kesas-linked.zip'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(publicMediaInventory(directory), /Unsafe public archive/);
});

test('non-file archives and invalid cached digests fail closed or are rehashed', async t => {
  const { directory } = await fixture(t);
  await fs.writeFile(path.join(directory, 'kesas-pack.zip'), 'pack');
  const first = await publicMediaInventory(directory);
  first.cache['kesas-pack.zip'].sha256 = 'invalid';
  assert.equal((await publicMediaInventory(directory, first.cache)).cache['kesas-pack.zip'].sha256, digest(Buffer.from('pack')));
  await fs.mkdir(path.join(directory, 'kesas-directory.zip'));
  await assert.rejects(publicMediaInventory(directory), /Unsafe public archive/);
});
