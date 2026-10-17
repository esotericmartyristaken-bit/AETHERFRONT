$ErrorActionPreference = 'Stop'
$apf = '.'
$bot = Join-Path $apf 'APFDiscordBot'
$live = '.\EXTI-Server'
$testParent = Join-Path $apf 'Runtime-Tests'
$testRoot = Join-Path $testParent 'LifecycleServer'
$testExe = Join-Path $testRoot 'Server.exe'
function Assert-TestPath([string] $target) {
  $resolved = [IO.Path]::GetFullPath($target)
  $parent = ([IO.Path]::GetFullPath($testParent)).TrimEnd('\') + '\'
  if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe lifecycle test path: $resolved" }
}
try {
  Assert-TestPath $testRoot
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
  New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
  Copy-Item -Path (Join-Path $live '*') -Destination $testRoot -Recurse -Force
  $connectionPath = Join-Path $testRoot 'Config\ConnectionSettings.xml'
  $connection = (Get-Content -LiteralPath $connectionPath -Raw).Replace('<Port>2252</Port>', '<Port>22953</Port>')
  Set-Content -LiteralPath $connectionPath -Value $connection -Encoding Unicode
  $masterPath = Join-Path $testRoot 'Config\MasterServerSettings.xml'
  $master = (Get-Content -LiteralPath $masterPath -Raw).Replace('<RegisterWithMasterServer>true</RegisterWithMasterServer>', '<RegisterWithMasterServer>false</RegisterWithMasterServer>')
  Set-Content -LiteralPath $masterPath -Value $master -Encoding Unicode
  $generalPath = Join-Path $testRoot 'Config\GeneralSettings.xml'
  $general = (Get-Content -LiteralPath $generalPath -Raw).Replace('<ServerName>EXTI</ServerName>', '<ServerName>APF Lifecycle Smoke</ServerName>')
  Set-Content -LiteralPath $generalPath -Value $general -Encoding Unicode
  $env:APF_LIFECYCLE_ROOT = $testRoot
  Push-Location $bot
  try { & node (Join-Path $bot 'src\lifecycle-smoke.js'); if ($LASTEXITCODE -ne 0) { throw "Lifecycle smoke exited with code $LASTEXITCODE" } }
  finally { Pop-Location }
} finally {
  $env:APF_LIFECYCLE_ROOT = $null
  $running = @(Get-CimInstance Win32_Process -Filter "Name = 'Server.exe'" | Where-Object { $_.ExecutablePath -eq $testExe })
  foreach ($process in $running) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
  if (Test-Path -LiteralPath $testRoot) { Assert-TestPath $testRoot; Remove-Item -LiteralPath $testRoot -Recurse -Force }
  $parentChildren = if (Test-Path -LiteralPath $testParent -PathType Container) { @(Get-ChildItem -LiteralPath $testParent -Force) } else { @('missing') }
  if ((Test-Path -LiteralPath $testParent -PathType Container) -and $parentChildren.Count -eq 0) { Remove-Item -LiteralPath $testParent -Force }
}
Write-Output 'Temporary lifecycle smoke root removed.'
