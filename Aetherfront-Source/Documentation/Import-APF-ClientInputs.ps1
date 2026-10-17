param(
    [Parameter(Mandatory = $true)]
    [string]$InputArchive,
    [string]$DestinationRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ExternalInput'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $InputArchive -PathType Leaf)) {
    throw "Input archive not found: $InputArchive"
}

$destination = [System.IO.Path]::GetFullPath($DestinationRoot)
if (Test-Path -LiteralPath $destination) {
    if (-not $Force) { throw "Destination exists; use -Force only for an intentional replacement: $destination" }
    $resolved = (Resolve-Path -LiteralPath $destination).Path
    if ([string]::IsNullOrWhiteSpace($resolved) -or $resolved -eq [System.IO.Path]::GetPathRoot($resolved)) {
        throw "Refusing to replace broad destination: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = (Resolve-Path -LiteralPath $InputArchive).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $rootWithSeparator = $destination.TrimEnd('\') + '\'
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('/', '\')
        $name = $name.TrimEnd('\')
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name.StartsWith('\') -or $name -match '^[A-Za-z]:') { throw "Unsafe archive path: $($entry.FullName)" }
        $parts = $name -split '\\'
        if ($parts -contains '..' -or $parts -contains '') { throw "Unsafe archive path: $($entry.FullName)" }
        $target = [System.IO.Path]::GetFullPath((Join-Path $destination $name))
        if (-not $target.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Archive escapes destination: $($entry.FullName)"
        }
    }
}
finally {
    $archive.Dispose()
}

[System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $destination)
$kspManaged = Join-Path $destination 'KSP\KSP_x64_Data\Managed'
$fixtureGameData = Join-Path $destination 'EXTI\GameData'
if (-not (Test-Path -LiteralPath (Join-Path $kspManaged 'Assembly-CSharp.dll'))) { throw 'Imported KSP references incomplete' }
if (-not (Test-Path -LiteralPath $fixtureGameData -PathType Container)) { throw 'Imported EXTI GameData missing' }
Write-Host "Imported KSP build root: $destination\KSP"
Write-Host "Imported EXTI fixture: $destination\EXTI"
