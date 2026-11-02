# RC39 shipping scope

The user authorized shipping working changes now and explicitly required no
active arenas. This release includes only the mandatory collision-off policy and
signed Windows game-assembly repair. Experimental campaign, arena and NPC code
is excluded from client/server artifacts. The shipping server runtime and core
remain byte-identical to RC38; production configuration/universe are not bundled.

Build with `Build.ps1` after the session-policy and runtime-repair builds/tests.
The builder verifies the exact RC38 release, narrow method audits, version graft,
immutable mod/media reuse, coherent profile/catalog versions and absence of
experimental or private files. No arena can be activated by this package.

Scoped evidence before publication: 63 policy checks in actual KSP Mono; the
same policy hash passed 12 earlier actual-game runtime checks; 63 signed repair
transaction assertions each in CLR and actual KSP Mono. Run the existing guarded
publisher's local validation, then publication and live status readback. Complete
the short actual client update/restart/join smoke check; extensive feature/soak
testing is deferred at the user's request.

See CHANGELOG.md for player-facing changes and the older-helper bootstrap limit.
