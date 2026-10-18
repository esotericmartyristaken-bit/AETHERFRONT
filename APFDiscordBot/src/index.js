import path from 'node:path';
import { DiscordBot } from './bot.js';
import { loadConfig } from './config.js';
import { BridgeEventTailer, LmpController, LogTailer, shortLeaveReason } from './lmp.js';
import { UniverseManager } from './universe.js';
import { UpdateManager } from './update-manager.js';
import { GitHubDeployment } from './github-deployment.js';
import { HealthMonitor, LogAlertDeduper, backupStale, isActionableLmpLogLine, statusPollDelay } from './health.js';
import { acquireSingleInstance } from './single-instance.js';
import { clientTimeoutAdvice, likelyClientSideTimeout, matchesDefaultClientTimeout, timeoutGapSeconds } from './timeout-help.js';

const releaseInstance = acquireSingleInstance(path.resolve('state', 'bot-instance.lock'));
if (!releaseInstance) {
  console.log('APF Discord Bot already running; duplicate launcher exiting.');
  process.exit(0);
}

const config = await loadConfig();
const controller = new LmpController(config);
const universe = new UniverseManager(config);
const updateManager = new UpdateManager(config, controller, universe);
await updateManager.load();
const tailer = new LogTailer(controller);
const bridgeTailer = new BridgeEventTailer(controller);
const bot = new DiscordBot(config, controller, universe, updateManager);
const githubDeployment = new GitHubDeployment(config, updateManager, {
  onEvent: event => bot.recordEvent('github-deployment', event)
});

let pendingErrors = [];
let errorTimer = null;
const logAlertDeduper = new LogAlertDeduper(config.health?.logAlertCooldownMs);
let lunaSystemQueue = Promise.resolve();
function queueLunaSystemMessage(content, options = {}) {
  lunaSystemQueue = lunaSystemQueue.catch(() => {}).then(() => bot.sendLunaChat(content, options));
  return lunaSystemQueue;
}
// Server chat log records each accepted message once. Bridge callbacks can
// produce multiple event files for the same message, so do not relay both.
// Retain/retry log chat across short Discord outages.
tailer.on('chat', chat => bot.mirrorChat(chat, { retainOnFailure: true }));
tailer.on('player', player => {
  const name = String(player.name || 'unknown').replaceAll('@', '@ ').replaceAll('`', "'").slice(0, 80);
  const detail = player.type === 'join' ? `KSP ${player.kspVersion} · LMP ${player.lmpVersion}` : shortLeaveReason(player.reason);
  bot.recordEvent(`player-${player.type}`, { name, id: player.id || null, detail }).catch(() => {});
  const presence = `**${name}** ${player.type === 'join' ? 'joined' : 'left'} · ${detail}`;
  bot.sendConsole(presence).catch(() => {});
  queueLunaSystemMessage(presence).catch(() => {});
});
tailer.on('timeout', timeout => {
  const name = String(timeout.name || 'unknown').replaceAll('@', '@ ').replaceAll('`', "'").slice(0, 80);
  const event = { ...timeout, name };
  bot.recordEvent('client-timeout-diagnostic', {
    name,
    heartbeatGapSeconds: timeoutGapSeconds(event),
    averageRttMs: event.averageRttMs,
    likelyClientSide: likelyClientSideTimeout(event),
    defaultTimeoutLikely: matchesDefaultClientTimeout(event),
    queueMessages: event.queueMessages,
    pendingVesselMessages: event.pendingVesselMessages
  }).catch(() => {});
  // The server already has a definitive Lidgren timeout event here. Waiting
  // for the player to reconnect hid the useful details during the outage and
  // made them appear to be caused by the later reconnect/exit action.
  const supportName = String(config.discord?.timeoutSupportName || 'Eso');
  bot.recordEvent('client-timeout-advice', { name, reason: 'timeout-diagnostic-received' }).catch(() => {});
  queueLunaSystemMessage(clientTimeoutAdvice(event, supportName)).catch(() => {});
});
tailer.on('line', line => {
  if (!isActionableLmpLogLine(line)) return;
  if (!pendingErrors.includes(line)) pendingErrors.push(line);
  if (errorTimer) return;
  errorTimer = setTimeout(async () => {
    const batch = logAlertDeduper.filter(pendingErrors.splice(0, pendingErrors.length));
    errorTimer = null;
    if (!batch.length) return;
    await bot.recordEvent('lmp-log-alert', { lines: batch.slice(-12) });
    await bot.sendConsole(`LMP log alert:\n\`\`\`text\n${batch.slice(-12).join('\n')}\n\`\`\``).catch(() => {});
  }, 1500);
});
tailer.on('error', error => bot.reportError(error));
bridgeTailer.on('error', error => bot.reportError(error));

controller.on('output', text => {
  if (/(fatal|exception|error|crash)/i.test(text)) bot.sendConsole(`LMP output:\n\`\`\`text\n${text.slice(-1700)}\n\`\`\``).catch(() => {});
});

await bot.start();
await tailer.start();
await bridgeTailer.start();
githubDeployment.start();

let previousBackupStale = null;
let statusCheckBusy = false;
const healthMonitor = new HealthMonitor(config.health || {});
async function monitorStatus() {
  if (statusCheckBusy) return;
  statusCheckBusy = true;
  try {
    const status = await controller.status();
    bot.updatePresence(status);
    // Publish observed process state first. Dashboard, health, backup, or update
    // work must never leave the public card stale during an outage.
    const initialUpdate = await updateManager.status();
    bot.syncPublicStatus(status, null, { update: initialUpdate }).catch(error => bot.reportError(error));
    for (const event of healthMonitor.update(status)) {
      await bot.recordEvent(event.type, { summary: status.healthSummary });
      await bot.sendConsole(event.message).catch(() => {});
    }
    const updateResult = await updateManager.reconcile(status, {
      onProgress: async (message, update) => {
        await bot.sendConsole(message).catch(() => {});
        // Never reuse the pre-deployment snapshot: stop/install/start phases
        // change process state while reconcile() is still running.
        const current = await controller.status();
        bot.syncPublicStatus(current, null, { update: update || await updateManager.status() }).catch(error => bot.reportError(error));
      },
      onAnnouncement: async (message, update) => {
        const broadcast = await controller.say(message).catch(async error => {
          await bot.reportError(error);
          return { ok: false };
        });
        if (config.lmp.deploymentRoot && !broadcast.ok && (update?.lastObservation?.connectedCount ?? 1) > 0) {
          throw new Error('Restart warning did not reach the game; countdown is waiting for the bridge.');
        }
        // Update broadcasts are deliberately echo-suppressed by the chat bridge
        // to prevent Discord -> LMP -> Discord bounceback. Publish the same text
        // directly to luna-chat exactly once regardless of LMP delivery result.
        await bot.sendLunaChat(message).catch(() => {});
        await bot.sendPublicStatusAnnouncement(message).catch(error => bot.reportError(error));
        await bot.sendConsole(message).catch(() => {});
        const current = await controller.status();
        bot.syncPublicStatus(current, null, { update: update || await updateManager.status() }).catch(error => bot.reportError(error));
      },
      onDeployed: async (message, update) => {
        const broadcast = await controller.say(message).catch(async error => {
          await bot.reportError(error);
          return { ok: false };
        });
        await bot.sendLunaChat(message).catch(() => {});
        await bot.sendPublicStatusAnnouncement(message).catch(error => bot.reportError(error));
        const current = await controller.status();
        bot.syncPublicStatus(current, null, { update }).catch(error => bot.reportError(error));
      }
    });
    const displayStatus = updateResult && !updateResult.deferred && !updateResult.idle ? await controller.status() : status;
    const [backupRows, universeStats, finalUpdate, backupInProgress] = await Promise.all([
      universe.listBackups(),
      universe.stats(),
      updateManager.status(),
      universe.backupInProgress()
    ]);
    // A scheduled snapshot owns .backup.lock through final rename and pruning.
    // Keep the previous health state during that atomic replacement window.
    const stale = backupInProgress
      ? previousBackupStale
      : backupStale(backupRows[0]?.createdAt, config.universe.autoBackupIntervalMinutes || 0);
    if (!backupInProgress && stale !== null && ((previousBackupStale !== null && stale !== previousBackupStale) || (previousBackupStale === null && stale))) {
      const event = stale
        ? { type: 'backup-stale', message: 'Latest APF universe backup is missing or stale.' }
        : { type: 'backup-current', message: 'APF universe backups are current again.' };
      await bot.recordEvent(event.type, { latestBackup: backupRows[0]?.id || null });
      await bot.sendConsole(event.message).catch(() => {});
    }
    if (!backupInProgress) previousBackupStale = stale;
    const dashboardUniverse = {
      stats: universeStats,
      backups: backupRows,
      autoBackupIntervalMinutes: Number(config.universe.autoBackupIntervalMinutes || 0)
    };
    await bot.safeRefreshDashboard({ status: displayStatus, universe: dashboardUniverse, update: finalUpdate });
    bot.syncPublicStatus(displayStatus, backupRows, { update: finalUpdate }).catch(error => bot.reportError(error));
    return { status: displayStatus, update: finalUpdate };
  } finally { statusCheckBusy = false; }
}
const statusPollMs = Math.max(5000, Number(config.health?.statusPollMs) || 5000);
const idleStatusPollMs = Math.max(statusPollMs, Number(config.health?.idleStatusPollMs) || 30000);
let statusTimer = null;
let statusLoopStopped = false;
async function runStatusLoop() {
  let result = null;
  try { result = await monitorStatus(); }
  catch (error) { await bot.reportError(error); }
  if (statusLoopStopped) return;
  const delay = statusPollDelay(result?.status, result?.update, statusPollMs, idleStatusPollMs);
  statusTimer = setTimeout(runStatusLoop, delay);
  statusTimer.unref?.();
}
runStatusLoop();

const autoBackupMinutes = Number(config.universe.autoBackupIntervalMinutes || 0);
let backupTimer = null;
if (autoBackupMinutes > 0) {
  backupTimer = setInterval(async () => {
    try {
      const status = await controller.status();
      if (!status.online) return;
      const backups = await universe.listBackups();
      const last = backups[0]?.createdAt ? Date.parse(backups[0].createdAt) : 0;
      if (Date.now() - last < autoBackupMinutes * 60 * 1000) return;
      const result = await universe.createBackup('scheduled');
      if (result.skipped) return;
      await bot.sendConsole('Scheduled backup ' + result.id + ' created · ' + result.size);
      await bot.audit({ user: { tag: 'scheduler' } }, 'scheduled-backup', result);
    } catch (error) { await bot.reportError(error); }
  }, autoBackupMinutes * 60 * 1000);
  backupTimer.unref?.();
}

let shuttingDown = false;
async function shutdown(signal) {
  if (shuttingDown) return;
  shuttingDown = true;
  statusLoopStopped = true;
  if (statusTimer) clearTimeout(statusTimer);
  if (backupTimer) clearInterval(backupTimer);
  githubDeployment.stop();
  tailer.stop();
  bridgeTailer.stop();
  bot.stop();
  releaseInstance();
  console.log(`APF Discord Bot stopped (${signal}).`);
}

process.on('SIGINT', () => { shutdown('SIGINT').catch(error => { console.error(error); process.exitCode = 1; }); });
process.on('SIGTERM', () => { shutdown('SIGTERM').catch(error => { console.error(error); process.exitCode = 1; }); });

console.log(`APF Discord Bot running from ${path.resolve(config.__root)}`);
