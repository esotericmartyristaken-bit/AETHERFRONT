import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { commandHelpEmbeds, commands, dashboardButtons, DiscordBot, discordRuntimeProblems, playerCountText, publicErrorText, publicStatusEmbed, redactCommandText, redactLocalPaths, redactSensitiveOutput, rejectPublicStatusRateLimit } from '../src/bot.js';
import { BridgeEventTailer, LmpController, LogTailer, memoryThresholdExceeded, parseBridgeJson, resolvePlayerCounts, shortLeaveReason } from '../src/lmp.js';
import { UniverseManager } from '../src/universe.js';
import { UpdateManager } from '../src/update-manager.js';
import { writeJsonAtomic } from '../src/util.js';
import { HealthMonitor, LogAlertDeduper, backupStale, healthIssues, healthKey, healthTransitions, isActionableLmpLogLine, logAlertFingerprint, statusPollDelay } from '../src/health.js';
import { acquireSingleInstance } from '../src/single-instance.js';
import { udpHealthProblems } from '../src/doctor.js';
import { clientTimeoutAdvice, likelyClientSideTimeout, matchesDefaultClientTimeout, parseNetworkTimeoutLine, TimeoutAdviceTracker, timeoutGapSeconds } from '../src/timeout-help.js';

test('slash command catalog contains the operational surface', () => {
  const names = commands().map(command => command.name);
  assert.deepEqual(names, ['help', 'status', 'health', 'diagnose', 'bridge', 'modcontrol', 'players', 'who', 'settings', 'plugins', 'logs', 'connections', 'locks', 'clients', 'server-time', 'motd', 'rules', 'lmp', 'say', 'start', 'stop', 'restart', 'backup', 'maintenance', 'deploy', 'events', 'audit', 'command', 'countclients', 'listclients', 'listlocks', 'connectionstats', 'changesettings', 'clearvessels', 'dekessler', 'nukeksc', 'kick', 'ban', 'vessel', 'setfunds', 'setscience', 'restartserver', 'universe']);
  assert.ok(dashboardButtons().every(row => row.components.length <= 5));
  const help = commandHelpEmbeds(); assert.equal(help.length, 1); assert.ok(help[0].data.fields.every(field => field.value.length <= 1024));
  const lmp = commands().find(command => command.name === 'lmp');
  const operations = lmp.options.find(option => option.name === 'operation').choices.map(choice => choice.value);
  assert.ok(operations.includes('countclients')); assert.ok(operations.includes('changesettings')); assert.ok(operations.includes('setscience'));
  const universe = commands().find(command => command.name === 'universe');
  assert.ok(universe.options.some(option => option.name === 'verify')); assert.ok(universe.options.some(option => option.name === 'diff')); assert.ok(universe.options.some(option => option.name === 'recover-vessel'));
});

test('player status uses online count without maximum slots', () => {
  assert.equal(playerCountText(0), '0 players online');
  assert.equal(playerCountText(1), '1 player online');
  assert.equal(playerCountText(42), '42 players online');
  assert.equal(playerCountText(null), 'Players unknown');
});

test('Discord error text redacts host paths and omits stack traces', () => {
  const error = Object.assign(new Error("ENOENT: no such file or directory, stat 'C:\\Users\\Administrator\\Documents\\Codex\\private\\event.json'"), { code: 'ENOENT' });
  error.stack = `${error.message}\n    at async BridgeEventTailer.tick (file:///./APFDiscordBot/src/lmp.js:646:22)`;
  const safe = publicErrorText(error);
  assert.match(safe, /^Error \[ENOENT\]:/);
  assert.match(safe, /<local-path>/);
  assert.doesNotMatch(safe, /Administrator|Documents|Desktop|APFDiscordBot|BridgeEventTailer|file:\/\//);
  assert.equal(redactLocalPaths('open \\\\tsclient\\C\\Users\\jkell\\private.json failed'), 'open <local-path> failed');
});

test('bridge JSON accepts the UTF-8 BOM emitted by the .NET plugin', () => {
  assert.deepEqual(parseBridgeJson('\uFEFF{"pid":10404}'), { pid: 10404 });
});

test('public status embed is simple and omits maximum player slots', () => {
  const now = Date.parse('2026-08-13T02:00:00Z');
  const career = { funds: 5845190.253854256, science: 261.1361 };
  const embed = publicStatusEmbed({ online: true, ready: true, playerCount: 3, uptimeMs: 3600000, settings: { name: 'EXTI', mode: 'Career' }, master: { visible: true } }, { createdAt: '2026-08-13T01:00:00Z', size: '62 MB' }, now, career, '5293').toJSON();
  assert.match(embed.title, /EXTI SERVER ONLINE/);
  assert.equal(embed.description, 'Server Password: **5293**\n\nThe server is operational.');
  assert.match(embed.fields.find(field => field.name === 'Players').value, /^3 players online\n\n\*\*Uptime\*\*\n/);
  assert.match(embed.fields.find(field => field.name === 'Last backup').value, /\n\n\*\*Funds · Science\*\*\n5,845,190 · 261\.1$/);
  assert.deepEqual(embed.fields.map(field => field.name), ['Players', 'Last backup']);
  assert.equal(embed.thumbnail, undefined);
  assert.equal(JSON.stringify(embed).includes('Mode'), false);
  assert.equal(embed.fields.some(field => field.name === 'Public listing'), false);
  assert.doesNotMatch(JSON.stringify(embed), /3\//);
  assert.equal(embed.components, undefined);
  const queued = publicStatusEmbed({ online: true, ready: true, playerCount: 1, uptimeMs: 1, settings: { name: 'EXTI' }, master: { visible: true } }, null, now, null, '', { status: 'queued', version: '2.0.0', lastObservation: { playerCount: 1 } }).toJSON();
  assert.doesNotMatch(queued.description, /Update|queued/);
});

test('Discord startup preflight rejects incomplete managed-channel configuration', () => {
  const problems = discordRuntimeProblems({ discord: { guildId: 'bad', channels: { dashboard: '1', console: '2', lunaChat: '4' } } });
  assert.deepEqual(problems, ['discord.guildId', 'discord.channels.dashboard', 'discord.channels.console', 'discord.channels.lunaChat']);
  assert.deepEqual(discordRuntimeProblems({ discord: { guildId: '12345678901234567', channels: { dashboard: '12345678901234568', console: '12345678901234569', lunaChat: '12345678901234571' } } }), []);
});

test('bad Discord activation fails before login or any LMP side effect', async () => {
  const envName = 'APF_TEST_DISCORD_TOKEN'; const previous = process.env[envName]; process.env[envName] = 'test-token';
  try {
    const config = { __root: os.tmpdir(), discord: { tokenEnv: envName, guildId: '', channels: {} }, security: {}, lmp: {}, universe: {} };
    let touched = false;
    const bot = new DiscordBot(config, { status: async () => { touched = true; } }, {});
    await assert.rejects(() => bot.start(), /Discord configuration incomplete/);
    assert.equal(touched, false);
  } finally {
    if (previous === undefined) delete process.env[envName]; else process.env[envName] = previous;
  }
});

test('doctor classifies UDP bind ownership without false positives', () => {
  assert.deepEqual(udpHealthProblems({ online: false }), []);
  assert.deepEqual(udpHealthProblems({ online: true, udpBinding: { checked: false } }, 2252), []);
  assert.deepEqual(udpHealthProblems({ online: true, udpBinding: { checked: true, port: 2252, bound: false } }), ['LMP UDP 2252 is not bound']);
  assert.deepEqual(udpHealthProblems({ online: true, udpBinding: { checked: true, port: 2252, bound: true, ownedByProcess: false } }), ['LMP UDP 2252 is owned by another process']);
  assert.deepEqual(udpHealthProblems({ online: true, udpBinding: { checked: true, port: 2252, bound: true, ownedByProcess: true } }), []);
});

test('Discord access preflight validates managed channels and required permissions', async () => {
  const ids = { dashboard: '12345678901234568', console: '12345678901234569', lunaChat: '12345678901234571' };
  const config = { __root: os.tmpdir(), discord: { guildId: '12345678901234567', channels: ids, pinDashboard: true }, security: {}, lmp: {}, universe: {} };
  const permissions = flags => ({ has: flag => flags.includes(flag) });
  const channels = Object.fromEntries(Object.entries(ids).map(([key, id]) => [id, { id, guildId: config.discord.guildId, isTextBased: () => true, permissionsFor: () => permissions(['ViewChannel', 'SendMessages', 'EmbedLinks', 'ReadMessageHistory']) }]));
  const bot = new DiscordBot(config, {}, {});
  bot.client = { user: { id: 'bot' }, guilds: { fetch: async id => ({ id }) }, channels: { fetch: async id => channels[id] } };
  const result = await bot.validateDiscordAccess(); assert.equal(result.guildId, config.discord.guildId); assert.equal(result.pinWarnings.length, 1);
  channels[ids.console].permissionsFor = () => permissions(['ViewChannel']);
  await assert.rejects(() => bot.validateDiscordAccess(), /console: missing SendMessages/);
});

test('bot shutdown clears retry state and disconnects Discord cleanly', () => {
  const config = { __root: os.tmpdir(), discord: { channels: {} }, security: {}, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); let destroyed = 0;
  bot.client = { destroy: () => { destroyed++; } };
  bot.chatRetryTimer = setInterval(() => {}, 60_000);
  bot.stop();
  assert.equal(destroyed, 1); assert.equal(bot.chatRetryTimer, null); assert.equal(bot.discordReady, false);
});

test('help uses structured Discord-safe output and community helpers honor channel routing', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-help-')); let payload;
  const config = { __root: temp, discord: { channels: { console: 'console' }, timezone: 'Europe/Berlin', motd: 'Welcome', rules: 'Be kind' }, security: {}, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {});
  const interaction = { user: { id: '1' }, channelId: 'console', commandName: 'help', isButton: () => false, isChatInputCommand: () => true, reply: async value => { payload = value; } };
  await bot.handleInteraction(interaction); assert.match(payload.content, /Complete APF command catalog/); assert.equal(payload.embeds.length, 1); assert.ok(payload.embeds[0].data.fields.length >= 4);
  assert.match(bot.serverTimeText(), /Server time/); bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('health transitions emit one actionable event per incident edge', () => {
  const base = { online: true, healthWarnings: [], master: { checked: true, visible: true }, bridge: { ready: true } };
  const offline = { online: false, healthWarnings: ['server offline'], master: { checked: true, visible: false }, bridge: { ready: false }, healthSummary: 'server offline' };
  const rows = healthTransitions(base, offline); assert.deepEqual(rows.map(row => row.type), ['server-offline']);
  assert.deepEqual(healthTransitions(offline, { ...base, healthSummary: 'healthy' }).map(row => row.type), ['server-online']);
  assert.deepEqual(healthTransitions(base, { ...base, master: { checked: true, visible: false }, healthSummary: 'not visible' }).map(row => row.type), ['master-lost']);
  assert.notEqual(healthKey(base), healthKey(offline));
});

test('health monitoring ignores quiet logs and debounces transient incidents', () => {
  const quiet = { online: true, healthWarnings: ['log file stale'], master: { checked: true, visible: true }, bridge: { configured: true, ready: true } };
  assert.deepEqual(healthIssues(quiet), []);
  const base = { online: true, healthWarnings: [], master: { checked: true, visible: true }, bridge: { configured: true, ready: true } };
  const lost = { ...base, master: { checked: true, visible: false }, healthWarnings: ['not visible in master listing'] };
  const monitor = new HealthMonitor({ failureConfirmations: 2, recoveryConfirmations: 2 });
  assert.deepEqual(monitor.update(base), []);
  assert.deepEqual(monitor.update(lost), []);
  assert.deepEqual(monitor.update(base), []);
  assert.deepEqual(monitor.update(lost), []);
  assert.deepEqual(monitor.update(lost).map(row => row.type), ['master-lost']);
  assert.deepEqual(monitor.update(base), []);
  assert.deepEqual(monitor.update(base).map(row => row.type), ['master-visible']);
});

test('health monitoring ignores unconfirmed or unavailable master probes', () => {
  const base = { online: true, healthWarnings: [], bridge: { configured: true, ready: true } };
  assert.deepEqual(healthIssues({ ...base, master: { checked: true, visible: false, confirmedMissing: false } }), []);
  assert.deepEqual(healthIssues({ ...base, master: { checked: false, visible: null, error: 'HTTP 503' } }), []);
  assert.deepEqual(healthIssues({ ...base, master: { checked: true, visible: false, confirmedMissing: true } }).map(row => row.code), ['master-listing']);
});

test('status polling backs off only for a healthy empty idle server', () => {
  const empty = { online: true, ready: true, playerCount: 0, connectedCount: 0 };
  assert.equal(statusPollDelay(empty, { status: 'idle' }, 5000, 30000), 30000);
  assert.equal(statusPollDelay({ ...empty, playerCount: 1 }, { status: 'idle' }, 5000, 30000), 5000);
  assert.equal(statusPollDelay({ ...empty, connectedCount: 1 }, { status: 'idle' }, 5000, 30000), 5000);
  assert.equal(statusPollDelay({ ...empty, online: false }, { status: 'idle' }, 5000, 30000), 5000);
  assert.equal(statusPollDelay(empty, { status: 'queued' }, 5000, 30000), 5000);
});

test('LMP alert filter excludes chat and routine disconnect noise', () => {
  assert.equal(isActionableLmpLogLine('[15:46:24][LMP]: Broadcasting [Discord] Yogurt: its throwing errors on console'), false);
  assert.equal(isActionableLmpLogLine('[15:46:24][Chat]: [Discord] Yogurt: its throwing errors on console'), false);
  assert.equal(isActionableLmpLogLine('[17:18:14][Debug]: Kiryu failed to release lock Control - abc - Kiryu'), false);
  assert.equal(isActionableLmpLogLine('[14:05:36][Debug]: PaulDanielz sent Connection end message, reason: Connection timed out'), false);
  assert.equal(isActionableLmpLogLine("[19:49:43][Error]: Exception while trying to delete 'C:\\server\\logs\\CraftCreationAndRemoval.txt'!, Exception: The process cannot access the file because it is being used by another process."), false);
  assert.equal(isActionableLmpLogLine('[10:20:19][Debug]: Player sent Connection end message, reason: [LMP]: Failed mod validation'), true);
  assert.equal(isActionableLmpLogLine('[15:37:47][Warning]: Cannot save flag Squad/Flags/Eagle Union from Kiryu as it\'s flag name has invalid characters'), true);
  assert.equal(isActionableLmpLogLine('[12:00:00][Error]: Unhandled exception in server loop'), true);
});

test('log alert deduper collapses timestamped exception storms and reports suppressed repeats', () => {
  const deduper = new LogAlertDeduper(300000);
  const first = '[21:05:22][Error]: Unobserved task exception: duplicate module';
  const repeat = '[21:05:54][Error]: Unobserved task exception: duplicate module';

  assert.equal(logAlertFingerprint(first), logAlertFingerprint(repeat));
  assert.deepEqual(deduper.filter([first, repeat], 1000), [first]);
  assert.deepEqual(deduper.filter([repeat], 2000), []);
  assert.match(deduper.filter([repeat], 302000)[0], /1 repeat alert\(s\) suppressed/);
});

test('single-instance lock rejects a second bot and releases cleanly', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-single-instance-'));
  const lock = path.join(temp, 'bot.lock');
  const release = acquireSingleInstance(lock);

  assert.equal(typeof release, 'function');
  assert.equal(acquireSingleInstance(lock), null);
  release();

  const reacquired = acquireSingleInstance(lock);
  assert.equal(typeof reacquired, 'function');
  reacquired();
  await fs.rm(temp, { recursive: true, force: true });
});

test('backup staleness only activates when automatic backups are configured', () => {
  const now = Date.parse('2026-08-13T00:00:00Z');
  assert.equal(backupStale(null, 0, now), null);
  assert.equal(backupStale('2026-08-12T23:30:00Z', 30, now), false);
  assert.equal(backupStale('2026-08-12T22:00:00Z', 30, now), true);
  assert.equal(backupStale('bad-date', 30, now), true);
});

test('career stats read authoritative top-level funds and science', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-career-'));
  const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups');
  const scenarios = path.join(source, 'Scenarios'); await fs.mkdir(scenarios, { recursive: true });
  await fs.writeFile(path.join(scenarios, 'Funding.txt'), 'name = Funding\nfunds = 5845190.253854256\n');
  await fs.writeFile(path.join(scenarios, 'ResearchAndDevelopment.txt'), 'name = ResearchAndDevelopment\nsci = 261.1361\nTech\n{\n\tsci = 999\n}\n');
  const manager = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups } });
  assert.deepEqual(await manager.careerStats(), { funds: 5845190.253854256, science: 261.1361 });
});

test('universe manager snapshots, searches, lists, and plans restore without live mutation', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-universe-'));
  const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups');
  await fs.mkdir(path.join(source, 'Crafts'), { recursive: true });
  await fs.mkdir(path.join(source, 'Kerbals'), { recursive: true });
  await fs.writeFile(path.join(source, 'Crafts', 'Test.craft'), 'ship = APF\nowner = Pilot');
  await fs.mkdir(path.join(source, 'Crafts', 'Owners'), { recursive: true });
  await fs.writeFile(path.join(source, 'Crafts', 'Owners', 'Nested.craft'), 'ship = Nested');
  await fs.writeFile(path.join(source, 'Crafts', 'Recovered.craft'), 'recovered = true');
  await fs.writeFile(path.join(source, 'Kerbals', 'Player.txt'), 'name = Player\nstate = Active');
  const manager = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups, maxBackups: 3, allowRestore: false } });
  const stats = await manager.stats(); assert.equal(stats.fileCount, 4);
  assert.equal((await manager.search('APF')).length, 1);
  assert.equal((await manager.listAssets('Crafts')).length, 3);
  const craftDetails = await manager.listCraftDetails(); assert.equal(craftDetails.length, 3); assert.equal(craftDetails.find(row => row.name === 'APF').parts, 0); assert.equal(craftDetails.find(row => row.name === 'APF').owner, 'Pilot'); assert.equal((await manager.listCraftDetails(100, 'pilot')).length, 1);
  const kerbalDetails = await manager.listKerbalDetails(); assert.equal(kerbalDetails[0].name, 'Player'); assert.equal(kerbalDetails[0].state, 'Active'); assert.equal((await manager.listKerbalDetails(200, 'active')).length, 1);
  const backup = await manager.createBackup('test'); assert.equal(backup.complete, true);
  assert.equal((await manager.listBackups()).length, 1);
  const verified = await manager.verifyBackup(backup.id); assert.equal(verified.ok, true); assert.equal(verified.hashVerified, true);
  await fs.appendFile(path.join(backups, backup.id, 'Crafts', 'Test.craft'), '\ntampered');
  const tampered = await manager.verifyBackup(backup.id); assert.equal(tampered.ok, false); assert.ok(tampered.hashMismatches.includes('Crafts/Test.craft'));
  await fs.copyFile(path.join(source, 'Crafts', 'Test.craft'), path.join(backups, backup.id, 'Crafts', 'Test.craft'));
  await fs.writeFile(path.join(source, 'Crafts', 'Changed.craft'), 'changed = true');
  const secondBackup = await manager.createBackup('test-second');
  const diff = await manager.diffBackups(backup.id, secondBackup.id);
  assert.equal(diff.counts.added, 1); assert.equal(diff.counts.removed, 0); assert.ok(diff.rows.some(row => row.path === 'Crafts/Changed.craft'));
  const plan = await manager.restorePlan(backup.id); assert.equal(plan.enabled, false);
  const exported = await manager.exportAsset('Crafts/Test.craft'); assert.equal(exported.size, Buffer.byteLength('ship = APF\nowner = Pilot')); assert.match(exported.destination, /exports/i);
  await fs.rm(path.join(source, 'Crafts', 'Recovered.craft'));
  await fs.rm(path.join(source, 'Crafts', 'Owners', 'Nested.craft'));
  await fs.rm(path.join(source, 'Kerbals', 'Player.txt'));
  const recoverManager = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups, maxBackups: 3, allowRestore: false, allowAssetRestore: true } });
  const recovered = await recoverManager.recoverCraft(backup.id, 'Crafts/Recovered.craft', { maintenance: true, serverOffline: true }); assert.equal(recovered.name, 'Recovered.craft');
  const nested = await recoverManager.recoverCraft(backup.id, 'Crafts/Owners/Nested.craft', { maintenance: true, serverOffline: true }); assert.equal(nested.relative, 'Crafts/Owners/Nested.craft'); assert.ok(await fs.stat(path.join(source, 'Crafts', 'Owners', 'Nested.craft')));
  const recoveredKerbal = await recoverManager.recoverKerbal(backup.id, 'Kerbals/Player.txt', { maintenance: true, serverOffline: true }); assert.equal(recoveredKerbal.name, 'Player.txt');
  await fs.rm(temp, { recursive: true, force: true });
});

test('full restore is guarded, verified, atomic, and leaves rollback copy', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-full-restore-')); const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups'); const exports = path.join(temp, 'Exports');
  await fs.mkdir(source, { recursive: true }); await fs.writeFile(path.join(source, 'state.sfs'), 'original');
  const config = { universe: { universeRoot: source, backupRoot: backups, exportRoot: exports, maxBackups: 3, allowRestore: true } }; const manager = new UniverseManager(config); const backup = await manager.createBackup('restore-test');
  await fs.writeFile(path.join(source, 'state.sfs'), 'changed'); await fs.writeFile(path.join(source, 'extra.txt'), 'new');
  const restored = await manager.restore(backup.id, `RESTORE ${backup.id}`, { maintenance: true, serverOffline: true });
  assert.equal(await fs.readFile(path.join(source, 'state.sfs'), 'utf8'), 'original'); assert.equal(await fs.stat(path.join(source, 'extra.txt')).catch(() => null), null);
  assert.equal(await fs.stat(path.join(source, 'backup.json')).catch(() => null), null); assert.ok(await fs.stat(restored.rollbackRoot));
  await fs.rm(temp, { recursive: true, force: true });
});

test('restore points can be listed and safely rolled back', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-rollback-')); const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups');
  await fs.mkdir(source, { recursive: true }); await fs.writeFile(path.join(source, 'state.sfs'), 'old');
  const manager = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups, maxBackups: 3, allowRestore: true } }); const backup = await manager.createBackup('rollback-test');
  await fs.writeFile(path.join(source, 'state.sfs'), 'new'); const restored = await manager.restore(backup.id, `RESTORE ${backup.id}`, { maintenance: true, serverOffline: true });
  const points = await manager.listRestorePoints(); assert.equal(points.length, 1); assert.equal(points[0].id, path.basename(restored.rollbackRoot));
  const rolled = await manager.rollback(points[0].id, `ROLLBACK ${points[0].id}`, { maintenance: true, serverOffline: true });
  assert.equal(await fs.readFile(path.join(source, 'state.sfs'), 'utf8'), 'new'); assert.ok(await fs.stat(rolled.displacedCurrent));
  await fs.rm(temp, { recursive: true, force: true });
});

test('universe manager rejects nested backup/export roots', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-roots-')); const source = path.join(temp, 'Universe');
  await fs.mkdir(source, { recursive: true });
  assert.throws(() => new UniverseManager({ universe: { universeRoot: source, backupRoot: path.join(source, 'Backups') } }), /backupRoot cannot be inside universeRoot/);
  assert.throws(() => new UniverseManager({ universe: { universeRoot: source, backupRoot: path.join(temp, 'Data'), exportRoot: path.join(temp, 'Data', 'Exports') } }), /backupRoot and exportRoot cannot overlap/);
  if (process.platform === 'win32') assert.throws(() => new UniverseManager({ universe: { universeRoot: source, backupRoot: path.join(source.toLowerCase(), 'CaseBackups') } }), /backupRoot cannot be inside universeRoot/);
  await fs.rm(temp, { recursive: true, force: true });
});

test('universe asset inspection/recovery covers vessels and scenarios with mutation locking', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-assets-')); const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups');
  await fs.mkdir(path.join(source, 'Vessels'), { recursive: true }); await fs.mkdir(path.join(source, 'Scenarios'), { recursive: true });
  await fs.writeFile(path.join(source, 'Vessels', 'Orbiter.sfs'), 'name = Orbiter\nvesselType = Ship\npart = one');
  await fs.writeFile(path.join(source, 'Scenarios', 'Career.sfs'), 'name = Career');
  const manager = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups, allowAssetRestore: true } }); const backup = await manager.createBackup('assets');
  const vessel = await manager.assetInfo('Vessels/Orbiter.sfs', { rootName: 'Vessels', extensions: ['.sfs'] }); assert.equal(vessel.name, 'Orbiter'); assert.equal(vessel.parts, 1);
  await assert.rejects(() => manager.recoverVessel(backup.id, 'Vessels/Orbiter.sfs', { maintenance: true }), /server must be offline/);
  await fs.rm(path.join(source, 'Vessels', 'Orbiter.sfs')); await fs.rm(path.join(source, 'Scenarios', 'Career.sfs'));
  const recoveredVessel = await manager.recoverVessel(backup.id, 'Vessels/Orbiter.sfs', { maintenance: true, serverOffline: true }); assert.equal(recoveredVessel.name, 'Orbiter.sfs');
  const recoveredScenario = await manager.recoverScenario(backup.id, 'Scenarios/Career.sfs', { maintenance: true, serverOffline: true }); assert.equal(recoveredScenario.name, 'Career.sfs');
  await assert.rejects(() => manager.listAssets('../'), /asset root is not allowed/);
  const lockPath = path.join(path.dirname(source), `${path.basename(source)}.mutation.lock`); const lock = await fs.open(lockPath, 'w'); await lock.close();
  await assert.rejects(() => manager.recoverVessel(backup.id, 'Vessels/Orbiter.sfs', { maintenance: true, serverOffline: true }), /destination vessel exists|mutation already in progress/);
  await fs.rm(lockPath, { force: true }); await fs.rm(temp, { recursive: true, force: true });
});

test('cross-process-style backup lock prevents duplicate snapshot work', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-lock-')); const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups');
  await fs.mkdir(source, { recursive: true }); await fs.writeFile(path.join(source, 'state.sfs'), 'state');
  const first = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups, maxBackups: 4 } }); const second = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups, maxBackups: 4 } });
  await fs.mkdir(backups, { recursive: true }); const lock = await fs.open(path.join(backups, '.backup.lock'), 'w'); await lock.close();
  assert.equal(await second.backupInProgress(), true);
  const skipped = await second.createBackup('concurrent'); assert.equal(skipped.skipped, true);
  await fs.rm(path.join(backups, '.backup.lock'), { force: true }); assert.equal(await second.backupInProgress(), false);
  const created = await first.createBackup('after-lock'); assert.equal(created.complete, true);
  await fs.rm(temp, { recursive: true, force: true });
});

test('incomplete backup rows sort after valid snapshots', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-backup-order-')); const source = path.join(temp, 'Universe'); const backups = path.join(temp, 'Backups');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(path.join(backups, 'deleted-during-prune'), { recursive: true });
  const manager = new UniverseManager({ universe: { universeRoot: source, backupRoot: backups } });
  const valid = path.join(backups, 'valid'); await fs.mkdir(valid); await fs.writeFile(path.join(valid, 'backup.json'), JSON.stringify({ id: 'valid', complete: true, createdAt: '2026-08-27T22:02:43.679Z' }));
  const rows = await manager.listBackups(); assert.equal(rows[0].id, 'valid'); assert.equal(rows[1].complete, false);
  await fs.rm(temp, { recursive: true, force: true });
});

test('master listing requires sustained absence and ignores probe failures', async () => {
  let now = Date.parse('2026-08-28T00:00:00Z'); let body = '<table><tr><td>Other</td><td>host:1234</td></tr></table>';
  const fetch = async () => ({ ok: true, status: 200, text: async () => body });
  const controller = new LmpController({ lmp: { serverRoot: 'C:\\missing', masterListUrl: 'https://master.test/' }, health: { masterListingGraceMinutes: 5 }, security: {} }, { fetch, now: () => now });
  const settings = { name: 'EXTI', port: 2252 };
  assert.equal((await controller.masterListing(settings)).confirmedMissing, false);
  now += 4 * 60 * 1000; assert.equal((await controller.masterListing(settings)).confirmedMissing, false);
  now += 60 * 1000; assert.equal((await controller.masterListing(settings)).confirmedMissing, true);
  body = '<table><tr><td>EXTI</td><td>85.239.237.125:2252</td></tr></table>';
  const restored = await controller.masterListing(settings); assert.equal(restored.visible, true); assert.equal(restored.missingSince, null);
  controller.fetch = async () => { throw new Error('network down'); };
  const failed = await controller.masterListing(settings); assert.equal(failed.checked, false); assert.equal(failed.visible, null);
});

// This legacy integration check needs a separately running EXTI host. Keep the
// normal isolated suite independent of production processes/configuration.
test('controller reads live LMP settings and observes current process without controlling it', {
  skip: process.env.APF_RUN_LIVE_EXTI_OBSERVER !== '1' && 'Requires explicitly enabled legacy live EXTI observer environment'
}, async () => {
  const root = '.\\EXTI-Server';
  const controller = new LmpController({ lmp: { serverRoot: root, executable: 'Server.exe', port: 2252, serverName: 'EXTI', masterListUrl: '' , control: { mode: 'observe' } }, security: {} });
  const settings = await controller.readSettings();
  assert.equal(settings.name, 'EXTI'); assert.equal(settings.port, 2252); assert.equal(settings.register, true);
  const process = await controller.processInfo();
  assert.ok(process, 'live LMP process should be discoverable'); assert.equal(process.owned, false);
  const players = await controller.playerSnapshot(); assert.ok(Array.isArray(players.players)); assert.ok(players.count === null || Number.isInteger(players.count));
  const bridge = await controller.bridgeStatus(process); assert.equal(bridge.ready, Boolean(bridge.pid && Number(bridge.pid) === Number(process.pid)));
  const modControl = await controller.modControlSummary(); assert.equal(modControl.configured, true); assert.ok(modControl.allowedParts > 0);
  const liveStatus = await controller.status(); assert.equal(liveStatus.pid, process.pid); assert.ok(liveStatus.resources?.workingSetBytes > 0); assert.ok(!liveStatus.healthWarnings.includes('log file stale'));
});

test('player snapshot replays handshakes and connection-end events', async () => {
  const controller = new LmpController({ lmp: { serverRoot: 'C:\\missing' }, security: {} });
  controller.processInfo = async () => ({ pid: 9, created: new Date().toISOString(), owned: false });
  controller.readPlayerLogDelta = async () => ({ file: 'test.log', pid: 9, reset: true, offset: 180, text: '[10:00:00][LMP]: Client Alpha (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n[10:00:01][Debug]: Online Players: 1, connected: 1\n' });
  assert.equal((await controller.playerSnapshot()).players[0].name, 'Alpha');
  controller.readPlayerLogDelta = async () => ({ file: 'test.log', pid: 9, reset: true, offset: 250, text: '[10:00:00][LMP]: Client Alpha (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n[10:00:01][Debug]: Alpha sent Connection end message, reason: Quit\n[10:00:02][Debug]: Online Players: 0, connected: 0\n' });
  assert.equal((await controller.playerSnapshot()).players.length, 0);
});

test('pre-auth socket counts do not erase established player identities', async () => {
  const controller = new LmpController({ lmp: { serverRoot: 'C:\\missing' }, security: {} });
  controller.processInfo = async () => ({ pid: 9, created: new Date().toISOString(), owned: false });
  const text = [
    '[10:00:00][LMP]: Client Alpha (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5',
    '[10:00:01][Debug]: Online Players: 1, connected: 1',
    '[10:00:02][LMP]: Client Ghost (1111111111111111) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5',
    '[10:00:03][Debug]: Online Players: 1, connected: 2',
    '[10:00:04][Debug]: Ghost sent Connection end message, reason: Quit',
    '[10:00:05][Debug]: Online Players: 1, connected: 1',
    '[10:00:06][LMP]: Client Bravo (0123456789abcdef) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5',
    '[10:00:07][Debug]: Online Players: 2, connected: 2',
    ''
  ].join('\n');
  controller.readPlayerLogDelta = async () => ({ file: 'test.log', pid: 9, reset: true, offset: text.length, text });
  const snapshot = await controller.playerSnapshot();
  assert.deepEqual(snapshot.players.map(player => player.name), ['Alpha', 'Bravo']);
  assert.equal(snapshot.count, 2);
});

test('midnight log rotation preserves established player identities', async () => {
  const controller = new LmpController({ lmp: { serverRoot: 'C:\\missing' }, security: {} });
  controller.processInfo = async () => ({ pid: 9, created: new Date().toISOString(), owned: false });
  let call = 0;
  controller.readPlayerLogDelta = async () => call++ === 0
    ? { file: 'before-midnight.log', pid: 9, reset: true, offset: 180, text: '[23:59:00][LMP]: Client Alpha (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n[23:59:01][Debug]: Online Players: 1, connected: 1\n' }
    : { file: 'after-midnight.log', pid: 9, reset: false, offset: 70, text: '[00:00:01][Debug]: Performing backups...\n[00:00:02][Debug]: Backups done\n' };
  assert.deepEqual((await controller.playerSnapshot()).players.map(player => player.name), ['Alpha']);
  const rotated = await controller.playerSnapshot();
  assert.deepEqual(rotated.players.map(player => player.name), ['Alpha']); assert.equal(rotated.count, 1);
});

test('new server PID clears sessions from the previous process and starts at zero', async () => {
  const controller = new LmpController({ lmp: { serverRoot: 'C:\\missing' }, security: {} });
  let pid = 8;
  controller.processInfo = async () => ({ pid, created: new Date().toISOString(), owned: false });
  controller.readPlayerLogDelta = async () => pid === 8
    ? { file: 'old.log', pid, reset: true, offset: 180, text: '[10:00:00][LMP]: Client PaulDanielz (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n[10:00:01][Debug]: Online Players: 1, connected: 1\n' }
    : { file: 'new.log', pid, reset: true, offset: 120, text: "[10:01:00][LMP]: Starting 'EXTI' on Address 0.0.0.0 Port 2252...\n[10:01:01][LMP]: All systems up and running.\n" };
  assert.deepEqual((await controller.playerSnapshot()).players.map(player => player.name), ['PaulDanielz']);
  pid = 9;
  const restarted = await controller.playerSnapshot();
  assert.equal(restarted.count, 0);
  assert.equal(restarted.connected, 0);
  assert.deepEqual(restarted.players, []);
});

test('player log reader never replays a log older than the current process', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-player-log-'));
  const logs = path.join(temp, 'logs');
  await fs.mkdir(logs, { recursive: true });
  await fs.writeFile(path.join(logs, 'old.log'), '[10:00:00][LMP]: Client Stale (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n');
  const controller = new LmpController({ lmp: { serverRoot: temp }, security: {} });
  const delta = await controller.readPlayerLogDelta({ pid: 9, created: new Date(Date.now() + 60_000).toISOString() }, controller.playerSession);
  assert.equal(delta.file, '');
  assert.equal(delta.text, '');
  await fs.rm(temp, { recursive: true, force: true });
});

test('player counts clear offline and prefer current server logs over stale master data', () => {
  assert.deepEqual(resolvePlayerCounts(false, 2, 2, 2, 2), { playerCount: 0, connectedCount: 0, source: 'offline' });
  assert.deepEqual(resolvePlayerCounts(true, 0, 0, 1, 1), { playerCount: 0, connectedCount: 0, source: 'server-log' });
  assert.deepEqual(resolvePlayerCounts(true, null, null, 3, 2), { playerCount: 2, connectedCount: 2, source: 'session-replay' });
  assert.deepEqual(resolvePlayerCounts(true, null, null, 3, null), { playerCount: 3, connectedCount: 3, source: 'master' });
});

test('memory threshold uses hysteresis before reporting recovery', () => {
  const threshold = 2 * 1024 * 1024 * 1024;
  assert.equal(memoryThresholdExceeded(threshold * 1.01, threshold, false), true);
  assert.equal(memoryThresholdExceeded(threshold * 0.95, threshold, false), false);
  assert.equal(memoryThresholdExceeded(threshold * 0.95, threshold, true), true);
  assert.equal(memoryThresholdExceeded(threshold * 0.84, threshold, true), false);
});

test('bridge event tailer consumes atomic chat events once', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-bridge-')); const events = path.join(temp, 'events');
  await fs.mkdir(events, { recursive: true });
  await fs.writeFile(path.join(temp, 'ready.json'), JSON.stringify({ pid: 22 }));
  await fs.writeFile(path.join(events, '000-stale.json'), JSON.stringify({ type: 'lmp-chat', serverPid: 11, from: 'Old', text: 'drop', receivedAt: new Date().toISOString() }));
  await fs.writeFile(path.join(events, '001.json'), JSON.stringify({ type: 'lmp-chat', id: 'event-001', serverPid: 22, from: 'Pilot', text: 'hello', receivedAt: new Date().toISOString() }));
  const tailer = new BridgeEventTailer({ bridgeRoot: temp, processInfo: async () => ({ pid: 22 }), config: { security: { bridgeMessageTtlMs: 120000 } } }); const seen = [];
  tailer.on('chat', event => seen.push(event)); await tailer.tick();
  assert.deepEqual(seen.map(event => [event.id, event.from, event.text]), [['event-001', 'Pilot', 'hello']]);
  assert.equal((await fs.readdir(events)).length, 0);
  await fs.rm(temp, { recursive: true, force: true });
});

test('bridge event tailer does not replay an event during overlapping polls', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-bridge-overlap-'));
  const events = path.join(temp, 'events');
  await fs.mkdir(events, { recursive: true });
  await fs.writeFile(path.join(temp, 'ready.json'), JSON.stringify({ pid: 22 }));
  await fs.writeFile(path.join(events, 'chat.json'), JSON.stringify({ type: 'lmp-chat', id: 'one', serverPid: 22, from: 'Pilot', text: 'hello', receivedAt: new Date().toISOString() }));
  const tailer = new BridgeEventTailer({ bridgeRoot: temp, processInfo: async () => ({ pid: 22 }), config: { security: { bridgeMessageTtlMs: 120000 } } });
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let calls = 0;
  tailer.on('chat', async () => { calls++; await gate; return true; });
  const first = tailer.tick();
  while (calls === 0) await new Promise(resolve => setTimeout(resolve, 1));
  const second = tailer.tick();
  release();
  await Promise.all([first, second]);
  assert.equal(calls, 1);
  assert.equal((await fs.readdir(events)).length, 0);
  await fs.rm(temp, { recursive: true, force: true });
});

test('bridge event tailer retains valid chat until delivery succeeds', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-bridge-retry-')); const events = path.join(temp, 'events');
  await fs.mkdir(events, { recursive: true }); await fs.writeFile(path.join(temp, 'ready.json'), JSON.stringify({ pid: 22 }));
  const file = path.join(events, 'retry.json'); await fs.writeFile(file, JSON.stringify({ type: 'lmp-chat', id: 'retry-1', serverPid: 22, from: 'Pilot', text: 'retry', receivedAt: new Date().toISOString() }));
  let ready = false; const tailer = new BridgeEventTailer({ bridgeRoot: temp, processInfo: async () => ({ pid: 22 }), config: { security: { bridgeMessageTtlMs: 120000 } } });
  tailer.on('chat', async () => ready); await tailer.tick(); assert.ok(await fs.stat(file)); ready = true; await tailer.tick(); assert.equal(await fs.stat(file).catch(() => null), null);
  await fs.rm(temp, { recursive: true, force: true });
});

test('bridge event tailer rejects events from a dead or different server process', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-bridge-pid-')); const events = path.join(temp, 'events');
  await fs.mkdir(events, { recursive: true }); await fs.writeFile(path.join(temp, 'ready.json'), JSON.stringify({ pid: 22 }));
  const controller = { bridgeRoot: temp, config: { security: { bridgeMessageTtlMs: 120000 } }, processInfo: async () => null };
  const tailer = new BridgeEventTailer(controller); const seen = []; tailer.on('chat', event => seen.push(event));
  await fs.writeFile(path.join(events, 'dead.json'), JSON.stringify({ type: 'lmp-chat', serverPid: 22, from: 'Dead', text: 'drop', receivedAt: new Date().toISOString() }));
  await tailer.tick(); assert.equal(seen.length, 0); assert.equal((await fs.readdir(events)).length, 0);
  await fs.rm(temp, { recursive: true, force: true });
});

test('bridge event tailer drops events missing required identity or timestamp', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-bridge-metadata-')); const events = path.join(temp, 'events');
  await fs.mkdir(events, { recursive: true }); await fs.writeFile(path.join(temp, 'ready.json'), JSON.stringify({ pid: 22 }));
  const controller = { bridgeRoot: temp, config: { security: { bridgeMessageTtlMs: 120000 } }, processInfo: async () => ({ pid: 22 }) };
  const tailer = new BridgeEventTailer(controller); const seen = []; tailer.on('chat', event => seen.push(event));
  await fs.writeFile(path.join(events, 'missing-pid.json'), JSON.stringify({ type: 'lmp-chat', text: 'drop', receivedAt: new Date().toISOString() }));
  await fs.writeFile(path.join(events, 'missing-time.json'), JSON.stringify({ type: 'lmp-chat', serverPid: 22, text: 'drop' }));
  await tailer.tick(); assert.equal(seen.length, 0); assert.equal((await fs.readdir(events)).length, 0);
  await fs.rm(temp, { recursive: true, force: true });
});

test('chat mirror deduplicates bridge event IDs without dropping distinct identical messages', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-event-id-')); const messages = [];
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); const channel = { id: 'chat', isTextBased: () => true, send: async message => { messages.push(message); } };
  bot.client = { channels: { fetch: async () => channel } };
  await bot.mirrorChat({ id: 'event-a', from: 'Pilot', text: 'same' });
  await new Promise(resolve => setTimeout(resolve, 3100));
  await bot.mirrorChat({ id: 'event-b', from: 'Pilot', text: 'same' });
  await bot.mirrorChat({ id: 'event-a', from: 'Pilot', text: 'same' });
  assert.equal(messages.length, 2); assert.deepEqual(messages[0].allowedMentions, { parse: [] }); await fs.rm(temp, { recursive: true, force: true });
});

test('chat mirror pairs bridge and log copies without dropping repeated real messages', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-cross-source-')); const messages = [];
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); const channel = { id: 'chat', isTextBased: () => true, send: async message => { messages.push(message); } };
  bot.client = { channels: { fetch: async () => channel } };
  await bot.mirrorChat({ id: 'bridge-a', source: 'bridge', from: 'Pilot', text: 'same' });
  await bot.mirrorChat({ id: 'log-a', source: 'log', from: 'Pilot', text: 'same' });
  await new Promise(resolve => setTimeout(resolve, 3100));
  await bot.mirrorChat({ id: 'log-b', source: 'log', from: 'Pilot', text: 'same' });
  await bot.mirrorChat({ id: 'bridge-b', source: 'bridge', from: 'Pilot', text: 'same' });
  assert.equal(messages.length, 2); await fs.rm(temp, { recursive: true, force: true });
});

test('chat mirror serializes concurrent bridge/log copies', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-concurrent-')); const messages = [];
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); const channel = { id: 'chat', isTextBased: () => true, send: async message => { await new Promise(resolve => setTimeout(resolve, 5)); messages.push(message); } };
  bot.client = { channels: { fetch: async () => channel } };
  await Promise.all([
    bot.mirrorChat({ id: 'bridge-race', source: 'bridge', from: 'Pilot', text: 'race' }),
    bot.mirrorChat({ id: 'log-race', source: 'log', from: 'Pilot', text: 'race' })
  ]);
  assert.equal(messages.length, 1); await fs.rm(temp, { recursive: true, force: true });
});

test('chat mirror suppresses duplicate payloads with different event IDs', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-content-dedupe-')); const messages = [];
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500, chatDuplicateWindowMs: 3000 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); const channel = { id: 'chat', isTextBased: () => true, send: async message => { messages.push(message); } };
  bot.client = { channels: { fetch: async () => channel } };
  await bot.mirrorChat({ id: 'bridge-one', source: 'bridge', from: 'Pilot', text: 'broadcast' });
  await bot.mirrorChat({ id: 'bridge-two', source: 'bridge', from: 'Pilot', text: 'broadcast' });
  await new Promise(resolve => setTimeout(resolve, 3100));
  await bot.mirrorChat({ id: 'bridge-three', source: 'bridge', from: 'Pilot', text: 'broadcast' });
  assert.equal(messages.length, 2); await fs.rm(temp, { recursive: true, force: true });
});

test('chat mirror suppresses log/bridge counterpart across bot restart', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-restart-dedupe-')); const messages = [];
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500, chatRestartDedupeWindowMs: 30000 }, lmp: {}, universe: {} };
  const channel = { id: 'chat', isTextBased: () => true, send: async message => { messages.push(message); } };
  const first = new DiscordBot(config, {}, {}); first.client = { channels: { fetch: async () => channel } };
  await first.mirrorChat({ id: 'log-before-restart', source: 'log', from: 'Pilot', text: 'restart race' });
  const persisted = JSON.parse(await fs.readFile(path.join(temp, 'state', 'bot-state.json'), 'utf8'));
  assert.equal(persisted.recentChatDeliveries.length, 1); assert.equal(persisted.recentChatDeliveries[0].text, undefined);
  const second = new DiscordBot(config, {}, {}); second.client = { channels: { fetch: async () => channel } }; await second.loadState();
  await second.mirrorChat({ id: 'bridge-after-restart', source: 'bridge', from: 'Pilot', text: 'restart race' });
  assert.equal(messages.length, 1); await fs.rm(temp, { recursive: true, force: true });
});

test('update manager queues but never deploys while players remain online', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-queue-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'new');
  let stopCalls = 0; const controller = { root: live, processInfo: async () => ({ pid: 7 }), status: async () => ({ online: true, ready: true, playerCount: 1, connectedCount: 1 }), stop: async () => { stopCalls++; return { ok: true }; } };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: false } }, controller, null);
  await manager.stage(source, { id: 'queued-test', version: '1.0.0' }); assert.equal((await manager.queue('queued-test')).ok, true);
  const result = await manager.reconcile({ online: true, playerCount: 1, connectedCount: 1 });
  assert.equal(result.deferred, true); assert.equal(result.reason, 'players-online'); assert.equal(stopCalls, 0); assert.equal((await manager.status()).status, 'queued');
  const handshake = await manager.reconcile({ online: true, playerCount: 0, connectedCount: 1 });
  assert.equal(handshake.deferred, true); assert.equal(handshake.reason, 'connections-online'); assert.equal(stopCalls, 0); assert.equal((await manager.status()).status, 'queued');
  await fs.rm(temp, { recursive: true, force: true });
});

test('running update manager observes updates queued by a separate process', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-external-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'new');
  const config = { __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: false } };
  const observer = new UpdateManager(config, { root: live }, null); await observer.load();
  const scheduler = new UpdateManager(config, null, null); await scheduler.stage(source, { id: 'external-test', version: '1.0.0' });
  assert.equal((await scheduler.queue('external-test', 'local-operator', { countdownMinutes: 15 })).ok, true);
  const observed = await observer.status();
  assert.equal(observed.status, 'queued'); assert.equal(observed.packageId, 'external-test');
  await fs.rm(temp, { recursive: true, force: true });
});

test('update manager announces useful countdown marks without obsolete bursts', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-countdown-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'new');
  const controller = { root: live, processInfo: async () => ({ pid: 7 }), status: async () => ({ online: true, ready: true, playerCount: 1, connectedCount: 1 }) };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: false, defaultCountdownMinutes: 15 } }, controller, null);
  await manager.stage(source, { id: 'countdown-test', version: '3.0.0', notes: 'Hiring and stability fixes.' });
  const start = Date.parse('2026-08-14T00:00:00.000Z');
  const queued = await manager.queue('countdown-test', 'tester', { now: start, countdownMinutes: 15 });
  assert.equal(queued.state.countdownMinutes, 15); assert.equal(Date.parse(queued.state.deployAt), start + 15 * 60_000);
  const announcements = []; const hooks = { onAnnouncement: async message => announcements.push(message) };
  await manager.reconcile({ online: true, playerCount: 1, connectedCount: 1 }, hooks, start);
  await manager.reconcile({ online: true, playerCount: 1, connectedCount: 1 }, hooks, start + 6 * 60_000);
  await manager.reconcile({ online: true, playerCount: 1, connectedCount: 1 }, hooks, start + 14 * 60_000);
  await manager.reconcile({ online: true, playerCount: 1, connectedCount: 1 }, hooks, start + 14 * 60_000 + 45_000);
  assert.deepEqual(announcements.map(message => message.match(/in (.+?) for update/)?.[1]), ['15 minutes', '10 minutes', '1 minute', '15 seconds']);
  assert.equal(announcements.some(message => /restart in 5 minutes\b/.test(message)), false);
  assert.match(announcements[0], /Hiring and stability fixes/);
  await fs.rm(temp, { recursive: true, force: true });
});

test('timed update forces deployment at deadline while occupied', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-deadline-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'new'); await fs.writeFile(path.join(live, 'Server.exe'), 'old');
  let online = true; let stops = 0; const controller = {
    root: live, processInfo: async () => online ? { pid: 7 } : null,
    status: async () => ({ online, ready: online, playerCount: 1, connectedCount: 1 }),
    stop: async () => { stops++; online = false; return { ok: true }; },
    startExternal: async () => { online = true; return { ok: true, pid: 8 }; }
  };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: false } }, controller, null);
  await manager.stage(source, { id: 'deadline-test', version: '3.1.0' }); const start = Date.parse('2026-08-14T00:00:00.000Z');
  await manager.queue('deadline-test', 'tester', { now: start, countdownMinutes: 1 });
  const announcements = []; const deployed = []; const result = await manager.reconcile({ online: true, playerCount: 1, connectedCount: 1 }, { onAnnouncement: async message => announcements.push(message), onDeployed: async message => deployed.push(message) }, start + 60_000);
  assert.equal(result.ok, true); assert.equal(stops, 1); assert.equal(announcements.some(message => message.includes('restarting now')), true);
  assert.equal(await fs.readFile(path.join(live, 'Server.exe'), 'utf8'), 'new');
  assert.deepEqual(deployed, ['Server is back online. Update 3.1.0 deployed successfully.']);
  await fs.rm(temp, { recursive: true, force: true });
});

test('update manager deploys at zero players with backup, rollback metadata, and readiness gate', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-deploy-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'new'); await fs.writeFile(path.join(live, 'Server.exe'), 'old');
  let online = true; let backups = 0; let stops = 0; const controller = {
    root: live,
    processInfo: async () => online ? { pid: 7 } : null,
    status: async () => ({ online, ready: online, playerCount: 0, connectedCount: 0 }),
    stop: async () => { stops++; online = false; return { ok: true }; },
    startExternal: async () => { online = true; return { ok: true, pid: 8 }; }
  };
  const universe = { createBackup: async () => { backups++; return { id: 'pre-update' }; } };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: true } }, controller, universe);
  await manager.stage(source, { id: 'deploy-test', version: '2.0.0' }); await manager.queue('deploy-test');
  const result = await manager.reconcile({ online: true, playerCount: 0, connectedCount: 0 });
  assert.equal(result.ok, true); assert.equal(backups, 1); assert.equal(stops, 1); assert.equal(await fs.readFile(path.join(live, 'Server.exe'), 'utf8'), 'new'); assert.equal((await manager.status()).status, 'idle');
  await fs.rm(temp, { recursive: true, force: true });
});

test('failed updated process is stopped before rollback and restored server reaches readiness', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-failed-start-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'bad-new'); await fs.writeFile(path.join(live, 'Server.exe'), 'old-good');
  let running = true; let ready = true; let starts = 0; let stops = 0; const controller = {
    root: live,
    processInfo: async () => running ? { pid: starts + 7 } : null,
    status: async () => ({ online: running, ready: running && ready, playerCount: 0, connectedCount: 0 }),
    stop: async () => { stops++; running = false; return { ok: true }; },
    startExternal: async () => { starts++; running = true; ready = starts >= 2; return { ok: true, pid: starts + 7 }; }
  };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: false } }, controller, null);
  manager.pollMs = 1; manager.deploymentTimeoutMs = 20;
  await manager.stage(source, { id: 'failed-start-test', version: '2.0.2' }); await manager.queue('failed-start-test');
  const result = await manager.reconcile({ online: true, playerCount: 0, connectedCount: 0 });
  assert.equal(result.ok, false); assert.match(result.reason, /did not become ready/); assert.equal(stops, 2); assert.equal(starts, 2);
  assert.equal(running, true); assert.equal(ready, true); assert.equal(await fs.readFile(path.join(live, 'Server.exe'), 'utf8'), 'old-good');
  assert.equal((await manager.status()).status, 'failed');
  await fs.rm(temp, { recursive: true, force: true });
});

test('queued update deploys and recovers when the server crashes offline', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-crash-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'fixed'); await fs.writeFile(path.join(live, 'Server.exe'), 'old');
  let online = false; let starts = 0; const controller = {
    root: live,
    processInfo: async () => online ? { pid: 8 } : null,
    status: async () => ({ online, ready: online, playerCount: 0, connectedCount: 0 }),
    startExternal: async () => { starts++; online = true; return { ok: true, pid: 8 }; }
  };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: false } }, controller, null);
  await manager.stage(source, { id: 'crash-recovery-test', version: '2.0.1' }); await manager.queue('crash-recovery-test');
  const result = await manager.reconcile({ online: false, playerCount: 0, connectedCount: 0 });
  assert.equal(result.ok, true); assert.equal(starts, 1); assert.equal(await fs.readFile(path.join(live, 'Server.exe'), 'utf8'), 'fixed'); assert.equal((await manager.status()).status, 'idle');
  await fs.rm(temp, { recursive: true, force: true });
});

test('update manager aborts safely when a connection arrives during pre-deploy backup', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-update-race-')); const source = path.join(temp, 'source'); const live = path.join(temp, 'live');
  await fs.mkdir(source, { recursive: true }); await fs.mkdir(live, { recursive: true }); await fs.writeFile(path.join(source, 'Server.exe'), 'new');
  let connected = 0; let stops = 0; const controller = {
    root: live,
    processInfo: async () => ({ pid: 7 }),
    status: async () => ({ online: true, ready: true, playerCount: 0, connectedCount: connected }),
    stop: async () => { stops++; return { ok: true }; }
  };
  const universe = { createBackup: async () => { connected = 1; return { id: 'pre-update' }; } };
  const manager = new UpdateManager({ __root: temp, updates: { pollIntervalMs: 1, requirePreDeployBackup: true } }, controller, universe);
  await manager.stage(source, { id: 'race-test', version: '2.1.0' }); await manager.queue('race-test');
  const result = await manager.reconcile({ online: true, playerCount: 0, connectedCount: 0 });
  assert.equal(result.deferred, true); assert.equal(result.reason, 'activity-resumed'); assert.equal(stops, 0); assert.equal((await manager.status()).status, 'queued');
  await fs.rm(temp, { recursive: true, force: true });
});

test('chat echo suppression only consumes matching Discord-origin messages', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-echo-')); const messages = [];
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500, chatCooldownMs: 0 }, lmp: {}, universe: {} };
  const channel = { id: 'chat', isTextBased: () => true, send: async message => { messages.push(message); } };
  const bot = new DiscordBot(config, { say: async () => ({ ok: true }) }, {}); bot.client = { channels: { fetch: async () => channel } };
  await bot.handleMessage({ author: { bot: false, username: 'PilotOne', id: '1' }, channelId: 'chat', content: 'same' });
  await bot.mirrorChat({ id: 'echo-one', from: '[Discord] PilotOne', text: 'same' });
  await bot.mirrorChat({ id: 'echo-untracked', from: '[Discord] Untracked', text: 'arrived before tracking' });
  await bot.mirrorChat({ id: 'native-two', from: 'NativePilot', text: 'same' });
  assert.equal(messages.length, 1); assert.match(messages[0].content, /NativePilot/); await fs.rm(temp, { recursive: true, force: true });
});

test('log-chat delivery queues across a Discord send failure and retries', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-chat-retry-')); const messages = []; let fail = true;
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { maxMessageLength: 500, chatQueueMax: 5, chatQueueTtlMs: 120000 }, lmp: {}, universe: {} };
  const channel = { id: 'chat', isTextBased: () => true, send: async message => { if (fail) throw new Error('Discord unavailable'); messages.push(message); } };
  const bot = new DiscordBot(config, {}, {}); bot.client = { channels: { fetch: async () => channel } };
  assert.equal(await bot.mirrorChat({ from: 'Pilot', text: 'queued' }, { retainOnFailure: true }), false); assert.equal(bot.chatDeliveryQueue.size, 1);
  fail = false; await bot.flushChatDeliveryQueue(); assert.equal(messages.length, 1); assert.equal(bot.chatDeliveryQueue.size, 0); await fs.rm(temp, { recursive: true, force: true });
});

test('log tailer resumes after log truncation', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-log-')); const logs = path.join(temp, 'logs'); const file = path.join(logs, 'server.log');
  await fs.mkdir(logs, { recursive: true }); await fs.writeFile(file, 'initial-line-that-is-long\n');
  const tailer = new LogTailer({ logsPath: logs }); const lines = []; tailer.on('line', line => lines.push(line));
  await tailer.tick(); await fs.appendFile(file, 'first-line-that-is-long\n'); await tailer.tick();
  await fs.writeFile(file, 'reset\n'); await tailer.tick();
  assert.deepEqual(lines, ['first-line-that-is-long', 'reset']); await fs.rm(temp, { recursive: true, force: true });
});

test('log tailer emits an appended chat line once during overlapping polls', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-log-overlap-'));
  const logs = path.join(temp, 'logs'); const file = path.join(logs, 'server.log');
  await fs.mkdir(logs, { recursive: true }); await fs.writeFile(file, 'initial\n');
  const tailer = new LogTailer({ logsPath: logs }); const chats = [];
  tailer.on('chat', chat => chats.push(chat));
  await tailer.tick();
  await fs.appendFile(file, '[12:00:00][Chat]: Pilot: hello\n');
  await Promise.all([tailer.tick(), tailer.tick(), tailer.tick()]);
  assert.deepEqual(chats.map(chat => chat.text), ['hello']);
  await fs.rm(temp, { recursive: true, force: true });
});

test('command log cursor returns only bytes appended after dispatch', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-log-cursor-')); const logs = path.join(temp, 'logs'); const file = path.join(logs, 'server.log');
  await fs.mkdir(logs, { recursive: true }); await fs.writeFile(file, 'old line\n');
  const controller = new LmpController({ lmp: { serverRoot: temp }, security: {} }); const cursor = await controller.logCursor();
  await fs.appendFile(file, '[LMP]: Executing /kick\n[LMP]: Player: missing not found\n');
  const delta = await controller.readLogSince(cursor); assert.equal(delta.text, '[LMP]: Executing /kick\n[LMP]: Player: missing not found\n');
  await fs.rm(temp, { recursive: true, force: true });
});

test('log tailer emits structured player join/leave events', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-player-events-')); const logs = path.join(temp, 'logs'); const file = path.join(logs, 'server.log');
  await fs.mkdir(logs, { recursive: true }); await fs.writeFile(file, 'seed\n');
  const tailer = new LogTailer({ logsPath: logs }); const events = []; tailer.on('player', event => events.push(event)); await tailer.tick();
  await fs.appendFile(file, '[10:00:00][LMP]: Client Alpha (abcdef0123456789) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n[10:00:01][Debug]: Alpha sent Connection end message, reason: Quit\n[10:00:02][LMP]: Client Player (0123456789abcdef) handshake successful, LMP Version: 0.29.2, KSP Version: 1.12.5\n[10:00:03][Debug]: Player sent Connection end message, reason: Quit\n'); await tailer.tick();
  assert.deepEqual(events.map(event => [event.type, event.name]), [['join', 'Alpha'], ['leave', 'Alpha'], ['join', 'Player'], ['leave', 'Player']]); await fs.rm(temp, { recursive: true, force: true });
});

test('leave reasons are concise and distinguish voluntary exits from timeouts', () => {
  assert.equal(shortLeaveReason('Connection timed out'), 'connection timed out');
  assert.equal(shortLeaveReason('Quit'), 'quit');
  assert.equal(shortLeaveReason('Kicked from the server: test'), 'kicked');
  assert.equal(shortLeaveReason('You were banned from the server: test'), 'banned');
  assert.equal(shortLeaveReason('[LMP]: Failed mod validation'), 'mod validation failed');
});

test('network timeout diagnostics identify default client timeout behavior', () => {
  const line = '[02:39:04][Warning]: Network timeout for Thirdboy-CASA at 124.122.34.121:3354; origin remote-disconnect; average RTT 262 ms, negotiated MTU 1200 bytes; queue 0 messages/0 bytes (control/realtime/bulk 0/0/0, high 1468/14493135 bytes); Lidgren vessel pending 0 (high 169); heartbeat age 19.84s (high 19.84s); inbound packet age 19.80s (high 19.80s), application message age 20.10s; server network loop age 0.001s, last gap 0.002s (client high 0.040s, server high 0.050s); failures/drops/backpressure/coalesced/throttled 2/0/0/2544/3187.';
  const timeout = parseNetworkTimeoutLine(line);
  assert.equal(timeout.name, 'Thirdboy-CASA');
  assert.equal(timeout.averageRttMs, 262);
  assert.equal(timeoutGapSeconds(timeout), 19.84);
  assert.equal(timeout.origin, 'remote-disconnect');
  assert.equal(timeout.networkLoopLastGapSeconds, 0.002);
  assert.equal(matchesDefaultClientTimeout(timeout), true);
  assert.equal(likelyClientSideTimeout(timeout), true);
  const advice = clientTimeoutAdvice(timeout, 'Eso');
  assert.match(advice, /19\.8 seconds/);
  assert.match(advice, /reported receiving no server traffic/);
  assert.doesNotMatch(advice, /stopped sending packets/);
  assert.match(advice, /TimeoutSeconds.*15.*60/s);
  assert.match(advice, /ping \*\*Eso\*\*/);
  assert.doesNotMatch(advice, /<@\d+>/);
  assert.match(advice, /KSP\.log/);
  assert.match(advice, /VesselSyncLog\.txt/);
});

test('all timeouts request logs, but server pressure suppresses default-timeout advice', () => {
  const line = '[02:39:04][Warning]: Network timeout for Pilot at 1.2.3.4:1234; origin remote-disconnect; average RTT 80 ms, negotiated MTU 1200 bytes; queue 8 messages/2048 bytes (control/realtime/bulk 0/0/8, high 10/4096 bytes); Lidgren vessel pending 3 (high 20); heartbeat age 18.00s (high 18.00s); inbound packet age 18.00s (high 18.00s), application message age 18.00s; server network loop age 0.001s, last gap 0.002s (client high 0.010s, server high 0.010s); failures/drops/backpressure/coalesced/throttled 0/0/1/0/0.';
  const timeout = parseNetworkTimeoutLine(line);
  assert.equal(likelyClientSideTimeout(timeout), false);
  const advice = clientTimeoutAdvice(timeout, 'Eso');
  assert.match(advice, /KSP\.log/);
  assert.doesNotMatch(advice, /Change `TimeoutSeconds`/);
});

test('reliable flag backlog is treated as server-side timeout pressure', () => {
  const line = '[02:39:04][Warning]: Network timeout for Pilot at 1.2.3.4:1234; origin remote-disconnect; average RTT 80 ms, negotiated MTU 1200 bytes; queue 0 messages/0 bytes (control/realtime/bulk 0/0/0, high 10/4096 bytes); Lidgren vessel pending 0 (high 20); Lidgren flag pending 16 (high 168); heartbeat age 18.00s (high 18.00s); inbound packet age 18.00s (high 18.00s), application message age 18.00s; server network loop age 0.001s, last gap 0.002s (client high 0.010s, server high 0.010s); failures/drops/backpressure/coalesced/throttled 0/0/0/0/0.';
  const timeout = parseNetworkTimeoutLine(line);
  assert.equal(timeout.pendingFlagMessages, 16);
  assert.equal(likelyClientSideTimeout(timeout), false);
  assert.doesNotMatch(clientTimeoutAdvice(timeout, 'Eso'), /Change `TimeoutSeconds`/);
});

test('remote disconnect diagnostics use heartbeat gap instead of late packet age', () => {
  const line = '[02:39:04][Warning]: Network timeout for Pilot at 1.2.3.4:1234; origin remote-disconnect; average RTT 80 ms, negotiated MTU 1200 bytes; queue 0 messages/0 bytes (control/realtime/bulk 0/0/0, high 10/4096 bytes); Lidgren vessel pending 0 (high 20); heartbeat age 15.40s (high 60.00s); inbound packet age 0.02s (high 60.00s), application message age 15.60s; server network loop age 0.001s, last gap 0.002s (client high 0.010s, server high 0.010s); failures/drops/backpressure/coalesced/throttled 0/0/0/0/0.';
  const timeout = parseNetworkTimeoutLine(line);
  assert.equal(timeoutGapSeconds(timeout), 15.4);
  const advice = clientTimeoutAdvice(timeout, 'Eso');
  assert.match(advice, /15\.4 seconds/);
  assert.match(advice, /reported receiving no server traffic/);
  assert.doesNotMatch(advice, /stopped sending packets/);
});

test('server deadline is not mislabeled as a client-initiated timeout', () => {
  const line = '[02:39:04][Warning]: Network timeout for Pilot at 1.2.3.4:1234; origin server-deadline; average RTT 80 ms, negotiated MTU 1200 bytes; queue 0 messages/0 bytes (control/realtime/bulk 0/0/0, high 10/4096 bytes); Lidgren vessel pending 0 (high 20); heartbeat age 60.01s (high 60.01s); inbound packet age 60.00s (high 60.00s), application message age 60.10s; server network loop age 0.001s, last gap 0.002s (client high 0.010s, server high 0.010s); failures/drops/backpressure/coalesced/throttled 0/0/0/0/0.';
  const timeout = parseNetworkTimeoutLine(line);
  assert.equal(timeout.origin, 'server-deadline');
  assert.equal(likelyClientSideTimeout(timeout), false);
  const advice = clientTimeoutAdvice(timeout, 'Eso');
  assert.match(advice, /Server timeout deadline/);
  assert.match(advice, /60\.0 seconds/);
  assert.doesNotMatch(advice, /Change `TimeoutSeconds`/);
});

test('server network-loop stalls are reported separately', () => {
  const line = '[02:39:04][Warning]: Network timeout for Pilot at 1.2.3.4:1234; origin server-deadline; average RTT 80 ms, negotiated MTU 1200 bytes; queue 0 messages/0 bytes (control/realtime/bulk 0/0/0, high 10/4096 bytes); Lidgren vessel pending 0 (high 20); heartbeat age 60.01s (high 60.01s); inbound packet age 60.00s (high 60.00s), application message age 60.10s; server network loop age 0.001s, last gap 61.200s (client high 61.200s, server high 61.200s); failures/drops/backpressure/coalesced/throttled 0/0/0/0/0.';
  const advice = clientTimeoutAdvice(parseNetworkTimeoutLine(line), 'Eso');
  assert.match(advice, /network loop also stalled/);
});

test('timeout advice requires a matching reconnect', () => {
  let now = 1_000;
  const tracker = new TimeoutAdviceTracker(300_000, () => now);
  const timeout = { name: 'Mr_SillyGoose', heartbeatAgeSeconds: 27.1 };
  assert.equal(tracker.record(timeout), true);
  assert.equal(tracker.confirmReconnect('someone-else'), null);
  assert.equal(tracker.confirmReconnect('mr_sillygoose'), timeout);
  assert.equal(tracker.confirmReconnect('Mr_SillyGoose'), null);
});

test('timeout advice expires when a player intentionally stays offline', () => {
  let now = 1_000;
  const tracker = new TimeoutAdviceTracker(300_000, () => now);
  tracker.record({ name: 'Pilot', heartbeatAgeSeconds: 20 });
  now += 300_001;
  assert.equal(tracker.confirmReconnect('Pilot'), null);
});

test('log tailer emits parsed network timeout diagnostics', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-timeout-events-')); const logs = path.join(temp, 'logs'); const file = path.join(logs, 'server.log');
  await fs.mkdir(logs, { recursive: true }); await fs.writeFile(file, 'seed\n');
  const tailer = new LogTailer({ logsPath: logs }); const events = []; tailer.on('timeout', event => events.push(event)); await tailer.tick();
  await fs.appendFile(file, '[02:39:04][Warning]: Network timeout for Pilot at 1.2.3.4:1234; origin remote-disconnect; average RTT 80 ms, negotiated MTU 1200 bytes; queue 0 messages/0 bytes (control/realtime/bulk 0/0/0, high 10/4096 bytes); Lidgren vessel pending 0 (high 20); heartbeat age 19.50s (high 19.50s); inbound packet age 19.40s (high 19.40s), application message age 19.60s; server network loop age 0.001s, last gap 0.002s (client high 0.010s, server high 0.010s); failures/drops/backpressure/coalesced/throttled 0/0/0/0/0.\n'); await tailer.tick();
  assert.equal(events.length, 1); assert.equal(events[0].name, 'Pilot'); assert.equal(events[0].heartbeatAgeSeconds, 19.5);
  await fs.rm(temp, { recursive: true, force: true });
});

test('player presence messages can publish to luna-chat', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-luna-presence-')); let sent = null;
  const config = { __root: temp, discord: { channels: { lunaChat: 'luna-chat' } }, security: {}, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {});
  bot.getChannel = async key => key === 'lunaChat' ? { isTextBased: () => true, send: async payload => { sent = payload; return payload; } } : null;
  await bot.sendLunaChat('**Eso** joined · KSP 1.12.5 · LMP 0.29.2');
  assert.match(sent.content, /Eso.*joined/); assert.deepEqual(sent.allowedMentions, { parse: [] });
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('bridge inbox enforces current server PID and writes bounded relay metadata', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-inbox-')); const bridge = path.join(temp, 'bridge');
  await fs.mkdir(bridge, { recursive: true });
  await fs.writeFile(path.join(bridge, 'ready.json'), JSON.stringify({ pid: 42 }));
  const controller = new LmpController({ lmp: { serverRoot: temp, bridgeDirectory: bridge }, security: { maxMessageLength: 500, bridgeMessageTtlMs: 120000 } });
  controller.processInfo = async () => ({ pid: 42, owned: false });
  const queued = await controller.sendBridgeMessage('hello', 'Pilot'); assert.equal(queued.ok, true);
  const inbox = (await fs.readdir(path.join(bridge, 'inbox')))[0]; const payload = JSON.parse(await fs.readFile(path.join(bridge, 'inbox', inbox), 'utf8'));
  assert.equal(payload.author, 'Pilot'); assert.equal(payload.text, 'hello'); assert.equal(payload.serverPid, 42); assert.ok(payload.expiresAt);
  await fs.writeFile(path.join(bridge, 'ready.json'), JSON.stringify({ pid: 43 }));
  assert.deepEqual(await controller.sendBridgeMessage('stale', 'Pilot'), { ok: false, reason: 'bridge-stale' });
  await fs.rm(temp, { recursive: true, force: true });
});

test('external LMP commands use the allowlisted bridge and await its result', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-command-bridge-')); const bridge = path.join(temp, 'bridge');
  await fs.mkdir(bridge, { recursive: true });
  await fs.writeFile(path.join(bridge, 'ready.json'), JSON.stringify({ pid: 42, capabilities: ['chat', 'commands'] }));
  const controller = new LmpController({ lmp: { serverRoot: temp, bridgeDirectory: bridge }, security: { bridgeCommandTimeoutMs: 2000 } });
  controller.processInfo = async () => ({ pid: 42, owned: false });
  let request;
  const responder = (async () => {
    for (let attempt = 0; attempt < 100; attempt++) {
      const files = (await fs.readdir(path.join(bridge, 'inbox')).catch(() => [])).filter(name => name.endsWith('.json'));
      if (files.length) {
        request = JSON.parse(await fs.readFile(path.join(bridge, 'inbox', files[0]), 'utf8'));
        await fs.mkdir(path.join(bridge, 'results'), { recursive: true });
        await writeJsonAtomic(path.join(bridge, 'results', `${request.id}.json`), { type: 'command-result', id: request.id, serverPid: 42, command: request.command, ok: true });
        return;
      }
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    throw new Error('bridge command was not queued');
  })();
  const result = await controller.sendBridgeCommand('kick', 'Pilot testing'); await responder;
  assert.equal(result.ok, true); assert.equal(result.mode, 'bridge');
  assert.equal(request.kind, 'command'); assert.equal(request.command, 'kick'); assert.equal(request.argument, 'Pilot testing'); assert.equal(request.serverPid, 42);
  assert.equal((await fs.readdir(path.join(bridge, 'results'))).length, 0);
  assert.equal((await controller.sendBridgeCommand('shell', 'anything')).reason, 'invalid-bridge-command');
  await fs.rm(temp, { recursive: true, force: true });
});

test('safe command runner falls back to the bridge for an external server', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-command-fallback-')); const controller = new LmpController({ lmp: { serverRoot: temp }, security: {} });
  controller.sendConsoleLine = async () => ({ ok: false, reason: 'not-controller-owned' });
  controller.playerSnapshot = async () => ({ players: [{ name: 'Pilot' }] });
  controller.sendBridgeCommand = async (command, argument) => ({ ok: command === 'kick' && argument === 'Pilot', command: '/kick Pilot', mode: 'bridge' });
  controller.readLogTail = async () => ({ text: '' });
  const result = await controller.runSafeCommand('kick pilot'); assert.equal(result.ok, true); assert.equal(result.mode, 'bridge');
  await fs.rm(temp, { recursive: true, force: true });
});

test('bridge queues enforce capacity and quarantine oversized events', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-bridge-limits-')); const bridge = path.join(temp, 'bridge'); const inbox = path.join(bridge, 'inbox'); const events = path.join(bridge, 'events');
  await fs.mkdir(inbox, { recursive: true }); await fs.mkdir(events, { recursive: true }); await fs.writeFile(path.join(bridge, 'ready.json'), JSON.stringify({ pid: 42 }));
  const controller = new LmpController({ lmp: { serverRoot: temp, bridgeDirectory: bridge }, security: { bridgeMaxInboxFiles: 1, bridgeMaxEventBytes: 4096, bridgeMessageTtlMs: 120000 } }); controller.processInfo = async () => ({ pid: 42, owned: false });
  await fs.writeFile(path.join(inbox, 'queued.json'), '{}'); assert.deepEqual(await controller.sendBridgeMessage('blocked', 'Pilot'), { ok: false, reason: 'bridge-inbox-full' });
  await fs.writeFile(path.join(events, 'oversized.json'), JSON.stringify({ type: 'lmp-chat', text: 'x'.repeat(5000) })); const tailer = new BridgeEventTailer(controller); await tailer.tick();
  assert.ok((await fs.readdir(path.join(events, 'failed'))).includes('oversized.json')); await fs.rm(temp, { recursive: true, force: true });
});

test('operator audit persists locally when Discord console delivery is unavailable', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-audit-'));
  const config = { __root: temp, discord: { channels: {} }, security: { auditAllActions: true }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); bot.sendConsole = async () => null;
  await bot.audit({ user: { id: '7', tag: 'Pilot#0001' } }, 'smoke', { ok: true, output: 'bounded' });
  const row = JSON.parse((await fs.readFile(path.join(temp, 'state', 'audit.jsonl'), 'utf8')).trim());
  assert.equal(row.actorId, '7'); assert.equal(row.action, 'smoke'); assert.equal(row.result.output, 'bounded');
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('local event history persists independently of Discord delivery', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-events-'));
  const config = { __root: temp, discord: { channels: {} }, security: {}, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); await bot.recordEvent('smoke-event', { value: 1 });
  assert.match(await bot.eventTail(), /smoke-event/); bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('Discord-to-LMP relay uses the guild display name', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-display-name-')); let relayed = null;
  const config = { __root: temp, discord: { channels: { lunaChat: 'chat' } }, security: { chatCooldownMs: 0 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, { say: async (text, author) => { relayed = { text, author }; return { ok: true }; } }, {});
  await bot.handleMessage({
    author: { bot: false, id: 'target', username: 'login-name', globalName: 'Global Name' },
    member: { displayName: 'Server Nickname' }, channelId: 'chat', content: 'hello from Discord'
  });
  assert.deepEqual(relayed, { text: 'hello from Discord', author: 'Server Nickname' });
  assert.equal('chatMutes' in bot, false);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('maintenance windows persist, report expiry, and auto-clear safely', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-maintenance-window-')); let replyPayload;
  const config = { __root: temp, discord: { channels: { console: 'console' }, operatorRoleIds: ['operator'], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  let interaction; const bot = new DiscordBot(config, {}, {}); bot.safeRefreshDashboard = async () => { assert.equal(interaction.deferred, true); return true; }; bot.sendConsole = async () => null;
  interaction = { user: { id: 'operator-user' }, channelId: 'console', commandName: 'maintenance', isButton: () => false, isChatInputCommand: () => true, member: { permissions: { has: () => false }, roles: { cache: [{ id: 'operator' }] } }, options: { getString: () => 'on', getInteger: () => 1 }, deferReply: async () => { interaction.deferred = true; }, editReply: async payload => { replyPayload = payload; }, deleteReply: async () => {} };
  await bot.handleInteraction(interaction);
  assert.equal(bot.maintenance, true); assert.ok(bot.maintenanceUntil > Date.now()); assert.match(replyPayload.content, /until/);
  bot.maintenanceUntil = Date.now() - 1; assert.equal(await bot.expireMaintenance(), true); assert.equal(bot.maintenance, false); assert.equal(bot.maintenanceUntil, null);
  const state = JSON.parse(await fs.readFile(path.join(temp, 'state', 'bot-state.json'), 'utf8')); assert.equal(state.maintenance, false); assert.equal(state.maintenanceUntil, null);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('concurrent atomic JSON writes do not share staging paths', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-atomic-')); const target = path.join(temp, 'state.json');
  await Promise.all(Array.from({ length: 20 }, (_, index) => writeJsonAtomic(target, { index })));
  const result = JSON.parse(await fs.readFile(target, 'utf8')); assert.ok(Number.isInteger(result.index));
  assert.equal((await fs.readdir(temp)).filter(name => name.endsWith('.tmp')).length, 0); await fs.rm(temp, { recursive: true, force: true });
});

test('/command creates a confirmation gate before invoking native LMP input', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-command-')); let invoked = false; let replyPayload = null;
  const config = {
    __root: temp,
    discord: { channels: { console: 'console' }, operatorRoleIds: ['operator'], adminRoleIds: [], moderatorRoleIds: [] },
    security: { commandCooldownMs: 0, auditAllActions: true }, lmp: {}, universe: {}
  };
  const controller = { runSafeCommand: async () => { invoked = true; return { ok: true }; } };
  const bot = new DiscordBot(config, controller, {});
  const interaction = {
    user: { id: '7', tag: 'Pilot#0001' }, channelId: 'console',
    member: { permissions: { has: () => false }, roles: { cache: [{ id: 'operator' }] } },
    commandName: 'command', isButton: () => false, isChatInputCommand: () => true,
    options: { getString: () => 'nukeksc *' },
    reply: async payload => { replyPayload = payload; }, deleteReply: async () => {}
  };
  await bot.handleInteraction(interaction);
  assert.equal(invoked, false); assert.equal(bot.pendingActions.size, 1); assert.match(replyPayload.content, /Confirm/); assert.equal(replyPayload.ephemeral, true);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('typed native shortcuts reuse the confirmation gate with structured arguments', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-native-shortcut-')); let invoked = false; let replyPayload = null;
  const config = { __root: temp, discord: { channels: { console: 'console' }, operatorRoleIds: ['operator'], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, { runSafeCommand: async () => { invoked = true; return { ok: true }; } }, {});
  const interaction = {
    user: { id: '8', tag: 'Pilot#0002' }, channelId: 'console', commandName: 'kick',
    isButton: () => false, isChatInputCommand: () => true,
    member: { permissions: { has: () => false }, roles: { cache: [{ id: 'operator' }] } },
    options: { getString: key => key === 'player' ? 'Pilot One' : '', getInteger: () => null },
    reply: async payload => { replyPayload = payload; }, deleteReply: async () => {}
  };
  await bot.handleInteraction(interaction);
  const pending = [...bot.pendingActions.values()][0];
  assert.equal(invoked, false); assert.equal(pending.command, 'kick'); assert.equal(pending.argument, 'Pilot One'); assert.match(replyPayload.content, /kick Pilot One/);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('sensitive native command arguments are redacted from previews and audit labels', () => {
  assert.equal(redactCommandText('changesettings settings Password super-secret'), 'changesettings settings Password <redacted>');
  assert.equal(redactCommandText('/say private message'), 'say <redacted>');
  assert.match(redactSensitiveOutput('Command input: /say private message'), /<redacted>/);
  assert.match(redactSensitiveOutput('Password: super-secret'), /Password: <redacted>/);
});

test('say audit records do not retain message text from direct stdin results', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-say-audit-'));
  const config = { __root: temp, discord: { channels: {} }, security: { auditAllActions: true }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); bot.sendConsole = async () => null;
  await bot.audit({ user: { id: '8', tag: 'Pilot#0002' } }, 'say', { ok: true, command: '[Discord] Pilot: secret text' });
  const row = JSON.parse((await fs.readFile(path.join(temp, 'state', 'audit.jsonl'), 'utf8')).trim()); assert.equal(row.result.command, '<redacted>');
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard button controls reject clicks outside dashboard channel', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-button-channel-')); let replyPayload;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash', console: 'console', lunaChat: 'chat' }, operatorRoleIds: [], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {});
  const interaction = { user: { id: '1' }, channelId: 'console', customId: 'apf:backup', isButton: () => true, isChatInputCommand: () => false, reply: async payload => { replyPayload = payload; } };
  await bot.handleInteraction(interaction); assert.match(replyPayload.content, /Dashboard controls/); assert.equal(replyPayload.ephemeral, true);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('/help is routed to the console channel', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-help-channel-')); let replyPayload;
  const config = { __root: temp, discord: { channels: { console: 'console' }, operatorRoleIds: [], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {});
  const interaction = { user: { id: '1' }, channelId: 'dashboard', commandName: 'help', isButton: () => false, isChatInputCommand: () => true, reply: async payload => { replyPayload = payload; } };
  await bot.handleInteraction(interaction); assert.match(replyPayload.content, /Use #console/); assert.equal(replyPayload.ephemeral, true);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('successful console help, plugin, and diagnostic replies are public', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-public-console-'));
  const config = { __root: temp, discord: { channels: { console: 'console' }, operatorRoleIds: [], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  const status = { online: true, pid: 7, owned: false, uptimeMs: 1000, ready: true, registration: true, playerCount: 0, nat: '127.0.0.1:2252', settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10, modControl: true, backupIntervalMs: 30000 }, master: { visible: true, checked: true }, udpBinding: { bound: true, ownedByProcess: true }, bridge: { ready: true }, modControl: {}, storage: { available: true, free: '1 GB' }, plugins: [{ type: 'file', name: 'Plugin.dll' }], healthWarnings: [], healthSummary: 'healthy' };
  const bot = new DiscordBot(config, { status: async () => status, readLogTail: async () => ({ text: 'ok' }) }, { listBackups: async () => [] });
  let helpReply; await bot.handleInteraction({ user: { id: 'help' }, channelId: 'console', commandName: 'help', isButton: () => false, isChatInputCommand: () => true, reply: async payload => { helpReply = payload; } });
  assert.equal(helpReply.ephemeral, false);
  for (const commandName of ['plugins', 'diagnose']) {
    let deferred; let edited;
    const interaction = { user: { id: commandName }, channelId: 'console', commandName, isButton: () => false, isChatInputCommand: () => true, member: { permissions: { has: () => false }, roles: { cache: [] } }, deferReply: async payload => { deferred = payload; interaction.deferred = true; }, editReply: async payload => { edited = payload; } };
    await bot.handleInteraction(interaction); assert.equal(deferred.ephemeral, false); assert.ok(edited);
  }
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard channel access authorizes lifecycle controls and actions do not race', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-button-guards-')); let replyPayload; let updatePayload;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash', console: 'console', lunaChat: 'chat' }, operatorRoleIds: [], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  const bot = new DiscordBot(config, { restart: async () => { throw new Error('must not run'); } }, {});
  const base = { channelId: 'dash', isButton: () => true, isChatInputCommand: () => false, reply: async payload => { replyPayload = payload; }, update: async payload => { updatePayload = payload; }, user: { id: 'guest' }, member: { permissions: { has: () => false }, roles: { cache: [] } } };
  await bot.handleInteraction({ ...base, customId: 'apf:restart', deleteReply: async () => {} }); assert.match(replyPayload.content, /Confirm/);
  bot.lifecycleBusy = true; await bot.handleInteraction({ ...base, user: { id: 'guest-2' }, customId: 'apf:confirm:restart' }); assert.match(updatePayload.content, /already running/);
  await fs.rm(temp, { recursive: true, force: true }); bot.client.destroy();
});

test('console channel access authorizes lifecycle and folded universe commands without roles', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-channel-auth-')); let replyPayload; let editPayload; let deferPayload;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash', console: 'console', lunaChat: 'chat' }, operatorRoleIds: [], adminRoleIds: [], moderatorRoleIds: [] }, security: { commandCooldownMs: 0 }, lmp: {}, universe: {} };
  const member = { permissions: { has: () => false }, roles: { cache: [] } };
  const bot = new DiscordBot(config, {}, { verifyBackup: async id => ({ ok: true, id }) });
  const restart = { user: { id: 'guest-1' }, member, channelId: 'console', commandName: 'restart', isButton: () => false, isChatInputCommand: () => true, reply: async payload => { replyPayload = payload; }, deleteReply: async () => {} };
  await bot.handleInteraction(restart); assert.match(replyPayload.content, /Confirm/);
  const universe = { user: { id: 'guest-2' }, member, channelId: 'console', commandName: 'universe', isButton: () => false, isChatInputCommand: () => true, options: { getSubcommand: () => 'verify', getString: () => 'backup-1' }, deferReply: async payload => { deferPayload = payload; }, editReply: async payload => { editPayload = payload; } };
  await bot.handleInteraction(universe); assert.match(editPayload.content, /backup-1/);
  assert.equal(deferPayload.ephemeral, false);
  assert.equal(bot.canOperate({ channelId: 'public', member }), false); assert.equal(bot.canModerate({ channelId: 'public', member }), false);
  bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('slow status interaction acknowledges publicly before health work', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-defer-')); let deferred = false; let deferPayload = null; let edited = null;
  const config = { __root: temp, discord: { channels: { console: 'console' }, operatorRoleIds: [], adminRoleIds: [], moderatorRoleIds: [] }, security: {}, lmp: {}, universe: {} };
  const status = { online: true, ready: true, registration: true, playerCount: 0, settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10 }, master: { visible: true, checked: true }, bridge: { ready: false, configured: false }, storage: {}, plugins: [], healthWarnings: [], healthSummary: 'healthy' };
  const controller = { status: async () => { assert.equal(deferred, true); return status; } }; const bot = new DiscordBot(config, controller, {});
  const interaction = { user: { id: '1' }, channelId: 'console', commandName: 'status', isButton: () => false, isChatInputCommand: () => true, deferReply: async payload => { deferred = true; deferPayload = payload; interaction.deferred = true; }, editReply: async payload => { edited = payload; }, reply: async () => {} };
  await bot.handleInteraction(interaction); assert.equal(deferred, true); assert.equal(deferPayload.ephemeral, false); assert.ok(edited.embeds); bot.client.destroy(); await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard startup creates once then reuses the persistent message', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-dashboard-')); let sent = 0; let edits = 0;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash' }, pinDashboard: false }, security: {}, lmp: {}, universe: {} };
  const status = { online: true, pid: 7, owned: false, ready: true, registration: true, playerCount: 2, nat: '127.0.0.1:2252', settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10, modControl: true, backupIntervalMs: 30000 }, master: { visible: true, checked: true }, bridge: { ready: false, configured: false }, storage: { available: true, free: '1 GB' }, plugins: [], logAgeMs: 10, healthSummary: 'healthy', healthWarnings: [] };
  const message = { id: 'dashboard-message', author: { id: 'bot' }, embeds: [{ footer: { text: 'APF persistent dashboard' } }], pinned: false, edit: async () => { edits++; return message; } };
  const channel = { id: 'dash', isTextBased: () => true, messages: { fetch: async value => typeof value === 'string' ? message : [] }, send: async () => { sent++; return message; } };
  const bot = new DiscordBot(config, { status: async () => status }, { stats: async () => ({ fileCount: 1, size: '1 KB' }), listBackups: async () => [] });
  bot.client = { user: { id: 'bot', setPresence: () => {} }, channels: { fetch: async () => channel } };
  await bot.ensureDashboard(); await bot.ensureDashboard();
  assert.equal(sent, 1); assert.equal(edits, 1); const state = JSON.parse(await fs.readFile(path.join(temp, 'state', 'bot-state.json'), 'utf8')); assert.equal(state.dashboardMessageId, 'dashboard-message');
  await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard startup finds an older pinned panel when state or recent history is missing', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-dashboard-pinned-recovery-')); let sent = 0;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash' }, pinDashboard: true }, security: {}, lmp: {}, universe: {} };
  const status = { online: true, pid: 7, owned: false, ready: true, registration: true, playerCount: 1, nat: '127.0.0.1:2252', settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10, modControl: true, backupIntervalMs: 30000 }, master: { visible: true, checked: true }, bridge: { ready: false, configured: false }, storage: { available: true, free: '1 GB' }, plugins: [], logAgeMs: 10, healthSummary: 'healthy', healthWarnings: [] };
  const message = { id: 'old-pinned-dashboard', author: { id: 'bot' }, embeds: [{ footer: { text: 'APF persistent dashboard' } }], pinned: true, edit: async () => message };
  const channel = { id: 'dash', isTextBased: () => true, messages: { fetch: async value => typeof value === 'string' ? null : [], fetchPinned: async () => ({ find: predicate => predicate(message) ? message : null }) }, send: async () => { sent++; return message; } };
  const bot = new DiscordBot(config, { status: async () => status }, { stats: async () => ({ fileCount: 1, size: '1 KB' }), listBackups: async () => [] });
  bot.client = { user: { id: 'bot', setPresence: () => {} }, channels: { fetch: async () => channel } };
  await bot.ensureDashboard();
  assert.equal(sent, 0); assert.equal(bot.dashboardMessageId, message.id);
  await fs.rm(temp, { recursive: true, force: true });
});

test('Discord initialization is single-flight and retryable without duplicate startup announcements', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-discord-init-')); let loads = 0; let registers = 0; let dashboards = 0; let announcements = 0;
  const config = { __root: temp, discord: { channels: {} }, lmp: { control: { mode: 'observe' } }, security: {}, universe: {} };
  const bot = new DiscordBot(config, {}, {}); bot.loadState = async () => { loads++; await new Promise(resolve => setTimeout(resolve, 10)); }; bot.registerCommands = async () => { registers++; }; bot.ensureDashboard = async () => { dashboards++; }; bot.sendConsole = async () => { announcements++; };
  await Promise.all([bot.initializeDiscord(), bot.initializeDiscord()]); await bot.initializeDiscord();
  assert.equal(loads, 2); assert.equal(registers, 2); assert.equal(dashboards, 2); assert.equal(announcements, 1); assert.equal(bot.discordReady, true);
  await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard refresh recreates a deleted persistent message without duplicating on transient errors', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-dashboard-recover-')); let sent = 0; let statusCalls = 0;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash' }, pinDashboard: false }, security: {}, lmp: {}, universe: {} };
  const status = { online: true, pid: 7, owned: false, ready: true, registration: true, playerCount: 0, nat: '127.0.0.1:2252', settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10, modControl: false, backupIntervalMs: 30000 }, master: { visible: true, checked: true }, bridge: { ready: false, configured: false }, storage: { available: true, free: '1 GB' }, plugins: [], logAgeMs: 10, healthSummary: 'healthy', healthWarnings: [] };
  const deleted = { id: 'old-dashboard', author: { id: 'bot' }, embeds: [{ footer: { text: 'APF persistent dashboard' } }], pinned: false, edit: async () => { const error = new Error('Unknown Message'); error.code = 10008; throw error; } };
  const replacement = { id: 'new-dashboard', author: { id: 'bot' }, embeds: [{ footer: { text: 'APF persistent dashboard' } }], pinned: false, edit: async () => replacement };
  const channel = { id: 'dash', isTextBased: () => true, messages: { fetch: async value => typeof value === 'string' ? (value === 'old-dashboard' ? deleted : replacement) : [] }, send: async () => { sent++; return replacement; } };
  const bot = new DiscordBot(config, { status: async () => { statusCalls++; return status; } }, { stats: async () => ({ fileCount: 1, size: '1 KB' }), listBackups: async () => [] });
  bot.client = { user: { id: 'bot', setPresence: () => {} }, channels: { fetch: async () => channel } };
  bot.dashboardMessage = deleted; bot.dashboardMessageId = deleted.id; bot.dashboardChannelId = channel.id;
  await bot.refreshDashboard();
  assert.equal(sent, 1); assert.equal(bot.dashboardMessageId, 'new-dashboard'); assert.ok(statusCalls >= 2);
  await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard initialization is single-flight under concurrent startup and refresh calls', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-dashboard-single-flight-')); let sent = 0;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash' }, pinDashboard: false }, security: {}, lmp: {}, universe: {} };
  const status = { online: true, pid: 7, owned: false, ready: true, registration: true, playerCount: 0, nat: '127.0.0.1:2252', settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10, modControl: false, backupIntervalMs: 30000 }, master: { visible: true, checked: true }, bridge: { ready: false, configured: false }, storage: { available: true, free: '1 GB' }, plugins: [], logAgeMs: 10, healthSummary: 'healthy', healthWarnings: [] };
  const message = { id: 'single-flight-dashboard', author: { id: 'bot' }, embeds: [{ footer: { text: 'APF persistent dashboard' } }], pinned: false, edit: async () => message };
  const channel = { id: 'dash', isTextBased: () => true, messages: { fetch: async () => { await new Promise(resolve => setTimeout(resolve, 20)); return []; } }, send: async () => { sent++; return message; } };
  const bot = new DiscordBot(config, { status: async () => { await new Promise(resolve => setTimeout(resolve, 20)); return status; } }, { stats: async () => ({ fileCount: 1, size: '1 KB' }), listBackups: async () => [] });
  bot.client = { user: { id: 'bot', setPresence: () => {} }, channels: { fetch: async () => channel } };
  await Promise.all([bot.ensureDashboard(), bot.ensureDashboard(), bot.ensureDashboard()]);
  assert.equal(sent, 1);
  await fs.rm(temp, { recursive: true, force: true });
});

test('dashboard refresh repairs a missing pin', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-dashboard-pin-')); let pins = 0;
  const config = { __root: temp, discord: { channels: { dashboard: 'dash' }, pinDashboard: true }, security: {}, lmp: {}, universe: {} };
  const status = { online: true, pid: 7, owned: false, ready: true, registration: true, playerCount: 0, nat: '127.0.0.1:2252', settings: { name: 'APF', mode: 'Career', port: 2252, maxPlayers: 10, modControl: false, backupIntervalMs: 30000 }, master: { visible: true, checked: true }, bridge: { ready: false, configured: false }, storage: { available: true, free: '1 GB' }, plugins: [], logAgeMs: 10, healthSummary: 'healthy', healthWarnings: [] };
  const message = { id: 'dashboard-pin', author: { id: 'bot' }, embeds: [{ footer: { text: 'APF persistent dashboard' } }], pinned: false, edit: async () => message, pin: async () => { pins++; message.pinned = true; } };
  const channel = { id: 'dash', isTextBased: () => true, messages: { fetch: async () => message }, send: async () => message };
  const bot = new DiscordBot(config, { status: async () => status }, { stats: async () => ({ fileCount: 1, size: '1 KB' }), listBackups: async () => [] });
  bot.client = { user: { id: 'bot', setPresence: () => {} }, channels: { fetch: async () => channel } };
  bot.dashboardMessage = message; bot.dashboardMessageId = message.id; bot.dashboardChannelId = channel.id;
  await bot.refreshDashboard(); assert.equal(pins, 1);
  await fs.rm(temp, { recursive: true, force: true });
});

test('public status moves below new messages and sends one offline ping per outage', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-public-status-')); let deletes = 0; let edits = 0; const sent = [];
  const config = { __root: temp, discord: { channels: { publicStatus: 'public', lunaChat: 'chat' }, offlineAlertUserId: '545068258508931082' }, lmp: { serverName: 'EXTI' }, security: {}, universe: {} };
  const online = { online: true, ready: true, playerCount: 0, uptimeMs: 1000, settings: { name: 'EXTI', mode: 'Career' }, master: { visible: true } };
  const offline = { online: false, ready: false, playerCount: 0, uptimeMs: null, settings: { name: 'EXTI', mode: 'Career' }, master: { visible: false } };
  const backup = { id: 'backup', createdAt: new Date().toISOString(), size: '62 MB' };
  let sequence = 0;
  const makeMessage = payload => {
    const message = { id: `status-${++sequence}`, author: { id: 'bot', bot: true }, embeds: (payload.embeds || []).map(embed => embed.toJSON?.() || embed) };
    message.delete = async () => { deletes++; };
    message.edit = async next => { edits++; message.embeds = (next.embeds || []).map(embed => embed.toJSON?.() || embed); return message; };
    return message;
  };
  let existing = makeMessage({ embeds: [publicStatusEmbed(online, backup)] });
  const channel = { id: 'public', isTextBased: () => true, messages: { fetch: async value => typeof value === 'string' ? existing : { find: predicate => predicate(existing) ? existing : null } }, send: async payload => { sent.push(payload); existing = makeMessage(payload); return existing; } };
  const bot = new DiscordBot(config, { status: async () => online }, { listBackups: async () => [backup] });
  bot.client = { user: { id: 'bot' }, channels: { fetch: async () => channel }, destroy: () => {} };
  bot.publicStatusMessage = existing; bot.publicStatusMessageId = existing.id; bot.publicStatusChannelId = channel.id;
  await bot.refreshPublicStatus({ moveToBottom: true, status: online, backups: [backup] });
  assert.equal(deletes, 1); assert.equal(sent.length, 1); assert.equal(sent[0].components, undefined);
  const stableId = bot.publicStatusMessage.id; const priorDeletes = deletes; const priorSends = sent.length;
  await bot.syncPublicStatus({ ...online, playerCount: 1 }, [backup]);
  assert.equal(bot.publicStatusMessage.id, stableId); assert.equal(deletes, priorDeletes); assert.equal(sent.length, priorSends);
  let scheduled = 0; bot.schedulePublicStatusMove = () => { scheduled++; };
  await bot.handleMessage({ channelId: 'public', author: { id: 'human', bot: false }, embeds: [] }); assert.equal(scheduled, 1);
  bot.publicStatusOnline = true;
  const pollAt = Date.now();
  bot.publicAvailabilityMonitor.now = () => pollAt + 45000;
  for (const seconds of [0, 15, 30, 45]) await bot.syncPublicStatus({ ...offline, observedAt: new Date(pollAt + seconds * 1000).toISOString() }, [backup]);
  const alerts = sent.filter(payload => typeof payload.content === 'string' && payload.content.includes('went offline'));
  assert.equal(alerts.length, 1); assert.match(alerts[0].content, /<@545068258508931082>/); assert.ok(edits >= 1);
  bot.stop(); await fs.rm(temp, { recursive: true, force: true });
});

test('public status renders deployment downtime instead of stale online state', () => {
  const status = { online: true, ready: true, playerCount: 2, uptimeMs: 1000, settings: { name: 'EXTI' } };
  const embed = publicStatusEmbed(status, null, Date.now(), null, '5293', { status: 'deploying', phase: 'installing', version: 'next' }).toJSON();
  assert.match(embed.title, /SERVER UPDATING/);
  assert.match(embed.description, /temporarily offline while an update installs/);
});

test('planned update downtime suppresses the public offline ping', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-planned-offline-'));
  const config = { __root: temp, discord: { channels: { publicStatus: 'public' }, offlineAlertUserId: '545068258508931082' }, lmp: {}, security: {}, universe: {} };
  const bot = new DiscordBot(config, {}, { listBackups: async () => [] }); let alerts = 0;
  bot.publicStatusOnline = true; bot.sendPublicOfflineAlert = async () => { alerts++; }; bot.refreshPublicStatus = async () => ({});
  await bot.syncPublicStatus({ online: false }, [], { update: { status: 'deploying', phase: 'stopping' } });
  assert.equal(alerts, 0);
  bot.publicStatusOnline = true;
  const pollAt = Date.now();
  bot.publicAvailabilityMonitor.now = () => pollAt + 60000;
  for (const seconds of [15, 30, 45, 60]) await bot.syncPublicStatus({ online: false, observedAt: new Date(pollAt + seconds * 1000).toISOString() }, [], { update: { status: 'idle' } });
  assert.equal(alerts, 1);
  await fs.rm(temp, { recursive: true, force: true });
});

test('public status card omits queued-update detail handled by announcements', () => {
  const status = { online: true, ready: true, playerCount: 2, uptimeMs: 1000, settings: { name: 'EXTI' } };
  const deployAt = '2026-08-14T00:15:00.000Z';
  const embed = publicStatusEmbed(status, null, Date.parse('2026-08-14T00:00:00.000Z'), null, '5293', { status: 'queued', version: 'next', deployAt, countdownChangelog: 'Hiring fix.' }).toJSON();
  assert.doesNotMatch(embed.description, /<t:/); assert.doesNotMatch(embed.description, /Hiring fix/); assert.doesNotMatch(embed.description, /queued/);
});

test('public status rejects only its own PATCH rate-limit queue', () => {
  const config = { discord: { channels: { publicStatus: '1008956290942373938' } } };
  assert.equal(rejectPublicStatusRateLimit(config, { method: 'PATCH', majorParameter: '1008956290942373938', route: '/channels/:id/messages/:id', global: false }), true);
  assert.equal(rejectPublicStatusRateLimit(config, { method: 'POST', majorParameter: '1008956290942373938', route: '/channels/:id/messages', global: false }), false);
  assert.equal(rejectPublicStatusRateLimit(config, { method: 'PATCH', majorParameter: 'other', route: '/channels/:id/messages/:id', global: false }), false);
  assert.equal(rejectPublicStatusRateLimit(config, { method: 'PATCH', majorParameter: '1008956290942373938', route: '/channels/:id/messages/:id', global: true }), false);
});

test('public status rate limit keeps only newest snapshot for retry', async () => {
  const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-public-rate-limit-'));
  const config = { __root: temp, discord: { channels: { publicStatus: 'public' } }, lmp: { serverName: 'EXTI' }, security: {}, universe: {} };
  const backup = { id: 'backup', createdAt: new Date().toISOString(), size: '62 MB' };
  let attempts = 0;
  const message = {
    id: 'status', author: { id: 'bot', bot: true },
    edit: async payload => {
      attempts++;
      if (attempts === 1) throw Object.assign(new Error('rate limited'), { name: 'RateLimitError[/channels/:id/messages/:id]', retryAfter: 10, method: 'PATCH', scope: 'shared' });
      message.embeds = (payload.embeds || []).map(embed => embed.toJSON?.() || embed);
      return message;
    }
  };
  const channel = { id: 'public', isTextBased: () => true, messages: { fetch: async () => message }, send: async () => message };
  const bot = new DiscordBot(config, { status: async () => ({}) }, { listBackups: async () => [backup] });
  bot.client = { user: { id: 'bot' }, channels: { fetch: async () => channel }, destroy: () => {} };
  bot.publicStatusMessage = message; bot.publicStatusMessageId = message.id; bot.publicStatusChannelId = channel.id;
  const first = { online: true, ready: true, playerCount: 1, uptimeMs: 1000, settings: { name: 'EXTI' } };
  const latest = { ...first, playerCount: 2 };
  await bot.refreshPublicStatus({ status: first, backups: [backup] });
  await bot.refreshPublicStatus({ status: latest, backups: [backup] });
  await new Promise(resolve => setTimeout(resolve, 900));
  assert.equal(attempts, 2);
  assert.match(message.embeds[0].fields.find(field => field.name === 'Players').value, /^2 players online/);
  assert.equal(bot.publicStatusRetryTimer, null);
  bot.stop(); await fs.rm(temp, { recursive: true, force: true });
});
