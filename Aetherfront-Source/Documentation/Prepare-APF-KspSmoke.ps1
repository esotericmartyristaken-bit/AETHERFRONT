param(
    [Parameter(Mandatory = $true)][string]$SourceKspRoot,
    [Parameter(Mandatory = $true)][string]$DestinationKspRoot,
    [Parameter(Mandatory = $true)][string]$ClientPackageRoot,
    [Parameter(Mandatory = $true)][string]$PackArchiveRoot,
    [Parameter(Mandatory = $true)][string]$PackManifestPath
)

$ErrorActionPreference = "Stop"
$source = [IO.Path]::GetFullPath($SourceKspRoot).TrimEnd("\")
$destination = [IO.Path]::GetFullPath($DestinationKspRoot).TrimEnd("\")
$clientPackage = [IO.Path]::GetFullPath($ClientPackageRoot).TrimEnd("\")
$archiveRoot = [IO.Path]::GetFullPath($PackArchiveRoot).TrimEnd("\")

if (-not (Test-Path -LiteralPath (Join-Path $source "KSP_x64.exe") -PathType Leaf)) { throw "Source KSP root is invalid: $source" }
if (Test-Path -LiteralPath $destination) { throw "Smoke KSP destination already exists: $destination" }
if (-not (Test-Path -LiteralPath (Join-Path $clientPackage "GameData\LunaMultiplayer") -PathType Container)) { throw "Client package root is invalid: $clientPackage" }
if (-not (Test-Path -LiteralPath $PackManifestPath -PathType Leaf)) { throw "Pack manifest missing: $PackManifestPath" }

[IO.Directory]::CreateDirectory($destination) | Out-Null

function New-ApfHardLink([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    try {
        New-Item -ItemType HardLink -Path $Destination -Target $Source -ErrorAction Stop | Out-Null
    }
    catch {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
}

# Root files are copied because KSP can rewrite several of them. Runtime
# directories are hard-linked; they are immutable Unity/KSP binaries.
foreach ($file in Get-ChildItem -LiteralPath $source -File) {
    if ($file.Name -in @("KSP.log", "PartDatabase.cfg")) { continue }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
}

$excludedDirectories = @("GameData", "saves", "Logs", "APF", "Screenshots", "PluginData")
foreach ($directory in Get-ChildItem -LiteralPath $source -Directory) {
    if ($directory.Name -in $excludedDirectories) { continue }
    $prefix = $directory.FullName.TrimEnd("\") + "\"
    foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -Recurse -File) {
        $relative = $file.FullName.Substring($prefix.Length)
        New-ApfHardLink $file.FullName (Join-Path (Join-Path $destination $directory.Name) $relative)
    }
}

# A genuinely clean GameData base: stock assets plus the packaged APF client.
foreach ($stockRootName in @("Squad", "SquadExpansion")) {
    $stockRoot = Join-Path $source ("GameData\" + $stockRootName)
    if (-not (Test-Path -LiteralPath $stockRoot -PathType Container)) { continue }
    $prefix = $stockRoot.TrimEnd("\") + "\"
    foreach ($file in Get-ChildItem -LiteralPath $stockRoot -Recurse -File) {
        $relative = $file.FullName.Substring($prefix.Length)
        New-ApfHardLink $file.FullName (Join-Path (Join-Path $destination ("GameData\" + $stockRootName)) $relative)
    }
}

Copy-Item -LiteralPath (Join-Path $clientPackage "GameData") -Destination $destination -Recurse -Force
Copy-Item -LiteralPath "$env:WINDIR\System32\cmd.exe" -Destination (Join-Path $destination "APFSmokeRelaunch.exe") -Force

$manifest = Get-Content -LiteralPath $PackManifestPath -Raw | ConvertFrom-Json
$stagingRoot = Join-Path $destination "APF\.staging\full-pack"
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
foreach ($package in $manifest.managedPackages) {
    $archiveName = [Uri]::UnescapeDataString([IO.Path]::GetFileName(([Uri]$package.archiveUrl).AbsolutePath))
    $archivePath = Join-Path $archiveRoot $archiveName
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "Pack archive missing: $archivePath" }
    New-ApfHardLink $archivePath (Join-Path $stagingRoot $archiveName)
}

[pscustomobject]@{
    Destination = $destination
    StockRoots = @("Squad", "SquadExpansion" | Where-Object { Test-Path -LiteralPath (Join-Path $destination ("GameData\" + $_)) }).Count
    StagedPackages = @($manifest.managedPackages).Count
}
