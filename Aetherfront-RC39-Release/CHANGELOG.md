# RC39

- Enforce Parallax collisions off on every join and reconnect; prevent settings
  or cached scatter flags from enabling collision processing during a session.
- Add signed, post-exit repair of an incompatible Windows KSP 1.12.5 game
  assembly. Preserve the previous file and recovery journal; verify the exact
  approved payload and release authorization before replacement.
- Keep unchanged-profile graphics intact during a runtime-only repair.

Existing mod archives are reused. Arenas, NPCs and the experimental campaign code
are not included or activated. The server remains in its existing Sandbox mode.
Older loaded updater versions must first install the updater feature; a later
Connect can prepare an assembly repair. This is not a seamless one-step repair
claim for an already-running older helper.
