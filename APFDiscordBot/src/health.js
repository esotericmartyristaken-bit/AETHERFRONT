const ISSUE_EVENT = Object.freeze({
  'server-offline': { enter: 'server-offline', exit: 'server-online', enterMessage: 'LMP server went offline.', exitMessage: 'LMP server is online again.' },
  'startup-readiness': { enter: 'readiness-lost', exit: 'readiness-restored', enterMessage: 'LMP startup readiness is not confirmed.', exitMessage: 'LMP startup readiness confirmed.' },
  'master-registration': { enter: 'registration-lost', exit: 'registration-restored', enterMessage: 'LMP master registration is not confirmed.', exitMessage: 'LMP master registration confirmed.' },
  'master-listing': { enter: 'master-lost', exit: 'master-visible', enterMessage: 'LMP server disappeared from the master listing.', exitMessage: 'LMP server is visible in the master listing again.' },
  'udp-bind': { enter: 'udp-bind-lost', exit: 'udp-bind-restored', enterMessage: 'LMP UDP port is no longer bound by the server.', exitMessage: 'LMP UDP port is bound again.' },
  'udp-owner': { enter: 'udp-owner-lost', exit: 'udp-owner-restored', enterMessage: 'LMP UDP port is owned by another process.', exitMessage: 'LMP UDP port ownership returned to LMP.' },
  'bridge': { enter: 'bridge-lost', exit: 'bridge-ready', enterMessage: 'APF Discord bridge is no longer ready.', exitMessage: 'APF Discord bridge is ready.' },
  'disk-low': { enter: 'disk-low', exit: 'disk-restored', enterMessage: 'Disk free space is below the configured threshold.', exitMessage: 'Disk free space returned above the configured threshold.' },
  'memory-high': { enter: 'memory-high', exit: 'memory-restored', enterMessage: 'LMP working-set memory is above the configured threshold.', exitMessage: 'LMP working-set memory returned below the configured threshold.' }
});

const LEGACY_WARNING = [
  [/startup readiness/i, 'startup-readiness'],
  [/master registration/i, 'master-registration'],
  [/not visible in master listing/i, 'master-listing'],
  [/UDP .* is not bound/i, 'udp-bind'],
  [/UDP .* is owned by another process/i, 'udp-owner'],
  [/bridge .*not ready/i, 'bridge'],
  [/disk free below/i, 'disk-low'],
  [/working set above/i, 'memory-high']
];

function issueMessage(code, source = '') {
  const definition = ISSUE_EVENT[code];
  return String(source || definition?.enterMessage || code);
}

function issueSeverity(code, source = '') {
  if (code === 'server-offline' || code === 'udp-bind' || code === 'udp-owner') return 'critical';
  return source?.severity || 'warning';
}

export function healthIssues(status) {
  const issues = new Map();
  const add = (code, source = '') => {
    if (!ISSUE_EVENT[code] || issues.has(code)) return;
    const raw = typeof source === 'object' ? source.message : source;
    issues.set(code, { code, severity: issueSeverity(code, source), message: issueMessage(code, raw) });
  };

  if (!status || status.online !== true) {
    add('server-offline', 'server offline');
    return [...issues.values()];
  }

  if (Array.isArray(status.healthIssues)) {
    for (const issue of status.healthIssues) add(issue?.code, issue);
  } else {
    for (const warning of status.healthWarnings || []) {
      const match = LEGACY_WARNING.find(([regex]) => regex.test(String(warning)));
      if (match) add(match[1], warning);
    }
  }
  if (status.master?.checked && status.master.visible === false && status.master.confirmedMissing !== false) add('master-listing', 'not visible in master listing');
  if (status.udpBinding?.checked && !status.udpBinding.bound) add('udp-bind', 'UDP is not bound');
  if (status.udpBinding?.checked && status.udpBinding.bound && !status.udpBinding.ownedByProcess) add('udp-owner', 'UDP is owned by another process');
  if (status.bridge?.configured && !status.bridge.ready) add('bridge', 'bridge not ready');
  return [...issues.values()];
}

export function statusPollDelay(status, update, activeMs = 5000, idleMs = 30000) {
  const active = Math.max(1000, Number(activeMs) || 5000);
  const idle = Math.max(active, Number(idleMs) || 30000);
  const emptyAndHealthy = status?.online === true && status?.ready === true &&
    Number(status?.playerCount) === 0 && Number(status?.connectedCount) === 0;
  return emptyAndHealthy && update?.status === 'idle' ? idle : active;
}

export function isActionableLmpLogLine(line) {
  const text = String(line || '');
  // Chat is user content; words like "error" in chat are not server faults.
  if (/\]\[(?:Chat)\]:/i.test(text) || /\]\[LMP\]:\s*Broadcasting\b/i.test(text)) return false;
  // Luna emits this during ordinary client disconnect/lock cleanup.
  if (/failed to release lock/i.test(text)) return false;
  if (/Connection (?:timed out|end message)/i.test(text) && !/Failed mod validation/i.test(text)) return false;
  if (/Player: .* not found/i.test(text)) return false;
  // CraftCreationAndRemoval.txt is intentionally held open for the lifetime of
  // the server. Older retention code periodically tries to expire that active
  // file and logs a harmless sharing violation until the server-side fix is
  // deployed. Do not turn that known maintenance race into Discord alerts.
  if (/Exception while trying to delete .*CraftCreationAndRemoval\.txt.*being used by another process/i.test(text)) return false;
  if (/Cannot save flag .* invalid characters/i.test(text)) return true;
  if (/Failed mod validation/i.test(text)) return true;
  return /\]\[(?:Error|Exception|Fatal)\]:|\b(?:unhandled exception|out of memory|outofmemory|crash|abort)\b/i.test(text);
}

export function logAlertFingerprint(line) {
  return String(line || '')
    .replace(/^\[\d{2}:\d{2}:\d{2}\]/, '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

/**
 * Coalesces recurring server faults so one exception storm produces one Discord
 * alert instead of one message per GC/finalizer batch. A still-active fault may
 * alert again after cooldown; that message includes the number suppressed.
 */
export class LogAlertDeduper {
  constructor(cooldownMs = 5 * 60 * 1000, maxEntries = 256) {
    this.cooldownMs = Math.max(1000, Number(cooldownMs) || 5 * 60 * 1000);
    this.maxEntries = Math.max(16, Number(maxEntries) || 256);
    this.recent = new Map();
  }

  filter(lines, now = Date.now()) {
    const output = [];
    const fingerprintsInBatch = new Set();

    for (const line of lines || []) {
      const fingerprint = logAlertFingerprint(line);
      if (!fingerprint || fingerprintsInBatch.has(fingerprint)) continue;
      fingerprintsInBatch.add(fingerprint);

      const previous = this.recent.get(fingerprint);
      if (previous && now - previous.lastSentAt < this.cooldownMs) {
        previous.suppressed += 1;
        previous.lastSeenAt = now;
        continue;
      }

      const suppressed = previous?.suppressed || 0;
      output.push(suppressed ? `${line} [${suppressed} repeat alert(s) suppressed]` : line);
      this.recent.set(fingerprint, { lastSentAt: now, lastSeenAt: now, suppressed: 0 });
    }

    for (const [fingerprint, state] of this.recent) {
      if (now - state.lastSeenAt > this.cooldownMs * 4) this.recent.delete(fingerprint);
    }
    while (this.recent.size > this.maxEntries) this.recent.delete(this.recent.keys().next().value);

    return output;
  }
}

export function healthKey(status) {
  return healthIssues(status).map(issue => issue.code).join('|');
}

export function healthTransitions(previous, current) {
  if (!previous || !current) return [];
  const before = new Map(healthIssues(previous).map(issue => [issue.code, issue]));
  const after = new Map(healthIssues(current).map(issue => [issue.code, issue]));
  if (before.has('server-offline') && !after.has('server-offline')) {
    return [{ type: ISSUE_EVENT['server-offline'].exit, message: ISSUE_EVENT['server-offline'].exitMessage }];
  }
  if (!before.has('server-offline') && after.has('server-offline')) {
    return [{ type: ISSUE_EVENT['server-offline'].enter, message: ISSUE_EVENT['server-offline'].enterMessage }];
  }
  const rows = [];
  for (const code of after.keys()) {
    if (!before.has(code)) rows.push({ type: ISSUE_EVENT[code].enter, message: ISSUE_EVENT[code].enterMessage });
  }
  for (const code of before.keys()) {
    if (!after.has(code)) rows.push({ type: ISSUE_EVENT[code].exit, message: ISSUE_EVENT[code].exitMessage });
  }
  return rows;
}

export class HealthMonitor {
  constructor(options = {}) {
    this.failureConfirmations = Math.max(1, Number(options.failureConfirmations) || 2);
    this.recoveryConfirmations = Math.max(1, Number(options.recoveryConfirmations) || 2);
    this.stableStatus = null;
    this.candidateKey = null;
    this.candidateStatus = null;
    this.candidateCount = 0;
  }

  update(status) {
    if (!status) return [];
    if (!this.stableStatus) { this.stableStatus = status; return []; }
    const currentKey = healthKey(status);
    if (currentKey === healthKey(this.stableStatus)) {
      this.candidateKey = null; this.candidateStatus = null; this.candidateCount = 0;
      return [];
    }
    if (this.candidateKey === currentKey) this.candidateCount += 1;
    else { this.candidateKey = currentKey; this.candidateStatus = status; this.candidateCount = 1; }
    const recovering = healthIssues(status).length < healthIssues(this.stableStatus).length;
    const required = recovering ? this.recoveryConfirmations : this.failureConfirmations;
    if (this.candidateCount < required) return [];
    const previous = this.stableStatus;
    this.stableStatus = this.candidateStatus;
    this.candidateKey = null; this.candidateStatus = null; this.candidateCount = 0;
    return healthTransitions(previous, this.stableStatus);
  }
}

export function backupStale(latestCreatedAt, intervalMinutes, now = Date.now()) {
  const intervalMs = Number(intervalMinutes) * 60 * 1000;
  if (!Number.isFinite(intervalMs) || intervalMs <= 0) return null;
  const created = Date.parse(latestCreatedAt || '');
  return !Number.isFinite(created) || now - created > intervalMs * 2;
}
