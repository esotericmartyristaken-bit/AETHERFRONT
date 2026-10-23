# RC35 beta release review

The controller requested publication after the focused checks on 2026-10-23.
This is a beta release, not a claim that every mod interaction has been tested.
Production remains Sandbox. Personal time and survival state travel with the
authorized vessel; faction members retain ship access and outsiders stay hidden.

Changes include authenticated personal timeline persistence, atomic survival
resource checkpoints, USI/DeepFreeze simulation authority and timer compatibility,
scoped editor and Tracking Station fixes, flag synchronization, and the requested
mod additions. Regular receives KerbalFX and Firefly; Potato omits those effects.
Shared parts, cameras and science definitions are identical between profiles.
The native server/client runtime grafts retain the reviewed deployed baseline's
authentication, permission and atomic checkpoint validation paths.

Validation:

* 701 clock persistence assertions on CLR and actual KSP Mono, each.
* 110 authenticated runtime integration assertions.
* 401 actual KSP Mono signature, serialization, timer and regression assertions.
* 173 actual Lidgren resource framing assertions on CLR and KSP Mono, each.
* Repeated normal two-client UDP joins, faction control transfers, offline peer
  warp, own-subspace reconnect, native docking/undocking and EVA cases.
* Loaded cryopod power consumption remained within one tick's budget during
  another pilot's actual 50x warp. Offline vessel/scenario/marker bytes stayed
  unchanged over 7,388 peer game seconds. Rejoin retained exact battery amount
  and restored the owner's subspace, rather than the peer's later time.
* Native unloaded cryopod consumption persisted across acknowledged commits,
  normal server restart, reconnect and reboarding.
* Normal installed final helper loaded 3,267 parts and joined the local server.
  The Tracking Station's absent USI settings now preserve an untouched validated
  deferred snapshot; native checkpoint acknowledgement and disconnect passed.
* Matching LunaCompat server plugin is included and mandatory in local staging.
  Both clients received version 0.1.0.0 after fixing the incomplete test fixture.
* Actual generated technology tree: 180 nodes. Final native science export:
  93,893 subjects, including new experiment metadata.

Limits and unfinished acceptance coverage:

* Full signed bootstrap/apply/restart and both profile switches were not repeated
  for this release. Local direct-connect tests deliberately bypass that gate.
* Complete KIS/KAS interaction, nearby decal rendering, camera UI, unloaded MKS
  logistics, frozen multi-part topology and starvation/coma combinations remain
  beta acceptance work. No security exploit is claimed from a source audit alone.
* The editor lost-button-release lock and organizer sorter errors were reproduced
  and addressed; every possible physical mouse selection failure was not proven.
* One earlier Unity orbit NaN crash remains unclassified. Successful later native
  docking tests do not establish its root cause or imply that it was fixed.
* No thousands-of-vessels Unity rendering performance claim: outsider visibility
  remains restricted, as authorized. Benchmarks cover metadata policy only.

Release inputs exclude identities, private keys, full saves, test instrumentation
and runtime preferences. Existing base-pack/DLC media remains immutable and is
reused. Newly published media is content addressed; the production host alone
signs client updates and applies the guarded native deployment.
