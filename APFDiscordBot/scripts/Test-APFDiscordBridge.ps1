$ErrorActionPreference = 'Stop'
$apf = '.'
$live = '.\EXTI-Server'
$testParent = Join-Path $apf 'Runtime-Tests'
$testRoot = Join-Path $testParent 'BridgeServer'
$testExe = Join-Path $testRoot 'Server.exe'
$fixture = Join-Path $apf 'APFDiscordBot\test\fixtures\bridge-message.json'
$testPid = $null
function Assert-TestPath([string] $target) {
  $resolved = [IO.Path]::GetFullPath($target)
  $parent = ([IO.Path]::GetFullPath($testParent)).TrimEnd('\') + '\'
  if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe test path: $resolved" }
}
try {
  Assert-TestPath $testRoot
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
  New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
  Copy-Item -Path (Join-Path $live '*') -Destination $testRoot -Recurse -Force
  $connectionPath = Join-Path $testRoot 'Config\ConnectionSettings.xml'
  $connection = (Get-Content -LiteralPath $connectionPath -Raw).Replace('<Port>2252</Port>', '<Port>22952</Port>')
  Set-Content -LiteralPath $connectionPath -Value $connection -Encoding Unicode
  $masterPath = Join-Path $testRoot 'Config\MasterServerSettings.xml'
  $master = (Get-Content -LiteralPath $masterPath -Raw).Replace('<RegisterWithMasterServer>true</RegisterWithMasterServer>', '<RegisterWithMasterServer>false</RegisterWithMasterServer>')
  Set-Content -LiteralPath $masterPath -Value $master -Encoding Unicode
  $generalPath = Join-Path $testRoot 'Config\GeneralSettings.xml'
  $general = (Get-Content -LiteralPath $generalPath -Raw).Replace('<ServerName>EXTI</ServerName>', '<ServerName>APF Bridge Smoke</ServerName>')
  Set-Content -LiteralPath $generalPath -Value $general -Encoding Unicode
  Copy-Item -LiteralPath (Join-Path $apf 'APFDiscordBot\artifacts\APFDiscordBridge.dll') -Destination (Join-Path $testRoot 'Plugins\APFDiscordBridge.dll') -Force
  $proc = Start-Process -FilePath $testExe -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
  $testPid = $proc.Id
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath (Join-Path $testRoot 'APFDiscordBridge\ready.json'))) {
    if ($proc.HasExited) { throw "Smoke server exited: $($proc.ExitCode)" }
    Start-Sleep -Milliseconds 500
  }
  $ready = Test-Path -LiteralPath (Join-Path $testRoot 'APFDiscordBridge\ready.json')
  if (-not $ready) { throw 'Bridge ready.json was not created within 30 seconds' }
  Copy-Item -LiteralPath $fixture -Destination (Join-Path $testRoot 'APFDiscordBridge\inbox\smoke.json') -Force
  $inbox = Join-Path $testRoot 'APFDiscordBridge\inbox\smoke.json'
  $deadline = (Get-Date).AddSeconds(10)
  while ((Get-Date) -lt $deadline -and (Test-Path -LiteralPath $inbox)) { Start-Sleep -Milliseconds 500 }
  $logFiles = Get-ChildItem -LiteralPath (Join-Path $testRoot 'logs') -Filter *.log -File
  $logs = $logFiles | Get-Content -Raw -ErrorAction SilentlyContinue
  $combinedLogs = $logs -join "`n"
  $relay = [bool]($combinedLogs -match 'bridge smoke message')
  [PSCustomObject]@{ Ready = $ready; InboxProcessed = -not (Test-Path -LiteralPath $inbox); RelayLogged = $relay; TestPid = $testPid } | Format-List
  Write-Output 'Bridge-related log lines:'
  $combinedLogs -split "`r?`n" | Select-String -Pattern 'APF Discord|bridge smoke|Command input|Chat' | Select-Object -Last 20
} finally {
  if ($testPid) { Stop-Process -Id $testPid -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }
  if (Test-Path -LiteralPath $testRoot) { Assert-TestPath $testRoot; Remove-Item -LiteralPath $testRoot -Recurse -Force }
  $parentChildren = if (Test-Path -LiteralPath $testParent -PathType Container) { @(Get-ChildItem -LiteralPath $testParent -Force) } else { @('missing') }
  if ((Test-Path -LiteralPath $testParent -PathType Container) -and $parentChildren.Count -eq 0) { Remove-Item -LiteralPath $testParent -Force }
}
Write-Output 'Temporary bridge smoke root removed.'
