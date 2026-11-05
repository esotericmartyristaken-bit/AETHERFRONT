# RC47 science reliability and disconnect corrections

RC47 is an audited overlay on the immutable deployed RC46 archive. It does not
replace the client or server with an older source rebuild. The patcher pins all
five input DLL hashes and verifies that unrelated method bodies, fields,
assembly identities and resources remain unchanged.

## Repairs

- Stock research keeps a presentation-only overlay for queued purchases. Costs
  are reserved once in the displayed balance. Saved purchases are not charged
  twice; intermediate snapshots cannot relock later valid queued purchases.
  Invalid, unaffordable or paused intents reconcile with authoritative state and
  a visible error. Disconnect and identity changes discard provisional unlocks.
  Server validation, command sequences, prerequisites and replay protection stay
  authoritative. Purchases are persisted immediately, not only on leaving R&D.
- Recovery/transmission subject evidence is saved during the receipt event,
  before a scene transition can skip the next addon Update. Existing cumulative
  high-water awards, multiplier handling, immutable lab receipts, bounded
  backoff and terminal-rejection evidence remain intact.
- Changing account/server clears only obsolete in-flight receipt correlation
  and snapshot targets. Durable claims keep their original identity. An old
  server's high revision cannot cause endless refreshes on a different server.
- Personal research no longer asks every other player to download a world
  snapshot per purchase. Shared-science notices coalesce for 10 seconds instead
  of 2 seconds. Earners still receive immediate compact durable receipts; the
  shared ledger settles from the same authoritative totals.
- Mission-clock corrections up to 2 seconds retain the durable high-water mark.
  Repeated corrections cannot accumulate backward. Larger rewinds, invalid
  times, corrupt records and failed durable checkpoints still fail closed.
- State snapshots use unique flushed temporary files and atomic replacement.
  Known Windows replacement conflicts retry for at most approximately 1 second
  only while both pre-operation byte images remain unchanged. No permissions
  change, partial-write fallback, false acknowledgement or journal reset occurs.

These address confirmed code defects and reduce snapshot pressure. They do not
prove the cause of every generic `Disconnected`, burn-time timeout or incoming
queue overflow. Transport memory limits are not raised or disabled. Client logs
from a remaining incident are needed to distinguish those cases.

## Verification and operation

`Build.ps1 -BuildTag NEW_TAG` compiles donors privately, then performs narrowly
scoped grafts. `Test.ps1 -Code ABSOLUTE_CODE_DIRECTORY -TestTag NEW_TAG` exercises
research ordering/reservations, reward replay/reconnect/disk failure, actual
native store commits/reload, clock high-water behavior, and KSP embedded Mono.
The legacy client JsonFx library emits Mono-specific IL for nonempty lists;
nine documented .NET 8-only failures remain in evidence and must pass the Mono
gate. They are not presented as successful .NET 8 tests. Receiver fixtures run
the actual final DLL and check message recycling and sender cancellation.
The existing RC44 server integration harness additionally accepts an explicit
build-time `CandidateHash` and exercises real personal-clock admission hooks.
No fixture opens production sockets or mutates production accounts/state.

`Build-Release.py` requires the pinned RC46 release plus all five audit reports.
Full, Potato and starter feeds receive the same client. Media and all unreviewed
payloads remain byte-identical. Production keys, saves and credentials are never
packaged. Deploy with the existing local RC44 deployment operator: complete
isolated signed candidate startup/HTTPS/shutdown checks before stopping service,
then a 15-minute in-game/Discord restart warning if players are online. Fresh
authoritative empty status permits earlier activation. Git-triggered host
deployment remains retired. An interactive multiplayer R&D/flight playthrough
is separate from the headless and Mono gates.
