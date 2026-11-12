#requires -Version 7.0
$script:PreactivationRecoverySource=Join-Path $PSScriptRoot 'Restore-PreactivationService.ps1'
$script:PreactivationOwnedChildren=[Collections.Generic.Dictionary[string,object]]::new()

function Initialize-PreactivationRecovery {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'An already elevated PowerShell 7 caller is required; no elevation is requested'}
    $source=$script:PreactivationRecoverySource
    foreach($path in @($source,[IO.Path]::GetDirectoryName($source))){
        $acl=Get-Acl -LiteralPath $path -ErrorAction Stop
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'The fixed watchdog source must be administrator-owned'}
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
            if($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership')){throw 'The fixed watchdog source is writable by another identity'}
        }
    }
    for($cursor=$source;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint){throw 'Unsafe watchdog source path'}}
    $tokens=$null;$problems=$null
    $tree=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$problems)
    if($problems.Count){throw 'The fixed watchdog does not parse'}
    foreach($definition in $tree.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){
        Invoke-Expression ($definition.Extent.Text -replace '^function\s+','function script:')
    }
}

function Set-PreactivationPrivateAcl([string]$Path,[switch]$Directory) {
    $acl=if($Directory){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($value in @('S-1-5-18','S-1-5-32-544')){
        $sid=[Security.Principal.SecurityIdentifier]::new($value)
        $rule=if($Directory){[Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')}else{[Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','Allow')}
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
    Assert-RecoveryProtection $Path -Private
}

function Get-PreactivationNativeInventory([string]$Root,[string[]]$Required) {
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($name in $Required){$null=$names.Add($name)}
    $pending=[Collections.Generic.Stack[string]]::new()
    foreach($family in @('Server','PackHost','CertificateTool','UpdateTool')){$pending.Push((Get-RecoveryChild $Root $family))}
    while($pending.Count){
        foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop){
            $path=Assert-RecoveryLocalPath $item.FullName
            if($item.PSIsContainer -and $item.Name -iin @('Config','Universe','Aetherfront','Logs','secrets','APFDiscordBridge')){continue}
            if($item.PSIsContainer){$pending.Push($path);continue}
            if($item.Extension -inotmatch '^\.(exe|dll|pdb|config)$' -and $item.Name -inotmatch '\.(deps|runtimeconfig(?:\.dev)?)\.json$'){continue}
            $relative=$path.Substring($Root.TrimEnd('\').Length+1).Replace('\','/')
            $null=$names.Add($relative)
        }
    }
    return @(foreach($name in $names|Sort-Object){[pscustomobject]@{path=$name;sha256=(Get-FileHash -LiteralPath (Get-RecoveryChild $Root $name) -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()}})
}

function Start-PreactivationRecovery {
    [CmdletBinding(PositionalBinding=$false)]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$SelectedRoot,
        [Parameter(Mandatory)][string]$OldVersion,
        [Parameter(Mandatory)][string]$CandidateVersion,
        [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$ExpectedGamePid
    )
    $ErrorActionPreference='Stop'
    Initialize-PreactivationRecovery
    $rootPath=Assert-RecoveryLocalPath $Root
    $selectedPath=Assert-RecoveryLocalPath $SelectedRoot
    $control=Get-RecoveryControlRoot
    Assert-RecoveryProtection $control -ControlRoot
    $cache=Get-RecoveryChild $control 'native-cache'
    Assert-RecoveryProtection $cache -Private
    foreach($relative in @('host.json','native-cache/current-native.json')){Assert-RecoveryProtection (Get-RecoveryChild $control $relative)}
    $settings=Read-RecoveryJson (Get-RecoveryChild $control 'host.json')
    $selection=Read-RecoveryJson (Get-RecoveryChild $control 'native-cache/current-native.json')
    if((Assert-RecoveryLocalPath $settings.root) -ine $control -or (Assert-RecoveryLocalPath $settings.nativeRoot) -ine $rootPath -or (Assert-RecoveryLocalPath $selection.directory) -ine $selectedPath){throw 'Arm roots differ from the protected host settings or native selection'}
    if((Get-RecoveryReleaseNumber $CandidateVersion) -le (Get-RecoveryReleaseNumber $OldVersion)){throw 'CandidateVersion must be newer than OldVersion'}
    $required=Get-RecoveryRequiredPaths $OldVersion
    $targetHashes=@(Get-PreactivationNativeInventory $rootPath $required.Native)
    $selectedHashes=@(foreach($name in $required.Selected){[pscustomobject]@{path=$name;sha256=(Get-FileHash -LiteralPath (Get-RecoveryChild $selectedPath $name) -Algorithm SHA256).Hash.ToLowerInvariant()}})
    $controlHashes=@(foreach($name in $required.Control){[pscustomobject]@{path=$name;sha256=(Get-FileHash -LiteralPath (Get-RecoveryChild $control $name) -Algorithm SHA256).Hash.ToLowerInvariant()}})
    $configuration=Read-RecoveryJson (Get-RecoveryChild $rootPath 'deployment.json')
    $snapshot=Get-RecoveryServices ([pscustomobject]@{NativeRoot=$rootPath}) $configuration
    $game=@($snapshot.Processes|Where-Object Path -ieq (Get-RecoveryChild $rootPath 'Server/Server.exe'))
    $pack=@($snapshot.Processes|Where-Object Path -ieq (Get-RecoveryChild $rootPath 'PackHost/ApfPackHost.exe'))
    if($game.Count -ne 1 -or $pack.Count -ne 1 -or $game[0].Pid -ne $ExpectedGamePid){throw 'Expected original game and pack process identities are not uniquely active'}
    $parent=Get-RecoveryProcess $PID
    if(-not $parent){throw 'Cannot capture the publisher creation identity'}
    $engine=$parent.Path
    if([IO.Path]::GetFileName($engine) -ine 'pwsh.exe'){throw 'The caller must already be running in PowerShell 7'}
    $base=Get-RecoveryChild $cache 'preactivation-recovery'
    if([IO.Directory]::Exists($base)){Assert-RecoveryProtection $base -Private}else{$null=[IO.Directory]::CreateDirectory($base);Set-PreactivationPrivateAcl $base -Directory}
    $id=[Guid]::NewGuid().ToString('N')
    $directory=Get-RecoveryChild $base $id
    if(Test-Path -LiteralPath $directory){throw 'Recovery plan directory already exists'}
    $null=[IO.Directory]::CreateDirectory($directory)
    Set-PreactivationPrivateAcl $directory -Directory
    $copy=Get-RecoveryChild $directory 'Restore-PreactivationService.ps1'
    [IO.File]::Copy($script:PreactivationRecoverySource,$copy,$false)
    Set-PreactivationPrivateAcl $copy
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$taskHash=[BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes((Get-RecoveryTaskXml)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    $created=[DateTimeOffset]::UtcNow
    $plan=[pscustomobject]@{
        schemaVersion=1;purpose='restore-preactivation-service';id=$id;createdAt=$created.ToString('o');expiresAt=$created.AddMinutes(30).ToString('o')
        nativeRoot=$rootPath;selectedRoot=$selectedPath;oldVersion=$OldVersion;candidateVersion=$CandidateVersion;expectedGamePid=$ExpectedGamePid
        publisher=[pscustomobject]@{pid=$parent.Pid;startedAt=$parent.StartedAt.ToString('o')}
        originalProcesses=@([pscustomobject]@{role='game';pid=$game[0].Pid;startedAt=$game[0].StartedAt.ToString('o')},[pscustomobject]@{role='pack';pid=$pack[0].Pid;startedAt=$pack[0].StartedAt.ToString('o')})
        targetHashes=$targetHashes;selectedHashes=$selectedHashes;controlHashes=$controlHashes;taskXmlSha256=$taskHash;watchdogSha256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $planPath=Get-RecoveryChild $directory 'plan.json'
    $stream=[IO.FileStream]::new($planPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($plan|ConvertTo-Json -Depth 8));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    Set-PreactivationPrivateAcl $planPath
    $context=New-RecoveryContext $planPath $copy
    $null=Assert-RecoveryUnchanged $context
    $child=Start-Process -FilePath $engine -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-WindowStyle','Hidden','-File',"`"$copy`"",'-Plan',"`"$planPath`"") -WindowStyle Hidden -RedirectStandardOutput (Get-RecoveryChild $directory 'watchdog.stdout.log') -RedirectStandardError (Get-RecoveryChild $directory 'watchdog.stderr.log') -PassThru
    $child.Refresh()
    $owned=[pscustomobject]@{Id=$id;Process=$child;ProcessId=$child.Id;StartedAt=$child.StartTime.ToUniversalTime();Engine=$engine;ParentId=$PID;ParentStartedAt=$parent.StartedAt;Context=$context;Plan=$plan;Status=$null;Disarmed=$false}
    $script:PreactivationOwnedChildren.Add($id,[pscustomobject]@{Handle=$owned;Process=$child;ProcessId=$owned.ProcessId;StartedAt=$owned.StartedAt;Engine=$engine;ParentId=$PID;ParentStartedAt=$parent.StartedAt})
    return (Wait-PreactivationArmed $owned)
}

function Wait-PreactivationArmed($Owned,[ValidateRange(0.01,180)][double]$TimeoutSeconds=180) {
    $child=$Owned.Process;$context=$Owned.Context;$directory=$context.Directory;$id=$Owned.Id
    $clock=[Diagnostics.Stopwatch]::StartNew();$lastReason='no report'
    while($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds){
        $child.Refresh()
        if($child.HasExited){throw "Recovery watchdog exited before arming; preserve $directory"}
        $reportPath=Get-RecoveryChild $directory 'report.json'
        if([IO.File]::Exists($reportPath)){
            Assert-RecoveryProtection $reportPath -Private
            $report=Read-RecoveryJson $reportPath 8192
            if($report.id -cne $id -or $report.planSha256 -cne $context.PlanSha256){throw "Recovery arm report identity mismatch; preserve $directory"}
            $lastReason=[string]$report.reason
            if($report.status -ceq 'needs-review'){throw "Recovery refused to arm: $($report.reason); preserve $directory"}
            if($report.status -ceq 'pending' -and $report.reason -ceq 'armed' -and $report.armedAt){
                $child.Refresh()
                if($child.HasExited){throw "Recovery watchdog exited during acknowledgement; preserve $directory"}
                $Owned.Status=$report;return $Owned
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Recovery did not acknowledge arming within $TimeoutSeconds seconds (last progress: $lastReason); no stop is authorized. Watchdog retained at $directory"
}

function Stop-PreactivationRecovery {
    [CmdletBinding(PositionalBinding=$false)]
    param([Parameter(Mandatory)]$Recovery,[Parameter(Mandatory)][switch]$VerifiedHappyPath)
    if(-not $VerifiedHappyPath){throw 'Only a verified successful publication may disarm its owned watchdog'}
    if(-not $script:PreactivationOwnedChildren.ContainsKey([string]$Recovery.Id)){throw 'This caller does not own that watchdog object'}
    $registration=$script:PreactivationOwnedChildren[[string]$Recovery.Id]
    if(-not [object]::ReferenceEquals($registration.Handle,$Recovery)){throw 'This caller does not own that watchdog object'}
    $parent=Get-RecoveryProcess $PID
    if($registration.ParentId -ne $PID -or -not $parent -or $parent.StartedAt -ne $registration.ParentStartedAt){throw 'Watchdog ownership belongs to another caller creation identity'}
    $process=$registration.Process
    $process.Refresh()
    if($process.HasExited){$Recovery.Disarmed=$true;return $false}
    $current=Get-RecoveryProcess $registration.ProcessId
    if(-not $current){$process.Refresh();if($process.HasExited){$Recovery.Disarmed=$true;return $false}}
    if(-not $current -or $current.StartedAt -ne $registration.StartedAt -or $current.Path -ine $registration.Engine -or $process.Id -ne $registration.ProcessId){throw 'Owned watchdog process identity changed; no termination requested'}
    try{$process.Kill();$null=$process.WaitForExit(5000)}catch{if(-not $process.HasExited){throw}}
    $Recovery.Disarmed=$true
    return $true
}
