# RC48 recovery science capture

RC48 is an audited overlay on the pinned deployed RC47 archive. The existing
research queue/reservations, transport pacing, clock handling and atomic native
store are preserved. Unrelated methods, fields, resources and assembly identities
are checked by the Cecil graft audit. Native Server.dll and continuity DLLs remain
byte-identical to RC47; the personal server changes only receipt logging.

## Confirmed failure paths

Inspection of the licensed KSP 1.12.5 assembly shows that recovery reads its
`scienceSubjects` dictionary directly. It bypasses the private subject getter
previously patched to restore authoritative experiment history. A stale scenario
can therefore show a stock recovery award that the cumulative server ledger has
already credited. Periodic snapshot application does not close that timing gap.

The canonical six-argument `SubmitScienceData` overload updates the subject,
awards the stock wallet, and then fires `OnScienceRecieved`. An earlier failing
event subscriber can prevent a later subscriber from seeing the award. An award
must not depend solely on that event or on the next frame occurring.

RC48 restores both saved server history and unsent local cumulative history at
submission entry, before stock reward calculation. A submission-scoped finalizer
saves evidence immediately after a successful award, including an observed
wallet award followed by an event exception. Both AddScience and submission use
finalizers: KSP changes its wallet before firing currency events, whose exceptions
would skip an AddScience postfix. Cumulative float ULP is included when correlating
a small award after an exception. Original exceptions remain intact;
float values are widened before decimal conversion so small increments on larger
subject totals survive the six-decimal wire representation;
failures before any wallet award cannot mint science. Event and finalizer capture
merge into one cumulative claim. Nested equal-amount submissions are attributed
to their own scope. Identity is captured before submission and never migrated
to a newly connected account/server. Partial Harmony installation retries only
missing hooks. Subjectless lab receipts keep their existing immutable flow.

These are demonstrated code defects, not proof of the exact cause or amount of
every reported lost mission. Historical packets cannot be reconstructed from
the account ledger. Bounded authenticated science-receipt logs now record the
requested and recorded amounts, disposition and reason without dumping packets,
snapshots, tokens or private keys. Existing cap/replay validation remains enabled.

## Verification

`CollectorTests/Run.ps1` executes the production collector/outbox/history source
with the shipping Harmony DLL on KSP's embedded Mono. KSP/Unity services are
fixture doubles; the submission ordering is checked against the actual licensed
assembly. Cases cover stale caches, scene reloads, 25-experiment recovery,
transmission, event exceptions, pre-award failures, nested awards, identity
changes, subjectless awards and sandbox exclusion. This is not an interactive
multiplayer flight playthrough.

`Test.ps1` rechecks research, outbox, actual grafted receiver, native store,
science receipt handling, replay and reload. The nine documented .NET 8-only
JsonFx nonempty-list failures must still pass on KSP Mono; unexpected failures
are not waived. `Test-Compensation.ps1` validates private file transactions and
replay/rollback behavior. `NativeCompensationTests` verifies that a compensation
survives the actual server store loader, a later mutation and another reload.

## Private operator compensation

An optional hash-bound, data-only plan stays outside Git and release archives.
The elevated local operator freezes it alongside the reviewed source/archive.
Validation happens before candidate testing and shutdown. Application happens
only after signed candidate startup/HTTPS/clean-shutdown checks, player warning,
clean production stop, exclusive activation lock, full world backup and durable
activation journal. Only the named personal Science balance and the revision/
durable commit counters change. Earned/tradable science, cumulative sources and
shared totals do not change. The receipt is part of the backed-up world; rollback
restores both credit and receipt. Completed receipts are idempotent; ambiguous
receipts block replay. This is not a network-accessible administrator grant API.

Deployment remains local-only. If occupied, the existing 15-minute in-game and
Discord warning is required. Only fresh authoritative player and connection
counts of zero permit earlier activation. No extra restart is used for credit.

The receipt reader preserves hash-bound process-start timestamp strings instead
of round-tripping them through PowerShell's automatic DateTime conversion. This
prevents trailing fractional zeros from producing a false live-process-change
failure. Tests still reject a one-tick start-time change and changed PID, listener
ownership or pack hash. No identity check is rounded, skipped or weakened.
