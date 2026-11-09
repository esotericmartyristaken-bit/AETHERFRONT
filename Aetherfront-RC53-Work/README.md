# RC53 personal flight checkpoints

F5 saves the pilot's entire personally owned fleet. F9 opens a confirmation
window; the same controls are available under **Personal checkpoints** in flight,
the space center and tracking station. Loading disconnects only that pilot.
Connect again to resume the saved personal mission time and fleet; KSP does not
need a process restart.

Automatic choices target approximately 1, 5 and 30 real minutes ago, plus the
latest accepted VAB/SPH launch. Manual quicksave is a fifth independent choice.
The actual saved date/time, mission time and craft count are displayed. Sampling
starts after authenticated clock synchronization and runs once per minute while
personal time is stable; time spent warping or offline can leave older samples or
make an age unavailable. Historical samples accumulate as people play, so a new
account does not immediately have a 30-minute checkpoint.

## Persistence and limits

- Up to 32 compressed rolling samples per pilot, bounded to 128 MiB, plus one
  launch and one manual snapshot. The compressed byte limit can shorten rolling
  coverage for unusually large fleets. No duplicate complete game installations
  or release payloads are stored in checkpoints.
- Automatic snapshots capture immutable strings under the existing fleet gate;
  compression, writes and retention cleanup run outside it. A single worker
  processes at most one due pilot per second and skips a busy gate. Disconnected
  client references, save throttles and idle workers are removed.
- Atomic checksummed, length-bounded compressed files. Incomplete writes use
  unique temporary files; stale abandoned files are cleaned after ten minutes.
  A completed restore removes its journal; the temporary pinned restore image is
  removed after successful reconnect. Active recovery evidence is preserved.
- The existing native DeepFreeze transaction store commits the affected vessel
  and crew files. Interrupted world/registry/clock/receipt phases retry
  idempotently. A server restart recovers outstanding journals before resuming
  players. Monotonic clock protection remains active outside an authorized
  personal restore.

## What a load changes

Saved personally owned craft, their resources/carried science/embedded survival
timers, crew and owned DeepFreeze state return to their saved values. New personal
craft created after the checkpoint are removed. Restored craft are assigned to
the pilot's newly resumed private subspace. Other players' craft, clocks, crew,
and newer DeepFreeze state remain current.

Banked personal science, research, funds, science receipts and already-issued
50% shared science stay current. Cumulative subject receipts prevent ordinary
experiment science from being awarded twice. For safety, a restore is refused
after intervening trades, purchases, funds changes or anonymous laboratory awards;
make a new checkpoint after those transactions. Changed ownership/sharing, mixed
ownership docking, another pilot controlling the fleet, foreign crew/parts and
captured asteroid/comet fleets also require resolution before loading. A change
to server mode or the published pack invalidates earlier checkpoints.

## Release construction

`Patch` pins the five RC52 input assembly hashes and preserves shipping identity,
resources and unrelated method bodies. It adds only checkpoint types and narrow
dispatch/launch/clock/client hooks, and stamps the client RC53. A method/field
audit is emitted per assembly. Existing transport and updater implementations
are retained. The build does not deploy unfinished arena features.

`Build-Release.py` verifies the exact RC52 native archive and all manifest hashes,
checks each graft audit, rebuilds client/catalog/release receipts and verifies
that only the five assemblies and required version metadata changed. Existing
media archives and native dependency files remain byte-for-byte unchanged.

## Verification

The `Development/PersonalCheckpoint*Tests` projects exercise bounded storage,
cleanup and compression, exact shipping server integration, interrupted recovery,
crew/DeepFreeze isolation and shipped-client parsing/JIT under KSP Mono. The
scene probe is restricted to an owned temporary KSP install and renders disabled
synthetic data after MAINMENU/part compilation. It is never shipped.

`PersonalCheckpointWireTests --loopback <new-evidence>` tests ordinary accepted
launch, save, resource changes, load and RSA-authenticated reconnect, while a
second player remains connected. `--live-review` explicitly selects the authorized
production endpoint for the same small synthetic-client acceptance scenario.
These are normal workflow tests; they do not claim Unity flight/physics testing.
Do not run either against another server or use real player identities.
