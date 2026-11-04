#requires -Version 7.4
[CmdletBinding()]
param([int]$TimeoutSeconds = 180, [string]$PreparedRun, [switch]$PrepareOnly,
    [string]$CandidateLmp, [string]$CandidateLmpSha256,
    [string]$GraftedGround, [string]$GraftedGroundSha256 = '564cd10e83bc8b3bce298044ef9e54048616b7557651b678c1bd45519e893b58',
    [string]$WorkspaceRoot, [string]$KspRoot, [string]$BaselineClient, [string]$BaselineRelease,
    [string]$GroundSource, [string]$OrbitSource, [string]$OrbitHelper, [string]$OrbitHelperSha256)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$apf = if ($WorkspaceRoot) { [IO.Path]::GetFullPath($WorkspaceRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')) }
$allowed = Join-Path $apf 'UpdateBuilds/Launch-Trajectories-20261104'
$run = Join-Path $allowed ('unity-physics-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ'))
if ($PreparedRun) { $run = [IO.Path]::GetFullPath($PreparedRun) }
$game = Join-Path $run 'Game'
$source = if ($KspRoot) { [IO.Path]::GetFullPath($KspRoot) } else { Join-Path $apf 'Aetherfront-KSP-Smoke' }
$managed = Join-Path $source 'KSP_x64_Data/Managed'
$dotnet = Join-Path $apf 'tools/dotnet-sdk8/dotnet.exe'
$compiler = Join-Path $apf 'tools/dotnet-sdk8/sdk/8.0.424/Roslyn/bincore/csc.dll'
$client = if ($BaselineClient) { [IO.Path]::GetFullPath($BaselineClient) } else { Join-Path $apf 'UpdateBuilds/MissionTime-20261104/client-patched/LmpClient.dll' }
$groundHelper = if ($GroundSource) { [IO.Path]::GetFullPath($GroundSource) } else { Join-Path $repo 'Aetherfront-RC45-Work/ClientFixes/GroundClearance.cs' }
$groundHelperHash = (Get-FileHash -LiteralPath $groundHelper -Algorithm SHA256).Hash
$orbitSourcePath = if ($OrbitSource) { [IO.Path]::GetFullPath($OrbitSource) } else { Join-Path $repo 'Aetherfront-RC45-Work/ClientFixes/LocalOrbitTracking.cs' }
$orbitAssemblyPath = if ($OrbitHelper) { [IO.Path]::GetFullPath($OrbitHelper) } else { $null }
if (-not $GraftedGround) { $GraftedGround = Join-Path $apf 'UpdateBuilds/RC45-20261104/Candidate1/AetherfrontContinuityClient.dll' }
$GraftedGround = [IO.Path]::GetFullPath($GraftedGround)
$harmony = Join-Path $source 'GameData/000_Harmony/0Harmony.dll'
if ((Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash -ne 'e90c85067eefc8baf99589fd8625df22d373e0abcb37f4374e31a0ce14aa1b31') { throw 'Unexpected shipping client' }
if ((Get-FileHash -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -Algorithm SHA256).Hash -ne 'd9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4') { throw 'Unexpected shipping KSP managed baseline' }
if ((Get-FileHash -LiteralPath $GraftedGround -Algorithm SHA256).Hash -ne $GraftedGroundSha256) { throw 'Unexpected grafted Ground candidate' }
if ($orbitAssemblyPath -and $OrbitHelperSha256 -and (Get-FileHash -LiteralPath $orbitAssemblyPath -Algorithm SHA256).Hash -ne $OrbitHelperSha256) { throw 'Unexpected explicit orbit donor' }
if ($CandidateLmp -and ((-not $CandidateLmpSha256) -or (Get-FileHash -LiteralPath $CandidateLmp -Algorithm SHA256).Hash -ne $CandidateLmpSha256)) { throw 'Provide exact candidate client SHA256' }
if (-not $run.StartsWith($allowed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output escapes allowed private directory' }
if ((Test-Path -LiteralPath $run) -and -not $PreparedRun) { throw 'Use a fresh private directory' }
if ($PreparedRun -and -not (Test-Path -LiteralPath (Join-Path $game 'KSP_x64.exe'))) { throw 'Prepared private player missing' }
if (-not $PreparedRun) {
$null = New-Item -ItemType Directory -Path $game
$null = New-Item -ItemType Directory -Path (Join-Path $run 'inputs')
$pluginDirectory = Join-Path $game 'GameData/PhysicsTests/Plugins'
$null = New-Item -ItemType Directory -Path (Join-Path $game 'GameData/Squad') -Force
$null = New-Item -ItemType Directory -Path $pluginDirectory

# Independent byte copies, not shared hardlinks/junctions: game writes cannot
# reach the dedicated fixture. Do not copy GameData, saves, launchers or updater.
foreach ($name in @('KSP_x64.exe','UnityPlayer.dll','Physics.cfg','buildID64.txt')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $game $name)
}
foreach ($name in @('KSP_x64_Data','MonoBleedingEdge')) {
    & robocopy (Join-Path $source $name) (Join-Path $game $name) /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /XJ /NFL /NDL /NJH /NJS
    if ($LASTEXITCODE -gt 7) { throw "Private runtime copy failed: $name" }
}
}
$pluginDirectory = Join-Path $game 'GameData/PhysicsTests/Plugins'
$null = New-Item -ItemType Directory -Path (Join-Path $game 'GameData/Squad') -Force
$attempt = Join-Path $run ('attempt-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ'))
$null = New-Item -ItemType Directory -Path $attempt
$release = if ($BaselineRelease) { [IO.Path]::GetFullPath($BaselineRelease) } else { Join-Path $apf 'UpdateBuilds/RC44-20261104/release/Aetherfront-Release-kesas-rc44.zip' }
$outer = [IO.Compression.ZipFile]::OpenRead($release)
try {
    $memory = [IO.MemoryStream]::new(); $outer.GetEntry('ClientTemplate.zip').Open().CopyTo($memory); $memory.Position = 0
    $inner = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
    try {
        foreach ($entry in $inner.Entries | Where-Object { $_.FullName -match '^GameData/LunaMultiplayer/Plugins/[^/]+\.dll$' }) {
            $output = Join-Path $run ('inputs/' + [IO.Path]::GetFileNameWithoutExtension($entry.Name) + '.bin')
            $stream = [IO.File]::Create($output)
            try { $entry.Open().CopyTo($stream) } finally { $stream.Dispose() }
        }
    } finally { $inner.Dispose(); $memory.Dispose() }
} finally { $outer.Dispose() }
if ((Get-FileHash -LiteralPath (Join-Path $run 'inputs/LmpClient.bin') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash) { throw 'RC44 nested payload differs from pinned input' }
Copy-Item -LiteralPath $GraftedGround -Destination (Join-Path $run 'inputs/AetherfrontContinuityClient.bin') -Force
$selectedClientHash = (Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash.ToLowerInvariant()
if ($CandidateLmp) {
    Copy-Item -LiteralPath $CandidateLmp -Destination (Join-Path $run 'inputs/LmpClient.bin') -Force
    $selectedClientHash = $CandidateLmpSha256.ToLowerInvariant()
}
$snapshots = Join-Path $attempt 'sources'
$null = New-Item -ItemType Directory -Path $snapshots
foreach ($name in @('TrackingEligibility.cs','UnityTrackingProbe.cs','GroundClearanceProbe.cs','PrefixGuardProbe.cs','Run.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $snapshots $name)
}
Copy-Item -LiteralPath $groundHelper -Destination (Join-Path $snapshots 'GroundClearance.cs')
Copy-Item -LiteralPath $orbitSourcePath -Destination (Join-Path $snapshots 'LocalOrbitTracking.cs')
Copy-Item -LiteralPath (Join-Path $repo 'Aetherfront-RC45-Work/ClientFixes/LocalOrbitTracking.csproj') -Destination (Join-Path $snapshots 'LocalOrbitTracking.csproj')
$references = @('mscorlib.dll','System.dll','System.Core.dll','Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll') |
    ForEach-Object { '-r:' + (Join-Path $managed $_) }
$references += '-r:' + $harmony
if (-not $orbitAssemblyPath) {
    $orbitAssemblyPath = Join-Path $attempt 'AetherfrontLocalOrbitTracking.dll'
    & $dotnet $compiler -nologo -noconfig -nostdlib -langversion:10 -target:library -optimize+ -deterministic+ "-out:$orbitAssemblyPath" @references "-r:$client" (Join-Path $snapshots 'LocalOrbitTracking.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Production orbit helper compilation failed' }
}
Copy-Item -LiteralPath $orbitAssemblyPath -Destination (Join-Path $run 'inputs/AetherfrontLocalOrbitTracking.bin') -Force
Copy-Item -LiteralPath $harmony -Destination (Join-Path $pluginDirectory '0Harmony.dll') -Force
$plugin = Join-Path $pluginDirectory 'PhysicsTests.dll'
& $dotnet $compiler -nologo -noconfig -nostdlib -langversion:latest -target:library -optimize+ "-out:$plugin" @references (Join-Path $snapshots 'TrackingEligibility.cs') (Join-Path $snapshots 'UnityTrackingProbe.cs') (Join-Path $snapshots 'GroundClearanceProbe.cs') (Join-Path $snapshots 'PrefixGuardProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native Unity probe compilation failed' }
if ((Get-FileHash -LiteralPath $groundHelper -Algorithm SHA256).Hash -ne $groundHelperHash) { throw 'Candidate source changed during compilation; rerun' }
$inputs = @($client, (Join-Path $managed 'Assembly-CSharp.dll'), (Join-Path $source 'UnityPlayer.dll'), $plugin, $groundHelper, $harmony, $orbitAssemblyPath, $GraftedGround) + @(Get-ChildItem -LiteralPath $snapshots -File | Select-Object -ExpandProperty FullName)
$inputs = $inputs |
    ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; [pscustomobject]@{path=$hash.Path;sha256=$hash.Hash.ToLowerInvariant()} }
[IO.File]::WriteAllText((Join-Path $attempt 'inputs.json'), (ConvertTo-Json -InputObject @($inputs) -Depth 4))
Copy-Item -LiteralPath $plugin -Destination (Join-Path $attempt 'PhysicsTests.dll')
Copy-Item -LiteralPath $GraftedGround -Destination (Join-Path $attempt 'AetherfrontContinuityClient.dll')
if ($orbitAssemblyPath -ne (Join-Path $attempt 'AetherfrontLocalOrbitTracking.dll')) { Copy-Item -LiteralPath $orbitAssemblyPath -Destination (Join-Path $attempt 'AetherfrontLocalOrbitTracking.dll') }
if ($CandidateLmp) { Copy-Item -LiteralPath $CandidateLmp -Destination (Join-Path $attempt 'LmpClient.dll') }
if ($PrepareOnly) { Write-Output "PREPARED $attempt No KSP process launched"; return }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $game 'KSP_x64.exe'
$start.WorkingDirectory = $game
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.Environment['AF_PHYSICS_RUN_ROOT'] = $run
$start.Environment['AF_PHYSICS_EVIDENCE'] = $attempt
$start.Environment['AF_PHYSICS_CLIENT_SHA'] = $selectedClientHash
foreach ($argument in @('-batchmode','-nographics','-logFile',(Join-Path $attempt 'Unity.log'))) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
Write-Output "RUN $attempt PID $($process.Id)"
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill(); $process.WaitForExit()
    throw "Private Unity probe timed out; inspect $attempt/Unity.log"
}
Write-Output "EXIT $($process.ExitCode)"
$result = Join-Path $attempt 'unity-results.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
else { throw "No test result; inspect $attempt/Unity.log" }
if ($process.ExitCode -ne 0) { throw 'Private Unity probe failed' }
Write-Output "RESULTS $attempt"
