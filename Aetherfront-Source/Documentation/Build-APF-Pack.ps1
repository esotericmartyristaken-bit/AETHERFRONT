param(
    [Parameter(Mandatory = $true)] [string]$SourceGameData,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory,
    [Parameter(Mandatory = $true)] [string]$ServerId,
    [Parameter(Mandatory = $true)] [string]$PackId,
    [Parameter(Mandatory = $true)] [string]$PackVersion,
    [Parameter(Mandatory = $true)] [string]$ArchiveBaseUrl,
    [string]$KspVersion,
    [string]$ClientVersion = 'apf-client-1',
    [string[]]$ClientOnlyRoot = @(),
    [string[]]$RequiredDlc = @(),
    [string[]]$ProtectedRoot = @(),
    [switch]$ReuseExistingArchives
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

$arguments = @(
    'run', '--project', (Join-Path $sourceRoot 'ApfPackTool\ApfPackTool.csproj'),
    '--configuration', 'Release', '--',
    '--source-gamedata', $SourceGameData,
    '--output', $OutputDirectory,
    '--server-id', $ServerId,
    '--pack-id', $PackId,
    '--pack-version', $PackVersion,
    '--archive-base-url', $ArchiveBaseUrl,
    '--client-version', $ClientVersion
)
if ($KspVersion) { $arguments += @('--ksp-version', $KspVersion) }
foreach ($value in $ClientOnlyRoot) { $arguments += @('--client-only-root', $value) }
foreach ($value in $RequiredDlc) { $arguments += @('--required-dlc', $value) }
foreach ($value in $ProtectedRoot) { $arguments += @('--protected-root', $value) }
if ($ReuseExistingArchives) { $arguments += '--reuse-existing' }

& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'APF pack build succeeded. Publish the output archives and APFPack.json over HTTPS.'
