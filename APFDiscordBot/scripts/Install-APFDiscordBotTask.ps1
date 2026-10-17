param(
  [string] $TaskName = 'APF Discord Bot',
  [string] $BotRoot = ''
)
$ErrorActionPreference = 'Stop'
if (-not $BotRoot) { $BotRoot = Split-Path -Parent $PSScriptRoot }
$resolvedRoot = [IO.Path]::GetFullPath($BotRoot)
$startScript = Join-Path $resolvedRoot 'scripts\Start-APFDiscordBot.ps1'
if (-not (Test-Path -LiteralPath $startScript -PathType Leaf)) { throw "Bot start script missing: $startScript" }
if (-not $env:APF_DISCORD_TOKEN) { $env:APF_DISCORD_TOKEN = [Environment]::GetEnvironmentVariable('APF_DISCORD_TOKEN', 'User') }
if (-not $env:APF_DISCORD_TOKEN) { throw 'APF_DISCORD_TOKEN is not set. Set it in the user environment before installing the task.' }
$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$startScript`""
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $resolvedRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Days 7) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'APF Discord observability/control bot; does not own or stop LMP in observe mode.' -Force | Out-Null
Write-Output "Installed scheduled task '$TaskName' for user $env:USERNAME. It starts the bot at interactive logon; it does not stop LMP."
