# RC35 local candidate

Production remains RC34 until the local release gates below have direct evidence.
Do not patch user installations, reuse production identities/keys in fixtures, or
publish an untested candidate. Reuse work/apf-test; a second client is authorized
only for this explicitly requested multiplayer test.

## Required behavior and gates

* Persist each authenticated account's universe time, scoped to the active save
  namespace. Offline time and another account's warp must not advance it. Cover
  disconnect, crash/checkpoint recovery, server restart, skipped releases, corrupt
  state, unknown subspaces, bounds, and duplicate sessions.
* Maintain a single simulation authority per vessel. Faction members can take
  control; ungranted outsiders cannot. USI Life Support, background resources,
  DeepFreeze, logistics, loaded peers and unloaded vessels need individual review.
  Control transfer must preserve remaining supplies and elapsed survival timers,
  including between players with different clocks. Merely blocking network writes
  does not stop local simulation.
* Other ships in tracking must be bounded lightweight markers, not fully loaded
  KSP vessels or ProtoVessels. Easy player/faction filters, no fly button or control
  bypass, and full data only for authorized nearby vessels. Measure thousands of
  records, per-frame work, allocations, bandwidth and scene changes. The user
  explicitly accepts retaining current restricted visibility if a performant
  marker implementation cannot be verified.
* Add KerbalFX (all released modules), BetterBurnTime, Conformal Decals, Firefly,
  Camera Tools Continued, Neptune Camera, Planetside Exploration Technologies,
  Tarsier Space Technology. Verify KIS/KAS. Regular gets new visual effects;
  Potato remains light. Keep shared part definitions compatible between profiles.
* Player flags and visible craft decals must reach peers, including late join,
  reconnect and nearby-vessel rendering; bound/validate transferred image data.
* Export science/technology definitions from the final loaded pack and update the
  authoritative catalogs for new experiments. Sandbox stays production mode.
* Inspect fresh KSP/Player/ModuleManager/updater logs, distinguish inherited
  warnings, and test full startup, gameplay, profiles, reconnect and signed apply.
* Run local multi-client tests first, preserve compact sanitized evidence, then
  publish through the guarded control-7 deployment path and verify live readback.

## Implementation notes

The historical source does not reproduce the deployed hardened runtime. Preserve
fingerprinted RC34 client and RC33 server images and audit each changed method.
Do not replace their safety checks with older source implementations. Personal
clocks must use the authenticated account, never a user-supplied display name.
First migration has no historical personal-clock record: preserve that limitation
explicitly rather than claiming pre-feature offline recovery.

ClockStore's runtime integration is staged locally, with authenticated server
tests and actual two-client offline/warp evidence. Signed installation and the
remaining gameplay scenarios are still release gates. Its records use hashed
account IDs, bounded invariant values, a corruption digest,
durable temporary writes and atomic replacement. Corrupt records fail closed.
The digest detects accidental damage; it is not a MAC or a trust boundary against
an administrator who can modify server state.

## Local evidence, 2026-10-22

See controller `tests/RC35-CURRENT-STATE.json` for current fixture processes and
pending checks. Evidence is local and sanitized before any distribution.

* Normal two-client Tracking Station faction handoffs, offline peer warp,
  unchanged authenticated personal-time restore and flag synchronization have
  measured evidence under `tests/rc35-resource-live`.
* Normal installed-code EVA creation preserves the parent's virtual epoch and
  schema2 launch anchor. Local and server persisted clocks matched the expected
  inherited offset with zero measured error. Normal boarding succeeded. This is
  not a signed installer acceptance test.
* The previous `normal-gameplay-exceptions.json` scan missed module-startup
  exception headings. Its scope correction is stored alongside it; do not infer
  a zero-exception gameplay pass from that historical artifact.
* An inherited RSE EVA startup error comes from indexing prefab modules with a
  live module index. The candidate matches concrete type and occurrence. Actual
  KSP Mono IL/selection checks pass; a fresh Unity run is in progress. One stock
  orbit NaN exception during the second EVA remains unclassified.
* Latest focused build: 701 clock assertions on each CLR and KSP Mono; 82
  authenticated server assertions; 206 KSP Mono signature/serialization checks;
  173 Lidgren framing checks on each runtime. These counts are not substitutes
  for UDP, scene, rendering or signed updater checks.
* In the isolated shared-DeepFreeze profile, native transfer, freeze and thaw
  completed, but revision10 acknowledged the awake post-thaw state only. Frozen
  capture failed because the adapter expected Missing while native1.32 writes
  Dead. This is a failing persistence test, not a successful shared freeze. The
  candidate recognizes the native state without modifying it or weakening the
  server's full roster/part/world and authority checks. Its actual runtime
  frozen persistence now passes, including a full local server restart. Both
  clients restore the frozen registry on initial connection. Native building
  callbacks preserve the frozen crew and pod through Tracking Station and into
  flight; native thaw and its canonical/peer awake projections pass. The earlier
  failed scene test skipped save-before-building-entry in the harness. No
  product scenario repair was needed. Read-only reproduction
  exposed the same Missing-vs-Dead comparison in the native shared merge on both
  runtimes. The candidate patches only that reviewed status comparison; captured
  tuple tests prove the full native validator accepts Dead and legacy Missing,
  while rejecting mismatched identity, roster status and prior assignment.
  This run also exposed a
  separate resource framing conflict: the native outer message appends an
  aligned Int64 world revision. The extension reader now leaves that exact tail
  for native validation. Actual client/server serializer regressions cover it;
  the subsequent two-client gameplay run had zero framing failures.
* Cryopod IVA startup exposed MAS dereferencing a missing ModuleCommand. The
  candidate uses MAS's existing default control-point behavior for non-command
  cabins; command parts retain their original getter. Native IL tests pass,
  and subsequent two-client Unity runs had zero MAS startup exceptions.
* The failed legacy freeze profile is preserved. Successful fresh-profile
  freeze/thaw does not establish legacy migration or DeepFreeze power timing.
* A candidate DeepFreeze controller filter limits native vessel enumeration,
  discovery, coma recovery and part ticks to locally controlled simulation.
  It prevents an observer treating missing replicas as deleted ships or
  advancing their timer metadata. Native iteration-shape and non-mutating
  selection checks pass. Fresh two-client native freeze/thaw and persistence
  pass. Observer native timer/count fields match its committed server tuple
  exactly, including during a 50x owner warp. All four frozen checkpoint hashes
  remain unchanged for a measured 36.79-second interval with the owner offline
  and a peer warping at 50x. These tests use maintenance power and fatal effects
  disabled; they do not establish their compatibility. A native
  coma work list now rebuilds each tick instead of accumulating duplicate rows.
  Full DeepFreeze virtual-time and maintenance-power coverage remains open.
  A same-faction pilot on a later personal timeline took the frozen pod,
  thawed its crew, and committed the awake state. The former owner remained an
  inert observer. USI crew meal/update times remained within one second of the
  ship's virtual clock rather than jumping to the new pilot's personal UT.
* DeepFreeze raw time sources, docking/splitting and cross-vessel crew clocks,
  unloaded production/logistics, new mod interactions, both profiles and signed
  interrupted/skipped-version upgrades remain open. Production is unchanged.
* Normal flight disconnect exposed a null-reference burst in multiple installed
  mods. Native StopGame clears the active vessel and three vessel lists before
  the scene-exit callbacks. A candidate defers those four clears until KSP's
  main-menu GUI-ready event; native method-shape and callback checks pass.
  Both normal installed-code flight disconnects and reconnects now pass with
  zero exception headings (baseline9 primary/10 peer). Both main-menu cleanup
  assertions show pendingFalse and no vessels/active vessel; frozen canonical
  state and peer projection remain valid. This does not assert a complete
  pack or signed installer acceptance pass.
* Closed map/authority Player.log files are losslessly compressed as .log.gz.
  Their original and archive SHA256 hashes are in controller evidence
  `tests/rc35-resource-live/closed-df-log-compression.json`. Logical sizes reduced
  by 125917640 bytes; physical free-space recovery was not measured.
* The 5000-record permission benchmark covers only metadata predicates, not
  client rendering or bandwidth. Retain restricted outsider visibility unless
  lightweight public markers meet the user's performance requirement.
* Before the separation test, a native resource-list change exposed another
  proto/resource delivery race: an observer disconnected on a missing resource
  destination. The candidate rejects malformed rows first, defers the entire
  valid packet on a stale resource layout, and requests an ordinary current
  single-ship sync with bounded request frequency. Native packet tests cover
  old/fresh topology and malformed values/counts/null rows. The installed-code
  two-client test with a bounded proto-drain delay exercised the stale branch,
  made one current-proto request, stayed connected, and converged to both new
  resource values after release. The earlier add-only harness omitted native
  full-proto publication; it was corrected rather than counted as a product
  no-convergence failure. No overall release or signed-updater pass is implied.

Firefly/Planetside redistribution permission is recorded from the human response
in `REDISTRIBUTION-PERMISSION.json`; it is not an independently verified license.

The native separation test confirmed a clock-inheritance failure: the detached
capsule had no epoch until its first control handoff, then initialized to the
pilot's raw time, 1761.669 seconds ahead of its parent's virtual survival time.
Evidence is `tests/rc35-resource-live/split-clock-failure.json`. The original
failed craft and its detached section remain preserved without manual repair.
The next candidate scopes authoritative native decouple/undock operations and
seeds the actual new child during native Vessel.Initialize, before completion
events can publish a proto. It carries the launch anchor and captures only crew
on the child's current topology. Native construction-order and exception-scope
restoration regressions pass (217 actual-KSP-Mono checks total). The two-client
installed-code retest passes native separation, serialized crew partition,
observer read and same-pilot disconnect/reconnect/Tracking Station boarding.
The inherited personal-to-survival offset differs from the parent's by only
4.2e-11 seconds. This particular capsule has no Supplies and retains its native
home-world exemption; it does not add a consumption claim. See controller
`tests/rc35-resource-live/split-clock-candidate-results.json`. Docking, native
undocking and transfers between different survival epochs remain open gates.

The player's intermittent editor-selection report is tracked separately in
`tests/rc35-resource-live/editor-selection-investigation.json`. Native GUI-only
outside-click testing released the multiplayer menu's text focus and input lock;
that did not reproduce a stuck menu. The actual shipped WASD camera 0.7.3.3 does
lose a right-button release while the left button is held in an adjustment gizmo:
it updates mouseWasDown, then returns before removing EDITOR_GIZMO_TOOLS. The
original native method reproduced the retained lock across two frames using
controlled input/state getters on an unattached managed test instance in Unity.
No desktop input or cursor movement was used. The candidate finalizer releases
only WASD's own lock on the lost edge, with cleanup on camera-off and destruction.
The same controlled native test now clears on the first frame; native OnDestroy
and preservation of an unrelated editor lock pass. The production source was
initially linked into the test worker for this focused test. A subsequent normal
startup with the original WASD addon and candidate helper passes actual
product-hook/component checks, native move/rotate/place handlers in VAB and SPH,
unchanged craft geometry, native facility switching, normal exit/re-entry with
controls unlocked, and a native camera-toggle cycle. The naturally invalidated
ModuleManager cache produced a full run of 80472 patches with zero errors or
exception headings. Native handler and controlled release tests do not claim
physical mouse-click coverage or establish the cause of every intermittent
part-selection failure. Production is unchanged.

Natural orbital stock-port capture exposed a separate Probe Control Room
`stopIVA` null reference during `Part.Couple`: the callback dereferences its
missing room before checking it. The candidate replaces only that reviewed
camera-ancestry predicate with a null-safe check and retains the native cleanup,
portrait reset, input-lock release and camera restoration. The reviewed shipped
MVID and exact IL shape are enforced. 228 actual KSP Mono assertions pass; an
actual inactive PCR instance with no room completes its native cleanup when the
same product source is linked into the test worker. Normal installed-code
startup now also passes the actual missing-room callback and two natural stock
port captures, including a genuine Docked-state undock between them. Both
two-crew captures merged through the native FSM without exception headings.
This local helper is SHA256 `0BCB9CD1B8EFCA60526AE62F2DFCE878BFB0C5B5DBB45A9E929E7FF624AB84B5`;
see `tests/rc35-resource-live/probe-room-normal-capture-results.json` for the
interim scope and separate harness failures. Cross-epoch merging remains open.

The later repeated docking test ended in a native Unity crash after a Kerbin to
Sun transition produced NaN orbital state. The last recorded controller forces
were finite, but this does not establish the cause. The test controller now
rejects nonfinite translation/angular inputs, including an undefined identity
rotation axis, before applying forces. This is a harness precaution, not a
confirmed product fix. Prior logs are preserved as `.log.gz` with round-trip
hashes; `tests/rc35-docking-crash` records the terminal event. The private crash
dump is excluded from distribution. A subsequent read-only actual Unity probe
returned a finite axis for identity rotation, so that suspected cause is not
supported. The two later successful captures do not establish the cause of the
earlier crash. Cross-epoch crew transfers remain unverified; no production
release is implied.
