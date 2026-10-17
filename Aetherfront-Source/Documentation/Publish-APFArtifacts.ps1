param(
    [string]$SyncServerDirectory,
    [string]$SyncMasterDirectory
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$workspaceDotnet = Join-Path (Split-Path -Parent $sourceRoot) '.dotnet-sdk6\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet -PathType Leaf) {
    $workspaceDotnet
} elseif (Test-Path -LiteralPath 'C:\Program Files\dotnet\dotnet.exe' -PathType Leaf) {
    'C:\Program Files\dotnet\dotnet.exe'
} else {
    'dotnet'
}

$artifacts = @(
    @{ Project = 'Server\Server.csproj'; Output = 'Build\APF\Server' },
    @{ Project = 'MasterServer\MasterServer.csproj'; Output = 'Build\APF\MasterServer' },
    @{ Project = 'ApfUpdater\ApfUpdater.csproj'; Output = 'Build\APF\APFUpdater' },
    @{ Project = 'ApfPackTool\ApfPackTool.csproj'; Output = 'Build\APF\ApfPackTool' },
    @{ Project = 'ApfUpdateTool\ApfUpdateTool.csproj'; Output = 'Build\APF\ApfUpdateTool' },
    @{ Project = 'ApfPackHost\ApfPackHost.csproj'; Output = 'Build\APF\ApfPackHost' },
    @{ Project = 'ApfDiagnosticsHost\ApfDiagnosticsHost.csproj'; Output = 'Build\APF\ApfDiagnosticsHost' }
)

foreach ($artifact in $artifacts) {
    $project = Join-Path $sourceRoot $artifact.Project
    $output = Join-Path $sourceRoot $artifact.Output
    & $dotnet publish $project --configuration Release --output $output --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Sync-ArtifactTree([string]$Source, [string]$Destination) {
    $sourcePath = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $destinationPath = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if ($sourcePath.Equals($destinationPath, [System.StringComparison]::OrdinalIgnoreCase)) { return }
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    $prefix = $sourcePath + '\'
    foreach ($file in Get-ChildItem -LiteralPath $sourcePath -File -Recurse) {
        $relative = $file.FullName.Substring($prefix.Length)
        $target = Join-Path $destinationPath $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

if ($SyncServerDirectory) {
    $serverTarget = [System.IO.Path]::GetFullPath($SyncServerDirectory)
    Sync-ArtifactTree (Join-Path $sourceRoot 'Build\APF\Server') $serverTarget
    Write-Host "APF server synchronized: $serverTarget"
}

if ($SyncMasterDirectory) {
    $masterTarget = [System.IO.Path]::GetFullPath($SyncMasterDirectory)
    Sync-ArtifactTree (Join-Path $sourceRoot 'Build\APF\MasterServer') $masterTarget
    Write-Host "APF master synchronized: $masterTarget"
}

Write-Host 'APF artifacts published.'
