param(
    [Parameter(Mandatory = $true)]
    [string]$KspRoot,
    [Parameter(Mandatory = $true)]
    [string]$FixturePath,
    [Parameter(Mandatory = $true)]
    [string]$OutputArchive
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $KspRoot -PathType Container)) {
    throw "KSP root not found: $KspRoot"
}
$managedCandidates = @(
    (Join-Path $KspRoot 'KSP_x64_Data\Managed'),
    (Join-Path $KspRoot 'KSP_Data\Managed')
)
$managed = $managedCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
if (-not $managed) { throw "KSP Managed directory not found beneath $KspRoot" }

$fixtureGameData = Join-Path $FixturePath 'GameData'
if (-not (Test-Path -LiteralPath $fixtureGameData -PathType Container)) {
    throw "Fixture GameData not found: $fixtureGameData"
}

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
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required KSP assembly missing: $path"
    }
}
$uniLinq = Get-ChildItem -LiteralPath $KspRoot -Recurse -File -Filter 'UniLinq.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $uniLinq) { throw "UniLinq.dll not found beneath $KspRoot" }

$archive = [System.IO.Path]::GetFullPath($OutputArchive)
$archiveParent = Split-Path -Parent $archive
New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("apf-inputs-" + [Guid]::NewGuid().ToString('N'))
try {
    $stagedManaged = Join-Path $stage 'KSP\KSP_x64_Data\Managed'
    $stagedFixture = Join-Path $stage 'EXTI\GameData'
    New-Item -ItemType Directory -Path $stagedManaged,$stagedFixture -Force | Out-Null
    foreach ($name in $required) {
        Copy-Item -LiteralPath (Join-Path $managed $name) -Destination (Join-Path $stagedManaged $name) -Force
    }
    Copy-Item -LiteralPath $uniLinq.FullName -Destination (Join-Path $stagedManaged 'UniLinq.dll') -Force
    Copy-Item -LiteralPath $fixtureGameData -Destination (Join-Path $stage 'EXTI') -Recurse -Force
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal -Force
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
Write-Host "APF client inputs exported: $archive"
