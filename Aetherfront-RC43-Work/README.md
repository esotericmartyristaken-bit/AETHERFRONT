# RC43 science transmission and reliable sender pacing

This coordinated release requires client and pack version `kesas-rc43` in both
install profiles. The authenticated updater supplies the new client helper and
LmpClient hooks. Transport libraries, authentication, science caps, award
validation, account ownership, and shared-science accounting remain unchanged.

Science confirmations carry a correlated, account-scoped receipt instead of a
full account snapshot. Advancing science notices coalesce over two seconds and
include a trailing notification. Clients retain unconfirmed evidence in the
existing version-1 outbox, persist attempts before sending, allow two in-flight
claims, and send at most once per second. Retry delays rise to 60 seconds.
Terminally rejected claims pause with a reason; they are not awarded or deleted.
Other claims continue. A user retry or a new client release can retry paused
evidence. Reward chunk IDs and cumulative-subject idempotence are preserved.

The reliable sender waits on its existing background thread before allocating
another outgoing message when retained connection or peer storage approaches
its limits. Admission accounts for all six budgets and a conservative next
message cost. Real ACK progress, not flushes, frees capacity. Cancellation,
connection replacement, generation checks, hard caps, and the original single
message-recycle finally remain intact. A 30-second no-progress deadline ends a
stalled session explicitly. Rate-limited counters aid subsequent diagnosis.

## Build and verification

Use .NET SDK 8 for the net8 server and tooling; the client targets net472 against
the exact licensed KSP managed references. Keep build output and fixtures outside
Git. `ClientPlugins`, `KspManaged`, `ServerReference`, `PersonalCore`,
`ServerRuntime`, and `CecilPath` select private baseline dependencies.

`ClientTests` uses the actual Core/JsonFx with isolated Unity/network stubs.
`Mono` checks compatibility in KSP's embedded runtime; .NET 8 alone cannot
validate this older JsonFx collection deserializer. `ServerTests` uses isolated
ledger/catalog fixtures. `PacingTests` uses the actual unstarted transport and
simulated ACKs, never production sockets. The real deadline case takes 30 seconds.

`Audit` pins the original helpers and rejects unrelated method, field, resource,
or identity changes. `Patch` pins RC42 LmpClient, inserts only release, receipt,
and pacing hooks, and reconstructs the original receiver/sender lifecycle for
comparison. `Build-Release.py` derives a keyless native archive from immutable
RC42, verifies those audits, updates both catalog profiles and all manifests,
preserves media, and excludes account operations and production state.

Publish only through the existing guarded native workflow. Honor the 15-minute
online-player warning and final admission guard. Do not copy live DLLs, reset
deployment journals, or replay earlier account operations.
