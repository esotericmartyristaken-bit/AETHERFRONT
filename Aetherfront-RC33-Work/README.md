# RC33 candidate

Personal science, funds and technology are separate from faction membership.
An earned science award adds its full value to the earner and an additional
50% to every other account. Offline/new accounts receive their historical
entitlement lazily. Starting/shared science is research-only. Trades move
personally earned science without generating another award. Factions retain
vessel ownership, leases, permissions and their other social features.

Subject awards use durable per-account cumulative high-water marks, a generated
server catalog and server-owned multipliers. Dynamic source UIDs and explicitly
unscaled experiments retain their KSP semantics. Labs/mods without a subject use
durable immutable reward receipts; the client persists a bounded retry outbox.
These are accounting/replay controls, not proof that an untrusted modified game
physically performed an experiment. Science and Sandbox are the target modes;
Career funds/reputation earning is outside this change.

The compatibility addon limits Kerbal Konstructs launch placement to the active
vessel's matching launch origin and its first off-rails placement. The MM patch
leaves Scatterer in charge of shadows when installed. Initial stock 1280x720
display defaults become native monitor resolution (1920x1080 fallback), while
custom settings remain intact. PAPI is absent from both catalogs. Potato keeps
the existing complete plume archives and reduced optional scenery.

## Builds and gates

`Build-Progression.ps1` grafts only authored types/hooks into fingerprinted
actual client and .NET 8 server baselines. Historical net6 projects are not used
as replacements for the deployed runtime. `Development/ProgressionPatch`
reviews original method images and performs the separate RC33 version graft.
`Build-Engine.ps1` retains the RC31 installer except for the explicitly reviewed
legacy recovery target. It needs the licensed KSP Managed references and an
explicit Roslyn compiler path. `Build-Release.py` verifies the production RC31
signature/archive before assembling a keyless native bundle. Production signing
keys stay on the host; publish only through its guarded deployment workflow.

Tests are under `Development/PersonalProgressionTests`, `PersonalServerTests`,
`PersonalUdpTests` and `SettingsTests`. Catalog builders consume exports from
the actual loaded game, not guessed technology/science definitions. Keep
headless ledger/RPC proof, Unity gameplay, skipped-version migration and live
deployment readback as separate gates. Thin identical-profile local fixtures
cannot establish a successful Regular/Potato switch or a vanilla pack install.
Never package test drivers, fixtures, state, saves, identities or private keys.
