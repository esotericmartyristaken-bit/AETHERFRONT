# RC25 verification in progress

RC25 replaces the RC24 desktop installation window with in-game staging and an
explicit Restart & apply button. A hidden post-exit helper remains necessary to
replace loaded assemblies. A client already running RC24 or earlier must still
use its loaded legacy updater for its initial migration; the new flow begins
once RC25 is loaded. Combined updates plan client, pack and removals before one
restart. Reconnect receipts are accepted only for completed matching revisions
and profiles, never incomplete, failed or changed-profile installations.

Runtime settings have a narrow mutable-file policy. Assemblies, part
definitions and textures remain verified. The final installation verification
does not use the connection-only receipt fast path. The server manifest keeps
empty managedPackages and its client-only allowances so it does not impose
Regular graphical content on Potato.

Candidate execution evidence:

- Sixty-one native installation/rollback checks passed on Windows CLR and the
  actual KSP Mono runtime with the starter's unchanged runtime DLLs. Coverage
  includes prepared-file corruption, execution locks, permanent approved mod
  removal, recovery of replacements, approved duplicate-client disable and
  narrow generated-settings/cache exceptions.
- Seventy-one installer harness checks and thirteen combined NativeRun commit
  assertions passed on each runtime. A synthetic RC25-to-RC26 client/pack bundle
  committed client files, prepared packages, duplicate repair and a rich receipt
  before explicit GameStub restart. Invalid metadata/receipt cases fall back
  without treating an incomplete install as current.
- The production Windows PowerShell helper passed without any desktop UI
  payload. It loaded shadow dependencies, authenticated/applied a prepared pack,
  published readiness/completion and released executable locks.
- The compatibility media suite passed 3,323 checks. Four new immutable assets
  were downloaded anonymously from GitHub and matched their declared hashes.
- Full-size fresh Potato-to-Regular-to-Potato execution remains in progress.

Tests using the actual KSP
Mono runtime are runtime/API tests, not a Unity scene, rendered game, B9 fuel
selection or real remote player connection. No clean post-update KSP.log is
claimed before that game has actually been run.

## Historical RC24 verification

RC24 keeps all RC23 Regular and Potato package declarations. Native installation
now has a visible, responsive progress window and a success-only Restart KSP
button. Neither pack nor client helper restarts automatically. Both executable
names are locked during application/verification and until completion is
acknowledged. Extra-mod removal still requires approval; approved removed mods
are permanently deleted without a content backup. Replaced managed files and
state retain failure recovery.

Current execution evidence:

- Forty-six native checks passed, including actual CreateProcess rejection
  for both executable names while locked, explicit restart, authenticated apply,
  removed-mod backup absence, replacement-file recovery and player preservation.
  Edge cases cover replacement/removal collisions, bare deletion paths,
  overlapping completion ownership and forced final-scan failure. Failed-scan
  rollback restored managed content/state without restoring permanently removed
  extra mods.
- Thirty-one hidden STA completion-window checks passed. These cover close
  blocking, success-only explicit restart, failure details and rollback without a
  restart action. A separate rendered preview was visually inspected.
- The production Windows PowerShell shadow loader passed with the desktop UI
  loaded from its `.bin` payload, authenticated pack application, a final helper
  completion marker and released execution locks.
- All 394 GitHub/redirect checks passed on Windows CLR and the exact KSP Mono
  runtime. All 41 transfer checks passed on each runtime. The first CLR transfer
  attempt failed its observed-parallelism assertion; the exact unchanged
  executable passed on rerun. The original failed log is retained; its cause was
  not independently established and is not relabeled as a pass.
- Twenty-eight actual visible-window integration checks passed in 5.12 seconds:
  the real form started the native worker, rejected closing while busy, completed
  verification without automatically launching a process, and its explicit
  restart button launched the owned GameStub. This is not Unity gameplay testing.
- Fourteen helper-application baselines passed: RC13 through RC24, damaged RC13
  and Potato RC16. Every run verified all 463 signed files and preservation of
  save, identity, selected server, profile and profile preference. These are
  direct helper-application tests, not proof every historical Connect repairs
  itself. RC24's own helper ran headlessly and did not auto-restart.
- RC24 activated at 2026-10-17 06:51:40 UTC through the existing 15-minute
  workflow's empty-server gate. Native signature/file verification, bridge and
  listener ownership, pinned HTTPS health and protected configuration hashes
  passed. Vanilla and the bot retained their original processes.
- Anonymous downloads of all three RC24 release assets matched native bytes;
  the 463-file signature/inventory and all 218 media assets verified. The public
  starter SHA256 is
  `5d42c4df07e82425e0677cd0a48fd0ea28fe733f7f8d873e5339d16d01b87b7a`.
- The actual old RC23 Connect implementation fetched the signed RC24 client
  through GitHub and applied it with its existing native helper. All 463 installed
  hashes matched. Save, identity, Potato selection and partial cache survived.
  This network check covers client migration, not a fresh full-modpack install.
- No full Unity gameplay session or RC24 full-profile reconnect is claimed.

The patcher verified 6,311 unrelated client methods and 684 unrelated core
methods unchanged. The native guard hooks are active only inside the post-exit
helper. Final helper completion is recorded separately from the core transaction
log, after graphics/profile writes succeed.

## RC23 verification

RC23 changes Potato's Kcalbeloh texture declaration to the unmodified official
`v1.1.8` 4K archive. Regular's 210 package declarations and the other 175 Potato
packages are unchanged. Installer/core DLLs are byte-identical to RC22; the client
patch changes only its version constant and one version-returning method, with
6,312 unrelated methods preserved.

## Official archive comparison

- Official 4K archive: 696,453,837 bytes; SHA256
  `57beb1c4342582da0ef44a9f838f24103583028f4c5eb2fa6ba33f5ce541c2b1`.
- Both variants contain the same 227 file paths. All 42 height/biome maps match
  byte-for-byte. There are 179 identical files and 48 differing visual textures.
- Expanded textures: 1,251,654,435 bytes for 4K versus 2,668,739,659 bytes for 8K.
- Fresh Potato archive total falls from 5,381,231,200 to 4,401,551,360 bytes.
- The official archive retains its original bytes and `GameData/` prefix. Its
  explicit destination/strip-prefix mapping installs one `KcalbelohTextures` root.
- GitHub media preparation verified 218 immutable assets. The 217 prior assets
  were retained; only the new 4K asset was uploaded.

## RC23 execution evidence

The production native helper passed an isolated 8K seed, 8K-to-4K, 4K-to-8K and
repeat 8K-to-4K sequence. Each phase independently verified all 227 installed
content hashes, removal of obsolete package state, absence of leftover files,
missing-file detection and preservation of unrelated data. The repeated final
phase used an explicitly re-established preservation checkpoint; the first three
phase proofs were retained. Final positive result: 2026-10-17 06:08:02 UTC.

RC23 activated and passed native readiness and anonymous GitHub verification:
all three release assets matched native bytes, all 462 signed files verified and
all 218 media assets matched. Vanilla and the bot were unchanged. The separate
RC22 Connect test upgraded its client to RC23 and downloaded the official 4K
archive, but timed out fetching the final pinned-origin pack metadata before
application. That run is retained as a failure, not an installation pass. These
tests use owned headless fixtures, not a real gameplay session. Full-profile
rendering and mixed-profile landings are not claimed.

An initial isolated-fixture test correctly failed because it declared the entire
modpack while staging only one texture package. The corrected focused fixture
declares only that texture plus an unrelated sentinel rule. No shipping
validation was disabled or bypassed. Early build attempts also exposed a missing
local Cecil resolver path, a disallowed additional metadata filename, and an
export-directory restriction; those unpublished attempts were retained and the
recipes corrected before publication.

An initially repeated fixture operation reused dummy process ID 0 and produced
the same authorization as an earlier operation. The helper correctly rejected
the already-consumed authorization. The final continuation used a fresh signed
fixture argument; no receipt was deleted and replay protection was not bypassed.

## RC22 baseline verification

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

The remaining live headless chain completed successfully at 2026-10-17 04:57 UTC:

- Potato to Regular fetched exactly 41 new GitHub archives (6,518,557,972 bytes), without origin archive fallback. The native helper completed; independent checks verified all 210 packages, 31,282 files and 19,279,193,750 expanded bytes.
- Regular to Potato reused all seven cached policy archives with zero archive HTTP requests. The native helper removed 35 Regular-only roots; independent checks verified all 176 packages, 28,674 files and 11,211,717,503 expanded bytes. No excluded graphical roots remained.
- Clean reconnects in both profiles, including the final Potato reconnect, requested no archive download or restart. State, settings, saved selection, identity, test save and verified cache remained unchanged.
- Shared package state/content, profile graphics policy/restoration, all original evidence and the final matching signed GitHub/native RC22 feeds passed validation. The continuation process exited successfully.

## Explicit limits and retained failures

Headless profile-chain tests use shipped binaries and the real post-exit helper,
but a test executable replaces KSP. They do not prove rendering, UI appearance,
multiplayer gameplay or end-user download speed.

Initial continuation attempts rejected completed transaction markers, stale
constant-field metadata and an overly long generated evidence filename. Test-only
guards/names were corrected against the authenticated completed transaction and
exact shipped method bytes. Shipping binaries were not changed; failed attempts
and original cancellation/fresh-install evidence remain retained.

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

The fixed three-second HTTPS health diagnostic occasionally timed out during
the extended test run. Identical pinned retries and subsequent full service
checks passed with unchanged processes/listeners/configuration. These misses
remain recorded; their cause was not established and no production timeout was
raised to hide them.

All real KSP processes were isolated, owned and resource-guarded. No graphical
KSP test is launched on the shared production host. Final interactive testing
uses the user's separately authorized vanilla test installation.
