# RC22 verification

## Completed checks

- 394 GitHub redirect/TLS policy checks on Windows CLR and the exact KSP Mono runtime.
- 41 transfer, cache, locking and ETA checks on each runtime.
- 29 native installer/post-exit checks, including both profile switches and preservation.
- 916 runtime calls audited against the KSP libraries; no unconditional unsupported API found. The RC18 negative control reproduced its unsupported API.
- Direct signed upgrades from RC13 through RC21, damaged RC13 and Potato RC16: all 462 files and preserved player data verified. These are helper application tests, not proof every historical Connect implementation can repair itself.
- 86 offline publisher checks; 23 recovery/gating checks in each PowerShell runtime.
- Seven bot deployment/warning checks, including unknown counts and failed warning delivery.
- 217 immutable media assets uploaded; remote SHA256 digests and sizes matched.
- A separate 1 MiB buffered sequential pass verified all 11,899,789,172 local media bytes against the catalog in 41.405 seconds. This is a local hashing measurement, not download bandwidth.
- Real GitHub CDN range recovery fetched 8 MiB and reconstructed a SHA256-verified 408,428,372-byte archive from deliberately damaged seeded cache. This was not a fresh-install bandwidth benchmark.
- Actual KSP 1.12.5/Unity 2019.4.18f1 verified GitHub TLS/hash, pinned-origin signed metadata, tampered-signature rejection and shipped ZIP/deflate APIs.

The operator reset helper separately passed four positive, 38 negative and 12
static/integrity checks in each PowerShell runtime. That private helper and its
home-computer paths are deliberately excluded from this repository.

## Live end-to-end checks

RC22 activated through the normal deployment workflow. Anonymous public downloads
of all three release assets matched their native sizes and SHA256 hashes; the
signed 462-file inventory and all 217 media assets also matched.

An unmodified RC21 Connect implementation fetched the live signed RC22 update,
applied it with the native helper and preserved the test save, player identity,
Potato selection and partial cache. Historical direct-upgrade results above are
not substitutes for this separate network migration test.

A fresh Potato download fetched all 176 archives from GitHub without origin
archive fallback. Intentional cancellation preserved the installed client;
32,848,512 retained bytes were subsequently reused through HTTP Range requests.
The native helper completed installation, and an independent check verified all
28,674 installed files (11,211,717,503 bytes), package inventories and profile
roots. Fresh-install preservation and archive hashes passed.

Regular/Potato switching and complete clean-reconnect verification are still
running. Results will be recorded after execution.

## Explicit limits and retained failures

Headless profile-chain tests use shipped binaries and the real post-exit helper,
but a test executable replaces KSP. They do not prove rendering, UI appearance,
multiplayer gameplay or end-user download speed.

The helper hashes all managed content after installation; the independent test
harness also checks every installed file. Production fast reconnect hashes
code/configuration files and checks other assets' presence, not their full
content. Do not describe it as an all-asset rehash on every Connect.

An initial real-Unity smoke process exceeded its fixed 110-second guard after
an early loading-time quit request. The failure remains retained, not relabeled
as a pass. A diagnostic rerun exited normally in 44.508 seconds; shipped cleanup
returned in 47 ms. A representative main-menu attempt never reached that state
before the unchanged memory guard stopped it. No shipping cleanup deadlock was
reproduced; representative main-menu/updater shutdown remains unverified.

The first publisher preparation was deliberately stopped before activation
after small-read verification proved slow on this host. Exact signed prepared
bytes were retained. Full-file SHA256 verification now uses larger sequential
buffers; no hash or signature check was removed. A later GitHub create succeeded
while immediate list readback lagged; preparation failed safely with native RC21
still running. Bounded metadata-only retries were added and regression-tested.
No uncertain write is blindly repeated.

Completion recovery separately reproduced an older publication being promoted
again over a newer latest release. The final hook now checks published/latest
identity before writing, returns completed publications without writes, rejects
newer/conflicting channels and uses bounded read-only retries after one PATCH.
Twenty-one added regressions passed in both PowerShell runtimes. Real GitHub
latest metadata parsing also passed without publication writes. GitHub read/PATCH
is not atomic against independent publishers; use one coordinated publisher.

One direct-upgrade test initially exceeded its 60-second test-helper bound under
host load, although the helper completed successfully. The remaining matrix
passed with a five-minute owned-helper bound. Production timeout settings were
not changed.

All real KSP processes were isolated, owned and resource-guarded. No graphical
KSP test is launched on the shared production host. Final interactive testing
uses the user's separately authorized vanilla test installation.
