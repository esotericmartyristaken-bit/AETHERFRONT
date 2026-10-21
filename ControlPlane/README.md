# Host control contract 1

This is the host-side controller linkage. An ordinary source push does not deploy an application or a control release. Application releases and control releases have separate monotonic versions and separate files on the existing `deploy` branch of `esotericmartyristaken-bit/AETHERFRONT`.

## Components and ownership

The configured production `APFDiscordBot` directory holds operator configuration, credentials supplied through the existing environment, bot state, native queue state and staged application releases. It is not replaced by a source checkout. The configured `KESAS-VPS-Server` directory holds the deployment settings, Universe, identities, keys, game/pack binaries, public archives and signed client feed.

The initial installation creates `ProgramData/Aetherfront-Control`, under a protected system parent rather than a replaceable user-owned workspace directory. The root namespace, fixed code, host settings and privileged cache are protected; the limited coordinator receives write access only to its runtime state/staging areas. The already provisioned PowerShell runtime is copied into the protected bootstrap; the privileged Node runtime must be under Program Files.

Layout:

```
bootstrap/                 fixed, protected supervisor/verifier/native broker
host.json                  protected operator paths and provisioned runtime versions
releases/control-N-HASH/   complete immutable control code plus locked dependencies
active.json                atomically selected code directory
fallback.json              one known-good control pointer
verified-active.json       completed startup-health proof bound to version and hash
transaction.json           current durable control transaction; never cleared to retry
transactions/HASH.json     durable revisioned transaction history
maintenance.json           narrowly owned activation gate
native-requests/            limited bot's declarative archive requests, not commands
native-cache/               protected authenticated code, archives and broker journals
native-results/             sanitized completion mirrors, not authorization
native-capabilities.json    frozen broker/wrapper hashes and recovery protocol
host-status.json            sanitized local readback
```

The limited `APF Discord Bot` task starts a fixed PowerShell wrapper. It holds a Windows exclusive file lease for the supervisor's lifetime. The supervisor runs a versioned coordinator, which runs the limited bot/watcher/native queue. Only the versioned worker, bot source, pinned dependencies and six native scripts are remote control-payload destinations. The executing version directory is never overwritten.

The separate, fixed `Aetherfront Native Publisher` task runs elevated under the existing interactive operator account so it can inspect the SYSTEM game and use that account's existing GitHub authentication. The bot is not elevated. Requests cannot specify commands, destinations, executable paths or force switches. The broker independently authenticates the exact application intent at an immutable GitHub commit, copies and rehashes its archive into protected storage, and invokes independently verified native publisher code. It never imports release-provided JavaScript into its privileged process.

The existing SYSTEM `KESAS Server` task remains the game/pack process owner. Its future-start action uses a protected launcher selector. Updating the control plane does not restart this task, the game or pack service. An application publication uses the original guarded stop/start and native rollback machinery.

## Versioned contracts and coverage

`contract.mjs` is shared by the controller and host. Unknown fields/types, duplicate JSON keys in control transport/archive metadata, unsupported schemas and transaction versions fail closed. Existing application channel schema 1 remains readable. Newly published application intent uses schema 2 and declares its minimum capabilities.

Capabilities: `application-v1`, `media-payload-v1`, `control-plane-v1`, `host-report-v1`, `guard-15m-empty-v1`, `atomic-control-rollback-v1`, `native-tools-v1`.

| Component | Update path | Inventory/readback |
| --- | --- | --- |
| Watcher and native queue runner | Versioned bot source in control bundle | Actual `github-deployment.js` / `update-manager.js` SHA256 |
| Publisher, verifier, GitHub publisher, export and game launch scripts | Explicit control-file allowlist | Actual script SHA256 and control bundle SHA256 |
| Bot dependencies | Exact direct versions, integrity-pinned public npm lock, staged `npm ci --ignore-scripts --engine-strict` | Canonical digest of the actual installed dependency inventory; lock and per-file hashes are retained in the verified release receipt |
| Pack service | `PackHostPayload/` application family | Canonical digest of the full pack binary/runtime/dependency inventory |
| Server runtime and plugins | `ServerPayload/` application family | Canonical digest of root executable/DLL/runtime files; complete per-file plugin inventory digest |
| Certificate/signing update tools | `CertificateToolPayload/`, `UpdateToolPayload/` | Full tool/runtime/dependency inventory digests; operator keys are excluded |
| Client bootstrap, in-game updater and post-exit helper | Signed `ClientTemplate.zip` application path | Bootstrap hash; verified updater DLL group and helper/embedded-helper assembly group hashes from the actual signed client ZIP |
| Catalog and public mod archives | Pack template and additive `MediaPayload/` | Combined actual pack-document/install-catalog hash; canonical digest of public archive names and actual content hashes |
| Fixed bootstrap and broker | Reviewed host-local operator installation only | Bootstrap hash; explicit broker/wrapper hashes in diagnostics; bootstrap contract 1 and separately advertised native recovery protocol 1 |

The public hash groups are not a claim that a mod DLL or KSP itself was launched during host acceptance tests. Client package file hashes remain in the signed feed. Source provenance and running code are distinct: only a completed host readback establishes activation.

Public mod ZIPs may be hardlinked to the existing immutable pack cache. Their read-only inventory checks file identity and content timestamps before and after hashing; control code, journals and writes retain the strict no-hardlinks policy. The changing `kesas-host-status.zip` and `kesas-host-diagnostics.zip` are excluded from the media digest.

## Trust and prerequisites

The existing trust boundary is retained: authenticated write access to this fixed repository's deployment branch authorizes host release intent. The host reads the branch through HTTPS, pins an immutable commit, rereads the descriptor there and checks archive hashes and full inventory. A standalone hash, upload or source commit does not authorize execution. Existing host-held signing keys still sign client updates; the controller does not acquire those keys.

Manual prerequisites are explicit: Windows x64, protected Program Files Node 24 with npm, provisioned PowerShell 7.4 or newer in major 7, the existing .NET 8 application runtime, existing limited bot and SYSTEM game tasks, operator GitHub authentication, Discord credential, deployment configuration, certificates, signing keys and licensed KSP/client build prerequisites. The existing deployment directory and Administrator account remain trusted host boundaries; this is not an operating-system sandbox for an untrusted local administrator. Existing server paths are not relocated or re-permissioned. The fixed bootstrap contract, Windows/runtime provisioning, account/credential provisioning, and identity/key loss recovery do not self-update. A contract/runtime-major change needs a reviewed operator bootstrap. No autologon, account, VPN, RDP or firewall changes are made. Existing tasks resume after the operator's interactive logon following reboot; unattended pre-logon hosting is not newly provisioned.

## Controller commands

Use the existing host certificate SHA256 from the trusted client bootstrap. Do not replace it with a value from an unauthenticated error page. `PIN` below is a public certificate fingerprint, not a password.

```powershell
# Fresh capability/current/pending/phase/health/hash/failure readback:
node .\ControlPlane\controller.mjs status --origin https://217.216.49.250:18866 --pin PIN

# Separate sanitized diagnostics, including failures before native handoff:
node .\ControlPlane\controller.mjs diagnostics --origin https://217.216.49.250:18866 --pin PIN

# Explicitly authorize one proved-safe, blocked pre-publisher retry:
node .\ControlPlane\controller.mjs recover --origin https://217.216.49.250:18866 --pin PIN

# Read the requested control channel (not proof of activation):
node .\ControlPlane\controller.mjs read

# Build reviewed control code, never a working directory or arbitrary ZIP:
pwsh -File .\ControlPlane\Build-ControlBundle.ps1 -Version control-2 -Output C:\Aetherfront\Aetherfront-Control-control-2.zip

# Publish control only; immutable prerelease, never the latest client release:
node .\ControlPlane\controller.mjs publish-control --origin https://217.216.49.250:18866 --pin PIN --archive C:\Aetherfront\Aetherfront-Control-control-2.zip --version control-2

# Normal combined path: activate required control code, observe its exact hash
# and completed health, recheck capabilities, then publish application intent:
pwsh -File .\ControlPlane\Publish-HostRelease.ps1 -ControlArchive C:\Aetherfront\Aetherfront-Control-control-2.zip -ControlVersion control-2 -Archive C:\Aetherfront\Aetherfront-Release-kesas-rc33.zip -Version kesas-rc33 -HostCertificateSha256 PIN -Notes 'Reviewed changes'

# Application-only path when the installed control code is already compatible:
pwsh -File .\ControlPlane\Publish-HostRelease.ps1 -Archive C:\Aetherfront\Aetherfront-Release-kesas-rc33.zip -Version kesas-rc33 -HostCertificateSha256 PIN -Notes 'Reviewed changes'
```

Missing, unreachable, stale, mismatched or unsupported capability reports stop publication before the application channel is advanced. Application publication rechecks immediately before its final channel write. Schema 2 automatically requires media/tool capabilities when those payload families are present. Source push and prerelease upload are not deployment success.

Status is a bounded single-entry STORE ZIP, `kesas-host-status.zip`, served by the existing pinned HTTPS pack service. It is refreshed during downloads and pending transactions, contains only fixed public labels/versions/hashes/health codes, and expires after 150 seconds. No tokens, operator paths, raw errors, config contents, signing material, player names or Universe data are included. The controller uses a cache-busting URL and exact certificate pin; it has no unverified fallback.

Interpret states separately: `accepted`, `staging`, `waiting-existing-guard`, `applying`, `healthy`, `rolled-back`, `blocked`. A `healthy` control version needs a durable version/hash-bound startup proof. A verified fallback can accept a newer repair control release without pretending the failed release succeeded. Application publication remains blocked until control recovery is healthy and failure-free.

## Guards, recovery and rollback

Both watchers retain a minimum 60-second poll. Native application deployment retains the 15-minute warning/early-empty behavior, fresh known player counts and existing backup/native journals. Only a completed matching 15-minute countdown with recorded warning and fresh bridge counts can produce `-AllowActivePlayers`; a request or premature operator force flag cannot.

Control staging validates the entire archive, allowed destinations, hashes and dependencies in a fresh directory. It waits for native idle and the native OS publisher lease, establishes its maintenance marker, drains both bot polling loops, then durably hands off. The fixed supervisor switches one pointer, checks replacement startup, and restores the known-good pointer on failure. A rollback intent precedes the rollback pointer so a reboot cannot revive a rejected candidate. Revisioned history repairs a process death between the two journal writes. Duplicate/completed/older transactions do not requeue publication.

An orphaned applying publisher is never killed. The independent supervisor keeps a fresh blocked readback while waiting. The protected broker checks its durable result, recorded helper PID, native lease, active feed/pack and pinned health before recognizing completion. It never replays an ambiguous applying/native-activation operation. An interrupted native-activation journal remains blocked for operator recovery; deleting or clearing it is not a recovery method. Existing publisher GitHub-completion recovery remains available for the same verified bytes and notes after review.

### Native diagnostics and recovery protocol 1

The original host-report contract 1 is unchanged. A separate bounded STORE ZIP, `kesas-host-diagnostics.zip`, contains only `host-diagnostics.json` and expires after 150 seconds. It reports fixed watcher/native stages and reason codes, immutable request/archive/source identities, a result digest, exit code, attempt number and actual frozen broker/wrapper hashes. Watcher download/staging errors are reported even before a native request exists. Raw exceptions, output, notes, paths, player data and credentials are never included. Detailed capped stdout, stderr, exceptions and typed publisher receipts remain under protected `native-cache/diagnostics/REQUEST/`.

`recover` is an explicit repository-authenticated request, not automatic failure replay. It writes `deployment/recovery.json` on `deploy`, with schema 1, a unique `recoveryId`, fixed action `retry-preactivation`, request/version/archive/source identity, the exact protected-result digest and a maximum one-hour validity. The frozen broker independently pins the branch commit, rereads those exact bytes, validates the original immutable application intent and archive, consumes the nonce in protected history, and refuses any helper/lease/journal activity, prepared version, non-older active release or unhealthy baseline. Only failed stages before a publisher PID could exist qualify. A fresh normal 15-minute queue or authoritative early-empty guard remains mandatory. The limited watcher cannot authorize recovery by editing a mirror.

Publisher preparation, activation and GitHub-completion failures remain held for host review; this endpoint never guesses that they are safe to replay. A consumed/interrupted authorization remains audited and is not replayed. Download/staging failures before native handoff retain the watcher's existing safe polling behavior; recovery does not reset its attempt history. A waiting/cancelled recovery does not monopolize newer deployment requests.

A host operator may separately review a completed automatic rollback: verify every restored replacement/state tree, absence of an active journal or helper, exact prepared signed bytes and the healthy older release. The operator-only `--authorize-reviewed-rollback-retry` requires the digest of that protected evidence as well as the original result digest. It retains both histories and issues a protected, readable bootstrap approval. `UpdateManager.acknowledgeReviewedRollback` consumes that approval against the exact failed queue bytes, archives the failed state and records `lastDeployment.status: rolled-back`; it does not claim the failed release succeeded. This audited terminal acknowledgement allows a repair control release to activate through the ordinary idle guard before the original application is queued again with a fresh warning/early-empty guard. It is not a remote rollback-reset endpoint. Native cold-start readiness is bounded at 180 seconds and still requires owned listeners and pinned HTTPS health.

For an urgent host-local publisher correction, that same protected rollback evidence can instead bind a `publisherRepairSha256` manifest. A normal UAC-reviewed operator installs exactly the six allowed native PowerShell scripts in a new protected cache snapshot. The broker verifies the manifest/request/original-result binding, every script size/hash and all fixed destinations before selecting it for that one request. Remote recovery intent cannot create or select this operator snapshot. This permits the corrected publisher to complete the original application with its normal countdown/player/lease/signing/rollback guards without waiting for a separate control-worker rollout. No active or immutable control directory is edited in place. The ordinary source/control rollout remains a separate operation.

`nativeRecoveryVersion: 1` is advertised only when the frozen agent's capability receipt matches the actual installed broker and wrapper hashes. The privileged broker and wrapper still require an idle, reviewed host-local installation under normal UAC; publishing a control bundle or pushing source does not replace them. Windows launches the hidden publisher without Node's `detached` flag, using protected regular-file handles rather than parent-owned pipes. A successful private receipt bound to the exact request/archive is required before completion; process exit 0 or a matching version string alone is insufficient. No live process-death/fault experiment is implied by deployment verification.

One fallback pointer is retained. Older immutable staging/release directories and transaction evidence are not automatically deleted; they are not additional selectable fallback slots. Disk-retention cleanup is a separate operator task.

## Isolated acceptance

```powershell
cd .\APFDiscordBot
node --test
cd ..
# Required only for real native publication fixture: a privately provisioned
# existing UpdateTool directory with the public ClientTemplate.zip beside it.
$env:APF_TEST_UPDATE_TOOL='C:\Aetherfront\KESAS-VPS-Server\UpdateTool'
node --test .\ControlPlane\test\*.test.mjs
```

Fixtures start with publisher/verifier/watcher files from `7b28642`, upgrade through the real controller publication function and host staging path, then exercise the actual upgraded verifier/publisher and signed media application path. A second/skipped control revision changes and loads its installer module. Tests cover archive/authentication/schema/path/reparse/private-media/conflict rejection, staging/activation/rollback crashes, revisioned-journal recovery, duplicates/competition, failed startup rollback and protected-state preservation. GitHub transport, bridge health and unrelated-process sentinels are test doubles; archive bytes, PowerShell extraction, native publisher and disposable signing keys are real. The optional native fixture is explicitly skipped without its private prerequisites. No production fault injection, application deployment or KSP GUI launch is part of these host tests.

Initial installation is an operator action, after the tests and source review pass:

```powershell
pwsh -File .\ControlPlane\Install-HostControl.ps1 -ControlRoot C:\ProgramData\Aetherfront-Control -BotRoot C:\Aetherfront\APFDiscordBot -NativeRoot C:\Aetherfront\KESAS-VPS-Server -PowerShell C:\Tools\PowerShell7\pwsh.exe
```

It requests normal UAC for the fixed bootstrap/task setup, preserves prior task actions, verifies native idle before changing the bot launcher, restarts only the changed bot/control components and restores the prior actions if startup health fails. It does not deploy RC33 or restart the current game/pack processes.
