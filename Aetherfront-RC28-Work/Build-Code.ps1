#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EngineResultPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$EngineResultSha256,
    [string]$UnityManaged = (Join-Path $PSScriptRoot '../Aetherfront-KSP-Smoke/KSP_x64_Data/Managed')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$workspace = Split-Path $work
$server = Join-Path $workspace 'KESAS-VPS-Server'
$utf8 = [Text.UTF8Encoding]::new($false)
function Plain([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $cursor" }
    }
    return $full
}
function Owned([string]$Path) {
    $full = Plain $Path
    if (!$full.StartsWith($work + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Output/input escapes RC28 work directory: $full" }
    return $full
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath (Plain $Path) -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Hash([string]$Path,[string]$Expected) {
    if ($Expected -cnotmatch '^[a-fA-F0-9]{64}$' -or (Hash $Path) -ine $Expected) { throw "SHA256 mismatch: $Path" }
}
function Write-New([string]$Path,[string]$Text) {
    $stream = [IO.FileStream]::new((Owned $Path),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes = $utf8.GetBytes($Text); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Resolve-EnginePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Empty Engine result path.' }
    if (![IO.Path]::IsPathRooted($Path)) { $Path = Join-Path ([IO.Path]::GetDirectoryName($EngineResultPath)) $Path }
    Owned $Path
}
$EngineResultPath = Owned $EngineResultPath
Assert-Hash $EngineResultPath $EngineResultSha256
$engine = Get-Content -LiteralPath $EngineResultPath -Raw | ConvertFrom-Json
if ($engine.schemaVersion -ne 1 -or $engine.version -cne 'kesas-rc28' -or $engine.completed -isnot [bool] -or !$engine.completed) { throw 'Use the FINAL completed schemaVersion 1 RC28 ENGINE-RESULT.json.' }
$enginePath = Resolve-EnginePath ([string]$engine.enginePath)
$baseline = Resolve-EnginePath ([string]$engine.baselinePath)
Assert-Hash $enginePath ([string]$engine.engineSha256)
$feedPath = Plain "$server/Updates/kesas-rc27/APFClientUpdate.json"
$activeFeedPath = Plain "$server/Updates/APFClientUpdate.json"
$archive = Plain "$server/Updates/kesas-rc27/APFClient-kesas-rc27.zip"
$publicKey = Plain "$server/secrets/update-signing-public.xml"
$verifier = Plain "$server/UpdateTool/ApfUpdateTool.exe"
Assert-Hash $feedPath ([string]$engine.baselineFeedSha256)
Assert-Hash $activeFeedPath ([string]$engine.baselineFeedSha256)
Assert-Hash $archive ([string]$engine.baselineArchiveSha256)
Assert-Hash $verifier '215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30'
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
if ($feed.version -cne 'kesas-rc27' -or @($feed.files).Count -ne 464) { throw 'Expected current signed RC27 inventory of 464 files.' }
$sourceRecords = @($engine.sources)
if ($sourceRecords.Count -eq 0) { throw 'Engine freeze source manifest is empty.' }
$sourcePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($source in $sourceRecords) {
    $sourcePath = Plain ([string]$source.path)
    if (!$sourcePaths.Add($sourcePath)) { throw "Duplicate frozen Engine source: $sourcePath" }
    Assert-Hash $sourcePath ([string]$source.sha256)
}
# Verify every declared source, every local Engine source, and all linked
# compilation inputs against the final Engine owner's frozen manifest.
$requiredSources = @(Get-ChildItem -LiteralPath "$work/Engine" -File | ForEach-Object FullName)
$linkedSources = @(
    "$workspace/Aetherfront-RC22-Work/Engine/Transfer.cs",
    "$workspace/Aetherfront-RC22-Work/Engine/DownloadFiles.cs",
    "$workspace/Aetherfront-RC22-Work/Engine/GitHub.cs"
)
foreach ($source in $requiredSources) { if (!$sourcePaths.Contains((Plain $source))) { throw "ENGINE-RESULT.json must freeze current source: $source" } }
$unfrozenLinkedSources = @($linkedSources | Where-Object { !$sourcePaths.Contains((Plain $_)) })
if ($unfrozenLinkedSources.Count -ne 0) { throw ('ENGINE-RESULT.json must freeze linked sources: ' + ($unfrozenLinkedSources -join ', ')) }
$UnityManaged = Plain $UnityManaged
$dotnet = Plain "$workspace/tools/dotnet-sdk8/dotnet.exe"
$cecil = Plain "$workspace/tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net6.0/Mono.Cecil.dll"
$fastLoaderProofPath = Owned "$work/FastLoader-Ordering-Proof.json"
$fastLoaderProofSha256 = '2d036f89fece787dd7dd09ff373bf316af18215c51305484ea7bd93ab01f4939'
Assert-Hash $fastLoaderProofPath $fastLoaderProofSha256
foreach ($path in @($dotnet,$cecil,"$UnityManaged/Assembly-CSharp.dll","$UnityManaged/UnityEngine.CoreModule.dll")) { if (![IO.File]::Exists($path)) { throw "Required local tooling/reference missing: $path" } }
$inputs = @($EngineResultPath,$enginePath,$feedPath,$activeFeedPath,$archive,$publicKey,$verifier,$PSCommandPath,
    "$work/VersionPatcher/Program.cs","$work/VersionPatcher/VersionPatcher.csproj",$cecil,"$UnityManaged/Assembly-CSharp.dll",$fastLoaderProofPath) + @($sourcePaths) + $linkedSources
$snapshots = @(foreach ($path in $inputs) { [ordered]@{path=(Plain $path);sha256=(Hash $path)} })
$attemptName = 'Code-' + [Guid]::NewGuid().ToString('N')
$pending = Owned "$work/BuildAttempts/.incomplete-$attemptName"
$complete = Owned "$work/BuildAttempts/$attemptName"
if ((Test-Path -LiteralPath $pending) -or (Test-Path -LiteralPath $complete)) { throw 'Immutable Code attempt already exists.' }
New-Item -ItemType Directory -Path "$pending/Engine" | Out-Null
$previous = @{}
$environment = @{
    DOTNET_CLI_HOME="$pending/cli";DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true';MSBUILDDISABLENODEREUSE='1';NUGET_PACKAGES="$pending/packages"
    NUGET_HTTP_CACHE_PATH="$pending/nuget-http";NUGET_PLUGINS_CACHE_PATH="$pending/nuget-plugins"
}
foreach ($name in $environment.Keys) { $previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process');[Environment]::SetEnvironmentVariable($name,$environment[$name],'Process') }
try {
    Write-New "$pending/NuGet.Config" '<configuration><packageSources><clear /></packageSources></configuration>'
    & $dotnet build "$work/VersionPatcher/VersionPatcher.csproj" -c Release --nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false -p:MSBuildEnableWorkloadResolver=false "-p:RestoreConfigFile=$pending/NuGet.Config" "-p:OutputPath=$pending/Tool/" "-p:BaseIntermediateOutputPath=$pending/obj-tool/"
    if ($LASTEXITCODE -ne 0) { throw 'RC28 VersionPatcher compilation failed.' }
    [IO.File]::Copy($enginePath,"$pending/Engine/KesasInstall.dll",$false)
    Assert-Hash "$pending/Engine/KesasInstall.dll" ([string]$engine.engineSha256)
    $arguments = @($feedPath,$archive,$publicKey,$verifier,$baseline,"$pending/Engine/KesasInstall.dll",$UnityManaged,"$pending/Patched",[string]$engine.baselineFeedSha256,[string]$engine.baselineArchiveSha256,[string]$engine.engineSha256)
    & $dotnet "$pending/Tool/VersionPatcher.dll" patch @arguments
    if ($LASTEXITCODE -ne 0) { throw 'RC28 authenticated client graft failed.' }
    & $dotnet "$pending/Tool/VersionPatcher.dll" review @arguments
    if ($LASTEXITCODE -ne 0) { throw 'RC28 independent method/field/core/startup review failed.' }
    foreach ($inputFile in $snapshots) { Assert-Hash $inputFile.path $inputFile.sha256 }
    $currentSources = @(Get-ChildItem -LiteralPath "$work/Engine" -File | ForEach-Object { Plain $_.FullName })
    if (@($currentSources | Where-Object { !$sourcePaths.Contains($_) }).Count -ne 0 -or @($requiredSources | Where-Object { ![IO.File]::Exists($_) }).Count -ne 0) { throw 'Engine source inventory changed during graft.' }
    $result = [ordered]@{
        schemaVersion=1;version='kesas-rc28';completed=$true;baselineVersion='kesas-rc27'
        enginePath="$complete/Engine/KesasInstall.dll";engineSha256=(Hash "$pending/Engine/KesasInstall.dll")
        clientPath="$complete/Patched/LmpClient.dll";clientSha256=(Hash "$pending/Patched/LmpClient.dll")
        corePath="$complete/Patched/ApfCore.dll";coreSha256=(Hash "$pending/Patched/ApfCore.dll")
        baselineFeedSha256=(Hash $feedPath);baselineArchiveSha256=(Hash $archive)
        auditPath="$complete/Patched/audit.json";auditSha256=(Hash "$pending/Patched/audit.json")
        engineResultPath=$EngineResultPath;engineResultSha256=(Hash $EngineResultPath);sources=$snapshots
        fastLoaderProofPath=$fastLoaderProofPath;fastLoaderProofSha256=$fastLoaderProofSha256
        engineDeclaredSourcesVerified=$true;linkedSourcesAbsentFromEngineManifest=$unfrozenLinkedSources
        coreModified=$false;networkUsed=$false;productionWrites=$false
    }
    Write-New "$pending/CODE-RESULT.json" ((ConvertTo-Json -InputObject $result -Depth 30) + "`n")
    Move-Item -LiteralPath $pending -Destination $complete
    "CODE-RESULT: $complete/CODE-RESULT.json"
} finally { foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') } }
