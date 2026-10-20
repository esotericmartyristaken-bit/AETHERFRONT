import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { PublicAvailabilityMonitor, HealthMonitor, healthIssues } from '../src/health.js';
import { DiscordBot, publicStatusEmbed } from '../src/bot.js';

const epoch = Date.parse('2026-10-20T10:00:00Z');
function snapshot(state, seconds) {
  return { observedAt: new Date(epoch + seconds * 1000).toISOString(), online: state === 'unknown' ? null : state === 'online', processObservation: { state, reason: state === 'offline' ? 'process-not-found' : 'bridge-status-stale' } };
}
function monitor() { return new PublicAvailabilityMonitor({}, () => epoch + 3600000); }

test('one failed poll, duplicate snapshots, and rapid calls cannot confirm an outage', () => {
  const m = monitor();
  for (let i = 0; i < 10; i++) assert.deepEqual(m.update(snapshot('offline', 0), true), { online: true, wentOffline: false });
  assert.equal(m.update(snapshot('offline', 1), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 2), true).wentOffline, false);
  assert.equal(m.update(snapshot('online', 3), true).online, true);
  assert.equal(m.update(snapshot('offline', 35), true).wentOffline, false, 'recovery reset the failure run');
});

test('three independent failures spanning 30 seconds confirm a real outage once', () => {
  const m = monitor();
  assert.equal(m.update(snapshot('offline', 0), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 15), true).wentOffline, false);
  assert.deepEqual(m.update(snapshot('offline', 30), true), { online: false, wentOffline: true });
  assert.deepEqual(m.update(snapshot('offline', 45), false), { online: false, wentOffline: false });
});

test('unknown, out-of-order, and widely separated observations do not confirm an outage', () => {
  const m = monitor();
  m.update(snapshot('offline', 0), true);
  m.update(snapshot('unknown', 15), true);
  assert.equal(m.update(snapshot('offline', 30), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 10), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 200), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 215), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 230), true).wentOffline, true);
});

test('brief recovery cannot re-arm repeated pings during one outage', () => {
  const m = monitor();
  m.update(snapshot('offline', 0), true); m.update(snapshot('offline', 15), true);
  assert.equal(m.update(snapshot('offline', 30), true).online, false);
  assert.equal(m.update(snapshot('online', 35), false).online, false);
  assert.equal(m.update(snapshot('offline', 40), false).wentOffline, false);
  assert.equal(m.update(snapshot('online', 50), false).online, false);
  assert.equal(m.update(snapshot('online', 60), false).online, true);
  m.update(snapshot('offline', 65), true); m.update(snapshot('offline', 80), true);
  assert.equal(m.update(snapshot('offline', 95), true).wentOffline, true);
});

test('unknown samples never create health offline or recovery notifications', () => {
  const m = new HealthMonitor();
  const online = { online: true, healthIssues: [] };
  m.update(online);
  for (const seconds of [0, 15, 30, 45]) assert.deepEqual(m.update(snapshot('unknown', seconds)), []);
  assert.deepEqual(m.update(online), []);
  assert.deepEqual(healthIssues(snapshot('unknown', 50)).map(row => row.code), ['status-observation']);
});

test('unknown public status is not presented as offline or verified online', () => {
  const embed = publicStatusEmbed(snapshot('unknown', 0)).toJSON();
  assert.match(embed.title, /STATUS UNAVAILABLE/);
  assert.doesNotMatch(embed.title, /SERVER OFFLINE|SERVER ONLINE/);
  assert.match(embed.description, /does not confirm an outage/);
});

test('public sync serializes concurrent polls and logs unknown evidence without sending pings', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-offline-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const config = { __root: root, discord: { channels: { publicStatus: 'public' } }, lmp: {}, security: {}, universe: {} };
  const bot = new DiscordBot(config, {}, { listBackups: async () => [] });
  t.after(() => bot.stop());
  bot.publicAvailabilityMonitor = monitor();
  bot.publicStatusOnline = true;
  let alerts = 0; const displays = []; const events = [];
  bot.sendPublicOfflineAlert = async () => { alerts++; };
  bot.refreshPublicStatus = async options => { displays.push(options.status); };
  bot.recordEvent = async (type, details) => { events.push({ type, ...details }); };
  await Promise.all(Array.from({ length: 8 }, () => bot.syncPublicStatus(snapshot('offline', 0), [], { update: { status: 'idle' } })));
  assert.equal(alerts, 0);
  assert.equal(displays.at(-1).processObservation.state, 'unknown');
  for (const seconds of [15, 30, 45]) await bot.syncPublicStatus(snapshot('unknown', seconds), [], { update: { status: 'idle' } });
  assert.equal(alerts, 0);
  assert.ok(events.some(event => event.type === 'server-observation' && event.reason === 'bridge-status-stale'));
  for (const seconds of [60, 75, 90, 105]) await bot.syncPublicStatus(snapshot('offline', seconds), [], { update: { status: 'idle' } });
  assert.equal(alerts, 1);
  assert.equal(bot.publicStatusOnline, false);
  const restarted = new DiscordBot(config, {}, { listBackups: async () => [] });
  t.after(() => restarted.stop());
  await restarted.loadState();
  restarted.publicAvailabilityMonitor = monitor();
  restarted.sendPublicOfflineAlert = async () => { alerts++; };
  restarted.refreshPublicStatus = async () => {};
  await restarted.syncPublicStatus(snapshot('offline', 120), [], { update: { status: 'idle' } });
  assert.equal(alerts, 1, 'a bot restart must not repeat an ongoing outage alert');
});

test('confirmed planned deployment downtime never sends an offline ping', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-planned-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const bot = new DiscordBot({ __root: root, discord: { channels: { publicStatus: 'public' } }, lmp: {}, security: {}, universe: {} }, {}, { listBackups: async () => [] });
  t.after(() => bot.stop());
  bot.publicAvailabilityMonitor = monitor(); bot.publicStatusOnline = true;
  let alerts = 0;
  bot.sendPublicOfflineAlert = async () => { alerts++; };
  bot.refreshPublicStatus = async () => {};
  for (const seconds of [0, 15, 30, 45]) await bot.syncPublicStatus(snapshot('offline', seconds), [], { update: { status: 'deploying', phase: 'installing' } });
  await bot.syncPublicStatus(snapshot('offline', 60), [], { update: { status: 'idle' } });
  assert.equal(alerts, 0);
});

test('planned samples cannot contribute to an unplanned outage confirmation', () => {
  const m = monitor();
  m.update(snapshot('offline', 0), true, { planned: true });
  m.update(snapshot('offline', 15), true, { planned: true });
  assert.equal(m.update(snapshot('offline', 30), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 45), true).wentOffline, false);
  assert.equal(m.update(snapshot('offline', 60), true).wentOffline, true, 'real failure still alerts after fresh unplanned confirmation');
});

test('direct public card refresh cannot bypass offline confirmation', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'apf-public-direct-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const bot = new DiscordBot({ __root: root, discord: { channels: { publicStatus: 'public' } }, lmp: {}, security: {}, universe: {} }, { status: async () => snapshot('offline', 0) }, { listBackups: async () => [] });
  t.after(() => bot.stop());
  bot.publicStatusOnline = true;
  const payloads = [];
  const message = { id: 'card', author: { id: 'bot' }, embeds: [], edit: async payload => { payloads.push(payload); return message; } };
  bot.publicStatusMessage = message;
  bot.getChannel = async () => ({ id: 'public', isTextBased: () => true });
  await bot.refreshPublicStatus();
  assert.match(payloads.at(-1).embeds[0].toJSON().title, /STATUS UNAVAILABLE/);
  assert.doesNotMatch(payloads.at(-1).embeds[0].toJSON().description, /Staff have been notified/);
});
