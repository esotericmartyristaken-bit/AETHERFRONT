# APF Discord setup

## Discord application

1. Create a Discord application and bot at the Discord Developer Portal.
2. Enable the **Message Content Intent**. It is required for the `luna-chat` text mirror.
3. Invite the bot to the APF guild with these permissions:
   - View Channels
   - Send Messages
   - Embed Links
   - Read Message History
   - Use Application Commands
   - Manage Messages (optional; needed only if the bot should pin the dashboard)
4. Create these channels, or map equivalent IDs in `config.json`:
   - `dashboard`: persistent, state-tracked (and optionally pinned) status embed and lifecycle buttons.
   - `console`: logs, audit trail, health alerts, operator commands, backups, and universe inspection.
   - `luna-chat`: LMP/Discord chat mirror.
   - `servers-status`: optional public status card; configured as `discord.channels.publicStatus`. New channel messages cause the card to be deleted and re-sent at the bottom. `discord.offlineAlertUserId` receives one mention when online state transitions to offline.

If using `npm run provision:channels` instead of creating channels manually, the bot also needs **Manage Channels** during provisioning. Remove that permission afterward if desired; runtime operation does not require it.

`/help` returns a complete structured catalog in Discord-safe embed fields. Console also includes `/who`, `/server-time`, `/motd`, and `/rules`; set optional `discord.motd` and `discord.rules` in `config.json` for community-facing text.

## Authorization

Discord channel visibility is the authorization boundary:

- Anyone able to access `dashboard` can use its lifecycle, maintenance, backup, and diagnostic buttons.
- Anyone able to access `console` can use all console and `/universe` commands, including restart/stop and confirmation-gated native commands.
- Keep both channels private with Discord category/channel permission overrides.

Role-ID arrays remain accepted for compatibility but do not further restrict members already admitted to these private channels.

## Local configuration

Copy `config.example.json` to `config.json`, then fill `guildId` and channel IDs. Configure Discord permission overwrites before granting users either private control channel.

After copying the config, `scripts\Initialize-APFDiscordConfig.ps1` can write and validate all Discord IDs without touching the token:

```powershell
& .\scripts\Initialize-APFDiscordConfig.ps1 -GuildId '...' -DashboardChannelId '...' -ConsoleChannelId '...' -LunaChatChannelId '...'
```

If the managed text channels do not exist yet, set `discord.guildId` and `APF_DISCORD_TOKEN`, then run `npm run provision:channels`. The provisioner finds or creates `dashboard`, `console`, and `luna-chat`, then safely writes their IDs to `config.json`; it does not write or print the token.

Set the token only in the environment:

```powershell
$env:APF_DISCORD_TOKEN = 'paste-token-here'
Set-Location '.\APFDiscordBot'
npm run validate
npm start
```

Do not put the token in JSON, source, Git, or a Discord message.

Before live startup, run `npm run doctor`. It is read-only and reports LMP health, current backup verification, bridge artifact/install hashes, configured roots, and which Discord activation values are still missing.

Optional bot auto-launch: after setting `APF_DISCORD_TOKEN` in the user environment, run `scripts\Install-APFDiscordBotTask.ps1`. This registers an interactive-logon task for the bot only; observe mode remains non-owning and does not stop/restart LMP. Remove it with `scripts\Uninstall-APFDiscordBotTask.ps1`.

## Bridge deployment

`scripts\Build-APFDiscordBridge.ps1` builds against the `lmp.serverRoot` in `config.json` (or an explicit `-ServerRoot`) and refreshes `artifacts\APFDiscordBridge.dll`. Copy that artifact into the live server's `Plugins` directory only during a planned LMP restart. The bot does not copy it automatically. After restart, a successful plugin load creates:

```text
<LMP root>\APFDiscordBridge\ready.json
<LMP root>\APFDiscordBridge\inbox\
<LMP root>\APFDiscordBridge\events\
```

The bridge has no Discord token and no network code. It exchanges bounded JSON files. The Node bot verifies the bridge PID before enqueueing Discord chat, enforces inbox/event size limits, expires stale messages, and quarantines malformed or oversized events. Valid bridge events stay queued until the Discord send succeeds; log-tail chat has a bounded retry queue controlled by `security.chatQueueMax` and `security.chatQueueTtlMs`. The server plugin also caps the event queue at 200 files and 500 characters per event. This prevents stale queues or unbounded input from targeting a different server process.

Inbound items carry an expiry timestamp and are capped at 64 KiB by the plugin. Malformed outbound event files are moved under `events\failed` instead of being retried forever. Discord-originated echoes are suppressed by short-lived event IDs/text tracking.

The isolated bridge smoke test is available as `scripts\Test-APFDiscordBridge.ps1`. It clones the server into `APF\Runtime-Tests`, disables master registration, uses UDP `22952`, verifies plugin startup and Discord-to-LMP queue processing, then removes the temporary clone. It never targets the live install.

Supervisor lifecycle smoke is available as `scripts\Test-APFLifecycle.ps1`. It uses a separate clone and UDP `22953` to exercise bot-owned start/stop behavior, then removes the clone. It never targets the live install.

Run `scripts\Test-APFAll.ps1` to execute the complete safe validation suite: unit tests, JavaScript syntax checks, dependency audit, bridge clone smoke, and lifecycle clone smoke. It never stops or modifies the live LMP process.

The bot persists the dashboard message ID in `state\bot-state.json`, so restarts do not create a new dashboard merely because the channel has more than 50 newer messages. It pins the dashboard when `discord.pinDashboard` is true and the bot has permission; lack of pin permission does not stop startup. Bot-generated console/chat messages suppress Discord mentions.

`/command` and `/lmp` both require operator confirmation, even for allowlisted native commands. `/events` shows locally persisted joins, leaves, health transitions, and alert batches. Password values in `changesettings` and broadcast text passed through native `say` are redacted from previews/audit records. `/universe verify` and `/universe diff` are read-only backup integrity/comparison tools; new backups include a per-file SHA-256 manifest, while older backups use count/byte verification. Craft/Kerbal/vessel/scenario recovery additionally requires bot maintenance mode and an offline LMP process; the bot-local maintenance flag is not a join lock. Asset recovery and full restore/rollback use a cross-process mutation lock to prevent concurrent file changes.

## Rollback

Stop the bot, remove only `APFDiscordBridge.dll` from `Plugins`, and restart LMP. The game server remains usable without the Discord bot. Do not delete the universe or the bridge queue as part of rollback unless queued chat is intentionally disposable.

## Backup without Discord

The bot schedules backups only while it is running. For independent protection, run `npm run backup` once or install the optional Windows task:

```powershell
& .\scripts\Install-APFUniverseBackupTask.ps1 -IntervalMinutes 30
```

The task uses local `config.json`, requires no Discord token, and does not stop LMP. Remove it with `Unregister-ScheduledTask -TaskName 'APF Universe Backup'` when no longer wanted.
