# RC46 research confirmation repair

Stock R&D purchases were optimistic locally. Concurrent purchase events used the
same cached personal command sequence; the server correctly rejected later
requests with `PersonalSequenceMismatch`, then its snapshot re-locked the node.

The client now queues up to 32 distinct technology intents. Only one is sent at
a time, at most once per second. The saved personal unlock confirms a purchase;
an unrelated command reply cannot confirm or discard it. A newer authoritative
counter retries the still-locked intent. A missing confirmation requests a fresh
snapshot before retrying. Three attempts or an unavailable snapshot pause with
a visible warning. Reconnect/identity changes discard session-only intents;
the saved server state remains authoritative. Science costs, prerequisites,
sequence validation and replay protection are unchanged.

`ResearchQueue.cs` is the testable state machine. `ResearchSync.cs` is its
Unity-thread adapter, called by the existing personal-science addon. Both the
stock R&D event and Aetherfront research panel already share the patched
`PersonalClient.RequestTechnology` entry point.

The patcher pins RC45 inputs and grafts only these two types into the existing
personal client. Exactly three original methods change: purchase entry point,
addon Update, and research status display. LmpClient changes only its release
version. No extra bootstrap plugin or protocol/core/server change is introduced.

The release builder pins the immutable RC45 archive, preserves every native
payload byte (including the tested clock repair), ground/orbit repairs, media,
and all client files except the two reviewed DLLs and three version/manifests.
Full and Potato use the same repaired client. No accounts, universe, signing
keys, credentials or one-time compensation operations are packaged.

## Verification

`Tests/Program.cs`: 9 cases / 40 checks using the shipping server core:
rapid/dependent purchases, duplicate clicks, stale sequence/transfer race,
lost reply and late duplicate delivery, disconnect/server/account changes,
insufficient science/prerequisites, bounded/manual retries, dead connection,
and queue capacity. `Tests/Run-Mono.ps1` executes the same cases against the
actual grafted helper under KSP's embedded Mono, not a rebuilt substitute.
This is not an interactive stock R&D multiplayer playthrough.

Build with the repository's existing .NET 8 SDK, KSP managed references and
Mono.Cecil, supplying `ClientPlugins`, `KspManaged`, and `CecilPath` properties.
Run the net8 test DLL through that SDK explicitly. The patcher additionally
needs `AETHERFRONT_KSP_MANAGED`. Generated binaries/evidence remain private.

Deploy through `Aetherfront-RC44-Work/Deployment/Deploy-LocalRelease.ps1`:
normal UAC consent, frozen source/archive hashes, isolated signed candidate
startup/HTTPS/shutdown gate, then 15-minute player admission if occupied.
Production must stay running on any preflight failure. Do not resume the
retired Git-triggered host publisher or reuse changed bytes under RC45.
