param([ValidateSet('state','start','lease')][string]$Operation)
$ErrorActionPreference='Stop'
if($Operation -in @('state','start')){
    # Avoid PowerShell 7's ScheduledTasks compatibility subprocess. This is a
    # fixed-name local Task Scheduler operation, never a remote command surface.
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $task=$scheduler.GetFolder([string][IO.Path]::DirectorySeparatorChar).GetTask('APF Discord Bot')
    if($Operation -eq 'state'){[Console]::WriteLine($(if($task.State -eq 4){'Running'}else{'Ready'}))}
    elseif($task.State -ne 4){$null=$task.Run($null)}
    exit
}
$settings=Get-Content -LiteralPath "$PSScriptRoot/../host.json" -Raw|ConvertFrom-Json
$lease=$null
try{$lease=[IO.FileStream]::new((Join-Path $settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);[Console]::WriteLine('idle')}
catch [IO.IOException]{[Console]::WriteLine('busy')}
finally{if($lease){$lease.Dispose()}}
