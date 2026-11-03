# RC40 reliable receive-buffer retention

RC39 can reuse a large recycled incoming array for a tiny packet. If several
packets arrive ahead of their expected sequence, their backing arrays consume
the reliable-order retention allowance and disconnect an otherwise ordinary
session. The exact shipping client reproduced the failure with five 64-byte
messages backed by recycled 1 MiB arrays. The fifth exceeded the 4 MiB budget.

The patch compacts unusually oversized retained message storage before the
existing allocated-byte checks. Copies occur only when wasted capacity exceeds
1024 bytes and capacity exceeds twice the active payload. Valid active bytes,
bit length and reader state are preserved. Copies are bounded to the original
4 MiB connection allowance. Oversized arrays are not immediately recycled back
into the same allocation pattern. Normal in-order traffic is unchanged.

Both original memory limits remain unchanged: 4 MiB per connection and 64 MiB
per peer. The patch does not count only payload, disable guards or change the
wire format. Each reviewed client/server transport receives one private helper
and a two-instruction guard preamble. Method audits establish that every other
method body, reference, field and resource is unchanged. The client also gets
the normal one-getter/build-constant release-version change.

`Patch/TransportPatch.csproj` targets .NET 8 and takes an explicit existing
Mono.Cecil assembly through `-p:CecilPath=...`. Its two-argument command patches
one transport into a separate output. The version command is:

```text
dotnet TransportPatch.dll version ORIGINAL_LmpClient.dll NEW_LmpClient.dll kesas-rc39 kesas-rc40 KSP_MANAGED_REFERENCE_DIRECTORY
```

`Tests/TransportTests.csproj` exercises original and patched client/server
libraries in isolation. No production peer/socket is started. Build artifacts
and licensed inputs stay outside this source tree. `Mono/Program.cs` provides
a separate shipping-client check for the actual KSP Mono runtime.

Verification passed: 84 isolated CLR cases with 3,130 assertions, plus eight
cases with 365 assertions under KSP's embedded Mono runtime. The original
shipping DLL reproduced the allocator/retention failure; the patched DLL did
not. Reordering, duplicates, sequence wrap, cleanup, malformed lengths and
genuine limit violations are covered. No game, production socket or remote
client was started for these tests. This is not a captured replay of a player's
traffic or a live KSP session test.

`Build-Release.py --baseline RC39_NATIVE_ZIP --code PATCHED_DIRECTORY --output OUTPUT_DIRECTORY`
verifies the immutable RC39 native release and every baseline manifest entry.
It preserves unrelated client/server code, gameplay, progression and all mod
media. Only transport, client release identity and matching catalog/manifest
metadata change. Regular and Potato share the signed updater inventory, so no
overlapping catalog DLL owner is added. Cross-revision installs continue using
the existing signed updater and transaction/restart mechanisms.

Publish through the existing authenticated GitHub application publisher, with
the trusted host certificate pin and fresh host preflight. It retains backups,
journals and the occupied-server 15-minute warning/guard. Never force a restart,
copy binaries into the running installation or change the vanilla server.
