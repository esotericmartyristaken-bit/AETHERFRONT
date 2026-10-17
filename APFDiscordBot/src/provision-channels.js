import { ChannelType, Client, GatewayIntentBits } from 'discord.js';
import { loadConfig, tokenFor } from './config.js';
import { readJson, writeJsonAtomic } from './util.js';

const config = await loadConfig();
const token = tokenFor(config);
const guildId = process.env.APF_DISCORD_GUILD_ID || config.discord.guildId;
if (!token) throw new Error(`Discord token missing. Set ${config.discord.tokenEnv || 'APF_DISCORD_TOKEN'}.`);
if (!/^\d{17,20}$/.test(String(guildId || ''))) throw new Error('Guild ID missing/invalid. Set discord.guildId or APF_DISCORD_GUILD_ID.');

const names = { dashboard: 'dashboard', console: 'console', lunaChat: 'luna-chat' };
const client = new Client({ intents: [GatewayIntentBits.Guilds] });
try {
  await client.login(token);
  const guild = await client.guilds.fetch(guildId);
  const channels = await guild.channels.fetch();
  const stored = await readJson(config.__file, {});
  stored.discord ??= {}; stored.discord.channels ??= {};
  for (const [key, name] of Object.entries(names)) {
    let channel = channels.find(candidate => candidate?.type === ChannelType.GuildText && candidate.name === name);
    if (!channel) channel = await guild.channels.create({ name, type: ChannelType.GuildText, reason: 'APF Discord bot managed-channel setup' });
    stored.discord.channels[key] = channel.id;
    console.log(`${key}: #${channel.name} (${channel.id})`);
  }
  stored.discord.guildId = guild.id;
  await writeJsonAtomic(config.__file, stored);
  console.log(`Wrote channel IDs to ${config.__file}. Token remained environment-only.`);
} finally { client.destroy(); }
