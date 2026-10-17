# APF Discord activation checklist

Live activation completed on 2026-08-13: bot scheduled task running, channels registered, bridge installed, visible external LMP terminal preserved, and command/chat capabilities healthy. Keep this checklist for rebuilds or additional hosts.

## Safe observe-only activation

1. Create a Discord application/bot. Enable Message Content Intent.
2. Invite it with View Channels, Send Messages, Embed Links, Read Message History, Use Application Commands, and optionally Manage Messages (required only for dashboard pinning).
3. Put the guild ID in `config.json` and set `APF_DISCORD_TOKEN` only in the bot process environment.
4. Run `npm run provision:channels` to find/create `dashboard`, `console`, and `luna-chat`.
5. Restrict `dashboard` and `console` with Discord channel permissions; run `npm run validate`.
6. Start `npm start` with `lmp.control.mode=observe`.
7. Confirm the dashboard shows the existing LMP PID, UDP port, master listing, player count, universe size, backup age, and `Chat bridge: not ready/stale`.

Optional: after one successful foreground run, install interactive-logon auto-launch with `scripts\Install-APFDiscordBotTask.ps1`. This task starts only the Discord bot and does not stop/restart LMP.

Observe mode does not own the externally launched server's stdin. Status, logs, player/session parsing, dashboard refresh, filesystem backups, and universe tools work immediately. Once `APFDiscordBridge` is installed, allowlisted native commands and two-way chat also work without transferring process ownership. Separately enabled external lifecycle flags control graceful start/stop/restart.

## Bridge rollout for a rebuild/additional host

1. Announce maintenance in Discord/LMP chat. Confirm current backup and `/universe verify` pass.
2. Stop LMP through the existing operator/supervisor. Do not use an unplanned force-stop.
3. Run `scripts\Build-APFDiscordBridge.ps1` and verify its SHA-256 output.
4. Run `scripts\Install-APFDiscordBridge.ps1 -ServerRoot '<LMP root>'` while `Server.exe` is stopped.
5. Start the same LMP process with the same universe/configuration.
6. Confirm `APF Discord` / `Bridge ready` in the LMP log and `Chat bridge: ready` in `/status`.
7. Send one harmless test message from `luna-chat`; confirm it appears in LMP. Send one harmless LMP message; confirm it appears in Discord.
8. Run a harmless `/countclients`; confirm the bridge result contains only command-local log lines.

The bridge has no Discord token and no outbound network access. Removing only `Plugins\APFDiscordBridge.dll` and restarting LMP rolls back the bridge.

## Supervisor migration later

Supervisor ownership is optional. The current production model deliberately retains the visible external LMP terminal and uses the in-process command bridge. If migrating to `control.mode=supervise`, use a separate maintenance window and clone/test universe first.
