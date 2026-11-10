#requires -Version 7.4
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'LocalBotProcess.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('local-bot-reuse-'+[Guid]::NewGuid().ToString('N'))
$bundle=Join-Path $fixture 'bundle';$runtime=Join-Path $fixture 'runtime';$checks=0
foreach($relative in @('APFDiscordBot/src','APFDiscordBot/scripts','ControlPlane')){
    $source=Join-Path $bundle $relative;$target=Join-Path $runtime $relative
    $null=[IO.Directory]::CreateDirectory($source);$null=[IO.Directory]::CreateDirectory($target)
    [IO.File]::WriteAllText((Join-Path $source 'code.js'),'immutable code')
    [IO.File]::WriteAllText((Join-Path $target 'code.js'),'immutable code')
}
$null=[IO.Directory]::CreateDirectory((Join-Path $bundle 'Aetherfront-RC44-Work/Deployment'))
foreach($name in @('package.json','package-lock.json')){
    [IO.File]::WriteAllText((Join-Path $bundle "APFDiscordBot/$name"),'{}')
    [IO.File]::WriteAllText((Join-Path $runtime "APFDiscordBot/$name"),'{}')
}
[IO.File]::WriteAllText((Join-Path $bundle 'Aetherfront-RC44-Work/Deployment/Start-LocalBot.ps1'),'launch')
[IO.File]::WriteAllText((Join-Path $runtime 'Start-LocalBot.ps1'),'launch')
function Check([bool]$Value,[string]$Name){$script:checks++;if(-not $Value){throw $Name}}
Check (Test-LocalBotRuntimeContent $bundle $runtime) 'Identical sealed code accepted'
$code=Join-Path $runtime 'APFDiscordBot/src/code.js'
[IO.File]::WriteAllText($code,'changed code')
Check (-not (Test-LocalBotRuntimeContent $bundle $runtime)) 'Changed code must restart through normal guarded installation'
[IO.File]::WriteAllText($code,'immutable code')
$extra=Join-Path $runtime 'ControlPlane/extra.js';[IO.File]::WriteAllText($extra,'extra')
Check (-not (Test-LocalBotRuntimeContent $bundle $runtime)) 'Extra source file rejected'
[IO.File]::Delete($extra)
$package=Join-Path $runtime 'APFDiscordBot/package-lock.json';[IO.File]::WriteAllText($package,'different dependencies')
Check (-not (Test-LocalBotRuntimeContent $bundle $runtime)) 'Different dependency lock rejected'
[IO.File]::WriteAllText($package,'{}')
$launcher=Join-Path $runtime 'Start-LocalBot.ps1';[IO.File]::WriteAllText($launcher,'different launcher')
Check (-not (Test-LocalBotRuntimeContent $bundle $runtime)) 'Different launcher rejected'
[IO.File]::WriteAllText($launcher,'launch')
Check (Test-LocalBotRuntimeContent $bundle $runtime) 'Original content restored'
$log=Join-Path $fixture 'stdout.log';[IO.File]::WriteAllText($log,'Not authenticated')
Check (-not (Test-LocalBotStartupBanner $log)) 'Missing authenticated startup banner rejected'
[IO.File]::WriteAllText($log,"APF Discord Bot running from fixture`n"+('x'*2000000))
$writer=[IO.FileStream]::new($log,[IO.FileMode]::Open,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
try{Check (Test-LocalBotStartupBanner $log) 'Live shared writer and large log read safely with bounded startup prefix'}finally{$writer.Dispose()}
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Install-LocalBot.ps1') -Raw
Check ($source.IndexOf('Get-ReusableLocalBot') -lt $source.IndexOf('npm-cache')) 'Verified reuse precedes dependency reinstall and task stops'
Check ($source.Contains('TotalSeconds -lt 300')) 'Changed bot keeps bounded cold-start budget'
# Exercise reuse orchestration with synthetic process/task metadata. Real
# identity checks stay enabled; no OS task or process is queried or changed.
$state=Join-Path $fixture 'state';$null=[IO.Directory]::CreateDirectory($state)
$created=[DateTime]::UtcNow.AddSeconds(-60)
$sid='S-1-5-21-100-200-300-500'
$sealedRuntime='C:\ProgramData\Aetherfront-Local\releases\'+('a'*40)+'-'+('b'*16)+'\BotRuntime'
$settings=[pscustomobject]@{botRoot=$fixture;root=(Join-Path $fixture 'control');node='C:\Program Files\nodejs\node.exe'}
$script:testLaunch=[pscustomobject]@{pid=12345;runtime=$sealedRuntime;startedAt=$created.ToString('o');stdout=$log}
$script:testProcess=[pscustomobject]@{ProcessId=12345;CreationDate=$created;ExecutablePath=$settings.node;CommandLine='"'+$settings.node+'" "'+$sealedRuntime+'\APFDiscordBot\src\index.js"'}
$holder=[pscustomobject]@{pid=12345;startedAt=$created.AddSeconds(10).ToString('o')}
[IO.File]::WriteAllText((Join-Path $state 'bot-instance.lock'),($holder|ConvertTo-Json))
$script:testLaunch.stdout=Join-Path $state 'local-bot-20261110T000000000Z-1.stdout.log'
[IO.File]::WriteAllText($script:testLaunch.stdout,'APF Discord Bot running from fixture')
$script:testAction=[pscustomobject]@{Path=(Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe');WorkingDirectory=$fixture;Arguments="-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$sealedRuntime/Start-LocalBot.ps1`" -Settings `"$($settings.root)/host.json`""}
$actions=[pscustomobject]@{Count=1};$actions|Add-Member -MemberType ScriptMethod -Name Item -Value {param($index) return $script:testAction}
$task=[pscustomobject]@{State=4;Definition=[pscustomobject]@{Actions=$actions}}
$remote=[pscustomobject]@{Enabled=$false;State=3}
$script:contentMatch=$true;$script:ownerSid=$sid;$script:queries=0;$script:changedGeneration=$false
function Get-CimInstance {param($ClassName,$Filter,$ErrorAction) $script:queries++;if($script:changedGeneration -and $script:queries -ge 2){$copy=$script:testProcess.PSObject.Copy();$copy.CreationDate=$created.AddSeconds(3);return $copy};return $script:testProcess}
function Invoke-CimMethod {param($InputObject,$MethodName,$ErrorAction) return [pscustomobject]@{ReturnValue=0;Sid=$script:ownerSid}}
function Get-LocalBotLaunchRecord {param($Settings,$Process) return $script:testLaunch}
function Assert-LocalBotTrustedRelease {param($Runtime) if($Runtime -cne $sealedRuntime){throw 'Unexpected trusted runtime'}}
function Test-LocalBotRuntimeContent {param($Bundle,$Runtime) return $script:contentMatch}
$reuse=Get-ReusableLocalBot $bundle $settings $task $remote $sid
Check ($reuse.reused -and $reuse.botPid -eq 12345 -and $script:queries -eq 2) 'Healthy identical bot reused with two generation checks'
$remote.Enabled=$true
Check ($null -eq (Get-ReusableLocalBot $bundle $settings $task $remote $sid)) 'Enabled remote publisher prevents reuse'
$remote.Enabled=$false;$script:contentMatch=$false
Check ($null -eq (Get-ReusableLocalBot $bundle $settings $task $remote $sid)) 'Changed frozen content takes guarded reinstall path'
$script:contentMatch=$true;$script:ownerSid='S-1-5-18';$rejected=$false
try{$null=Get-ReusableLocalBot $bundle $settings $task $remote $sid}catch{$rejected=$true}
Check $rejected 'Different process owner cannot be reused'
$script:ownerSid=$sid;$script:queries=0;$script:changedGeneration=$true;$rejected=$false
try{$null=Get-ReusableLocalBot $bundle $settings $task $remote $sid}catch{$rejected=$true}
Check $rejected 'Process generation changing during hashes cannot be reused'
"PASS $checks runtime-content/shared-log checks; no live tasks, credentials or processes modified"
