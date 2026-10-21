param([ValidateSet('state','start','lease')][string]$Operation)
$ErrorActionPreference='Stop'
if($Operation -eq 'state'){[Console]::WriteLine([string](Get-ScheduledTask -TaskName 'APF Discord Bot').State);exit}
if($Operation -eq 'start'){Start-ScheduledTask -TaskName 'APF Discord Bot';exit}
$settings=Get-Content -LiteralPath "$PSScriptRoot/../host.json" -Raw|ConvertFrom-Json
$lease=$null
try{$lease=[IO.FileStream]::new((Join-Path $settings.nativeRoot '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);[Console]::WriteLine('idle')}
catch [IO.IOException]{[Console]::WriteLine('busy')}
finally{if($lease){$lease.Dispose()}}
