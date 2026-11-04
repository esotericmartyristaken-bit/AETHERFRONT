#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Plan)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Get-RecoveryControlRoot {
    [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Aetherfront-Control'))
}

function Assert-RecoveryLocalPath([string]$Path) {
    if(-not [IO.Path]::IsPathFullyQualified($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':')){throw 'A fixed local path is required'}
    $full=[IO.Path]::GetFullPath($Path)
    foreach($segment in $full.Substring(3).Split([char[]]'\/')){
        if($segment -match '[. ]$|[<>"|?*\x00-\x1f]' -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'){throw 'Unsafe recovery path'}
    }
    for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        try{$attributes=[IO.File]::GetAttributes($cursor)}
        catch [IO.FileNotFoundException]{continue}
        catch [IO.DirectoryNotFoundException]{continue}
        if($attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Recovery refuses reparse points'}
    }
    return $full
}

function Get-RecoveryChild([string]$Root,[string]$Relative) {
    if(-not $Relative -or $Relative.Contains('\') -or $Relative -match '(^|/)\.\.?(/|$)|^/|//|[:<>"|?*\x00-\x1f]'){throw 'Unsafe recovery relative path'}
    $base=(Assert-RecoveryLocalPath $Root).TrimEnd('\','/')
    $child=Assert-RecoveryLocalPath (Join-Path $base $Relative)
    if(-not $child.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Recovery path escapes its root'}
    return $child
}

function Assert-RecoveryProtection([string]$Path,[switch]$Private,[switch]$ControlRoot) {
    $null=Assert-RecoveryLocalPath $Path
    $acl=Get-Acl -LiteralPath $Path -ErrorAction Stop
    $trusted=@('S-1-5-18','S-1-5-32-544')
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'Recovery code and plan must be owned by Administrators or SYSTEM'}
    $dangerous=[Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    if($ControlRoot){$dangerous=[Security.AccessControl.FileSystemRights]'Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)){continue}
        if($rule.IdentityReference.Value -notin $trusted -and ($Private -or ($rule.FileSystemRights -band $dangerous))){throw 'Recovery permissions are not protected'}
    }
}

function Read-RecoveryJson([string]$Path,[int]$MaximumBytes=262144) {
    $null=Assert-RecoveryLocalPath $Path
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -gt $MaximumBytes){throw 'Recovery JSON exceeds its limit'}
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$true)
        try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
    }finally{$stream.Dispose()}
    $json=[Text.Json.JsonDocument]::Parse($text)
    try{
        $pending=[Collections.Generic.Stack[Text.Json.JsonElement]]::new()
        $pending.Push($json.RootElement)
        while($pending.Count){
            $node=$pending.Pop()
            if($node.ValueKind -eq [Text.Json.JsonValueKind]::Object){
                $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach($property in $node.EnumerateObject()){
                    if(-not $names.Add($property.Name)){throw 'Duplicate recovery JSON property'}
                    $pending.Push($property.Value)
                }
            }elseif($node.ValueKind -eq [Text.Json.JsonValueKind]::Array){foreach($value in $node.EnumerateArray()){$pending.Push($value)}}
        }
    }finally{$json.Dispose()}
    return ($text|ConvertFrom-Json -ErrorAction Stop)
}

function Assert-RecoveryFields($Value,[string[]]$Names) {
    if($null -eq $Value -or @($Value.PSObject.Properties).Count -ne $Names.Count -or @($Value.PSObject.Properties|Where-Object Name -cnotin $Names).Count){throw 'Unsupported recovery schema'}
}

function Get-RecoveryInstant($Value) {
    if($Value -is [DateTimeOffset]){return $Value.ToUniversalTime()}
    if($Value -is [DateTime]){return ([DateTimeOffset]::new($Value)).ToUniversalTime()}
    if($Value -isnot [string]){throw 'Invalid recovery timestamp'}
    return [DateTimeOffset]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Get-RecoveryReleaseNumber([string]$Version) {
    [long]$number=0
    if($Version -cnotmatch '^kesas-rc[1-9][0-9]*$' -or -not [long]::TryParse($Version.Substring(8),[ref]$number)){throw 'Recovery versions must be kesas-rc followed by a positive Int64'}
    return $number
}

function Get-RecoveryRequiredPaths([string]$OldVersion) {
    $null=Get-RecoveryReleaseNumber $OldVersion
    return [pscustomobject]@{
        Native=@('deployment.json','Server/Server.exe','PackHost/ApfPackHost.exe','CertificateTool/ApfCertificateTool.exe','UpdateTool/ApfUpdateTool.exe','Server/Config/APFPack.json','Server/Config/AetherfrontTechnology.json','ClientTemplate.zip','KESAS-Client-Bootstrap.zip','KESAS-Client-Bootstrap.zip.sha256','Updates/APFClientUpdate.json',"Updates/$OldVersion/APFClientUpdate.json",'secrets/update-signing-private.xml','secrets/update-signing-public.xml','secrets/pack.pfx')
        Selected=@('Update.Common.ps1','Publish-Release.ps1','Start-Server.ps1','Stop-Server.ps1','GitHub.Publish.ps1','Export-Client.ps1')
        Control=@('host.json','native-cache/current-native.json','bootstrap/Start-NativeServer.ps1')
    }
}

function Assert-RecoveryHashes($Records,[string[]]$Required,[string]$Family) {
    if($Records -isnot [array] -or $Records.Count -gt 2048){throw 'Invalid recovery hash inventory'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($record in $Records){
        Assert-RecoveryFields $record @('path','sha256')
        if($record.path -isnot [string] -or $record.sha256 -isnot [string] -or $record.sha256 -cnotmatch '^[a-f0-9]{64}$' -or -not $seen.Add($record.path)){throw 'Invalid recovery hash record'}
        $allowed=$Required -ccontains $record.path
        if($Family -ceq 'native' -and $record.path -cmatch '^(Server|PackHost|CertificateTool|UpdateTool)/' -and $record.path -cnotmatch '(^|/)(Config|Universe|Aetherfront|Logs|secrets)(/|$)'){$allowed=$true}
        if(-not $allowed){throw 'Recovery hash path is not allowed'}
    }
    foreach($name in $Required){if(-not $seen.Contains($name)){throw "Recovery hash inventory is missing $name"}}
}

function New-RecoveryContext([string]$PlanPath,[string]$ScriptPath) {
    $control=Get-RecoveryControlRoot
    Assert-RecoveryProtection $control -ControlRoot
    $cache=Get-RecoveryChild $control 'native-cache'
    Assert-RecoveryProtection $cache -Private
    $recoveryRoot=Get-RecoveryChild $cache 'preactivation-recovery'
    Assert-RecoveryProtection $recoveryRoot -Private
    $full=Assert-RecoveryLocalPath $PlanPath
    $prefix=$recoveryRoot+'\'
    if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $full.Substring($prefix.Length).Replace('\','/') -cnotmatch '^[a-f0-9]{32}/plan.json$'){throw 'Unexpected protected recovery plan location'}
    $directory=[IO.Path]::GetDirectoryName($full)
    Assert-RecoveryProtection $directory -Private
    Assert-RecoveryProtection $full -Private
    $script=Get-RecoveryChild $directory 'Restore-PreactivationService.ps1'
    if((Assert-RecoveryLocalPath $ScriptPath) -ine $script){throw 'Recovery must run the fixed protected script copy'}
    Assert-RecoveryProtection $script -Private
    $plan=Read-RecoveryJson $full
    Assert-RecoveryFields $plan @('schemaVersion','purpose','id','createdAt','expiresAt','nativeRoot','selectedRoot','oldVersion','candidateVersion','expectedGamePid','publisher','originalProcesses','targetHashes','selectedHashes','controlHashes','taskXmlSha256','watchdogSha256')
    if($plan.schemaVersion -ne 1 -or $plan.purpose -cne 'restore-preactivation-service' -or $plan.id -cne [IO.Path]::GetFileName($directory)){throw 'Unexpected recovery intent'}
    $oldNumber=Get-RecoveryReleaseNumber $plan.oldVersion
    $candidateNumber=Get-RecoveryReleaseNumber $plan.candidateVersion
    if($candidateNumber -le $oldNumber){throw 'Recovery candidate must be newer than the prior release'}
    $created=Get-RecoveryInstant $plan.createdAt
    $expires=Get-RecoveryInstant $plan.expiresAt
    if(($created-[DateTimeOffset]::UtcNow).TotalSeconds -gt 5 -or ($expires-$created).TotalSeconds -le 0 -or ($expires-$created).TotalSeconds -gt 1800){throw 'Recovery plan must expire within 30 minutes'}
    $native=Assert-RecoveryLocalPath $plan.nativeRoot
    $selected=Assert-RecoveryLocalPath $plan.selectedRoot
    foreach($record in @($plan.publisher)+@($plan.originalProcesses)){
        $fields=if($record -eq $plan.publisher){@('pid','startedAt')}else{@('role','pid','startedAt')}
        Assert-RecoveryFields $record $fields
        if($record.pid -isnot [long] -and $record.pid -isnot [int]){throw 'Invalid recovery process ID'}
        if($record.pid -le 0 -or $record.pid -gt [int]::MaxValue -or ((Get-RecoveryInstant $record.startedAt)-$created).TotalSeconds -gt 5){throw 'Invalid recovery process identity'}
    }
    if($plan.originalProcesses -isnot [array] -or $plan.originalProcesses.Count -ne 2 -or @($plan.originalProcesses|Where-Object role -ceq 'game').Count -ne 1 -or @($plan.originalProcesses|Where-Object role -ceq 'pack').Count -ne 1 -or @($plan.originalProcesses.pid|Sort-Object -Unique).Count -ne 2 -or $plan.publisher.pid -in $plan.originalProcesses.pid -or $plan.expectedGamePid -ne @($plan.originalProcesses|Where-Object role -ceq 'game')[0].pid){throw 'Recovery requires both original service identities and the expected game PID'}
    $required=Get-RecoveryRequiredPaths $plan.oldVersion
    Assert-RecoveryHashes $plan.targetHashes $required.Native 'native'
    Assert-RecoveryHashes $plan.selectedHashes $required.Selected 'selected'
    Assert-RecoveryHashes $plan.controlHashes $required.Control 'control'
    foreach($hash in @($plan.taskXmlSha256,$plan.watchdogSha256)){if($hash -isnot [string] -or $hash -cnotmatch '^[a-f0-9]{64}$'){throw 'Invalid recovery code or task hash'}}
    if((Get-FileHash -LiteralPath $script -Algorithm SHA256).Hash -ine $plan.watchdogSha256){throw 'Protected recovery code hash mismatch'}
    return [pscustomobject]@{Plan=$plan;PlanPath=$full;PlanSha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant();ControlRoot=$control;NativeRoot=$native;SelectedRoot=$selected;Directory=$directory;ExpiresAt=$expires;ArmedAt=$null;LastReason=$null}
}

function Write-RecoveryReport($Context,[string]$Status,[string]$Reason) {
    Assert-RecoveryProtection $Context.Directory -Private
    $path=Get-RecoveryChild $Context.Directory 'report.json'
    if([IO.File]::Exists($path)){Assert-RecoveryProtection $path -Private}
    $report=[ordered]@{schemaVersion=1;id=$Context.Plan.id;planSha256=$Context.PlanSha256;status=$Status;reason=$Reason;armedAt=$Context.ArmedAt;updatedAt=[DateTimeOffset]::UtcNow.ToString('o');expiresAt=$Context.ExpiresAt.ToString('o')}
    $temporary=Get-RecoveryChild $Context.Directory ('report-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try{
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Compress));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if([IO.File]::Exists($path)){[IO.File]::Replace($temporary,$path,[Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$path)}
    }finally{if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}}
    $Context.LastReason=$Reason
    return [pscustomobject]$report
}

function Get-RecoveryProcess([int]$ProcessId) {
    $process=$null
    try{
        $process=Get-Process -Id $ProcessId -ErrorAction Stop
        $process.Refresh()
        if($process.HasExited){return $null}
        if(-not $process.Path){throw 'Cannot verify recovery process identity'}
        return [pscustomobject]@{Pid=$process.Id;StartedAt=$process.StartTime.ToUniversalTime();Path=$process.Path}
    }catch{
        $failure=$_
        if($failure.CategoryInfo.Category -eq 'ObjectNotFound'){return $null}
        if($process){try{$process.Refresh();if($process.HasExited){return $null}}catch{}}
        throw $failure
    }
}

function Test-RecoveryProcessAlive($Identity) {
    $current=Get-RecoveryProcess ([int]$Identity.pid)
    return ($null -ne $current -and $current.StartedAt -eq (Get-RecoveryInstant $Identity.startedAt).UtcDateTime)
}

function Open-RecoveryLock([string]$Path) {
    $null=Assert-RecoveryLocalPath $Path
    try{return [IO.FileStream]::new($Path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
    catch [IO.IOException]{if(($_.Exception.GetBaseException().HResult -band 65535) -notin @(32,33)){throw};return $null}
}

function Assert-RecoveryTaskBinding($Context) {
    $task=Get-ScheduledTask -TaskName 'KESAS Server' -TaskPath '\' -ErrorAction Stop
    if(@($task.Actions).Count -ne 1 -or $task.Principal.UserId -notin @('SYSTEM','NT AUTHORITY\SYSTEM','S-1-5-18')){throw 'Fixed KESAS Server task no longer matches its protected router (principal/actions)'}
    $execute=Assert-RecoveryLocalPath (Join-Path ([Environment]::GetFolderPath('Windows')) 'System32/WindowsPowerShell/v1.0/powershell.exe')
    $router=Get-RecoveryChild $Context.ControlRoot 'bootstrap/Start-NativeServer.ps1'
    $settings=Get-RecoveryChild $Context.ControlRoot 'host.json'
    $arguments="-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$router`" -Settings `"$settings`""
    # The installer uses forward slashes in task paths. Keep exact routing and
    # argument checks, but compare executable/directory identities canonically.
    $actualExecute=Assert-RecoveryLocalPath $task.Actions[0].Execute
    $actualDirectory=[IO.Path]::TrimEndingDirectorySeparator((Assert-RecoveryLocalPath $task.Actions[0].WorkingDirectory))
    $expectedDirectory=[IO.Path]::TrimEndingDirectorySeparator((Assert-RecoveryLocalPath $Context.NativeRoot))
    if($actualExecute -ine $execute -or $task.Actions[0].Arguments.Replace('/','\') -ine $arguments -or $actualDirectory -ine $expectedDirectory){throw 'Fixed KESAS Server task no longer matches its protected router (executable/arguments/directory)'}
}

function Assert-RecoveryUnchanged($Context) {
    $plan=$Context.Plan
    if((Get-FileHash -LiteralPath $Context.PlanPath -Algorithm SHA256).Hash -ine $Context.PlanSha256){throw 'Recovery plan changed'}
    foreach($relative in @('.update-transaction.json',"Updates/$($plan.candidateVersion)")){
        if(Test-Path -LiteralPath (Get-RecoveryChild $Context.NativeRoot $relative)){throw 'Activation journal or candidate release exists; preserve it for review'}
    }
    foreach($family in @(@{Root=$Context.NativeRoot;Records=$plan.targetHashes},@{Root=$Context.SelectedRoot;Records=$plan.selectedHashes},@{Root=$Context.ControlRoot;Records=$plan.controlHashes})){
        foreach($record in $family.Records){
            $path=Get-RecoveryChild $family.Root $record.path
            if($family.Root -ine $Context.NativeRoot){
                for($cursor=$path;$cursor -ine $Context.ControlRoot;$cursor=[IO.Path]::GetDirectoryName($cursor)){Assert-RecoveryProtection $cursor}
            }
            if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $record.sha256){throw "Prior release hash mismatch: $($record.path)"}
        }
    }
    $hostSettings=Read-RecoveryJson (Get-RecoveryChild $Context.ControlRoot 'host.json')
    if((Assert-RecoveryLocalPath $hostSettings.root) -ine $Context.ControlRoot -or (Assert-RecoveryLocalPath $hostSettings.nativeRoot) -ine $Context.NativeRoot){throw 'Recovery root differs from protected host settings'}
    $selection=Read-RecoveryJson (Get-RecoveryChild $Context.ControlRoot 'native-cache/current-native.json')
    $managed=Get-RecoveryChild $Context.ControlRoot 'managed/releases'
    $seed=Get-RecoveryChild $Context.ControlRoot 'bootstrap/native'
    $cache=Get-RecoveryChild $Context.ControlRoot 'native-cache/control'
    $isManaged=$Context.SelectedRoot.StartsWith($managed+'\',[StringComparison]::OrdinalIgnoreCase) -and $Context.SelectedRoot.Substring($managed.Length+1).Replace('\','/') -cmatch '^control-[1-9][0-9]{0,8}-[a-f0-9]{64}/KESAS-VPS-Server$'
    if((Assert-RecoveryLocalPath $selection.directory) -ine $Context.SelectedRoot -or ($Context.SelectedRoot -ine $seed -and -not $isManaged -and -not $Context.SelectedRoot.StartsWith($cache+'\',[StringComparison]::OrdinalIgnoreCase)) -or $selection.sha256 -cne @($plan.selectedHashes|Where-Object path -ceq 'Start-Server.ps1')[0].sha256){throw 'Protected native selection changed or is outside approved roots'}
    $feed=Read-RecoveryJson (Get-RecoveryChild $Context.NativeRoot 'Updates/APFClientUpdate.json')
    $pack=Read-RecoveryJson (Get-RecoveryChild $Context.NativeRoot 'Server/Config/APFPack.json')
    if($feed.version -cne $plan.oldVersion -or $pack.requiredClientVersion -cne $plan.oldVersion -or $pack.packVersion -cne $plan.oldVersion){throw 'Old native release is not active'}
    $configuration=Read-RecoveryJson (Get-RecoveryChild $Context.NativeRoot 'deployment.json')
    if($configuration.gamePort -lt 1024 -or $configuration.gamePort -gt 65535 -or $configuration.packPort -lt 1024 -or $configuration.packPort -gt 65535 -or $configuration.gamePort -eq $configuration.packPort -or $configuration.certificateSha256 -cnotmatch '^[a-fA-F0-9]{64}$'){throw 'Invalid pinned native deployment settings'}
    Assert-RecoveryTaskBinding $Context
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$taskHash=[BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes([string](Export-ScheduledTask -TaskName 'KESAS Server' -TaskPath '\' -ErrorAction Stop)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    if($taskHash -cne $plan.taskXmlSha256){throw 'Fixed KESAS Server task definition changed'}
    return $configuration
}

function Get-RecoveryServices($Context,$Configuration) {
    $processErrors=@()
    $candidates=@(Get-Process -Name Server,ApfPackHost -ErrorAction SilentlyContinue -ErrorVariable +processErrors)
    if(@($processErrors|Where-Object {$_.CategoryInfo.Category -ne 'ObjectNotFound'}).Count){throw 'Native service enumeration failed; no start requested'}
    $processes=@(foreach($process in $candidates){
        $identity=Get-RecoveryProcess $process.Id
        if($null -ne $identity -and ($identity.Path -ieq (Get-RecoveryChild $Context.NativeRoot 'Server/Server.exe') -or $identity.Path -ieq (Get-RecoveryChild $Context.NativeRoot 'PackHost/ApfPackHost.exe'))){$identity}
    })
    $udp=@(Get-NetUDPEndpoint -ErrorAction Stop|Where-Object LocalPort -eq $Configuration.gamePort)
    $tcp=@(Get-NetTCPConnection -ErrorAction Stop|Where-Object {$_.LocalPort -eq $Configuration.packPort -and $_.State -eq 'Listen'})
    return [pscustomobject]@{Processes=$processes;Udp=$udp;Tcp=$tcp;TaskState=[string](Get-ScheduledTask -TaskName 'KESAS Server' -TaskPath '\' -ErrorAction Stop).State}
}

function Test-RecoveryHealthy($Context,$Configuration,$Services) {
    $game=@($Services.Processes|Where-Object Path -ieq (Get-RecoveryChild $Context.NativeRoot 'Server/Server.exe'))
    $pack=@($Services.Processes|Where-Object Path -ieq (Get-RecoveryChild $Context.NativeRoot 'PackHost/ApfPackHost.exe'))
    if($game.Count -ne 1 -or $pack.Count -ne 1 -or -not $Services.Udp.Count -or -not $Services.Tcp.Count -or @($Services.Udp|Where-Object OwningProcess -ne $game[0].Pid).Count -or @($Services.Tcp|Where-Object OwningProcess -ne $pack[0].Pid).Count){return $false}
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=Get-RecoveryChild $Context.NativeRoot 'CertificateTool/ApfCertificateTool.exe'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.Arguments="--probe-server https://127.0.0.1:$($Configuration.packPort)/health $($Configuration.certificateSha256)"
    $probe=[Diagnostics.Process]::Start($start)
    try{if(-not $probe.WaitForExit(15000)){throw 'Pinned health probe did not finish; no service start requested'};return $probe.ExitCode -eq 0}finally{$probe.Dispose()}
}

function Invoke-PreactivationRecovery($Context) {
    $watchdogLock=$null;$publicationLock=$null;$serverLock=$null
    $elapsed=[Diagnostics.Stopwatch]::StartNew()
    $budget=[Math]::Min(1800,($Context.ExpiresAt-[DateTimeOffset]::UtcNow).TotalSeconds)
    try{
        $watchdogLock=Open-RecoveryLock (Get-RecoveryChild $Context.Directory '.watchdog.lock')
        if(-not $watchdogLock){throw 'A recovery watchdog already owns this plan'}
        if($budget -le 0){return (Write-RecoveryReport $Context 'pending' 'deadline-expired')}
        $priorPath=Get-RecoveryChild $Context.Directory 'report.json'
        if([IO.File]::Exists($priorPath)){
            Assert-RecoveryProtection $priorPath -Private
            $prior=Read-RecoveryJson $priorPath 8192
            if($prior.planSha256 -cne $Context.PlanSha256 -or $prior.id -cne $Context.Plan.id){throw 'Recovery report does not match its protected plan'}
            if($prior.status -cin @('success','needs-review')){return $prior}
            $Context.ArmedAt=$prior.armedAt
        }
        if(-not $Context.ArmedAt){
            $null=Assert-RecoveryUnchanged $Context
            if(-not (Test-RecoveryProcessAlive $Context.Plan.publisher)){throw 'Publisher exited before recovery was armed'}
            foreach($identity in $Context.Plan.originalProcesses){
                $original=Get-RecoveryProcess $identity.pid
                $relative=if($identity.role -ceq 'game'){'Server/Server.exe'}else{'PackHost/ApfPackHost.exe'}
                if(-not (Test-RecoveryProcessAlive $identity) -or $original.Path -ine (Get-RecoveryChild $Context.NativeRoot $relative)){throw 'An original service exited or changed identity before recovery was armed'}
            }
            $Context.ArmedAt=[DateTimeOffset]::UtcNow.ToString('o')
            $null=Write-RecoveryReport $Context 'pending' 'armed'
        }
        $attemptPath=Get-RecoveryChild $Context.Directory 'start-attempted.json'
        while($elapsed.Elapsed.TotalSeconds -lt $budget -and [DateTimeOffset]::UtcNow -lt $Context.ExpiresAt){
            foreach($relative in @('.update-transaction.json',"Updates/$($Context.Plan.candidateVersion)")){
                if(Test-Path -LiteralPath (Get-RecoveryChild $Context.NativeRoot $relative)){throw 'Activation journal or candidate release exists; preserve it for review'}
            }
            $reason='armed'
            if(-not (Test-RecoveryProcessAlive $Context.Plan.publisher)){
                if(-not $publicationLock){$publicationLock=Open-RecoveryLock (Get-RecoveryChild $Context.NativeRoot '.publish.lock')}
                $reason='publication-lease-occupied'
                if($publicationLock){
                    if(Test-RecoveryProcessAlive $Context.Plan.publisher){throw 'Publisher identity became active again; review the plan'}
                    $configuration=Assert-RecoveryUnchanged $Context
                    $services=Get-RecoveryServices $Context $configuration
                    if(Test-RecoveryHealthy $Context $configuration $services){return (Write-RecoveryReport $Context 'success' 'old-service-healthy')}
                    $reason='old-services-exiting'
                    $originalAlive=@($Context.Plan.originalProcesses|Where-Object {Test-RecoveryProcessAlive $_}).Count -gt 0
                    if(-not $originalAlive -and -not $services.Processes.Count -and -not $services.Udp.Count -and -not $services.Tcp.Count -and $services.TaskState -ceq 'Ready'){
                        if([IO.File]::Exists($attemptPath)){$reason='start-already-attempted'}
                        elseif(Test-Path -LiteralPath (Get-RecoveryChild $Context.ControlRoot 'native-cache/server-start-permit.json')){throw 'An existing publication start permit needs review'}
                        else{
                            $serverLock=Open-RecoveryLock (Get-RecoveryChild $Context.NativeRoot '.server.lock')
                            $reason='server-lock-occupied'
                            if($serverLock){
                                $configuration=Assert-RecoveryUnchanged $Context
                                $services=Get-RecoveryServices $Context $configuration
                                if($services.Processes.Count -or $services.Udp.Count -or $services.Tcp.Count -or $services.TaskState -cne 'Ready' -or (Test-RecoveryProcessAlive $Context.Plan.publisher) -or @($Context.Plan.originalProcesses|Where-Object {Test-RecoveryProcessAlive $_}).Count){throw 'Native liveness changed during recovery; no duplicate start requested'}
                                if($elapsed.Elapsed.TotalSeconds -ge $budget -or [DateTimeOffset]::UtcNow -ge $Context.ExpiresAt){return (Write-RecoveryReport $Context 'pending' 'deadline-expired')}
                                $claim=[IO.FileStream]::new($attemptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                                try{$bytes=[Text.UTF8Encoding]::new($false).GetBytes((@{schemaVersion=1;id=$Context.Plan.id;planSha256=$Context.PlanSha256;attemptedAt=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Compress));$claim.Write($bytes,0,$bytes.Length);$claim.Flush($true)}finally{$claim.Dispose()}
                                $null=Write-RecoveryReport $Context 'pending' 'start-requested'
                                $serverLock.Dispose();$serverLock=$null
                                Start-ScheduledTask -TaskName 'KESAS Server' -TaskPath '\' -ErrorAction Stop
                                $reason='start-requested'
                            }
                        }
                    }
                }
            }
            if($Context.LastReason -cne $reason){$null=Write-RecoveryReport $Context 'pending' $reason}
            Start-Sleep -Milliseconds 1000
        }
        return (Write-RecoveryReport $Context 'pending' 'deadline-expired')
    }catch{
        if(-not $watchdogLock){throw}
        return (Write-RecoveryReport $Context 'needs-review' $_.Exception.Message)
    }finally{
        if($serverLock){$serverLock.Dispose()}
        if($publicationLock){$publicationLock.Dispose()}
        if($watchdogLock){$watchdogLock.Dispose()}
        $elapsed.Stop()
    }
}

$context=New-RecoveryContext $Plan $PSCommandPath
Invoke-PreactivationRecovery $context
