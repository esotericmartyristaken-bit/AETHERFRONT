# APF Discord bot requirement audit

This is the current implementation audit for the APF control plane.

| Requirement | Current evidence | State |
| --- | --- | --- |
| Managed channels: `dashboard`, `console`, `luna-chat`; universe tools folded into console | `src/provision-channels.js`, `src/validate-config.js`, startup ID/guild/permission preflight in `src/bot.js` | Implemented and live |
| Persistent dashboard embed | `ensureDashboard`, persisted `state/bot-state.json`, pinned-panel recovery, 30-second refresh | Implemented and live |
| Dashboard status information | Process/PID, uptime, memory, UDP bind owner, players, readiness, master listing, NAT, bridge, mod-control, backups, storage, plugins, log age | Implemented |
| Dashboard buttons | Refresh, Players, Health, Logs, Backup, Universe, Connections, Maintenance, Start, Stop, Restart | Implemented |
| Button confirmations and cleanup | Ephemeral replies, confirmation buttons, transient deletion, lifecycle mutex, private-channel gate | Implemented and tested |
| Console command surface | 42 registered top-level commands plus `/universe` subcommands; structured `/help`; typed native shortcuts; public success output inside gated console | Implemented, tested, and registered live |
| Native LMP control | Dual allowlists in Node/plugin; private confirmation; PID-bound request/results; direct stdin for owned processes and bridge fallback for external LMP | Implemented and live-tested with `countclients`/non-player `kick` |
| Universe backups | Scheduled/manual snapshots, retention, inter-process lock, atomic metadata, SHA-256 manifests, verification | Implemented and clone-tested |
| Universe inspection | Craft/Kerbal/vessel/scenario/contracts inventory, metadata, owner/state filters, search, recent changes, safe export | Implemented and clone-tested |
| Recovery | Non-overwriting asset recovery; guarded full restore/rollback; maintenance, offline, confirmation, mutation-lock gates | Implemented and clone-tested; disabled by default |
| Luna chat mirror | LMP→Discord event tailing; Discord→LMP bridge queue; guild display-name labels, dedupe, echo suppression, cooldowns, retry/TTL | Implemented and live; native LMP has no server mute command |
| Health/incident visibility | Process, readiness, registration, master visibility, UDP bind, memory, disk, bridge, backup staleness, debounced transition alerts; quiet log age is telemetry-only | Implemented and doctor-verified |
| Live-server safety | Exact-process validation, graceful restart, pre-deploy universe backup, retained prior bridge DLL, visible terminal preserved | Verified live |
| Automated validation | Node suite, .NET 6 bridge build, live PID/UDP/master/bridge checks | Passing: 58/58; bridge build 0 warnings/errors |

## Deliberately pending

- Discord token remains environment-only.
- Dashboard pinning remains unavailable until the bot receives `Manage Messages`; dashboard persistence itself is live.
- Full restore/asset recovery remain disabled by configuration unless explicitly enabled.
- Supervisor ownership is optional; the current visible external LMP process uses the command bridge.

## Completed live proof

1. Guild/channel configuration and environment-only token validated.
2. Bot scheduled task running; 42 slash commands registered; obsolete mute commands absent.
3. Bridge reports matching live PID and `chat`/`commands` capabilities with empty queues.
4. LMP healthy, UDP-owned, master-visible, and visible-terminal launch preserved after graceful deployment restart.
5. Harmless bridge command returned LMP's exact execution/result lines.
