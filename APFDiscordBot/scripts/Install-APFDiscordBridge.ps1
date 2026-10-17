param(
  [Parameter(Mandatory = $true)] [string] $ServerRoot
)
$ErrorActionPreference = 'Stop'
$botRoot = Split-Path -Parent $PSScriptRoot
$artifact = Join-Path $botRoot 'artifacts\APFDiscordBridge.dll'
$resolvedRoot = [IO.Path]::GetFullPath($ServerRoot)
$serverExe = Join-Path $resolvedRoot 'Server.exe'
$plugins = Join-Path $resolvedRoot 'Plugins'
if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw "Bridge artifact missing: $artifact. Build APFDiscordBridge first." }
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) { throw "Server.exe missing: $serverExe" }
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'Server.exe'" | Where-Object { $_.ExecutablePath -eq $serverExe })
if ($running.Count -gt 0) { throw "Refusing install while Server.exe is running (PID $($running[0].ProcessId)). Stop/restart during a planned maintenance window." }
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
$destination = Join-Path $plugins 'APFDiscordBridge.dll'
$sourceHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
Copy-Item -LiteralPath $artifact -Destination $destination -Force
$destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) { throw "Bridge install hash mismatch: source=$sourceHash destination=$destinationHash" }
Write-Output "Installed APFDiscordBridge.dll to $plugins"
Write-Output "SHA256: $destinationHash"
