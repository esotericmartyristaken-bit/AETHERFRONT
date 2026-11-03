# RC42 allocator and reliable-send regressions

`ReflectionSuite.cs` is a shared reflection-only test engine for .NET 8 and KSP's
embedded Mono. It never starts a peer, opens a socket, connects, flushes a send
queue, runs a heartbeat, accesses a player installation, or changes production.
The ACK cases encode two small messages into a private managed buffer; they do
not transmit packets. Cases and targets run sequentially, with full GC cleanup
between cases. Integer overflow cases invoke the capacity predicate with large
integers without allocating correspondingly large arrays.

The CLR entry point pins all four original/patched DLL hashes and requires:

```text
dotnet TransportTests.dll ORIGINAL_CLIENT PATCHED_CLIENT ORIGINAL_SERVER PATCHED_SERVER SUMMARY.json
```

Build `TransportTests.csproj` with .NET 8. Put build output, intermediate files,
and the summary outside the source tree, for example in
`UpdateBuilds/RC42-20261103/tests` and `tests-obj`.

Coverage includes the exact original 33rd-message disconnect after seeding a
64 MiB pool, patched 128-message/8 KiB bursts, both allocation directions,
1024-byte and 2x eligibility boundaries, first-fit order, preservation and later
reuse of skipped arrays, negative/zero/large requests, disabled recycling,
Int64 helper arithmetic, all outgoing byte/array/reservation caps, shared
fragment/multirecipient buffer accounting, out-of-order/duplicate ACKs, reset,
and preservation of the RC40 receive compaction and cap.

`../Mono/Run.ps1` compiles the same engine against the local KSP smoke copy's
managed reference assemblies and runs original/patched client DLLs in separate
existing MonoHost processes. It does not launch KSP or any GUI. Its lighter
19-case matrix excludes the six high-iteration/large-cap CLR-only cases.
Runtime inputs and results default to `UpdateBuilds/RC42-20261103/mono`.

These are isolated regressions of the exact binaries, not a player traffic
replay or evidence of an affected player's actual pool contents or ACK timing.
