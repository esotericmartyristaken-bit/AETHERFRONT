import fs from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

export const execFileAsync = promisify(execFile);
const atomicWriteQueues = new Map();
const TRANSIENT_RENAME_ERRORS = new Set(['EACCES', 'EBUSY', 'EPERM']);

async function renameWithRetry(source, destination) {
  let lastError;
  for (let attempt = 0; attempt < 9; attempt += 1) {
    try {
      await fs.rename(source, destination);
      return;
    } catch (error) {
      lastError = error;
      if (!TRANSIENT_RENAME_ERRORS.has(error?.code) || attempt === 8) throw error;
      await new Promise(resolve => setTimeout(resolve, Math.min(25 * (2 ** attempt), 800)));
    }
  }
  throw lastError;
}

export async function readJson(filePath, fallback = null) {
  try { return JSON.parse(await fs.readFile(filePath, 'utf8')); }
  catch (error) { if (fallback !== null && (error.code === 'ENOENT' || error instanceof SyntaxError)) return fallback; throw error; }
}

export async function writeJsonAtomic(filePath, value) {
  const key = path.resolve(filePath);
  const previous = atomicWriteQueues.get(key) || Promise.resolve();
  const current = previous.catch(() => {}).then(async () => {
    await fs.mkdir(path.dirname(filePath), { recursive: true });
    const tmp = `${filePath}.${process.pid}.${Date.now()}-${Math.random().toString(16).slice(2)}.tmp`;
    try {
      await fs.writeFile(tmp, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
      // Windows can transiently lock the destination while Discord/status and
      // operator processes both update it. Retrying preserves atomic replace
      // semantics without deleting the last good state file first.
      await renameWithRetry(tmp, filePath);
    } finally { await fs.rm(tmp, { force: true }).catch(() => {}); }
  });
  atomicWriteQueues.set(key, current);
  try { return await current; }
  finally { if (atomicWriteQueues.get(key) === current) atomicWriteQueues.delete(key); }
}

export async function appendJsonLine(filePath, value) {
  await fs.mkdir(path.dirname(filePath), { recursive: true });
  await fs.appendFile(filePath, `${JSON.stringify(value)}\n`, 'utf8');
}

export async function tailText(filePath, maxLines = 120) {
  try {
    const text = await fs.readFile(filePath, 'utf8');
    return text.split(/\r?\n/).slice(-maxLines).join('\n');
  } catch { return ''; }
}

export function parseTag(xml, tag, fallback = '') {
  const match = String(xml || '').match(new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`, 'i'));
  return match ? match[1].trim() : fallback;
}

export function formatBytes(bytes) {
  if (!Number.isFinite(bytes)) return '—';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  for (const unit of units) { value /= 1024; if (value < 1024) return `${value.toFixed(value >= 10 ? 0 : 1)} ${unit}`; }
  return `${value.toFixed(1)} PB`;
}

export function formatDuration(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const seconds = Math.floor(ms / 1000);
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const rest = seconds % 60;
  return `${days ? `${days}d ` : ''}${hours ? `${hours}h ` : ''}${minutes ? `${minutes}m ` : ''}${rest}s`.trim();
}

export function clampText(value, max = 1900) {
  const text = String(value ?? '');
  return text.length <= max ? text : `${text.slice(0, max - 16)}…[truncated]`;
}

export function shellQuotePowerShell(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}

export async function powershellJson(script) {
  const { stdout } = await execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script], { windowsHide: true, maxBuffer: 2 * 1024 * 1024 });
  return JSON.parse(stdout || 'null');
}

export function nowIso() { return new Date().toISOString(); }
