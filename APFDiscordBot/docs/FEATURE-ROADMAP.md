# APF Discord feature map

## Implemented foundation

- Persistent dashboard embed with refresh, player, backup, start, stop, and restart controls.
- Private-channel-gated slash command surface with cooldowns and audit messages.
- Read-only LMP process discovery, XML settings inspection, log tail, NAT/registration/readiness indicators, master-list check, uptime, and process resource telemetry.
- Deduplicated incident-edge alerts for LMP offline/online recovery, master-list loss/recovery, bridge loss/recovery, and health transitions.
- Deduplicated stale/missing automatic-backup alerts with recovery notice.
- Discord gateway error/shard reconnect handling with dashboard refresh after recovery.
- Managed-channel routing: dashboard controls, console operations/universe tools, and luna-chat.
- Scheduled and manual filesystem universe snapshots with atomic completion metadata and retention pruning.
- Craft, Kerbal, filename/content search, recent-file, and backup inspection.
- Maintenance-gated craft and Kerbal recovery from a selected backup with exact confirmation and no-overwrite policy.
- Backup verification and two-backup diff inspection without live mutation.
- Persistent dashboard message identity with optional pinning and duplicate-resistant startup recovery.
- Confirmation gate on every Discord path that can invoke a native LMP command.
- Structured player join/leave/session parsing and local event history (`/events`).
- Deferred Discord interaction acknowledgements for slow status, backup, diagnostics, and universe operations.
- Read-only `/diagnose` and `/bridge` incident views.
- Read-only `/modcontrol` summary of the live `LMPModControl.xml` policy.
- Public successful command results inside the gated console; private authorization errors and confirmation gates.
- PID-bound, dual-allowlisted command bridge for external visible LMP processes, with result acknowledgements and cursor-bounded log output.
- Case-insensitive Discord player targets normalized to LMP's exact live player name.
- Discord-to-LMP messages labeled with guild display names.
- Persisted maintenance windows with optional expiry and automatic clear/notice.
- Structured `/help` catalog plus safe `/who`, `/server-time`, `/motd`, and `/rules` community commands.
- Cross-process backup locking and per-file SHA-256 manifests for new backups.
- Vessel/scenario/contracts inventory, metadata inspection, and guarded non-overwriting recovery.
- Cross-process universe mutation lock covering asset recovery and full restore/rollback.
- Hash-aware backup diffs when both snapshots contain integrity manifests.
- Server-side LMP bridge for two-way chat without putting Discord credentials in LMP.
- Bridge event IDs plus short-lived text fallback prevent echo loops without dropping distinct identical chat messages.
- Bridge delivery retry/TTL and bounded log-chat retry queue prevent transient Discord outages from silently losing chat.
- Safe command allowlist; no arbitrary PowerShell or shell execution from Discord.
- Read-only `npm run doctor` report covering LMP runtime, latest backup verification, bridge hashes, roots, and Discord activation readiness.
- Single safe validation runner covering code, dependency, bridge, and lifecycle checks without targeting live LMP.
- Optional interactive-logon scheduled-task installer for the Discord bot, with explicit token preflight and no LMP lifecycle side effects.
- Read-only Windows UDP bind/owner telemetry in health reports and the dashboard.
- Pinned-dashboard recovery when state is missing or the panel is older than the recent-message scan window.
- Dashboard distinguishes LMP’s internal save interval from APF universe-backup cadence.
- Graceful bot shutdown clears monitoring/retry timers and disconnects Discord without touching LMP lifecycle.

## Next high-value features

### Operations

- Per-server profiles: one bot, multiple LMP roots, ports, universes, manifests, and Discord channel groups.
- Health checks for process age, master registration, NAT address, log freshness, memory, disk, and backup age.
- Incident state machine: detect crash, post one alert, suppress duplicates, expose recovery status, optionally restart only after operator policy permits it.
- Maintenance windows with countdown, join lock, warning broadcasts, and automatic return-to-service check.
- Start/restart reason capture and post-restart health summary.
- Read-only process console streaming with severity filters and log attachments for long incidents.

### Universe and player tools

- Backup browser with age/size/checksum, retention preview, immutable restore plan, guarded full restore, retained restore-point listing, and rollback.
- Two-person restore approval, player drain, staging validation, and post-restore validation remain next for destructive production recovery.
- Craft browser with owner, vessel type, part count, last update, export/download, and quarantine workflow.
- Kerbal roster: active/dead/available, location, owner, recovery/export tools.
- Scenario/contract search and diff between two backups.
- Player presence cards, join/leave history, connection duration, ping/timeout counters, and moderation notes.
- Typed allowlisted `/vessel`, `/setfunds`, `/setscience`, `/dekessler`, `/nukeksc`, kick/ban, client, lock, settings, and restart shortcuts with explicit confirmations.

### Chat and community

- Discord reply threads mapped to LMP sender when protocol permits.
- Per-user cooldowns, slow mode, word filters, mention escaping, and moderator delete/report actions. Any future mute system must be explicitly Discord-relay-only or use verified Discord↔LMP account linking; LMP 0.29.2 has no native server mute command.
- Join/leave/system message categories and optional separate `luna-events` channel.
- Player linking: Discord account ↔ LMP name, with opt-in verification code.
- Discord commands such as `/who`, `/whereis`, `/motd`, `/rules`, and `/server-time`.

### Admin and security

- Encrypted secret store or Windows Credential Manager integration.
- Signed audit records and exportable action history.
- Capability roles per command instead of one broad operator role.
- Dry-run mode for every destructive operation.
- Server allowlist and per-server command policy.
- Bridge queue quotas, stale-item expiry, malformed-item quarantine, and replay protection.
- Code-sign the helper/plugin later; keep HTTPS/hash verification for any future mod/asset downloads.

### Future APF integration

- APF pack manifest status in Discord: pack ID/version, missing/extra mod count, client version, and join-block reason.
- Manifest publish/rollback workflow with validation before visibility.
- Client sync incident reports and aggregate incompatible-client diagnostics.
- Private APF master health and server-directory controls.

## Design boundary

Discord should be a control plane and observability plane, not the source of truth for universe files. LMP remains authoritative for runtime state; backups are filesystem snapshots; every destructive mutation requires maintenance and an auditable, reversible workflow.
