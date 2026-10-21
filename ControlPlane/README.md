# Host control contract 1: managed broker

Source pushes are not deployments. The fixed public repository
`esotericmartyristaken-bit/AETHERFRONT` has two separate monotonic intents on
`deploy`: `deployment/control-plane.json` and `deployment/production.json`.
Only an authenticated immutable repository intent, followed by exact archive and
file-inventory verification, authorizes installation. An uploaded ZIP or hash
alone does not authorize code execution.

## Executing components

`Upgrade-HostAnchor.ps1` is the one-time, normal-UAC transition from the original
interactive control-2 installation. It preserves task XML and bootstrap files,
requires an idle native publisher, and does not restart the game, change its
SYSTEM identity/action, modify the vanilla task, reboot, or change networking.
It snapshots the existing operator GitHub and Discord credentials into
machine-DPAPI ciphertext. File ACLs restrict GitHub ciphertext to SYSTEM and
elevated Administrators; only the limited bot identity additionally reads its
own Discord ciphertext. Machine DPAPI is not an ACL substitute.

The `Aetherfront Native Publisher` task runs the fixed `Start-Anchor.ps1` and
`anchor.mjs` as SYSTEM. The fixed loader retains archive/schema/path/dependency
verification, repository authority, OS/helper lease checks, pointer switching and
fallback. The loaded `broker-host.mjs`, `native-agent.mjs`, publisher wrapper,
reporter, bot/watcher, verifier, publisher/export/GitHub scripts, and locked bot
dependencies all come from a versioned authenticated control bundle. Runtime
code is not imported from a limited bot's writable mirror.

The `APF Discord Bot` task remains limited, uses noninteractive S4U, and launches
the selected worker and bot. It receives no GitHub publisher token. Startup and
five-minute recovery triggers plus restart-on-failure replace interactive-logon
dependency. Installation checks must verify real Discord/network use under this
identity; task registration alone is not proof. No reboot/logoff test is implied.

```
bootstrap/                     fixed verifier/loader, runtime, protected credentials
managed/releases/control-N-HASH/ authenticated code and exact locked dependencies
managed/active.json             atomic protected selection for broker and worker
managed/fallback.json           previous known-good selection
managed/verified-active.json    two-observation broker startup proof
managed/transaction.json        revisioned control operation
managed/transactions/HASH.json   retained immutable-identity history
managed/drain.json              bounded control-only drain signal
worker-status.json              limited observation, never privileged authority
worker-diagnostics.json         bounded worker/inventory observation
native-cache/                   private requests, receipts, logs, recovery evidence
native-requests/                declarative limited inbox, not executable commands
native-results/                 observation mirrors, never completion authority
host-status.json                fresh sanitized report
```

Old control-2 journals/releases and RC33 recovery evidence remain intact. Control
installation uses `managed/`; it does not reset the old failed/successful history.

## Controller commands

Use the certificate SHA256 from the trusted bootstrap, not an unauthenticated
error page. `PIN` is a public fingerprint. These commands use controller-local
GitHub authentication. They never require the host signing keys.

```powershell
git pull --ff-only
node ControlPlane/controller.mjs status --origin https://217.216.49.250:18866 --pin PIN
node ControlPlane/controller.mjs diagnostics --origin https://217.216.49.250:18866 --pin PIN

# Build the next reviewed control revision, including broker/wrapper fixes.
pwsh -File ControlPlane/Build-ControlBundle.ps1 -Version control-5 -Output C:/build/Aetherfront-Control-control-5.zip
node ControlPlane/controller.mjs publish-control --origin https://217.216.49.250:18866 --pin PIN --archive C:/build/Aetherfront-Control-control-5.zip --version control-5

# Combined path observes exact control version/hash/health before app intent.
pwsh -File ControlPlane/Publish-HostRelease.ps1 -ControlArchive C:/build/Aetherfront-Control-control-5.zip -ControlVersion control-5 -Archive C:/build/Aetherfront-Release-kesas-rc34.zip -Version kesas-rc34 -HostCertificateSha256 PIN -Notes 'Reviewed changes'

# Existing compatible host: application only.
pwsh -File ControlPlane/Publish-HostRelease.ps1 -Archive C:/build/Aetherfront-Release-kesas-rc34.zip -Version kesas-rc34 -HostCertificateSha256 PIN -Notes 'Reviewed changes'

# Only after diagnostics advertises this precise safe preactivation action.
node ControlPlane/controller.mjs recover --origin https://217.216.49.250:18866 --pin PIN
```

Use the actual next version, not an already consumed immutable version. Publishing
identical bytes is idempotent. Conflicting bytes, stale readback, unknown requested
capabilities, or app/control version mismatch fail closed. During initial
transition only, the operator publishes the reviewed control-3 intent through the
same fixed repository because the old stale report cannot pass normal controller
preflight. The UAC installer independently authenticates that intent and downloads
its public bytes; it does not trust the working tree as deployed broker code.

## Activation and failure recovery

The loader stages complete control archives into protected new directories,
validates every entry and locked dependency (`npm ci --ignore-scripts`), drains
the broker/limited worker, checks live helper PIDs and native OS lease, and journals
before switching one pointer. Startup requires two independently published broker
observations. The previous pointer remains available for startup rollback. Never
edit an immutable installed release in place.

An ordinary failed application does not block idle control repair. A live applying
publisher is never terminated. An ambiguous native journal can continue blocking
application replay without blocking diagnosis or compatible idle control repair.
The loader does not use application mirrors to authorize code.

The existing 15-minute warning/early-empty native guards, fresh player counts,
signing, backup, native activation journal, and completion checks still govern
every application. Control publication does not authorize app replay. Applications
are accepted only after healthy exact control activation and compatible fresh
inventory. Do not reset/delete journals, forge completion receipts, or use force.

`recover` writes a bounded one-hour `deployment/recovery.json` intent, binding a
unique nonce, exact original request/archive/source commit and protected-result
digest. The broker independently authenticates it, retains the original evidence,
consumes the nonce before acting, and only admits proved pre-publisher retries.
The new attempt still needs a fresh normal countdown/early-empty guard. Native
activation/GitHub-completion ambiguity is not guessed safe. Specific sanitized
rejections identify changed results, live helpers, ambiguous activation,
identity/baseline mismatch, or consumed authorization. A newer compatible control
release remains available when an application retry is rejected.

## Independent readback

The privileged reporter refreshes `kesas-host-status.zip` and
`kesas-host-diagnostics.zip` every ten seconds. Reports expire in 30 seconds.
Worker crashes and inventory errors produce stopped/unknown/incomplete states,
not a retained healthy claim. Expensive archive inventory runs outside the
heartbeat. Completion is reconstructed from protected request/attempt receipts
and exact cached archive bytes, actual feed/catalog identity, fresh bridge state,
and independently pinned HTTPS health. No receipt timestamp is fabricated.

Status remains schema 1. Diagnostics schema 2 retains schema-1 fields and adds
`host: {stage, reason, worker, broker, inventory, leaseActive, journalPhase,
exitCode}`. All values are fixed sanitized codes; paths, raw errors, player names,
configuration, keys, tokens and child output stay private and capped. The fixed
loader emits a fresh blocked fallback if broker reporting stops. Its
`versioned-broker-v1` capability permits control repair publication, never app
publication. A disconnected host still cannot serve fresh reports.

Capabilities additionally include `versioned-broker-v1`, `independent-report-v1`,
`unattended-host-v1`, and `media-reference-v1`. Application/media capabilities are
withheld until the actual running worker and installed inventory agree.

## Large media and remaining boundaries

See [MEDIA.md](MEDIA.md) for exact separate immutable media publication/reference
schema and command. Embedded small `MediaPayload/` remains supported. Neither
path loosens ordinary `SquadExpansion`/stock-folder protection. A compatible
signed expansion bootstrap must separately implement any protected DLC install.

Routine client/updater/helper, server/plugins, pack service, catalog/mod media,
publisher/wrapper/agent/reporter/watcher and bot dependency changes use the
authenticated app/control paths. Manual boundaries remain OS/runtime-major and
fixed loader contract provisioning, physical/OS recovery, unavailable/replaced
host, lost/revoked credentials, lost/compromised trust/signing keys, and genuinely
ambiguous native activation requiring evidence unavailable remotely. Future
business-logic fixes do not require copying agent/wrapper files under UAC.
The existing Administrator/deployment directories remain trusted host boundaries;
this is not a sandbox against a malicious local administrator.
