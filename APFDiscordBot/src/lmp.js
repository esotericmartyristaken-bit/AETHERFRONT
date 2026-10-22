import { EventEmitter } from 'node:events';
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { formatBytes, parseTag, powershellJson, shellQuotePowerShell, tailText, writeJsonAtomic } from './util.js';
import { parseNetworkTimeoutLine } from './timeout-help.js';
import { publishThroughBroker } from './native-broker.js';
const execFileAsync = promisify(execFile);

async function latestFile(directory, extension = '.log') {
  try {
    const entries = await fs.readdir(directory, { withFileTypes: true });
    const files = [];
    for (const entry of entries) {
      if (!entry.isFile() || !entry.name.toLowerCase().endsWith(extension)) continue;
      const fullPath = path.join(directory, entry.name);
      const stat = await fs.stat(fullPath);
      files.push({ fullPath, stat });
    }
    return files.sort((a, b) => b.stat.mtimeMs - a.stat.mtimeMs)[0]?.fullPath || '';
  } catch { return ''; }
}

function lastMatch(text, regex, fallback = null) {
  const matches = [...String(text).matchAll(regex)];
  return matches.length ? matches[matches.length - 1][1] : fallback;
}

function dateFromValue(value) {
  const dotNet = String(value || '').match(/\/Date\((\d+)\)\//);
  if (dotNet) return new Date(Number(dotNet[1]));
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function shortLeaveReason(reason) {
  const value = String(reason || '').trim();
  if (!value) return 'disconnected';
  if (/^connection timed out$/i.test(value)) return 'connection timed out';
  if (/^quit$/i.test(value)) return 'quit';
  if (/\bkick(?:ed)?\b/i.test(value)) return 'kicked';
  if (/\bban(?:ned)?\b/i.test(value)) return 'banned';
  if (/version mismatch/i.test(value)) return 'version mismatch';
  if (/failed mod validation/i.test(value)) return 'mod validation failed';
  if (/server (?:is )?(?:stopping|shutting down|stopped)/i.test(value)) return 'server stopped';
  return value.replaceAll('@', '@ ').replaceAll('`', "'").replace(/\s+/g, ' ').slice(0, 80);
}

export function resolvePlayerCounts(online, logCount, logConnected, masterCount, snapshotCount = null) {
  if (!online) return { playerCount: 0, connectedCount: 0, source: 'offline' };
  const valid = value => value !== null && value !== undefined && value !== '' && Number.isInteger(Number(value)) && Number(value) >= 0 ? Number(value) : null;
  const logPlayers = valid(logCount);
  const logConnections = valid(logConnected);
  const masterPlayers = valid(masterCount);
  const snapshotPlayers = valid(snapshotCount);
  // Current-process logs change immediately on join/leave. Master rows can lag
  // a disconnect, especially following a crash or quick restart.
  if (logPlayers !== null) return { playerCount: logPlayers, connectedCount: logConnections ?? logPlayers, source: 'server-log' };
  if (snapshotPlayers !== null) return { playerCount: snapshotPlayers, connectedCount: snapshotPlayers, source: 'session-replay' };
  if (masterPlayers !== null) return { playerCount: masterPlayers, connectedCount: masterPlayers, source: 'master' };
  return { playerCount: null, connectedCount: null, source: 'unknown' };
}

export function memoryThresholdExceeded(bytes, thresholdBytes, wasHigh = false, recoveryRatio = 0.85) {
  const value = Number(bytes);
  const threshold = Number(thresholdBytes);
  const ratio = Math.min(0.99, Math.max(0.1, Number(recoveryRatio) || 0.85));
  if (!Number.isFinite(value) || !Number.isFinite(threshold) || threshold <= 0) return false;
  return value > threshold * (wasHigh ? ratio : 1);
}

function boundedText(value, max) {
  return String(value ?? '').replace(/[\u0000-\u001f\u007f]/g, ' ').trim().slice(0, max);
}

const SAFE_SERVER_COMMANDS = new Set([
  'help', 'countclients', 'listclients', 'listlocks', 'connectionstats', 'backup', 'changesettings',
  'dekessler', 'nukeksc', 'clearvessels', 'kick', 'ban', 'vessel', 'setfunds', 'setscience', 'restartserver'
]);

export function parseBridgeJson(text) {
  return JSON.parse(String(text ?? '').replace(/^\uFEFF/, ''));
}

export class LmpController extends EventEmitter {
  constructor(config, dependencies = {}) {
    super();
    this.config = config;
    this.fetch = dependencies.fetch || globalThis.fetch;
    this.now = dependencies.now || Date.now;
    this.powershellJson = dependencies.powershellJson || powershellJson;
    this.child = null;
    this.startedAt = null;
    this.externalPid = null;
    this.lastStatus = null;
    this.masterMissingSinceMs = null;
    this.playerSession = { pid: null, file: '', offset: 0, remainder: '', count: null, connected: null, active: new Map() };
    this.playerSnapshotPromise = Promise.resolve();
  }

  get root() { return path.resolve(this.config.lmp.serverRoot); }
  get executablePath() { return path.join(this.root, this.config.lmp.executable || 'Server.exe'); }
  get logsPath() { return path.join(this.root, 'logs'); }
  get bridgeRoot() { return path.resolve(this.config.lmp.bridgeDirectory || path.join(this.root, 'APFDiscordBridge')); }
  get deploymentRoot() {
    const root = this.config.lmp.deploymentRoot;
    if (!root) return null;
    if (path.resolve(root, 'Server').toLowerCase() !== this.root.toLowerCase()) throw new Error('KESAS deployment does not own this server directory');
    return path.resolve(root);
  }

  async readSettings() {
    const read = async name => { try { return await fs.readFile(path.join(this.root, 'Config', name), 'utf8'); } catch { return ''; } };
    const [general, connection, master, debug, interval, website] = await Promise.all([
      read('GeneralSettings.xml'), read('ConnectionSettings.xml'), read('MasterServerSettings.xml'),
      read('DebugSettings.xml'), read('IntervalSettings.xml'), read('WebsiteSettings.xml')
    ]);
    return {
      name: parseTag(general, 'ServerName', this.config.lmp.serverName || 'LMP'),
      description: parseTag(general, 'Description'),
      mode: parseTag(general, 'GameMode'),
      password: parseTag(general, 'Password').length > 0,
      maxPlayers: Number.parseInt(parseTag(general, 'MaxPlayers', '0'), 10) || 0,
      modControl: parseTag(general, 'ModControl', 'false').toLowerCase() === 'true',
      port: Number.parseInt(parseTag(connection, 'Port', String(this.config.lmp.port || 2252)), 10),
      register: parseTag(master, 'RegisterWithMasterServer', 'false').toLowerCase() === 'true',
      customMaster: parseTag(debug, 'CustomMasterServer'),
      backupIntervalMs: Number.parseInt(parseTag(interval, 'BackupIntervalMs', '30000'), 10) || 30000,
      websiteEnabled: parseTag(website, 'EnableWebsite', 'false').toLowerCase() === 'true',
      websitePort: Number.parseInt(parseTag(website, 'Port', '8900'), 10) || 8900
    };
  }

  async findExternalProcess() {
    return (await this.observeExternalProcess()).process;
  }

  async observeExternalProcess() {
    const target = shellQuotePowerShell(this.executablePath);
    const name = shellQuotePowerShell(path.basename(this.executablePath));
    let reason = 'process-query-failed';
    try {
      const result = await this.powershellJson(`$target=${target}; $name=${name}; @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $_.ExecutablePath -eq $target -or $_.Name -eq $name } | Select-Object ProcessId,Name,CreationDate,ExecutablePath,CommandLine | ConvertTo-Json -Compress)`);
      const rows = Array.isArray(result) ? result : (result ? [result] : []);
      const row = rows.find(value => Number.isSafeInteger(Number(value.ProcessId)) && Number(value.ProcessId) > 0 &&
        String(value.ExecutablePath || '').toLowerCase() === this.executablePath.toLowerCase());
      if (row) return { state: 'online', reason: 'exact-process-path', process: { pid: Number(row.ProcessId), created: row.CreationDate, executablePath: row.ExecutablePath, commandLine: row.CommandLine, owned: false } };
      // A successful complete process query can prove absence. A hidden path,
      // failed query, or stale bridge cannot prove that the server exited.
      const protectedRows = rows.filter(value => !value.ExecutablePath && String(value.Name || '').toLowerCase() === path.basename(this.executablePath).toLowerCase());
      if (!protectedRows.length) return { state: 'offline', reason: 'process-not-found', process: null };
      reason = 'protected-process-path';
    } catch { /* A protected process can hide ExecutablePath without being offline. */ }
    if (!this.deploymentRoot) return { state: 'unknown', reason, process: null };
    return this.observeBridgeProcess();
  }

  async findBridgeProcess() {
    return (await this.observeBridgeProcess()).process;
  }

  async observeBridgeProcess() {
    const unknown = reason => ({ state: 'unknown', reason, process: null });
    try {
      // Restrict this observation fallback to the native KESAS bridge inside its
      // owned server root. Never treat a PID file or a bound port alone as identity.
      if (!this.deploymentRoot || this.bridgeRoot.toLowerCase() !== path.join(this.root, 'APFDiscordBridge').toLowerCase()) return unknown('bridge-location-untrusted');
      const canonicalRoot = path.join(await fs.realpath(this.deploymentRoot), 'Server');
      if ((await fs.realpath(this.root)).toLowerCase() !== canonicalRoot.toLowerCase() ||
          (await fs.lstat(this.root)).isSymbolicLink() || (await fs.lstat(this.bridgeRoot)).isSymbolicLink()) return unknown('bridge-location-untrusted');
      const read = async () => {
        const files = ['ready.json', 'status.json'].map(name => path.join(this.bridgeRoot, name));
        for (const file of files) {
          const stat = await fs.lstat(file);
          if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 1024 * 1024 ||
              (await fs.realpath(file)).toLowerCase() !== path.join(canonicalRoot, 'APFDiscordBridge', path.basename(file)).toLowerCase()) throw new Error('Untrusted bridge location');
        }
        return Promise.all(files.map(async file => parseBridgeJson(await fs.readFile(file, 'utf8'))));
      };
      const [firstReady, firstLive] = await read();
      const pid = firstReady.pid;
      if (!Number.isSafeInteger(pid) || pid <= 0 || firstLive.serverPid !== pid) return unknown('bridge-pid-mismatch');
      const settings = await this.readSettings();
      const port = Number(settings.port);
      if (!Number.isInteger(port) || port < 1 || port > 65535) return unknown('invalid-game-port');
      const observation = await this.powershellJson(`$p=@(Get-CimInstance Win32_Process -Filter 'ProcessId=${pid}' -ErrorAction Stop | Select-Object ProcessId,Name,CreationDate,ExecutablePath,CommandLine); $owners=@(Get-NetUDPEndpoint -LocalPort ${port} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique); [pscustomobject]@{processes=$p;udpOwners=$owners} | ConvertTo-Json -Compress`);
      const rows = Array.isArray(observation?.processes) ? observation.processes : (observation?.processes ? [observation.processes] : []);
      const owners = Array.isArray(observation?.udpOwners) ? observation.udpOwners : [observation?.udpOwners];
      const row = rows[0];
      if (rows.length !== 1 || Number(row.ProcessId) !== pid ||
          String(row.Name || '').toLowerCase() !== path.basename(this.executablePath).toLowerCase() ||
          owners.length !== 1 || Number(owners[0]) !== pid ||
          (row.ExecutablePath && String(row.ExecutablePath).toLowerCase() !== this.executablePath.toLowerCase())) return unknown('bridge-process-or-udp-mismatch');
      // Re-read after the OS query to reject a restart/PID reuse or stale bridge.
      const [ready, live] = await read();
      const created = dateFromValue(row.CreationDate)?.getTime();
      const started = Date.parse(ready.startedAt);
      const updated = Date.parse(live.updatedAt);
      const now = this.now();
      if (ready.pid !== pid || live.serverPid !== pid || ready.startedAt !== firstReady.startedAt ||
          !Number.isFinite(created) || !Number.isFinite(started) || !Number.isFinite(updated) ||
          created > now + 2000 || started < created - 1000 || started > now + 2000 ||
          updated < started - 1000 || now - updated > 5000 || updated > now + 2000 ||
          !Number.isInteger(live.playerCount) || live.playerCount < 0 ||
          !Number.isInteger(live.connectedCount) || live.connectedCount < live.playerCount ||
          !Array.isArray(live.players) || live.players.length !== live.playerCount) return unknown(now - updated > 5000 ? 'bridge-status-stale' : 'bridge-status-invalid');
      return { state: 'online', reason: 'native-bridge-udp', process: {
        pid, created: row.CreationDate, executablePath: row.ExecutablePath || null,
        commandLine: row.CommandLine || null, owned: false, identitySource: 'native-bridge-udp',
        executablePathVerified: Boolean(row.ExecutablePath)
      } };
    } catch (error) { return unknown(`bridge-read-or-query-${/^[A-Z0-9_]{1,32}$/.test(error?.code || '') ? error.code : 'failed'}`); }
  }

  async processInfo() {
    return (await this.observeProcess()).process;
  }

  async observeProcess() {
    if (this.child && !this.child.killed && this.child.exitCode === null) {
      return { state: 'online', reason: 'owned-child', process: { pid: this.child.pid, created: this.startedAt, executablePath: this.executablePath, commandLine: this.executablePath, owned: true } };
    }
    this.child = null;
    return this.observeExternalProcess();
  }

  async processResources(process) {
    const pid = Number(process?.pid);
    if (!Number.isInteger(pid) || pid <= 0) return null;
    try {
      const result = await powershellJson(`$p=Get-Process -Id ${pid} -ErrorAction Stop; [pscustomobject]@{ workingSetBytes=[int64]$p.WorkingSet64; privateBytes=[int64]$p.PrivateMemorySize64; cpuSeconds=[double]$p.CPU } | ConvertTo-Json -Compress`);
      return { workingSetBytes: Number(result?.workingSetBytes) || 0, privateBytes: Number(result?.privateBytes) || 0, cpuSeconds: Number(result?.cpuSeconds) || 0 };
    } catch { return null; }
  }

  async readLogTail(lines = 120) {
    const file = await latestFile(this.logsPath);
    return { file, text: file ? await tailText(file, lines) : '' };
  }

  async logCursor() {
    const file = await latestFile(this.logsPath);
    const stat = file ? await fs.stat(file).catch(() => null) : null;
    return { file, offset: stat?.size || 0 };
  }

  async readLogSince(cursor, maxBytes = 64 * 1024) {
    const file = await latestFile(this.logsPath);
    if (!file) return { file: '', text: '' };
    const stat = await fs.stat(file).catch(() => null);
    if (!stat) return { file, text: '' };
    const sameFile = cursor?.file && path.resolve(cursor.file) === path.resolve(file) && stat.size >= Number(cursor.offset || 0);
    let start = sameFile ? Number(cursor.offset || 0) : 0;
    if (stat.size - start > maxBytes) start = stat.size - maxBytes;
    const length = Math.max(0, stat.size - start);
    if (!length) return { file, text: '' };
    const handle = await fs.open(file, 'r');
    try {
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await handle.read(buffer, 0, length, start);
      return { file, text: buffer.subarray(0, bytesRead).toString('utf8').replace(/^\uFEFF/, '') };
    } finally { await handle.close(); }
  }

  async readLogWindow(maxChars = 1_000_000, startedAt = null) {
    let entries = await fs.readdir(this.logsPath, { withFileTypes: true }).catch(() => []);
    const files = [];
    for (const entry of entries.filter(item => item.isFile() && item.name.toLowerCase().endsWith('.log'))) {
      const file = path.join(this.logsPath, entry.name); const stat = await fs.stat(file).catch(() => null);
      if (stat) files.push({ file, mtimeMs: stat.mtimeMs, createdMs: stat.birthtimeMs > 0 ? stat.birthtimeMs : stat.mtimeMs });
    }
    files.sort((a, b) => a.mtimeMs - b.mtimeMs);
    const startMs = dateFromValue(startedAt)?.getTime() ?? null;
    const fromProcess = startMs === null ? files : files.filter(item => item.createdMs >= startMs - 2_000);
    // LMP starts a second log at midnight without restarting. Retain startup
    // markers from the first process log and current telemetry from the last.
    // Never fall back to a previous process's log while a new process is still
    // creating its first log. That can resurrect stale players and readiness.
    const selected = fromProcess;
    const file = selected[selected.length - 1]?.file || '';
    if (!file) return { file: '', text: '' };
    if (selected.length > 1) {
      const headLength = Math.floor(maxChars * 0.25);
      const tailLength = maxChars - headLength;
      const [first, last] = await Promise.all([
        fs.readFile(selected[0].file, 'utf8').catch(() => ''),
        fs.readFile(selected[selected.length - 1].file, 'utf8').catch(() => '')
      ]);
      return { file, text: `${first.slice(0, headLength)}\n...[rotated logs omitted]\n${last.slice(-tailLength)}` };
    }
    const text = await fs.readFile(selected[0].file, 'utf8').catch(() => '');
    if (text.length <= maxChars) return { file, text };
    const head = Math.floor(maxChars * 0.25);
    return { file, text: `${text.slice(0, head)}\n...[middle omitted]\n${text.slice(-(maxChars - head))}` };
  }

  async readPlayerLogDelta(process, session) {
    const entries = await fs.readdir(this.logsPath, { withFileTypes: true }).catch(() => []);
    const files = [];
    for (const entry of entries.filter(item => item.isFile() && item.name.toLowerCase().endsWith('.log'))) {
      const candidate = path.join(this.logsPath, entry.name);
      const candidateStat = await fs.stat(candidate).catch(() => null);
      if (candidateStat) files.push({ file: candidate, stat: candidateStat, createdMs: candidateStat.birthtimeMs > 0 ? candidateStat.birthtimeMs : candidateStat.mtimeMs });
    }
    files.sort((a, b) => a.stat.mtimeMs - b.stat.mtimeMs);
    const startMs = dateFromValue(process.created)?.getTime() ?? null;
    const processFiles = startMs === null ? files : files.filter(item => item.createdMs >= startMs - 2_000);
    const latest = processFiles[processFiles.length - 1];
    const file = latest?.file || '';
    const pid = Number(process.pid);
    if (!file) return { file: '', pid, reset: session.pid !== pid, offset: 0, text: '' };
    const stat = latest.stat;
    if (!stat) return { file, pid, reset: session.pid !== pid, offset: 0, text: '' };
    const reset = session.pid !== pid || !session.file;
    if (reset) {
      const text = (await Promise.all(processFiles.map(item => fs.readFile(item.file, 'utf8').catch(() => '')))).join('\n');
      return { file, pid, reset: true, offset: stat.size, text };
    }
    const sameFile = path.resolve(session.file || '.') === path.resolve(file);
    const start = sameFile && stat.size >= session.offset ? session.offset : 0;
    const length = Math.max(0, stat.size - start);
    if (!length) return { file, pid, reset, offset: stat.size, text: '' };
    const handle = await fs.open(file, 'r');
    try {
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await handle.read(buffer, 0, length, start);
      return { file, pid, reset, offset: start + bytesRead, text: buffer.subarray(0, bytesRead).toString('utf8').replace(/^\uFEFF/, '') };
    } finally { await handle.close(); }
  }

  async playerSnapshotUnlocked() {
    const process = await this.processInfo();
    if (!process) {
      this.playerSession = { pid: null, file: '', offset: 0, remainder: '', count: 0, connected: 0, active: new Map() };
      if (this.config.lmp.requireLivePlayerSnapshot || this.deploymentRoot) return { count: null, connected: null, players: [], source: 'process-unverified' };
      return { count: 0, connected: 0, players: [], source: 'offline' };
    }
    const live = await fs.readFile(path.join(this.bridgeRoot, 'status.json'), 'utf8').then(parseBridgeJson).catch(() => null);
    if (live && Number(live.serverPid) === Number(process.pid) &&
        Math.abs(Date.now() - Date.parse(live.updatedAt)) <= 5000 &&
        Number.isInteger(live.playerCount) && live.playerCount >= 0 &&
        Number.isInteger(live.connectedCount) && live.connectedCount >= live.playerCount &&
        Array.isArray(live.players) && live.players.length === live.playerCount) {
      return { count: live.playerCount, connected: live.connectedCount, players: live.players, source: 'bridge' };
    }
    if (this.config.lmp.requireLivePlayerSnapshot) return { count: null, connected: null, players: [], source: 'bridge-unavailable' };
    const delta = await this.readPlayerLogDelta(process, this.playerSession);
    if (delta.reset) this.playerSession = { pid: delta.pid, file: delta.file, offset: 0, remainder: '', count: null, connected: null, active: new Map() };
    const session = this.playerSession;
    const text = `${session.remainder}${delta.text}`;
    const lines = text.split(/\r?\n/);
    session.remainder = lines.pop() || '';
    for (const line of lines) {
      if (/\]\[LMP\]: (?:Starting '.+?' on Address|All systems up and running)/i.test(line) && session.count === null) {
        session.count = 0;
        session.connected = 0;
      }
      const handshake = line.match(/\]\[LMP\]: Client (.+?) \(((?:key-)?[0-9a-f]{16,})\) handshake successful, LMP Version: ([^,]+), KSP Version: ([^\s\]]+)/i);
      if (handshake) {
        session.active.set(handshake[1].trim(), { name: handshake[1].trim(), id: handshake[2], lmpVersion: handshake[3].trim(), kspVersion: handshake[4].trim(), lastSeen: line.slice(1, 9) });
        continue;
      }
      const ended = line.match(/\]\[Debug\]: (.+?) sent Connection end message, reason: (.+)$/i);
      if (ended) session.active.delete(ended[1].trim());
      const online = line.match(/Online Players:\s*(\d+),\s*connected:\s*(\d+)/i);
      if (online) {
        session.count = Number(online[1]); session.connected = Number(online[2]);
        // A newly opened socket is logged before authentication as
        // "Online Players: 0, connected: 1". It must not erase established
        // handshakes. Only a fully empty server is authoritative enough to
        // clear identities; named connection-end lines handle normal leaves.
        if (session.count === 0 && session.connected === 0) session.active.clear();
      }
    }
    session.pid = delta.pid;
    session.file = delta.file;
    session.offset = delta.offset;
    return { count: session.count, connected: session.connected, players: [...session.active.values()].sort((a, b) => a.name.localeCompare(b.name)), source: 'log' };
  }

  async playerSnapshot() {
    const run = () => this.playerSnapshotUnlocked();
    const pending = this.playerSnapshotPromise.then(run, run);
    this.playerSnapshotPromise = pending.then(() => undefined, () => undefined);
    return pending;
  }

  async pluginInventory() {
    const directory = path.join(this.root, 'Plugins');
    const entries = await fs.readdir(directory, { withFileTypes: true }).catch(() => []);
    const rows = [];
    for (const entry of entries) {
      const base = path.join(directory, entry.name);
      if (entry.isFile() && entry.name.toLowerCase().endsWith('.dll')) rows.push({ name: entry.name, path: base, type: 'dll' });
      if (!entry.isDirectory()) continue;
      const nested = await fs.readdir(base, { withFileTypes: true }).catch(() => []);
      const dlls = nested.filter(item => item.isFile() && item.name.toLowerCase().endsWith('.dll')).map(item => item.name);
      rows.push({ name: entry.name, path: base, type: 'directory', dlls });
    }
    return rows.sort((a, b) => a.name.localeCompare(b.name));
  }

  async modControlSummary() {
    const candidates = [path.join(this.root, 'Config', 'LMPModControl.xml'), path.join(this.root, 'LMPModControl.xml')];
    let file = ''; let buffer = null;
    for (const candidate of candidates) { const read = await fs.readFile(candidate).catch(() => null); if (read) { file = candidate; buffer = read; break; } }
    if (!buffer) return { configured: false, file: null };
    const text = buffer[0] === 0xff && buffer[1] === 0xfe ? buffer.toString('utf16le') : buffer.toString('utf8');
    const block = name => text.match(new RegExp(`<${name}>[\\s\\S]*?<\\/${name}>`, 'i'))?.[0] || '';
    const strings = value => [...value.matchAll(/<string>([\s\S]*?)<\/string>/gi)].map(match => match[1].trim()).filter(Boolean);
    const count = (value, tag) => (value.match(new RegExp(`<${tag}(?:\\s|>)`, 'gi')) || []).length;
    const stat = await fs.stat(file).catch(() => null);
    return {
      configured: true, file, bytes: buffer.length, modifiedAt: stat?.mtime?.toISOString() || null,
      allowNonListedPlugins: parseTag(text, 'AllowNonListedPlugins', 'false').toLowerCase() === 'true',
      requiredExpansions: strings(block('RequiredExpansions')),
      mandatoryPlugins: count(block('MandatoryPlugins'), 'DllFile'),
      optionalPlugins: count(block('OptionalPlugins'), 'DllFile'),
      allowedParts: strings(block('AllowedParts')).length,
      allowedResources: strings(block('AllowedResources')).length
    };
  }

  async storageInfo() {
    const stat = await fs.statfs(this.root).catch(() => null);
    if (!stat) return { available: false };
    const totalBytes = Number(stat.blocks) * Number(stat.bsize);
    const freeBytes = Number(stat.bavail) * Number(stat.bsize);
    return { available: true, totalBytes, freeBytes, total: formatBytes(totalBytes), free: formatBytes(freeBytes), percentFree: totalBytes ? (freeBytes / totalBytes) * 100 : 0 };
  }

  async udpBinding(port, processInfo = null) {
    const value = Number(port);
    if (!Number.isInteger(value) || value < 1 || value > 65535) return { checked: false, port: value, reason: 'invalid-port' };
    if (process.platform !== 'win32') return { checked: false, port: value, reason: 'windows-check-only' };
    try {
      const result = await powershellJson(`$rows=@(Get-NetUDPEndpoint -LocalPort ${value} -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,OwningProcess); [pscustomobject]@{ endpoints=$rows } | ConvertTo-Json -Compress`);
      const endpoints = Array.isArray(result?.endpoints) ? result.endpoints : (result?.endpoints ? [result.endpoints] : []);
      const pid = Number(processInfo?.pid || 0);
      return { checked: true, port: value, bound: endpoints.length > 0, ownedByProcess: pid > 0 && endpoints.some(endpoint => Number(endpoint.OwningProcess) === pid), endpoints: endpoints.slice(0, 8) };
    } catch (error) {
      return { checked: false, port: value, reason: error.message };
    }
  }

  async bridgeStatus(process = null) {
    const readyPath = path.join(this.bridgeRoot, 'ready.json');
    const ready = await fs.readFile(readyPath, 'utf8').then(parseBridgeJson).catch(() => null);
    const count = async name => (await fs.readdir(path.join(this.bridgeRoot, name), { withFileTypes: true }).catch(() => [])).filter(item => item.isFile()).length;
    const [inbox, events, results] = await Promise.all([count('inbox'), count('events'), count('results')]);
    return {
      configured: Boolean(this.config.lmp.bridgeDirectory),
      ready: Boolean(ready?.pid && process?.pid && Number(ready.pid) === Number(process.pid)),
      pid: ready?.pid || null,
      currentPid: process?.pid || null,
      capabilities: Array.isArray(ready?.capabilities) ? ready.capabilities : [],
      inbox,
      events,
      results
    };
  }

  async masterListing(settings) {
    const url = this.config.lmp.masterListUrl;
    if (!url) return { checked: false, visible: false, url: '' };
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await this.fetch(url, { signal: controller.signal });
      if (!response.ok) return { checked: false, visible: null, available: false, url, status: response.status, error: `HTTP ${response.status}` };
      const html = await response.text();
      const rows = [...html.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/gi)].map(match => match[1]);
      if (!rows.length) return { checked: false, visible: null, available: false, url, status: response.status, error: 'master list contained no rows' };
      const decode = value => String(value).replace(/<[^>]+>/g, ' ').replaceAll('&nbsp;', ' ').replaceAll('&#39;', "'").replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&quot;', ' ').replace(/\s+/g, ' ').trim();
      const row = rows.find(candidate => { const text = decode(candidate); return text.includes(settings.name) && text.includes(`:${settings.port}`); });
      const cells = row ? [...row.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/gi)].map(match => decode(match[1])) : [];
      const parseCount = value => /^\d+$/.test(String(value || '').trim()) ? Number(value) : null;
      const visible = Boolean(row);
      const now = this.now();
      if (visible) this.masterMissingSinceMs = null;
      else if (this.masterMissingSinceMs === null) this.masterMissingSinceMs = now;
      const missingForMs = visible || this.masterMissingSinceMs === null ? 0 : Math.max(0, now - this.masterMissingSinceMs);
      const configuredGrace = Number(this.config.health?.masterListingGraceMinutes);
      const graceMinutes = Number.isFinite(configuredGrace) ? Math.max(0, configuredGrace) : 5;
      const graceMs = graceMinutes * 60 * 1000;
      return {
        checked: true, available: true, visible, confirmedMissing: !visible && missingForMs >= graceMs,
        missingSince: visible || this.masterMissingSinceMs === null ? null : new Date(this.masterMissingSinceMs).toISOString(),
        missingForMs, graceMs, url, status: response.status,
        playerCount: parseCount(cells[8]), maxPlayers: parseCount(cells[9])
      };
    } catch (error) { return { checked: false, visible: null, available: false, url, error: error.message }; }
    finally { clearTimeout(timer); }
  }

  async status() {
    const observedAt = new Date(this.now()).toISOString();
    const [settings, observation, storage, plugins, modControl, playerSnapshot] = await Promise.all([this.readSettings(), this.observeProcess(), this.storageInfo(), this.pluginInventory(), this.modControlSummary(), this.playerSnapshot()]);
    const process = observation.process;
    const unknown = observation.state === 'unknown';
    const resources = await this.processResources(process);
    const bridge = await this.bridgeStatus(process);
    const log = await this.readLogWindow(1_000_000, process?.created);
    const master = await this.masterListing(settings);
    const udpBinding = await this.udpBinding(settings.port, process);
    const playerCountText = lastMatch(log.text, /(?:Online Players|Players Online|connected clients|connected):\s*(\d+)/gi, null);
    const connectedText = lastMatch(log.text, /(?:connected clients|connected):\s*(\d+)/gi, null);
    const nat = lastMatch(log.text, /Detected NAT addresses:\s*([^\r\n]+)/gi, 'unknown');
    // The startup line can leave the bounded log window on a long-running
    // server. A fresh PID-bound bridge snapshot plus that process's owned UDP
    // listener also establishes readiness without trusting an old PID file.
    const ready = /All systems up and running/i.test(log.text) || Boolean(
      process && bridge.ready && playerSnapshot.source === 'bridge' &&
      udpBinding.checked && udpBinding.ownedByProcess);
    const registration = /Master server registration is active/i.test(log.text);
    const logStat = log.file ? await fs.stat(log.file).catch(() => null) : null;
    const logAgeMs = logStat ? Date.now() - logStat.mtimeMs : null;
    const healthConfig = this.config.health || {};
    const logQuietMinutes = Math.max(1, Number(healthConfig.logQuietMinutes) || 60);
    const diskWarningPercent = Math.min(99, Math.max(1, Number(healthConfig.diskWarningPercent) || 10));
    const startupGraceMinutes = Math.max(0, Number(healthConfig.startupGraceMinutes) || 5);
    const processStartMs = dateFromValue(process?.created || this.startedAt)?.getTime() || null;
    const processAgeMs = processStartMs === null ? null : Math.max(0, Date.now() - processStartMs);
    const healthIssues = [];
    const addHealthIssue = (code, message, severity = 'warning') => healthIssues.push({ code, message, severity });
    if (!process) addHealthIssue(unknown ? 'status-observation' : 'server-offline', unknown ? `server status unavailable (${observation.reason})` : 'server offline', unknown ? 'warning' : 'critical');
    if (process && !ready && (processAgeMs === null || processAgeMs > startupGraceMinutes * 60 * 1000)) addHealthIssue('startup-readiness', 'startup readiness not confirmed');
    if (process && settings.register && !registration && (processAgeMs === null || processAgeMs > startupGraceMinutes * 60 * 1000)) addHealthIssue('master-registration', 'master registration not confirmed');
    if (process && master.checked && master.visible === false && master.confirmedMissing) addHealthIssue('master-listing', 'not visible in master listing');
    if (process && udpBinding.checked && !udpBinding.bound) addHealthIssue('udp-bind', `UDP ${settings.port} is not bound`, 'critical');
    if (process && udpBinding.checked && udpBinding.bound && !udpBinding.ownedByProcess) addHealthIssue('udp-owner', `UDP ${settings.port} is owned by another process`, 'critical');
    if (process && storage.available && storage.percentFree < diskWarningPercent) addHealthIssue('disk-low', `disk free below ${diskWarningPercent}%`);
    const memoryWarningBytes = Number(this.config.lmp.memoryWarningBytes || 0);
    const wasMemoryHigh = this.lastStatus?.healthIssues?.some(issue => issue?.code === 'memory-high') === true;
    if (process && memoryThresholdExceeded(resources?.workingSetBytes, memoryWarningBytes, wasMemoryHigh, healthConfig.memoryRecoveryRatio)) addHealthIssue('memory-high', `working set above ${formatBytes(memoryWarningBytes)}`);
    if (process && bridge.configured && !bridge.ready) addHealthIssue('bridge', 'bridge not ready');
    const healthInfo = [];
    if (process && logAgeMs !== null && logAgeMs > logQuietMinutes * 60 * 1000) healthInfo.push(`log quiet for ${Math.round(logAgeMs / 60000)} minutes; telemetry only`);
    const logPlayerCount = playerCountText === null ? null : Number(playerCountText);
    const masterPlayerCount = Number.isFinite(master.playerCount) ? master.playerCount : null;
    const counts = this.config.lmp.requireLivePlayerSnapshot
      ? { playerCount: playerSnapshot.count, connectedCount: playerSnapshot.connected, source: playerSnapshot.source }
      : resolvePlayerCounts(Boolean(process), playerSnapshot.count ?? logPlayerCount, playerSnapshot.connected ?? (connectedText === null ? null : Number(connectedText)), masterPlayerCount, playerSnapshot.count);
    const status = {
      observedAt,
      processObservation: { state: observation.state, reason: observation.reason },
      online: unknown ? null : Boolean(process),
      pid: process?.pid || null,
      owned: Boolean(process?.owned),
      startedAt: dateFromValue(process?.created || this.startedAt)?.toISOString() || null,
      uptimeMs: process ? Math.max(0, Date.now() - (processStartMs || Date.now())) : null,
      resources,
      executablePath: this.executablePath,
      settings,
      logFile: log.file,
      lastLogWriteAt: logStat?.mtime?.toISOString() || null,
      logAgeMs,
      ready,
      registration,
      nat,
      udpBinding,
      master,
      storage,
      plugins,
      modControl,
      bridge,
      healthIssues,
      healthWarnings: healthIssues.map(issue => issue.message),
      healthInfo,
      healthPolicy: { startupGraceMinutes, logQuietMinutes, diskWarningPercent, masterListingGraceMinutes: Number(healthConfig.masterListingGraceMinutes ?? 5) },
      healthSummary: healthIssues.length ? healthIssues.map(issue => issue.message).join('; ') : 'healthy',
      playerCount: unknown ? null : (observation.state === 'offline' ? 0 : counts.playerCount),
      connectedCount: unknown ? null : (observation.state === 'offline' ? 0 : counts.connectedCount),
      playerCountSource: unknown ? 'unknown' : (observation.state === 'offline' ? 'offline' : counts.source),
      players: playerSnapshot.players,
      playerSnapshot,
      mode: this.config.lmp.control?.mode || 'observe',
      externalLifecycleEnabled: Boolean(this.config.lmp.control?.allowExternalProcessStart && this.config.lmp.control?.allowExternalProcessStop && this.config.lmp.control?.allowExternalProcessRestart)
    };
    this.lastStatus = status;
    return status;
  }

  async start() {
    const existing = await this.processInfo();
    if (existing) return { ok: false, reason: 'already-running', process: existing };
    const mode = this.config.lmp.control?.mode || 'observe';
    if (mode !== 'supervise' && !this.config.lmp.control?.allowExternalProcessStart) return { ok: false, reason: 'observe-mode' };
    if (!await fs.stat(this.executablePath).catch(() => null)) return { ok: false, reason: `missing-executable:${this.executablePath}` };
    if (mode !== 'supervise') return this.startExternal();
    const visible = this.config.lmp.control?.visibleTerminal !== false;
    this.child = spawn(this.executablePath, [], { cwd: this.root, windowsHide: !visible, stdio: ['pipe', 'pipe', 'pipe'] });
    this.startedAt = new Date().toISOString();
    const forward = stream => stream.on('data', data => {
      const text = data.toString();
      if (visible) process.stdout.write(`[LMP] ${text}`);
      this.emit('output', text);
    });
    forward(this.child.stdout); forward(this.child.stderr);
    this.child.on('exit', (code, signal) => this.emit('exit', { code, signal, pid: this.child?.pid }));
    return { ok: true, pid: this.child.pid, visibleTerminal: visible };
  }

  async sendConsoleLine(line) {
    const process = await this.processInfo();
    if (!process) return { ok: false, reason: 'offline' };
    if (!process.owned || !this.child?.stdin?.writable) return { ok: false, reason: 'not-controller-owned' };
    const text = String(line).replace(/[\r\n]/g, ' ').trim();
    if (!text) return { ok: false, reason: 'empty-command' };
    this.child.stdin.write(`${text}\n`);
    return { ok: true, command: text };
  }

  async sendBridgeMessage(text, author = 'Discord', options = {}) {
    const ready = await fs.stat(path.join(this.bridgeRoot, 'ready.json')).catch(() => null);
    if (!ready?.isFile()) return { ok: false, reason: 'bridge-not-ready' };
    const process = await this.processInfo();
    if (!process) return { ok: false, reason: 'server-offline' };
    const readyData = await fs.readFile(path.join(this.bridgeRoot, 'ready.json'), 'utf8').then(parseBridgeJson).catch(() => null);
    if (readyData?.pid && Number(readyData.pid) !== Number(process.pid)) return { ok: false, reason: 'bridge-stale' };
    const inbox = path.join(this.bridgeRoot, 'inbox'); await fs.mkdir(inbox, { recursive: true });
    const maxQueued = Math.max(1, Number(this.config.security.bridgeMaxInboxFiles || 200));
    const queued = (await fs.readdir(inbox, { withFileTypes: true }).catch(() => [])).filter(entry => entry.isFile() && entry.name.endsWith('.json')).length;
    if (queued >= maxQueued) return { ok: false, reason: 'bridge-inbox-full' };
    const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    const ttlMs = Math.max(10_000, Number(this.config.security.bridgeMessageTtlMs || 120_000));
    const messageText = boundedText(text, this.config.security.maxMessageLength || 500);
    if (!messageText) return { ok: false, reason: 'empty-message' };
    await writeJsonAtomic(path.join(inbox, `${id}.json`), { kind: 'chat', id, serverPid: Number(process.pid), author: boundedText(author, 64) || 'Discord', text: messageText, announcement: options.announcement === true, createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + ttlMs).toISOString() });
    if (options.announcement && this.deploymentRoot) {
      const resultPath = path.join(this.bridgeRoot, 'results', `${id}.json`);
      const deadline = Date.now() + 5000;
      while (Date.now() < deadline) {
        const result = await fs.readFile(resultPath, 'utf8').then(parseBridgeJson).catch(() => null);
        if (result) {
          await fs.unlink(resultPath).catch(() => {});
          return { ok: result.ok === true && Number(result.serverPid) === Number(process.pid) && result.id === id,
            command: 'announcement', id };
        }
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      return { ok: false, reason: 'announcement-not-confirmed', id };
    }
    return { ok: true, command: 'bridge-queued', id };
  }

  async sendBridgeCommand(command, argument = '') {
    const name = String(command || '').trim().toLowerCase();
    const commandArgument = String(argument || '').replace(/[\r\n\x00-\x1f\x7f]/g, ' ').trim();
    if (!SAFE_SERVER_COMMANDS.has(name) || commandArgument.length > 260) return { ok: false, reason: 'invalid-bridge-command' };
    const readyPath = path.join(this.bridgeRoot, 'ready.json');
    const ready = await fs.stat(readyPath).catch(() => null);
    if (!ready?.isFile()) return { ok: false, reason: 'bridge-not-ready' };
    const process = await this.processInfo();
    if (!process) return { ok: false, reason: 'server-offline' };
    const readyData = await fs.readFile(readyPath, 'utf8').then(parseBridgeJson).catch(() => null);
    if (!readyData?.pid || Number(readyData.pid) !== Number(process.pid)) return { ok: false, reason: 'bridge-stale' };
    if (!Array.isArray(readyData.capabilities) || !readyData.capabilities.includes('commands')) return { ok: false, reason: 'bridge-command-not-supported' };

    const inbox = path.join(this.bridgeRoot, 'inbox');
    const results = path.join(this.bridgeRoot, 'results');
    await Promise.all([fs.mkdir(inbox, { recursive: true }), fs.mkdir(results, { recursive: true })]);
    const maxQueued = Math.max(1, Number(this.config.security.bridgeMaxInboxFiles || 200));
    const queued = (await fs.readdir(inbox, { withFileTypes: true }).catch(() => [])).filter(entry => entry.isFile() && entry.name.endsWith('.json')).length;
    if (queued >= maxQueued) return { ok: false, reason: 'bridge-inbox-full' };

    const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    const ttlMs = Math.max(10_000, Number(this.config.security.bridgeMessageTtlMs || 120_000));
    const resultPath = path.join(results, `${id}.json`);
    await writeJsonAtomic(path.join(inbox, `${id}.json`), {
      kind: 'command', id, serverPid: Number(process.pid), command: name, argument: commandArgument,
      createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + ttlMs).toISOString()
    });

    const timeoutMs = Math.max(1000, Math.min(15_000, Number(this.config.security.bridgeCommandTimeoutMs || 5000)));
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const response = await fs.readFile(resultPath, 'utf8').then(parseBridgeJson).catch(() => null);
      if (response) {
        await fs.unlink(resultPath).catch(() => {});
        if (response.id !== id || Number(response.serverPid) !== Number(process.pid)) return { ok: false, reason: 'bridge-command-result-invalid', id };
        return { ok: response.ok === true, reason: response.reason || undefined, command: `/${name}${commandArgument ? ` ${commandArgument}` : ''}`, mode: 'bridge', id };
      }
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    return { ok: false, reason: 'bridge-command-timeout', command: `/${name}`, mode: 'bridge', id };
  }

  async say(text, author = '') {
    const body = String(text).slice(0, this.config.security.maxMessageLength || 500);
    const directText = author ? `[Discord] ${String(author).slice(0, 64)}: ${body}` : body;
    const direct = await this.sendConsoleLine(directText);
    if (direct.ok || direct.reason !== 'not-controller-owned') return direct;
    return this.sendBridgeMessage(body, author || 'Discord', { announcement: !author });
  }

  async runSafeCommand(command) {
    let normalized = String(command).trim().replace(/^\//, '');
    if (!normalized || normalized.length > 300 || /[\x00-\x1f\x7f]/.test(normalized)) return { ok: false, reason: 'invalid-command-text' };
    const name = normalized.split(/\s+/, 1)[0].toLowerCase();
    if (!SAFE_SERVER_COMMANDS.has(name)) return { ok: false, reason: `command-not-allowlisted:${name}` };
    if (['kick', 'ban', 'setfunds', 'setscience'].includes(name)) {
      const argument = normalized.slice(name.length).trim();
      const splitAt = argument.search(/\s/);
      const requestedPlayer = splitAt < 0 ? argument : argument.slice(0, splitAt);
      const suffix = splitAt < 0 ? '' : argument.slice(splitAt);
      const snapshot = await this.playerSnapshot();
      const player = snapshot.players.find(candidate => String(candidate.name).toLowerCase() === requestedPlayer.toLowerCase());
      if (player) normalized = `${name} ${player.name}${suffix}`;
    }
    const cursor = await this.logCursor();
    let result = await this.sendConsoleLine(`/${normalized}`);
    if (!result.ok && result.reason === 'not-controller-owned') {
      result = name === 'restartserver'
        ? await this.restart()
        : await this.sendBridgeCommand(name, normalized.slice(name.length).trim());
    }
    if (!result.ok && result.mode !== 'bridge') return result;
    await new Promise(resolve => setTimeout(resolve, 800));
    const after = await this.readLogSince(cursor);
    return { ...result, output: after.text.split(/\r?\n/).filter(Boolean).join('\n') };
  }

  async restart() {
    const process = await this.processInfo();
    if (!process) return { ok: false, reason: 'offline' };
    if (process.owned) return this.sendConsoleLine('/restartserver');
    if (!this.config.lmp.control?.allowExternalProcessRestart) return { ok: false, reason: 'external-process-protected' };
    const stopped = await this.stopExternal(process);
    if (!stopped.ok) return stopped;
    await new Promise(resolve => setTimeout(resolve, 1000));
    return this.startExternal();
  }

  async stop() {
    const process = await this.processInfo();
    if (!process) return { ok: false, reason: 'offline' };
    if (!process.owned && !this.config.lmp.control?.allowExternalProcessStop) return { ok: false, reason: 'external-process-protected' };
    if (!process.owned) return this.stopExternal(process);
    this.child.kill('SIGTERM');
    return { ok: true, pid: process.pid };
  }

  async startExternal() {
    if (this.deploymentRoot) {
      const root = shellQuotePowerShell(this.deploymentRoot);
      const launcher = shellQuotePowerShell(path.join(this.deploymentRoot, 'Start-Server.ps1'));
      const result = await powershellJson(`$p=Start-Process powershell.exe -WorkingDirectory ${root} -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',${launcher}) -PassThru; [pscustomobject]@{ pid=$p.Id } | ConvertTo-Json -Compress`);
      return { ok: true, pid: Number(result?.pid), mode: 'kesas-launcher' };
    }
    const file = shellQuotePowerShell(this.executablePath); const directory = shellQuotePowerShell(this.root); const windowStyle = this.config.lmp.control?.visibleTerminal === false ? 'Hidden' : 'Normal';
    const result = await powershellJson(`$p=Start-Process -FilePath ${file} -WorkingDirectory ${directory} -PassThru -WindowStyle ${windowStyle}; [pscustomobject]@{ pid=$p.Id } | ConvertTo-Json -Compress`);
    return { ok: true, pid: Number(result?.pid), mode: 'external' };
  }

  async stopExternal(process) {
    if (this.deploymentRoot) {
      const script = shellQuotePowerShell(path.join(this.config.__root, 'scripts', 'Stop-KesasGracefully.ps1'));
      const root = shellQuotePowerShell(this.deploymentRoot);
      const pid = Number(process?.pid);
      if (!Number.isInteger(pid) || pid <= 0) return { ok: false, reason: 'invalid-process-id' };
      await powershellJson(`& ${script} -DeploymentRoot ${root} -ProcessId ${pid}`);
      return { ok: true, pid: process.pid, mode: 'kesas-launcher', graceful: true };
    }
    const pid = Number(process?.pid);
    if (!Number.isInteger(pid) || pid <= 0) return { ok: false, reason: 'invalid-process-id' };
    const script = shellQuotePowerShell(path.join(this.config.__root, 'scripts', 'Stop-APFLmpGracefully.ps1'));
    const executable = shellQuotePowerShell(this.executablePath);
    const result = await powershellJson(`& ${script} -ProcessId ${pid} -ExecutablePath ${executable} -TimeoutSeconds 45`);
    if (!result?.stopped) return { ok: false, reason: 'process-did-not-stop', pid, mode: 'external' };
    return { ok: true, pid, mode: 'external', graceful: Boolean(result.graceful), forced: Boolean(result.forced) };
  }

  async publishRelease(archive, notes = '', options = {}) {
    const root = this.deploymentRoot;
    if (!root) throw new Error('KESAS deployment root is not configured');
    if (this.config.__controlRoot) return publishThroughBroker(this.config, archive, notes, options);
    const executable = this.config.updates?.powerShellExecutable || 'pwsh.exe';
    const { stdout } = await execFileAsync(executable, ['-NoProfile', '-NonInteractive', '-File',
      path.join(this.config.__nativeCodeRoot || root, 'Publish-Release.ps1'), '-ReleaseZip', path.resolve(archive), '-Notes', String(notes), '-DeploymentRoot', root],
    { cwd: root, windowsHide: true, maxBuffer: 1024 * 1024 });
    return { ok: true, output: stdout, rollbackRoot: path.join(root, 'Backups', 'updates') };
  }
}

export class LogTailer extends EventEmitter {
  constructor(controller, intervalMs = 1000) { super(); this.controller = controller; this.intervalMs = intervalMs; this.timer = null; this.file = ''; this.offset = 0; this.chatSequence = 0; this.tickPromise = null; }
  tick() {
    if (this.tickPromise) return this.tickPromise;
    const run = this.tickUnlocked();
    this.tickPromise = run;
    void run.finally(() => { if (this.tickPromise === run) this.tickPromise = null; }).catch(() => {});
    return run;
  }
  async tickUnlocked() {
    const file = await latestFile(this.controller.logsPath);
    if (!file) return;
    if (file !== this.file) { this.file = file; this.offset = (await fs.stat(file)).size; return; }
    const stat = await fs.stat(file).catch(() => null);
    if (!stat) return;
    if (stat.size < this.offset) this.offset = 0;
    if (stat.size <= this.offset) return;
    const handle = await fs.open(file, 'r');
    try {
      const buffer = Buffer.alloc(stat.size - this.offset);
      await handle.read(buffer, 0, buffer.length, this.offset);
      this.offset = stat.size;
      for (const line of buffer.toString('utf8').split(/\r?\n/).filter(Boolean)) {
        this.emit('line', line);
        const match = line.match(/\]\[Chat\]:\s*([^:]+):\s*(.*)$/i) || line.match(/\]\[Chat\]\s+([^:]+):\s*(.*)$/i);
        if (match) this.emit('chat', { id: `log-${++this.chatSequence}`, source: 'log', from: match[1].trim(), text: match[2].trim(), raw: line });
        const handshake = line.match(/\]\[LMP\]: Client (.+?) \(((?:key-)?[0-9a-f]{16,})\) handshake successful, LMP Version: ([^,]+), KSP Version: ([^\s\]]+)/i);
        if (handshake) this.emit('player', { type: 'join', name: handshake[1].trim(), id: handshake[2], lmpVersion: handshake[3].trim(), kspVersion: handshake[4].trim(), raw: line });
        const ended = line.match(/\]\[Debug\]: (.+?) sent Connection end message, reason: (.+)$/i);
        if (ended) this.emit('player', { type: 'leave', name: ended[1].trim(), reason: ended[2].trim(), raw: line });
        const timeout = parseNetworkTimeoutLine(line);
        if (timeout) this.emit('timeout', { ...timeout, raw: line });
      }
    } finally { await handle.close(); }
  }
  start() { if (!this.timer) this.timer = setInterval(() => this.tick().catch(error => { if (this.listenerCount('error')) this.emit('error', error); }), this.intervalMs); return this.tick(); }
  stop() { if (this.timer) clearInterval(this.timer); this.timer = null; }
}

export class BridgeEventTailer extends EventEmitter {
  constructor(controller, intervalMs = 500) {
    super(); this.controller = controller; this.intervalMs = intervalMs; this.timer = null; this.tickPromise = null;
  }
  tick() {
    if (this.tickPromise) return this.tickPromise;
    const run = this.tickUnlocked();
    this.tickPromise = run;
    void run.finally(() => { if (this.tickPromise === run) this.tickPromise = null; }).catch(() => {});
    return run;
  }
  async tickUnlocked() {
    const dir = path.join(this.controller.bridgeRoot, 'events');
    const entries = await fs.readdir(dir, { withFileTypes: true }).catch(() => []);
    for (const entry of entries.filter(item => item.isFile() && item.name.endsWith('.json')).sort().slice(0, 32)) {
      const file = path.join(dir, entry.name);
      try {
        const stat = await fs.stat(file);
        const maxBytes = Math.max(4096, Number(this.controller.config?.security?.bridgeMaxEventBytes || 128 * 1024));
        if (!stat.isFile() || stat.size > maxBytes) throw new Error('bridge event exceeds size limit');
        const event = parseBridgeJson(await fs.readFile(file, 'utf8'));
        const ready = await fs.readFile(path.join(this.controller.bridgeRoot, 'ready.json'), 'utf8').then(parseBridgeJson).catch(() => null);
        const liveProcess = typeof this.controller.processInfo === 'function' ? await this.controller.processInfo() : null;
        const eventPid = Number(event?.serverPid);
        const livePid = Number(liveProcess?.pid);
        const readyPid = Number(ready?.pid);
        const receivedAt = Date.parse(event?.receivedAt || '');
        const ttlMs = Math.max(10_000, Number(this.controller.config?.security?.bridgeMessageTtlMs || 120_000));
        if (!Number.isInteger(eventPid) || eventPid <= 0 || !Number.isInteger(livePid) || eventPid !== livePid || !Number.isInteger(readyPid) || eventPid !== readyPid || !Number.isFinite(receivedAt) || Date.now() - receivedAt > ttlMs) { await fs.rm(file, { force: true }); continue; }
        let delivered = true;
        if (event?.type === 'lmp-chat' && event.text) {
          const text = boundedText(event.text, this.controller.config?.security?.maxMessageLength || 500);
          const from = boundedText(event.from || 'LMP', 64) || 'LMP';
          const id = boundedText(event.id || '', 80) || null;
          if (text) {
            const listeners = this.listeners('chat');
            if (listeners.length) {
              delivered = false;
              for (const listener of listeners) {
                try { if ((await listener({ id, source: 'bridge', from, text, raw: JSON.stringify(event) })) !== false) delivered = true; }
                catch (listenerError) { if (this.listenerCount('error')) this.emit('error', listenerError); }
              }
            }
          }
        }
        // Keep valid events when Discord is temporarily unavailable. The next
        // poll retries; normal TTL validation eventually removes stale items.
        if (!delivered) continue;
        await fs.rm(file, { force: true });
      } catch (error) {
        const failed = path.join(dir, 'failed');
        await fs.mkdir(failed, { recursive: true });
        await fs.rename(file, path.join(failed, entry.name)).catch(() => {});
        if (this.listenerCount('error')) this.emit('error', error);
      }
    }
  }
  start() { if (!this.timer) this.timer = setInterval(() => this.tick().catch(error => { if (this.listenerCount('error')) this.emit('error', error); }), this.intervalMs); return this.tick(); }
  stop() { if (this.timer) clearInterval(this.timer); this.timer = null; }
}
