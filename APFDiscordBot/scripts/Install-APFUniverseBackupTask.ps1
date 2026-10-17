param(
  [string] $TaskName = 'APF Universe Backup',
  [ValidateRange(5, 1440)] [int] $IntervalMinutes = 240
)
$ErrorActionPreference = 'Stop'
$botRoot = Split-Path -Parent $PSScriptRoot
$node = (Get-Command node.exe -ErrorAction Stop).Source
$entry = Join-Path $botRoot 'src\backup-once.js'
if (-not (Test-Path -LiteralPath (Join-Path $botRoot 'config.json') -PathType Leaf)) { throw "Missing config.json: $(Join-Path $botRoot 'config.json')" }
$start = (Get-Date).AddMinutes($IntervalMinutes)
if (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue) {
  $action = New-ScheduledTaskAction -Execute $node -Argument "`"$entry`"" -WorkingDirectory $botRoot
  $trigger = New-ScheduledTaskTrigger -Once -At $start -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)
  $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
  $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
} else {
  $startText = $start.ToString('HH:mm')
  $taskCommand = '"{0}" "{1}"' -f $node, $entry
  & schtasks.exe /Create /SC MINUTE /MO $IntervalMinutes /TN $TaskName /TR $taskCommand /ST $startText /RU SYSTEM /F | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "schtasks.exe failed with exit code $LASTEXITCODE" }
}
Write-Output "Registered '$TaskName' every $IntervalMinutes minutes. First run: $start"
