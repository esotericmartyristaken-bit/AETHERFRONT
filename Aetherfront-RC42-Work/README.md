# RC42 bounded pooled-buffer reuse

The shipped RC41 transport can reuse a recycled incoming 2 MiB array for a
64-byte outgoing message. Its reliable-send memory guard correctly counts the
real allocation. Thirty-two such messages consume the 64 MiB allowance; the
next message requests a disconnect although retained payload is only 2 KiB.
The same 33-message test with appropriately sized buffers succeeds. This is an
isolated reproduction, not a capture of an affected player's actual queue.

RC42 changes the shared allocator before an array leaves its pool. A candidate
must still meet the original minimum capacity. It is skipped only if capacity
exceeds both requested size plus 1024 bytes and twice requested size. Ordinary
slack and exact/large-buffer reuse remain available. Oversized candidates stay
in the existing bounded pool for appropriately sized requests. All comparison
arithmetic uses Int64, and the pre-existing negative-input behavior is retained.

This location is deliberate: changing storage after send reservation or
fragment sharing would invalidate array-identity accounting. No reserved array
is modified. Send queueing, ACK processing, fragmentation, retransmission,
disconnect cleanup, pool mutation/accounting and wire format remain unchanged.
The 64 MiB send/pool limits and existing 4 MiB/64 MiB receive limits remain in
force. The RC40 receiving compaction fix remains present. This does not disable
disconnects caused by a genuine large reliable backlog or a broken connection.

`Patch/Patch.csproj` targets .NET 8 and accepts Mono.Cecil through
`-p:CecilPath=...`. Invoke `transport INPUT OUTPUT` for either reviewed shipping
transport, or `version INPUT OUTPUT KSP_MANAGED_REFERENCE_DIRECTORY` for the
client's RC41-to-RC42 version-only change. Inputs are pinned by SHA256. Each
transport gets one six-instruction allocator eligibility hook and one private
helper. Removing the hook recovers the original method body, including its
monitor/exception/accounting structure. Audits verify every other method body,
reference, field and resource unchanged. No KSP or proprietary DLL is exported.

`Tests` exercises the exact original and patched client/server assemblies in
isolation. `Mono` covers the client under KSP's embedded runtime. Test fixtures
must not start a production peer, access a remote installation, or create a
production player account. Generated binaries/results stay outside this tree.

`Build-Release.py --baseline RC41_NATIVE_ZIP --code PATCHED_DIRECTORY --output OUTPUT_DIRECTORY`
validates the immutable RC41 archive and every inventory hash. It preserves
gameplay, progression, identity checks, mod assets, profile ownership and all
immutable media. Both Full and Potato receive matching RC42 versions and the
same corrected shared client transport. The normal signed updater supports
skipped revisions; unchanged mod archives do not need downloading again.

The builder explicitly removes RC41's one-time `AccountReset.json`. That
completed account operation must never be replayed by this or a future release.
No production identity, state, configuration or private key is included.

Publish only after regression and runtime gates pass. Use the normal pinned
host preflight and immutable GitHub application channel, preserving backups,
journals, and the 15-minute occupied-server warning/guard. Do not force a
restart, replace live DLLs manually, or touch the vanilla deployment.
