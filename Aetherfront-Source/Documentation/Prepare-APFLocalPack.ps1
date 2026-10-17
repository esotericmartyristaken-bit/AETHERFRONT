param(
    [Parameter(Mandatory = $true)] [string]$PackDirectory,
    [Parameter(Mandatory = $true)] [string]$ServerDirectory,
    [Parameter(Mandatory = $true)] [string]$HostUrl,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$uri = $null
if (-not [Uri]::TryCreate($HostUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
    throw 'HostUrl must be an absolute HTTPS URL'
}
$manifest = Join-Path $PackDirectory 'APFPack.json'
$config = Join-Path $ServerDirectory 'Config'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "Pack manifest missing: $manifest" }
if (-not (Test-Path -LiteralPath $config -PathType Container)) { throw "Server Config directory missing: $config" }
$targetManifest = Join-Path $config 'APFPack.json'
$targetUrl = Join-Path $config 'APFManifestUrl.txt'
if (-not $Force -and (Test-Path -LiteralPath $targetManifest -PathType Leaf)) { throw "Refusing to overwrite $targetManifest without -Force" }
if (-not $Force -and (Test-Path -LiteralPath $targetUrl -PathType Leaf)) { throw "Refusing to overwrite $targetUrl without -Force" }
Copy-Item -LiteralPath $manifest -Destination $targetManifest -Force
Set-Content -LiteralPath $targetUrl -Value ($HostUrl.TrimEnd('/') + '/APFPack.json') -Encoding UTF8
Write-Host "APF pack installed into $config"
Write-Host "Manifest URL: $($HostUrl.TrimEnd('/') + '/APFPack.json')"
