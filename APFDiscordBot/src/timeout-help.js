const DEFAULT_TIMEOUT_MIN_SECONDS = 14.5;
const DEFAULT_TIMEOUT_MAX_SECONDS = 30;
const DEFAULT_RECONNECT_CONFIRMATION_MS = 5 * 60 * 1000;

function playerKey(name) {
  return String(name || '').trim().toLocaleLowerCase('en-US');
}

export class TimeoutAdviceTracker {
  constructor(windowMs = DEFAULT_RECONNECT_CONFIRMATION_MS, now = () => Date.now()) {
    this.windowMs = Math.max(1, Number(windowMs) || DEFAULT_RECONNECT_CONFIRMATION_MS);
    this.now = now;
    this.pending = new Map();
  }

  prune() {
    const cutoff = this.now() - this.windowMs;
    for (const [key, entry] of this.pending) {
      if (entry.recordedAt < cutoff) this.pending.delete(key);
    }
  }

  record(timeout) {
    this.prune();
    const key = playerKey(timeout?.name);
    if (!key) return false;
    this.pending.set(key, { timeout, recordedAt: this.now() });
    return true;
  }

  confirmReconnect(name) {
    this.prune();
    const key = playerKey(name);
    const entry = this.pending.get(key);
    if (!entry) return null;
    this.pending.delete(key);
    return entry.timeout;
  }
}

function numberFrom(line, pattern) {
  const match = String(line || '').match(pattern);
  return match ? Number(match[1]) : null;
}

export function parseNetworkTimeoutLine(line) {
  const text = String(line || '');
  const header = text.match(/\]\[Warning\]: Network timeout for (.+?) at (.+?);/i);
  if (!header) return null;

  const flow = text.match(/failures\/drops\/backpressure\/coalesced\/throttled\s+(\d+)\/(\d+)\/(\d+)\/(\d+)\/(\d+)/i);
  const originMatch = text.match(/origin\s+(server-deadline|remote-disconnect|local-server-request|unknown)/i);
  const legacyRemote = /remote client initiated this disconnect/i.test(text);
  return {
    name: header[1].trim(),
    averageRttMs: numberFrom(text, /average RTT\s+(\d+)\s+ms/i),
    queueMessages: numberFrom(text, /queue\s+(\d+)\s+messages\//i),
    pendingVesselMessages: numberFrom(text, /Lidgren vessel pending\s+(\d+)/i),
    pendingFlagMessages: numberFrom(text, /Lidgren flag pending\s+(\d+)/i),
    heartbeatAgeSeconds: numberFrom(text, /heartbeat age\s+([0-9.]+)s/i),
    maxHeartbeatAgeSeconds: numberFrom(text, /heartbeat age\s+[0-9.]+s\s+\(high\s+([0-9.]+)s\)/i),
    inboundPacketAgeSeconds: numberFrom(text, /inbound packet age\s+([0-9.]+)s/i),
    applicationMessageAgeSeconds: numberFrom(text, /application message age\s+([0-9.]+)s/i),
    networkLoopAgeSeconds: numberFrom(text, /server network loop age\s+([0-9.]+)s/i),
    networkLoopLastGapSeconds: numberFrom(text, /last gap\s+([0-9.]+)s/i),
    networkLoopClientHighSeconds: numberFrom(text, /client high\s+([0-9.]+)s/i),
    sendFailures: flow ? Number(flow[1]) : null,
    queueDrops: flow ? Number(flow[2]) : null,
    queueBackpressure: flow ? Number(flow[3]) : null,
    origin: originMatch ? originMatch[1].toLowerCase() : (legacyRemote ? 'remote-disconnect' : 'unknown'),
    remoteInitiated: originMatch ? originMatch[1].toLowerCase() === 'remote-disconnect' : legacyRemote
  };
}

export function timeoutGapSeconds(timeout) {
  // Current heartbeat age is the server's direct connection-liveness measurement. Inbound
  // packet age can be reset by late UDP traffic and the historical maximum is not this event.
  for (const candidate of [timeout?.heartbeatAgeSeconds, timeout?.applicationMessageAgeSeconds,
    timeout?.inboundPacketAgeSeconds]) {
    if (candidate === null || candidate === undefined || candidate === '') continue;
    const value = Number(candidate);
    if (Number.isFinite(value)) return value;
  }
  return null;
}

export function matchesDefaultClientTimeout(timeout) {
  const gap = timeoutGapSeconds(timeout);
  return gap !== null && gap >= DEFAULT_TIMEOUT_MIN_SECONDS && gap <= DEFAULT_TIMEOUT_MAX_SECONDS;
}

export function likelyClientSideTimeout(timeout) {
  const gap = timeoutGapSeconds(timeout);
  const loopHealthy = Number(timeout?.networkLoopAgeSeconds || 0) < 1 &&
    Number(timeout?.networkLoopLastGapSeconds || 0) < 1;
  return timeout?.origin === 'remote-disconnect' && loopHealthy &&
    gap !== null && gap >= DEFAULT_TIMEOUT_MIN_SECONDS &&
    timeout?.queueMessages === 0 && timeout?.pendingVesselMessages === 0 &&
    Number(timeout?.pendingFlagMessages ?? 0) === 0 &&
    timeout?.queueDrops === 0 && timeout?.queueBackpressure === 0;
}

export function clientTimeoutAdvice(timeout, operatorName = 'Eso') {
  const gap = timeoutGapSeconds(timeout);
  const gapText = gap === null ? 'an unknown period' : `about **${gap.toFixed(1)} seconds**`;
  const rttText = Number.isFinite(Number(timeout?.averageRttMs)) ? `; RTT **${Number(timeout.averageRttMs)} ms**` : '';
  const likelyClient = likelyClientSideTimeout(timeout);
  const defaultTimeout = likelyClient && matchesDefaultClientTimeout(timeout);
  const loopStalled = Number(timeout?.networkLoopAgeSeconds || 0) >= 1 ||
    Number(timeout?.networkLoopLastGapSeconds || 0) >= 1;
  let diagnosis;
  if (likelyClient) {
    diagnosis = `Client-reported timeout: **${timeout.name}** reported receiving no server traffic for ${gapText}${rttText} while the server queue and network loop were healthy.`;
  } else if (timeout?.origin === 'server-deadline' && loopStalled) {
    diagnosis = `Server timeout deadline: **${timeout.name}** stopped sending packets for ${gapText}${rttText}; the server network loop also stalled.`;
  } else if (timeout?.origin === 'server-deadline') {
    diagnosis = `Server timeout deadline: **${timeout.name}** stopped sending packets for ${gapText}${rttText}. No server queue or network-loop stall was detected.`;
  } else {
    diagnosis = timeout?.origin === 'remote-disconnect'
      ? `Client-reported timeout: **${timeout.name}** reported receiving no server traffic for ${gapText}${rttText}. Server metrics do not clearly identify the cause.`
      : `Connection timeout details: **${timeout.name}** had no heartbeat for ${gapText}${rttText}. Server metrics do not clearly identify the cause.`;
  }
  const fix = defaultTimeout
    ? " This matches LMP's default 15-second timeout.\n\nFix:\nGo to:\n`GameData\\LunaMultiplayer\\Data\\settings.xml`\n\nChange `TimeoutSeconds` from `15` to `60`."
    : '';
  const supportName = String(operatorName || 'Eso').replaceAll('@', '').replaceAll('`', "'").slice(0, 40) || 'Eso';

  return `${diagnosis}${fix}\n\nIf you believe this timeout was caused by your client, ping **${supportName}** with:\n` +
    '- `KSP.log`\n' +
    '- `Logs\\LMP\\VesselSyncLog.txt`\n' +
    '- `GameData\\LunaMultiplayer\\Data\\settings.xml`';
}
