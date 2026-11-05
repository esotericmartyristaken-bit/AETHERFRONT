#requires -Version 7.4
function Invoke-ScienceCompensation {
    param([string]$Root,[string]$Plan,[string]$PlanSha256,[string]$Version,[switch]$ValidateOnly)
    if(-not $Plan){if($PlanSha256){throw 'Compensation hash requires a plan'};return}
    if($PlanSha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $Plan).Hash.ToLowerInvariant() -cne $PlanSha256){throw 'Compensation plan hash differs'}
    if(-not ('ScienceCompensation' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'ScienceCompensation.cs')}
    $text=Read-UpdateText $Plan 4096
    $intent=[ScienceCompensation]::ValidateIntent($text,$Version)|ConvertFrom-Json
    $state=Get-UpdateChildPath $Root 'Server/Aetherfront/state.json'
    $binding=Get-UpdateChildPath $Root 'Server/Universe/IdentityBindings.json'
    $directory=Get-UpdateChildPath $Root 'Server/Aetherfront/ScienceCompensations'
    $receipt=Get-UpdateChildPath $directory ($intent.transactionId+'.json')
    if(Test-Path -LiteralPath $receipt){
        $prior=Read-UpdateText $receipt 16384|ConvertFrom-Json
        if($prior.phase -cne 'applied' -or $prior.planSha256 -cne $PlanSha256 -or $prior.accountId -cne $intent.accountId -or $prior.amount -ne $intent.amount){throw 'Existing compensation receipt needs review; no replay'}
        return
    }
    $beforeText=Read-UpdateText $state 64MB;$bindingText=Read-UpdateText $binding 16MB
    $next=[ScienceCompensation]::Build($beforeText,$bindingText,$text,$Version)
    if($ValidateOnly){return}
    # Caller holds publication/server locks after candidate, warning, clean stop,
    # full world backup and durable activation journal. Receipt lives inside that
    # backed-up world, so activation rollback restores both credit and marker.
    New-UpdateDirectory $directory
    $old=$beforeText|ConvertFrom-Json;$updated=$next|ConvertFrom-Json
    $oldPlayer=@($old.PersonalProgression.Players|Where-Object AccountId -CEQ $intent.accountId)[0]
    $newPlayer=@($updated.PersonalProgression.Players|Where-Object AccountId -CEQ $intent.accountId)[0]
    $nextPath=Join-Path $directory ($intent.transactionId+'.next.json')
    if(Test-Path -LiteralPath $nextPath){throw 'Existing compensation preparation needs review'}
    Write-UpdateAtomic $nextPath $next
    $afterHash=(Get-FileHash -LiteralPath $nextPath).Hash.ToLowerInvariant()
    $record=[ordered]@{phase='prepared';transactionId=$intent.transactionId;planSha256=$PlanSha256;accountId=$intent.accountId;amount=$intent.amount;beforeScience=$oldPlayer.Science;afterScience=$newPlayer.Science;beforeRevision=$old.Revision;afterRevision=$updated.Revision;beforeCommitSequence=$old.StoreCommitSequence;afterCommitSequence=$updated.StoreCommitSequence;afterSha256=$afterHash;at=[DateTime]::UtcNow.ToString('o');earnedAndSharedTotalsUnchanged=$true;tradableBalanceUnchanged=$true}
    Write-UpdateAtomic $receipt ($record|ConvertTo-Json -Compress)
    if((Read-UpdateText $state 64MB) -cne $beforeText -or (Read-UpdateText $binding 16MB) -cne $bindingText){throw 'Compensation snapshot changed after shutdown'}
    [IO.File]::Replace($nextPath,$state,(Join-Path $directory ($intent.transactionId+'.before.json')))
    $stream=[IO.FileStream]::new($state,'Open','ReadWrite','Read');try{$stream.Flush($true)}finally{$stream.Dispose()}
    if((Get-FileHash -LiteralPath $state).Hash.ToLowerInvariant() -cne $afterHash){throw 'Compensation readback failed'}
    $record.phase='applied';Write-UpdateAtomic $receipt ($record|ConvertTo-Json -Compress)
}
