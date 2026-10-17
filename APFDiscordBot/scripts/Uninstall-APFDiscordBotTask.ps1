param([string] $TaskName = 'APF Discord Bot')
$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false; Write-Output "Removed scheduled task '$TaskName'." }
else { Write-Output "Scheduled task '$TaskName' was not present." }
