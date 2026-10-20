import {
  ActionRowBuilder, ButtonBuilder, ButtonStyle, Client, EmbedBuilder, GatewayIntentBits,
  Partials, REST, Routes, SlashCommandBuilder
} from 'discord.js';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { isModerator, isOperator, tokenFor } from './config.js';
import { appendJsonLine, clampText, formatBytes, formatDuration, readJson, tailText, writeJsonAtomic } from './util.js';
import { PublicAvailabilityMonitor } from './health.js';

const COLOR = { good: 0x2ecc71, warn: 0xf1c40f, bad: 0xe74c3c, blue: 0x3498db, gray: 0x95a5a6 };
const fence = String.fromCharCode(96).repeat(3);
const PUBLIC_STATUS_FOOTER = 'APF public server status · updates automatically';
const DISCORD_OPERATION_TIMEOUT_MS = 15000;
const PUBLIC_STATUS_HEARTBEAT_MS = 15 * 60 * 1000;
const PUBLIC_STATUS_DATA_REFRESH_MS = 5 * 60 * 1000;

async function discordOperation(promise, label, timeoutMs = DISCORD_OPERATION_TIMEOUT_MS) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`${label} timed out after ${timeoutMs}ms`)), timeoutMs); })
    ]);
  } finally { if (timer) clearTimeout(timer); }
}
function channelId(config, key) { return config.discord.channels?.[key] || ''; }
function channelName(key) { return ({ dashboard: 'dashboard', console: 'console', lunaChat: 'luna-chat', publicStatus: 'servers-status' })[key] || key; }
function mergePublicStatusOptions(prior = {}, next = {}) {
  return { ...prior, ...next, moveToBottom: prior.moveToBottom === true || next.moveToBottom === true };
}
function isDiscordRateLimit(error) {
  return String(error?.name || '').startsWith('RateLimitError') || Number(error?.status) === 429 || Number(error?.code) === 20028;
}
function discordRateLimitDelay(error) {
  const values = [error?.retryAfter, error?.timeToReset, error?.sublimitTimeout].map(Number).filter(value => Number.isFinite(value) && value > 0);
  return Math.max(500, ...values) + 250;
}
export function rejectPublicStatusRateLimit(config, data) {
  const publicStatusId = String(channelId(config, 'publicStatus'));
  return Boolean(publicStatusId)
    && data?.global !== true
    && String(data?.method || '').toUpperCase() === 'PATCH'
    && String(data?.majorParameter || '') === publicStatusId
    && String(data?.route || '').includes('/messages/');
}
export function discordRuntimeProblems(config) {
  const snowflake = value => /^\d{17,20}$/.test(String(value || ''));
  const problems = [];
  if (!snowflake(config.discord.guildId)) problems.push('discord.guildId');
  for (const key of ['dashboard', 'console', 'lunaChat']) if (!snowflake(channelId(config, key))) problems.push(`discord.channels.${key}`);
  if (channelId(config, 'publicStatus') && !snowflake(channelId(config, 'publicStatus'))) problems.push('discord.channels.publicStatus');
  if (config.discord.offlineAlertUserId && !snowflake(config.discord.offlineAlertUserId)) problems.push('discord.offlineAlertUserId');
  return problems;
}
function cleanMention(text) { return String(text).replaceAll('@everyone', '@ everyone').replaceAll('@here', '@ here').replaceAll('<@', '< @'); }
function transientTimer(callback, delay) { const timer = setTimeout(callback, delay); timer.unref?.(); return timer; }
function chatFingerprint(chat) {
  const normalize = value => String(value ?? '').normalize('NFKC').replace(/\s+/g, ' ').trim().toLocaleLowerCase();
  return createHash('sha256').update(`${normalize(chat?.from)}\u0000${normalize(chat?.text)}`).digest('hex');
}
export function playerCountText(count) {
  if (count === null || count === undefined || !Number.isFinite(Number(count))) return 'Players unknown';
  const value = Number(count);
  return `${value} ${value === 1 ? 'player' : 'players'} online`;
}
function backupScheduleText(minutes) {
  const value = Number(minutes || 0);
  if (value <= 0) return 'disabled';
  if (value % 60 === 0) return `every ${value / 60}h`;
  return `every ${value}m`;
}
function isPublicStatusMessage(message, botId) {
  return Boolean(message?.author?.id === botId && message?.embeds?.some(embed => String(embed.footer?.text || '').startsWith('APF public server status')));
}

function updateNoticeText(update) {
  if (!update || !['queued', 'deploying', 'failed'].includes(update.status)) return '';
  const version = update.version || update.packageId || 'pending update';
  if (update.status === 'failed') return `Update **${version}** failed: ${update.error || 'manual intervention required'}.`;
  if (update.status === 'deploying') return `Update **${version}** is deploying. The server is restarting safely.`;
  const deployAt = Date.parse(update.deployAt || '');
  const deadline = Number.isFinite(deployAt) ? ` Scheduled restart <t:${Math.floor(deployAt / 1000)}:R>.` : '';
  const changes = String(update.countdownChangelog || update.notes || '').trim();
  const changelog = changes ? `\nChanges: ${changes}` : '';
  const players = Number.isFinite(Number(update.lastObservation?.playerCount)) ? ` Current players: ${update.lastObservation.playerCount}.` : '';
  return `Update **${version}** queued.${deadline} Restarts immediately if the server empties.${players}${changelog}`;
}

function deploymentDisplayState(update) {
  if (update?.status !== 'deploying') return null;
  if (update.phase === 'starting') return 'starting';
  if (['stopping', 'installing'].includes(update.phase)) return 'updating';
  return 'deploying';
}

function publicStatusForDisplay(status, confirmedOnline) {
  if (status.online === true || confirmedOnline === false) return status;
  return { ...status, online: null, playerCount: null, connectedCount: null,
    processObservation: { state: 'unknown', reason: status.processObservation?.reason || 'outage-not-confirmed' } };
}

export function publicStatusEmbed(status, latestBackup = null, now = Date.now(), careerStats = null, publicPassword = '', update = null) {
  const settings = status?.settings || {};
  const online = status?.online === true;
  const unknown = status?.processObservation?.state === 'unknown';
  const ready = online && status?.ready === true;
  const deployment = deploymentDisplayState(update);
  const title = deployment === 'updating'
    ? `${settings.name || 'LMP'} SERVER UPDATING`
    : deployment === 'starting'
      ? `${settings.name || 'LMP'} SERVER STARTING`
      : unknown
        ? `${settings.name || 'LMP'} STATUS UNAVAILABLE`
      : ready
        ? `${settings.name || 'LMP'} SERVER ONLINE`
        : (online ? `${settings.name || 'LMP'} SERVER STARTING` : `${settings.name || 'LMP'} SERVER OFFLINE`);
  const color = deployment || unknown ? COLOR.warn : (ready ? COLOR.good : (online ? COLOR.warn : COLOR.bad));
  const created = Date.parse(latestBackup?.createdAt || '');
  const backup = Number.isFinite(created) ? `${formatDuration(Math.max(0, now - created))} ago${latestBackup?.size ? ` · ${latestBackup.size}` : ''}` : 'No verified backup found';
  const uptime = online && Number.isFinite(status?.uptimeMs) ? formatDuration(status.uptimeMs) : (unknown ? 'Unknown' : 'Offline');
  const publicNumber = (value, maximumFractionDigits) => value === null || value === undefined || !Number.isFinite(Number(value))
    ? 'Unknown'
    : Number(value).toLocaleString('en-US', { maximumFractionDigits });
  const funds = publicNumber(careerStats?.funds, 0);
  const science = publicNumber(careerStats?.science, 1);
  const stateText = deployment === 'updating'
    ? 'The server is temporarily offline while an update installs.'
    : deployment === 'starting'
      ? 'The updated server is starting.'
      : unknown
        ? 'The latest server status could not be verified. This does not confirm an outage.'
      : ready
        ? 'The server is operational.'
        : (online ? 'The server is starting.' : 'The server is currently unavailable. Staff have been notified.');
  const password = settings.password === false ? '' : String(publicPassword || '').trim().replace(/[\r\n]/g, ' ').slice(0, 100);
  const description = [password ? `Server Password: **${password}**` : '', stateText].filter(Boolean).join('\n\n');
  return new EmbedBuilder().setColor(color).setTitle(title).setDescription(description).addFields(
    { name: 'Players', value: `${playerCountText(status?.playerCount)}\n\n**Uptime**\n${uptime}`, inline: true },
    { name: 'Last backup', value: `${backup}\n\n**Funds · Science**\n${funds} · ${science}`, inline: true }
  ).setFooter({ text: PUBLIC_STATUS_FOOTER }).setTimestamp(new Date(now));
}
export function redactCommandText(text) {
  const value = String(text || '').trim();
  const match = value.replace(/^\//, '').match(/^(changesettings)\s+(\S+)\s+(Password|password|ServerPassword)\s+(.+)$/i);
  if (match) return `${match[1]} ${match[2]} ${match[3]} <redacted>`;
  const say = value.replace(/^\//, '').match(/^(say)\s+(.+)$/i);
  if (say) return `${say[1]} <redacted>`;
  return value;
}

export function redactSensitiveOutput(text) {
  return String(text || '').split(/\r?\n/).map(line => line
    .replace(/(\b(?:password|serverpassword)\b\s*(?:[:=]\s*)?).*$/i, '$1<redacted>')
    .replace(/(\b(?:command\s+input\s*:\s*\/?say\s+|broadcasting\s+|\/?say\s+)).*$/i, '$1<redacted>')
    .replace(/(\[Discord\]\s+[^:]{1,80}:\s*).*/i, '$1<redacted>')).join('\n');
}

export function redactLocalPaths(text) {
  return String(text ?? '')
    .replace(/(['"])(?:(?:file:\/\/\/)?[a-z]:[\\/]|\\\\)[^\r\n'"]+\1/gi, '$1<local-path>$1')
    .replace(/\((?:file:\/\/\/)?[a-z]:[\\/][^)\r\n]+\)/gi, '(<local-path>)')
    .replace(/file:\/\/\/[a-z]:\/[^\s)\]}>]+/gi, '<local-path>')
    .replace(/\b[a-z]:[\\/](?:[^\s)\]}>:,]+[\\/])*[^\s)\]}>:,]+/gi, '<local-path>')
    .replace(/\\\\[^\\/\s]+[\\/][^\\/\s]+(?:[\\/][^\s)\]}>:,]+)+/g, '<local-path>')
    .replace(/\/(?:Users|home)\/[^\s)\]}>]+/g, '<local-path>');
}

export function publicErrorText(error) {
  const name = redactLocalPaths(error?.name || 'Error').replace(/\s+/g, ' ').trim() || 'Error';
  const rawCode = String(error?.code ?? '').trim();
  const code = /^[a-z0-9_-]{1,32}$/i.test(rawCode) ? ` [${rawCode}]` : '';
  const message = redactLocalPaths(error?.message ?? String(error ?? 'Unknown error')).replace(/\s+/g, ' ').trim();
  return message && message !== name ? `${name}${code}: ${message}` : `${name}${code}`;
}

function statusEmbed(status, maintenance = false, universe = null, maintenanceUntil = null, update = null) {
  const settings = status.settings || {};
  const master = status.master || {};
  const unknown = status.processObservation?.state === 'unknown';
  const color = unknown ? COLOR.warn : (!status.online ? COLOR.bad : (!status.ready || (settings.register && (!status.registration || !master.visible)) || status.healthIssues?.length ? COLOR.warn : COLOR.good));
  const players = playerCountText(status.playerCount);
  const externallyControllable = Boolean(status.externalLifecycleEnabled);
  const embed = new EmbedBuilder().setColor(color).setTitle(`APF · ${settings.name || 'LMP Server'}`).setDescription(unknown ? 'Server status unavailable; an outage is not confirmed.' : (status.online ? (status.ready ? 'Server operational.' : 'Server running with health warnings.') : 'Server offline.')).addFields(
    { name: 'Process', value: unknown ? 'unknown' : (status.online ? `online · PID ${status.pid}${status.owned ? ' · bot-owned' : (externallyControllable ? ' · external lifecycle enabled' : ' · observe-only')}` : 'offline'), inline: true },
    { name: 'Uptime', value: unknown ? 'unknown' : (status.uptimeMs === null ? 'offline' : formatDuration(status.uptimeMs)), inline: true },
    { name: 'Memory', value: status.resources?.workingSetBytes ? formatBytes(status.resources.workingSetBytes) : 'unknown', inline: true },
    { name: 'LMP', value: `${settings.name || 'unknown'} · ${settings.mode || '?'} · UDP ${settings.port || '?'}`, inline: true },
    { name: 'Players', value: players, inline: true },
    { name: 'Readiness', value: status.ready ? 'systems up' : 'starting/not ready', inline: true },
    { name: 'UDP bind', value: status.udpBinding?.checked ? (status.udpBinding.bound ? (status.udpBinding.ownedByProcess ? 'bound by LMP' : 'bound by another process') : 'not bound') : 'not checked', inline: true },
    { name: 'Master listing', value: settings.register === false ? 'disabled' : (master.visible ? 'visible' : (master.checked ? 'not visible' : 'not checked')), inline: true },
    { name: 'NAT', value: status.nat || 'unknown', inline: true },
    { name: 'Chat bridge', value: status.bridge?.ready ? 'ready' : (status.bridge?.configured ? 'not ready/stale' : 'not configured'), inline: true },
    { name: 'Mod control', value: settings.modControl ? (status.modControl?.configured ? `enabled · ${status.modControl.allowedParts ?? '?'} parts` : 'enabled') : 'disabled', inline: true },
    { name: 'Backup interval', value: `${Math.round((settings.backupIntervalMs || 0) / 1000)}s`, inline: true },
    { name: 'Health', value: clampText(status.healthSummary || 'unknown', 100), inline: true },
    { name: 'Storage', value: status.storage?.available ? `${status.storage.free} free` : 'unknown', inline: true },
    { name: 'Plugins', value: String(status.plugins?.length ?? 0), inline: true },
    { name: 'Log age', value: status.logAgeMs === null ? 'unknown' : formatDuration(status.logAgeMs), inline: true },
    { name: 'Mode', value: maintenance ? `MAINTENANCE${maintenanceUntil ? ` until ${new Date(maintenanceUntil).toISOString()}` : ''}` : (status.mode || 'observe'), inline: true }
  ).setFooter({ text: 'APF persistent dashboard' }).setTimestamp();
  if (universe) {
    const latest = universe.backups?.[0]; const age = latest?.createdAt ? formatDuration(Math.max(0, Date.now() - Date.parse(latest.createdAt))) : 'none';
    embed.addFields(
      { name: 'Universe', value: `${universe.stats?.fileCount ?? '?'} files · ${universe.stats?.size || 'unknown'}`, inline: true },
      { name: 'Last backup', value: latest ? `${age} ago · ${latest.size || 'unknown'}` : 'none', inline: true },
      { name: 'APF backup schedule', value: backupScheduleText(universe.autoBackupIntervalMinutes), inline: true }
    );
  }
  const updateNotice = updateNoticeText(update);
  if (updateNotice) embed.addFields({ name: 'Update', value: updateNotice, inline: false });
  return embed;
}

export function dashboardButtons() {
  return [new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId('apf:refresh').setLabel('Refresh').setStyle(ButtonStyle.Secondary),
    new ButtonBuilder().setCustomId('apf:players').setLabel('Players').setStyle(ButtonStyle.Primary),
    new ButtonBuilder().setCustomId('apf:health').setLabel('Health').setStyle(ButtonStyle.Primary),
    new ButtonBuilder().setCustomId('apf:logs').setLabel('Logs').setStyle(ButtonStyle.Secondary)
  ), new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId('apf:backup').setLabel('Backup').setStyle(ButtonStyle.Success),
    new ButtonBuilder().setCustomId('apf:universe').setLabel('Universe').setStyle(ButtonStyle.Primary),
    new ButtonBuilder().setCustomId('apf:connections').setLabel('Connections').setStyle(ButtonStyle.Primary),
    new ButtonBuilder().setCustomId('apf:maintenance').setLabel('Maintenance').setStyle(ButtonStyle.Secondary)
  ), new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId('apf:restart').setLabel('Restart').setStyle(ButtonStyle.Danger),
    new ButtonBuilder().setCustomId('apf:stop').setLabel('Stop').setStyle(ButtonStyle.Danger),
    new ButtonBuilder().setCustomId('apf:start').setLabel('Start').setStyle(ButtonStyle.Success)
  )];
}

function confirmButtons(action) {
  return [new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId(`apf:confirm:${action}`).setLabel(`Confirm ${action}`).setStyle(ButtonStyle.Danger),
    new ButtonBuilder().setCustomId('apf:cancel').setLabel('Cancel').setStyle(ButtonStyle.Secondary)
  )];
}

function commandConfirmButtons(token) {
  return [new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId(`apf:confirmcmd:${token}`).setLabel('Confirm LMP action').setStyle(ButtonStyle.Danger),
    new ButtonBuilder().setCustomId('apf:cancel').setLabel('Cancel').setStyle(ButtonStyle.Secondary)
  )];
}

const LMP_COMMANDS = [
  ['help', 'show server command help'],
  ['backup', 'create an LMP universe backup'],
  ['changesettings', 'change an LMP setting'],
  ['clearvessels', 'remove matching vessels'],
  ['connectionstats', 'show network traffic statistics'],
  ['countclients', 'count connected clients'],
  ['dekessler', 'clear debris'],
  ['kick', 'kick a player'],
  ['ban', 'ban a player'],
  ['listclients', 'list connected clients'],
  ['listlocks', 'list current locks'],
  ['nukeksc', 'clear vessels at KSC/runway'],
  ['restartserver', 'restart the LMP server'],
  ['say', 'broadcast a server message'],
  ['setfunds', 'set player funds'],
  ['setscience', 'set player science'],
  ['vessel', 'inspect or manage a vessel']
];

const NATIVE_SHORTCUTS = [
  ['countclients', 'Count connected LMP clients.'],
  ['listclients', 'List connected LMP clients.'],
  ['listlocks', 'List current LMP locks.'],
  ['connectionstats', 'Show LMP connection statistics.'],
  ['changesettings', 'Change one LMP setting.'],
  ['clearvessels', 'Clear matching vessels.'],
  ['dekessler', 'Clear debris.'],
  ['nukeksc', 'Clear vessels around KSC/runway.'],
  ['kick', 'Kick one LMP player.'],
  ['ban', 'Ban one LMP player.'],
  ['vessel', 'Inspect or manage one vessel.'],
  ['setfunds', 'Set one player’s funds.'],
  ['setscience', 'Set one player’s science.'],
  ['restartserver', 'Request an LMP server restart.']
];

const CONSOLE_HELP = [
  ['/help', 'full command catalog'], ['/status', 'live status embed'], ['/health', 'health checks and warnings'],
  ['/diagnose', 'bounded incident snapshot'], ['/bridge', 'bridge queue/PID state'], ['/modcontrol', 'LMP mod-control summary'],
  ['/players', 'known player sessions'], ['/who', 'short player presence view'], ['/settings', 'safe server settings'],
  ['/plugins', 'installed plugin inventory'], ['/logs', 'recent log tail'], ['/connections', 'connection statistics'],
  ['/locks', 'current LMP locks'], ['/clients', 'connected-client list'], ['/server-time', 'server-local time'],
  ['/motd', 'configured message of the day'], ['/rules', 'configured server rules'], ['/lmp', 'confirmed native LMP action'],
  ['/say', 'broadcast a server message'], ['/start', 'confirmed lifecycle start'], ['/stop', 'confirmed lifecycle stop'],
  ['/restart', 'confirmed lifecycle restart'], ['/backup', 'filesystem universe backup'], ['/maintenance', 'maintenance window'],
  ['/deploy status|staged|queue|cancel|force', 'safe staged update deployment'],
  ['/audit', 'local operator audit'], ['/events', 'local event history'], ['/command', 'confirmed allowlisted LMP command'],
  ...NATIVE_SHORTCUTS.map(([name, description]) => [`/${name}`, `typed shortcut · ${description.toLowerCase()}`])
];
const UNIVERSE_HELP = [
  ['/universe status', 'universe size/buckets'], ['/universe backups', 'backup browser'], ['/universe backup', 'manual backup'],
  ['/universe verify', 'SHA-256/count verification'], ['/universe diff', 'read-only backup diff'], ['/universe changes', 'recent file changes'],
  ['/universe crafts [owner]', 'craft browser/filter'], ['/universe craft-info', 'craft metadata'], ['/universe kerbals [state]', 'Kerbal roster/filter'],
  ['/universe kerbal-info', 'Kerbal metadata'], ['/universe vessels', 'saved vessel files'], ['/universe vessel-info', 'vessel metadata'],
  ['/universe scenarios', 'scenario files'], ['/universe contracts', 'contract files'], ['/universe search', 'filename/content search'],
  ['/universe export', 'safe file export'], ['/universe recover-craft', 'non-overwriting craft recovery'],
  ['/universe recover-kerbal', 'non-overwriting Kerbal recovery'], ['/universe recover-vessel', 'non-overwriting vessel recovery'],
  ['/universe recover-scenario', 'non-overwriting scenario recovery'], ['/universe restore-plan', 'non-destructive restore plan'],
  ['/universe restore-points', 'retained rollback points'], ['/universe restore', 'guarded full restore'], ['/universe rollback', 'guarded rollback']
];

export function commandHelpText() {
  const native = LMP_COMMANDS.map(([name, description]) => `/${name} - ${description}`).join('\n');
  return `**APF console**\n${CONSOLE_HELP.map(([name]) => name).join(' · ')}\n\n**Universe**\n${UNIVERSE_HELP.map(([name]) => name).join(' · ')}\n\n**Native LMP command catalog**\n${native}\n\nDangerous operations require operator permission, confirmation, audit logging, and (for live file recovery) maintenance/offline state.`;
}

export function commandHelpEmbeds() {
  const native = LMP_COMMANDS.map(([name, description]) => `/${name} - ${description}`).join('\n');
  const chunks = (rows, prefix) => {
    const fields = []; let current = ''; let part = 1;
    for (const [name, description] of rows) {
      const line = `**${name}** - ${description}`;
      if (current && current.length + line.length + 1 > 1000) { fields.push({ name: `${prefix} ${part++}`, value: current }); current = ''; }
      current += (current ? '\n' : '') + line;
    }
    if (current) fields.push({ name: `${prefix} ${part}`, value: current });
    return fields;
  };
  return [new EmbedBuilder().setColor(COLOR.blue).setTitle('APF command catalog').setDescription('Use commands in #console unless explicitly stated. Successful results are visible to everyone with channel access; private confirmation gates protect state-changing actions.').addFields(
    ...chunks(CONSOLE_HELP, 'Console / operations'),
    ...chunks(UNIVERSE_HELP, 'Universe / recovery'),
    { name: 'Native LMP catalog', value: native },
    { name: 'Safety model', value: 'Lifecycle, native state changes, and destructive universe actions require role checks, confirmation, audit logging, and additional maintenance/offline gates where applicable.' }
  ).setFooter({ text: 'APF Discord control plane' })];
}

function command(name, description) { return new SlashCommandBuilder().setName(name).setDescription(description).setDMPermission(false); }
function nativeShortcutCommand(name, description) {
  const builder = command(name, description);
  if (name === 'changesettings') return builder.addStringOption(o => o.setName('setting').setDescription('Setting name').setRequired(true).setMaxLength(100)).addStringOption(o => o.setName('value').setDescription('New value').setRequired(true).setMaxLength(160));
  if (name === 'clearvessels') return builder.addStringOption(o => o.setName('pattern').setDescription('Optional vessel/name pattern').setMaxLength(120));
  if (['kick', 'ban'].includes(name)) return builder.addStringOption(o => o.setName('player').setDescription('Player name or identifier').setRequired(true).setMaxLength(120));
  if (name === 'vessel') return builder.addStringOption(o => o.setName('action').setDescription('LMP vessel action').setRequired(true).setMaxLength(80)).addStringOption(o => o.setName('target').setDescription('Optional vessel/name argument').setMaxLength(160));
  if (['setfunds', 'setscience'].includes(name)) return builder.addStringOption(o => o.setName('player').setDescription('Player name or identifier').setRequired(true).setMaxLength(120)).addIntegerOption(o => o.setName('amount').setDescription('Non-negative amount').setRequired(true).setMinValue(0).setMaxValue(2147483647));
  return builder;
}
function nativeShortcutArgument(interaction, name) {
  const string = key => interaction.options.getString(key) || '';
  if (name === 'changesettings') return `${string('setting')} ${string('value')}`.trim();
  if (name === 'clearvessels') return string('pattern');
  if (['kick', 'ban'].includes(name)) return string('player');
  if (name === 'vessel') return `${string('action')} ${string('target')}`.trim();
  if (['setfunds', 'setscience'].includes(name)) return `${string('player')} ${interaction.options.getInteger('amount')}`;
  return '';
}
export function commands() {
  return [
    command('help', 'Show APF bot commands.'),
    command('status', 'Show live LMP/APF health.'),
    command('health', 'Show health checks and warnings.'),
    command('diagnose', 'Show a bounded incident snapshot.'),
    command('bridge', 'Show Discord/LMP bridge state.'),
    command('modcontrol', 'Show LMP mod-control policy summary.'),
    command('players', 'Show connected players.'),
    command('who', 'Show a short player presence view.'),
    command('settings', 'Show safe server settings.'),
    command('plugins', 'List installed server plugins.'),
    command('logs', 'Show recent server log lines.').addIntegerOption(o => o.setName('lines').setDescription('1-80 lines').setMinValue(1).setMaxValue(80)),
    command('connections', 'Request LMP connection statistics.'),
    command('locks', 'Request current LMP locks.'),
    command('clients', 'Request the connected-client list.'),
    command('server-time', 'Show current time in the configured server timezone.'),
    command('motd', 'Show the configured message of the day.'),
    command('rules', 'Show the configured server rules.'),
    command('lmp', 'Run a named, confirmed LMP administrative action.').addStringOption(o => o.setName('operation').setDescription('Named LMP operation').setRequired(true).addChoices(...LMP_COMMANDS.map(([name]) => ({ name, value: name }))))
      .addStringOption(o => o.setName('argument').setDescription('LMP operation argument, if required').setMaxLength(260)),
    command('say', 'Broadcast a server message.').addStringOption(o => o.setName('message').setDescription('Message').setRequired(true).setMaxLength(500)),
    command('start', 'Start LMP under bot supervision.'), command('stop', 'Stop LMP under bot supervision.'), command('restart', 'Restart LMP under bot supervision.'),
    command('backup', 'Create a filesystem universe snapshot.'),
    command('maintenance', 'Set or inspect maintenance mode.').addStringOption(o => o.setName('mode').setDescription('Mode').setRequired(true).addChoices({ name: 'on', value: 'on' }, { name: 'off', value: 'off' }, { name: 'status', value: 'status' })).addIntegerOption(o => o.setName('minutes').setDescription('Optional expiry: 1-1440 minutes').setMinValue(1).setMaxValue(1440)),
    new SlashCommandBuilder().setName('deploy').setDescription('Queue and control staged server updates.').setDMPermission(false)
      .addSubcommand(s => s.setName('status').setDescription('Show queued update state.'))
      .addSubcommand(s => s.setName('staged').setDescription('List valid staged update packages.'))
      .addSubcommand(s => s.setName('queue').setDescription('Schedule a timed update restart.')
        .addStringOption(o => o.setName('id').setDescription('Staged package ID').setRequired(true).setMaxLength(80))
        .addIntegerOption(o => o.setName('minutes').setDescription('Restart countdown; default 15 minutes').setMinValue(1).setMaxValue(1440))
        .addStringOption(o => o.setName('changelog').setDescription('Short public changelog').setMaxLength(320)))
      .addSubcommand(s => s.setName('cancel').setDescription('Cancel a queued or failed update.'))
      .addSubcommand(s => s.setName('force').setDescription('Arm the queued update even if players remain online.')),
    command('events', 'Show recent local server/bot events.').addIntegerOption(o => o.setName('lines').setDescription('1-80 events').setMinValue(1).setMaxValue(80)),
    command('audit', 'Show recent local operator audit records.').addIntegerOption(o => o.setName('lines').setDescription('1-40 records').setMinValue(1).setMaxValue(40)),
    command('command', 'Run an allowlisted LMP console command.').addStringOption(o => o.setName('command').setDescription('No arbitrary shell access').setRequired(true).setMaxLength(300)),
    ...NATIVE_SHORTCUTS.map(([name, description]) => nativeShortcutCommand(name, description)),
    new SlashCommandBuilder().setName('universe').setDescription('Inspect and manage the LMP universe.').setDMPermission(false)
      .addSubcommand(s => s.setName('status').setDescription('Universe file/size summary.'))
      .addSubcommand(s => s.setName('backups').setDescription('List backups.'))
      .addSubcommand(s => s.setName('backup').setDescription('Create backup.'))
      .addSubcommand(s => s.setName('verify').setDescription('Verify a backup file count and byte total.').addStringOption(o => o.setName('id').setDescription('Backup ID').setRequired(true)))
      .addSubcommand(s => s.setName('diff').setDescription('Compare two backups without changing files.').addStringOption(o => o.setName('from').setDescription('Older/left backup ID').setRequired(true)).addStringOption(o => o.setName('to').setDescription('Newer/right backup ID').setRequired(true)).addIntegerOption(o => o.setName('limit').setDescription('1-100 rows').setMinValue(1).setMaxValue(100)))
      .addSubcommand(s => s.setName('changes').setDescription('Show recently changed universe files.').addIntegerOption(o => o.setName('limit').setDescription('1-50').setMinValue(1).setMaxValue(50)))
      .addSubcommand(s => s.setName('crafts').setDescription('List craft files.').addStringOption(o => o.setName('owner').setDescription('Optional owner/name filter').setMaxLength(80)))
      .addSubcommand(s => s.setName('craft-info').setDescription('Inspect one craft metadata summary.').addStringOption(o => o.setName('path').setDescription('Universe-relative Crafts path').setRequired(true).setMaxLength(260)))
      .addSubcommand(s => s.setName('kerbals').setDescription('List kerbal data files.').addStringOption(o => o.setName('state').setDescription('Optional state filter').setMaxLength(80)))
      .addSubcommand(s => s.setName('kerbal-info').setDescription('Inspect one Kerbal metadata summary.').addStringOption(o => o.setName('path').setDescription('Universe-relative Kerbals path').setRequired(true).setMaxLength(260)))
      .addSubcommand(s => s.setName('vessels').setDescription('List saved vessel files.'))
      .addSubcommand(s => s.setName('vessel-info').setDescription('Inspect one vessel metadata summary.').addStringOption(o => o.setName('path').setDescription('Universe-relative Vessels path').setRequired(true).setMaxLength(260)))
      .addSubcommand(s => s.setName('scenarios').setDescription('List scenario files.'))
      .addSubcommand(s => s.setName('contracts').setDescription('List contract files when present.'))
      .addSubcommand(s => s.setName('search').setDescription('Search universe files/content.').addStringOption(o => o.setName('query').setDescription('Search term').setRequired(true).setMaxLength(80)))
      .addSubcommand(s => s.setName('export').setDescription('Copy a safe craft/Kerbal file to APF exports.').addStringOption(o => o.setName('path').setDescription('Universe-relative path').setRequired(true).setMaxLength(260)))
      .addSubcommand(s => s.setName('recover-craft').setDescription('Recover a craft from a backup; maintenance + exact confirmation required.').addStringOption(o => o.setName('backup').setDescription('Backup ID').setRequired(true)).addStringOption(o => o.setName('path').setDescription('Backup-relative Crafts path').setRequired(true).setMaxLength(260)).addStringOption(o => o.setName('confirm').setDescription('Type the exact confirmation token').setRequired(true).setMaxLength(400)))
      .addSubcommand(s => s.setName('recover-kerbal').setDescription('Recover a Kerbal from a backup; maintenance + exact confirmation required.').addStringOption(o => o.setName('backup').setDescription('Backup ID').setRequired(true)).addStringOption(o => o.setName('path').setDescription('Backup-relative Kerbals path').setRequired(true).setMaxLength(260)).addStringOption(o => o.setName('confirm').setDescription('Type the exact confirmation token').setRequired(true).setMaxLength(400)))
      .addSubcommand(s => s.setName('recover-vessel').setDescription('Recover a vessel from a backup; maintenance + exact confirmation required.').addStringOption(o => o.setName('backup').setDescription('Backup ID').setRequired(true)).addStringOption(o => o.setName('path').setDescription('Backup-relative Vessels path').setRequired(true).setMaxLength(260)).addStringOption(o => o.setName('confirm').setDescription('Type the exact confirmation token').setRequired(true).setMaxLength(400)))
      .addSubcommand(s => s.setName('recover-scenario').setDescription('Recover a scenario from a backup; maintenance + exact confirmation required.').addStringOption(o => o.setName('backup').setDescription('Backup ID').setRequired(true)).addStringOption(o => o.setName('path').setDescription('Backup-relative Scenarios path').setRequired(true).setMaxLength(260)).addStringOption(o => o.setName('confirm').setDescription('Type the exact confirmation token').setRequired(true).setMaxLength(400)))
      .addSubcommand(s => s.setName('restore-plan').setDescription('Show a non-destructive restore plan.').addStringOption(o => o.setName('id').setDescription('Backup ID').setRequired(true)))
      .addSubcommand(s => s.setName('restore-points').setDescription('List retained pre-restore rollback points.'))
      .addSubcommand(s => s.setName('restore').setDescription('Full restore; requires maintenance, offline server, and confirmation.').addStringOption(o => o.setName('id').setDescription('Backup ID').setRequired(true)).addStringOption(o => o.setName('confirm').setDescription('Type RESTORE <backup-id>').setRequired(true).setMaxLength(400)))
      .addSubcommand(s => s.setName('rollback').setDescription('Rollback to a retained restore point; maintenance + offline required.').addStringOption(o => o.setName('point').setDescription('Restore point ID').setRequired(true).setMaxLength(260)).addStringOption(o => o.setName('confirm').setDescription('Type ROLLBACK <point-id>').setRequired(true).setMaxLength(400)))
  ].map(item => item.toJSON());
}

export class DiscordBot {
  constructor(config, controller, universe, updates = null) {
    this.config = config; this.controller = controller; this.universe = universe; this.updates = updates; this.maintenance = false; this.maintenanceUntil = null; this.lifecycleBusy = false; this.dashboardMessage = null; this.cooldowns = new Map(); this.chatCooldowns = new Map(); this.recentChats = new Map(); this.recentChatContents = new Map(); this.crossSourceChats = new Map(); this.outboundChats = new Map(); this.chatDeliveryQueue = new Map(); this.chatRetryTimer = null; this.chatFlushPromise = null; this.chatDeliveryLedger = []; this.restartChatDeliveries = []; this.pendingActions = new Map(); this.discordReady = false; this.discordInitPromise = null; this.startupAnnounced = false; this.statePath = path.join(config.__root || process.cwd(), 'state', 'bot-state.json'); this.auditPath = path.join(config.__root || process.cwd(), 'state', 'audit.jsonl');
    this.dashboardMessageId = null;
    this.dashboardChannelId = null;
    this.dashboardInitPromise = null;
    this.publicStatusMessage = null;
    this.publicStatusMessageId = null;
    this.publicStatusChannelId = null;
    this.publicStatusOnline = null;
    this.publicAvailabilityMonitor = new PublicAvailabilityMonitor(config.health || {});
    this.publicStatusSyncPromise = Promise.resolve();
    this.lastObservationDiagnosticKey = null;
    this.lastObservationDiagnosticAt = 0;
    this.publicStatusLastCoreKey = null;
    this.publicStatusLastDataKey = null;
    this.publicStatusLastWrittenAt = 0;
    this.publicStatusRefreshPromise = null;
    this.publicStatusQueuedOptions = null;
    this.publicStatusMoveQueued = false;
    this.publicStatusMoveTimer = null;
    this.publicStatusRateLimitedUntil = 0;
    this.publicStatusRateLimitStreak = 0;
    this.publicStatusLastRateLimitEventAt = 0;
    this.publicStatusRetryOptions = null;
    this.publicStatusRetryTimer = null;
    this.publicStatusRetryScheduledFor = 0;
    this.publicStatusSuppressBotMessageUntil = 0;
    this.expectedPublicStatusDeletes = new Set();
    this.chatMirrorChains = new Map();
    this.eventPath = path.join(config.__root || process.cwd(), 'state', 'events.jsonl');
    this.client = new Client({
      intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent],
      partials: [Partials.Channel],
      // Discord may impose a long per-channel write sublimit (error 20028).
      // Reject only public-card PATCH waits so our latest-wins retry can replace
      // stale queued payloads. Every other Discord route keeps normal queuing.
      rest: { rejectOnRateLimit: data => rejectPublicStatusRateLimit(config, data) }
    });
  }

  async start() {
    const token = tokenFor(this.config); if (!token) throw new Error(`Discord token missing. Set ${this.config.discord.tokenEnv || 'APF_DISCORD_TOKEN'}.`);
    const configProblems = discordRuntimeProblems(this.config); if (configProblems.length) throw new Error(`Discord configuration incomplete: ${configProblems.join(', ')}. Run npm run validate/provision:channels.`);
    this.client.on('clientReady', () => this.initializeDiscord().catch(error => this.reportError(error)));
    this.client.on('error', error => this.reportError(error));
    this.client.on('shardError', error => this.reportError(error));
    this.client.on('shardDisconnect', (event, shardId) => { this.recordEvent('discord-shard-disconnect', { shardId, code: event?.code || null, reason: event?.reason || null }).catch(() => {}); this.sendConsole(`Discord gateway shard ${shardId} disconnected (${event?.code || 'unknown'}).`).catch(() => {}); });
    this.client.on('shardReady', shardId => { this.recordEvent('discord-shard-ready', { shardId }).catch(() => {}); if (this.discordReady) this.safeRefreshDashboard().catch(() => {}); else this.initializeDiscord().catch(error => this.reportError(error)); });
    this.client.on('interactionCreate', interaction => this.handleInteraction(interaction).catch(error => this.handleInteractionError(interaction, error)));
    this.client.on('messageCreate', message => this.handleMessage(message).catch(error => this.reportError(error)));
    this.client.on('messageDelete', message => this.handleMessageDelete(message).catch(error => this.reportError(error)));
    this.controller.on('exit', event => this.sendConsole(`LMP exited: code=${event.code ?? '—'} signal=${event.signal ?? '—'}`).catch(() => {}));
    await this.client.login(token);
    await this.initializeDiscord();
  }

  stop() {
    this.discordReady = false;
    if (this.chatRetryTimer) clearInterval(this.chatRetryTimer);
    if (this.publicStatusMoveTimer) clearTimeout(this.publicStatusMoveTimer);
    if (this.publicStatusRetryTimer) clearTimeout(this.publicStatusRetryTimer);
    this.publicStatusMoveTimer = null;
    this.publicStatusRetryTimer = null;
    this.publicStatusRetryOptions = null;
    this.publicStatusRetryScheduledFor = 0;
    this.chatRetryTimer = null;
    this.chatFlushPromise = null;
    this.client.destroy();
  }

  async initializeDiscord() {
    if (this.discordInitPromise) return this.discordInitPromise;
    this.discordInitPromise = (async () => {
      if (this.config.discord.guildId) await this.validateDiscordAccess();
      await this.loadState(); await this.registerCommands(); await this.ensureDashboard(); this.discordReady = true;
      // Public status writes may be delayed for minutes by Discord's channel
      // write limiter. Never let that block command/chat bot startup.
      this.ensurePublicStatus().catch(error => this.reportError(error));
      if (!this.startupAnnounced) {
        this.startupAnnounced = true;
        const control = this.config.lmp.control || {}; const mode = control.mode || 'observe';
        const externalLifecycle = control.allowExternalProcessStart && control.allowExternalProcessStop && control.allowExternalProcessRestart;
        await this.sendConsole(mode === 'supervise' ? 'APF bot online; supervisor mode active.' : (externalLifecycle ? 'APF bot online; external LMP lifecycle controls active.' : 'APF bot online; observe-only attach active.'));
      }
    })();
    try { return await this.discordInitPromise; }
    finally { this.discordInitPromise = null; }
  }

  async registerCommands() {
    if (!this.config.discord.guildId) return;
    const rest = new REST({ version: '10' }).setToken(tokenFor(this.config));
    await rest.put(Routes.applicationGuildCommands(this.client.user.id, this.config.discord.guildId), { body: commands() });
  }
  async validateDiscordAccess() {
    const guild = await this.client.guilds.fetch(this.config.discord.guildId);
    const required = ['ViewChannel', 'SendMessages', 'EmbedLinks', 'ReadMessageHistory'];
    const problems = [];
    const pinWarnings = [];
    const channelKeys = ['dashboard', 'console', 'lunaChat'];
    if (channelId(this.config, 'publicStatus')) channelKeys.push('publicStatus');
    for (const key of channelKeys) {
      const channel = await this.getChannel(key);
      if (!channel?.isTextBased?.()) { problems.push(`${key}: channel missing or not text-based`); continue; }
      if (channel.guildId && channel.guildId !== guild.id) { problems.push(`${key}: channel belongs to another guild`); continue; }
      const permissions = channel.permissionsFor?.(this.client.user);
      if (!permissions) { problems.push(`${key}: bot permissions unavailable`); continue; }
      const missing = required.filter(flag => !permissions.has(flag));
      if (missing.length) problems.push(`${key}: missing ${missing.join(', ')}`);
      if (key === 'dashboard' && this.config.discord.pinDashboard !== false && !permissions.has('ManageMessages')) pinWarnings.push('dashboard: ManageMessages missing; pinning disabled by Discord permissions');
    }
    for (const warning of pinWarnings) console.warn(`[APF Discord] ${warning}`);
    if (problems.length) throw new Error(`Discord channel access incomplete: ${problems.join('; ')}`);
    return { guildId: guild.id, pinWarnings };
  }
  async loadState() {
    const state = await readJson(this.statePath, {});
    this.maintenanceUntil = Number.isFinite(Number(state.maintenanceUntil)) && Number(state.maintenanceUntil) > Date.now() ? Number(state.maintenanceUntil) : null;
    this.maintenance = state.maintenance === true && (!state.maintenanceUntil || this.maintenanceUntil !== null);
    this.dashboardMessageId = state.dashboardMessageId || null;
    this.dashboardChannelId = state.dashboardChannelId || null;
    this.publicStatusMessageId = state.publicStatusMessageId || null;
    this.publicStatusChannelId = state.publicStatusChannelId || null;
    this.publicStatusOnline = typeof state.publicStatusOnline === 'boolean' ? state.publicStatusOnline : null;
    this.publicStatusLastCoreKey = typeof state.publicStatusLastCoreKey === 'string' ? state.publicStatusLastCoreKey : null;
    this.publicStatusLastDataKey = typeof state.publicStatusLastDataKey === 'string' ? state.publicStatusLastDataKey : null;
    this.publicStatusLastWrittenAt = Number.isFinite(Number(state.publicStatusLastWrittenAt)) ? Number(state.publicStatusLastWrittenAt) : 0;
    const now = Date.now();
    const restartWindowMs = Math.max(5000, Number(this.config.security?.chatRestartDedupeWindowMs || 30000));
    this.chatDeliveryLedger = (Array.isArray(state.recentChatDeliveries) ? state.recentChatDeliveries : [])
      .filter(entry => /^[a-f0-9]{64}$/.test(String(entry?.fingerprint || '')) && Number.isFinite(Number(entry?.timestamp)) && now - Number(entry.timestamp) <= restartWindowMs)
      .slice(-100)
      .map(entry => ({ fingerprint: String(entry.fingerprint), source: String(entry.source || ''), id: String(entry.id || '').slice(0, 80), timestamp: Number(entry.timestamp) }));
    this.restartChatDeliveries = this.chatDeliveryLedger.map(entry => ({ ...entry }));
  }
  async saveState() {
    const now = Date.now();
    const restartWindowMs = Math.max(5000, Number(this.config.security?.chatRestartDedupeWindowMs || 30000));
    this.chatDeliveryLedger = this.chatDeliveryLedger.filter(entry => now - entry.timestamp <= restartWindowMs).slice(-100);
    await writeJsonAtomic(this.statePath, {
      maintenance: this.maintenance,
      maintenanceUntil: this.maintenanceUntil,
      dashboardMessageId: this.dashboardMessageId,
      dashboardChannelId: this.dashboardChannelId,
      publicStatusMessageId: this.publicStatusMessageId,
      publicStatusChannelId: this.publicStatusChannelId,
      publicStatusOnline: this.publicStatusOnline,
      publicStatusLastCoreKey: this.publicStatusLastCoreKey,
      publicStatusLastDataKey: this.publicStatusLastDataKey,
      publicStatusLastWrittenAt: this.publicStatusLastWrittenAt,
      recentChatDeliveries: this.chatDeliveryLedger,
      savedAt: new Date().toISOString()
    });
  }
  async getChannel(key) {
    const id = channelId(this.config, key);
    if (!id) return null;
    const cached = this.client.channels.cache?.get?.(id);
    if (cached) return cached;
    return discordOperation(this.client.channels.fetch(id), `Discord channel fetch (${key})`).catch(() => null);
  }
  async expireMaintenance() {
    if (!this.maintenance || !this.maintenanceUntil || Date.now() < this.maintenanceUntil) return false;
    this.maintenance = false;
    this.maintenanceUntil = null;
    await this.saveState();
    await this.recordEvent('maintenance-expired', {}).catch(() => {});
    await this.sendConsole('Maintenance window expired; maintenance mode cleared.').catch(() => {});
    return true;
  }
  async ensureDashboard() {
    if (this.dashboardInitPromise) return this.dashboardInitPromise;
    this.dashboardInitPromise = this.ensureDashboardUnlocked();
    try { return await this.dashboardInitPromise; }
    finally { this.dashboardInitPromise = null; }
  }
  async ensureDashboardUnlocked() {
    const channel = await this.getChannel('dashboard'); if (!channel?.isTextBased()) return;
    this.dashboardMessage = null;
    if (this.dashboardMessageId && this.dashboardChannelId === channel.id) {
      this.dashboardMessage = await channel.messages.fetch(this.dashboardMessageId).catch(() => null);
      if (!this.dashboardMessage || this.dashboardMessage.author.id !== this.client.user.id || !this.dashboardMessage.embeds.some(embed => embed.footer?.text === 'APF persistent dashboard')) this.dashboardMessage = null;
    }
    if (!this.dashboardMessage) {
      const pinned = typeof channel.messages.fetchPinned === 'function' ? await channel.messages.fetchPinned().catch(() => null) : null;
      this.dashboardMessage = pinned?.find(message => message.author.id === this.client.user.id && message.embeds.some(embed => embed.footer?.text === 'APF persistent dashboard')) || null;
    }
    if (!this.dashboardMessage) {
      const messages = await channel.messages.fetch({ limit: 100 }).catch(() => null);
      this.dashboardMessage = messages?.find(message => message.author.id === this.client.user.id && message.embeds.some(embed => embed.footer?.text === 'APF persistent dashboard')) || null;
    }
    const [status, universe, update] = await Promise.all([this.controller.status(), this.dashboardUniverse(), this.updateStatus()]); this.updatePresence(status);
    const payload = { embeds: [statusEmbed(status, this.maintenance, universe, this.maintenanceUntil, update)], components: dashboardButtons() };
    if (this.dashboardMessage) await this.dashboardMessage.edit(payload); else this.dashboardMessage = await channel.send(payload);
    this.dashboardMessageId = this.dashboardMessage.id;
    this.dashboardChannelId = channel.id;
    await this.saveState().catch(error => this.reportError(error));
    if (this.config.discord.pinDashboard !== false && !this.dashboardMessage.pinned) await this.dashboardMessage.pin().catch(() => {});
  }
  updatePresence(status) {
    if (!this.client.user) return;
    this.client.user.setPresence({ status: status.online && !status.healthWarnings?.length ? 'online' : 'idle', activities: [{ name: playerCountText(status.playerCount), type: 0 }] });
  }
  async refreshDashboard(options = {}) {
    await this.expireMaintenance();
    const [status, universe, update] = await Promise.all([
      options.status ? Promise.resolve(options.status) : this.controller.status(),
      options.universe ? Promise.resolve(options.universe) : this.dashboardUniverse(),
      options.update ? Promise.resolve(options.update) : this.updateStatus()
    ]); this.updatePresence(status);
    if (!this.dashboardMessage) return this.ensureDashboard();
    const payload = { embeds: [statusEmbed(status, this.maintenance, universe, this.maintenanceUntil, update)], components: dashboardButtons() };
    try { await this.dashboardMessage.edit(payload); }
    catch (error) {
      // Unknown Message/Not Found means an operator deleted the persistent panel.
      // Recreate only for that case; transient Discord failures must not create duplicates.
      if (Number(error?.code) !== 10008 && Number(error?.status) !== 404) throw error;
      this.dashboardMessage = null;
      this.dashboardMessageId = null;
      this.dashboardChannelId = null;
      await this.saveState();
      await this.ensureDashboard();
    }
    if (this.config.discord.pinDashboard !== false && !this.dashboardMessage.pinned) await this.dashboardMessage.pin().catch(() => {});
  }
  async safeRefreshDashboard(options = {}) { try { await this.refreshDashboard(options); return true; } catch (error) { await this.reportError(error); return false; } }
  async dashboardUniverse() { try { const [stats, backups] = await Promise.all([this.universe.stats(), this.universe.listBackups()]); return { stats, backups, autoBackupIntervalMinutes: Number(this.config.universe.autoBackupIntervalMinutes || 0) }; } catch (error) { this.reportError(error); return null; } }
  async updateStatus() { return this.updates?.status ? this.updates.status().catch(error => { this.reportError(error); return null; }) : null; }
  async ensurePublicStatus() {
    if (!channelId(this.config, 'publicStatus')) return null;
    // Startup/status refreshes edit the durable card in place. Moving it is
    // reserved for a new human announcement (or an outage alert), otherwise
    // every bot restart would create a new Discord notification.
    return this.syncPublicStatus(null, null, { moveToBottom: false });
  }
  syncPublicStatus(status = null, backups = null, options = {}) {
    const current = this.publicStatusSyncPromise.catch(() => {}).then(() => this.syncPublicStatusUnlocked(status, backups, options));
    this.publicStatusSyncPromise = current;
    return current;
  }
  async syncPublicStatusUnlocked(status = null, backups = null, options = {}) {
    if (!channelId(this.config, 'publicStatus')) return null;
    const current = status || await this.controller.status();
    if (Number.isFinite(Date.parse(current.observedAt)) && Date.parse(current.observedAt) < this.publicAvailabilityMonitor.lastObservedAt) return this.publicStatusMessage;
    const backupRows = backups || await this.universe.listBackups();
    const update = options.update || await this.updateStatus();
    const plannedDowntime = update?.status === 'deploying';
    const result = this.publicAvailabilityMonitor.update(current, this.publicStatusOnline, { planned: plannedDowntime });
    const changed = this.publicStatusOnline !== result.online;
    this.publicStatusOnline = result.online;
    if (changed) await this.saveState();
    const wentOffline = result.wentOffline;
    const observation = current.processObservation;
    if (observation && observation.state !== 'online') {
      const key = `${observation.state}:${observation.reason}`;
      if (key !== this.lastObservationDiagnosticKey || Date.now() - this.lastObservationDiagnosticAt >= 300000) {
        this.lastObservationDiagnosticKey = key;
        this.lastObservationDiagnosticAt = Date.now();
        await this.recordEvent('server-observation', { state: observation.state, reason: observation.reason, observedAt: current.observedAt, confirmedOnline: this.publicStatusOnline }).catch(() => {});
      }
    }
    if (wentOffline && !plannedDowntime) await this.sendPublicOfflineAlert(current);
    return this.refreshPublicStatus({ status: publicStatusForDisplay(current, this.publicStatusOnline), backups: backupRows, update, moveToBottom: options.moveToBottom === true || (wentOffline && !plannedDowntime) });
  }
  async sendPublicOfflineAlert(status) {
    const channel = await this.getChannel('publicStatus'); if (!channel?.isTextBased()) return null;
    const userId = String(this.config.discord.offlineAlertUserId || '');
    const mention = /^\d{17,20}$/.test(userId) ? `<@${userId}> ` : '';
    const name = status?.settings?.name || this.config.lmp.serverName || 'LMP server';
    this.publicStatusSuppressBotMessageUntil = Date.now() + 2000;
    const message = await channel.send({
      content: `${mention}**${cleanMention(name)} went offline.** Please check the host and server console.`,
      allowedMentions: { parse: [], users: mention ? [userId] : [] }
    });
    await this.recordEvent('public-server-offline-alert', { userId: mention ? userId : null, channelId: channel.id }).catch(() => {});
    return message;
  }
  async sendPublicStatusAnnouncement(text) {
    const channel = await this.getChannel('publicStatus'); if (!channel?.isTextBased()) return null;
    this.publicStatusSuppressBotMessageUntil = Date.now() + 2000;
    const message = await channel.send({ content: cleanMention(text), allowedMentions: { parse: [] } });
    await this.recordEvent('public-update-announcement', { channelId: channel.id, text: cleanMention(text).slice(0, 500) }).catch(() => {});
    this.schedulePublicStatusMove(500);
    return message;
  }
  schedulePublicStatusMove(delayMs = 750) {
    if (!channelId(this.config, 'publicStatus')) return;
    if (this.publicStatusMoveTimer) clearTimeout(this.publicStatusMoveTimer);
    this.publicStatusMoveTimer = setTimeout(() => {
      this.publicStatusMoveTimer = null;
      this.refreshPublicStatus({ moveToBottom: true }).catch(error => this.reportError(error));
    }, Math.max(0, Number(delayMs) || 0));
    this.publicStatusMoveTimer.unref?.();
  }
  schedulePublicStatusRetry(options = {}, delayMs = 1000) {
    this.publicStatusRetryOptions = mergePublicStatusOptions(this.publicStatusRetryOptions || {}, options);
    const target = Math.max(this.publicStatusRateLimitedUntil, Date.now() + Math.max(0, Number(delayMs) || 0));
    this.publicStatusRateLimitedUntil = target;
    if (this.publicStatusRetryTimer && this.publicStatusRetryScheduledFor === target) return;
    if (this.publicStatusRetryTimer) clearTimeout(this.publicStatusRetryTimer);
    this.publicStatusRetryScheduledFor = target;
    this.publicStatusRetryTimer = setTimeout(() => {
      this.publicStatusRetryTimer = null;
      this.publicStatusRetryScheduledFor = 0;
      if (Date.now() < this.publicStatusRateLimitedUntil) {
        this.schedulePublicStatusRetry({}, this.publicStatusRateLimitedUntil - Date.now());
        return;
      }
      const latest = this.publicStatusRetryOptions || {};
      this.publicStatusRetryOptions = null;
      this.publicStatusRateLimitedUntil = 0;
      this.refreshPublicStatus(latest).catch(error => this.reportError(error));
    }, Math.max(0, target - Date.now()));
    this.publicStatusRetryTimer.unref?.();
  }
  clearPublicStatusRetry() {
    if (this.publicStatusRetryTimer) clearTimeout(this.publicStatusRetryTimer);
    this.publicStatusRetryTimer = null;
    this.publicStatusRetryOptions = null;
    this.publicStatusRetryScheduledFor = 0;
    this.publicStatusRateLimitedUntil = 0;
    this.publicStatusRateLimitStreak = 0;
  }
  async refreshPublicStatus(options = {}) {
    if (!channelId(this.config, 'publicStatus')) return null;
    if (this.publicStatusRefreshPromise) {
      this.publicStatusQueuedOptions = mergePublicStatusOptions(this.publicStatusQueuedOptions || {}, options);
      return this.publicStatusRefreshPromise;
    }
    if (Date.now() < this.publicStatusRateLimitedUntil && options.moveToBottom !== true) {
      this.schedulePublicStatusRetry(options, this.publicStatusRateLimitedUntil - Date.now());
      return this.publicStatusMessage;
    }
    this.publicStatusRefreshPromise = (async () => {
      let next = options;
      let result = null;
      do {
        result = await this.refreshPublicStatusUnlocked(next);
        next = this.publicStatusQueuedOptions;
        this.publicStatusQueuedOptions = null;
      } while (next);
      return result;
    })();
    try { return await this.publicStatusRefreshPromise; }
    finally {
      this.publicStatusRefreshPromise = null;
      if (this.publicStatusMoveQueued) { this.publicStatusMoveQueued = false; this.schedulePublicStatusMove(0); }
    }
  }
  async refreshPublicStatusUnlocked(options = {}) {
    if (Date.now() < this.publicStatusRateLimitedUntil && options.moveToBottom !== true) {
      this.schedulePublicStatusRetry(options, this.publicStatusRateLimitedUntil - Date.now());
      return this.publicStatusMessage;
    }
    const channel = await this.getChannel('publicStatus'); if (!channel?.isTextBased()) return null;
    if (!this.publicStatusMessage && this.publicStatusMessageId && this.publicStatusChannelId === channel.id) {
      this.publicStatusMessage = await discordOperation(channel.messages.fetch(this.publicStatusMessageId), 'Public status message fetch').catch(() => null);
      if (!isPublicStatusMessage(this.publicStatusMessage, this.client.user.id)) this.publicStatusMessage = null;
    }
    if (!this.publicStatusMessage) {
      const messages = await discordOperation(channel.messages.fetch({ limit: 100 }), 'Public status history fetch').catch(() => null);
      this.publicStatusMessage = messages?.find(message => isPublicStatusMessage(message, this.client.user.id)) || null;
    }
    if (options.moveToBottom === true && this.publicStatusMessage) {
      const oldId = this.publicStatusMessage.id; this.expectedPublicStatusDeletes.add(oldId);
      try { await discordOperation(this.publicStatusMessage.delete(), 'Public status move/delete'); }
      catch (error) { if (Number(error?.code) !== 10008 && Number(error?.status) !== 404) throw error; }
      finally { transientTimer(() => this.expectedPublicStatusDeletes.delete(oldId), 5000); }
      this.publicStatusMessage = null; this.publicStatusMessageId = null; this.publicStatusChannelId = null;
    }
    const status = publicStatusForDisplay(options.status || await this.controller.status(), this.publicStatusOnline);
    const careerPromise = options.careerStats
      ? Promise.resolve(options.careerStats)
      : (typeof this.universe.careerStats === 'function' ? this.universe.careerStats().catch(() => null) : Promise.resolve(null));
    const [backups, careerStats] = await Promise.all([options.backups || this.universe.listBackups(), careerPromise]);
    const update = options.update || await this.updateStatus();
    const latestBackup = backups?.[0] || null;
    const coreKey = JSON.stringify({
      online: status?.online === true,
      observation: status?.processObservation?.state || null,
      ready: status?.ready === true,
      players: Number.isFinite(Number(status?.playerCount)) ? Number(status.playerCount) : null,
      update: update ? [deploymentDisplayState(update), update.status === 'failed' ? update.error : null] : null
    });
    const dataKey = JSON.stringify({
      backup: latestBackup ? [latestBackup.id, latestBackup.createdAt, latestBackup.size] : null,
      funds: Number.isFinite(Number(careerStats?.funds)) ? Math.round(Number(careerStats.funds)) : null,
      science: Number.isFinite(Number(careerStats?.science)) ? Number(careerStats.science).toFixed(1) : null
    });
    const now = Date.now();
    const coreChanged = coreKey !== this.publicStatusLastCoreKey;
    const dataChanged = dataKey !== this.publicStatusLastDataKey;
    const age = now - this.publicStatusLastWrittenAt;
    if (this.publicStatusMessage && options.moveToBottom !== true && !coreChanged && (!dataChanged || age < PUBLIC_STATUS_DATA_REFRESH_MS) && age < PUBLIC_STATUS_HEARTBEAT_MS) return this.publicStatusMessage;
    const payload = { embeds: [publicStatusEmbed(status, latestBackup, now, careerStats, this.config.discord.publicServerPassword, update)] };
    if (this.publicStatusMessage) {
      try { await this.publicStatusMessage.edit(payload); }
      catch (error) {
        if (isDiscordRateLimit(error)) {
          this.publicStatusRateLimitStreak += 1;
          const backoffMs = Math.min(60_000, 500 * (2 ** Math.min(7, this.publicStatusRateLimitStreak - 1)));
          const delayMs = Math.max(discordRateLimitDelay(error), backoffMs);
          this.schedulePublicStatusRetry({ ...options, status, backups, careerStats, update }, delayMs);
          if (Date.now() - this.publicStatusLastRateLimitEventAt >= 60_000) {
            this.publicStatusLastRateLimitEventAt = Date.now();
            await this.recordEvent('public-status-rate-limited', { delayMs, streak: this.publicStatusRateLimitStreak, method: error?.method || null, scope: error?.scope || null }).catch(() => {});
          }
          return this.publicStatusMessage;
        }
        if (Number(error?.code) !== 10008 && Number(error?.status) !== 404) throw error;
        this.publicStatusMessage = null;
      }
    }
    if (!this.publicStatusMessage) this.publicStatusMessage = await channel.send(payload);
    this.clearPublicStatusRetry();
    this.publicStatusMessageId = this.publicStatusMessage.id;
    this.publicStatusChannelId = channel.id;
    this.publicStatusLastCoreKey = coreKey;
    this.publicStatusLastDataKey = dataKey;
    this.publicStatusLastWrittenAt = now;
    await this.saveState();
    return this.publicStatusMessage;
  }
  async handleMessageDelete(message) {
    if (!message?.id) return;
    if (this.expectedPublicStatusDeletes.delete(message.id)) return;
    if (message.id !== this.publicStatusMessageId) return;
    this.publicStatusMessage = null; this.publicStatusMessageId = null; this.publicStatusChannelId = null;
    await this.saveState(); this.schedulePublicStatusMove(250);
  }
  async sendConsole(content, options = {}) { const channel = await this.getChannel('console'); if (channel?.isTextBased()) return channel.send({ content: clampText(content, 1900), allowedMentions: { parse: [] }, ...options }); return null; }
  async sendLunaChat(content, options = {}) { const channel = await this.getChannel('lunaChat'); if (channel?.isTextBased()) return channel.send({ content: clampText(content, 1900), allowedMentions: { parse: [] }, ...options }); return null; }
  enqueueChatDelivery(chat) {
    const security = this.config.security || {}; const max = Math.max(1, Number(security.chatQueueMax || 200));
    const key = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    this.chatDeliveryQueue.set(key, { chat, queuedAt: Date.now() });
    while (this.chatDeliveryQueue.size > max) this.chatDeliveryQueue.delete(this.chatDeliveryQueue.keys().next().value);
    if (!this.chatRetryTimer) { this.chatRetryTimer = setInterval(() => this.flushChatDeliveryQueue().catch(error => this.reportError(error)), 5000); this.chatRetryTimer.unref?.(); }
  }
  async flushChatDeliveryQueue() {
    if (this.chatFlushPromise) return this.chatFlushPromise;
    this.chatFlushPromise = this.flushChatDeliveryQueueUnlocked();
    try { return await this.chatFlushPromise; }
    finally { this.chatFlushPromise = null; }
  }
  async flushChatDeliveryQueueUnlocked() {
    const security = this.config.security || {}; const ttl = Math.max(10_000, Number(security.chatQueueTtlMs || 300_000));
    for (const [key, item] of this.chatDeliveryQueue) {
      if (Date.now() - item.queuedAt > ttl) { this.chatDeliveryQueue.delete(key); continue; }
      const delivered = await this.mirrorChat(item.chat, { retainOnFailure: false });
      if (delivered) this.chatDeliveryQueue.delete(key); else break;
    }
    if (!this.chatDeliveryQueue.size && this.chatRetryTimer) { clearInterval(this.chatRetryTimer); this.chatRetryTimer = null; }
  }
  async publishCommandOutput(label, result) { if (result?.output) await this.sendConsole(`/${redactCommandText(label)}\n${fence}text\n${clampText(redactSensitiveOutput(result.output), 1700)}\n${fence}`).catch(() => {}); }
  async reportError(error) { console.error(error); await this.sendConsole('Bot error: ' + clampText(publicErrorText(error), 1600)).catch(() => {}); }
  async handleInteractionError(interaction, error) {
    await this.reportError(error);
    const content = 'Operation failed: ' + clampText(publicErrorText(error), 900);
    if (interaction?.deferred || interaction?.replied) await interaction.editReply({ content, components: [] }).catch(() => {});
    else await interaction?.reply?.({ content, ephemeral: true }).catch(() => {});
  }
  async audit(actor, action, result) {
    if (!this.config.security.auditAllActions) return;
    const who = actor?.user?.tag || actor?.user?.username || 'system';
    const safeAction = redactCommandText(action);
    const safeResult = typeof result === 'string' ? result : { ...result, command: result?.command ? redactCommandText(result.command) : result?.command, output: result?.output ? clampText(redactSensitiveOutput(result.output), 1000) : undefined };
    if (safeAction.toLowerCase() === 'say' && safeResult && typeof safeResult === 'object' && safeResult.command) safeResult.command = '<redacted>';
    const record = { at: new Date().toISOString(), actorId: actor?.user?.id || null, actor: who, action: safeAction, result: safeResult };
    await appendJsonLine(this.auditPath, record).catch(error => console.error('Audit write failed:', error));
    await this.sendConsole('**' + who + '** · ' + safeAction + ' · ' + JSON.stringify(record.result)).catch(() => {});
  }
  async auditTail(lines = 20) { return tailText(this.auditPath, Math.max(1, Math.min(40, lines))) || 'No local audit records.'; }
  async recordEvent(type, data = {}) { await appendJsonLine(this.eventPath, { at: new Date().toISOString(), type, ...data }).catch(error => console.error('Event write failed:', error)); }
  async eventTail(lines = 20) { return tailText(this.eventPath, Math.max(1, Math.min(80, lines))) || 'No local events.'; }
  allowedChannel(interaction, key) { const expected = channelId(this.config, key); return !expected || interaction.channelId === expected; }
  managedChannelAccess(interaction) {
    return ['dashboard', 'console'].some(key => {
      const expected = channelId(this.config, key);
      return Boolean(expected && interaction.channelId === expected);
    });
  }
  canOperate(interaction) { return this.managedChannelAccess(interaction) || isOperator(interaction.member, this.config); }
  canModerate(interaction) { return this.managedChannelAccess(interaction) || isModerator(interaction.member, this.config); }
  cooldown(interaction) { const now = Date.now(); for (const [id, until] of this.cooldowns) if (until <= now) this.cooldowns.delete(id); const key = interaction.user.id; const until = this.cooldowns.get(key) || 0; if (until > now) return false; this.cooldowns.set(key, now + (this.config.security.commandCooldownMs || 750)); return true; }
  async cleanReply(interaction, content) { const result = await interaction.editReply(content); transientTimer(() => interaction.deleteReply().catch(() => {}), this.config.security.deleteTransientResponsesMs || 12000); return result; }

  async handleDeploy(interaction) {
    if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
    if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
    if (!this.updates) return interaction.reply({ content: 'Update manager unavailable.', ephemeral: true });
    const sub = interaction.options.getSubcommand();
    await interaction.deferReply({ ephemeral: false });
    let result;
    if (sub === 'status') {
      result = await this.updates.status();
      return interaction.editReply({ content: fence + 'json\n' + clampText(JSON.stringify(result, null, 2), 1800) + '\n' + fence });
    }
    if (sub === 'staged') {
      const rows = (await this.updates.status()).staged || [];
      return interaction.editReply({ content: rows.length ? rows.map(row => `${row.id} · ${row.version} · ${row.files} files${row.notes ? ` · ${row.notes}` : ''}`).join('\n') : 'No valid staged updates found.' });
    }
    if (sub === 'queue') {
      const id = interaction.options.getString('id');
      const minutes = interaction.options.getInteger('minutes') || undefined;
      const changelog = interaction.options.getString('changelog') || '';
      result = await this.updates.queue(id, interaction.user.tag || interaction.user.username || interaction.user.id, { countdownMinutes: minutes, changelog });
      await this.audit(interaction, 'deploy-queue', result);
      if (result.ok) {
        await this.safeRefreshDashboard();
        await this.syncPublicStatus();
        await this.sendConsole(`Update **${result.state.version}** scheduled for <t:${Math.floor(Date.parse(result.state.deployAt) / 1000)}:R>; deploys immediately if the server empties.`);
      }
      return interaction.editReply({ content: result.ok ? `Scheduled **${result.state.version}** for <t:${Math.floor(Date.parse(result.state.deployAt) / 1000)}:R>. It deploys immediately if everyone leaves.` : `Queue blocked: **${result.reason}**.` });
    }
    if (sub === 'cancel') {
      result = await this.updates.cancel();
      await this.audit(interaction, 'deploy-cancel', result);
      await this.safeRefreshDashboard(); await this.syncPublicStatus();
      return interaction.editReply({ content: result.ok ? 'Queued update cancelled.' : `Cancel blocked: **${result.reason}**.` });
    }
    if (sub === 'force') {
      result = await this.updates.force();
      await this.audit(interaction, 'deploy-force-arm', result);
      if (result.ok) { await this.safeRefreshDashboard(); await this.syncPublicStatus(); await this.sendConsole('Forced update deployment armed; connected players may be interrupted.'); }
      return interaction.editReply({ content: result.ok ? 'Forced deployment armed. It will run on the next monitor cycle.' : `Force blocked: **${result.reason}**.` });
    }
    return interaction.editReply({ content: 'Unknown deploy operation.' });
  }

  async handleInteraction(interaction) {
    await this.expireMaintenance();
    if (!this.cooldown(interaction)) return interaction.reply({ content: 'Rate limited; retry in a moment.', ephemeral: true }).catch(() => {});
    if (interaction.isButton()) return this.handleButton(interaction);
    if (!interaction.isChatInputCommand()) return;
    if (interaction.commandName === 'status') {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status(); return interaction.editReply({ embeds: [statusEmbed(status, this.maintenance, null, this.maintenanceUntil, await this.updateStatus())] });
    }
    if (interaction.commandName === 'health') {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status(); return interaction.editReply({ content: `**Health:** ${status.healthSummary}\nWarnings: ${status.healthWarnings?.length ? status.healthWarnings.join('; ') : 'none'}`, embeds: [statusEmbed(status, this.maintenance, null, this.maintenanceUntil, await this.updateStatus())] });
    }
    if (['diagnose', 'bridge', 'modcontrol'].includes(interaction.commandName)) {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status();
      if (interaction.commandName === 'bridge') return interaction.editReply({ content: fence + 'json\n' + JSON.stringify(status.bridge, null, 2) + '\n' + fence });
      if (interaction.commandName === 'modcontrol') return interaction.editReply({ content: fence + 'json\n' + JSON.stringify(status.modControl, null, 2) + '\n' + fence });
      const log = await this.controller.readLogTail(12); const backupRows = await this.universe.listBackups();
      return interaction.editReply({ content: clampText(`**Diagnostic snapshot**\nHealth: ${status.healthSummary}\nOnline: ${status.online} · Ready: ${status.ready} · Registered: ${status.registration}\nMaster: ${status.master?.visible ? 'visible' : 'not visible'}\nBridge: ${status.bridge?.ready ? 'ready' : 'not ready'}\nPlayers: ${status.playerCount ?? 'unknown'}\nLatest backup: ${backupRows[0]?.id || 'none'}\nRecent log tail:\n${fence}text\n${log.text || 'none'}\n${fence}`, 1900), embeds: [statusEmbed(status, this.maintenance, null, this.maintenanceUntil)] });
    }
    if (interaction.commandName === 'help') {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      return interaction.reply({ content: 'Complete APF command catalog:', embeds: commandHelpEmbeds(), ephemeral: false });
    }
    if (['players', 'who'].includes(interaction.commandName)) {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); return interaction.editReply({ content: await this.playerText() });
    }
    if (['server-time', 'motd', 'rules'].includes(interaction.commandName)) {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      if (interaction.commandName === 'server-time') return interaction.reply({ content: this.serverTimeText(), ephemeral: false });
      if (interaction.commandName === 'motd') return interaction.reply({ content: clampText(cleanMention(this.config.discord.motd || 'No message of the day configured.'), 1900), allowedMentions: { parse: [] }, ephemeral: false });
      return interaction.reply({ content: clampText(cleanMention(this.config.discord.rules || 'No server rules configured.'), 1900), allowedMentions: { parse: [] }, ephemeral: false });
    }
    if (['settings', 'plugins', 'connections', 'locks', 'clients'].includes(interaction.commandName)) {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      const requiredRole = interaction.commandName === 'locks' ? this.canOperate(interaction) : this.canModerate(interaction);
      if (!['settings', 'plugins'].includes(interaction.commandName) && !requiredRole) return interaction.reply({ content: interaction.commandName === 'locks' ? 'Operator role required.' : 'Moderator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status();
      if (interaction.commandName === 'settings') {
        const safe = { name: status.settings.name, description: status.settings.description, mode: status.settings.mode, maxPlayers: status.settings.maxPlayers, port: status.settings.port, register: status.settings.register, modControl: status.settings.modControl, modControlSummary: status.modControl, backupIntervalMs: status.settings.backupIntervalMs, websiteEnabled: status.settings.websiteEnabled, websitePort: status.settings.websitePort, chatBridge: status.bridge };
        return interaction.editReply({ content: fence + 'json\n' + JSON.stringify(safe, null, 2) + '\n' + fence });
      }
      if (interaction.commandName === 'plugins') return interaction.editReply({ content: clampText(status.plugins?.length ? status.plugins.map(plugin => plugin.type === 'directory' ? `${plugin.name}/ (${plugin.dlls.join(', ') || 'no DLLs found'})` : plugin.name).join('\n') : 'No plugins found.', 1900) });
      const commandMap = { connections: 'connectionstats', locks: 'listlocks', clients: 'listclients' };
      const result = await this.controller.runSafeCommand(commandMap[interaction.commandName]); await this.publishCommandOutput(commandMap[interaction.commandName], result); await this.audit(interaction, commandMap[interaction.commandName], result);
      return interaction.editReply({ content: this.resultText(result) + '\nOutput/log details will appear in #' + channelName('console') + '.' });
    }
    if (interaction.commandName === 'logs') {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      const lines = interaction.options.getInteger('lines') || 30; await interaction.deferReply({ ephemeral: false }); const log = await this.controller.readLogTail(lines);
      return interaction.editReply({ content: fence + 'text\n' + clampText(log.text, 1850) + '\n' + fence });
    }
    if (interaction.commandName === 'universe') return this.handleUniverse(interaction);
    if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
    if (interaction.commandName === 'lmp') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      const operation = interaction.options.getString('operation'); const argument = interaction.options.getString('argument') || '';
      return this.requestCommandDanger(interaction, operation, argument);
    }
    if (NATIVE_SHORTCUTS.some(([name]) => name === interaction.commandName)) {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      return this.requestCommandDanger(interaction, interaction.commandName, nativeShortcutArgument(interaction, interaction.commandName));
    }
    if (interaction.commandName === 'say') {
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const result = await this.controller.say(cleanMention(interaction.options.getString('message'))); await this.audit(interaction, 'say', result);
      return interaction.editReply({ content: this.resultText(result) });
    }
    if (['start', 'stop', 'restart'].includes(interaction.commandName)) return this.requestDanger(interaction, interaction.commandName);
    if (interaction.commandName === 'backup') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false });
      const result = await this.universe.createBackup('discord-manual'); await this.audit(interaction, 'universe-backup', result);
      return interaction.editReply({ content: result.skipped ? 'Backup already in progress; this request was not duplicated.' : 'Backup created: ' + result.id + ' · ' + result.size });
    }
    if (interaction.commandName === 'maintenance') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false });
      const mode = interaction.options.getString('mode'); const minutes = interaction.options.getInteger('minutes');
      if (mode !== 'status') { this.maintenance = mode === 'on'; this.maintenanceUntil = this.maintenance && minutes ? Date.now() + minutes * 60 * 1000 : null; await this.saveState(); }
      await this.safeRefreshDashboard();
      return interaction.editReply({ content: `Maintenance: **${this.maintenance ? 'ON' : 'OFF'}**${this.maintenanceUntil ? ` until ${new Date(this.maintenanceUntil).toISOString()}` : ''}` });
    }
    if (interaction.commandName === 'deploy') return this.handleDeploy(interaction);
    if (interaction.commandName === 'command') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      const requested = interaction.options.getString('command');
      return this.requestCommandDanger(interaction, requested.split(/\s+/, 1)[0].replace(/^\//, ''), requested.replace(/^\/?\S+\s*/, ''));
    }
    if (interaction.commandName === 'audit') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      const lines = interaction.options.getInteger('lines') || 20;
      return interaction.reply({ content: fence + 'jsonl\n' + clampText(await this.auditTail(lines), 1850) + '\n' + fence, ephemeral: false });
    }
    if (interaction.commandName === 'events') {
      if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      const lines = interaction.options.getInteger('lines') || 30;
      return interaction.reply({ content: fence + 'jsonl\n' + clampText(await this.eventTail(lines), 1850) + '\n' + fence, ephemeral: false });
    }
  }

  async requestDanger(interaction, action) {
    if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
    await interaction.reply({ content: `Confirm **${action}**? This affects the LMP process.`, components: confirmButtons(action), ephemeral: true });
    transientTimer(() => interaction.deleteReply().catch(() => {}), Math.max(30_000, this.config.security.deleteTransientResponsesMs || 12_000));
  }

  async requestCommandDanger(interaction, command, argument) {
    const normalizedCommand = String(command || '').trim().replace(/^\//, '').toLowerCase();
    if (!LMP_COMMANDS.some(([name]) => name === normalizedCommand)) return interaction.reply({ content: `Command not available: **${normalizedCommand || 'empty'}**. Use /help.`, ephemeral: true });
    const safeArgument = String(argument || '').replace(/[\r\n\x00-\x1f\x7f]/g, ' ').trim().slice(0, 260);
    const token = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
    this.pendingActions.set(token, { command: normalizedCommand, argument: safeArgument, userId: interaction.user.id, expiresAt: Date.now() + 30_000 });
    const preview = redactCommandText(`${normalizedCommand}${safeArgument ? ` ${safeArgument}` : ''}`);
    await interaction.reply({ content: `Confirm **/${preview}**? This changes live LMP state.`, components: commandConfirmButtons(token), ephemeral: true });
    transientTimer(() => { this.pendingActions.delete(token); interaction.deleteReply().catch(() => {}); }, 30_000);
  }

  async handleButton(interaction) {
    const action = interaction.customId.split(':')[1];
    if (!['cancel', 'confirmcmd'].includes(action) && !this.allowedChannel(interaction, 'dashboard')) return interaction.reply({ content: 'Dashboard controls only work in #' + channelName('dashboard') + '.', ephemeral: true });
    if (action === 'cancel') {
      await interaction.update({ content: 'Cancelled.', components: [] });
      transientTimer(() => interaction.deleteReply().catch(() => {}), 3000);
      return;
    }
    if (action === 'confirm') {
      const requested = interaction.customId.split(':')[2]; if (!this.canOperate(interaction)) return interaction.update({ content: 'Operator role required.', components: [] });
      if (!['start', 'stop', 'restart'].includes(requested)) return interaction.update({ content: 'Unknown lifecycle action.', components: [] });
      if (this.lifecycleBusy) return interaction.update({ content: 'Another lifecycle action is already running.', components: [] });
      await interaction.update({ content: `Executing ${requested}...`, components: [] });
      this.lifecycleBusy = true;
      let result;
      try { result = requested === 'start' ? await this.controller.start() : requested === 'stop' ? await this.controller.stop() : await this.controller.restart(); }
      finally { this.lifecycleBusy = false; }
      await this.audit(interaction, requested, result); await this.safeRefreshDashboard();
      await interaction.editReply({ content: `${requested}: ${this.resultText(result)}`, components: [] });
      transientTimer(() => interaction.deleteReply().catch(() => {}), this.config.security.deleteTransientResponsesMs || 12_000);
      return this.sendConsole(requested + ': ' + this.resultText(result));
    }
    if (action === 'confirmcmd') {
      const token = interaction.customId.split(':')[2]; const pending = this.pendingActions.get(token);
      if (!pending || pending.expiresAt < Date.now()) return interaction.update({ content: 'Expired action.', components: [] });
      if (pending.userId !== interaction.user.id || !this.canOperate(interaction)) return interaction.update({ content: 'Only the requesting operator can confirm this.', components: [] });
      this.pendingActions.delete(token);
      await interaction.update({ content: `Executing /${pending.command}...`, components: [] });
      const text = pending.argument ? `${pending.command} ${pending.argument}` : pending.command;
      const result = await this.controller.runSafeCommand(text); await this.publishCommandOutput(text, result); await this.audit(interaction, text, result); await this.safeRefreshDashboard();
      await interaction.editReply({ content: `/${redactCommandText(text)}: ${this.resultText(result)}`, components: [] });
      transientTimer(() => interaction.deleteReply().catch(() => {}), this.config.security.deleteTransientResponsesMs || 12_000);
      return this.sendConsole('LMP action /' + redactCommandText(text) + ': ' + this.resultText(result));
    }
    if (action === 'refresh') { await interaction.deferReply({ ephemeral: true }); const ok = await this.safeRefreshDashboard(); return this.cleanReply(interaction, ok ? 'Dashboard refreshed.' : 'Dashboard refresh failed; see console.'); }
    if (action === 'players') { await interaction.deferReply({ ephemeral: true }); return this.cleanReply(interaction, { content: await this.playerText() }); }
    if (action === 'health') { await interaction.deferReply({ ephemeral: true }); const status = await this.controller.status(); return this.cleanReply(interaction, { content: `**Health:** ${status.healthSummary}\n${status.healthWarnings?.length ? status.healthWarnings.join('; ') : 'All checks nominal.'}`, embeds: [statusEmbed(status, this.maintenance, null, this.maintenanceUntil)] }); }
    if (action === 'logs') {
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: true }); const log = await this.controller.readLogTail(25);
      return this.cleanReply(interaction, { content: fence + 'text\n' + clampText(log.text || 'No log output.', 1850) + '\n' + fence });
    }
    if (action === 'universe') {
      await interaction.deferReply({ ephemeral: true }); const [stats, backups] = await Promise.all([this.universe.stats(), this.universe.listBackups()]);
      transientTimer(() => interaction.deleteReply().catch(() => {}), this.config.security.deleteTransientResponsesMs || 12000);
      return interaction.editReply({ content: `**Universe**\n${stats.fileCount} files · ${stats.size}\nNewest: ${stats.newest?.path || 'none'}\nBackups: ${backups.length}\nLatest: ${backups[0]?.id || 'none'}` });
    }
    if (action === 'connections') {
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: true }); const result = await this.controller.runSafeCommand('connectionstats'); await this.publishCommandOutput('connectionstats', result); await this.audit(interaction, 'connectionstats', result);
      const response = await interaction.editReply({ content: this.resultText(result) + '\nDetails posted to #' + channelName('console') + '.' }); transientTimer(() => interaction.deleteReply().catch(() => {}), this.config.security.deleteTransientResponsesMs || 12000); return response;
    }
    if (action === 'maintenance') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: true });
      this.maintenance = !this.maintenance; this.maintenanceUntil = null; await this.saveState(); await this.safeRefreshDashboard();
      return this.cleanReply(interaction, { content: `Maintenance: **${this.maintenance ? 'ON' : 'OFF'}**` });
    }
    if (['backup', 'restart', 'stop', 'start'].includes(action)) {
      if (action !== 'backup') return this.requestDanger(interaction, action);
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: true }); const result = await this.universe.createBackup('dashboard-button'); await this.audit(interaction, 'universe-backup', result);
      return this.cleanReply(interaction, result.skipped ? 'Backup already in progress; no duplicate created.' : 'Backup created: ' + result.id);
    }
    return interaction.reply({ content: 'Unknown dashboard action.', ephemeral: true }).catch(() => {});
  }

  async handleUniverse(interaction) {
    if (!this.allowedChannel(interaction, 'console')) return interaction.reply({ content: 'Use #' + channelName('console') + '.', ephemeral: true });
    const sub = interaction.options.getSubcommand();
    if (sub === 'backups') { await interaction.deferReply({ ephemeral: false }); const rows = await this.universe.listBackups(); return interaction.editReply({ content: rows.length ? rows.slice(0, 20).map(row => `${row.id} ${row.size || 'unknown'} ${row.createdAt || 'incomplete'}`).join('\n') : 'No backups found.' }); }
    if (sub === 'backup') { if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true }); await interaction.deferReply({ ephemeral: false }); const result = await this.universe.createBackup('discord-console'); await this.audit(interaction, 'universe-backup', result); return interaction.editReply({ content: result.skipped ? 'Backup already in progress; no duplicate created.' : 'Created ' + result.id + ' ' + result.size }); }
    if (sub === 'status') { await interaction.deferReply({ ephemeral: false }); return interaction.editReply({ content: fence + 'json\n' + JSON.stringify(await this.universe.stats(), null, 2).slice(0, 1800) + '\n' + fence }); }
    if (sub === 'verify') { if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true }); await interaction.deferReply({ ephemeral: false }); const result = await this.universe.verifyBackup(interaction.options.getString('id')); return interaction.editReply({ content: fence + 'json\n' + clampText(JSON.stringify(result, null, 2), 1800) + '\n' + fence }); }
    if (sub === 'diff') { if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true }); await interaction.deferReply({ ephemeral: false }); const result = await this.universe.diffBackups(interaction.options.getString('from'), interaction.options.getString('to'), interaction.options.getInteger('limit') || 50); return interaction.editReply({ content: fence + 'json\n' + clampText(JSON.stringify(result, null, 2), 1800) + '\n' + fence }); }
    if (sub === 'changes') { await interaction.deferReply({ ephemeral: false }); const rows = await this.universe.recentChanges(interaction.options.getInteger('limit') || 20); return interaction.editReply({ content: clampText(rows.length ? fence + 'text\n' + rows.map(row => `${row.mtime} · ${formatBytes(row.size)} · ${row.path}`).join('\n') + '\n' + fence : 'No universe files found.', 1900) }); }
    if (sub === 'crafts') { await interaction.deferReply({ ephemeral: false }); const rows = await this.universe.listCraftDetails(100, interaction.options.getString('owner') || ''); return interaction.editReply({ content: clampText(rows.length ? rows.slice(0, 60).map(row => `${row.path} · ${row.name}${row.owner ? ` · owner ${row.owner}` : ''} · ${row.parts} parts · ${formatBytes(row.size)}`).join('\n') : 'No crafts found.', 1900) }); }
    if (sub === 'craft-info') { await interaction.deferReply({ ephemeral: false }); const result = await this.universe.craftInfo(interaction.options.getString('path')); return interaction.editReply({ content: fence + 'json\n' + clampText(JSON.stringify(result, null, 2), 1800) + '\n' + fence }); }
    if (sub === 'kerbals') { await interaction.deferReply({ ephemeral: false }); const rows = await this.universe.listKerbalDetails(200, interaction.options.getString('state') || ''); return interaction.editReply({ content: clampText(rows.length ? rows.slice(0, 100).map(row => `${row.name} · ${row.trait || '?'} · ${row.state || '?'} · ${row.path}`).join('\n') : 'No Kerbals found.', 1900) }); }
    if (sub === 'kerbal-info') { await interaction.deferReply({ ephemeral: false }); const result = await this.universe.kerbalInfo(interaction.options.getString('path')); return interaction.editReply({ content: fence + 'json\n' + clampText(JSON.stringify(result, null, 2), 1800) + '\n' + fence }); }
    if (sub === 'vessel-info') { await interaction.deferReply({ ephemeral: false }); const result = await this.universe.assetInfo(interaction.options.getString('path'), { rootName: 'Vessels', extensions: ['.sfs', '.txt', '.cfg', '.json', '.xml'] }); return interaction.editReply({ content: fence + 'json\n' + clampText(JSON.stringify(result, null, 2), 1800) + '\n' + fence }); }
    if (['vessels', 'scenarios', 'contracts'].includes(sub)) {
      await interaction.deferReply({ ephemeral: false }); const kind = sub === 'vessels' ? 'Vessels' : sub === 'scenarios' ? 'Scenarios' : 'Contracts';
      const rows = await this.universe.listAssets(kind); return interaction.editReply({ content: clampText(rows.length ? rows.slice(0, 80).map(row => row.path + ' · ' + formatBytes(row.size)).join('\n') : 'No ' + sub + ' files found.', 1900) });
    }
    if (sub === 'search') { await interaction.deferReply({ ephemeral: false }); const rows = await this.universe.search(interaction.options.getString('query')); return interaction.editReply({ content: clampText(rows.length ? rows.map(row => row.match + ' · ' + row.path).join('\n') : 'No matches.', 1900) }); }
    if (sub === 'export') {
      if (!this.canModerate(interaction)) return interaction.reply({ content: 'Moderator role required.', ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const result = await this.universe.exportAsset(interaction.options.getString('path')); await this.audit(interaction, 'universe-export', result);
      return interaction.editReply({ content: `Exported '${result.relative}' (${formatBytes(result.size)}).`, files: [{ attachment: result.destination, name: result.name }] });
    }
    if (sub === 'recover-craft') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      if (!this.maintenance) return interaction.reply({ content: 'Enable maintenance mode first. No live craft mutation allowed outside maintenance.', ephemeral: true });
      const backup = interaction.options.getString('backup'); const relative = interaction.options.getString('path'); const expected = `RECOVER ${backup} ${relative}`; const confirmation = interaction.options.getString('confirm');
      if (confirmation !== expected) return interaction.reply({ content: `Exact confirmation required: \`${expected}\``, ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); if ((await this.controller.status()).online) return interaction.editReply({ content: 'Server must be offline for safe craft recovery. Maintenance flag alone does not lock LMP joins.' });
      const result = await this.universe.recoverCraft(backup, relative, { maintenance: this.maintenance, serverOffline: true }); await this.audit(interaction, 'craft-recovery', result);
      return interaction.editReply({ content: `Recovered '${result.relative}' to live Crafts as '${result.name}'.` });
    }
    if (sub === 'recover-kerbal') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      if (!this.maintenance) return interaction.reply({ content: 'Enable maintenance mode first. No live Kerbal mutation allowed outside maintenance.', ephemeral: true });
      const backup = interaction.options.getString('backup'); const relative = interaction.options.getString('path'); const expected = `RECOVER ${backup} ${relative}`; const confirmation = interaction.options.getString('confirm');
      if (confirmation !== expected) return interaction.reply({ content: `Exact confirmation required: \`${expected}\``, ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); if ((await this.controller.status()).online) return interaction.editReply({ content: 'Server must be offline for safe Kerbal recovery. Maintenance flag alone does not lock LMP joins.' });
      const result = await this.universe.recoverKerbal(backup, relative, { maintenance: this.maintenance, serverOffline: true }); await this.audit(interaction, 'kerbal-recovery', result);
      return interaction.editReply({ content: `Recovered '${result.relative}' to live Kerbals as '${result.name}'.` });
    }
    if (['recover-vessel', 'recover-scenario'].includes(sub)) {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      if (!this.maintenance) return interaction.reply({ content: 'Enable maintenance mode first. No live asset mutation allowed outside maintenance.', ephemeral: true });
      const backup = interaction.options.getString('backup'); const relative = interaction.options.getString('path'); const expected = `RECOVER ${backup} ${relative}`; const confirmation = interaction.options.getString('confirm');
      if (confirmation !== expected) return interaction.reply({ content: `Exact confirmation required: \`${expected}\``, ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status();
      if (status.online !== false) return interaction.editReply({ content: 'Server must be confirmed offline for safe asset recovery. No files changed.' });
      const result = sub === 'recover-vessel'
        ? await this.universe.recoverVessel(backup, relative, { maintenance: this.maintenance, serverOffline: true })
        : await this.universe.recoverScenario(backup, relative, { maintenance: this.maintenance, serverOffline: true });
      await this.audit(interaction, sub, result);
      return interaction.editReply({ content: `Recovered '${result.relative}' to the live universe as '${result.name}'.` });
    }
    if (sub === 'restore-plan') { if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true }); await interaction.deferReply({ ephemeral: false }); const plan = await this.universe.restorePlan(interaction.options.getString('id')); return interaction.editReply({ content: '**Non-destructive plan**\nBackup: ' + plan.id + '\nFiles: ' + plan.fileCount + ' · ' + plan.size + '\nTarget: ' + plan.target + '\nRestore enabled: ' + plan.enabled + '\nRequires maintenance + offline server.\nConfirmation: `' + plan.confirmation + '`\nNo files changed.' }); }
    if (sub === 'restore-points') { if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true }); await interaction.deferReply({ ephemeral: false }); const rows = await this.universe.listRestorePoints(); return interaction.editReply({ content: rows.length ? rows.map(row => `${row.id} · ${row.createdAt}`).join('\n') : 'No retained restore points.' }); }
    if (sub === 'restore') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      if (!this.maintenance) return interaction.reply({ content: 'Enable maintenance mode first.', ephemeral: true });
      const backup = interaction.options.getString('id'); const expected = `RESTORE ${backup}`; const confirmation = interaction.options.getString('confirm');
      if (confirmation !== expected) return interaction.reply({ content: `Exact confirmation required: \`${expected}\``, ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status(); if (status.online !== false) return interaction.editReply({ content: 'Server must be confirmed offline for full restore. No files changed.' });
      const result = await this.universe.restore(backup, confirmation, { maintenance: this.maintenance, serverOffline: true }); await this.audit(interaction, 'universe-restore', result);
      return interaction.editReply({ content: `Universe restored from ${backup}. Rollback copy retained at ${result.rollbackRoot}.` });
    }
    if (sub === 'rollback') {
      if (!this.canOperate(interaction)) return interaction.reply({ content: 'Operator role required.', ephemeral: true });
      if (!this.maintenance) return interaction.reply({ content: 'Enable maintenance mode first.', ephemeral: true });
      const point = interaction.options.getString('point'); const expected = `ROLLBACK ${point}`; const confirmation = interaction.options.getString('confirm');
      if (confirmation !== expected) return interaction.reply({ content: `Exact confirmation required: \`${expected}\``, ephemeral: true });
      await interaction.deferReply({ ephemeral: false }); const status = await this.controller.status(); if (status.online !== false) return interaction.editReply({ content: 'Server must be confirmed offline for rollback. No files changed.' });
      const result = await this.universe.rollback(point, confirmation, { maintenance: this.maintenance, serverOffline: true }); await this.audit(interaction, 'universe-rollback', result);
      return interaction.editReply({ content: `Universe rolled back to ${point}. Displaced current copy retained at ${result.displacedCurrent}.` });
    }
    return interaction.reply({ content: 'Unknown universe operation.', ephemeral: true }).catch(() => {});
  }

  serverTimeText() {
    const timezone = this.config.discord.timezone || 'UTC';
    try {
      const formatted = new Intl.DateTimeFormat('en-GB', { timeZone: timezone, dateStyle: 'full', timeStyle: 'long' }).format(new Date());
      return `**Server time**\n${formatted}\nTimezone: \`${timezone}\``;
    } catch {
      return `**Server time**\n${new Date().toISOString()}\nTimezone configuration invalid: \`${timezone}\``;
    }
  }

  async playerText() {
    const status = await this.controller.status();
    const rows = status.players?.length ? status.players.map(player => `- **${cleanMention(player.name)}** - KSP ${player.kspVersion} - LMP ${player.lmpVersion}`).join('\n') : 'No active named players.';
    return clampText(`Server: **${status.settings.name}**\nOnline: **${playerCountText(status.playerCount)}**\nActive players:\n${rows}\n\nUse /clients for authoritative live connection details.`, 1900);
  }
  resultText(result) { const command = result?.command ? (String(result.command).startsWith('[Discord]') ? '<redacted>' : redactCommandText(result.command)) : result?.pid; return result?.ok ? `OK: ${command || 'done'}` : `Blocked: ${result?.reason || 'unknown failure'}`; }

  async handleMessage(message) {
    if (message.channelId === channelId(this.config, 'publicStatus')) {
      const isStatus = isPublicStatusMessage(message, this.client.user?.id);
      const suppressedBotMessage = message.author?.bot && Date.now() < this.publicStatusSuppressBotMessageUntil;
      if (!isStatus && !suppressedBotMessage) this.schedulePublicStatusMove();
      if (message.channelId !== channelId(this.config, 'lunaChat')) return;
    }
    if (message.author.bot || message.channelId !== channelId(this.config, 'lunaChat')) return;
    const now = Date.now(); for (const [id, until] of this.chatCooldowns) if (until <= now) this.chatCooldowns.delete(id); const previous = this.chatCooldowns.get(message.author.id) || 0;
    if (previous > now) return;
    this.chatCooldowns.set(message.author.id, now + (this.config.security.chatCooldownMs || 750));
    const text = cleanMention(message.content).trim(); if (!text) return;
    if (text.length > (this.config.security.maxMessageLength || 500)) return message.reply('Message too long.').then(reply => transientTimer(() => reply.delete().catch(() => {}), 8000));
    const displayName = cleanMention(message.member?.displayName || message.author.globalName || message.author.displayName || message.author.username || 'Discord').trim().slice(0, 64) || 'Discord';
    const result = await this.controller.say(text, displayName);
    if (result.ok) {
      const key = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
      this.outboundChats.set(key, { text, author: displayName, timestamp: Date.now() });
    }
    if (!result.ok) await message.reply(`Chat relay unavailable: **${result.reason}**. Server is observe-only until supervisor/bridge deployment.`).then(reply => transientTimer(() => reply.delete().catch(() => {}), 12000));
  }

  async mirrorChat(chat, options = {}) {
    // Log-tail and bridge events for one LMP message arrive concurrently. Serialize
    // matching payloads so the first source can register the cross-source copy before
    // the second source checks deduplication. Same-source repeated messages remain valid.
    const sourceKey = `${chat?.from || ''}\u0000${chat?.text || ''}`;
    const previous = this.chatMirrorChains.get(sourceKey) || Promise.resolve();
    const current = previous.catch(() => {}).then(() => this.mirrorChatUnlocked(chat, options));
    this.chatMirrorChains.set(sourceKey, current);
    try { return await current; }
    finally { if (this.chatMirrorChains.get(sourceKey) === current) this.chatMirrorChains.delete(sourceKey); }
  }

  async mirrorChatUnlocked(chat, options = {}) {
    const channel = await this.getChannel('lunaChat');
    if (!channel?.isTextBased()) { if (options.retainOnFailure) this.enqueueChatDelivery(chat); return false; }
    const now = Date.now();
    const origin = `${chat.from || ''} ${chat.text || ''}`;
    const isDiscordOrigin = /^\s*\[?Discord\]?/i.test(origin);
    // Discord-origin messages are already visible as the user's original post.
    // Never mirror their LMP echo back into Discord. This unconditional origin
    // guard also closes the race where the bridge event arrives before the
    // outbound tracking entry is recorded.
    if (isDiscordOrigin) return true;
    for (const [key, pending] of this.outboundChats) {
      if (now - pending.timestamp > 5000) this.outboundChats.delete(key);
      else if ((chat.text === pending.text || chat.text.endsWith(`: ${pending.text}`)) && (!pending.author || origin.toLowerCase().includes(String(pending.author).toLowerCase()))) { this.outboundChats.delete(key); return true; }
    }
    const source = String(chat.source || options.source || '').toLowerCase();
    const contentKey = chatFingerprint(chat);
    const restartWindowMs = Math.max(5000, Number(this.config.security?.chatRestartDedupeWindowMs || 30000));
    this.restartChatDeliveries = this.restartChatDeliveries.filter(entry => now - entry.timestamp <= restartWindowMs);
    const replayedAcrossRestart = this.restartChatDeliveries.some(entry => entry.fingerprint === contentKey
      && ((chat.id && entry.id && String(chat.id) === entry.id) || (source && entry.source && source !== entry.source)));
    if (replayedAcrossRestart) return true;
    const sourceKey = contentKey;
    const crossSourceWindowMs = Math.max(5000, Number(this.config.security?.chatCrossSourceWindowMs || 30000));
    for (const [oldKey, entries] of this.crossSourceChats) {
      const current = entries.filter(entry => now - entry.timestamp <= crossSourceWindowMs);
      if (current.length) this.crossSourceChats.set(oldKey, current); else this.crossSourceChats.delete(oldKey);
    }
    if (source === 'log' || source === 'bridge') {
      const entries = this.crossSourceChats.get(sourceKey) || [];
      const counterpart = entries.findIndex(entry => entry.source !== source);
      if (counterpart >= 0) {
        entries.splice(counterpart, 1);
        if (entries.length) this.crossSourceChats.set(sourceKey, entries); else this.crossSourceChats.delete(sourceKey);
        return true;
      }
    }
    const key = chat.id ? `id:${chat.id}` : `${chat.from}\u0000${chat.text}`;
    for (const [oldKey, timestamp] of this.recentChats) if (now - timestamp > 3000) this.recentChats.delete(oldKey);
    if (this.recentChats.has(key)) return true;
    // One LMP broadcast can surface through multiple bridge callbacks (one per
    // connected client) plus the server log. IDs differ, but author/text match.
    // Suppress that short-lived duplicate while allowing a real repeated message
    // after the configured window.
    const duplicateWindowMs = Math.max(250, Number(this.config.security?.chatDuplicateWindowMs || 3000));
    for (const [oldKey, timestamp] of this.recentChatContents) if (now - timestamp > duplicateWindowMs) this.recentChatContents.delete(oldKey);
    if (this.recentChatContents.has(contentKey)) return true;
    this.recentChats.set(key, now);
    const from = clampText(cleanMention(chat.from), 64); const text = clampText(cleanMention(chat.text), this.config.security.maxMessageLength || 500);
    try {
      await channel.send({ content: `**${from}**: ${text}`, allowedMentions: { parse: [] } });
      this.recentChatContents.set(contentKey, now);
      this.chatDeliveryLedger.push({ fingerprint: contentKey, source, id: String(chat.id || '').slice(0, 80), timestamp: now });
      await this.saveState().catch(error => console.error('Chat delivery ledger write failed:', error));
      if (source === 'log' || source === 'bridge') {
        const entries = this.crossSourceChats.get(sourceKey) || [];
        entries.push({ source, timestamp: now }); this.crossSourceChats.set(sourceKey, entries);
      }
      return true;
    }
    catch { this.recentChats.delete(key); if (options.retainOnFailure) this.enqueueChatDelivery(chat); return false; }
  }
}
