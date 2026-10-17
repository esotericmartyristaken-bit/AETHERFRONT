import fs from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { formatBytes, nowIso, writeJsonAtomic } from './util.js';

async function walk(root) {
  const result = [];
  async function visit(current) {
    let entries = [];
    try { entries = await fs.readdir(current, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) await visit(full);
      else if (entry.isFile()) result.push(full);
    }
  }
  await visit(root);
  return result;
}

function relative(root, file) { return path.relative(root, file).replaceAll('\\', '/'); }
function comparePath(value) { return process.platform === 'win32' ? value.toLowerCase() : value; }
function safeChild(root, name) {
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(root, name);
  const rootKey = comparePath(resolvedRoot); const childKey = comparePath(resolved);
  if (childKey !== rootKey && !childKey.startsWith(`${rootKey}${path.sep}`)) throw new Error('path escapes configured root');
  return resolved;
}

function isWithin(parent, child) {
  const a = path.resolve(parent); const b = path.resolve(child);
  const parentKey = comparePath(a); const childKey = comparePath(b);
  return childKey === parentKey || childKey.startsWith(`${parentKey}${path.sep}`);
}

async function snapshotIndex(root) {
  const index = new Map();
  for (const file of await walk(root)) {
    if (path.basename(file).toLowerCase() === 'backup.json') continue;
    const stat = await fs.stat(file).catch(() => null);
    if (stat) index.set(relative(root, file), { size: stat.size, mtimeMs: stat.mtimeMs, mtime: stat.mtime.toISOString() });
  }
  return index;
}

async function sha256File(file) {
  return new Promise((resolve, reject) => {
    const hash = crypto.createHash('sha256'); const stream = createReadStream(file);
    stream.on('data', chunk => hash.update(chunk)); stream.on('end', () => resolve(hash.digest('hex'))); stream.on('error', reject);
  });
}

async function integrityIndex(root) {
  const index = await snapshotIndex(root);
  for (const [file, value] of index) value.sha256 = await sha256File(path.join(root, file.replaceAll('/', path.sep)));
  return index;
}

async function readTextLimited(file, maxBytes = 10 * 1024 * 1024) {
  const stat = await fs.stat(file).catch(() => null);
  if (!stat?.isFile()) throw new Error('file not found');
  if (stat.size > maxBytes) throw new Error('file exceeds inspection limit');
  return fs.readFile(file, 'utf8');
}

function firstKey(text, key) {
  return String(text).match(new RegExp(`^${key}\\s*=\\s*(.*)$`, 'im'))?.[1]?.trim() || null;
}

export class UniverseManager {
  constructor(config) {
    this.config = config;
    this.backupPromise = null;
    if (isWithin(this.root, this.backupRoot)) throw new Error('backupRoot cannot be inside universeRoot');
    if (isWithin(this.root, this.exportRoot)) throw new Error('exportRoot cannot be inside universeRoot');
    if (isWithin(this.backupRoot, this.exportRoot) || isWithin(this.exportRoot, this.backupRoot)) throw new Error('backupRoot and exportRoot cannot overlap');
  }
  get root() { return path.resolve(this.config.universe.universeRoot); }
  get backupRoot() { return path.resolve(this.config.universe.backupRoot); }
  get exportRoot() { return path.resolve(this.config.universe.exportRoot || path.join(path.dirname(this.backupRoot), 'Exports', path.basename(this.backupRoot))); }

  async careerStats() {
    const readNumber = async (fileName, key) => {
      const file = safeChild(this.root, path.join('Scenarios', fileName));
      const text = await readTextLimited(file, 2 * 1024 * 1024).catch(() => null);
      if (text === null) return null;
      const raw = firstKey(text, key);
      if (raw === null) return null;
      const value = Number(raw);
      return Number.isFinite(value) ? value : null;
    };
    const [funds, science] = await Promise.all([
      readNumber('Funding.txt', 'funds'),
      readNumber('ResearchAndDevelopment.txt', 'sci')
    ]);
    return { funds, science };
  }

  async withMutationLock(operation) {
    const parent = path.dirname(this.root); const lockPath = safeChild(parent, `${path.basename(this.root)}.mutation.lock`);
    let lock;
    try { lock = await fs.open(lockPath, 'wx'); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      const stat = await fs.stat(lockPath).catch(() => null);
      if (stat?.isFile() && Date.now() - stat.mtimeMs > 6 * 60 * 60 * 1000) {
        await fs.rm(lockPath, { force: true });
        lock = await fs.open(lockPath, 'wx').catch(retry => { if (retry.code === 'EEXIST') return null; throw retry; });
      }
      if (!lock) throw new Error('universe mutation already in progress');
    }
    try { await lock.writeFile(JSON.stringify({ pid: process.pid, startedAt: nowIso() })); }
    catch (error) { await lock.close().catch(() => {}); await fs.rm(lockPath, { force: true }).catch(() => {}); throw error; }
    await lock.close();
    try { return await operation(); } finally { await fs.rm(lockPath, { force: true }).catch(() => {}); }
  }

  async stats() {
    const files = await walk(this.root);
    let bytes = 0; const extensions = new Map(); const buckets = new Map(); let newest = null;
    for (const file of files) {
      const stat = await fs.stat(file).catch(() => null); if (!stat) continue;
      bytes += stat.size;
      const ext = path.extname(file).toLowerCase() || '[none]'; extensions.set(ext, (extensions.get(ext) || 0) + 1);
      const bucket = relative(this.root, file).split('/')[0] || '[root]'; buckets.set(bucket, (buckets.get(bucket) || 0) + 1);
      if (!newest || stat.mtimeMs > newest.mtimeMs) newest = { path: relative(this.root, file), mtime: stat.mtime.toISOString() , mtimeMs: stat.mtimeMs };
    }
    return { root: this.root, fileCount: files.length, bytes, size: formatBytes(bytes), extensions: Object.fromEntries(extensions), buckets: Object.fromEntries(buckets), newest };
  }

  async recentChanges(limit = 20) {
    const files = await walk(this.root); const rows = [];
    for (const file of files) { const stat = await fs.stat(file).catch(() => null); if (stat) rows.push({ path: relative(this.root, file), size: stat.size, mtime: stat.mtime.toISOString(), mtimeMs: stat.mtimeMs }); }
    return rows.sort((a, b) => b.mtimeMs - a.mtimeMs).slice(0, limit);
  }

  async search(query, limit = 50) {
    const needle = String(query || '').trim().toLowerCase(); if (!needle) return [];
    const rows = [];
    for (const file of await walk(this.root)) {
      const rel = relative(this.root, file);
      if (rel.toLowerCase().includes(needle)) rows.push({ path: rel, match: 'filename' });
      if (rows.length >= limit) break;
      if (!/\.(txt|cfg|craft|sfs|xml|json)$/i.test(file)) continue;
      const stat = await fs.stat(file).catch(() => null);
      if (!stat || stat.size > 8 * 1024 * 1024) continue;
      const text = await fs.readFile(file, 'utf8').catch(() => '');
      if (text.toLowerCase().includes(needle) && !rows.some(row => row.path === rel)) rows.push({ path: rel, match: 'content' });
      if (rows.length >= limit) break;
    }
    return rows;
  }

  async listAssets(kind, limit = 100) {
    const allowed = ['Crafts', 'Kerbals', 'Vessels', 'Scenarios', 'Contracts'];
    const canonical = allowed.find(value => value.toLowerCase() === String(kind || '').toLowerCase());
    if (!canonical) throw new Error('asset root is not allowed');
    const root = safeChild(this.root, canonical);
    const files = await walk(root); const rows = [];
    for (const file of files) { const stat = await fs.stat(file).catch(() => null); if (stat) rows.push({ path: relative(this.root, file), size: stat.size, mtime: stat.mtime.toISOString() }); }
    return rows.sort((a, b) => a.path.localeCompare(b.path)).slice(0, limit);
  }

  async listCraftDetails(limit = 100, owner = '') {
    const files = (await walk(safeChild(this.root, 'Crafts'))).filter(file => /\.craft$/i.test(file)); const rows = [];
    for (const file of files) {
      const text = await readTextLimited(file).catch(() => ''); const stat = await fs.stat(file).catch(() => null);
      rows.push({
        path: relative(this.root, file), name: firstKey(text, 'ship') || path.basename(file, '.craft'), owner: firstKey(text, 'owner'),
        vesselType: firstKey(text, 'vesselType'), craftType: firstKey(text, 'type'), version: firstKey(text, 'version'),
        parts: (text.match(/^\s*part\s*=/gim) || []).length, size: stat?.size || 0, mtime: stat?.mtime?.toISOString() || null
      });
    }
    const needle = String(owner || '').trim().toLowerCase();
    return rows.filter(row => !needle || String(row.owner || '').toLowerCase().includes(needle)).sort((a, b) => a.path.localeCompare(b.path)).slice(0, limit);
  }

  async craftInfo(relativePath) {
    const source = safeChild(this.root, String(relativePath || '').replaceAll('/', path.sep));
    const normalized = relative(this.root, source);
    if (normalized.split('/')[0]?.toLowerCase() !== 'crafts' || !/\.craft$/i.test(normalized)) throw new Error('craft inspection limited to Crafts .craft files');
    const text = await readTextLimited(source); const stat = await fs.stat(source);
    return { path: normalized, name: firstKey(text, 'ship') || path.basename(source, '.craft'), owner: firstKey(text, 'owner'), vesselType: firstKey(text, 'vesselType'), craftType: firstKey(text, 'type'), version: firstKey(text, 'version'), parts: (text.match(/^\s*part\s*=/gim) || []).length, size: stat.size, mtime: stat.mtime.toISOString() };
  }

  async listKerbalDetails(limit = 200, state = '') {
    const files = (await walk(safeChild(this.root, 'Kerbals'))).filter(file => /\.txt$/i.test(file)); const rows = [];
    for (const file of files) {
      const text = await readTextLimited(file, 5 * 1024 * 1024).catch(() => ''); const stat = await fs.stat(file).catch(() => null);
      rows.push({ path: relative(this.root, file), name: firstKey(text, 'name') || path.basename(file, '.txt'), state: firstKey(text, 'state'), trait: firstKey(text, 'trait'), type: firstKey(text, 'type'), gender: firstKey(text, 'gender'), inactive: firstKey(text, 'inactive'), size: stat?.size || 0, mtime: stat?.mtime?.toISOString() || null });
    }
    const needle = String(state || '').trim().toLowerCase();
    return rows.filter(row => !needle || String(row.state || '').toLowerCase().includes(needle)).sort((a, b) => a.name.localeCompare(b.name)).slice(0, limit);
  }

  async kerbalInfo(relativePath) {
    const source = safeChild(this.root, String(relativePath || '').replaceAll('/', path.sep));
    const normalized = relative(this.root, source);
    if (normalized.split('/')[0]?.toLowerCase() !== 'kerbals' || !/\.txt$/i.test(normalized)) throw new Error('Kerbal inspection limited to Kerbals .txt files');
    const text = await readTextLimited(source, 5 * 1024 * 1024); const stat = await fs.stat(source);
    return { path: normalized, name: firstKey(text, 'name') || path.basename(source, '.txt'), state: firstKey(text, 'state'), trait: firstKey(text, 'trait'), type: firstKey(text, 'type'), gender: firstKey(text, 'gender'), inactive: firstKey(text, 'inactive'), size: stat.size, mtime: stat.mtime.toISOString() };
  }

  async assetInfo(relativePath, options = {}) {
    const source = safeChild(this.root, String(relativePath || '').replaceAll('/', path.sep));
    const normalized = relative(this.root, source);
    const rootName = String(options.rootName || '').toLowerCase();
    if (rootName && normalized.split('/')[0]?.toLowerCase() !== rootName) throw new Error(`asset inspection limited to ${options.rootName}`);
    const extensions = (options.extensions || []).map(value => String(value).toLowerCase());
    if (extensions.length && !extensions.includes(path.extname(normalized).toLowerCase())) throw new Error('asset file type is not inspectable');
    const text = await readTextLimited(source, Number(options.maxBytes) || 10 * 1024 * 1024); const stat = await fs.stat(source);
    return {
      path: normalized, root: normalized.split('/')[0], name: firstKey(text, 'name') || firstKey(text, 'ship') || path.basename(source, path.extname(source)),
      type: firstKey(text, 'type'), vesselType: firstKey(text, 'vesselType'), state: firstKey(text, 'state'), trait: firstKey(text, 'trait'),
      parts: (text.match(/^\s*part\s*=/gim) || []).length, size: stat.size, mtime: stat.mtime.toISOString()
    };
  }

  async exportAsset(relativePath) {
    const source = safeChild(this.root, String(relativePath || '').replaceAll('/', path.sep));
    const relative = path.relative(this.root, source).replaceAll('\\', '/');
    const rootName = relative.split('/')[0]?.toLowerCase();
    if (!['crafts', 'kerbals', 'vessels', 'scenarios'].includes(rootName)) throw new Error('export limited to Crafts, Kerbals, Vessels, or Scenarios');
    if (!/\.(craft|sfs|txt|cfg|json|xml)$/i.test(relative)) throw new Error('file type not exportable');
    const stat = await fs.stat(source).catch(() => null);
    if (!stat?.isFile()) throw new Error('universe file not found');
    if (stat.size > 25 * 1024 * 1024) throw new Error('file exceeds 25 MB export limit');
    const destination = safeChild(this.exportRoot, relative);
    if (path.resolve(destination) === path.resolve(source)) throw new Error('export destination overlaps live universe');
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.copyFile(source, destination);
    return { relative, source, destination, name: path.basename(destination), size: stat.size, exportedAt: nowIso() };
  }

  async listBackups() {
    const entries = await fs.readdir(this.backupRoot, { withFileTypes: true }).catch(() => []); const rows = [];
    for (const entry of entries.filter(item => item.isDirectory() && item.name.toLowerCase() !== 'exports' && !item.name.toLowerCase().endsWith('.partial'))) {
      const meta = await fs.readFile(path.join(this.backupRoot, entry.name, 'backup.json'), 'utf8').then(JSON.parse).catch(() => null);
      if (meta?.complete === true && Number.isFinite(Date.parse(meta.createdAt || ''))) rows.push(meta);
      else rows.push({ id: entry.name, createdAt: null, complete: false });
    }
    // Retention may remove an old directory between readdir() and reading its
    // manifest. Never let that transient, incomplete row sort ahead of valid
    // snapshots and trigger a false stale-backup alert.
    return rows.sort((a, b) => {
      const aTime = Date.parse(a.createdAt || ''); const bTime = Date.parse(b.createdAt || '');
      const aValid = Number.isFinite(aTime); const bValid = Number.isFinite(bTime);
      if (aValid !== bValid) return aValid ? -1 : 1;
      if (aValid && aTime !== bTime) return bTime - aTime;
      return String(b.id || '').localeCompare(String(a.id || ''));
    });
  }

  async backupInProgress(now = Date.now()) {
    const lockPath = safeChild(this.backupRoot, '.backup.lock');
    const stat = await fs.stat(lockPath).catch(() => null);
    // createBackup() treats locks older than six hours as abandoned.
    return Boolean(stat?.isFile() && now - stat.mtimeMs <= 6 * 60 * 60 * 1000);
  }

  async createBackup(reason = 'manual') {
    if (this.backupPromise) return this.backupPromise;
    this.backupPromise = this.createBackupUnlocked(reason);
    try { return await this.backupPromise; } finally { this.backupPromise = null; }
  }

  async createBackupUnlocked(reason = 'manual') {
    const source = this.root; const sourceStat = await fs.stat(source).catch(() => null); if (!sourceStat?.isDirectory()) throw new Error(`Universe not found: ${source}`);
    await fs.mkdir(this.backupRoot, { recursive: true });
    const lockPath = safeChild(this.backupRoot, '.backup.lock');
    let lock;
    try { lock = await fs.open(lockPath, 'wx'); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      const lockStat = await fs.stat(lockPath).catch(() => null);
      if (lockStat?.isFile() && Date.now() - lockStat.mtimeMs > 6 * 60 * 60 * 1000) {
        await fs.rm(lockPath, { force: true });
        lock = await fs.open(lockPath, 'wx').catch(retryError => { if (retryError.code === 'EEXIST') return null; throw retryError; });
      }
      if (!lock) return { ok: false, skipped: true, reason: 'backup-in-progress', complete: false };
    }
    try { await lock.writeFile(JSON.stringify({ pid: process.pid, startedAt: nowIso(), reason })); }
    catch (error) { await lock.close().catch(() => {}); await fs.rm(lockPath, { force: true }).catch(() => {}); throw error; }
    await lock.close();
    try {
    const before = await this.stats();
    const stamp = new Date().toISOString().replaceAll(':', '').replaceAll('.', '');
    const id = `${stamp}-${crypto.randomBytes(3).toString('hex')}`; const partial = safeChild(this.backupRoot, `${id}.partial`); const target = safeChild(this.backupRoot, id);
    await fs.rm(partial, { recursive: true, force: true });
    await fs.cp(source, partial, { recursive: true, errorOnExist: false, force: true });
    const snapshot = await this.stats();
    const copied = await integrityIndex(partial);
    const copiedBytes = [...copied.values()].reduce((sum, row) => sum + row.size, 0);
    const stable = before.fileCount === snapshot.fileCount && before.bytes === snapshot.bytes && copied.size === snapshot.fileCount && copiedBytes === snapshot.bytes;
    const metadata = {
      id, reason, createdAt: nowIso(), complete: true,
      consistency: stable ? 'best-effort-live-copy-stable' : 'best-effort-live-copy-changed-during-copy',
      source, fileCount: copied.size, bytes: copiedBytes, size: formatBytes(copiedBytes),
      integrity: { algorithm: 'sha256', files: Object.fromEntries([...copied].map(([file, value]) => [file, value.sha256])) }
    };
    await writeJsonAtomic(path.join(partial, 'backup.json'), metadata);
    await fs.rename(partial, target);
    await this.prune();
    return metadata;
    } finally { await fs.rm(lockPath, { force: true }).catch(() => {}); }
  }

  async verifyBackup(id) {
    const backup = safeChild(this.backupRoot, id);
    const meta = await fs.readFile(path.join(backup, 'backup.json'), 'utf8').then(JSON.parse).catch(() => null);
    if (!meta?.complete) return { ok: false, id, reason: 'backup missing or incomplete' };
    const index = meta.integrity?.algorithm === 'sha256' ? await integrityIndex(backup) : await snapshotIndex(backup);
    const bytes = [...index.values()].reduce((sum, row) => sum + row.size, 0);
    const expectedHashes = meta.integrity?.files && typeof meta.integrity.files === 'object' ? meta.integrity.files : null;
    const hashMismatches = expectedHashes ? [...index].filter(([file, value]) => expectedHashes[file] !== value.sha256).map(([file]) => file).concat(Object.keys(expectedHashes).filter(file => !index.has(file))) : [];
    return {
      ok: index.size === Number(meta.fileCount) && bytes === Number(meta.bytes) && hashMismatches.length === 0,
      id,
      expected: { fileCount: Number(meta.fileCount), bytes: Number(meta.bytes) },
      actual: { fileCount: index.size, bytes },
      hashVerified: Boolean(expectedHashes),
      hashMismatches: hashMismatches.slice(0, 20),
      consistency: meta.consistency || 'unknown',
      createdAt: meta.createdAt
    };
  }

  async diffBackups(fromId, toId, limit = 50) {
    if (String(fromId) === String(toId)) throw new Error('diff requires two different backup IDs');
    const from = safeChild(this.backupRoot, fromId); const to = safeChild(this.backupRoot, toId);
    const [fromMeta, toMeta] = await Promise.all([
      fs.readFile(path.join(from, 'backup.json'), 'utf8').then(JSON.parse).catch(() => null),
      fs.readFile(path.join(to, 'backup.json'), 'utf8').then(JSON.parse).catch(() => null)
    ]);
    if (!fromMeta?.complete || !toMeta?.complete) throw new Error('both backups must be complete');
    const hashCompare = fromMeta.integrity?.algorithm === 'sha256' && toMeta.integrity?.algorithm === 'sha256';
    const [left, right] = await Promise.all([hashCompare ? integrityIndex(from) : snapshotIndex(from), hashCompare ? integrityIndex(to) : snapshotIndex(to)]);
    const rows = [];
    for (const [file, value] of left) {
      const other = right.get(file);
      if (!other) rows.push({ type: 'removed', path: file, from: value });
      else if (hashCompare ? other.sha256 !== value.sha256 : (other.size !== value.size || other.mtimeMs !== value.mtimeMs)) rows.push({ type: 'changed', path: file, from: value, to: other });
    }
    for (const [file, value] of right) if (!left.has(file)) rows.push({ type: 'added', path: file, to: value });
    rows.sort((a, b) => a.path.localeCompare(b.path));
    return {
      from: { id: fromId, createdAt: fromMeta.createdAt },
      to: { id: toId, createdAt: toMeta.createdAt },
      counts: {
        added: rows.filter(row => row.type === 'added').length,
        removed: rows.filter(row => row.type === 'removed').length,
        changed: rows.filter(row => row.type === 'changed').length
      },
      rows: rows.slice(0, Math.max(1, Math.min(100, Number(limit) || 50)))
    };
  }

  async prune() {
    const max = Math.max(1, Number(this.config.universe.maxBackups || 30)); const rows = await this.listBackups();
    for (const row of rows.slice(max)) { if (row.id) await fs.rm(safeChild(this.backupRoot, row.id), { recursive: true, force: true }); }
  }

  async restorePlan(id) {
    const backup = safeChild(this.backupRoot, id); const meta = await fs.readFile(path.join(backup, 'backup.json'), 'utf8').then(JSON.parse).catch(() => null);
    if (!meta?.complete) throw new Error('backup missing or incomplete');
    return { id, backupPath: backup, target: this.root, fileCount: meta.fileCount, size: meta.size, createdAt: meta.createdAt, destructive: true, enabled: this.config.universe.allowRestore === true, requiresMaintenance: true, requiresOffline: true, confirmation: `RESTORE ${id}` };
  }

  async listRestorePoints() {
    const parent = path.dirname(this.root); const prefix = `${path.basename(this.root)}.pre-restore-`;
    const entries = await fs.readdir(parent, { withFileTypes: true }).catch(() => []); const rows = [];
    for (const entry of entries.filter(item => item.isDirectory() && item.name.startsWith(prefix))) {
      const point = safeChild(parent, entry.name); const stat = await fs.stat(point).catch(() => null);
      if (stat) rows.push({ id: entry.name, path: point, createdAt: stat.mtime.toISOString() });
    }
    return rows.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  }

  async recoverAsset(backupId, relativePath, options = {}) {
    return this.withMutationLock(async () => {
      if (this.config.universe.allowAssetRestore !== true) throw new Error('asset recovery disabled in config');
      if (options.maintenance !== true) throw new Error('maintenance mode required');
      if (options.serverOffline !== true) throw new Error('server must be offline for asset recovery');
      const backup = safeChild(this.backupRoot, backupId);
      const relativeInput = String(relativePath || '').replaceAll('/', path.sep);
      const source = safeChild(backup, relativeInput);
      const normalized = path.relative(backup, source).replaceAll('\\', '/');
      const rootName = String(options.rootName || '').toLowerCase();
      if (!rootName || normalized.split('/')[0]?.toLowerCase() !== rootName) throw new Error(`recovery limited to ${options.rootName || 'an approved asset root'}`);
      const extensions = (options.extensions || []).map(value => String(value).toLowerCase());
      if (extensions.length && !extensions.includes(path.extname(normalized).toLowerCase())) throw new Error('recovery file type is not allowed');
      const sourceStat = await fs.stat(source).catch(() => null);
      if (!sourceStat?.isFile()) throw new Error(`${options.label || 'asset'} not found in backup`);
      const maxBytes = Number(options.maxBytes) || 25 * 1024 * 1024;
      if (sourceStat.size > maxBytes) throw new Error(`${options.label || 'asset'} exceeds ${formatBytes(maxBytes)} limit`);
      const destination = safeChild(this.root, normalized);
      if (await fs.stat(destination).catch(() => null)) throw new Error(`destination ${options.label || 'asset'} exists; recovery never overwrites live files`);
      await fs.mkdir(path.dirname(destination), { recursive: true });
      const temporary = `${destination}.${process.pid}.${crypto.randomBytes(3).toString('hex')}.recovering`;
      try {
        await fs.copyFile(source, temporary);
        await fs.rename(temporary, destination);
      } catch (error) {
        await fs.rm(temporary, { force: true }).catch(() => {});
        throw error;
      }
      return { backupId, relative: normalized, source, destination, name: path.basename(destination), size: sourceStat.size, overwrite: false, recoveredAt: nowIso() };
    });
  }

  async recoverVessel(backupId, relativePath, options = {}) {
    return this.recoverAsset(backupId, relativePath, { ...options, rootName: 'vessels', label: 'vessel', extensions: ['.sfs', '.txt', '.cfg', '.json', '.xml'] });
  }

  async recoverScenario(backupId, relativePath, options = {}) {
    return this.recoverAsset(backupId, relativePath, { ...options, rootName: 'scenarios', label: 'scenario', extensions: ['.sfs', '.txt', '.cfg', '.json', '.xml'] });
  }

  async recoverCraft(backupId, relativePath, options = {}) {
    return this.recoverAsset(backupId, relativePath, { ...options, rootName: 'crafts', label: 'craft', extensions: ['.craft'], maxBytes: 25 * 1024 * 1024 });
  }

  async recoverKerbal(backupId, relativePath, options = {}) {
    return this.recoverAsset(backupId, relativePath, { ...options, rootName: 'kerbals', label: 'Kerbal', extensions: ['.txt'], maxBytes: 5 * 1024 * 1024 });
  }

  async restore(id, confirmation = '', options = {}) {
    return this.withMutationLock(async () => {
      if (this.config.universe.allowRestore !== true) throw new Error('restore disabled in config');
      if (confirmation !== `RESTORE ${id}`) throw new Error(`confirmation required: RESTORE ${id}`);
      if (options.maintenance !== true) throw new Error('maintenance mode required');
      if (options.serverOffline !== true) throw new Error('server must be offline for full universe restore');
      const plan = await this.restorePlan(id); const verified = await this.verifyBackup(id);
      if (!verified.ok) throw new Error(`backup verification failed: ${verified.hashMismatches?.slice(0, 3).join(', ') || 'metadata mismatch'}`);
      const source = safeChild(this.backupRoot, id); const parent = path.dirname(this.root); const base = path.basename(this.root); const token = `${Date.now()}-${process.pid}-${crypto.randomBytes(2).toString('hex')}`;
      const staging = safeChild(parent, `${base}.restore-${token}.staging`); const rollback = safeChild(parent, `${base}.pre-restore-${token}`);
      try {
        await fs.rm(staging, { recursive: true, force: true });
        await fs.cp(source, staging, { recursive: true, errorOnExist: false, force: true });
        await fs.rm(path.join(staging, 'backup.json'), { force: true });
        await fs.rename(this.root, rollback);
        try { await fs.rename(staging, this.root); }
        catch (error) { await fs.rename(rollback, this.root).catch(() => {}); throw error; }
        return { ...plan, verified: true, restoredAt: nowIso(), rollbackRoot: rollback, stagingRemoved: true };
      } catch (error) {
        await fs.rm(staging, { recursive: true, force: true }).catch(() => {});
        throw error;
      }
    });
  }

  async rollback(pointId, confirmation = '', options = {}) {
    return this.withMutationLock(async () => {
      if (this.config.universe.allowRestore !== true) throw new Error('restore disabled in config');
      const id = path.basename(String(pointId || ''));
      if (!id || id !== String(pointId || '') || !id.startsWith(`${path.basename(this.root)}.pre-restore-`)) throw new Error('invalid restore point');
      if (confirmation !== `ROLLBACK ${id}`) throw new Error(`confirmation required: ROLLBACK ${id}`);
      if (options.maintenance !== true) throw new Error('maintenance mode required');
      if (options.serverOffline !== true) throw new Error('server must be offline for rollback');
      const parent = path.dirname(this.root); const point = safeChild(parent, id); const pointStat = await fs.stat(point).catch(() => null);
      if (!pointStat?.isDirectory()) throw new Error('restore point not found');
      const token = `${Date.now()}-${process.pid}-${crypto.randomBytes(2).toString('hex')}`; const displaced = safeChild(parent, `${path.basename(this.root)}.rollback-current-${token}`);
      await fs.rename(this.root, displaced);
      try { await fs.rename(point, this.root); }
      catch (error) { await fs.rename(displaced, this.root).catch(() => {}); throw error; }
      return { pointId: id, restoredAt: nowIso(), displacedCurrent: displaced };
    });
  }
}
