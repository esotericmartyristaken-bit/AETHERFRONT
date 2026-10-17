# Aetherfront source provenance

Source-only maintenance snapshot for `esotericmartyristaken-bit/AETHERFRONT`. The export scripts prepare and verify this snapshot; they do not initialize Git, authenticate, push, publish a release or change a running server/client. Publication and deployment are separate operator actions.

## Contents and provenance

- `Aetherfront-Source/`: available first-party Luna Multiplayer fork client, server, shared protocol, faction/career systems, updater, package/diagnostics hosts, tools, tests, and documentation.
- `KESAS-RC12-Work/`, `KESAS-RC14-Work/` through `KESAS-RC17-Work/`, and `Aetherfront-RC18-Work/` through `Aetherfront-RC22-Work/`: selected shipping-client patch lineage, installer/downloader overlays, build scripts and regression sources. RC22 adds GitHub transport, publisher and deployment tests. These are not release assets.
- `APFDiscordBot/` and `Aetherfront-Source/APFDiscordBridge/`: integration source, examples and tests, without production configuration or state.
- `KESAS-VPS-Server/`: explicitly selected deployment scripts only. They can stop/start/publish when invoked by an operator; do not run them as a build/test shortcut.
- `SOURCE-MANIFEST.json`: per-file original and staged SHA-256, role, source path, transformations, snapshot timestamp and exclusions. Paths are relative to the original workspace, not personal absolute paths.
- `BINARY-PREREQUISITES.json`: fingerprints of excluded baseline assemblies available during export. Presence is not permission to redistribute them and is not proof that a source tree exactly matches a deployed binary.
- `DEPENDENCIES.json`: exact NuGet `PackageReference` versions from the staged project files. Other assembly references remain explicit in those projects.
- `PRIVACY-SCAN.json`: redacted static-scan results, never credential values.

Upstream base: Luna Multiplayer `0.29.2`, commit `60a2ed5dbfdddc08536a98b999c781e3c12b0649`, evidenced by the sibling original checkout and existing handoff documentation. The modified available fork tree is not represented as a clean checkout of that commit. The original upstream README and license remain under `Aetherfront-Source/`.

## Build dependencies and limitations

**This snapshot alone cannot reproduce the shipping RC22 client or prove equivalence with the deployed server.** The available full fork predates later binary-based client work. The historical patchers preserve methods from excluded RC11/RC13/RC16/RC19 baseline assemblies. Recover the complete corresponding first-party source history, or obtain and privately verify the required baselines before attempting a shipping rebuild. Do not substitute an older full-tree build and call it RC22.

The current RC22 `Engine/Engine.csproj` references `KESAS-RC17-Work/Patched/ApfCore.dll`; tests reference `KESAS-RC15-Work/Inputs/JsonFx.dll`. These are not KSP game assemblies. Appropriate first-party/redistributable baseline DLLs may be recovered from independently verified public release starters after publication, where available, then checked against `BINARY-PREREQUISITES.json` and placed at the expected local paths. Typical starter entries are `GameData/LunaMultiplayer/Plugins/ApfCore.dll` and `GameData/LunaMultiplayer/Plugins/JsonFx.dll`. A newer assembly with the same filename is not an interchangeable historical baseline. This export intentionally remains source-only; public availability of a binary does not establish complete corresponding source or authorize redistribution of game dependencies.

Required toolchains:

- Windows x64 for the existing launcher, helper and embedded-Mono tests.
- .NET SDK **6.0.428**, pinned by `Aetherfront-Source/global.json` with `latestPatch` roll-forward. Server/tools target `net6.0`; shared projects also target `netstandard2.0` and `net472`.
- .NET SDK **8.0** for RC22 `net8.0` patcher/API-audit projects; existing scripts expect `tools/dotnet-sdk8/dotnet.exe`. The original machine has **8.0.424**, but no exact SDK 8 patch is pinned in the source. Historical Cecil references expect the **6.0.428** SDK's `Sdks/Microsoft.NET.ILLink.Tasks/tools/net6.0/Mono.Cecil.dll` at `tools/dotnet-sdk6/...`.
- Visual Studio 2022 / MSBuild with .NET desktop and shared-project support; .NET Framework **4.7.2**, **4.7.1** and **4.6** targeting/reference assemblies as specified by individual projects. Some projects use Microsoft.NETFramework.ReferenceAssemblies packages **1.0.3**; historical overlay projects depend on installed targeting packs.
- A separately licensed **KSP 1.12.5** installation, including its Unity and embedded Mono runtime, for client compilation/runtime validation. Provide required KSP/Unity assemblies privately at `Aetherfront-Source/External/KSPLibraries/` and the test installation at `Aetherfront-KSP-Smoke/`; neither is included. The latter path is a dependency, not a bundled game or test result.
- External `JsonFx.dll`, Harmony `0Harmony.dll`, and legacy `External/Nuget/` references are excluded. Client projects explicitly reference CachedQuickLz **1.3.1**, System.Buffers **4.5.1**, System.Memory **4.5.4**, System.Numerics.Vectors **4.5.0**, System.Runtime.CompilerServices.Unsafe **4.5.3**; inspect exact framework subpaths in `.csproj` files. Restore/acquire the correct licensed versions privately. Local dependency metadata identifies JsonFx assembly version **2.0.1209.2802** and Harmony assembly version **2.2.1.0**; these versions are not pinned by the source project files and do not identify a NuGet package version by themselves.
- Node.js **>=20**, npm and `discord.js` **14.27.0** for the optional bot; `package-lock.json` records transitive versions. Operational bot/deployment scripts also expect PowerShell 7 (`pwsh.exe`), Windows PowerShell 5.1, and configured external services. None is started by the export.

All images/icons and compiled resources are omitted. Some legacy projects reference `icon.ico` or other visual files; privately restore appropriate licensed resources or adjust those build properties. Server projects also refer to checked-in research/technology content templates (included), not live server configuration.

`LmpMasterServer/app.config` and `MasterServer/app.config` are deliberately omitted: credential-like values in those older files have ambiguous provenance and are not assumed safe merely because they appear upstream. Recreate clean application settings for development rather than restoring their undisclosed values. Machine-local `SetDirectories.bat` and `CopyToKSPDirectory.bat` are also omitted; configure a private Debug-copy workflow if required. Original files outside staging remain untouched.

After privately restoring prerequisites, commands below describe the original workflow, **not a verified clean rebuild**:

```powershell
# From Aetherfront-Source (SDK 6.0.428 applies here):
dotnet build .\Server\Server.csproj --configuration Release
dotnet build .\LmpClient\LmpClient.csproj --configuration Release "/p:SolutionDir=$PWD\"
dotnet run --project .\ApfCore.Tests\ApfCore.Tests.csproj --configuration Release
dotnet run --project .\AetherfrontCore.Tests\AetherfrontCore.Tests.csproj --configuration Release

# From the exported workspace root, only in an isolated development copy:
.\Aetherfront-RC22-Work\Build-Code.ps1
```

RC22 tests include real network/deployment probes, fixture publication and helper subprocesses. `Build-Code.ps1` invokes `Test-KspRuntime.ps1`, which needs excluded RC18/RC19 binary regression inputs. Do not run the complete suite against production. Historical `Build-Release.ps1` also needs an excluded client template, install catalog, pack archives, live-derived configuration and separately provisioned signing material. Publication scripts are retained as source only, not authorization to execute them.

No client/server build, game launch, runtime/deployment test or live service interaction was performed while preparing this export.

## Repeatable export and privacy review

`export-policy.json` is the reviewed directory/extension/file allowlist. Files are copied only from named source roots; reparse points, unlisted extensions and runtime/secret directories are rejected or skipped without traversal. The default never overwrites existing source: subsequent runs verify hashes and repeat the privacy scan without writes. Explicit `-Refresh` updates only allowlisted exported copies, retaining authored metadata documents. No original source files are changed. This supports concurrent RC22 work without claiming that an early snapshot is an immutable final release.

From the original workspace, retain this staging directory and run:

```powershell
.\Aetherfront-RC22-Work\Export-GitHubSource.ps1 -WorkspaceRoot $PWD
# After completion, verification is explicit or automatic on repeat:
.\Aetherfront-RC22-Work\Export-GitHubSource.ps1 -WorkspaceRoot $PWD -VerifyOnly

# After concurrent source changes and newly reviewed allowlist additions:
.\Aetherfront-RC22-Work\Export-GitHubSource.ps1 -WorkspaceRoot $PWD -Refresh
.\Aetherfront-GitHub-Source\scripts\Test-SourceStage.ps1 -WorkspaceRoot $PWD -CheckOriginalSources

# After replacing the staged README and moving this document to SOURCE-PROVENANCE.md:
.\Aetherfront-RC22-Work\Export-GitHubSource.ps1 -WorkspaceRoot $PWD -RefreshMetadata
.\Aetherfront-RC22-Work\Export-GitHubSource.ps1 -WorkspaceRoot $PWD -VerifyOnly
```

An identical exporter is retained in `scripts/`. A fresh export needs the reviewed metadata files in a new `Aetherfront-GitHub-Source` directory and a local verbatim LGPL 2.1 text (`-LgplLicensePath`); on this Windows setup it was copied from Git's license distribution. Paths to original sources are never followed outside the workspace. No credentials are read from excluded files. Personal workspace roots embedded in copied text become `.`; original and staged hashes make that transformation visible. Export/scan processes lower only their own priority to BelowNormal.

An interrupted, inspected staging directory can be resumed with `-ResumeIncomplete`; existing source must match the recomputed expected content. A selective update requires explicit `-RefreshSource` paths plus `-ResumeIncomplete`; `-Refresh` instead refreshes all reviewed selections. Existing completed source copies must still match their prior manifest before refresh. This does not authorize overwriting unrelated files. `-RefreshMetadata` rescans and rehashes authored metadata while requiring copied sources to remain unchanged; approved metadata paths are declared in `metadataFiles`.

The reviewed native/GitHub publishers, installed publication helper, `Test-GitHubRecovery.ps1`, PublicTransferProbe, UpdaterFixture and LiveProfileTests are explicitly allowlisted. Refresh after any further edits. The private operator reset helper and isolated licensed-game smoke fixtures are excluded; their verification boundaries are documented in `TESTING.md`. New/unlisted files are not silently included. A manifest records a snapshot, not proof of shipping-source equivalence.

The scan checks private key material, GitHub/cloud/Discord token shapes, credential-bearing URLs, literal credential assignments, private host paths, user-home paths, binary data and oversize text. Obvious placeholders are distinguished from actual values. Explicit exceptions for synthetic negative-test inputs are restricted by path, rule and matched-text SHA-256, with reasons recorded in the policy/report; no matched values are printed. Scanner pattern definitions are not credentials. Each file/rule has a two-second regex timeout; a timeout fails review rather than dropping a security check. Per-rule scan timings are recorded to identify expensive patterns safely. It is heuristic, not a guarantee: a human must review `EXPORT-REVIEW.md`, staged changes and licensing before publication. Never integrate a partially completed export or a nonzero-finding scan.

## Exclusions and licensing

No Git history, DLL/EXE/PDB/archive, paid KSP/Unity library, mod/pack payload, certificate/key file, production token/configuration, logs, universe/player/save state, diagnostics, SDK/cache or generated fixture state is included. Excluded binaries may be required privately for historical workflows; they do not belong in this repository.

The root `LICENSE` preserves Luna Multiplayer's MIT license. Bundled source includes separately attributed components, notably Lidgren (MIT) and uhttpsharp (LGPL 2.1 or later). Their notices remain in the source; see `THIRD-PARTY-NOTICES.md` and `licenses/LGPL-2.1.txt`. Do not treat the root MIT license as replacing third-party terms. Proprietary game dependencies are not licensed by this repository.
