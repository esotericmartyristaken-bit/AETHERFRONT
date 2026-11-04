#requires -Version 7.4
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalBotProcess.ps1')
$settings=[pscustomobject]@{node='C:\Program Files\nodejs\node.exe'}
$sid='S-1-5-21-111-222-333-1001'
$runtime='C:\ProgramData\Aetherfront-Local\releases\'+('a'*40)+'-'+('b'*16)+'\BotRuntime'
$created=[DateTime]::UtcNow.AddMinutes(-10)
$holder=[pscustomobject]@{pid=12345;startedAt=$created.AddSeconds(8).ToString('o')}
$launch=[pscustomobject]@{pid=12345;runtime=$runtime;startedAt=$created.ToString('o')}
$process=[pscustomobject]@{ProcessId=12345;ExecutablePath=$settings.node;CommandLine='"'+$settings.node+'" "'+$runtime+'\APFDiscordBot\src\index.js"';CreationDate=$created}
$checks=0
function Valid { $null=Assert-LocalBotChildIdentity $settings $sid $holder $launch $process $sid; $script:checks++ }
function Rejected([scriptblock]$Change,[scriptblock]$Restore) {
 & $Change
 try{$rejected=$false;try{Valid}catch{$rejected=$true};if(-not $rejected){throw 'Unsafe bot child accepted'};$script:checks++}finally{& $Restore}
}
Valid
Rejected {$holder.pid='12345'} {$holder.pid=12345}
Rejected {$holder.pid=-1} {$holder.pid=12345}
Rejected {$launch.pid=54321} {$launch.pid=12345}
Rejected {$process.ProcessId=54321} {$process.ProcessId=12345}
Rejected {$process.ExecutablePath='C:\other\node.exe'} {$process.ExecutablePath=$settings.node}
$originalCommand=$process.CommandLine
Rejected {$process.CommandLine+=' --other'} {$process.CommandLine=$originalCommand}
Rejected {$process.CommandLine='node.exe "C:\unrelated\index.js"'} {$process.CommandLine=$originalCommand}
Rejected {$launch.runtime='C:\Users\Public\BotRuntime'} {$launch.runtime=$runtime}
Rejected {$launch.runtime=$runtime+'\..\..\BotRuntime'} {$launch.runtime=$runtime}
Rejected {$launch.startedAt=$created.AddMinutes(1).ToString('o')} {$launch.startedAt=$created.ToString('o')}
Rejected {$holder.startedAt=$created.AddSeconds(-1).ToString('o')} {$holder.startedAt=$created.AddSeconds(8).ToString('o')}
Rejected {$holder.startedAt=$created.AddMinutes(3).ToString('o')} {$holder.startedAt=$created.AddSeconds(8).ToString('o')}
$rejected=$false;try{$null=Assert-LocalBotChildIdentity $settings $sid $holder $launch $process 'S-1-5-18'}catch{$rejected=$true}
if(-not $rejected){throw 'Wrong process owner accepted'};$checks++
$process.CommandLine=$settings.node+' "'+$runtime+'\APFDiscordBot\src\index.js"';Valid
$errorsFound=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-LocalBot.ps1'),[ref]$null,[ref]$errorsFound)
if($errorsFound.Count){throw 'Installer syntax invalid'};$checks++
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('local-bot-records-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path (Join-Path $fixture 'state')
$recordSettings=[pscustomobject]@{botRoot=$fixture}
$legacy=Join-Path $fixture 'state/local-bot-launch.json'
$old=[pscustomobject]@{pid=99999;startedAt=$created.ToString('o')}
[IO.File]::WriteAllText($legacy,($old|ConvertTo-Json))
$checks++;if(Get-LocalBotLaunchRecord $recordSettings $process){throw 'Wrong PID launch record accepted'}
$interrupted=Join-Path $fixture 'state/local-bot-launch.json.111.tmp'
[IO.File]::WriteAllText($interrupted,($launch|ConvertTo-Json))
$checks++;if((Get-LocalBotLaunchRecord $recordSettings $process).pid -ne 12345){throw 'Interrupted legacy record migration failed'}
[IO.File]::WriteAllText($interrupted,'partial invalid JSON')
$checks++;if(Get-LocalBotLaunchRecord $recordSettings $process){throw 'Partial record accepted'}
$versioned=Join-Path $fixture 'state/local-bot-launch-20261104T000000000Z-111-12345.json'
[IO.File]::WriteAllText($versioned,($launch|ConvertTo-Json))
$checks++;if((Get-LocalBotLaunchRecord $recordSettings $process).runtime -cne $runtime){throw 'Per-launch record unavailable'}
$process.CreationDate=$created.AddSeconds(3)
$checks++;if(Get-LocalBotLaunchRecord $recordSettings $process){throw 'Reused PID accepted'}
$process.CreationDate=$created
$errorsFound=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Start-LocalBot.ps1'),[ref]$null,[ref]$errorsFound)
if($errorsFound.Count){throw 'Launcher syntax invalid'};$checks++
"PASS $checks identity/syntax checks; no live process, task, ACL or server mutation"
