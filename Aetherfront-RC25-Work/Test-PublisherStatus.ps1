#requires -Version 5.1
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Directory]::GetParent($PSScriptRoot).FullName
$source=Join-Path $workspace 'KESAS-VPS-Server/Publish-Release.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Publisher syntax errors.'}
foreach($name in @('Read-PublisherPlayerStatus','Assert-PublisherEmpty')) {
    $matches=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true))
    if($matches.Count -ne 1){throw "Expected exactly one publisher function: $name"}
    Invoke-Expression $matches[0].Extent.Text
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public static class PublisherStatusLockProbe {
    public static Thread Hold(string path, int milliseconds) {
        var ready = new ManualResetEventSlim(false);
        var worker = new Thread(() => {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                ready.Set(); Thread.Sleep(milliseconds);
            }
        });
        worker.IsBackground = true; worker.Start();
        if (!ready.Wait(2000)) throw new IOException("Probe could not acquire its owned file.");
        return worker;
    }
}
'@
$fixture=Join-Path $PSScriptRoot ('PublisherStatusFixture-'+[Guid]::NewGuid().ToString('N'))
if(![IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetFullPath($PSScriptRoot)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid owned fixture.'}
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'Server/APFDiscordBridge'))
$path=Join-Path $fixture 'Server/APFDiscordBridge/status.json'
$script:checks=0
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message};$script:checks++;Write-Output "PASS $Message"}
function Get-UpdateChildPath([string]$Base,[string]$Relative){Join-Path $Base $Relative}
function Get-UpdateServiceProcesses($Root,$Config){[pscustomobject]@{Path=[IO.Path]::GetFullPath("$Root/Server/Server.exe");Id=12345}}
function Write-Status([int]$Players=0,[int]$Connections=0,[int]$Age=0,[int]$ServerPid=12345) {
    [IO.File]::WriteAllText($path,(@{serverPid=$ServerPid;updatedAt=[DateTimeOffset]::UtcNow.AddSeconds(-$Age).ToString('o');playerCount=$Players;connectedCount=$Connections}|ConvertTo-Json -Compress))
}
function Reject-Status([string]$Message) {
    $rejected=$false
    try{Assert-PublisherEmpty $fixture ([pscustomobject]@{})}catch{$rejected=$true}
    Check $rejected $Message
}
Write-Status
Check ((Read-PublisherPlayerStatus $path).serverPid -eq 12345) 'Ordinary bridge JSON is readable'
$worker=[PublisherStatusLockProbe]::Hold($path,350)
$timer=[Diagnostics.Stopwatch]::StartNew()
try{$status=Read-PublisherPlayerStatus $path}finally{$worker.Join()}
Check ($status.serverPid -eq 12345 -and $timer.ElapsedMilliseconds -ge 100 -and $timer.ElapsedMilliseconds -lt 4000) 'Transient exclusive writer is retried without dropping validation'
Assert-PublisherEmpty $fixture ([pscustomobject]@{})
Check $true 'Fresh authoritative zero counts accepted'
Write-Status -Players 1;Reject-Status 'Online player prevents activation'
Write-Status -Connections 1;Reject-Status 'Pending connection prevents activation'
Write-Status -Age 20;Reject-Status 'Stale status prevents activation'
Write-Status -Age -5;Reject-Status 'Future status prevents activation'
Write-Status -ServerPid 54321;Reject-Status 'Wrong server PID prevents activation'
[IO.File]::WriteAllText($path,'not JSON');Reject-Status 'Malformed JSON prevents activation'
[IO.File]::WriteAllText($path,('x'*(4MB+1)));Reject-Status 'Oversized status prevents activation'
Write-Status
$worker=[PublisherStatusLockProbe]::Hold($path,2600)
$timer.Restart();$locked=$false
try{Read-PublisherPlayerStatus $path|Out-Null}catch{$locked=$_.Exception.GetBaseException() -is [IO.IOException]}finally{$worker.Join()}
Check ($locked -and $timer.ElapsedMilliseconds -lt 6000) 'Persistent lock fails closed within a bounded wait'
$timer.Restart();$missing=$false
try{Read-PublisherPlayerStatus ($path+'.missing')|Out-Null}catch{$missing=$true}
Check ($missing -and $timer.ElapsedMilliseconds -lt 1500) 'Missing status is not treated as an empty server'
Write-Output "PASS publisher status checks=$script:checks fixture=$fixture"
