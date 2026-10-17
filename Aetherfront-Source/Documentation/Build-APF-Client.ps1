param(
    [Parameter(Mandatory = $true)] [string]$KspRoot,
    [string]$FixturePath,
    [string]$ClientVersion = 'apf-client-1',
    [switch]$InstallToKsp,
    [string]$OutputDirectory,
    [string]$PrivateMasterServer,
    [string]$ClientUpdateManifestUrl,
    [string]$ClientUpdatePublicKeyPath,
    [string]$TlsCertificatePinsPath,
    [string]$DiagnosticsEndpoint,
    [string]$DiagnosticsUploadToken,
    [switch]$ReplaceFixture
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $sourceRoot
$workspaceDotnet = Join-Path $workspaceRoot '.dotnet-sdk6\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet -PathType Leaf) {
    $workspaceDotnet
} elseif (Test-Path -LiteralPath 'C:\Program Files\dotnet\dotnet.exe' -PathType Leaf) {
    'C:\Program Files\dotnet\dotnet.exe'
} else {
    'dotnet'
}

if (-not $InstallToKsp -and [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw 'Specify -InstallToKsp, -OutputDirectory, or both'
}
if (($InstallToKsp -or $OutputDirectory) -and [string]::IsNullOrWhiteSpace($PrivateMasterServer)) {
    throw 'PrivateMasterServer is required for an APF client release; pass host:port'
}
if ($ClientVersion -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*$') { throw 'ClientVersion contains unsafe characters' }
if ($PrivateMasterServer -and $PrivateMasterServer -notmatch '^\[?[A-Za-z0-9:._-]+\]?:[0-9]{1,5}$') {
    throw 'PrivateMasterServer must use host:port format'
}

function Assert-Https([string]$Value, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return }
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
        throw "$Name must be an HTTPS URL"
    }
}
Assert-Https $ClientUpdateManifestUrl 'ClientUpdateManifestUrl'
Assert-Https $DiagnosticsEndpoint 'DiagnosticsEndpoint'
if ($DiagnosticsUploadToken -and [Text.Encoding]::UTF8.GetByteCount($DiagnosticsUploadToken) -lt 24) {
    throw 'DiagnosticsUploadToken must contain at least 24 UTF-8 bytes'
}

$managedCandidates = @(
    (Join-Path $KspRoot 'KSP_x64_Data\Managed'),
    (Join-Path $KspRoot 'KSP_Data\Managed')
)
$managed = $managedCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $managed) { throw "KSP Managed directory not found beneath $KspRoot" }

$target = Join-Path $sourceRoot 'External\KSPLibraries'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$required = @(
    'Assembly-CSharp.dll', 'System.dll', 'System.Xml.dll', 'UnityEngine.dll',
    'UnityEngine.AnimationModule.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.ImageConversionModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.PhysicsModule.dll',
    'UnityEngine.TextRenderingModule.dll', 'UnityEngine.UI.dll',
    'UnityEngine.UnityWebRequestModule.dll'
)
foreach ($name in $required) {
    $path = Join-Path $managed $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Required KSP assembly missing: $path" }
    Copy-Item -LiteralPath $path -Destination (Join-Path $target $name) -Force
}

$dependencyProject = Join-Path $sourceRoot 'Build\ClientDependencies\ClientDependencies.csproj'
if (Test-Path -LiteralPath $dependencyProject) {
    & $dotnet restore $dependencyProject --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
function Copy-PackageAssembly([string]$PackageId, [string]$Version, [string]$AssemblyName, [string[]]$Destinations) {
    $existing = $Destinations | ForEach-Object { Join-Path $sourceRoot ('External\Nuget\' + $_) } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    if ($existing.Count -eq $Destinations.Count) { return }
    $packageRoot = Join-Path $nugetRoot ($PackageId.ToLowerInvariant() + "\" + $Version)
    $assembly = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter $AssemblyName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\lib\\' } | Select-Object -First 1
    if (-not $assembly) { throw "Restored package assembly missing: $PackageId $Version $AssemblyName" }
    foreach ($destination in $Destinations) {
        $path = Join-Path $sourceRoot ('External\Nuget\' + $destination)
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Copy-Item -LiteralPath $assembly.FullName -Destination $path -Force
    }
}
Copy-PackageAssembly 'CachedQuickLz' '1.3.1' 'CachedQuickLz.dll' @('CachedQuickLz.1.3.1\lib\net46\CachedQuickLz.dll')
Copy-PackageAssembly 'System.Buffers' '4.5.1' 'System.Buffers.dll' @('System.Buffers.4.5.1\lib\net461\System.Buffers.dll', 'System.Buffers.4.5.1\lib\netstandard1.1\System.Buffers.dll')
Copy-PackageAssembly 'System.Memory' '4.5.4' 'System.Memory.dll' @('System.Memory.4.5.4\lib\net461\System.Memory.dll', 'System.Memory.4.5.4\lib\netstandard1.1\System.Memory.dll')
Copy-PackageAssembly 'System.Numerics.Vectors' '4.5.0' 'System.Numerics.Vectors.dll' @('System.Numerics.Vectors.4.5.0\lib\net46\System.Numerics.Vectors.dll')
Copy-PackageAssembly 'System.Runtime.CompilerServices.Unsafe' '4.5.3' 'System.Runtime.CompilerServices.Unsafe.dll' @('System.Runtime.CompilerServices.Unsafe.4.5.3\lib\net461\System.Runtime.CompilerServices.Unsafe.dll', 'System.Runtime.CompilerServices.Unsafe.4.5.3\lib\netstandard1.0\System.Runtime.CompilerServices.Unsafe.dll')

if ($FixturePath) {
    $fixtureGameData = Join-Path $FixturePath 'GameData'
    if (-not (Test-Path -LiteralPath $fixtureGameData)) { throw "Fixture GameData not found: $fixtureGameData" }
    $fixtureTarget = Join-Path $sourceRoot 'TestFixtures\EXTI'
    if (Test-Path -LiteralPath $fixtureTarget) {
        if (-not $ReplaceFixture) { throw "Fixture target already exists; use -ReplaceFixture: $fixtureTarget" }
        $resolvedFixtureTarget = (Resolve-Path -LiteralPath $fixtureTarget).Path.TrimEnd('\')
        $expectedFixtureTarget = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'TestFixtures\EXTI')).TrimEnd('\')
        if ($resolvedFixtureTarget -ne $expectedFixtureTarget) { throw "Refusing to replace unexpected fixture target: $resolvedFixtureTarget" }
        Remove-Item -LiteralPath $resolvedFixtureTarget -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fixtureTarget -Force | Out-Null
    Copy-Item -LiteralPath $fixtureGameData -Destination (Join-Path $fixtureTarget 'GameData') -Recurse -Force
}

& $dotnet build (Join-Path $sourceRoot 'LmpClient\LmpClient.csproj') --configuration Release --nologo "/p:SolutionDir=$sourceRoot\" "/p:ApfClientVersion=$ClientVersion"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$helperOutput = Join-Path $sourceRoot 'Build\APF\APFUpdaterClient'
& $dotnet publish (Join-Path $sourceRoot 'ApfUpdater\ApfUpdater.csproj') --configuration Release --runtime win-x64 --self-contained true --output $helperOutput --nologo '/p:PublishSingleFile=true' '/p:IncludeNativeLibrariesForSelfExtract=true' '/p:PublishTrimmed=false' '/p:DebugType=None' '/p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$helperExe = Join-Path $helperOutput 'ApfUpdater.exe'
if (-not (Test-Path -LiteralPath $helperExe -PathType Leaf)) { throw "Self-contained APF updater missing: $helperExe" }

function Copy-Tree([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "Source tree missing: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $sourcePath = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $prefix = $sourcePath + '\'
    foreach ($file in Get-ChildItem -LiteralPath $sourcePath -File -Recurse) {
        $relative = $file.FullName.Substring($prefix.Length)
        $destinationPath = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }
}

function Install-ClientTree([string]$ReleaseRoot) {
    $releaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
    $harmonyDirectory = Join-Path $releaseRoot 'GameData\000_Harmony'
    $lunaDirectory = Join-Path $releaseRoot 'GameData\LunaMultiplayer'
    $pluginsDirectory = Join-Path $lunaDirectory 'Plugins'
    $dataDirectory = Join-Path $lunaDirectory 'Data'
    New-Item -ItemType Directory -Path $harmonyDirectory,$pluginsDirectory,$dataDirectory -Force | Out-Null

    # LMP patches KSP during MainSystem construction, before APF can download
    # the managed pack. Bootstrap only Harmony's runtime DLL in its canonical
    # pack root; normal pack sync installs/verifies the rest of 000_Harmony.
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'External\Dependencies\Harmony\000_Harmony\0Harmony.dll') -Destination (Join-Path $harmonyDirectory '0Harmony.dll') -Force

    $clientBuild = Join-Path $sourceRoot 'LmpClient\bin\Release'
    $pluginNames = @(
        'LmpClient.dll', 'ApfCore.dll', 'AetherfrontCore.dll', 'Lidgren.Network.dll', 'CachedQuickLz.dll',
        'JsonFx.dll', 'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll'
    )
    foreach ($name in $pluginNames) {
        $source = Join-Path $clientBuild $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Client dependency missing: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $pluginsDirectory $name) -Force
    }

    Copy-Item -LiteralPath $helperExe -Destination (Join-Path $lunaDirectory 'APFUpdater.exe') -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LunaMultiplayer.version') -Destination (Join-Path $lunaDirectory 'LunaMultiplayer.version') -Force
    Copy-Tree (Join-Path $sourceRoot 'LmpClient\Resources\Icons') (Join-Path $lunaDirectory 'Icons')
    Copy-Tree (Join-Path $sourceRoot 'LmpClient\Resources\Flags') (Join-Path $lunaDirectory 'Flags')
    Copy-Tree (Join-Path $sourceRoot 'LmpClient\Localization\XML') (Join-Path $lunaDirectory 'Localization')
    Copy-Tree (Join-Path $sourceRoot 'LmpClient\ModuleStore\XML') (Join-Path $lunaDirectory 'PartSync')
    New-Item -ItemType Directory -Path (Join-Path $lunaDirectory 'Button') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LmpClient\Resources\LMPButton.png') -Destination (Join-Path $lunaDirectory 'Button\LMPButton.png') -Force

    Set-Content -LiteralPath (Join-Path $dataDirectory 'APFClient.json') -Value ('{"clientVersion":"' + $ClientVersion + '","channel":"stable"}') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $dataDirectory 'APFMasterServer.txt') -Value $PrivateMasterServer -Encoding UTF8
    if ($ClientUpdateManifestUrl) { Set-Content -LiteralPath (Join-Path $dataDirectory 'APFClientUpdateManifestUrl.txt') -Value $ClientUpdateManifestUrl -Encoding UTF8 }
    if ($ClientUpdatePublicKeyPath) {
        if (-not (Test-Path -LiteralPath $ClientUpdatePublicKeyPath -PathType Leaf)) { throw "Client update public key not found: $ClientUpdatePublicKeyPath" }
        $publicKeyText = Get-Content -LiteralPath $ClientUpdatePublicKeyPath -Raw
        $publicKeyXml = [xml]$publicKeyText
        if ($publicKeyXml.RSAKeyValue.P -or $publicKeyXml.RSAKeyValue.Q -or $publicKeyXml.RSAKeyValue.D) { throw 'Client update key must contain public RSA parameters only' }
        Set-Content -LiteralPath (Join-Path $dataDirectory 'APFClientUpdatePublicKey.xml') -Value $publicKeyText -Encoding UTF8
    }
    if ($DiagnosticsEndpoint) { Set-Content -LiteralPath (Join-Path $dataDirectory 'APFDiagnosticsEndpoint.txt') -Value $DiagnosticsEndpoint -Encoding UTF8 }
    if ($DiagnosticsUploadToken) { Set-Content -LiteralPath (Join-Path $dataDirectory 'APFDiagnosticsUploadToken.txt') -Value $DiagnosticsUploadToken -Encoding UTF8 }
    if ($TlsCertificatePinsPath) {
        if (-not (Test-Path -LiteralPath $TlsCertificatePinsPath -PathType Leaf)) { throw "TLS certificate pins file not found: $TlsCertificatePinsPath" }
        $pins = Get-Content -LiteralPath $TlsCertificatePinsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        if (-not $pins -or ($pins | Where-Object { $_.Trim() -notmatch '^[A-Fa-f0-9]{64}$' })) { throw 'TLS certificate pins must be one SHA-256 certificate hash per line' }
        Set-Content -LiteralPath (Join-Path $dataDirectory 'APFTlsCertificatePins.txt') -Value $pins -Encoding ASCII
    }
}

if ($InstallToKsp) {
    Install-ClientTree $KspRoot
    Write-Host "Installed Aetherfront client into $KspRoot"
}

if ($OutputDirectory) {
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $packageName = 'Aetherfront-Client-' + $ClientVersion
    $stagingRoot = Join-Path $output $packageName
    if (Test-Path -LiteralPath $stagingRoot) { throw "Release staging directory already exists: $stagingRoot" }
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    Install-ClientTree $stagingRoot
    $installText = @"
Aetherfront client $ClientVersion for KSP 1.12.5 (Windows x64)

1. Start with a fresh, writable copy of your KSP installation.
2. Exit KSP.
3. Copy the GameData folder from this package into the KSP root. Merge GameData when prompted.
4. Run KSP_x64.exe normally.
5. Accept the LMP disclaimer, open the APF server list, and select the Aetherfront test server.

The APF client only lists servers from $PrivateMasterServer. It will scan GameData before joining and ask before downloading or deleting pack files. The updater verifies signed manifests and SHA-256 hashes, stages changes, exits KSP, applies the update, then relaunches KSP.

Diagnostics are opt-in. Use Diagnostics, prepare the preview, review its file list, then consent to upload. A local redacted ZIP remains under APF\Diagnostics\Outbox.
"@
    Set-Content -LiteralPath (Join-Path $stagingRoot 'INSTALL.txt') -Value $installText -Encoding UTF8
    $zipPath = Join-Path $output ($packageName + '.zip')
    if (Test-Path -LiteralPath $zipPath) { throw "Release ZIP already exists: $zipPath" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($zipPath)) -Encoding ASCII
    Write-Host "Aetherfront client package: $zipPath"
    Write-Host "SHA-256: $hash"
}

Write-Host 'Aetherfront client build succeeded.'
