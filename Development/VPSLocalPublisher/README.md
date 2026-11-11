# VPS-local guarded publisher

One reviewed UAC installs the fixed SYSTEM scheduled task. Subsequent releases
are approved from the VPS operator's normal interactive session using
`C:\ProgramData\Aetherfront-VPSLocalPublisher\Tools-v3\Approve-LocalRelease.ps1`
with a staged ZIP, exact source commit, version and explicit `-Approve`.
`-Action verify` exercises the authorization, encrypted credentials, private
archive validation and clean source fetch without activating production.

The fixed task polls once per minute and can also be started by the operator.
It exposes no network listener or arbitrary command interface. An Ed25519
approval binds host, nonce, action, version, archive hash, source commit, encrypted
credential hash and ten-minute expiry. Source is fetched into a private clean
checkout. The ordinary native publisher retains its player warning, candidate
checks, rollback backup, signatures and final health checks.

The fresh approval key is CurrentUser-DPAPI protected and readable only by actual
interactive Windows logon SIDs discovered by SYSTEM. The limited session-zero
Discord bot does not receive it despite using the same account SID. Signing keys
and machine-DPAPI credentials remain on the VPS. Current GitHub credentials can
be refreshed per release using a signed RSA-OAEP encrypted envelope, without UAC.

Jobs have durable nonce claims and readable sanitized status/evidence. An OS-held
lock releases automatically if the worker exits. A failed job does not globally
disable later signed requests, but native ambiguous activation journals still
block unsafe publication. The worker never kills an applying publisher for a
timeout or output limit. Successful duplicate archives are removed; failed
private archives and recovery evidence are preserved.

Setup preserves existing legacy state/keys and records original execution ACLs.
No game restart occurs during commissioning. It requires the existing protected
SYSTEM game task and limited S4U bot. Administrative changes to task/runner
code or pinned host configuration remain administrative operations; this is
release publication authority, not unrestricted remote administration.

Validation: `node --test Development/VPSLocalPublisher/test/publisher.test.mjs`.
Build a code-only commissioning archive with `Build-SetupBundle.ps1`; run its
plan on the VPS before the single `Start-Commissioning.ps1` consent request.

## GitHub submission and receipts

On the controller, run `Initialize-GitHubReleaseSigner.ps1` once. Transfer only
the printed public key file and verify its SHA-256 on the VPS. In the elevated
VPS operator session, run the reviewed `Install-GitHubReleaseTransport.ps1`
with that public key and `-ExpectedPublicKeySha256`. It extends the already
installed v3 task and keeps a protected backup of the runner. Installation does
not trigger a release. The task uses its existing host credentials.

Submit a `verify` job with `Submit-GitHubRelease.ps1 -Archive ... -Version ...
-SourceCommit ... -Action verify`. Use the returned request ID with
`Get-GitHubReleaseReceipt.ps1`. A `verified` receipt exercises the VPS request,
archive and source checks without activation. Submit a fresh `publish` job for
an authorized release and confirm a `healthy` receipt against the pinned HTTPS
health and release feed. GitHub receipts are visible to authorized repository
users and carry status and a bounded failure code, with full diagnostics
retained in the protected VPS status/evidence directories.

The transport uses only the pinned private draft, host-bound signatures and a
durable request ID. It retries receipt delivery without replaying the release.
Remote releases must preserve the reviewed deployment and bot source trees.
Changing that protected code or rotating the controller key requires host
maintenance. The ordinary VPS-local operator path remains available.
