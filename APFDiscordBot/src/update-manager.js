import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readJson, writeJsonAtomic } from './util.js';
const execFileAsync = promisify(execFile);

const SCHEMA_VERSION = 1;
const DEFAULT_POLL_MS = 2_000;
const DEFAULT_DEPLOYMENT_TIMEOUT_MS = 180_000;
const DEFAULT_COUNTDOWN_MINUTES = 15;
const COUNTDOWN_MARKS_MS = [15 * 60_000, 10 * 60_000, 5 * 60_000, 60_000, 15_000];
const BLOCKED_DESTINATION_ROOTS = new Set(['universe', 'logs', 'backups', 'updates']);
const ACTIVE_STATES = new Set(['queued', 'deploying', 'failed']);

function text(value, max = 240) {
  return String(value ?? '').replace(/[\u0000-\u001f\u007f]/g, ' ').trim().slice(0, max);
}

function countdownMinutes(value, fallback = DEFAULT_COUNTDOWN_MINUTES) {
  const parsed = Number(value);
  return Math.max(1, Math.min(1440, Number.isFinite(parsed) ? Math.round(parsed) : fallback));
}

function countdownLabel(milliseconds) {
  if (milliseconds >= 60_000) {
    const minutes = Math.round(milliseconds / 60_000);
    return `${minutes} minute${minutes === 1 ? '' : 's'}`;
  }
  const seconds = Math.round(milliseconds / 1000);
  return `${seconds} seconds`;
}

function packageId(value) {
  const id = text(value, 80);
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/.test(id)) throw new Error('invalid update package id');
  return id;
}

function safeRelative(value, label) {
  const raw = String(value ?? '').replaceAll('\\', '/').trim();
  if (!raw || raw.includes('\0') || raw.startsWith('/') || /^[A-Za-z]:\//i.test(raw)) throw new Error(`${label} must be relative`);
  const parts = raw.split('/').filter(Boolean);
  if (!parts.length || parts.some(part => part === '..' || part === '.')) throw new Error(`${label} contains unsafe path segments`);
  return parts.join('/');
}

function destinationRelative(value) {
  const relative = safeRelative(value, 'update destination');
  const first = relative.split('/')[0].toLowerCase();
  if (BLOCKED_DESTINATION_ROOTS.has(first)) throw new Error(`update destination is protected: ${first}`);
  return relative;
}

function safeJoin(root, relative) {
  const resolvedRoot = path.resolve(root);
  const resolved = path.resolve(resolvedRoot, ...relative.split('/'));
  const relativeToRoot = path.relative(resolvedRoot, resolved);
  if (!relativeToRoot || relativeToRoot.startsWith('..') || path.isAbsolute(relativeToRoot)) throw new Error('update path escaped target root');
  return resolved;
}

async function sha256(file) {
  const hash = crypto.createHash('sha256');
  const data = await fs.readFile(file);
  hash.update(data);
  return hash.digest('hex');
}

async function walkFiles(root, relative = '') {
  const directory = relative ? path.join(root, ...relative.split('/')) : root;
  const entries = await fs.readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const child = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) files.push(...await walkFiles(root, child));
    else if (entry.isFile()) files.push(child.replaceAll('\\', '/'));
  }
  return files;
}

function initialState() {
  return {
    schemaVersion: SCHEMA_VERSION,
    status: 'idle',
    packageId: null,
    version: null,
    notes: '',
    queuedAt: null,
    queuedBy: null,
    force: false,
    phase: null,
    countdownStartedAt: null,
    deployAt: null,
    countdownMinutes: null,
    countdownChangelog: '',
    countdownAnnouncements: [],
    error: null,
    lastDeployment: null
  };
}

export class UpdateManager {
  constructor(config, controller, universe) {
    this.config = config;
    this.controller = controller;
    this.universe = universe;
    const root = config.__root || process.cwd();
    const updates = config.updates || {};
    this.stagingRoot = path.resolve(root, updates.stagingRoot || 'updates/staged');
    this.statePath = path.resolve(root, updates.stateFile || 'state/update-state.json');
    this.rollbackRoot = path.resolve(root, updates.rollbackRoot || 'state/update-rollbacks');
    this.pollMs = Math.max(50, Number(updates.pollIntervalMs) || DEFAULT_POLL_MS);
    this.deploymentTimeoutMs = Math.max(5_000, Number(updates.deploymentTimeoutMs) || DEFAULT_DEPLOYMENT_TIMEOUT_MS);
    this.defaultCountdownMinutes = countdownMinutes(updates.defaultCountdownMinutes);
    this.requirePreDeployBackup = updates.requirePreDeployBackup !== false;
    this.state = initialState();
    this.loaded = false;
    this.stateRevision = 0;
    this.pendingSave = Promise.resolve();
    this.lastObservation = null;
    this.operation = null;
  }

  async load() {
    if (this.loaded) return this.state;
    const revision = this.stateRevision;
    await this.pendingSave;
    if (revision !== this.stateRevision) return this.state;
    const saved = await this.readSavedState();
    if (revision !== this.stateRevision) return this.state;
    if (saved && typeof saved === 'object') this.state = { ...initialState(), ...saved, schemaVersion: SCHEMA_VERSION };
    this.loaded = true;
    return this.state;
  }

  async readSavedState() { return readJson(this.statePath, {}); }

  async refreshFromDisk() {
    await this.load();
    if (this.operation) return this.state;
    const revision = this.stateRevision;
    await this.pendingSave;
    if (this.operation || revision !== this.stateRevision) return this.state;
    const saved = await this.readSavedState();
    // A queue/cancel/save or a newer refresh may have completed while this disk
    // read was suspended. Never restore the older snapshot over that decision.
    if (this.operation || revision !== this.stateRevision) return this.state;
    if (saved && typeof saved === 'object') {
      const refreshed = { ...initialState(), ...saved, schemaVersion: SCHEMA_VERSION };
      if (JSON.stringify(refreshed) !== JSON.stringify(this.state)) {
        this.state = refreshed;
        this.stateRevision++;
      }
    }
    return this.state;
  }

  async save() {
    this.stateRevision++;
    this.pendingSave = writeJsonAtomic(this.statePath, this.state);
    await this.pendingSave;
    return this.state;
  }

  async listStaged() {
    await fs.mkdir(this.stagingRoot, { recursive: true });
    const entries = await fs.readdir(this.stagingRoot, { withFileTypes: true });
    const rows = [];
    for (const entry of entries) {
      if (!entry.isDirectory() || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/.test(entry.name)) continue;
      try {
        const prepared = await this.readManifest(entry.name);
        rows.push({ id: prepared.manifest.id, version: prepared.manifest.version, files: prepared.manifest.files.length, notes: prepared.manifest.notes || '' });
      } catch { /* incomplete staging folders stay invisible */ }
    }
    return rows.sort((a, b) => String(b.version).localeCompare(String(a.version)) || a.id.localeCompare(b.id));
  }

  async status() {
    await this.refreshFromDisk();
    return {
      ...this.state,
      lastObservation: this.lastObservation,
      staged: await this.listStaged()
    };
  }

  async readManifest(id) {
    const normalizedId = packageId(id);
    const packageDir = safeJoin(this.stagingRoot, normalizedId);
    const manifest = await readJson(path.join(packageDir, 'manifest.json'), null);
    if (!manifest || manifest.schemaVersion !== SCHEMA_VERSION || manifest.id !== normalizedId) throw new Error('invalid update manifest');
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/.test(String(manifest.version || ''))) throw new Error('invalid update version');
    if (!Array.isArray(manifest.files) || !manifest.files.length || manifest.files.length > 5000) throw new Error('update manifest has no valid files');
    if (this.config.lmp?.deploymentRoot && (manifest.provider !== 'kesas-release' || manifest.files.length !== 1 || !/^kesas-rc[1-9][0-9]*$/.test(manifest.version))) throw new Error('KESAS requires a release archive, not direct server file replacement');
    const destinations = new Set();
    for (const row of manifest.files) {
      const source = safeRelative(row.source, 'update source');
      const destination = destinationRelative(row.destination);
      const key = destination.toLowerCase();
      if (destinations.has(key)) throw new Error(`duplicate update destination: ${destination}`);
      destinations.add(key);
      if (!Number.isSafeInteger(Number(row.sizeBytes)) || Number(row.sizeBytes) < 0) throw new Error(`invalid size for ${destination}`);
      if (!/^[a-f0-9]{64}$/i.test(String(row.sha256 || ''))) throw new Error(`invalid SHA-256 for ${destination}`);
      row.source = source;
      row.destination = destination;
    }
    return { packageDir, manifest };
  }

  async stage(sourceDirectory, options = {}) {
    await this.load();
    const sourceRoot = path.resolve(String(sourceDirectory || ''));
    const sourceStat = await fs.stat(sourceRoot).catch(() => null);
    if (sourceStat?.isFile() && this.config.lmp?.deploymentRoot) return this.stageKesas(sourceRoot, options);
    if (!sourceStat?.isDirectory()) throw new Error('update source directory not found');
    let metadata = {};
    for (const name of ['update.json', 'manifest.json']) {
      const candidate = path.join(sourceRoot, name);
      const parsed = await readJson(candidate, null).catch(() => null);
      if (parsed && typeof parsed === 'object') { metadata = parsed; break; }
    }
    const id = packageId(options.id || metadata.id || path.basename(sourceRoot));
    const version = text(options.version || metadata.version || new Date().toISOString().replaceAll(':', '-'), 80);
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/.test(version)) throw new Error('invalid update version');
    const payloadCandidate = path.join(sourceRoot, 'payload');
    const payloadStat = await fs.stat(payloadCandidate).catch(() => null);
    const payloadRoot = payloadStat?.isDirectory() ? payloadCandidate : sourceRoot;
    const sourceFiles = (await walkFiles(payloadRoot)).filter(file => !['manifest.json', 'update.json'].includes(file.toLowerCase()));
    if (!sourceFiles.length) throw new Error('update source contains no payload files');
    const rows = [];
    for (const relative of sourceFiles) {
      const destination = destinationRelative(relative);
      const source = safeRelative(relative, 'update source');
      const fullPath = safeJoin(payloadRoot, source);
      const stat = await fs.stat(fullPath);
      rows.push({ source: `payload/${source}`, destination, sizeBytes: stat.size, sha256: await sha256(fullPath) });
    }
    const packageDir = safeJoin(this.stagingRoot, id);
    if (await fs.stat(packageDir).catch(() => null)) throw new Error(`staged update already exists: ${id}`);
    await fs.mkdir(path.join(packageDir, 'payload'), { recursive: true });
    try {
      for (const row of rows) {
        const source = safeJoin(payloadRoot, row.source.slice('payload/'.length));
        const target = safeJoin(packageDir, row.source);
        await fs.mkdir(path.dirname(target), { recursive: true });
        await fs.copyFile(source, target);
      }
      await writeJsonAtomic(path.join(packageDir, 'manifest.json'), {
        schemaVersion: SCHEMA_VERSION,
        id,
        version,
        notes: text(options.notes || metadata.notes || '', 500),
        createdAt: new Date().toISOString(),
        files: rows
      });
    } catch (error) {
      await fs.rm(packageDir, { recursive: true, force: true }).catch(() => {});
      throw error;
    }
    return (await this.readManifest(id)).manifest;
  }

  async stageKesas(archive, options = {}) {
    const version = String(options.version || path.basename(archive).match(/kesas-rc[1-9][0-9]*/)?.[0] || '');
    if (!/^kesas-rc[1-9][0-9]*$/.test(version) || !archive.toLowerCase().endsWith('.zip')) throw new Error('KESAS release archive/version required');
    await execFileAsync('pwsh.exe', ['-NoProfile','-NonInteractive','-File',
      path.join(this.config.__root, 'scripts', 'Verify-KesasRelease.ps1'), '-Archive', archive, '-ExpectedVersion', version],
    { windowsHide: true, maxBuffer: 256 * 1024 });
    const id = packageId(options.id || version);
    const packageDir = safeJoin(this.stagingRoot, id);
    if (await fs.stat(packageDir).catch(() => null)) throw new Error(`staged update already exists: ${id}`);
    const name = `KESAS-Release-${version}.zip`;
    const source = `payload/${name}`;
    await fs.mkdir(path.join(packageDir, 'payload'), { recursive: true });
    await fs.copyFile(archive, safeJoin(packageDir, source));
    const stat = await fs.stat(archive);
    await writeJsonAtomic(path.join(packageDir, 'manifest.json'), {
      schemaVersion: SCHEMA_VERSION, provider: 'kesas-release', id, version,
      notes: text(options.notes || '', 500), createdAt: new Date().toISOString(),
      files: [{ source, destination: name, sizeBytes: stat.size, sha256: await sha256(archive) }]
    });
    return (await this.readManifest(id)).manifest;
  }

  async queue(id, queuedBy = 'operator', options = {}) {
    await this.load();
    const { manifest } = await this.readManifest(id);
    if (this.operation) return { ok: false, reason: 'deployment-in-progress' };
    if (options.onlyIfIdle) {
      await this.refreshFromDisk();
      if (this.operation || this.state.status !== 'idle') return { ok: false, reason: 'update-already-active' };
      if (this.state.lastDeployment?.status === 'cancelled' && this.state.lastDeployment.version === manifest.version) {
        return { ok: false, reason: 'update-was-cancelled' };
      }
      const lastVersion = this.state.lastDeployment?.version;
      if (/^kesas-rc[1-9][0-9]*$/.test(lastVersion || '') && /^kesas-rc[1-9][0-9]*$/.test(manifest.version) &&
          BigInt(lastVersion.slice(8)) >= BigInt(manifest.version.slice(8))) return { ok: false, reason: 'version-already-deployed' };
    }
    const now = Number.isFinite(Number(options.now)) ? Number(options.now) : Date.now();
    const minutes = countdownMinutes(options.countdownMinutes, this.defaultCountdownMinutes);
    const changelog = text(options.changelog || manifest.notes || '', 320);
    this.state = {
      ...initialState(),
      status: 'queued',
      packageId: manifest.id,
      version: manifest.version,
      notes: manifest.notes || '',
      queuedAt: new Date(now).toISOString(),
      queuedBy: text(queuedBy, 120),
      phase: 'countdown-pending',
      countdownStartedAt: new Date(now).toISOString(),
      deployAt: new Date(now + minutes * 60_000).toISOString(),
      countdownMinutes: minutes,
      countdownChangelog: changelog,
      countdownAnnouncements: []
    };
    await this.save();
    return { ok: true, state: await this.status() };
  }

  async cancel() {
    await this.load();
    if (this.operation || this.state.status === 'deploying') return { ok: false, reason: 'deployment-in-progress' };
    if (!ACTIVE_STATES.has(this.state.status) || !this.state.packageId) return { ok: false, reason: 'no-update-queued' };
    const cancelled = { id: this.state.packageId, version: this.state.version, cancelledAt: new Date().toISOString() };
    this.state = { ...initialState(), lastDeployment: { ...cancelled, status: 'cancelled' } };
    await this.save();
    return { ok: true, state: await this.status() };
  }

  async force() {
    await this.load();
    if (this.operation || this.state.status === 'deploying') return { ok: false, reason: 'deployment-in-progress' };
    if (this.state.status !== 'queued' || !this.state.packageId) return { ok: false, reason: 'no-update-queued' };
    this.state.force = true;
    this.state.phase = 'force-deploy-armed';
    await this.save();
    return { ok: true, state: await this.status() };
  }

  async reconcile(status, hooks = {}, now = Date.now()) {
    await this.refreshFromDisk();
    if (this.state.status !== 'queued' || !this.state.packageId) return { ok: true, idle: true, state: await this.status() };
    const count = status?.playerCount == null ? NaN : Number(status.playerCount);
    const connected = status?.connectedCount == null ? NaN : Number(status.connectedCount);
    this.lastObservation = {
      at: new Date(now).toISOString(),
      online: status?.online === true,
      playerCount: Number.isFinite(count) ? count : null,
      connectedCount: Number.isFinite(connected) ? connected : null
    };
    let deployAt = Date.parse(this.state.deployAt || '');
    if (!Number.isFinite(deployAt)) {
      this.state.phase = 'awaiting-countdown';
      return { ok: false, deferred: true, reason: 'countdown-not-scheduled', state: await this.status() };
    }

    const empty = Number.isFinite(count) && count === 0 && Number.isFinite(connected) && connected === 0;
    if (this.config.lmp?.deploymentRoot && !empty && !this.state.force &&
        Number.isFinite(count) && Number.isFinite(connected) &&
        !this.state.countdownAnnouncements?.length) {
      // Give connected players the full notice period. Time spent waiting for
      // the bot/bridge before its first successful warning must not consume it.
      this.state.countdownStartedAt = new Date(now).toISOString();
      deployAt = now + Number(this.state.countdownMinutes || this.defaultCountdownMinutes) * 60_000;
      this.state.deployAt = new Date(deployAt).toISOString();
      await this.save();
    }
    if (empty) {
      await this.notifyAnnouncement(hooks, `Server is empty; restarting now for update ${this.state.version}.`);
    } else {
      await this.announceCountdown(now, hooks);
    }
    if (!empty && now >= deployAt && !this.state.force) {
      if (!Number.isFinite(count) || !Number.isFinite(connected)) return { ok: false, deferred: true, reason: 'live-player-count-unknown' };
      await this.notifyAnnouncement(hooks, `Server restarting now for update ${this.state.version}.`);
      this.state.force = true;
      this.state.phase = 'countdown-complete';
      await this.save();
    }
    // An offline server cannot contain active players. Treat an unexpected
    // crash as the safest deployment window: install the already-verified
    // queued package before relaunching instead of restoring the old binary.
    if (!this.state.force && (!Number.isFinite(count) || count !== 0)) return { ok: false, deferred: true, reason: Number.isFinite(count) ? 'players-online' : 'player-count-unknown', players: Number.isFinite(count) ? count : null, state: await this.status() };
    if (!this.state.force && (!Number.isFinite(connected) || connected !== 0)) return { ok: false, deferred: true, reason: Number.isFinite(connected) ? 'connections-online' : 'connection-count-unknown', connections: Number.isFinite(connected) ? connected : null, state: await this.status() };
    if (this.operation) return { ok: false, deferred: true, reason: 'deployment-in-progress', state: await this.status() };
    this.operation = this.deploy(status, hooks);
    try { return await this.operation; }
    finally { this.operation = null; }
  }

  async notify(hooks, message) {
    if (typeof hooks.onProgress === 'function') await hooks.onProgress(message, await this.status());
  }

  async notifyAnnouncement(hooks, message) {
    if (typeof hooks.onAnnouncement === 'function') await hooks.onAnnouncement(message, await this.status());
  }

  async announceCountdown(now, hooks) {
    const deployAt = Date.parse(this.state.deployAt || '');
    if (!Number.isFinite(deployAt) || now >= deployAt) return;

    const durationMs = Math.max(60_000, Number(this.state.countdownMinutes || this.defaultCountdownMinutes) * 60_000);
    const remainingMs = deployAt - now;
    const sent = new Set(Array.isArray(this.state.countdownAnnouncements) ? this.state.countdownAnnouncements.map(Number) : []);
    const crossed = COUNTDOWN_MARKS_MS.filter(mark => mark <= durationMs && remainingMs <= mark && !sent.has(mark));
    if (!crossed.length) return;

    // If monitor resumes late, emit only nearest useful warning and mark older
    // thresholds consumed. Avoid a burst of obsolete 15/10/5 minute notices.
    const selected = Math.min(...crossed);
    for (const mark of crossed) sent.add(mark);
    const changes = text(this.state.countdownChangelog || this.state.notes || '', 240);
    const suffix = changes ? ` Changes: ${changes}` : '';
    await this.notifyAnnouncement(hooks, `Server will restart in ${countdownLabel(selected)} for update ${this.state.version}. It will restart immediately if no players remain.${suffix}`);
    this.state.countdownAnnouncements = [...sent].sort((a, b) => b - a);
    this.state.phase = `countdown-${selected}`;
    await this.save();
  }

  async setPhase(phase, hooks, message = '') {
    this.state.phase = phase;
    await this.save();
    if (message) await this.notify(hooks, message);
  }

  async deploy(status, hooks) {
    const packageIdValue = this.state.packageId;
    const { packageDir, manifest } = await this.readManifest(packageIdValue);
    let applied = null;
    let stopped = false;
    const kesas = manifest.provider === 'kesas-release';
    try {
      this.state.status = 'deploying';
      this.state.error = null;
      await this.setPhase('preparing', hooks, `Update **${manifest.version}** is deploying${this.state.force ? ' (forced)' : ''}.`);
      if (this.requirePreDeployBackup && this.universe?.createBackup) {
        await this.setPhase('backup', hooks, 'Creating pre-deploy universe backup.');
        const backup = await this.universe.createBackup('pre-deploy-update');
        if (backup?.skipped) throw new Error('pre-deploy backup was skipped because another backup is active');
      }
      // Backup creation is intentionally done while LMP is still online. A new
      // socket can arrive during that window, so re-check immediately before
      // stopping and return to the queue if any player/connection appeared.
      if (!this.state.force) {
        const gate = await this.controller.status();
        const players = gate?.playerCount == null ? NaN : Number(gate.playerCount);
        const connections = gate?.connectedCount == null ? NaN : Number(gate.connectedCount);
        if (gate?.online === true && (!Number.isFinite(players) || players !== 0 || !Number.isFinite(connections) || connections !== 0)) {
          this.state.status = 'queued';
          this.state.phase = 'waiting-for-empty-server';
          await this.save();
          await this.notify(hooks, 'Update returned to the queue because player activity resumed during backup.');
          return { ok: false, deferred: true, reason: 'activity-resumed', state: await this.status() };
        }
      }
      if (kesas) {
        if (!this.config.lmp?.deploymentRoot || manifest.files.length !== 1) throw new Error('invalid KESAS release deployment');
        const row = manifest.files[0];
        const archive = safeJoin(packageDir, row.source);
        if ((await sha256(archive)) !== row.sha256) throw new Error('KESAS release hash changed after staging');
        await this.setPhase('installing', hooks, 'Publishing KESAS update.');
        applied = await this.controller.publishRelease(archive, manifest.notes || '');
        await this.setPhase('starting', hooks, 'Waiting for KESAS to start.');
        await this.waitForReady();
      } else {
      const process = await this.controller.processInfo();
      if (process) {
        await this.setPhase('stopping', hooks, 'No players online; stopping LMP for update.');
        const result = await this.controller.stop();
        if (!result?.ok) throw new Error(`server stop failed: ${result?.reason || 'unknown'}`);
        stopped = true;
        await this.waitForOffline();
      }
      await this.setPhase('installing', hooks, 'Installing verified update files.');
      applied = await this.applyManifest(packageDir, manifest);
      await this.setPhase('starting', hooks, 'Starting updated LMP server.');
      const start = await this.controller.startExternal();
      if (!start?.ok) throw new Error(`server start failed: ${start?.reason || 'unknown'}`);
      await this.waitForReady();
      }
      const deployedAt = new Date().toISOString();
      this.state = {
        ...initialState(),
        lastDeployment: { id: manifest.id, version: manifest.version, deployedAt, notes: manifest.notes || '', rollbackRoot: applied.rollbackRoot }
      };
      await this.save();
      await this.notify(hooks, `Update **${manifest.version}** deployed; server is healthy.`);
      if (typeof hooks.onDeployed === 'function') {
        await hooks.onDeployed(`Server is back online. Update ${manifest.version} deployed successfully.`, await this.status());
      }
      return { ok: true, state: await this.status(), manifest };
    } catch (error) {
      const reason = text(error?.message || error, 500) || 'deployment failed';
      let recoveryError = '';
      try {
        if (!kesas) {
        // A server can keep its process alive after a fatal startup error. Stop
        // that process before replacing loaded assemblies, then verify the
        // restored server reaches readiness instead of silently leaving it down.
        if (applied) {
          const failedProcess = await this.controller.processInfo();
          if (failedProcess) {
            const stop = await this.controller.stop();
            if (!stop?.ok) throw new Error(`failed update process did not stop: ${stop?.reason || 'unknown'}`);
            await this.waitForOffline();
          }
          await this.rollback(applied);
        }
        if (!(await this.controller.processInfo())) {
          const recoveryStart = await this.controller.startExternal();
          if (!recoveryStart?.ok) throw new Error(`rollback server start failed: ${recoveryStart?.reason || 'unknown'}`);
          if (stopped || applied) await this.waitForReady();
        }
        }
      } catch (recoveryFailure) {
        recoveryError = text(recoveryFailure?.message || recoveryFailure, 300) || 'unknown recovery failure';
      }
      this.state.status = 'failed';
      this.state.phase = 'failed';
      this.state.error = recoveryError ? `${reason}; recovery failed: ${recoveryError}` : reason;
      await this.save();
      await this.notify(hooks, `Update **${this.state.version}** failed: ${this.state.error}. Automatic retry disabled.`);
      return { ok: false, reason: this.state.error, state: await this.status() };
    }
  }

  async waitForOffline() {
    const deadline = Date.now() + this.deploymentTimeoutMs;
    while (Date.now() < deadline) {
      if (!(await this.controller.processInfo())) return true;
      await new Promise(resolve => setTimeout(resolve, this.pollMs));
    }
    throw new Error('server did not stop before deployment timeout');
  }

  async waitForReady() {
    const deadline = Date.now() + this.deploymentTimeoutMs;
    while (Date.now() < deadline) {
      const status = await this.controller.status();
      if (status?.online === true && status.ready === true && (!this.config.lmp?.deploymentRoot || status.bridge?.ready === true)) return status;
      await new Promise(resolve => setTimeout(resolve, this.pollMs));
    }
    throw new Error('updated server did not become ready before deployment timeout');
  }

  async applyManifest(packageDir, manifest) {
    const rollbackRoot = path.join(this.rollbackRoot, `${manifest.id}-${Date.now()}`);
    const changed = [];
    const serverRoot = this.controller.root;
    try {
      for (const row of manifest.files) {
        const source = safeJoin(packageDir, row.source);
        const sourceStat = await fs.stat(source).catch(() => null);
        if (!sourceStat?.isFile() || sourceStat.size !== Number(row.sizeBytes) || (await sha256(source)).toLowerCase() !== String(row.sha256).toLowerCase()) throw new Error(`staged file verification failed: ${row.source}`);
        const destination = safeJoin(serverRoot, row.destination);
        const existing = await fs.stat(destination).catch(() => null);
        const backup = existing?.isFile() ? path.join(rollbackRoot, ...row.destination.split('/')) : null;
        const record = { destination, backup, existed: Boolean(backup) };
        changed.push(record);
        if (backup) { await fs.mkdir(path.dirname(backup), { recursive: true }); await fs.copyFile(destination, backup); }
        await fs.mkdir(path.dirname(destination), { recursive: true });
        const temporary = `${destination}.apf-update-${process.pid}-${Date.now()}-${changed.length}.tmp`;
        await fs.copyFile(source, temporary);
        await fs.rm(destination, { force: true });
        await fs.rename(temporary, destination);
      }
      return { rollbackRoot, changed };
    } catch (error) {
      await this.rollback({ rollbackRoot, changed }).catch(() => {});
      throw error;
    }
  }

  async rollback(applied) {
    for (const record of [...(applied?.changed || [])].reverse()) {
      if (record.existed) {
        const temporary = `${record.destination}.apf-rollback-${process.pid}-${Date.now()}.tmp`;
        await fs.copyFile(record.backup, temporary);
        await fs.rm(record.destination, { force: true });
        await fs.rename(temporary, record.destination);
      } else await fs.rm(record.destination, { force: true });
    }
  }
}
