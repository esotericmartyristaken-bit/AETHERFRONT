#requires -Version 7.4
Set-StrictMode -Version Latest

function Invoke-LocalDeploymentBridge([string]$Node,[string]$CodeRoot,[string]$BotConfig,[string]$Action,[string]$InputPath='') {
    $arguments=@((Join-Path $CodeRoot 'LocalBridge.mjs'),$Action,$BotConfig)
    if($InputPath){$arguments+=$InputPath}
    $output=@(& $Node @arguments)
    if($LASTEXITCODE -ne 0){throw 'Local deployment status or announcement failed; no shutdown authorized'}
    return ($output -join "`n"|ConvertFrom-Json)
}
function Write-LocalDeploymentNotice([string]$Node,[string]$CodeRoot,[string]$BotConfig,[string]$Directory,[string]$Message) {
    $notice=Join-Path $Directory ('notice-'+[Guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::WriteAllText($notice,(@{message=$Message}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    $null=Invoke-LocalDeploymentBridge $Node $CodeRoot $BotConfig 'announce' $notice
}
function Wait-LocalDeploymentAdmission([string]$Node,[string]$CodeRoot,[string]$BotConfig,[string]$Directory,[string]$Version,[string]$Notes='') {
    $started=$null;$timer=$null;$marks=[Collections.Generic.HashSet[int]]::new();$initialPid=$null
    while($true){
        $live=Invoke-LocalDeploymentBridge $Node $CodeRoot $BotConfig 'status'
        if($null -eq $initialPid){$initialPid=$live.serverPid}
        if($live.serverPid -ne $initialPid){throw 'Server identity changed during local admission; recheck candidate before shutdown'}
        if($live.playerCount -eq 0 -and $live.connectedCount -eq 0){
            Write-LocalDeploymentNotice $Node $CodeRoot $BotConfig $Directory "Server is empty; applying verified update $Version now."
            return [pscustomobject]@{allowActivePlayers=$false;serverPid=$initialPid;warnedAt=$started;admittedAt=[DateTime]::UtcNow.ToString('o');reason='empty'}
        }
        if($null -eq $timer){
            $message="Server will restart in 15 minutes for verified update $Version."
            if($Notes){$message+=' Changes: '+$Notes.Substring(0,[Math]::Min(220,$Notes.Length))}
            Write-LocalDeploymentNotice $Node $CodeRoot $BotConfig $Directory $message
            $started=[DateTime]::UtcNow;$timer=[Diagnostics.Stopwatch]::StartNew();$null=$marks.Add(900)
        }
        $remaining=[Math]::Ceiling(900-$timer.Elapsed.TotalSeconds)
        foreach($mark in @(600,300,60,15)){
            if($remaining -le $mark -and $marks.Add($mark)){
                $duration=if($mark -ge 60){"$([int]($mark/60)) minutes"}else{"$mark seconds"}
                $message="Server will restart in $duration for verified update $Version."
                if($Notes){$message+=' Changes: '+$Notes.Substring(0,[Math]::Min(220,$Notes.Length))}
                Write-LocalDeploymentNotice $Node $CodeRoot $BotConfig $Directory $message
                break
            }
        }
        if($remaining -le 0){
            if(-not $marks.Contains(900)){throw 'Full fifteen-minute player warning was not delivered'}
            Write-LocalDeploymentNotice $Node $CodeRoot $BotConfig $Directory "Server restarting now for verified update $Version."
            return [pscustomobject]@{allowActivePlayers=$true;serverPid=$initialPid;warnedAt=$started.ToString('o');admittedAt=[DateTime]::UtcNow.ToString('o');reason='fifteen-minute-warning-complete'}
        }
        Start-Sleep -Seconds 5
    }
}
