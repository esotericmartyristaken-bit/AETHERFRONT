# Broader warp/disconnect investigation

No production changes, pushes, PC control, live client installation or restart.

## Confirmed code paths and evidence

1. RC37's CheckWarpStopped responds to rate index 0, actual rate near 1, current
   subspace -1 and no outstanding subspace request. It does not require Escape.
   Every automatic or manual warp exit can reach the same timeline checkpoint.
2. An audit of installed managed assemblies found 176 warp/UT-changing call sites.
   MechJeb, Kerbal Alarm Clock, BackgroundResources, ScienceChecklist and others
   can call TimeWarp.SetRate. These are callers, not proof they caused an incident.
   The audit executes no mod code. Exact results: tests/rc38-warp-call-sites.json.
3. The actual stock TimeWarp.autoWarpTo iterator logs "Warping down..." then calls
   Planetarium.SetUniversalTime(decelerationStart - fixedDeltaTime/2) to correct
   overshoot. This can be a real UT reduction, a distinct issue from packet offset
   skew. It is not silently clamped here: doing so could change maneuver timing
   and requires targeted Unity/USI tests before choosing the correct fix.
4. The current APFBeta KSP.log at 21:35:15 local shows a 21.228-second forward
   timesync correction during flight initialization, followed by ModularFlight-
   Integrator startup. Smaller ~3.5-second corrections recur. This demonstrates
   real simulation lag, not a Yogurt disconnect or a proven 5 km loading trigger.
5. The supplied KSP (1).log is the separate DLC/startup case; KSP(1).log is the
   earlier disk-space/startup case. Neither contains a joined-flight disconnect
   timeline. The screenshot supplies a report of mission-clock refusal, but no
   raw per-incident server log or current affected-client flight log is present.
   Existing public host diagnostics deliberately exclude raw logs/player details.

## Candidate improvement

The previous server correction was not Escape-specific. A second client-side
guard now prevents its outgoing NEW own-subspace offset from trailing its own
last stable offset after a long load/simulation stall. The server still rejects
large raw backwards proposals and the disk store retains its original 1 ms guard.
No older peer clock, offline clock, saved vessel or life-support state is reset.

Actual KSP Mono checks cover the shipped message producer, one scoped injection,
21.2/120-second lag, unchanged forward warp, non-finite rejection, separate system
floors, advancing acknowledgement and disconnect cleanup. The unrelated editor
and presentation changes remain part of the same unpublished candidate.

## Still required

- The affected player's KSP.log saved immediately after each disconnect, and
  matching server log lines containing the disconnect reason and preceding warp
  /subspace/error events. Do not collect identity private keys or whole saves.
- Exact automatic/manual warp, physics/on-rails, craft load/unpack and menu state.
- Separate mission-clock refusal, network timeout, message-buffer/deserialization
  failure and explicit Quit. Do not attribute every disconnect to one safeguard.
- Real Unity tests of loading a nearby craft, forced warp reductions and stock
  auto-warp overshoot. No confirmed 5 km threshold or complete fix claim yet.
