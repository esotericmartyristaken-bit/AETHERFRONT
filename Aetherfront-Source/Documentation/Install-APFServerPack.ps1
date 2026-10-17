param(
    [Parameter(Mandatory = $true)]
    [string]$ServerDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PackDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ManifestUrl,
    [Parameter(Mandatory = $true)]
    [string]$PrivateMasterServer,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$server = [System.IO.Path]::GetFullPath($ServerDirectory)
$pack = [System.IO.Path]::GetFullPath($PackDirectory)
$serverConfig = Join-Path $server 'Config'
$sourceManifest = Join-Path $pack 'APFPack.json'
$targetManifest = Join-Path $serverConfig 'APFPack.json'
$targetUrl = Join-Path $serverConfig 'APFManifestUrl.txt'
$debugSettings = Join-Path $serverConfig 'DebugSettings.xml'

if (-not (Test-Path -LiteralPath (Join-Path $server 'Server.exe') -PathType Leaf)) {
    throw "Fork server executable not found: $server"
}
if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) {
    throw "Pack manifest not found: $sourceManifest"
}
if (-not (Test-Path -LiteralPath $debugSettings -PathType Leaf)) {
    throw "DebugSettings.xml not found: $debugSettings"
}
$manifestUri = $null
if (-not [Uri]::TryCreate($ManifestUrl, [UriKind]::Absolute, [ref]$manifestUri) -or
    $manifestUri.Scheme -ne [Uri]::UriSchemeHttps) {
    throw "ManifestUrl must be an HTTPS URL"
}
if ([string]::IsNullOrWhiteSpace($PrivateMasterServer)) {
    throw 'PrivateMasterServer is required'
}
if ((Test-Path -LiteralPath $targetManifest) -or (Test-Path -LiteralPath $targetUrl)) {
    if (-not $Force) { throw "Server already has APF pack configuration; use -Force to replace it" }
}

New-Item -ItemType Directory -Path $serverConfig -Force | Out-Null
Copy-Item -LiteralPath $sourceManifest -Destination $targetManifest -Force
Set-Content -LiteralPath $targetUrl -Value $ManifestUrl -Encoding UTF8

[xml]$debug = Get-Content -LiteralPath $debugSettings -Raw
$masterNode = $debug.SelectSingleNode('//CustomMasterServer')
if (-not $masterNode) { throw 'DebugSettings.xml has no CustomMasterServer element' }
$masterNode.InnerText = $PrivateMasterServer
$debug.Save($debugSettings)

Write-Host "Installed APF pack $targetManifest"
Write-Host "Manifest URL: $ManifestUrl"
Write-Host "Private master: $PrivateMasterServer"
