#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string]$NativeArchive,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedNativeHash,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ReleaseCommit,
    [Parameter(Mandatory)][ValidatePattern('^S-1-5-21-[0-9-]+$')][string]$ExpectedOperatorSid)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if([Environment]::MachineName -ine 'VMI3254434' -or $identity.User.Value -cne $ExpectedOperatorSid -or -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Explicit VPS operator elevation is required for this code correction'}
. (Join-Path $PSScriptRoot 'Publisher.Common.ps1')
$root='C:\ProgramData\Aetherfront-VPSLocalPublisher';$runner=Join-Path $root 'Runner-v3'
Assert-PublisherPath $runner $root
$configuration=Read-PublisherJson (Join-Path $runner 'runner.json')
if((Get-FileHash -LiteralPath $NativeArchive).Hash.ToLowerInvariant() -cne $ExpectedNativeHash){throw 'Reviewed native archive differs'}
& (Join-Path $PSScriptRoot 'Verify-KesasRelease.ps1') -Archive $NativeArchive -ExpectedVersion kesas-rc54 | Out-Null
$live='C:\Users\Administrator\Desktop\APF\KESAS-VPS-Server'
$before=@(Get-NetUDPEndpoint -LocalPort 18865|Select-Object -ExpandProperty OwningProcess -Unique)
if($before.Count -ne 1 -or (Read-PublisherJson $configuration.activeFeedPath).version -cne 'kesas-rc52' -or [IO.File]::Exists((Join-Path $live '.update-transaction.json'))){throw 'Production changed or an activation is unresolved; no correction applied'}
$lock=$null
try {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not $lock){try{$lock=[IO.FileStream]::new((Join-Path $root 'instance-v3.lock'),'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{if($timer.Elapsed.TotalSeconds -ge 30){throw 'Worker is busy; no running helper was stopped'};Start-Sleep -Milliseconds 500}}
    $manifestPath=Join-Path $runner 'installed-files.json';$manifest=Read-PublisherJson $manifestPath
    foreach($row in $manifest.files){if((Get-FileHash -LiteralPath (Join-Path $runner $row.name)).Hash.ToLowerInvariant() -cne $row.sha256){throw 'Installed publisher differs from its recorded generation'}}
    $prior=@{'Publisher.Common.ps1'='2bb830911900c64940be1603bae81ddadf015b8f0a2149eb9ae01ec8da6d1325';'Invoke-VPSLocalPublisher.ps1'='263e83fdd4fe842cb07002dcbb121b9369a8bc0796ce2eef35a48443c1c2112e'}
    foreach($name in $prior.Keys){if((Get-FileHash -LiteralPath (Join-Path $runner $name)).Hash.ToLowerInvariant() -cne $prior[$name]){throw 'This correction only applies to the commissioned v3-4 generation'}}
    $backup=Join-Path $root ('Correction-v3-'+[Guid]::NewGuid().ToString('N'));Set-PublisherDirectory $backup
    [IO.File]::Copy($manifestPath,(Join-Path $backup 'installed-files.json'),$false)
    $changes=@()
    foreach($name in $prior.Keys){
        $path=Join-Path $runner $name;[IO.File]::Copy($path,(Join-Path $backup $name),$false)
        $bytes=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $name))
        $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $temp=Join-Path $runner ([Guid]::NewGuid().ToString('N')+'.tmp');[IO.File]::WriteAllBytes($temp,$bytes);[IO.File]::Move($temp,$path,$true)
        $row=@($manifest.files|Where-Object name -CEQ $name);if($row.Count -ne 1){throw 'Installed manifest entry differs'};$row[0].sha256=$sha
        $changes+=@{name=$name;beforeSha256=$prior[$name];afterSha256=$sha}
    }
    Write-PublisherJson $manifestPath $manifest
    # Preserve the failed commissioning request rather than silently replaying
    # its old approval. The normal operator will sign a fresh verification.
    $pending=Join-Path $configuration.inbox 'pending.json'
    if([IO.File]::Exists($pending)){
        $old=Read-PublisherJson $pending
        if($old.action -ceq 'verify'){
            foreach($name in @('pending.json','pending.sig')){if([IO.File]::Exists((Join-Path $configuration.inbox $name))){[IO.File]::Move((Join-Path $configuration.inbox $name),(Join-Path $backup $name))}}
        }
    }
    $after=@(Get-NetUDPEndpoint -LocalPort 18865|Select-Object -ExpandProperty OwningProcess -Unique)
    if($after.Count -ne 1 -or $after[0] -ne $before[0]){throw 'Production process changed during code correction'}
    $result=@{status='corrected-awaiting-normal-token-verification';sourceCommit=$ReleaseCommit;files=$changes;backup=$backup;productionActivated=$false;serverPid=$before[0];keysPreserved=$true;configurationPreserved=$true;observedAtUtc=[DateTime]::UtcNow.ToString('o')}
    Write-PublisherJson (Join-Path $configuration.statusRoot 'correction-result.json') $result
    $result|ConvertTo-Json -Depth 6
} finally {if($lock){$lock.Dispose()}}
