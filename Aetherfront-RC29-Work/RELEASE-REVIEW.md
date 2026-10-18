# RC29 release review

RC28 invalidates a completed installation receipt while preparing a different
profile or client version. Cancelling leaves the installed files intact but
loses the fast reconnect proof, causing repeated full-content scans. RC29 keeps
the existing receipt during preparation. The authenticated native helper still
writes `applying` before installed mutation, and a failed apply cannot acquire
completion proof merely from a later content scan.

Repeated Windows atomic replacement failures were observed in isolated tests.
RC29 retries only sharing/lock errors 32, 33 and 1175, at most six waits totalling
525 ms. It never deletes the destination or uses a non-atomic copy fallback.
Persistent-lock tests verify that both files remain intact. The same tests pass
on Windows CLR and KSP's embedded Mono.

The genuine RC28 signed client is the baseline. The code graft changes one
client version getter and one constant field; all other 6,326 client methods and
the early startup hook remain unchanged. ApfCore stays byte-identical. The
engine is compiled against actual KSP framework assemblies and the signed
client compression assemblies, rather than SDK-selected framework versions.
`Build-Engine.ps1` records compiler and input hashes. `VersionPatcher` verifies
the signed baseline and independently reviews the graft. `Build-Release.py`
assembles exactly four native files without production signing keys.

The legacy recovery target is explicitly RC29. The exact eight `settings28`
package-version declarations and the `prepared28` preservation protocol remain
unchanged. Mod archives, profile rules, repair26/repair27 declarations, server
identity and technology data remain unchanged. Tests cover actual loaded old26
and mixed27 helpers, fresh old starters, interruption checkpoints, direct Mono
startup recovery, repeat startup, later user edits, corruption and reparse
refusals. Headless tests are distinct from actual Unity/gameplay checks.

The host handoff contains the actual self-contained .NET 8.0.29 runtime. The
historical net6.0 server projects are not proven byte-equivalent to that runtime.
This release includes no server executable/plugin payload and does not replace
server DLLs with historical builds. Local validation uses the actual runtime,
loopback ports, disposable keys and a separate empty staging universe.

Staging uses a disposable signing realm with GitHub routing disabled only in
the test catalog. Production keeps its existing signing key and GitHub routing.
Product code hashes match the production native bundle. Returning this test
client to production requires restoring its preserved public trust/server
configuration and using the normal signed updater for any catalog difference.

Deployment is permitted only after actual candidate client tests pass. Use
`APFDiscordBot/scripts/Publish-GitHubDeployment.ps1`, first with `-WhatIf`, then
the guarded host prerelease/channel workflow. Source pushes alone do not deploy.
Never force, clear deployment journals, publish a cosmetic release, or include
private handoffs, keys, full saves, configuration or universes in a release.

The legacy bot smoke check for an already-running EXTI environment is now
explicitly opt-in via `APF_RUN_LIVE_EXTI_OBSERVER=1`. Isolated test reports must
count it as skipped; the normal suite must not require a production process.

The bot lockfile also updates transitive `undici` from 6.28.0 to 6.29.1.
The isolated dependency audit reports zero findings after the update. This is
a source dependency fix, not a native-bundle payload: the current guarded native
contract cannot replace bot dependencies. Do not claim the production bot's
installed dependency version changed without a separate host dependency refresh
and readback. Discord's installed gateway implementation uses `ws`; the audited
`undici` package is imported by the REST implementation.

Actual candidate client, production deployment and post-deployment connection
results must be recorded separately before declaring this release shipped.
