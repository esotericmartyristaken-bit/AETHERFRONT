# RC53 verification record

Observed 2026-11-09 on the local controller. Deployment and live production
acceptance must be recorded separately; this document does not assert they ran.

- 31 storage assertions passed, including manual compare-and-swap, checksum
  validation, rolling 1/5/30-minute selection, pinned launch, 32-sample rotation,
  the 128 MiB compressed quota, abandoned staging cleanup and completed restore
  journal/pinned-image cleanup.
- 269 integration assertions passed across normal restore, interrupted world,
  registry, clock and receipt writes, owned frozen crew recovery and preservation
  of another player's newer cryopod state. Tests load the actual patched RC52
  assemblies, call the native checkpoint command dispatch and clock resume hooks,
  and retain banked personal/shared science. Unavailable optional history returns
  a useful error while keeping the player connected. Two personally owned craft are
  restored while another player's craft and clock stay current.
- 91 checks passed under KSP's actual embedded Mono runtime: shipping client
  checkpoint methods JIT, stock quicksave target signatures exist, valid metadata
  parses without Unity-thread access, and the receipt queue stays bounded.
- Actual temporary KSP reached MAINMENU after part compilation, rendered all
  five choices and confirmation text, saved a screenshot and quit itself normally.
  No checkpoint initialization error appeared in that launch's KSP log. An old
  local arena test DLL caused unrelated compatibility warnings; that unshipped
  DLL and the test-only rendering probe were removed afterward. This was a
  disabled render fixture, not authenticated Unity flight acceptance.
- Eleven network assertions passed with two RSA-authenticated synthetic clients
  against the actual .NET 8.0.29 server process bound to loopback. The native
  proto reader pinned a launch checkpoint, revisiting an older craft with stale
  launch metadata preserved it, manual save persisted, resources were
  changed, and restore disconnected only the loading pilot. Reconnecting restored
  that pilot's saved time and resources, published one restore receipt, and left
  the observer connected with time advancing. The completed test vessel was
  removed normally. No real player identity was used.
- A 12-craft storage fixture totaling 1,912,680 raw bytes saved in approximately
  19.5 ms median and 20.2 ms maximum of twelve samples on this PC. Automatic
  compression and retention execute outside the fleet gate. This measures local
  storage performance, not production host or full Unity simulation performance.

The assembly graft audits verify unchanged shipping identity, resources and
unrelated existing method bodies. Release assembly verifies that unchanged mod
archives, native dependencies and configuration bytes are retained. Arenas stay
disabled. Reported historical client losses without supplied logs have not been
attributed conclusively; checkpoint recovery does not prove every underlying mod
or network problem has been resolved.
