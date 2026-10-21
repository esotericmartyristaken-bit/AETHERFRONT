import path from 'node:path';
import { readJson } from './util.js';

const root = path.resolve(process.env.APF_BOT_ROOT || path.join(import.meta.dirname, '..'));

export async function loadConfig() {
  const file = process.env.APF_BOT_CONFIG || path.join(root, 'config.json');
  const config = await readJson(file, null);
  if (!config) throw new Error(`Missing config: ${file}. Copy config.example.json to config.json.`);
  config.__file = file;
  config.__root = root;
  config.__codeRoot = process.env.APF_CONTROL_RELEASE_ROOT ? path.join(process.env.APF_CONTROL_RELEASE_ROOT, 'APFDiscordBot') : root;
  config.__nativeCodeRoot = process.env.APF_CONTROL_RELEASE_ROOT ? path.join(process.env.APF_CONTROL_RELEASE_ROOT, 'KESAS-VPS-Server') : null;
  config.__controlRoot = process.env.APF_CONTROL_ROOT || null;
  config.discord ??= {};
  config.discord.channels ??= {};
  config.discord.adminRoleIds ??= [];
  config.discord.operatorRoleIds ??= [];
  config.discord.moderatorRoleIds ??= [];
  config.discord.publicServerPassword ??= '';
  config.lmp ??= {};
  config.lmp.control ??= {};
  config.security ??= {};
  config.universe ??= {};
  config.updates ??= {};
  return config;
}

export function tokenFor(config) { return process.env[config.discord.tokenEnv || 'APF_DISCORD_TOKEN'] || ''; }

export function channelConfigured(config, key) { return Boolean(config.discord.channels?.[key]); }

export function roleSet(config, roleIds) { return new Set([...(roleIds || [])]); }

export function hasAnyRole(member, roleIds) {
  const allowed = roleSet(null, roleIds);
  return Boolean(member?.roles?.cache?.some(role => allowed.has(role.id)));
}

export function isOperator(member, config) {
  return Boolean(member?.permissions?.has?.('Administrator') || hasAnyRole(member, config.discord.operatorRoleIds) || hasAnyRole(member, config.discord.adminRoleIds));
}

export function isModerator(member, config) {
  return isOperator(member, config) || hasAnyRole(member, config.discord.moderatorRoleIds);
}
