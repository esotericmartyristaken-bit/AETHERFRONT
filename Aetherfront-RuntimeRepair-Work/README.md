# Automatic Windows game-assembly repair

This repair is included in the narrow RC39 release under
`Aetherfront-RC39-Release`. The user authorized publishing the working changes
while keeping all arena/NPC code out of the package. Signing remains on the
guarded host. The user's installation is a read-only build input; tests mutate
only small disposable filesystem fixtures. The headless transaction checks do
not substitute for the actual update/restart/join smoke check.

The November 1 player log proves that DeepFreeze and the continuity client match
the shipping RC38 DLLs, while Assembly-CSharp differs. The tracking-label MVID
guard aborts SimulationAuthority.Awake before DeepFreeze negotiation installs.

The candidate pins the user's Windows KSP 1.12.5 build 3190 implementation:

- SHA256 `d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4`
- MVID `4b449f28-41f8-4227-adfa-ad3149c8fdba`
- Target `KSP_x64_Data/Managed/Assembly-CSharp.dll`
- Non-DLL payload `GameData/LunaMultiplayer/PluginData/Runtime/Assembly-CSharp.apfbin`

Connect checks the one game assembly even when completed receipts match. A
missing or incompatible runtime cannot take either no-change return path or
proceed through the connection scanner. If the cached payload is absent or
damaged, the client-receipt shortcut is invalidated so the normal signed client
refresh can supply it. Compatible checks do not overwrite the runtime or need a
pack download. The new startup guard defers continuity initialization with a
specific repair message instead of throwing halfway through initialization.

The existing Restart & apply approval covers the transaction. After the core
verifies and applies the signed client/pack, NativeInstall.Run invokes repair
inside its normal failure handler, before success or relaunch. No game process
is launched by the repair itself. A currently loaded game assembly or an active
KSP process at that root blocks replacement. An approved disk file with an older
module still loaded also requires restart; disk replacement cannot change the
running process's implementation.

The new stock-assembly write also independently rechecks the publisher's RSA
release signature, the local transaction authorization, and the signed payload
inventory. A forged publisher signature with a valid local HMAC is refused. An
unchanged-profile repair preserves existing graphics preferences instead of
running the generic profile graphics reset.

Replacement requires Windows, build 3190, matching UnityEngine.CoreModule and
Assembly-CSharp-firstpass fingerprints, the exact pinned payload size/hash and
plain local paths. It uses durable staging, atomic replacement, one original
DLL recovery copy and a bounded recovery journal. Interrupted commits resume;
unknown intervening changes preserve evidence and fail closed. The normal client
manifest's allowed destinations remain unchanged: it supplies a non-executable
payload, and the new code can replace only the fixed, pinned target.

## Build and verification

Run `Build.ps1`, then `Test.ps1`. The patcher starts from the byte-identical
shipping RC38 DLLs; it changes five updater method bodies and one continuity
startup body, imports self-contained repair methods into KesasInstall, and audits
all other method bodies as unchanged. No RC39 campaign source is compiled or
modified. `build/runtime-repair-candidate` contains the code, audit, results and
unpublished overlay. `Test.ps1` uses the actual Windows CLR and KSP Mono and real
RSA-signed client transactions with disposable keys. It never calls Restart.
The final suite passes 63 assertions under each runtime, including actual
client and pack helper transactions. Run `Export-Results.py <controller>
<game-root>` to bind those results to the exact code and review ZIP.

## Release integration and limits

Merge the overlay's three files into the next versioned ClientTemplate and its
signed inventory. Add the resulting DLL hashes to profile metadata if that
release enumerates them, then use the normal release verifier and publisher.
This review ZIP is not itself an authorized updater feed or a standalone starter.

An already-loaded RC31-RC38 helper cannot execute repair code that it is only
just installing. Those clients first need the updater feature installed, then
their next Connect prepares the runtime repair. No single-restart bootstrap
from an old helper is claimed. New starters can include the approved target
directly and need not bootstrap this capability.

Live KSP restart/join, the affected player's supporting runtime fingerprints,
and older-helper bootstrap are unperformed. The current change does not provide
macOS/Linux support or overwrite assemblies from other KSP/Unity builds.
