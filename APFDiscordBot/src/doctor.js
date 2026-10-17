import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadConfig, tokenFor } from './config.js';
import { LmpController } from './lmp.js';
import { UniverseManager } from './universe.js';
import { powershellJson } from './util.js';

async function fileInfo(file) {
  const stat = await fs.stat(file).catch(() => null);
  if (!stat?.isFile()) return { exists: false, path: file };
  const hash = crypto.createHash('sha256');
  const data = await fs.readFile(file);
  hash.update(data);
  return { exists: true, path: file, bytes: stat.size, sha256: hash.digest('hex'), modifiedAt: stat.mtime.toISOString() };
}

async function backupTaskInfo() {
  try {
    return await powershellJson("$i=Get-ScheduledTaskInfo -TaskName 'APF Universe Backup' -ErrorAction Stop; $t=Get-ScheduledTask -TaskName 'APF Universe Backup' -ErrorAction Stop; [pscustomobject]@{ exists=$true; state=[string]$t.State; lastRunTime=[string]$i.LastRunTime; lastTaskResult=[int64]$i.LastTaskResult; nextRunTime=[string]$i.NextRunTime } | ConvertTo-Json -Compress");
  } catch { return { exists: false }; }
}

export function udpHealthProblems(status, fallbackPort = '?') {
  if (!status?.online || !status.udpBinding?.checked) return [];
  const port = status.udpBinding.port || status.settings?.port || fallbackPort;
  if (!status.udpBinding.bound) return [`LMP UDP ${port} is not bound`];
  if (!status.udpBinding.ownedByProcess) return [`LMP UDP ${port} is owned by another process`];
  return [];
}

export async function inspect(config) {
  const problems = [];
  const warnings = [];
  const controller = new LmpController(config);
  const status = await controller.status().catch(error => ({ online: false, healthSummary: `status error: ${error.message}`, error: error.message }));
  const universe = new UniverseManager(config);
  const backups = await universe.listBackups().catch(error => [{ error: error.message }]);
  const latestBackup = backups[0]?.id && !backups[0].error ? await universe.verifyBackup(backups[0].id).catch(error => ({ ok: false, error: error.message })) : null;
  const backupTask = await backupTaskInfo();
  const artifact = await fileInfo(path.join(config.__root, 'artifacts', 'APFDiscordBridge.dll'));
  const installed = await fileInfo(path.join(config.lmp.serverRoot, 'Plugins', 'APFDiscordBridge.dll'));
  const channels = ['dashboard', 'console', 'lunaChat'];
  if (config.discord.channels?.publicStatus) channels.push('publicStatus');
  const snowflake = value => /^\d{17,20}$/.test(String(value || ''));
  const discord = {
    tokenPresent: Boolean(tokenFor(config)),
    guildConfigured: Boolean(config.discord.guildId),
    guildValid: snowflake(config.discord.guildId),
    channels: Object.fromEntries(channels.map(key => [key, Boolean(config.discord.channels?.[key])])),
    validChannels: Object.fromEntries(channels.map(key => [key, snowflake(config.discord.channels?.[key])]))
  };
  if (!status.online) problems.push('LMP server offline');
  if (status.online && !status.ready) problems.push('LMP readiness not confirmed');
  if (status.online && !status.registration) problems.push('LMP master registration not confirmed');
  if (status.master?.checked && !status.master.visible) problems.push('LMP not visible in master listing');
  problems.push(...udpHealthProblems(status, config.lmp.port));
  if (!latestBackup) problems.push('no complete universe backup found');
  else if (!latestBackup.ok) problems.push('latest universe backup verification failed');
  if (Number(config.universe.autoBackupIntervalMinutes || 0) > 0 && !backupTask.exists) problems.push('automatic backup is configured but Windows task is missing');
  const backupTaskRunning = String(backupTask.state || '').toLowerCase() === 'running';
  const schedulerTaskRunning = 267009; // 0x41301 / SCHED_S_TASK_RUNNING
  if (backupTask.exists && Number(backupTask.lastTaskResult) !== 0 && !(backupTaskRunning && Number(backupTask.lastTaskResult) === schedulerTaskRunning)) {
    warnings.push(`Windows backup task last result was ${backupTask.lastTaskResult}`);
  }
  if (!artifact.exists) problems.push('bridge build artifact missing');
  if (artifact.exists && !installed.exists) problems.push('bridge not installed in live LMP; requires planned restart');
  if (artifact.sha256 && installed.sha256 && artifact.sha256 !== installed.sha256) problems.push('live bridge hash differs from current artifact');
  if (!discord.tokenPresent) problems.push('Discord token environment variable missing');
  if (!discord.guildConfigured) problems.push('Discord guild ID missing');
  else if (!discord.guildValid) problems.push('Discord guild ID is not a valid snowflake');
  for (const [key, present] of Object.entries(discord.channels)) if (!present) problems.push(`Discord channel missing: ${key}`);
  for (const [key, valid] of Object.entries(discord.validChannels)) if (discord.channels[key] && !valid) problems.push(`Discord channel ID invalid: ${key}`);
  const logQuietMinutes = Math.max(1, Number(config.health?.logQuietMinutes) || 60);
  if (status.logAgeMs !== null && status.logAgeMs > logQuietMinutes * 60 * 1000) warnings.push(`LMP log has been quiet for more than ${logQuietMinutes} minutes; telemetry-only, not a runtime failure`);
  if (config.lmp.bridgeDirectory && !status.bridge?.ready) warnings.push('APF bridge is not ready in the live process');
  return {
    ok: problems.length === 0,
    runtimeHealthy: Boolean(status.online && status.ready && status.registration && (!status.master?.checked || status.master.visible) && udpHealthProblems(status, config.lmp.port).length === 0 && latestBackup?.ok !== false),
    backupHealthy: latestBackup?.ok === true,
    activationReady: Boolean(discord.tokenPresent && discord.guildValid && Object.values(discord.validChannels).every(Boolean)),
    generatedAt: new Date().toISOString(),
    problems,
    warnings,
    runtime: { status, latestBackup, backupCount: backups.length, backupTask },
    bridge: { artifact, installed, installedMatchesArtifact: Boolean(artifact.sha256 && installed.sha256 && artifact.sha256 === installed.sha256) },
    discord,
    paths: { serverRoot: path.resolve(config.lmp.serverRoot), universeRoot: path.resolve(config.universe.universeRoot), backupRoot: path.resolve(config.universe.backupRoot), exportRoot: path.resolve(config.universe.exportRoot) }
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const config = await loadConfig();
  const report = await inspect(config);
  console.log(JSON.stringify(report, null, 2));
  if (!report.ok) process.exitCode = 1;
}
