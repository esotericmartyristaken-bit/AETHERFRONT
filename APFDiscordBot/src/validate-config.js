import fs from 'node:fs/promises';
import path from 'node:path';
import { loadConfig, tokenFor } from './config.js';

const config = await loadConfig();
const problems = [];
const requiredChannels = ['dashboard', 'console', 'lunaChat'];
const optionalChannels = ['publicStatus'];
for (const key of requiredChannels) if (!config.discord.channels?.[key]) problems.push(`discord.channels.${key} missing`);
if (!config.discord.guildId) problems.push('discord.guildId missing');
const snowflake = value => /^\d{17,20}$/.test(String(value || ''));
if (config.discord.guildId && !snowflake(config.discord.guildId)) problems.push('discord.guildId must be a Discord snowflake');
for (const key of requiredChannels) if (config.discord.channels?.[key] && !snowflake(config.discord.channels[key])) problems.push(`discord.channels.${key} must be a Discord snowflake`);
for (const key of optionalChannels) if (config.discord.channels?.[key] && !snowflake(config.discord.channels[key])) problems.push(`discord.channels.${key} must be a Discord snowflake`);
if (config.discord.offlineAlertUserId && !snowflake(config.discord.offlineAlertUserId)) problems.push('discord.offlineAlertUserId must be a Discord snowflake');
const configuredChannelIds = [...requiredChannels, ...optionalChannels].map(key => config.discord.channels?.[key]).filter(Boolean);
if (new Set(configuredChannelIds).size !== configuredChannelIds.length) problems.push('discord channel IDs must be unique');
for (const [label, ids] of [['adminRoleIds', config.discord.adminRoleIds], ['operatorRoleIds', config.discord.operatorRoleIds], ['moderatorRoleIds', config.discord.moderatorRoleIds]]) {
  for (const id of ids || []) if (!snowflake(id)) problems.push(`discord.${label} contains invalid snowflake: ${id}`);
}
if (!config.lmp.serverRoot) problems.push('lmp.serverRoot missing');
if (!config.universe.universeRoot) problems.push('universe.universeRoot missing');
if (!tokenFor(config)) problems.push(`Discord token env missing: ${config.discord.tokenEnv || 'APF_DISCORD_TOKEN'} (expected for live bot start)`);
if (config.discord.token) problems.push('Discord token must not be stored in config.json; use the configured environment variable');
if (config.lmp.masterListUrl && !/^https:\/\//i.test(config.lmp.masterListUrl)) problems.push('lmp.masterListUrl must use HTTPS');
if (!['observe', 'supervise'].includes(config.lmp.control?.mode || 'observe')) problems.push('lmp.control.mode must be observe or supervise');
const port = Number(config.lmp.port || 2252); if (!Number.isInteger(port) || port < 1 || port > 65535) problems.push('lmp.port must be an integer from 1 to 65535');
const maxBackups = Number(config.universe.maxBackups || 30); if (!Number.isInteger(maxBackups) || maxBackups < 1 || maxBackups > 1000) problems.push('universe.maxBackups must be an integer from 1 to 1000');
const autoBackup = Number(config.universe.autoBackupIntervalMinutes || 0); if (!Number.isFinite(autoBackup) || autoBackup < 0 || autoBackup > 10080) problems.push('universe.autoBackupIntervalMinutes must be 0-10080');
const statusPollMs = Number(config.health?.statusPollMs ?? 5000);
const idleStatusPollMs = Number(config.health?.idleStatusPollMs ?? 30000);
if (!Number.isInteger(statusPollMs) || statusPollMs < 5000 || statusPollMs > 300000) problems.push('health.statusPollMs must be an integer from 5000 to 300000');
if (!Number.isInteger(idleStatusPollMs) || idleStatusPollMs < statusPollMs || idleStatusPollMs > 300000) problems.push('health.idleStatusPollMs must be an integer from statusPollMs to 300000');
for (const [label, value, max] of [['security.chatQueueMax', config.security.chatQueueMax ?? 200, 10000], ['security.chatQueueTtlMs', config.security.chatQueueTtlMs ?? 300000, 86400000]]) {
  const number = Number(value); if (!Number.isInteger(number) || number < 1 || number > max) problems.push(`${label} must be an integer from 1 to ${max}`);
}
const chatDuplicateWindowMs = Number(config.security.chatDuplicateWindowMs ?? 3000);
if (!Number.isInteger(chatDuplicateWindowMs) || chatDuplicateWindowMs < 250 || chatDuplicateWindowMs > 30000) problems.push('security.chatDuplicateWindowMs must be an integer from 250 to 30000');
try { new Intl.DateTimeFormat('en-GB', { timeZone: config.discord.timezone || 'UTC' }); } catch { problems.push('discord.timezone must be a valid IANA timezone'); }
const inside = (parent, child) => { const a = path.resolve(parent); const b = path.resolve(child); const key = value => process.platform === 'win32' ? value.toLowerCase() : value; const parentKey = key(a); const childKey = key(b); return childKey === parentKey || childKey.startsWith(`${parentKey}${path.sep}`); };
if (!config.universe.backupRoot) problems.push('universe.backupRoot missing');
if (!config.universe.exportRoot) problems.push('universe.exportRoot missing');
if (config.universe.universeRoot && config.universe.backupRoot && inside(config.universe.universeRoot, config.universe.backupRoot)) problems.push('universe.backupRoot cannot be inside universeRoot');
if (config.universe.universeRoot && config.universe.exportRoot && inside(config.universe.universeRoot, config.universe.exportRoot)) problems.push('universe.exportRoot cannot be inside universeRoot');
if (config.universe.backupRoot && config.universe.exportRoot && (inside(config.universe.backupRoot, config.universe.exportRoot) || inside(config.universe.exportRoot, config.universe.backupRoot))) problems.push('universe.backupRoot and universe.exportRoot cannot overlap');
if (config.lmp.bridgeDirectory && config.lmp.serverRoot && !inside(config.lmp.serverRoot, config.lmp.bridgeDirectory)) problems.push('lmp.bridgeDirectory must be inside lmp.serverRoot');
for (const [label, target] of [['lmp.serverRoot', config.lmp.serverRoot], ['universe.universeRoot', config.universe.universeRoot]]) {
  const stat = await fs.stat(target).catch(() => null); if (!stat?.isDirectory()) problems.push(`${label} not found: ${target}`);
}
const executable = path.join(config.lmp.serverRoot, config.lmp.executable || 'Server.exe');
if (!(await fs.stat(executable).catch(() => null))?.isFile()) problems.push(`LMP executable missing: ${executable}`);
console.log(JSON.stringify({ ok: problems.length === 0, problems, controlMode: config.lmp.control?.mode || 'observe', configFile: config.__file }, null, 2));
if (problems.length) process.exitCode = 1;
