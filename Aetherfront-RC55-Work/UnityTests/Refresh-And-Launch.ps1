param([switch]$KeepServer)
$ErrorActionPreference='Stop'
$work=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$evidence=Join-Path $work 'rc50-input/rc55-stability'
$source=Join-Path $work 'rc50-source/Aetherfront-RC55-Work'
$serverRoot=Join-Path $evidence 'staging/PersonalServer'
if($work -cne 'C:\Users\jkell\Documents\Codex\2026-09-07\kesas-multiplayer-beta\work'){throw 'Owned workspace required'}
$roots=@((Join-Path $work 'apf-test'),(Join-Path $work 'apf-peer'))
$guarded=@((Join-Path $roots[0] 'KSP_x64.exe'),(Join-Path $roots[1] 'KSP_x64.exe'))
if(-not $KeepServer){$guarded+=(Join-Path $serverRoot 'Server.exe')}
$running=Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -in $guarded}
if($running){throw 'Close only owned test processes before refreshing files'}
function Atomic-Copy([string]$From,[string]$To){Copy-Item -LiteralPath $From -Destination ($To+'.rc55tmp');[IO.File]::Move(($To+'.rc55tmp'),$To,$true)}
if(-not $KeepServer){Atomic-Copy (Join-Path $evidence 'candidate/Server/Server.dll') (Join-Path $serverRoot 'Server.dll')}
$probe=Join-Path $source 'UnityTests/bin/Debug/net472/RC55TestProbe.dll'
foreach($root in $roots){
 $logs=@((Join-Path $root 'KSP.log'))|Where-Object {Test-Path -LiteralPath $_}
 if($logs){Compress-Archive -LiteralPath $logs -DestinationPath (Join-Path $evidence ('prior-'+[IO.Path]::GetFileName($root)+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'.zip'))}
 Atomic-Copy (Join-Path $source 'Client/bin/Debug/net472/AetherfrontStabilityClient.dll') (Join-Path $root 'GameData/LunaMultiplayer/Plugins/AetherfrontStabilityClient.dll')
 Atomic-Copy $probe (Join-Path $root 'GameData/RC55TestProbe/Plugins/RC55TestProbe.dll')
 $settings=Join-Path $root 'settings.cfg'
 $contents=[IO.File]::ReadAllText($settings) -replace '(?m)^TEXTURE_QUALITY\s*=\s*\d+', 'TEXTURE_QUALITY = 3' -replace '(?m)^SHADOWS_QUALITY\s*=\s*\d+', 'SHADOWS_QUALITY = 1'
 [IO.File]::WriteAllText($settings+'.rc55tmp',$contents);[IO.File]::Move($settings+'.rc55tmp',$settings,$true)
}
$packPath=Join-Path $serverRoot 'Config/APFPack.json'
$pack=Get-Content -LiteralPath $packPath -Raw|ConvertFrom-Json
($pack.clientOnly|Where-Object id -eq 'rc55-test-probe').dlls[0].sha256=(Get-FileHash -LiteralPath $probe).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($packPath,($pack|ConvertTo-Json -Depth 50))
$record=Get-Content -LiteralPath (Join-Path $evidence 'owned-processes.json') -Raw|ConvertFrom-Json
if(-not $KeepServer){
 $server=Start-Process -FilePath (Join-Path $serverRoot 'Server.exe') -WorkingDirectory $serverRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $evidence 'server-stdout.log') -RedirectStandardError (Join-Path $evidence 'server-stderr.log') -PassThru
 $record.server=$server.Id
}
$env:RC55_TEST_EVIDENCE=$evidence
$run=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
for($index=0;$index -lt $roots.Count;$index++){
 $label=if($index -eq 0){'alpha'}else{'beta'}
 $process=Start-Process -FilePath (Join-Path $roots[$index] 'KSP_x64.exe') -WorkingDirectory $roots[$index] -ArgumentList @('-popupwindow','-screen-width','960','-screen-height','540','-logFile',('"'+(Join-Path $evidence ('Player-'+$label+'-'+$run+'.log'))+'"')) -PassThru
 $record.$label=$process.Id
}
$record|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'owned-processes.json')
$record|ConvertTo-Json
