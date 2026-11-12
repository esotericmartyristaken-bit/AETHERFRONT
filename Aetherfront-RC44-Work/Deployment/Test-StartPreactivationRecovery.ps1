#requires -Version 7.0
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Start-PreactivationRecovery.ps1')
$tokens=$null;$problems=$null
$tree=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Restore-PreactivationService.ps1'),[ref]$tokens,[ref]$problems)
if($problems.Count){throw 'Watchdog parse failure'}
foreach($definition in $tree.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){Invoke-Expression $definition.Extent.Text}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('apf-arm-'+[Guid]::NewGuid().ToString('N'))
$script:control=Join-Path $fixture 'control'
$script:native=Join-Path $fixture 'native'
$script:selected=Join-Path $control 'bootstrap/native'
$script:stamp=[DateTime]::UtcNow.AddMinutes(-5)
$script:kills=0;$script:spawns=0;$script:reuse=$false;$script:badReport=$false
function Assert-Test([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Reject-Test([scriptblock]$Action,[string]$Contains){$caught=$null;try{& $Action|Out-Null}catch{$caught=$_};Assert-Test ($null -ne $caught -and $caught.Exception.Message.Contains($Contains)) "Expected $Contains; got $caught"}
function Write-TestFile([string]$Path,[string]$Text){Assert-Test ([IO.Path]::GetFullPath($Path).StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase)) 'Fixture write escape';$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Text)}
function Initialize-PreactivationRecovery {}
function Assert-RecoveryProtection {param($Path,[switch]$Private,[switch]$ControlRoot)}
function Set-PreactivationPrivateAcl {param($Path,[switch]$Directory)}
function Get-RecoveryControlRoot {$script:control}
function Get-RecoveryTaskXml {param($TaskName,$TaskPath,$ErrorAction)'<Task>arm fixture</Task>'}
function Get-RecoveryScheduledTask {
    param($TaskName,$TaskPath,$ErrorAction)
    [pscustomobject]@{State='Running';Principal=[pscustomobject]@{UserId='SYSTEM'};Actions=@([pscustomobject]@{Execute=(Join-Path ([Environment]::GetFolderPath('Windows')) 'System32/WindowsPowerShell/v1.0/powershell.exe');Arguments="-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$control/bootstrap/Start-NativeServer.ps1`" -Settings `"$control/host.json`"";WorkingDirectory=$native})}
}
function Get-RecoveryServices {
    param($Context,$Configuration)
    [pscustomobject]@{Processes=@([pscustomobject]@{Pid=101;StartedAt=$script:stamp;Path=(Get-RecoveryChild $native 'Server/Server.exe')},[pscustomobject]@{Pid=102;StartedAt=$script:stamp;Path=(Get-RecoveryChild $native 'PackHost/ApfPackHost.exe')});Udp=@();Tcp=@();TaskState='Running'}
}
function Get-RecoveryProcess {
    param($ProcessId)
    if($ProcessId -eq $PID){return [pscustomobject]@{Pid=$PID;StartedAt=$script:stamp;Path='C:\Program Files\PowerShell\7\pwsh.exe'}}
    if($ProcessId -eq 230001){return [pscustomobject]@{Pid=230001;StartedAt=$script:stamp.AddSeconds([int]$script:reuse);Path='C:\Program Files\PowerShell\7\pwsh.exe'}}
    throw 'Unexpected fixture process lookup'
}
function Start-Process {
    param($FilePath,$ArgumentList,$WindowStyle,$RedirectStandardOutput,$RedirectStandardError,[switch]$PassThru)
    $script:spawns++
    Assert-Test ($FilePath -ceq 'C:\Program Files\PowerShell\7\pwsh.exe' -and $WindowStyle -ceq 'Hidden' -and $PassThru) 'Wrong launcher options'
    Assert-Test ($ArgumentList.Count -eq 9 -and $ArgumentList[5] -ceq '-File' -and $ArgumentList[7] -ceq '-Plan') 'Unexpected or secret command arguments'
    $path=$ArgumentList[8].Trim('"')
    $copy=$ArgumentList[6].Trim('"')
    $context=New-RecoveryContext $path $copy
    $context.ArmedAt=[DateTimeOffset]::UtcNow.ToString('o')
    if($script:badReport){$null=Write-RecoveryReport $context 'needs-review' 'fixture refusing arm'}else{$null=Write-RecoveryReport $context 'pending' 'armed'}
    $child=[pscustomobject]@{Id=230001;StartTime=$script:stamp;HasExited=$false}
    $child|Add-Member ScriptMethod Refresh {}
    $child|Add-Member ScriptMethod Kill {$script:kills++;$this.HasExited=$true}
    $child|Add-Member ScriptMethod WaitForExit {param($Timeout)return $true}
    return $child
}
try{
    $required=Get-RecoveryRequiredPaths 'kesas-rc43'
    foreach($path in $required.Native){Write-TestFile (Join-Path $native $path) ('old '+$path)}
    foreach($path in $required.Selected){Write-TestFile (Join-Path $selected $path) ('fixed '+$path)}
    Write-TestFile (Join-Path $native 'deployment.json') (@{gamePort=18865;packPort=18866;certificateSha256=('a'*64)}|ConvertTo-Json)
    Write-TestFile (Join-Path $native 'Updates/APFClientUpdate.json') '{"version":"kesas-rc43"}'
    Write-TestFile (Join-Path $native 'Server/Config/APFPack.json') '{"requiredClientVersion":"kesas-rc43","packVersion":"kesas-rc43"}'
    Write-TestFile (Join-Path $control 'host.json') (@{root=$control;nativeRoot=$native}|ConvertTo-Json)
    Write-TestFile (Join-Path $control 'native-cache/current-native.json') (@{directory=$selected;sha256=(Get-FileHash (Join-Path $selected 'Start-Server.ps1')).Hash.ToLowerInvariant()}|ConvertTo-Json)
    Write-TestFile (Join-Path $control 'bootstrap/Start-NativeServer.ps1') 'fixed router'
    Write-TestFile (Join-Path $native 'Server/subfolder/extra.dll') 'extra native binary'
    Write-TestFile (Join-Path $native 'Server/Universe/live.json') 'must never inventory universe'
    Write-TestFile (Join-Path $native 'Server/Logs/live.log') 'must never inventory logs'
    Write-TestFile (Join-Path $native 'Server/APFDiscordBridge/status.json') 'must never inventory live player status'
    $handle=Start-PreactivationRecovery -Root $native -SelectedRoot $selected -OldVersion 'kesas-rc43' -CandidateVersion 'kesas-rc45' -ExpectedGamePid 101
    Assert-Test ($handle.Status.reason -ceq 'armed' -and $handle.Plan.candidateVersion -ceq 'kesas-rc45' -and $handle.Plan.publisher.pid -eq $PID) 'Plan or arm acknowledgement mismatch'
    Assert-Test ($handle.Plan.targetHashes.path -ccontains 'Server/subfolder/extra.dll' -and $handle.Plan.targetHashes.path -cnotcontains 'Server/Universe/live.json' -and $handle.Plan.targetHashes.path -cnotcontains 'Server/Logs/live.log' -and $handle.Plan.targetHashes.path -cnotcontains 'Server/APFDiscordBridge/status.json') 'Native inventory exclusions failed'
    'PASS complete plan, RC45, exact parent creation, native inventory, hidden child and armed report'
    Reject-Test {Stop-PreactivationRecovery -Recovery $handle -VerifiedHappyPath:$false} 'verified successful publication'
    Reject-Test {Stop-PreactivationRecovery -Recovery ([pscustomobject]@{Id=$handle.Id}) -VerifiedHappyPath} 'does not own'
    $script:reuse=$true
    Reject-Test {Stop-PreactivationRecovery -Recovery $handle -VerifiedHappyPath} 'identity changed'
    Assert-Test ($script:kills -eq 0) 'Unsafe stop killed a process'
    $script:reuse=$false
    $handle.ProcessId=230002
    Assert-Test (Stop-PreactivationRecovery -Recovery $handle -VerifiedHappyPath) 'Owned watchdog was not stopped'
    Assert-Test ($script:kills -eq 1 -and $handle.Disarmed) 'Owned-only stop mismatch'
    Assert-Test (-not (Stop-PreactivationRecovery -Recovery $handle -VerifiedHappyPath)) 'Already exited watchdog stopped again'
    'PASS false happy-path, forged object and reused PID reject; exact owned child stopped once'
    $script:badReport=$true
    Reject-Test {Start-PreactivationRecovery -Root $native -SelectedRoot $selected -OldVersion 'kesas-rc43' -CandidateVersion 'kesas-rc45' -ExpectedGamePid 101} 'refused to arm'
    Assert-Test ($script:kills -eq 1 -and $script:spawns -eq 2) 'Failed arm terminated its retained watcher'
    'PASS failed arm retains child; no service stop requested'
    Reject-Test {Start-PreactivationRecovery -Root $native -SelectedRoot $selected -OldVersion 'kesas-rc43' -CandidateVersion 'kesas-rc45' -ExpectedGamePid 999} 'not uniquely active'
    Assert-Test ($script:spawns -eq 2) 'Mismatched game spawned a watcher'
    'PASS expected game mismatch fails before child launch'
    'PASS 4 arm-helper groups; no real process, task, ACL change or production operation'
}finally{
    $resolved=[IO.Path]::GetFullPath($fixture)
    $temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if($resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^apf-arm-[a-f0-9]{32}$'){if([IO.Directory]::Exists($resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}}else{throw 'Refusing unsafe fixture cleanup'}
}
