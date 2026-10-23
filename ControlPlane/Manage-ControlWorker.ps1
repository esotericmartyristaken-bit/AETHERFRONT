param([ValidateSet('state','start','stop','recover-worker','lease')][string]$Operation)
$ErrorActionPreference='Stop'
function Assert-NoApplyingHelper($Settings){
    foreach($file in Get-ChildItem -LiteralPath "$($Settings.root)/native-cache/results" -Filter '*.json' -File){
        $result=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
        if($result.phase -ne 'applying'){continue}
        if(-not $result.nativePid -or $result.nativePid -le 0){throw 'Ambiguous native helper must remain untouched'}
        try{$helper=[Diagnostics.Process]::GetProcessById([int]$result.nativePid);$helper.Dispose();throw 'Applying native helper must remain untouched'}
        catch [ArgumentException]{} # Only a confirmed absent PID permits recovery.
    }
}
function Stop-ControlWorker($Settings,$Task,[string]$Mode){
    $state=Get-Content -LiteralPath "$($Settings.botRoot)/state/kesas-update-state.json" -Raw|ConvertFrom-Json
    if($Mode -eq 'stop' -and $state.status -notin @('idle','failed')){throw 'Native application operation is not idle'}
    if($state.schemaVersion -ne 1 -or $state.status -notin @('idle','failed','queued','deploying')){throw 'Unknown application state'}
    $lease=[IO.FileStream]::new((Join-Path $Settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{Assert-NoApplyingHelper $Settings;if($Task.State -eq 4){$Task.Stop(0)}}finally{$lease.Dispose()}
}
if($Operation -in @('state','start','stop','recover-worker')){
    # Avoid PowerShell 7's ScheduledTasks compatibility subprocess. This is a
    # fixed-name local Task Scheduler operation, never a remote command surface.
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $task=$scheduler.GetFolder([string][IO.Path]::DirectorySeparatorChar).GetTask('APF Discord Bot')
    if($Operation -eq 'state'){[Console]::WriteLine($(if($task.State -eq 4){'Running'}else{'Ready'}))}
    elseif($Operation -eq 'start' -and $task.State -ne 4){$null=$task.Run($null)}
    elseif($Operation -in @('stop','recover-worker')){
        $settings=Get-Content -LiteralPath "$PSScriptRoot/../host.json" -Raw|ConvertFrom-Json
        Stop-ControlWorker $settings $task $Operation
    }
    exit
}
$settings=Get-Content -LiteralPath "$PSScriptRoot/../host.json" -Raw|ConvertFrom-Json
$lease=$null
try{$lease=[IO.FileStream]::new((Join-Path $settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);[Console]::WriteLine('idle')}
catch [IO.IOException]{[Console]::WriteLine('busy')}
finally{if($lease){$lease.Dispose()}}
