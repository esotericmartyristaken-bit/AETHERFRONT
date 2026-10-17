param(
  [string] $ServerRoot = ''
)
$ErrorActionPreference = 'Stop'
$botRoot = Split-Path -Parent $PSScriptRoot
$apfRoot = Split-Path -Parent $botRoot
$project = Join-Path $apfRoot 'LunaMultiplayer-Source\APFDiscordBridge\APFDiscordBridge.csproj'
$configPath = Join-Path $botRoot 'config.json'
$artifactDir = Join-Path $botRoot 'artifacts'
$dotnet = Join-Path $apfRoot '.dotnet-sdk6\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
if (-not $ServerRoot) {
  if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "Missing config.json: $configPath" }
  $ServerRoot = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).lmp.serverRoot
}
$resolvedServerRoot = [IO.Path]::GetFullPath($ServerRoot)
if (-not (Test-Path -LiteralPath (Join-Path $resolvedServerRoot 'Server.dll') -PathType Leaf)) { throw "Server.dll missing under $resolvedServerRoot" }
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Bridge project missing: $project" }
& $dotnet build $project -c Release "/p:LmpServerRoot=$resolvedServerRoot"
if ($LASTEXITCODE -ne 0) { throw "Bridge build failed with exit code $LASTEXITCODE" }
$built = Join-Path (Split-Path -Parent $project) 'bin\Release\net6.0\APFDiscordBridge.dll'
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "Build output missing: $built" }
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
Copy-Item -LiteralPath $built -Destination (Join-Path $artifactDir 'APFDiscordBridge.dll') -Force
$pdb = [IO.Path]::ChangeExtension($built, '.pdb')
if (Test-Path -LiteralPath $pdb -PathType Leaf) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $artifactDir 'APFDiscordBridge.pdb') -Force }
Get-FileHash (Join-Path $artifactDir 'APFDiscordBridge.dll') -Algorithm SHA256
Write-Output "Bridge artifact ready: $(Join-Path $artifactDir 'APFDiscordBridge.dll')"
