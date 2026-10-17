param(
    [Parameter(Mandatory = $true)] [string]$ClientRoot,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory,
    [Parameter(Mandatory = $true)] [string]$Version,
    [Parameter(Mandatory = $true)] [string]$PackageBaseUrl,
    [Parameter(Mandatory = $true)] [string]$PrivateKeyXml,
    [string]$KspVersion,
    [string]$ReleaseNotes
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

if (-not (Test-Path -LiteralPath $ClientRoot -PathType Container)) { throw "Client root not found: $ClientRoot" }
if (-not (Test-Path -LiteralPath $PrivateKeyXml -PathType Leaf)) { throw "Private update key not found: $PrivateKeyXml" }
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]*$') { throw 'Version contains unsafe characters' }
$uri = $null
if (-not [Uri]::TryCreate($PackageBaseUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
    throw 'PackageBaseUrl must be an HTTPS URL'
}

$arguments = @(
    'run', '--project', (Join-Path $sourceRoot 'ApfUpdateTool\ApfUpdateTool.csproj'),
    '--configuration', 'Release', '--',
    '--source-root', $ClientRoot,
    '--output', $OutputDirectory,
    '--version', $Version,
    '--package-base-url', $PackageBaseUrl,
    '--private-key-xml', $PrivateKeyXml
)
if ($KspVersion) { $arguments += @('--ksp-version', $KspVersion) }
if ($ReleaseNotes) { $arguments += @('--release-notes', $ReleaseNotes) }

& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'APF client update built. Publish APFClientUpdate.json and its ZIP over HTTPS.'
