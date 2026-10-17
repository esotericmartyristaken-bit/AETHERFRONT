# APF managed packs

APF mode is opt-in and leaves ordinary LMP installs unchanged.

## Server

### Build a pack from a GameData fixture

The source tree includes `ApfPackTool` and `Build-APF-Pack.ps1`. It creates one
SHA-256-verified archive per top-level mod root, emits `APFPack.json`, and
requires explicit client-only roots. Example:

```powershell
.\Build-APF-Pack.ps1 `
  -SourceGameData 'C:\KSP-APF\GameData' `
  -OutputDirectory 'C:\APF-Packs\apf-main-1.0.0' `
  -ServerId 'apf-test' -PackId 'apf-main' -PackVersion '1.0.0' `
  -KspVersion '1.12.5' -ArchiveBaseUrl 'https://cdn.example.com/apf/1.0.0/' `
  -ClientOnlyRoot 'GraphicsMod'
```

The builder refuses unknown loose files directly beneath `GameData` (the known
ModuleManager generated cache/hash files are retained as protected state),
output inside the source `GameData`, empty mod roots, non-HTTPS archive URLs,
and invalid client-only paths. CKAN is not required; CKAN metadata/import
remains an optional future admin feature.

If the KSP clone and EXTI fixture are on another Windows PC, export only the
client compile references and fixture with:

    .\Documentation\Export-APF-ClientInputs.ps1 -KspRoot 'C:\KSP' -FixturePath 'C:\APF\EXTI' -OutputArchive 'C:\APF\APF-ClientInputs.zip'

Copy the archive into this workspace, import it, then build the client:

    .\Documentation\Import-APF-ClientInputs.ps1 -InputArchive .\APF-ClientInputs.zip
    .\Documentation\Build-APF-Client.ps1 -KspRoot .\ExternalInput\KSP -FixturePath .\ExternalInput\EXTI -ReplaceFixture

The export contains no saves, executable, or full KSP install: only DLLs
needed for compilation plus EXTI\GameData.

Publish the generated archives and manifest at the exact HTTPS URLs, then copy
the manifest to the server `Config` directory and put its HTTPS URL in
`Config/APFManifestUrl.txt`.

For the bundled static host, local smoke mode is:

```powershell
.\Documentation\Start-APFPackHost.ps1 `
  -RootDirectory C:\APF-Packs\apf-main-1.0.0
```

This intentionally advertises `https://localhost:18443`, which is valid only
for the same machine. For a LAN/public deployment, bind Kestrel separately and
publish the same reachable URL into the manifest/server config:

```powershell
.\Documentation\Start-APFPackHost.ps1 `
  -RootDirectory C:\APF-Packs\apf-main-1.0.0 `
  -ListenUrl https://0.0.0.0:18443 `
  -CertificatePath C:\APF-Secrets\pack-host.pfx `
  -CertificatePassword '<secret>'

.\Documentation\Prepare-APFLocalPack.ps1 `
  -PackDirectory C:\APF-Packs\apf-main-1.0.0 `
  -ServerDirectory C:\APF\LMPServer-Fork `
  -HostUrl https://packs.example.com:18443 `
  -Force
```

`ListenUrl` controls the local socket; `HostUrl` is the URL clients actually
use. The certificate must be trusted by every client and cover that hostname
or IP. Forward the pack-host TCP port, the APF master endpoint, and the game
UDP port separately; HTTPS pack hosting alone does not make the game server
reachable. The pack host exposes only `APFPack.json` and `.zip` artifacts.

Place these files in the server `Config` directory:

- `APFPack.json`: versioned manifest.
- `APFManifestUrl.txt`: HTTPS URL serving the same manifest.

An APF server must also use a private master endpoint through
`Config/DebugSettings.xml` → `CustomMasterServer`. Start the forked master with
`/apfonly`; it lists only APF-tagged registrations and does not query the public
LMP master/dedicated-server lists.

Package `destination` is the final directory below `GameData`; use a package
root such as `GameData/ExampleMod`. `stripPrefix` is the directory removed from
the archive. Archives are HTTPS + SHA-256 verified and may not contain
executables or traversal paths.

Optional `requiredDlc` entries are GameData-relative roots, e.g.
`SquadExpansion/MakingHistory`; the APF client blocks joins when any declared
DLC root is absent.

The normal `LMPModControl.xml` remains active for parts, resources, and runtime
plugin policy. APF pack sync only manages files.

For separated career groundwork, an APF server may add `Config/APFCareer.json`
using `Documentation/APFCareer.example.json`. The profile ID, progression schema,
save namespace, starting values, and pack-change reset policy are validated at
startup and the validated profile is carried in the APF handshake. The client
maps each profile to an isolated local `saves/LunaMultiplayer-APF-<profile>`
folder; vanilla/public LMP keeps `saves/LunaMultiplayer`. When
`resetOnPackChange` is true, the validated pack ID/version are included in that
namespace so a pack change starts a separate local progression folder. In APF
mode, the Aetherfront ledger is authoritative and legacy LMP ShareProgress
mutations are rejected; the client only mirrors approved faction values into
the local KSP UI. Vanilla/public LMP keeps its original shared scenario
behavior. Separate server `Universe` directories already provide hard server
state isolation. APF startup also defers creation of the ordinary
public-LMP blank save until the APF handshake selects its namespace.

If manifest retrieval, validation, or staging fails, the APF sync window keeps
the join blocked and exposes a user-triggered `Retry`; it never restarts KSP in
an automatic retry loop. APF craft-library reads/writes follow the selected
profile namespace as well as `persistent.sfs`.

For a local server/protocol smoke run, build the projects first and execute
`Documentation/Run-APF-Smoke.ps1`. It starts the APF-only master and forked
server, runs the gated handshake test, and stops both processes afterward.
For the complete repeatable local suite, execute
`Documentation/Run-APF-Exhaustive.ps1`; it also tests the signed update tool,
real updater process, rollback paths, script syntax, and process/port cleanup.

### Install a built pack into the fork server

After publishing the generated archives and manifest at the exact HTTPS URLs,
automate the server manifest copy and private-master XML edit with:

    .\Documentation\Install-APFServerPack.ps1 -ServerDirectory 'C:\APF\LMPServer-Fork' -PackDirectory 'C:\APF-Packs\apf-main-1.0.0' -ManifestUrl 'https://cdn.example.com/apf/1.0.0/APFPack.json' -PrivateMasterServer 'master.example.com:8700'

### Publish APF artifacts

Publish the server, private master, updater, pack-tool, and client-update-tool outputs together. Optional sync targets receive files only; existing server `Config`, `Universe`, and logs are preserved:

    .\Documentation\Publish-APFArtifacts.ps1 -SyncServerDirectory 'C:\APF\LMPServer-Fork' -SyncMasterDirectory 'C:\APF\APFMasterServer'

## Client

Install the APF client marker at:

`GameData/LunaMultiplayer/Data/APFClient.json`

Put the private master endpoint in:

`GameData/LunaMultiplayer/Data/APFMasterServer.txt`

Client creates `GameData/LunaMultiplayer/Data/APFHelper.key` on first accepted
sync. The transaction passed to `APFUpdater.exe` is HMAC-authorized with this
local key; unsigned or forged transaction files are rejected.

The client then filters out non-APF servers, obtains the selected manifest,
shows one combined install/delete consent screen, stages archives outside
`GameData`, and delegates file replacement to `APFUpdater.exe` after KSP exits.

The client installs at `GameData/LunaMultiPlayer/Plugins/LmpClient.dll` and
`GameData/LunaMultiPlayer/Plugins/ApfCore.dll`; `APFUpdater.exe` remains at
`GameData/LunaMultiPlayer/APFUpdater.exe`, with its helper-side
`GameData/LunaMultiPlayer/ApfCore.dll` dependency. The updater stays stable while a
client update is applied, so it can replace the loaded client assembly after
KSP exits.

### Signed APF client self-updates

Self-updates are opt-in and fail closed. Generate a release keypair once and
keep the private XML off the client/server web root:

```powershell
dotnet run --project .\ApfUpdateTool\ApfUpdateTool.csproj --configuration Release -- `
  --generate-key --private-output C:\APF-Secrets\apf-client-private.xml `
  --public-output C:\APF-Secrets\apf-client-public.xml
```

Stage a built client under `GameData/LunaMultiPlayer/Plugins`, then build the
signed update:

```powershell
.\Build-APF-ClientUpdate.ps1 `
  -ClientRoot C:\APF-ClientRoot `
  -OutputDirectory C:\APF-Packs\apf-client-2 `
  -Version apf-client-2 `
  -PackageBaseUrl https://cdn.example.com/apf/client-2/ `
  -PrivateKeyXml C:\APF-Secrets\apf-client-private.xml `
  -KspVersion 1.12.5
```

Publish `APFClientUpdate.json` and its ZIP over HTTPS. Install the matching
URL and public key into a clone with `Build-APF-Client.ps1`. The version is
embedded in `LmpClient.dll`, preventing a self-update loop after replacement:

```powershell
.\Build-APF-Client.ps1 -KspRoot C:\KSP-APF -ClientVersion apf-client-1 -InstallToKsp `
  -PrivateMasterServer master.example.com:8700 `
  -ClientUpdateManifestUrl https://cdn.example.com/apf/client-2/APFClientUpdate.json `
  -ClientUpdatePublicKeyPath C:\APF-Secrets\apf-client-public.xml
```

The client verifies HTTPS, the pinned RSA signature, archive SHA-256, exact
file hashes/sizes, and an allowlist containing only `LmpClient.dll` and
`ApfCore.dll`. It asks once, stages outside `GameData`, exits KSP, lets the
helper apply an authorized transaction, then relaunches the original
executable/arguments. Replacement has rollback handling; no downloaded
program is executed.

APF authentication is two-phase: the server keeps the connection unauthenticated
until the client reports a clean scan. The APF wire channel defines
`PackManifestRequest`, `PackManifestReply`, `PackStatusReport`,
`PackSyncRequired`, and `PackSyncAccepted`; only then does normal LMP handshake
and `LMPModControl.xml` validation continue.

The helper never executes archive contents. It accepts only a local transaction,
rejects protected roots/path traversal/collisions, verifies archive SHA-256, and
writes only to the clone's exact `GameData` directory. Deletes approved in the
single consent screen are permanent; save backups are intentionally not made.
It preflights read-only targets before deletion and records start/completion
state in `APF/APFTransaction.log`.

The referenced KSP fixture is required to compile/deploy the Unity client;
without extracted KSP assemblies only APF core, updater, protocol, and server
builds can be verified. After receiving a KSP install plus fixture, run
`Documentation/Build-APF-Client.ps1 -KspRoot <KSP> -FixturePath <EXTI> -InstallToKsp
-PrivateMasterServer <host:port>`; it copies reference assemblies, builds, and
restores the pinned legacy dependencies, installs `LmpClient.dll`,
`ApfCore.dll`, `APFUpdater.exe`, and the APF marker into the clone.
