#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ControlRoot,
    [Parameter(Mandatory)][string]$BotRoot,
    [Parameter(Mandatory)][string]$NativeRoot,
    [Parameter(Mandatory)][string]$PowerShell,
    [string]$Node=(Get-Command node.exe -ErrorAction Stop).Source
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=Split-Path -Parent $PSScriptRoot
. "$source/KESAS-VPS-Server/Update.Common.ps1"
$root=Assert-UpdatePath $ControlRoot
$expectedRoot=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Aetherfront-Control'))
if($root -ine $expectedRoot){throw 'Use the protected ProgramData/Aetherfront-Control installation root, not a user-writable parent.'}
$bot=Assert-UpdatePath $BotRoot
$native=Assert-UpdatePath $NativeRoot
if(Test-Path -LiteralPath $root){throw 'Control root already exists; preserve it and inspect bootstrap state before retrying.'}
$configuration=Read-UpdateText "$bot/config.json"|ConvertFrom-Json
if([IO.Path]::GetFullPath($configuration.lmp.deploymentRoot) -ine $native){throw 'Configured native root differs from the requested root.'}
$state=Read-UpdateText "$bot/state/kesas-update-state.json"|ConvertFrom-Json
if($state.schemaVersion -ne 1 -or $state.status -cne 'idle' -or [IO.File]::Exists("$native/.update-transaction.json")){throw 'Native deployment is not idle; no bootstrap requested.'}
$npm=Join-Path (Split-Path -Parent $Node) 'node_modules/npm/bin/npm-cli.js'
foreach($file in @($Node,$PowerShell,$npm)){if(-not (Test-Path -LiteralPath (Assert-UpdatePath $file) -PathType Leaf)){throw 'Required pinned runtime is not provisioned.'}}
$nodeVersion=(& $Node -p 'process.versions.node').Trim()
if($nodeVersion -notmatch '^24\.'){throw 'Node 24 is required; runtime provisioning remains manual.'}
$psVersion=(& $PowerShell -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()').Trim()
if($psVersion -notmatch '^7\.' -or [version]$psVersion -lt [version]'7.4.0'){throw 'PowerShell 7.4 or newer in major 7 is required; runtime provisioning remains manual.'}
$runtime=Read-UpdateText "$native/PackHost/ApfPackHost.runtimeconfig.json"|ConvertFrom-Json
$dotnet=@($runtime.runtimeOptions.includedFrameworks|Where-Object name -ceq 'Microsoft.NETCore.App')[0].version
if($dotnet -notmatch '^8\.'){throw '.NET 8 application runtime is required; runtime provisioning remains manual.'}
$commit=(& git -C $source rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$'){throw 'Source commit is unavailable.'}
if(@(& git -C $source status --porcelain).Count){throw 'Commit the reviewed source before initial bootstrap so provenance identifies the installed bytes.'}
foreach($name in @('bootstrap','bootstrap/native','native-cache','native-requests','native-results','releases','staging','transactions')){New-UpdateDirectory "$root/$name"}
$powerShellHome=Split-Path -Parent $PowerShell
if(-not (Test-Path -LiteralPath "$powerShellHome/pwsh.dll") -or -not (Test-Path -LiteralPath "$powerShellHome/System.Management.Automation.dll")){throw 'A complete provisioned PowerShell runtime is required.'}
Copy-UpdateTree $powerShellHome "$root/bootstrap/PowerShell7"
$protectedPowerShell=Join-Path $root 'bootstrap/PowerShell7/pwsh.exe'
foreach($name in @('bootstrap.mjs','engine.mjs','contract.mjs','github.mjs','public-status.mjs','native-agent.mjs','Invoke-NativePublisher.ps1','Expand-ControlBundle.ps1','Probe-NativeIdle.ps1','Read-ClientInventory.ps1','Start-ControlHost.ps1','Start-NativeAgent.ps1','Start-NativeServer.ps1','Provision-ControlTasks.ps1')){Copy-UpdateAtomic "$PSScriptRoot/$name" "$root/bootstrap/$name"}
foreach($name in @('Publish-Release','Update.Common','GitHub.Publish','Export-Client','Start-Server','Stop-Server')){Copy-UpdateAtomic "$source/KESAS-VPS-Server/$name.ps1" "$root/bootstrap/native/$name.ps1"}
Copy-UpdateAtomic "$source/APFDiscordBot/scripts/Verify-KesasRelease.ps1" "$root/bootstrap/Verify-KesasRelease.ps1"
$settings=[ordered]@{root=$root;botRoot=$bot;nativeRoot=$native;powershell=$protectedPowerShell;node=$Node;npm=$npm;runtime=[ordered]@{node=$nodeVersion;powershell=$psVersion;dotnet=$dotnet}}
Write-UpdateAtomic "$root/host.json" ($settings|ConvertTo-Json -Depth 4)
Write-UpdateAtomic "$root/setup-result.json" '{"ready":false}'
$archive="$root/bootstrap/Aetherfront-Control-control-1.zip"
$null=& "$PSScriptRoot/Build-ControlBundle.ps1" -Version control-1 -Output $archive -SourceRoot $source
& $Node "$PSScriptRoot/initialize.mjs" --settings "$root/host.json" --archive $archive --commit $commit
if($LASTEXITCODE -ne 0){throw 'Initial control release did not verify; preserve its root for review.'}
$arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$root/bootstrap/Provision-ControlTasks.ps1`" -Settings `"$root/host.json`""
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    & "$root/bootstrap/Provision-ControlTasks.ps1" -Settings "$root/host.json"
} else {
    # Normal UAC consent, not a bypass. The elevated helper has one fixed task:
    # protect this bootstrap and register the narrow publisher/supervisor tasks.
    $helper=Start-Process -FilePath "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $helper.WaitForExit()
    if($helper.ExitCode -ne 0){throw 'Elevated bootstrap did not complete; inspect the preserved setup result.'}
}
$result=Read-UpdateText "$root/setup-result.json"|ConvertFrom-Json
if(-not $result.ready){throw 'Control bootstrap is not healthy; prior bot task action restored.'}
$result|Select-Object ready,controlVersion,applicationVersion,serverRestarted,networkChanged,botElevated|ConvertTo-Json -Compress
