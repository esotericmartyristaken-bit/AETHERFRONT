#requires -Version 7.4
param([string]$Settings='C:\ProgramData\Aetherfront-Control\host.json')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'One-time normal Windows UAC approval is required.'}
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$root=[IO.Path]::GetFullPath($configuration.root)
if($root -ine 'C:\ProgramData\Aetherfront-Control'){throw 'Unexpected installation root'}
. "$root/bootstrap/native/Update.Common.ps1"
Add-Type -AssemblyName System.Security
$outcome=[ordered]@{schemaVersion=1;ready=$false;stage='preflight';reason=$null;networkChanged=$false;gameRestarted=$false;botElevated=$false}
$output=Join-Path $root 'anchor-upgrade-result.json'
$lease=$null
function Get-Preservation {
    $files=@('deployment.json','Server/Config/APFPack.json','Updates/APFClientUpdate.json')+@(Get-ChildItem -LiteralPath "$($configuration.nativeRoot)/secrets" -File|ForEach-Object {'secrets/'+$_.Name})
    $hashes=@(foreach($file in $files){[ordered]@{path=$file;sha256=(Get-FileHash -LiteralPath (Join-Path $configuration.nativeRoot $file)).Hash}})
    [ordered]@{gamePid=@(Get-NetUDPEndpoint -LocalPort 18865).OwningProcess;packPid=@(Get-NetTCPConnection -LocalPort 18866 -State Listen).OwningProcess;files=$hashes;gameTask=Export-ScheduledTask -TaskName 'KESAS Server'}
}
function Set-Protection([string]$Path,[switch]$Readable){
    $null=Assert-UpdatePath $Path
    $admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18');$user=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $items=@(Get-Item -LiteralPath $Path -Force)
    if($items[0].PSIsContainer){$items+=@(Get-ChildItem -LiteralPath $Path -Recurse -Force)}
    foreach($item in $items){
        $acl=Get-Acl -LiteralPath $item.FullName;$acl.SetAccessRuleProtection($true,$false)
        foreach($rule in $acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$acl.RemoveAccessRuleSpecific($rule)}
        $inherit=if($item.PSIsContainer){[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
        foreach($sid in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))}
        if($Readable){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user,'ReadAndExecute',$inherit,'None','Allow'))}
        $acl.SetOwner($admin);Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
}
function Save-Credential([string]$Path,[string]$Value,[switch]$Readable){
    if([string]::IsNullOrWhiteSpace($Value)){throw 'Required existing credential is unavailable.'}
    $bytes=[Text.Encoding]::UTF8.GetBytes($Value)
    try{$encrypted=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine);[IO.File]::WriteAllBytes($Path,$encrypted)}finally{[Array]::Clear($bytes,0,$bytes.Length)}
    Set-Protection $Path -Readable:$Readable
}
try{
    $bot=Get-ScheduledTask -TaskName 'APF Discord Bot'
    $native=Get-ScheduledTask -TaskName 'Aetherfront Native Publisher'
    $game=Get-ScheduledTask -TaskName 'KESAS Server'
    if($bot.Principal.RunLevel -notin @('Limited',0) -or $game.Principal.UserId -notin @('SYSTEM','NT AUTHORITY\SYSTEM','S-1-5-18')){throw 'Unexpected component identities'}
    $state=Get-Content -LiteralPath "$($configuration.botRoot)/state/kesas-update-state.json" -Raw|ConvertFrom-Json
    $before=Get-Preservation
    if($state.status -notin @('idle','failed')){throw 'Existing application operation must drain first.'}
    if(Test-Path -LiteralPath "$($configuration.nativeRoot)/.update-transaction.json"){throw 'Native activation journal requires review before initial transition.'}
    foreach($result in Get-ChildItem -LiteralPath "$root/native-cache/results" -Filter '*.json'){
        $saved=Get-Content -LiteralPath $result.FullName -Raw|ConvertFrom-Json
        if($saved.phase -in @('accepted','recovery-authorized','applying')){throw 'A native request is still active; preserve it.'}
    }
    $lease=[IO.FileStream]::new((Join-Path $configuration.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    $history=Join-Path $root "bootstrap/anchor-transition-$stamp"
    $null=[IO.Directory]::CreateDirectory($history)
    foreach($name in @('APF Discord Bot','Aetherfront Native Publisher','KESAS Server')){[IO.File]::WriteAllText((Join-Path $history ($name.Replace(' ','-')+'.xml')),(Export-ScheduledTask -TaskName $name))}
    [IO.File]::WriteAllText((Join-Path $history 'preservation-before.json'),($before|ConvertTo-Json -Depth 8))
    $outcome.stage='credentials'
    $credentials=Join-Path $root 'bootstrap/credentials'
    $null=[IO.Directory]::CreateDirectory($credentials);Set-Protection $credentials
    $github=(& gh auth token --hostname github.com 2>$null|Out-String).Trim()
    if($LASTEXITCODE -ne 0){throw 'Existing GitHub credential is unavailable under operator identity.'}
    Save-Credential (Join-Path $credentials 'github.dpapi') $github;$github=$null
    $discord=[Environment]::GetEnvironmentVariable('APF_DISCORD_TOKEN','User')
    Save-Credential "$root/bootstrap/bot-credential.dpapi" $discord -Readable;$discord=$null
    $outcome.stage='install'
    Stop-ScheduledTask -TaskName 'APF Discord Bot'
    Stop-ScheduledTask -TaskName 'Aetherfront Native Publisher'
    Start-Sleep -Seconds 2
    foreach($name in @('anchor.mjs','engine.mjs','contract.mjs','github.mjs','public-status.mjs','Expand-ControlBundle.ps1','Manage-ControlWorker.ps1','Start-Anchor.ps1','Start-ControlHost.ps1','Start-NativeServer.ps1')){
        $destination=Join-Path $root "bootstrap/$name"
        if(Test-Path -LiteralPath $destination){Copy-UpdateAtomic $destination (Join-Path $history $name)}
        Copy-UpdateAtomic (Join-Path $PSScriptRoot $name) $destination
        Set-Protection $destination -Readable
    }
    $managed=Join-Path $root 'managed';$null=[IO.Directory]::CreateDirectory($managed);Set-Protection $managed -Readable
    foreach($name in @('releases','staging','transactions')){$null=[IO.Directory]::CreateDirectory((Join-Path $managed $name))}
    $outcome.stage='tasks'
    $windowsPowerShell="$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe"
    $botAction=New-ScheduledTaskAction -Execute $windowsPowerShell -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$root/bootstrap/Start-ControlHost.ps1`" -Settings `"$Settings`"" -WorkingDirectory $configuration.botRoot
    $anchorAction=New-ScheduledTaskAction -Execute $windowsPowerShell -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$root/bootstrap/Start-Anchor.ps1`" -Settings `"$Settings`"" -WorkingDirectory $configuration.botRoot
    $taskSettings=New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $startup=New-ScheduledTaskTrigger -AtStartup
    $repeat=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes 5)
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    $limited=New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Limited
    $system=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName 'APF Discord Bot' -Action $botAction -Principal $limited -Trigger $startup,$repeat -Settings $taskSettings -Force|Out-Null
    Register-ScheduledTask -TaskName 'Aetherfront Native Publisher' -Action $anchorAction -Principal $system -Trigger $startup,$repeat -Settings $taskSettings -Force|Out-Null
    # Some task-folder DACLs inherit read/write without execute for SYSTEM.
    # Preserve every existing ACE and grant only the trusted broker's missing
    # right to start this fixed-name limited task; never elevate the bot.
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $registered=$scheduler.GetFolder('\').GetTask('APF Discord Bot')
    $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($registered.GetSecurityDescriptor(7))
    $systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    if(-not @($descriptor.DiscretionaryAcl|Where-Object {$_.SecurityIdentifier -eq $systemSid -and $_.AceQualifier -eq 'AccessAllowed' -and ($_.AccessMask -band 0x20000020)}).Count){
        if(@($descriptor.DiscretionaryAcl|Where-Object {$_.SecurityIdentifier -eq $systemSid -and $_.AceQualifier -eq 'AccessDenied'}).Count){throw 'Explicit SYSTEM task deny requires review.'}
        $descriptor.DiscretionaryAcl.InsertAce(0,[Security.AccessControl.CommonAce]::new([Security.AccessControl.AceFlags]::None,[Security.AccessControl.AceQualifier]::AccessAllowed,0x20000000,$systemSid,$false,$null))
        $registered.SetSecurityDescriptor($descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access'),0x10)
    }
    # Existing SYSTEM game task/action/trigger are deliberately not changed.
    [IO.File]::WriteAllText("$root/bootstrap/unattended-host.json",'{"schemaVersion":1,"ready":true,"broker":"SYSTEM","bot":"S4U-Limited"}')
    Set-Protection "$root/bootstrap/unattended-host.json" -Readable
    $lease.Dispose();$lease=$null
    Start-ScheduledTask -TaskName 'Aetherfront Native Publisher'
    $outcome.stage='activation'
    $deadline=(Get-Date).AddMinutes(5)
    do{
        Start-Sleep -Seconds 2
        try{$status=Get-Content -LiteralPath "$root/host-status.json" -Raw|ConvertFrom-Json}catch{$status=$null}
        if($status -and $status.control.status -eq 'healthy' -and $status.capabilities -contains 'unattended-host-v1' -and ([DateTimeOffset]::UtcNow-([DateTimeOffset]$status.observedAt)).TotalSeconds -lt 30){$outcome.ready=$true;break}
    }while((Get-Date)-lt $deadline)
    if(-not $outcome.ready){throw 'Control transition is not yet healthy; inspect protected evidence. No application rollback was requested.'}
    $outcome.stage='healthy'
    $after=Get-Preservation
    [IO.File]::WriteAllText((Join-Path $history 'preservation-after.json'),($after|ConvertTo-Json -Depth 8))
    $outcome.preserved=(($before|ConvertTo-Json -Depth 8 -Compress) -ceq ($after|ConvertTo-Json -Depth 8 -Compress))
    if(-not $outcome.preserved){throw 'Preservation readback differs; inspect private evidence.'}
}catch{
    $outcome.reason='anchor-provisioning-failed'
    [IO.File]::WriteAllText("$root/native-cache/anchor-provisioning-error.json",($_|Out-String))
}finally{
    if($lease){$lease.Dispose()}
    [IO.File]::WriteAllText($output,($outcome|ConvertTo-Json -Compress));Set-Protection $output -Readable
}
if(-not $outcome.ready){exit 1}
