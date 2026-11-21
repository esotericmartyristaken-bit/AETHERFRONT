# Read-only cross-faction Tracking Station markers

This preparation is pinned to the verified RC64 shipping assemblies. It does not
touch the running server, build a signed release, or publish anything.

Build on the VPS in PowerShell 7.4 or later:

```powershell
./TrackingStation/Build.ps1 -Output ../TrackingStation-Candidate
```

Use a fresh output directory. The matched outputs are
`Candidate/Server.dll` and `Client/AetherfrontTrackingClient.dll`.
Never ship `Reference/Server.dll`, the donor, or the test fixtures. A future
release must include the client DLL in the signed plugin catalog and the
grafted Server.dll in the matching server payload. Do not replace live DLLs.

The map draws a cyan dot and hover label, not a stock Vessel or ProtoVessel.
Markers never add a stock Fly, recover, target, or terminate action. Existing
stock vessels suppress duplicate dots. Orbits are estimates propagated to the
viewer's clock; landed and splashed craft use saved surface coordinates.

The existing authenticated snapshot decoder routes Tracking Station polls
before the fleet lock. The read-only projection uses the registered,
non-archived vessel store, excludes tombstones and the viewer's own or faction
craft, and sends no parts, ownership IDs, or control grants. Public faction
profiles expose these read-only positions; private craft require existing
metadata permission and a known matching subspace. Private shares without a
known saved subspace fail closed. Removing access or a vessel removes it on
the next successful poll; the overlay expires if replies stop. Polls occur
only in the Tracking Station and do not send the full catalog when the
revision is unchanged. The first 128 eligible IDs form a bounded catalog.

Before release, use a fresh isolated two-client world, not player saves:

1. Enter Tracking Station as an outsider with one public landed ship and one
   public orbiting ship owned by the other client. Verify marker placement,
   hover text, stock UI access, and that no foreign marker can be flown.
2. Disconnect the owner and reopen Tracking Station. Check both saved
   markers. Add a private ship, grant metadata in a shared timeline, change
   timeline, revoke the grant, archive or remove the ship, and verify the
   corresponding marker appears and disappears as expected.
3. Switch to flight and reconnect while a poll is in flight. Verify that the
   old response never alters gameplay state or appears in the next scene.
4. Compare network bytes and Tracking Station frame times with the feature
   off and on. Exercise a large fleet and record the 128-entry limit where
   applicable. Recheck stock marker suppression when a ship is fully synced.

## DeepFreeze startup recovery (RC67)

RC67 also handles missing DeepFreeze vessel projections without removing
the corresponding committed scenario entries. It permits a missing
projection to remain on hold only when the retained crew, freezer seats,
and roster agree and none of those crew or parts exist on a present ship.
Held vessel IDs cannot be replaced by ordinary packets. The existing
committed-crew protections and validation of present DeepFreeze vessels
remain active. This does not reconstruct a missing craft or thaw its crew.

`Test-HeldDeepFreezeWorld.ps1 -CandidateRuntime <test server directory>`
uses a retained save fixture. It checks the native validator and byte
preservation, then changes one roster entry on a disposable copy to prove
that mismatched data still fails startup. The guarded publisher must also
boot the complete candidate on a fresh snapshot before stopping production.
