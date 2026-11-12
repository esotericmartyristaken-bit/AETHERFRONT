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

Deployment speed defaults: use the fixed v3 approval above, not new UAC wrappers,
remote relays or manual service stops. Publish once with the exact tested clean
source commit and archive hash. Do not repeat complete fixture suites or build
unchanged clients during publication; run affected tests while preparing source.
The transient deployment process promotes inherited Idle/BelowNormal scheduling
to Normal and restores it on exit. No task settings or production priorities change.

Reviewed-source freezing uses two batched Git calls with immutable file handles,
Git attribute-aware blob comparison and copied-byte SHA256 verification. PowerShell
7 uses managed JSON/path checks; Windows PowerShell retains the legacy checks.
There is no persistent hash cache. Repeated GitHub hash/scan results are reused
only within a scope holding files against mutation. Existing content-addressed
media retain full SHA256 and remote digest verification, without inflating the
same public ZIPs each release; missing/new remote media are safety-scanned before
upload. The real isolated candidate still boots and passes signature/health
checks before player admission and any production stop. Its redundant preliminary
firewall probe is removed, not the candidate's actual firewall isolation.

Keep the 15-minute warning when players are online; that intentional wait cannot
fit inside a total 15-minute deadline. Log phase timings, follow fresh receipts,
and investigate a stalled phase instead of repeatedly submitting deployments.
Pipeline-only verification uses `-Action verify`; it does not execute the actual
candidate pipeline or benchmark a production deployment. Never restart a healthy
game solely to measure publisher performance.

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
