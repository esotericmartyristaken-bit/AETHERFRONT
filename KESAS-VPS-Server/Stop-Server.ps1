. "$PSScriptRoot/Update.Common.ps1"
Write-UpdateAtomic (Get-UpdateChildPath $PSScriptRoot 'stop-request.txt') 'stop'
Write-Host 'Shutdown requested. Wait for Start-Server.ps1 to finish before restarting.'
