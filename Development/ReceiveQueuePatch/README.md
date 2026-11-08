# RC51 connection reliability candidate

Start from the reviewed native RC50 archive. The graft tools refuse inputs
other than their pinned shipping DLLs and an explicitly identified older
server fixture for the warp regression. The older fixture must never be used
to assemble the release.

1. Build `Donor` in Release configuration. Run `Graft` with
   `-p:CecilPath=<absolute path to the trusted Mono.Cecil.dll>` and three
   arguments: input transport DLL, `Donor.dll`, fresh output path. Repeat for
   `Lidgren.Core.dll` and `Lidgren.Network.dll`.
2. Build `WarpDonor` in Release configuration with
   `-p:Runtime=<absolute directory of RC50 server code>`. Run `WarpGraft`
   with the same Cecil setting and arguments: input `Server.dll`,
   `WarpDonor.dll`, fresh output path.
3. Run `../ReceiveQueueTests` against the output using `verify` for the
   dedicated transport, `verify-client` for the client transport, and `udp`
   and `impaired-udp` for real loopback traffic. `reproduce` verifies the
   old overflow on the unchanged shipping dedicated transport.
4. Use the existing RC44 clock integration harness against a private RC50
   runtime, passing the actual continuity SHA as `CandidateHash`. Set
   `APF_VERIFY_STALE_SUBSPACE=reproduce` for the old server and
   `APF_VERIFY_STALE_SUBSPACE=verify` for the candidate. Confirm the stale
   request is rejected, the good clock persists, and forward switching,
   read-lock retry, corruption detection and offline resume still work.
5. Assemble with `Build-Release.py` using the original archive, all three
   reviewed output binaries and their recorded SHA-256 values. Run isolated
   authenticated LMP testing and the existing guarded release operator.

The queue changes keep the original aggregate limits, reserve headroom for
reliable traffic, isolate a reliable sender with excessive backlog on the
server, and distinguish future server and client receive overruns in logs.
Unreliable data can be dropped under pressure. Reliable messages keep their
ordering and an over-budget connection is explicitly closed for resync.

The warp guard checks the requested offset before moving the player and their
vessels. A stale request reasserts the current subspace through the existing
targeted warp reply. The durable clock still rejects rollback, corrupt
records and failed writes.

These changes do not establish the cause of the historical transport
inactivity or prove production reliability. Public probe checks, protocol
joins and sustained live monitoring are separate release gates.
