# RC28 validation status

The real RC27 attempt never relaunched KSP. Its already-loaded RC26 helper
committed the client/catalog, then rejected seven generated settings files and
one legitimately rewritten CLS settings file during final pack verification.
The pack rolled back, leaving client/catalog RC27 with pack/profile RC26.
Those logs are not evidence of a completed RC27 game launch.

RC28 recognizes exactly eight additional mod-owned settings paths. Their writer
methods were checked in the authenticated shipped assemblies. Preflight and
final package verification now use the same content scan; unchanged completed
reconnects still use their authenticated fast path. Required configuration,
assemblies, models and textures remain checked.

New helpers preserve settings after KSP exits and its executable is locked, so
shutdown saves are not replaced by an earlier preparation snapshot. Existing
RC26/RC27 helpers cannot switch their loaded code during self-update. Eight
version-only package repairs therefore let them complete final verification;
the new early-startup hook restores settings from authenticated replacement
backups. Archive bytes, URLs and sizes are unchanged and cached downloads can
be reused. The HETTN repair27 migration and canonical directory casing remain.

Recovery verifies the completed receipt, signed catalog/code, consumed request,
newest authenticated recovery journal, original backup contents and signed
archive defaults before writing. A durable authenticated progress record permits
interrupted recovery to resume. Completed recovery does not overwrite later
user edits. This protects ordinary updates and interruptions; it is not a
rollback-proof defense against wholesale deletion/replay of local history.

Two defects were caught before publication: directory sharing prevented atomic
progress-file moves, and an exiting unrelated KSP process could invalidate a
process-module lookup. Both were corrected. An unreadable live KSP process still
blocks installation. The retired candidates were not deployed.

Final automated evidence:

- 186 native assertions and 368 casing/HETTN assertions pass on Windows CLR
  and KSP's actual Mono runtime.
- 724 settings/migration assertions pass across twelve runs: new-helper rules,
  the old-helper negative control, RC26 and mixed-RC27 migrations, and fresh
  RC26/RC27 starters on both helper runtimes. All eight settings are preserved
  in one explicit restart, with no repeated rollback.
- 420 assertions pass across twelve real interruption/resume scenarios at
  plan-save, first-file-write and first-file-record checkpoints.
- A separate replay executes the new startup recovery itself under actual KSP
  Mono: 16 assertions verify eight restored settings, completion authentication,
  scanner integrity, repeat no-op, later-edit preservation and unchanged backups.
- Actual addon attributes and read paths confirm recovery precedes all eight
  settings loads. kOS has Instantly addons, but its reads are deferred to Start;
  the recovery runs synchronously in the canonical Instantly Awake hook.
- Core bytes are unchanged from signed RC27; 6,326 client methods are preserved.
  No new media or physics/content changes are included.

These are headless/runtime and static checks, not a completed Unity gameplay
test. Real startup, facility access and Regular/Potato switching still require
the local client test. Inherited optional-integration/schema/texture warnings
remain separate; no entirely error-free KSP log is claimed.

Post-publication verification passed: the running RC28 game and pinned HTTPS
service own their expected ports, the bridge is fresh, and all three anonymous
GitHub release downloads match the deployed bytes. All 464 signed files and six
prepared files match the reviewed candidate. Nine protected server files and
the vanilla server process are unchanged. Existing media and historical release
tags remain available.

# RC27 validation status

The real RC26 client test confirmed one approved restart, no external installer,
complete client/catalog/pack/profile receipts, no second installation round,
zero ModuleManager patch errors, no B9/TweakScale/Harmony loading failures, and
a successful server connection. It also exposed a different content regression:
four prepared package directories were renamed using lowercase comparison keys.
Windows still found their files, but KSP's model URLs did not. The test stopped
after 574 part-compilation failures; facility and profile-switch tests were skipped.

RC27 keeps comparison identities case-insensitive while installing directories
with their signed canonical spelling. Already-loaded old helpers still perform
the initial update, so the new canonical client synchronously restores folder
spelling during its early startup hook. Cached feed signatures, code/catalog
hashes and authenticated completion receipts are checked first. Case changes
use a recoverable authenticated journal, reject reparse points/collisions, and
briefly retry Windows sharing/access-denied rename failures without changing
permissions or overwriting an existing target.

The actual active KSPCommunityFixes FastLoader and compatibility-patch binaries
were inspected, not only vanilla KSP: the new asset UrlDir is built after this
hook and before model/texture enumeration. Their hashes match the real client's
pack state. No live URL-tree mutation or blanket case-insensitive lookup is used.

HideEmptyTechTreeNodes also requires an empty Resources directory that its own
writer does not create. RC27 initializes it and recognizes only the two generated
HETTN.TechTree and HETTN.TechTree.tmp files as mutable. One version-only package
repair lets old helpers safely replace already-generated copies; its archive
bytes, URL and size remain unchanged. Other package declarations are preserved.

Final local evidence:

- 93 native checks pass on Windows CLR and 93 on KSP's actual embedded Mono.
- On each runtime, 143 new casing/recovery checks, 17 original-engine negative
  controls and 24 signed old-helper one-restart migration checks pass. The latter
  includes the tech-tree cache replacement and a clean scan after regeneration.
- All 592 recorded missing-model occurrences (469 distinct URLs) resolve with
  ordinal spelling in the four authenticated archives. Payload prefix casing
  matches all 219 profile archive variants, covering 32,485 payload files and
  212 canonical roots across 211 Regular and 177 Potato packages. The four
  affected archive hashes were recomputed; the broader check reads inventories,
  not all archive payloads. The native publisher separately verifies media hashes.
- Core bytes are unchanged from signed RC26. Only the client version field,
  version getter and canonical early-startup method change; 6,325 other client
  methods are preserved by independent bytecode review.

These are headless and static proofs, not a completed Unity gameplay test.
Real RC27 startup, facility access and Regular-to-Potato switching still require
the local client test. No entirely error-free KSP log is claimed: inherited
optional-integration/schema/texture warnings are separate from the repaired
model failures.

Post-publication checks passed: native RC27 activation, fresh server bridge,
owned game/HTTPS listeners and pinned HTTPS health. All three public assets
were downloaded anonymously with normal TLS validation and matched the native
prepared files byte-for-byte. The starter also contains exactly four reviewed
public connection/trust settings beyond the 464 signed template files. All 228
existing media assets and six historical release tags remain present. Nine
protected deployment files and the running vanilla server are unchanged.

# RC26 validation status

The RC25 checks below did not cover all files generated by a real game launch.
Two subsequently captured installation attempts rolled back during final
verification. The client/catalog had advanced, but the effective pack remained
RC24. That left its invalid Potato `FOR` insert nodes and B9 selector failures
active. Of the 112 observed cases, 28 reference removed Waterfall renderers;
84 OPT cases reference RCS modules whose effect-name identity was cleared by
the Potato policy. Removing Waterfall references alone does not fix the latter.
A clean simulated installation was not evidence that this
existing-install migration worked.

RC26 retains RCS/engine effect-name fields and named effect groups, emptying
their particle/audio definitions instead. Physics selectors and distinct RCS/OMS
identifiers are preserved. The audited KSP effect implementation creates no
components or callbacks for these empty groups. This is not a flight test.

RC26 also corrects the observed runtime-file classifications, skips those files
before opening/hashing them during scans, and retains verification of required
configuration, assemblies and content. Archive extraction/preparation continues
authenticating every file, including runtime defaults supplied in an archive.

RC26 validation evidence:

- The unchanged RC25 helper reproduces all four observed package-verification
  mismatches and the resulting rollback: four assertions passed.
- The same old helper successfully applies four version-only repair replacements
  with unchanged archive hashes, commits, and performs one approved GameStub
  restart: ten migration assertions passed. This tests the loaded old updater,
  not just the new code after installation.
- Final RC26 binaries pass 90 native checks on Windows CLR and 90 on the actual
  KSP embedded Mono runtime. These include generated logs/caches, an exclusively
  locked raw texture cache, required CFG and added-DLL negative controls,
  corruption/rollback, execution locks, permanent approved extra-mod removal,
  recoverable replacements, and rejection of an empty child junction before
  recursive traversal. The final candidate was rerun after independent review
  identified that directory-walk edge case.
  The extended checks additionally authenticate flat GameData file packages,
  detect changed root DLL bytes, and distinguish genuinely missing file packages.
- The targeted core graft changes one scanner call site; 689 other core methods
  are preserved. The client graft changes only its two version constants;
  6,326 unrelated client methods are preserved.
- Five additive asset archives pass preservation checks for 2,344 original
  entries and their ZIP metadata. Seven existing-texture aliases and eight
  precise static-renderer bindings are added; other entries, physics content,
  unrelated package declarations, and the shared Full/Potato variants remain
  unchanged. Six negative asset-stage tests pass. This is not a Unity rendering
  test.
- Real ModuleManager 4.2.3 replay loads configurations from 211 Regular or 177
  Potato package archives, retaining the affected parts and their generated
  copies while applying the pack's patch rules. Both profiles cover all 112
  recorded switch cases with zero patch errors/exceptions/warnings. Every
  retained B9 selector has exactly one target. Potato preserves 131 non-renderer
  modifiers across 113 replayed parts. A separate postpatch audit confirms 32
  RCS/engine modules unchanged and 63 named effect groups empty, with their names
  preserved. This replay does not instantiate Unity prefabs.

- The unchanged original-policy negative control reproduces exactly 28 missing
  Waterfall targets, 84 RCS identity mismatches, and both insert-node errors.
  The complete real-MM/B9 replay passes 49,796 assertions across all three runs.
- The new 623-byte Potato policy archive has a hash-bound, independently checked
  plan. It replaces only the existing policy file; Regular receives no Potato
  policy. Another 211 pipeline boundary checks pass. All six new media assets
  are publicly downloadable with verified bytes; no active release is changed
  by publishing those immutable media files alone.

The final signed candidate completes the full 177-package dirty-install migration
on the unchanged loaded RC25 helper: ten archives, fifteen worker assertions,
one explicitly approved GameStub restart, and a completed RC26 client/pack/profile
receipt. Regenerated exclusively locked caches are accepted; an added PluginData
DLL remains detectable. The reconnect test stages the real connection metadata,
accepts the completed same-profile receipt without a full scan, and rejects the
opposite profile. The full-catalog Potato-to-Regular native transaction also
completed: 211 packages, 42 prepared replacements, one policy-root deletion,
one explicit restart, matching completion receipt and fast reconnect. Regenerated
locked runtime files remain accepted while the added-DLL negative is detected.
The optional reverse-cycle fixture was retired before completion after the
user requested immediate wrap-up; that final RC26 reverse cycle is not claimed
as passed. Real Unity startup, rendering, facility access and gameplay remain
local-client tests. No error-free end-user KSP log is claimed.
The first complete-catalog fixture run failed because its directory-only clone
omitted five flat GameData files, including ModuleManager. This was a test setup
failure, not accepted release evidence. The clone now copies and authenticates
those files and checks all required installed paths before launching the helper;
the exact signed candidate subsequently passed that migration without changing
product binaries. Its completion proof is preserved separately while profile
tests continue. An earlier reconnect fixture omitted the connection coordinator's
staged metadata and therefore exercised a full fallback scan; the corrected test
now supplies those authenticated inputs and checks the actual receipt predicate.

Known residual diagnostics from the captured pre-repair log include upstream
model texture inputs without a verified source replacement, a TGA loader
failure, and unverified Kerbal Konstructs collider/facility and laser-animation
warnings. Most static-model missing-texture messages have explicit deferred
material bindings; that establishes replacement intent, not correct rendering
of every model. New game logs and screenshots are required to close these items.

## Historical RC25 verification

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

- Sixty-eight native installation/rollback checks passed on Windows CLR and the
  actual KSP Mono runtime with the starter's unchanged runtime DLLs. Coverage
  includes prepared-file corruption, execution locks, permanent approved mod
  removal, recovery of replacements, approved duplicate-client disable and
  narrow generated-settings/cache exceptions.
- Thirteen actual Windows attribute-only reparse probes passed on .NET 8.
  Sharing flags alone do not prevent all retagging: the empty-directory probe
  changed the held handle's attributes, and the updated cache rejected reuse.
  Nonempty-directory retagging was rejected by Windows. The cache rechecks all
  held ancestor handles; normal path mutation releases the leases and performs
  the original full path safety checks before rename. File hashes are retained.
- Seventy-one installer harness checks and thirteen combined NativeRun commit
  assertions passed on each runtime. A synthetic RC25-to-RC26 client/pack bundle
  committed client files, prepared packages, duplicate repair and a rich receipt
  before explicit GameStub restart. Invalid metadata/receipt cases fall back
  without treating an incomplete install as current.
- The production Windows PowerShell helper passed without any desktop UI
  payload. It loaded shadow dependencies, authenticated/applied a prepared pack,
  published readiness/completion and released executable locks.
- Full-size preflight took 10 minutes 52 seconds on this host, exceeding the
  candidate's ten-minute readiness limit. The final core changes only that
  limit to thirty minutes; 689 other methods and all other instructions in the
  launcher are unchanged. Integration tests resolved 951 references on each
  runtime. No elapsed thirty-minute timeout test is claimed. The profile cycle
  uses the earlier core's identical content-application logic; final-core native
  checks and the production hidden helper were rerun separately.
- The compatibility media suite passed 3,323 checks. Four new immutable assets
  were downloaded anonymously from GitHub and matched their declared hashes.
- The first RC25 publication stopped before native activation because the
  bridge briefly held its status file exclusively. The same signed prepared
  release and all three matching draft assets were retained; RC24 stayed up.
  Publisher status reads now retry only Windows sharing/locking conflicts for
  a bounded interval. Twelve regression checks passed in Windows PowerShell
  5.1 and PowerShell 7, including persistent-lock rejection, malformed/oversized
  data, stale/future timestamps, nonzero counts and wrong server PID. No player
  or freshness guard was bypassed to resume publication.
- Full-size Potato-to-Regular-to-Potato execution completed on 2026-10-17 at
  10:18 UTC, with thirteen end-to-end assertions passing. The fresh 177-package
  Potato commit, Regular's 211-package profile with 42 replacements and two
  removals, and the return to Potato with eight replacements and 35 removals
  completed native verification. Matching reconnect receipts, changed-profile
  rejection, same-commit extra-mod removal, saves, identity and controls passed.
  The original fresh preparation had completed before its read-only preflight
  was stopped to improve redundant path checks. The continuation reused those
  authenticated prepared bytes; it did not repeat that initial extraction.
  Regular and return-to-Potato preparation ran in the continuation itself.

Tests using the actual KSP
Mono runtime are runtime/API tests, not a Unity scene, rendered game, B9 fuel
selection or real remote player connection. No clean post-update KSP.log is
claimed before that game has actually been run.

The inspected pre-update log also contained a stock `SpaceCenter.Start()` null
reference and upstream missing-model/texture warnings. Their causes were not
established independently. They must be rechecked in a new game log after the
dependency and compatibility fixes; they are not represented as proven fixes
or suppressed to manufacture a clean log.

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
