# GitHub deployment channel

This channel ships an already built and tested native KESAS bundle from another
Windows PC. Pushing source alone does not deploy anything. The publisher does not
build, execute KSP tests, start services, copy credentials, install a runner, or
change the production host directly. No self-hosted GitHub Actions runner is
needed or authorized by this workflow.

## Trust and fixed contract

Repository: `esotericmartyristaken-bit/AETHERFRONT`. Branch: `deploy`.
Channel file: `deployment/production.json`. The publisher accepts only this
repository; branch and path are not command-line overrides.

```json
{
  "schemaVersion": 1,
  "version": "kesas-rc29",
  "releaseTag": "host-kesas-rc29",
  "assetName": "Aetherfront-Release-kesas-rc29.zip",
  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "sizeBytes": 123456,
  "notes": "Describe the already tested change."
}
```

Example hash and size are placeholders, not a publishable artifact. Exactly these
seven fields are required; no unknown/duplicate fields. Version is `kesas-rcN`,
where N is a positive integer of at most 15 digits, matching the host watcher. Tag and asset name
must match version exactly. SHA-256 is 64 lowercase hexadecimal characters.
`sizeBytes` is a positive integer, at most 536870912 bytes (512 MiB). Notes contain
at most 500 UTF-16 code units, no Unicode control/format characters. There is no
arbitrary URL or command field. Consumers derive the exact repository release URL
from the fixed repository, tag and asset name.

Deployment authority is GitHub repository/channel write authority, HTTPS and the
channel's exact digest/length. **The bundle is not RSA-signed.** A digest inside a
repository-writable manifest does not protect against a repository writer. Such a
writer can request executable deployment and client content that the host signs.
Restrict repository/release/`deploy` write access; protect the branch, require
appropriate review and secure GitHub accounts. Do not put hosting private keys,
certificates, game files, tokens, or production configuration in Git or bundles.

The native host keeps its existing signer locally and generates the signed client
feed/archive through `Publish-Release.ps1`. The other PC needs **no hosting
signing key**. Do not copy or regenerate the host's keypair or TLS certificate.

## First setup on another PC

1. Obtain reviewed source, PowerShell 7 (`pwsh`), Git and GitHub CLI (`gh`). Sign in
   interactively: `gh auth login --hostname github.com --web`. Use an account/token
   limited to the intended repository with release and contents write permission;
   do not paste credentials into commands, source, documentation or build logs.
2. Have a repository administrator create the `deploy` branch once, through GitHub
   or a reviewed Git operation. The publisher deliberately does not create it or
   silently choose another branch. An absent channel file is allowed on that
   existing branch. Initialize a baseline only with the exact reviewed bundle for
   the release already active on the host, not a dummy hash. Host baseline handling
   must not queue/restart that already active release.
3. Restore private, licensed build prerequisites in an isolated development tree.
   `Aetherfront-GitHub-Source/SOURCE-PROVENANCE.md` documents Windows x64, .NET SDK
   6.0.428, SDK 8, Visual Studio/MSBuild and .NET Framework targeting packs, KSP
   1.12.5/Unity/embedded Mono, historical first-party baselines and external DLLs.
   Verify exact historical baseline hashes, not filenames. A source clone alone
   cannot reproduce the shipping client or prove deployed-server equivalence.
4. Build and run the release-specific regression and real KSP/runtime tests
   **outside this publisher**, against the current authenticated baseline. Preserve
   evidence and review the produced files. The RC28 recipe reads local deployment
   state and uses a local signer for its candidate preview; it is not a portable,
   keyless builder. Adapt an isolated build/package pipeline before relying on
   another PC. Never solve that dependency by copying hosting private keys or by
   running tests against production. Do not claim unexecuted tests passed.
5. Confirm host setup separately: current compatible bridge and fresh authoritative
   player status, bot's native `deploymentRoot`, existing signer/update tools,
   TLS trust, publication helper, and configured GitHub publication access. Keep
   machine paths and operational configuration private. Source export excludes
   those prerequisites. Ensure reviewed new source and the channel are included
   in the intended Git workflow; an export allowlist does not automatically include
   every new root-level file.

## Bundle format and server updates

Use the exact local filename `Aetherfront-Release-kesas-rcN.zip`. The publisher
opens it read-only, rejects network shares/reparse paths, enforces the 512 MiB
limit, and reuses `scripts/Verify-KesasRelease.ps1` before contacting GitHub.

Native ZIP layout: root `RELEASE.json` containing `version` and `files` records
(`path`, `sha256`), plus all three required root files:

- `ClientTemplate.zip`
- `APFPack-template.json`
- `AetherfrontTechnology.json`

Optional declared `ServerPayload/`, `PackHostPayload/` and
`CertificateToolPayload/` files map to the existing native `Server/`, `PackHost/`
and `CertificateTool/` directories. Every payload file must be declared and hashed.
The RC28 builder currently produces exactly four root files, with no optional
server binaries. A normal client release or starter is **not** a native bundle.

Server/tool updates need a separately reviewed compatible native bundle, including
all three templates even for a server-only change. The existing publisher performs
file replacement/addition, **not deletion, dependency pruning, schema migration or
automatic rollback of external effects**. Do not skip a required incremental
baseline: the channel holds only the latest desired artifact, not a deployment
history. Use a self-contained compatible overlay or an explicit prerequisite gate.
Native versions must increase, including server-only changes.

Never ship live `Config`, `Universe`, `Aetherfront`, `Logs`, `secrets`, bridge
inboxes/status/tokens, save/player state or credentials. The existing native guard
blocks selected top-level state directories, not every possible nested plugin
configuration/state filename. Review optional payload inventories and preserve
the current bridge/runtime/dependency compatibility. Publisher validation is not
a privacy scan, code audit, build proof or native extraction test.

## Publish

From the workspace root, after completing build, KSP tests and review:

```powershell
pwsh -NoProfile -File ./APFDiscordBot/scripts/Publish-GitHubDeployment.ps1 `
  -Archive ./artifacts/Aetherfront-Release-kesas-rc29.zip `
  -Version kesas-rc29 -Notes 'Describe the tested change.' -WhatIf

pwsh -NoProfile -File ./APFDiscordBot/scripts/Publish-GitHubDeployment.ps1 `
  -Archive ./artifacts/Aetherfront-Release-kesas-rc29.zip `
  -Version kesas-rc29 -Notes 'Describe the tested change.'
```

`-WhatIf` and `-DryRun` are strictly offline: local validation/hash and manifest
preview only, no `gh`, authentication, network, uploads or file writes. They do
not check remote channel order or prove a build/KSP test passed.

Real publication pins the existing deploy-branch head, reads the channel at that
commit, and refuses an older version or a changed same-version manifest. It creates
only the separate **prerelease** `host-kesas-rcN`, explicitly `--latest=false`, then
uploads without `--clobber`. Existing assets are never overwritten: exact name,
uploaded state, length and GitHub SHA-256 digest must match. If digest metadata is
absent, an anonymous, credential-free HTTPS stream is hashed with exact-length and
time limits. Redirects are limited to GitHub and the explicit GitHub asset hosts;
no TLS bypass, arbitrary origin or authenticated download fallback is permitted.

Only after asset verification does it conditionally update the channel with the
previous contents SHA. It rechecks branch-head/file identity first and confirms
readback afterward. Identical manifests are idempotent; a concurrency conflict or
uncertain result requires readback/retry, never a force write. GitHub contents SHA
is a file compare-and-swap, not a whole-branch lock; branch protection and consumers'
commit pinning remain necessary. Retained prereleases/assets after a conflict are
safe to retry with the same bytes/version/notes.

`-SkipChannelUpdate` uploads/verifies the host artifact but never changes the
channel. It still checks channel validity/order. It is staging, **not deployment**;
rerun without the switch using identical bytes and notes to request deployment.

The publisher never creates or promotes `kesas-rcN`, changes `pack-media`, or adds
an asset to the native publisher's exact three-asset client release. The host
retains that client-publication workflow. Do not manually mark host prereleases
latest/stable. No upload or successful channel write proves host activation.

## Host consumption and recovery

The bot's fixed channel poll interval is 60 seconds. A new candidate is read from
a pinned Git commit, downloaded as an artifact only, verified and handed to the
normal native update queue with its normal 15-minute scheduling policy. Polling
does not authorize source execution, arbitrary builds, immediate restart or force.
The already active baseline is not queued. Busy/failed/cancelled/crash-held work
must not be silently replaced or rearmed by polling.

Preserve `.github-prepared`, backups and `.update-transaction.json` on native
failure. In `github-completion`, the native release may already be active although
publication returned an error; retry the same archive and notes only through the
reviewed recovery path. Do not delete journals or automatically roll back/promote
another version. Verify host activation, listener ownership, preservation and
client-release completion independently.
