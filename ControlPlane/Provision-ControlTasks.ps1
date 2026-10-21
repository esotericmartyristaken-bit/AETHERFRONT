param([Parameter(Mandatory)][string]$Settings)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$root=[IO.Path]::GetFullPath($configuration.root)
. "$root/bootstrap/native/Update.Common.ps1"
$null=Assert-UpdatePath $root
$expectedRoot=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Aetherfront-Control'))
if($root -ine $expectedRoot){throw 'Protected ProgramData installation root is required.'}
$identity=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Normal Windows UAC elevation is required.'}
$botTask=$null;$gameTask=$null;$oldBotAction=$null;$oldGameAction=$null;$lease=$null;$changed=$false;$registered=$false
$result=[ordered]@{ready=$false;controlVersion='control-1';applicationVersion=$null;serverRestarted=$false;networkChanged=$false;botElevated=$false;failure=$null}
function Protect-Bootstrap([string]$Path,[bool]$Readable) {
    $full=Assert-UpdatePath $Path
    $allowed=@('bootstrap','native-cache','host.json','setup-result.json')|ForEach-Object {[IO.Path]::GetFullPath((Join-Path $root $_))}
    if($allowed -inotcontains $full){throw 'Unexpected ACL target'}
    Assert-UpdateTree $full
    $admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18');$user=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $items=@(Get-Item -LiteralPath $full -Force)
    if($items[0].PSIsContainer){$items+=@(Get-ChildItem -LiteralPath $full -Recurse -Force)}
    foreach($item in $items){
        $acl=Get-Acl -LiteralPath $item.FullName
        $acl.SetAccessRuleProtection($true,$false)
        foreach($rule in $acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$acl.RemoveAccessRuleSpecific($rule)}
        $inherit=if($item.PSIsContainer){[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
        foreach($sid in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))}
        if($Readable){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'ReadAndExecute',$inherit,'None','Allow'))}
        $acl.SetOwner($admin);Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
}
function Protect-ControlRoot {
    $admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18');$user=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $limitedSids=@($user.Value,'S-1-1-0','S-1-5-11','S-1-5-32-545')+@($identity.Groups|ForEach-Object Value|Where-Object {$_ -notin @($admin.Value,$system.Value)})
    foreach($ancestor in @((Split-Path -Parent $root),[IO.Path]::GetPathRoot($root))){
        $acl=Get-Acl -LiteralPath $ancestor
        if($acl.Owner -eq $identity.Name){throw 'Installation parent is owned by the limited operator identity.'}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
            if($rule.IdentityReference.Value -in $limitedSids -and $rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and ($rule.FileSystemRights -band ([Security.AccessControl.FileSystemRights]'DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership,Delete'))){throw 'Installation ancestor allows limited identity to replace protected control code.'}
        }
    }
    if(-not $configuration.node.StartsWith(([Environment]::GetFolderPath('ProgramFiles').TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Privileged runtime must use the provisioned protected Program Files Node installation.'}
    $acl=Get-Acl -LiteralPath $root
    $acl.SetAccessRuleProtection($true,$false)
    foreach($rule in $acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$acl.RemoveAccessRuleSpecific($rule)}
    foreach($sid in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'CreateFiles','None','None','Allow'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'Modify','ObjectInherit','InheritOnly','Allow'))
    $acl.SetOwner($admin);Set-Acl -LiteralPath $root -AclObject $acl
    foreach($name in @('releases','staging','transactions','native-requests','native-results')){
        $directory=Assert-UpdatePath (Join-Path $root $name)
        $access=Get-Acl -LiteralPath $directory
        $access.SetAccessRuleProtection($true,$false)
        foreach($rule in $access.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$access.RemoveAccessRuleSpecific($rule)}
        foreach($sid in @($admin,$system)){$access.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
        $access.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'Modify','ContainerInherit,ObjectInherit','None','Allow'))
        $access.SetOwner($admin);Set-Acl -LiteralPath $directory -AclObject $access
    }
}
try {
    $botTask=Get-ScheduledTask -TaskName 'APF Discord Bot' -ErrorAction Stop
    $gameTask=Get-ScheduledTask -TaskName 'KESAS Server' -ErrorAction Stop
    if($botTask.Principal.RunLevel -notin @('Limited',0) -or $gameTask.Principal.UserId -notin @('SYSTEM','NT AUTHORITY\SYSTEM','S-1-5-18')){throw 'Existing task ownership differs; no account changes permitted.'}
    $oldBotAction=$botTask.Actions;$oldGameAction=$gameTask.Actions
    if(@($oldBotAction).Count -ne 1 -or @($oldGameAction).Count -ne 1 -or $oldBotAction.Arguments -notlike '*Start-APFDiscordBot.ps1*' -or $oldGameAction.Arguments -notlike '*Start-Server.ps1*'){throw 'Unexpected launcher actions; preserve them and review.'}
    Write-UpdateAtomic "$root/bootstrap/prior-bot-task.xml" (Export-ScheduledTask -TaskName 'APF Discord Bot')
    Write-UpdateAtomic "$root/bootstrap/prior-game-task.xml" (Export-ScheduledTask -TaskName 'KESAS Server')
    $state=Read-UpdateText "$($configuration.botRoot)/state/kesas-update-state.json"|ConvertFrom-Json
    if($state.schemaVersion -ne 1 -or $state.status -cne 'idle' -or [IO.File]::Exists("$($configuration.nativeRoot)/.update-transaction.json")){throw 'Native deployment is not idle.'}
    $lease=[IO.FileStream]::new((Assert-UpdatePath "$($configuration.nativeRoot)/.publish.lock"),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Protect-ControlRoot
    Protect-Bootstrap "$root/bootstrap" $true
    Protect-Bootstrap "$root/host.json" $true
    Protect-Bootstrap "$root/setup-result.json" $true
    Protect-Bootstrap "$root/native-cache" $false
    Stop-ScheduledTask -TaskName 'APF Discord Bot'
    $deadline=(Get-Date).AddSeconds(20)
    while((Get-ScheduledTask -TaskName 'APF Discord Bot').State -eq 'Running') {if((Get-Date)-gt $deadline){throw 'Old bot task did not stop.'};Start-Sleep -Milliseconds 200}
    $state=Read-UpdateText "$($configuration.botRoot)/state/kesas-update-state.json"|ConvertFrom-Json
    if($state.status -cne 'idle'){throw 'Deployment arrived during bootstrap; prior bot will resume it unchanged.'}
    $windowsPowerShell="$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe"
    $botAction=New-ScheduledTaskAction -Execute $windowsPowerShell -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$root/bootstrap/Start-ControlHost.ps1`" -Settings `"$Settings`"" -WorkingDirectory $configuration.botRoot
    $gameAction=New-ScheduledTaskAction -Execute $windowsPowerShell -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$root/bootstrap/Start-NativeServer.ps1`" -Settings `"$Settings`"" -WorkingDirectory $configuration.nativeRoot
    $changed=$true
    Set-ScheduledTask -TaskName 'APF Discord Bot' -Action $botAction|Out-Null
    Set-ScheduledTask -TaskName 'KESAS Server' -Action $gameAction|Out-Null
    $nativeAction=New-ScheduledTaskAction -Execute $windowsPowerShell -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$root/bootstrap/Start-NativeAgent.ps1`" -Settings `"$Settings`"" -WorkingDirectory $configuration.botRoot
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    $nativePrincipal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
    $taskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    if(Get-ScheduledTask -TaskName 'Aetherfront Native Publisher' -ErrorAction SilentlyContinue){throw 'Native publisher task already exists; do not replace it.'}
    Register-ScheduledTask -TaskName 'Aetherfront Native Publisher' -Action $nativeAction -Principal $nativePrincipal -Trigger $trigger -Settings $taskSettings -Description 'Fixed authenticated Aetherfront publisher; no generic commands or bot elevation.'|Out-Null
    $registered=$true
    $lease.Dispose();$lease=$null
    # No application request is admitted while the initial bot replacement is
    # being health-checked. The native agent starts only after setup commits.
    Write-UpdateAtomic "$root/setup-in-progress.json" '{"schemaVersion":1}'
    Start-ScheduledTask -TaskName 'APF Discord Bot'
    $deadline=(Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 1
        try {$status=Read-UpdateText "$root/host-status.json"|ConvertFrom-Json}catch{$status=$null}
        if($status -and $status.control.version -ceq 'control-1' -and $status.control.lastSuccess -ceq 'control-1' -and $status.control.status -ceq 'healthy' -and $status.application.health -ceq 'healthy' -and @($status.capabilities).Count -eq 7){
            $result.ready=$true;$result.applicationVersion=$status.application.clientVersion;break
        }
    }while((Get-Date)-lt $deadline)
    if(-not $result.ready){throw 'New control-plane startup health check failed.'}
    Start-ScheduledTask -TaskName 'Aetherfront Native Publisher'
    $agentDeadline=(Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $agentState=(Get-ScheduledTask -TaskName 'Aetherfront Native Publisher' -ErrorAction Stop).State
        if($agentState -eq 'Running'){break}
    }while((Get-Date)-lt $agentDeadline)
    if($agentState -ne 'Running'){throw 'Fixed native publication agent failed to start.'}
    [IO.File]::Delete((Assert-UpdatePath "$root/setup-in-progress.json"))
} catch {
    $result.ready=$false
    $result.failure='bootstrap-setup-failed'
    if($lease){$lease.Dispose();$lease=$null}
    if($changed){
        Stop-ScheduledTask -TaskName 'APF Discord Bot' -ErrorAction SilentlyContinue
        Set-ScheduledTask -TaskName 'APF Discord Bot' -Action $oldBotAction|Out-Null
        Set-ScheduledTask -TaskName 'KESAS Server' -Action $oldGameAction|Out-Null
    }
    if($registered){
        # The agent was not started before readiness. If startup reached it,
        # preserve the task rather than terminating a possibly applying helper.
        $agent=Get-ScheduledTask -TaskName 'Aetherfront Native Publisher' -ErrorAction SilentlyContinue
        if($agent -and $agent.State -ne 'Running'){Unregister-ScheduledTask -TaskName 'Aetherfront Native Publisher' -Confirm:$false}
    }
    if($botTask){Start-ScheduledTask -TaskName 'APF Discord Bot' -ErrorAction SilentlyContinue}
} finally {
    if($lease){$lease.Dispose()}
    [IO.File]::WriteAllText("$root/setup-result.json",($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
if(-not $result.ready){exit 1}
