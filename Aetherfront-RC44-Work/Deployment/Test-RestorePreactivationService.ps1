#requires -Version 7.0
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=Join-Path $PSScriptRoot 'Restore-PreactivationService.ps1'
$tokens=$null;$problems=$null
$tree=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$problems)
if($problems.Count){throw ($problems.Message -join '; ')}
foreach($definition in $tree.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){Invoke-Expression $definition.Extent.Text}
$realProtection=${function:Assert-RecoveryProtection}
$realUnchanged=${function:Assert-RecoveryUnchanged}
$realAlive=${function:Test-RecoveryProcessAlive}
$realHealthy=${function:Test-RecoveryHealthy}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('apf-preactivation-'+[Guid]::NewGuid().ToString('N'))
$held=$null
$passes=0

function Assert-Fixture([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Pass-Fixture([string]$Name){$script:passes++;Write-Output "PASS $Name"}
function Reject-Fixture([scriptblock]$Action,[string]$Contains){
    $caught=$null
    try{& $Action|Out-Null}catch{$caught=$_}
    Assert-Fixture ($null -ne $caught -and $caught.Exception.Message.Contains($Contains)) "Expected rejection: $Contains; actual: $caught"
}
function Write-Fixture([string]$Path,[string]$Text){
    Assert-Fixture ([IO.Path]::GetFullPath($Path).StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)) 'Fixture write escaped temporary root'
    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))
}
function Get-FixtureHash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Get-FixtureTextHash([string]$Text){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{[BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($Text))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function Get-RecoveryControlRoot {$script:fixtureControl}
function Assert-RecoveryProtection {param($Path,[switch]$Private,[switch]$ControlRoot) if($script:rejectProtection){throw 'Recovery permissions are not protected'}}
function Get-RecoveryTaskXml {param($TaskName,$TaskPath,$ErrorAction) '<Task>fixed fixture router</Task>'}
function Get-RecoveryScheduledTask {
    param($TaskName,$TaskPath,$ErrorAction)
    if($script:taskOverride){return $script:taskOverride}
    $control=$script:fixtureControl
    $arguments="-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$control/bootstrap/Start-NativeServer.ps1`" -Settings `"$control/host.json`""
    [pscustomobject]@{State='Ready';Principal=[pscustomobject]@{UserId='SYSTEM'};Actions=@([pscustomobject]@{Execute=(Join-Path ([Environment]::GetFolderPath('Windows')) 'System32/WindowsPowerShell/v1.0/powershell.exe');Arguments=$arguments;WorkingDirectory=$script:fixtureNative})}
}

try{
    $script:taskOverride=$null
    $script:fixtureControl=Join-Path $fixture 'control'
    $script:fixtureNative=Join-Path $fixture 'native'
    $selected=Join-Path $fixtureControl 'bootstrap/native'
    $id='a'*32
    $directory=Join-Path $fixtureControl "native-cache/preactivation-recovery/$id"
    $planPath=Join-Path $directory 'plan.json'
    $copy=Join-Path $directory 'Restore-PreactivationService.ps1'
    $script:rejectProtection=$false
    $required=Get-RecoveryRequiredPaths 'kesas-rc43'
    $nativeNames=$required.Native
    $selectedNames=$required.Selected
    $controlNames=$required.Control
    foreach($name in $nativeNames){Write-Fixture (Join-Path $fixtureNative $name) ('old fixture '+$name)}
    foreach($name in $selectedNames){Write-Fixture (Join-Path $selected $name) ('fixed fixture '+$name)}
    Write-Fixture (Join-Path $fixtureNative 'deployment.json') (@{gamePort=18865;packPort=18866;certificateSha256=('b'*64)}|ConvertTo-Json)
    Write-Fixture (Join-Path $fixtureNative 'Updates/APFClientUpdate.json') '{"version":"kesas-rc43"}'
    Write-Fixture (Join-Path $fixtureNative 'Updates/kesas-rc43/APFClientUpdate.json') '{"version":"kesas-rc43"}'
    Write-Fixture (Join-Path $fixtureNative 'Server/Config/APFPack.json') '{"requiredClientVersion":"kesas-rc43","packVersion":"kesas-rc43","serverId":"preserved","packId":"preserved"}'
    Write-Fixture (Join-Path $fixtureControl 'host.json') (@{root=$fixtureControl;nativeRoot=$fixtureNative}|ConvertTo-Json)
    Write-Fixture (Join-Path $fixtureControl 'native-cache/current-native.json') (@{directory=$selected;sha256=(Get-FixtureHash (Join-Path $selected 'Start-Server.ps1'))}|ConvertTo-Json)
    Write-Fixture (Join-Path $fixtureControl 'bootstrap/Start-NativeServer.ps1') 'fixed fixture router; never executed'
    Write-Fixture $copy ([IO.File]::ReadAllText($source))
    $created=[DateTimeOffset]::UtcNow
    $plan=[pscustomobject]@{
        schemaVersion=1;purpose='restore-preactivation-service';id=$id;createdAt=$created.ToString('o');expiresAt=$created.AddMinutes(30).ToString('o')
        nativeRoot=$fixtureNative;selectedRoot=$selected;oldVersion='kesas-rc43';candidateVersion='kesas-rc45';expectedGamePid=101
        publisher=[pscustomobject]@{pid=100;startedAt=$created.AddMinutes(-1).ToString('o')}
        originalProcesses=@([pscustomobject]@{role='game';pid=101;startedAt=$created.AddHours(-1).ToString('o')},[pscustomobject]@{role='pack';pid=102;startedAt=$created.AddHours(-1).ToString('o')})
        targetHashes=@(foreach($name in $nativeNames){[pscustomobject]@{path=$name;sha256=(Get-FixtureHash (Join-Path $fixtureNative $name))}})
        selectedHashes=@(foreach($name in $selectedNames){[pscustomobject]@{path=$name;sha256=(Get-FixtureHash (Join-Path $selected $name))}})
        controlHashes=@(foreach($name in $controlNames){[pscustomobject]@{path=$name;sha256=(Get-FixtureHash (Join-Path $fixtureControl $name))}})
        taskXmlSha256=(Get-FixtureTextHash '<Task>fixed fixture router</Task>');watchdogSha256=(Get-FixtureHash $copy)
    }
    Write-Fixture $planPath ($plan|ConvertTo-Json -Depth 8)
    $context=New-RecoveryContext $planPath $copy
    $configuration=Assert-RecoveryUnchanged $context
    Assert-Fixture ($configuration.gamePort -eq 18865) 'Valid old release rejected'
    Pass-Fixture 'protected-plan schema, exact roots, old hashes/feed and fixed task binding'

    $script:taskOverride=Get-RecoveryScheduledTask -TaskName 'KESAS Server'
    $script:taskOverride.Actions[0].Execute=$script:taskOverride.Actions[0].Execute.Replace('\','/')
    $script:taskOverride.Actions[0].WorkingDirectory=$script:taskOverride.Actions[0].WorkingDirectory.Replace('\','/')
    $null=Assert-RecoveryUnchanged $context
    Pass-Fixture 'fixed router accepts canonical equivalent task executable and working directory'
    $script:taskOverride=$null
    foreach($change in @('principal','execute','arguments','directory','extra-action')){
        $task=Get-RecoveryScheduledTask -TaskName 'KESAS Server';$script:taskOverride=$task
        switch($change){
            'principal' {$task.Principal.UserId='untrusted-user'}
            'execute' {$task.Actions[0].Execute=Join-Path $fixture 'wrong.exe'}
            'arguments' {$task.Actions[0].Arguments+=' -Command unexpected'}
            'directory' {$task.Actions[0].WorkingDirectory=$fixture}
            'extra-action' {$task.Actions+=@($task.Actions[0])}
        }
        Reject-Fixture {Assert-RecoveryUnchanged $context} 'Fixed KESAS Server task'
        $script:taskOverride=$null
    }
    Pass-Fixture 'changed principal executable arguments directory and extra task actions rejected'

    Assert-Fixture ((Get-RecoveryReleaseNumber 'kesas-rc9223372036854775807') -eq [long]::MaxValue) 'Positive Int64 maximum rejected'
    foreach($version in @('kesas-rc0','kesas-rc01','kesas-rc-1','KESAS-rc45','kesas-rc9223372036854775808')){Reject-Fixture {Get-RecoveryReleaseNumber $version} 'positive Int64'}
    $initialPlan=[IO.File]::ReadAllText($planPath)
    $plan.candidateVersion='kesas-rc43'
    Write-Fixture $planPath ($plan|ConvertTo-Json -Depth 8)
    Reject-Fixture {New-RecoveryContext $planPath $copy} 'must be newer'
    $plan.candidateVersion='kesas-rc45'
    Write-Fixture $planPath $initialPlan
    Assert-Fixture ((New-RecoveryContext $planPath $copy).Plan.candidateVersion -ceq 'kesas-rc45') 'Combined RC45 candidate rejected'
    Pass-Fixture 'RC43 to RC45 accepted; positive Int64 boundaries and increasing versions enforced'

    $script:rejectProtection=$true
    Reject-Fixture {New-RecoveryContext $planPath $copy} 'permissions are not protected'
    $script:rejectProtection=$false
    Reject-Fixture {New-RecoveryContext $planPath $source} 'fixed protected script copy'
    Pass-Fixture 'unprotected plan and arbitrary script location rejected'

    function Get-Acl {
        param($LiteralPath,$ErrorAction)
        $acl=[pscustomobject]@{}
        $acl|Add-Member ScriptMethod GetOwner {param($Type)[pscustomobject]@{Value='S-1-5-21-100-100-100-1000'}}
        return $acl
    }
    Reject-Fixture {& $realProtection $planPath -Private} 'owned by Administrators or SYSTEM'
    Pass-Fixture 'real ACL guard rejects a non-administrator owner'
    function Get-Acl {
        param($LiteralPath,$ErrorAction)
        $acl=[pscustomobject]@{}
        $acl|Add-Member ScriptMethod GetOwner {param($Type)[pscustomobject]@{Value='S-1-5-32-544'}}
        $acl|Add-Member ScriptMethod GetAccessRules {param($Explicit,$Inherited,$Type)[pscustomobject]@{AccessControlType='Allow';PropagationFlags=[Security.AccessControl.PropagationFlags]::None;IdentityReference=[pscustomobject]@{Value='S-1-5-21-100-100-100-1000'};FileSystemRights=[Security.AccessControl.FileSystemRights]::Read}}
        return $acl
    }
    Reject-Fixture {& $realProtection $planPath -Private} 'permissions are not protected'
    Pass-Fixture 'private plan ACL rejects other users even with read-only access'

    $initialPlan=[IO.File]::ReadAllText($planPath)
    $plan|Add-Member NoteProperty command 'must never execute'
    Write-Fixture $planPath ($plan|ConvertTo-Json -Depth 8)
    Reject-Fixture {New-RecoveryContext $planPath $copy} 'Unsupported recovery schema'
    $plan.PSObject.Properties.Remove('command')
    $originalExpiry=$plan.expiresAt
    $plan.expiresAt=$created.AddMinutes(31).ToString('o')
    Write-Fixture $planPath ($plan|ConvertTo-Json -Depth 8)
    Reject-Fixture {New-RecoveryContext $planPath $copy} 'within 30 minutes'
    $plan.expiresAt=$originalExpiry
    Write-Fixture $planPath $initialPlan
    Pass-Fixture 'arbitrary command fields and plans exceeding 30 minutes rejected'

    $duplicate=Join-Path $directory 'duplicate-fixture.json'
    Write-Fixture $duplicate '{"schemaVersion":1,"schemaVersion":2}'
    Reject-Fixture {Read-RecoveryJson $duplicate} 'Duplicate recovery JSON'
    Reject-Fixture {Get-RecoveryChild $fixtureNative '../outside'} 'Unsafe recovery relative path'
    Pass-Fixture 'duplicate JSON and traversal rejected'

    $serverFile=Join-Path $fixtureNative 'Server/Server.exe'
    $oldBytes=[IO.File]::ReadAllText($serverFile)
    Write-Fixture $serverFile 'changed fixture binary'
    Reject-Fixture {Assert-RecoveryUnchanged $context} 'hash mismatch'
    Write-Fixture $serverFile $oldBytes
    $journal=Join-Path $fixtureNative '.update-transaction.json'
    Write-Fixture $journal '{"phase":"native-activation"}'
    Reject-Fixture {Assert-RecoveryUnchanged $context} 'Activation journal'
    Assert-Fixture ([IO.File]::ReadAllText($journal) -ceq '{"phase":"native-activation"}') 'Journal was altered'
    [IO.File]::Delete($journal)
    $retained=Join-Path $fixtureNative 'Updates/kesas-rc44'
    $null=[IO.Directory]::CreateDirectory($retained)
    $null=Assert-RecoveryUnchanged $context
    $candidate=Join-Path $fixtureNative 'Updates/kesas-rc45'
    $null=[IO.Directory]::CreateDirectory($candidate)
    Reject-Fixture {Assert-RecoveryUnchanged $context} 'candidate release exists'
    [IO.Directory]::Delete($candidate)
    Pass-Fixture 'mismatch and journal reject; retained RC44 allowed; RC45 candidate blocks recovery'

    $held=Open-RecoveryLock (Join-Path $fixtureNative '.publish.lock')
    Assert-Fixture ($null -eq (Open-RecoveryLock (Join-Path $fixtureNative '.publish.lock'))) 'Occupied publication lease was admitted'
    $held.Dispose();$held=$null
    $context.ArmedAt=[DateTimeOffset]::UtcNow.ToString('o')
    $report=Write-RecoveryReport $context 'pending' 'fixture-pending'
    $disk=Read-RecoveryJson (Join-Path $directory 'report.json')
    Assert-Fixture ($disk.status -ceq 'pending' -and $disk.planSha256 -ceq $context.PlanSha256 -and $report.reason -ceq 'fixture-pending') 'Durable report missing'
    Pass-Fixture 'real exclusive lease and durable atomic pending report'

    function Get-RecoveryProcess {
        param($ProcessId)
        $identity=if($ProcessId -eq 100){$script:scenarioContext.Plan.publisher}else{@($script:scenarioContext.Plan.originalProcesses|Where-Object pid -eq $ProcessId)[0]}
        $relative=if($ProcessId -eq 101){'Server/Server.exe'}else{'PackHost/ApfPackHost.exe'}
        [pscustomobject]@{Pid=$ProcessId;StartedAt=(Get-RecoveryInstant $identity.startedAt).UtcDateTime.AddSeconds($script:reuseOffset);Path=(Get-RecoveryChild $script:scenarioContext.NativeRoot $relative)}
    }
    $script:scenarioContext=$context;$script:reuseOffset=1
    Assert-Fixture (-not (& $realAlive $plan.publisher)) 'Reused PID was mistaken for original publisher'
    $script:reuseOffset=0
    Assert-Fixture (& $realAlive $plan.publisher) 'Original process creation identity rejected'
    Pass-Fixture 'publisher PID reuse distinguished by creation time'

    $services=[pscustomobject]@{Processes=@([pscustomobject]@{Pid=101;Path=(Get-RecoveryChild $fixtureNative 'Server/Server.exe')},[pscustomobject]@{Pid=102;Path=(Get-RecoveryChild $fixtureNative 'PackHost/ApfPackHost.exe')});Udp=@([pscustomobject]@{OwningProcess=999});Tcp=@([pscustomobject]@{OwningProcess=102})}
    Assert-Fixture (-not (& $realHealthy $context $configuration $services)) 'Unrelated UDP owner was healthy'
    $services.Processes+=@([pscustomobject]@{Pid=103;Path=(Get-RecoveryChild $fixtureNative 'Server/Server.exe')})
    Assert-Fixture (-not (& $realHealthy $context $configuration $services)) 'Duplicate game processes were healthy'
    Pass-Fixture 'real healthy-service gate rejects wrong listener ownership and duplicate games'

    function Assert-RecoveryUnchanged {
        param($Context)
        $script:checksAtStep++
        if($script:step -gt 0 -and $script:scenario -eq 'mismatch'){throw 'Prior release hash mismatch: Server/Server.exe'}
        if($script:step -gt 0 -and $script:scenario -eq 'journal'){throw 'Activation journal or candidate release exists; preserve it for review'}
        if($script:scenario -eq 'expired-before-start' -and $script:step -ge $script:exitAt -and $script:checksAtStep -gt 1){$Context.ExpiresAt=[DateTimeOffset]::UtcNow.AddSeconds(-1)}
        return [pscustomobject]@{gamePort=18865;packPort=18866;certificateSha256=('b'*64)}
    }
    function Test-RecoveryProcessAlive {
        param($Identity)
        if($Identity.pid -eq 100){return $script:scenario -eq 'live-publisher-journal' -or $script:step -lt 1}
        return $script:step -lt $script:exitAt
    }
    function Get-RecoveryServices {
        param($Context,$Configuration)
        $healthy=$script:scenario -eq 'healthy' -or ($script:starts -gt 0 -and $script:scenario -ne 'start-once')
        $processes=@();$udp=@();$tcp=@();$task='Ready'
        if($script:scenario -eq 'occupied-port'){$udp=@([pscustomobject]@{OwningProcess=999})}
        if($script:scenario -eq 'occupied-process'){$processes=@([pscustomobject]@{Pid=999})}
        if($script:scenario -eq 'running-task'){$task='Running'}
        return [pscustomobject]@{Processes=$processes;Udp=$udp;Tcp=$tcp;TaskState=$task;Healthy=$healthy}
    }
    function Test-RecoveryHealthy {param($Context,$Configuration,$Services) return $Services.Healthy}
    function Start-ScheduledTask {
        param($TaskName,$TaskPath,$ErrorAction)
        Assert-Fixture ($TaskName -ceq 'KESAS Server' -and $TaskPath -ceq '\') 'Wrong task requested'
        Assert-Fixture ($script:step -ge $script:exitAt) 'Original game process still alive at start'
        Assert-Fixture ($null -eq (Open-RecoveryLock (Get-RecoveryChild $script:scenarioContext.NativeRoot '.publish.lock'))) 'Start did not retain publication lease'
        $probe=Open-RecoveryLock (Get-RecoveryChild $script:scenarioContext.NativeRoot '.server.lock')
        Assert-Fixture ($null -ne $probe) 'Start retained server lock and would block its launcher'
        $probe.Dispose()
        Assert-Fixture ([IO.File]::Exists((Get-RecoveryChild $script:scenarioContext.Directory 'start-attempted.json'))) 'Start lacks a durable once-only marker'
        $script:starts++
    }
    function Start-Sleep {
        param($Milliseconds)
        Assert-Fixture ($Milliseconds -eq 1000) 'Unexpected polling interval'
        $script:step++
        $script:checksAtStep=0
        if($script:scenario -eq 'live-publisher-journal' -and $script:step -eq 1){Write-Fixture (Join-Path $script:fixtureNative '.update-transaction.json') '{"phase":"native-activation"}'}
        if($script:step -ge $script:maximumSteps){$script:scenarioContext.ExpiresAt=[DateTimeOffset]::UtcNow.AddSeconds(-1)}
    }
    foreach($name in @('late-exit','parent-crash','mismatch','healthy','journal','live-publisher-journal','occupied-port','occupied-process','running-task','occupied-lease','occupied-server-lock','expired-before-start','start-once')){
        $script:scenario=$name;$script:step=0;$script:starts=0;$script:reuseOffset=0;$script:checksAtStep=0
        $script:exitAt=if($name -eq 'late-exit'){350}else{3}
        $script:maximumSteps=if($name -eq 'late-exit'){360}else{7}
        $caseDirectory=Join-Path $fixture ('case-'+$name)
        $null=[IO.Directory]::CreateDirectory($caseDirectory)
        $script:scenarioContext=[pscustomobject]@{Plan=$plan;PlanPath=$context.PlanPath;PlanSha256=$context.PlanSha256;ControlRoot=$context.ControlRoot;NativeRoot=$context.NativeRoot;SelectedRoot=$context.SelectedRoot;Directory=$caseDirectory;ExpiresAt=[DateTimeOffset]::UtcNow.AddMinutes(30);ArmedAt=$null;LastReason=$null}
        if($name -eq 'occupied-lease'){$held=Open-RecoveryLock (Get-RecoveryChild $fixtureNative '.publish.lock')}
        if($name -eq 'occupied-server-lock'){$held=Open-RecoveryLock (Get-RecoveryChild $fixtureNative '.server.lock')}
        $result=Invoke-PreactivationRecovery $scenarioContext
        if($held){$held.Dispose();$held=$null}
        switch($name){
            {$_ -in @('late-exit','parent-crash')} {Assert-Fixture ($result.status -ceq 'success' -and $script:starts -eq 1 -and $script:step -ge $script:exitAt) "Failed late recovery: $name"}
            'healthy' {Assert-Fixture ($result.status -ceq 'success' -and $script:starts -eq 0) 'Healthy old services were restarted'}
            {$_ -in @('mismatch','journal','live-publisher-journal')} {Assert-Fixture ($result.status -ceq 'needs-review' -and $script:starts -eq 0) "Unsafe state was admitted: $name"}
            'start-once' {
                Assert-Fixture ($result.status -ceq 'pending' -and $script:starts -eq 1) 'Failed start did not remain pending'
                $script:scenarioContext.ExpiresAt=[DateTimeOffset]::UtcNow.AddMinutes(30)
                $script:maximumSteps=$script:step+3
                $again=Invoke-PreactivationRecovery $scenarioContext
                Assert-Fixture ($again.status -ceq 'pending' -and $script:starts -eq 1) 'Watchdog restart caused a duplicate start'
            }
            default {Assert-Fixture ($result.status -ceq 'pending' -and $script:starts -eq 0 -and $result.reason -ceq 'deadline-expired') "Occupied resource admitted: $name"}
        }
        $disk=Read-RecoveryJson (Join-Path $caseDirectory 'report.json')
        Assert-Fixture ($disk.status -ceq $result.status -and $disk.armedAt) "Durable result missing: $name"
        if($name -eq 'live-publisher-journal'){
            $journal=Join-Path $script:fixtureNative '.update-transaction.json'
            Assert-Fixture ([IO.File]::ReadAllText($journal) -ceq '{"phase":"native-activation"}') 'Live publisher journal was changed'
            [IO.File]::Delete($journal)
        }
        Pass-Fixture $name
    }
    Write-Output "PASS $passes isolated checks; no real task, game, publisher or health probe invoked"
}finally{
    if($held){$held.Dispose()}
    $resolved=[IO.Path]::GetFullPath($fixture)
    $temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if($resolved.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^apf-preactivation-[a-f0-9]{32}$'){
        if([IO.Directory]::Exists($resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
    }else{throw 'Refusing cleanup outside the exact disposable fixture directory'}
}
