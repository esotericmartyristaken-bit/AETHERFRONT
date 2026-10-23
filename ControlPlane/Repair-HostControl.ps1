#requires -Version 7.4
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,[string]$Settings='C:\ProgramData\Aetherfront-Control\host.json')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Normal Windows UAC approval required'}
$configuration=Get-Content -LiteralPath $Settings -Raw|ConvertFrom-Json
$root=[IO.Path]::GetFullPath($configuration.root)
if($root -ine 'C:\ProgramData\Aetherfront-Control'){throw 'Unexpected protected root'}
. "$root/bootstrap/native/Update.Common.ps1"
$repository=Split-Path -Parent $PSScriptRoot
$selected=@('anchor.mjs','engine.mjs','contract.mjs','github.mjs','public-status.mjs','control-evidence.mjs','signed-readback.mjs','observation-publisher.mjs','supervision.mjs','Expand-ControlBundle.ps1','Manage-ControlWorker.ps1','Sign-HostObservation.ps1')
$result=[ordered]@{schemaVersion=1;provisioned=$false;stage='preflight';sourceCommit=$SourceCommit;gameRestarted=$false;packRestarted=$false;networkChanged=$false;preserved=$false;rolledBack=$false;rollbackPreserved=$false;rollbackFailed=$false;hashes=@();reason=$null}
$output=Join-Path $root 'control-repair-result.json'
$operatorOutput=Join-Path (Split-Path -Parent $repository) 'TestResults/permanent-control-provisioning.json'
$lease=$null;$controlTouched=$false;$installationStarted=$false;$priorTasks=@();$backupRows=@()
function Native-Idle {
    $state=Get-Content -LiteralPath "$($configuration.botRoot)/state/kesas-update-state.json" -Raw|ConvertFrom-Json
    if($state.status -notin @('idle','failed') -or (Test-Path -LiteralPath "$($configuration.nativeRoot)/.update-transaction.json")){throw 'Native application operation must remain untouched'}
    foreach($file in Get-ChildItem -LiteralPath "$root/native-cache/results" -Filter '*.json' -File){$value=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json;if($value.phase -notin @('healthy','blocked')){throw 'An active or ambiguous native helper must remain untouched'}}
}
function Preservation {
    $names=@('deployment.json','Server/Config/APFPack.json','Updates/APFClientUpdate.json')+@(Get-ChildItem -LiteralPath "$($configuration.nativeRoot)/Server/Config" -File|Where-Object {$_.Name -notlike 'kesas-host-*' -and $_.Extension -in @('.xml','.json','.txt')}|ForEach-Object {'Server/Config/'+$_.Name})+@(Get-ChildItem -LiteralPath "$($configuration.nativeRoot)/secrets" -File|ForEach-Object {'secrets/'+$_.Name})
    [ordered]@{gamePids=@(Get-NetUDPEndpoint -LocalPort 18865).OwningProcess;packPids=@(Get-NetTCPConnection -LocalPort 18866 -State Listen).OwningProcess;gameTask=($scheduler.GetFolder('\').GetTask('KESAS Server')).Xml;files=@(foreach($name in $names|Sort-Object -Unique){[ordered]@{path=$name;sha256=(Get-FileHash -LiteralPath (Join-Path $configuration.nativeRoot $name)).Hash.ToLowerInvariant()}});universe=@(Get-ChildItem -LiteralPath "$($configuration.nativeRoot)/Server/Universe" -Recurse -File|ForEach-Object {[ordered]@{path=$_.FullName.Substring($configuration.nativeRoot.Length);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})}
}
function Protect-Code([string]$File){
    $acl=Get-Acl -LiteralPath $File;$acl.SetAccessRuleProtection($true,$false)
    foreach($rule in $acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$acl.RemoveAccessRuleSpecific($rule)}
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity.User,'ReadAndExecute','Allow'))
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $File -AclObject $acl
}
function Protect-RepairDirectory([string]$Directory){
    New-UpdateDirectory $Directory
    $acl=Get-Acl -LiteralPath $Directory;$acl.SetAccessRuleProtection($true,$false)
    foreach($rule in $acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier])){$acl.RemoveAccessRuleSpecific($rule)}
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $Directory -AclObject $acl
}
function Assert-RepairHash([string]$File,[string]$Expected){
    $null=Assert-UpdatePath $File
    if($Expected -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $File).Hash.ToLowerInvariant() -cne $Expected){throw 'Reviewed repair snapshot differs'}
}
function New-RepairSnapshot([string]$Source,[string]$Snapshot,[string[]]$Names,[System.Collections.IDictionary]$Hashes){
    Protect-RepairDirectory $Snapshot
    foreach($name in $Names){
        $target=Join-Path $Snapshot $name
        Copy-UpdateAtomic (Join-Path $Source $name) $target
        Assert-RepairHash $target $Hashes[$name]
    }
}
function Backup-RepairBootstrap([string]$Bootstrap,[string]$Backup,[string[]]$Names){
    Protect-RepairDirectory $Backup
    foreach($name in $Names){
        $target=Assert-UpdatePath (Join-Path $Bootstrap $name)
        if(Test-Path -LiteralPath $target -PathType Container){throw 'Unexpected bootstrap directory'}
        $exists=Test-Path -LiteralPath $target -PathType Leaf
        $row=[ordered]@{name=$name;existed=$exists;sha256=$null;sddl=$null}
        if($exists){
            $row.sddl=(Get-Acl -LiteralPath $target).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access')
            $row.sha256=(Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant()
            Copy-UpdateAtomic $target (Join-Path $Backup $name)
            Assert-RepairHash (Join-Path $Backup $name) $row.sha256
            Assert-RepairHash $target $row.sha256
        }
        [pscustomobject]$row
    }
}
function Install-RepairBootstrap([string]$Bootstrap,[string]$Snapshot,[string[]]$Names,[System.Collections.IDictionary]$Hashes){
    foreach($name in $Names){
        $source=Join-Path $Snapshot $name;$destination=Join-Path $Bootstrap $name
        Assert-RepairHash $source $Hashes[$name]
        Copy-UpdateAtomic $source $destination;Protect-Code $destination
        Assert-RepairHash $destination $Hashes[$name]
        [ordered]@{path="bootstrap/$name";sha256=$Hashes[$name]}
    }
}
function Restore-RepairBootstrap([string]$Bootstrap,[string]$Backup,[object[]]$Rows){
    # Validate the complete rollback source before changing any installed file.
    foreach($row in $Rows){if($row.existed){Assert-RepairHash (Join-Path $Backup $row.name) $row.sha256}}
    foreach($row in $Rows){
        $target=Assert-UpdatePath (Join-Path $Bootstrap $row.name)
        if(Test-Path -LiteralPath $target -PathType Container){throw 'Unexpected rollback directory'}
        if($row.existed){
            Copy-UpdateAtomic (Join-Path $Backup $row.name) $target
            $acl=Get-Acl -LiteralPath $target
            $acl.SetSecurityDescriptorSddlForm($row.sddl,[Security.AccessControl.AccessControlSections]'Owner,Group,Access')
            [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($target),$acl)
            Assert-RepairHash $target $row.sha256
            if((Get-Acl -LiteralPath $target).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner,Group,Access') -cne $row.sddl){throw 'Restored bootstrap ACL differs'}
        }elseif(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force}
    }
}
function Suspend-RepairTasks([object[]]$Tasks){
    foreach($row in $Tasks){$row.task.Enabled=$false}
    foreach($row in $Tasks){if($row.task.GetInstances(0).Count -gt 0 -or $row.task.State -eq 2){$row.task.Stop(0)}}
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while(@($Tasks|Where-Object {$_.task.GetInstances(0).Count -gt 0 -or $_.task.State -eq 2}).Count){
        if([DateTime]::UtcNow -ge $deadline){throw 'Control tasks did not stop'}
        Start-Sleep -Milliseconds 100
    }
}
function Restore-RepairTaskState([object[]]$Tasks){
    foreach($row in $Tasks){
        # A task can have been disabled while still running before maintenance.
        if($row.running){$row.task.Enabled=$true;$null=$row.task.Run($null)}
        $row.task.Enabled=$row.enabled
    }
}
function Test-RepairPreservation($Before,$After){
    return (($Before|ConvertTo-Json -Depth 12 -Compress) -ceq ($After|ConvertTo-Json -Depth 12 -Compress))
}
try{
    if((& git -C $repository rev-parse HEAD).Trim() -cne $SourceCommit){throw 'Reviewed source commit changed'}
    & git -C $repository diff --quiet HEAD -- @($selected|ForEach-Object {"ControlPlane/$_"}) 'ControlPlane/Repair-HostControl.ps1' 'SOURCE-MANIFEST.json'
    if($LASTEXITCODE -ne 0){throw 'Reviewed source is dirty'}
    # Read the immutable commit, not a worktree manifest that can change after diff.
    $manifestText=(& git -C $repository show "${SourceCommit}:SOURCE-MANIFEST.json"|Out-String)
    if($LASTEXITCODE -ne 0){throw 'Reviewed source manifest is unavailable'}
    $manifest=$manifestText|ConvertFrom-Json;$expectedHashes=@{}
    foreach($name in $selected){
        $entry=@($manifest.files|Where-Object path -CEQ "ControlPlane/$name")
        if($entry.Count -ne 1 -or $entry[0].sha256 -cnotmatch '^[a-f0-9]{64}$'){throw 'Reviewed source inventory differs'}
        $expectedHashes[$name]=$entry[0].sha256
    }
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $bot=$scheduler.GetFolder('\').GetTask('APF Discord Bot');$native=$scheduler.GetFolder('\').GetTask('Aetherfront Native Publisher')
    if($bot.Definition.Principal.RunLevel -ne 0 -or $native.Definition.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')){throw 'Unexpected control identities'}
    Native-Idle
    $before=Preservation
    $lease=[IO.FileStream]::new((Join-Path $configuration.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Native-Idle
    $stamp=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N');$history=Join-Path $root "native-cache/control-repair-$stamp"
    Protect-RepairDirectory $history
    $snapshot=Join-Path $history 'reviewed-source';$backup=Join-Path $history 'prior-bootstrap';$bootstrap=Join-Path $root 'bootstrap'
    New-RepairSnapshot $PSScriptRoot $snapshot $selected $expectedHashes
    $backupRows=@(Backup-RepairBootstrap $bootstrap $backup $selected)
    Write-UpdateAtomic (Join-Path $history 'bootstrap-before.json') ($backupRows|ConvertTo-Json -Depth 8)
    Write-UpdateAtomic (Join-Path $history 'reviewed-hashes.json') ($expectedHashes|ConvertTo-Json)
    foreach($task in @($bot,$native)){
        if($task.State -notin @(1,3,4)){throw 'Ambiguous prior control task state'}
        $priorTasks+=@([pscustomobject]@{name=$task.Name;task=$task;enabled=[bool]$task.Enabled;running=($task.State -eq 4)})
    }
    Write-UpdateAtomic (Join-Path $history 'control-task-state.json') (@($priorTasks|Select-Object name,enabled,running)|ConvertTo-Json)
    [IO.File]::WriteAllText((Join-Path $history 'preservation-before.json'),($before|ConvertTo-Json -Depth 12))
    [IO.File]::WriteAllText((Join-Path $history 'APF-Discord-Bot.xml'),$bot.Xml);[IO.File]::WriteAllText((Join-Path $history 'Native-Publisher.xml'),$native.Xml)
    $result.stage='stopping-idle-control'
    $processes=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -eq 'node.exe' -and $_.ExecutablePath -ieq $configuration.node -and ($_.CommandLine -match [regex]::Escape("$root\bootstrap\anchor.mjs") -or $_.CommandLine -match 'Aetherfront-Control[\\/]managed[\\/]releases[\\/]control-[1-9][0-9]{0,8}-[a-f0-9]{64}[\\/](ControlPlane[\\/](broker-host|worker|observation-publisher)\.mjs|APFDiscordBot[\\/]src[\\/]index\.js)' -or $_.CommandLine -match [regex]::Escape("$root\bootstrap\observation-publisher.mjs"))})
    foreach($process in $processes){if($process.ProcessId -in (@($before.gamePids)+@($before.packPids))){throw 'Invalid control stop target'}}
    $controlTouched=$true
    Suspend-RepairTasks $priorTasks
    Native-Idle
    foreach($process in $processes){$current=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ProcessId)" -ErrorAction SilentlyContinue;if($current -and $current.CreationDate -eq $process.CreationDate -and $current.CommandLine -ceq $process.CommandLine){Stop-Process -Id $process.ProcessId -ErrorAction Stop}}
    $result.stage='installing-reviewed-bootstrap'
    $installationStarted=$true
    $result.hashes=@(Install-RepairBootstrap $bootstrap $snapshot $selected $expectedHashes)
    # Check preservation while the native lease is still held and tasks are disabled.
    $after=Preservation
    [IO.File]::WriteAllText((Join-Path $history 'preservation-after.json'),($after|ConvertTo-Json -Depth 12))
    $result.preserved=Test-RepairPreservation $before $after
    if(-not $result.preserved){throw 'Preservation differs; review exact before/after evidence'}
    $lease.Dispose();$lease=$null
    foreach($row in $priorTasks){$row.task.Enabled=$row.enabled}
    $null=$native.Run($null)
    $result.provisioned=$true;$result.stage='control-started'
}catch{
    $failure=$_;$result.provisioned=$false
    $result.reason='CONTROL_PROVISIONING_FAILED';$result.errorLine=$_.InvocationInfo.ScriptLineNumber
    try{[IO.File]::WriteAllText((Join-Path $root 'native-cache/control-repair-error.json'),($failure|Out-String))}catch{}
    if($controlTouched){
        try{
            if(-not $lease){$lease=[IO.FileStream]::new((Join-Path $configuration.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
            Native-Idle
            Suspend-RepairTasks $priorTasks
            if($installationStarted){Restore-RepairBootstrap $bootstrap $backup $backupRows}
            $restored=Preservation
            Write-UpdateAtomic (Join-Path $history 'preservation-rollback.json') ($restored|ConvertTo-Json -Depth 12)
            $result.rollbackPreserved=Test-RepairPreservation $before $restored
            if(-not $result.rollbackPreserved){throw 'Rollback preservation differs; control remains suspended'}
            $lease.Dispose();$lease=$null
            Restore-RepairTaskState $priorTasks
            $result.rolledBack=$true
        }catch{
            $result.rollbackFailed=$true
            [IO.File]::WriteAllText((Join-Path $root 'native-cache/control-repair-rollback-error.json'),($_|Out-String))
        }
    }
}finally{
    if($lease){$lease.Dispose()}
    $json=$result|ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($output,$json,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText($operatorOutput,$json,[Text.UTF8Encoding]::new($false))
}
if(-not $result.provisioned -or -not $result.preserved){exit 1}
