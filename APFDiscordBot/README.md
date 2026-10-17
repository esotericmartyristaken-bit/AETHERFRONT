# APF Discord Bot

Local Discord control/observability layer for Luna Multiplayer.

## Configured channels

- `dashboard`: persistent status embed + buttons; includes process uptime, memory, player count, NAT, UDP bind ownership, master visibility, bridge, mod-control, storage, universe, and backup state.
- `console`: operator commands, logs, process/registration health, player/session events, backups, SHA-256 verification, two-backup diffs, restore planning, and universe inspection.
- `luna-chat`: two-way LMP/Discord chat bridge.
- `servers-status`: public, buttonless status card. It automatically moves beneath new announcements and sends one configured-user ping when LMP transitions offline.

Health monitoring treats process/UDP/master/bridge/storage/memory failures as actionable incidents. Startup readiness has a grace window; transient probe changes require consecutive confirmations; recovery requires its own confirmations. Quiet LMP logs are telemetry only, because an idle server can legitimately stop writing log lines while remaining healthy.

The bridge is server-side and token-free: the LMP plugin writes inbound chat events to a local queue; the Node process writes Discord messages and allowlisted native commands to separate local queues. Command requests receive PID-bound result files. Discord credentials never enter the game server. Bridge events remain on disk until Discord delivery succeeds or their bounded TTL expires; log-tail chat uses a bounded in-memory retry queue. Discord-to-LMP chat uses each member's guild display name, then global/username fallbacks.

## Safe rollout

Full token/channel/bridge rollout sequence: [docs/ACTIVATION-CHECKLIST.md](docs/ACTIVATION-CHECKLIST.md).
Requirement-by-requirement evidence and remaining activation gates: [docs/REQUIREMENT-AUDIT.md](docs/REQUIREMENT-AUDIT.md).

1. Copy `config.example.json` to `config.json`.
2. Fill Discord guild/channel IDs. Restrict `dashboard` and `console` with Discord channel permissions; access to either private control channel grants its relevant controls.
3. Set `APF_DISCORD_TOKEN` in the process environment. Never commit it.
4. Run `npm install` then `npm run validate`.
5. Run `npm run doctor` for a read-only runtime, backup, bridge, path, and Discord-readiness report.
6. Run `npm run test:safe` for the complete clone-only validation suite.
7. Start in `control.mode=observe`. This attaches to an existing LMP process without owning its stdin.
8. Install `APFDiscordBridge` during a planned restart to enable two-way chat and allowlisted native commands while preserving a separate visible LMP terminal. Supervisor mode remains optional.

Bot startup preflights the token, guild ID, managed channel IDs, channel ownership, and required channel permissions; incomplete Discord configuration fails fast instead of creating a logged-in but unusable bot. Missing `Manage Messages` only disables dashboard pinning.

For optional automatic bot launch at interactive logon, set `APF_DISCORD_TOKEN` in the user environment and run `scripts\Install-APFDiscordBotTask.ps1`. It creates only the bot task; it does not stop or restart LMP. Remove it with `scripts\Uninstall-APFDiscordBotTask.ps1`.

If the managed channels do not exist yet, set `discord.guildId` and `APF_DISCORD_TOKEN`, grant the bot **Manage Channels** temporarily, then run `npm run provision:channels`. It finds or creates `dashboard`, `console`, and `luna-chat`. Map the optional existing public announcement channel as `discord.channels.publicStatus` and its outage recipient as `discord.offlineAlertUserId`.

External-process start/stop/restart flags are separate and default `false`. Enabled external stop/restart first uses the bounded graceful-stop helper and falls back only if LMP does not exit; use lifecycle control with maintenance + backup policy.

The bot never executes arbitrary shell text from Discord. Discord channel permissions are the control-plane authorization boundary: anyone who can access `dashboard` may use its buttons, and anyone who can access `console` may use console and `/universe` commands. Native LMP commands remain allowlisted and confirmation-gated. Universe restore remains disabled by default and additionally requires maintenance, an offline server, and exact confirmation. Backup creation uses an inter-process lock so the bot scheduler and independent Windows task cannot duplicate a snapshot.

## Live external-server model

The live server runs independently in a visible terminal. The bot therefore cannot write directly to that process's stdin (`not-controller-owned`). The installed `APFDiscordBridge` handles two-way chat and tightly allowlisted commands inside LMP; lifecycle operations use the separately enabled external-process helper. Arbitrary shell text is never accepted.

LMP 0.29.2 has no native server mute/unmute command. Discord-only relay mute commands were removed rather than presenting them as LMP moderation.

## Commands

Console channel: `/help`, `/status`, `/health`, `/diagnose`, `/bridge`, `/modcontrol`, `/players`, `/who`, `/settings`, `/plugins`, `/logs`, `/connections`, `/locks`, `/clients`, `/server-time`, `/motd`, `/rules`, `/lmp`, `/say`, `/backup`, `/start`, `/stop`, `/restart`, `/maintenance`, `/events`, `/audit`, `/command`.

Successful console and `/universe` results are public within the gated console channel. Authorization failures and state-changing confirmation buttons remain ephemeral.

Typed native LMP shortcuts are also available: `/countclients`, `/listclients`, `/listlocks`, `/connectionstats`, `/changesettings`, `/clearvessels`, `/dekessler`, `/nukeksc`, `/kick`, `/ban`, `/vessel`, `/setfunds`, `/setscience`, and `/restartserver`. They use the same private-console gate, confirmation, allowlist, audit, and lifecycle protection as `/lmp`.

`/maintenance on` accepts an optional `minutes` value (1-1440). The expiry is persisted, displayed on the dashboard, and automatically clears with a console/event notice; `/maintenance on` without a duration remains indefinite until explicitly disabled.

Console universe commands: `/universe status`, `/universe backups`, `/universe backup`, `/universe verify`, `/universe diff`, `/universe changes`, `/universe crafts`, `/universe craft-info`, `/universe kerbals`, `/universe kerbal-info`, `/universe vessels`, `/universe vessel-info`, `/universe scenarios`, `/universe contracts`, `/universe search`, `/universe export`, `/universe recover-craft`, `/universe recover-kerbal`, `/universe recover-vessel`, `/universe recover-scenario`, `/universe restore-plan`, `/universe restore-points`, `/universe restore`, `/universe rollback`.

`/universe export` is read-only relative to the live universe. Craft/Kerbal recovery is disabled unless `allowAssetRestore` is explicitly enabled, maintenance mode is on, the exact confirmation token is supplied, the server is offline, and the destination does not already exist. Full-universe restore/rollback is disabled unless `allowRestore` is explicitly enabled; both require maintenance, an offline server, exact confirmation, SHA-256 verification, atomic staging, and a retained rollback copy.
