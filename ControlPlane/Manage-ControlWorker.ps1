param([ValidateSet('state','start','stop','lease')][string]$Operation)
$ErrorActionPreference='Stop'
if($Operation -in @('state','start','stop')){
    # Avoid PowerShell 7's ScheduledTasks compatibility subprocess. This is a
    # fixed-name local Task Scheduler operation, never a remote command surface.
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $task=$scheduler.GetFolder([string][IO.Path]::DirectorySeparatorChar).GetTask('APF Discord Bot')
    if($Operation -eq 'state'){[Console]::WriteLine($(if($task.State -eq 4){'Running'}else{'Ready'}))}
    elseif($Operation -eq 'start' -and $task.State -ne 4){$null=$task.Run($null)}
    elseif($Operation -eq 'stop'){
        $settings=Get-Content -LiteralPath "$PSScriptRoot/../host.json" -Raw|ConvertFrom-Json
        $state=Get-Content -LiteralPath "$($settings.botRoot)/state/kesas-update-state.json" -Raw|ConvertFrom-Json
        if($state.status -notin @('idle','failed')){throw 'Native application operation is not idle'}
        $lease=[IO.FileStream]::new((Join-Path $settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try{if($task.State -eq 4){$task.Stop(0)}}finally{$lease.Dispose()}
    }
    exit
}
$settings=Get-Content -LiteralPath "$PSScriptRoot/../host.json" -Raw|ConvertFrom-Json
$lease=$null
try{$lease=[IO.FileStream]::new((Join-Path $settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);[Console]::WriteLine('idle')}
catch [IO.IOException]{[Console]::WriteLine('busy')}
finally{if($lease){$lease.Dispose()}}
