param([string]$DeploymentRoot=$env:APF_NATIVE_ROOT)
. "$PSScriptRoot/Update.Common.ps1"
$root=Assert-UpdatePath $(if($DeploymentRoot){$DeploymentRoot}else{$PSScriptRoot})
Write-UpdateAtomic (Get-UpdateChildPath $root 'stop-request.txt') 'stop'
Write-Host 'Shutdown requested. Wait for Start-Server.ps1 to finish before restarting.'
