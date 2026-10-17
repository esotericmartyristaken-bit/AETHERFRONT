[CmdletBinding()]
param([string]$LauncherLog='',[switch]$PublicationActivation)
. "$PSScriptRoot/Update.Common.ps1"
$root=Assert-UpdatePath $PSScriptRoot
$settings=Get-UpdateDeployment $root
foreach($name in @('Server','PackHost','secrets','Updates')){Assert-UpdateTree (Get-UpdateChildPath $root $name)}
$serverLock=New-Object IO.FileStream((Get-UpdateChildPath $root '.server.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$pack=$null;$game=$null;$transcribing=$false;$shutdownError=$null
try{
    if(!$PublicationActivation -and [IO.File]::Exists((Get-UpdateChildPath $root '.update-transaction.json'))){throw 'An interrupted update needs recovery before starting services. Preserve .update-transaction.json and its backup'}
    if($LauncherLog){Assert-UpdatePath $LauncherLog | Out-Null;Start-Transcript -LiteralPath $LauncherLog -Append | Out-Null;$transcribing=$true}
    if(Get-NetUDPEndpoint -LocalPort $settings.gamePort -ErrorAction SilentlyContinue){throw 'Game port is already in use.'}
    if(Get-NetTCPConnection -LocalPort $settings.packPort -State Listen -ErrorAction SilentlyContinue){throw 'HTTPS port is already in use.'}
    $stopFile=Get-UpdateChildPath $root 'stop-request.txt'
    if([IO.File]::Exists($stopFile)){[IO.File]::Delete($stopFile)}
    $env:KESAS_LOCAL_ONLY=$null
    $updateArgs=@()
    if([IO.Directory]::Exists("$root/Updates")){$updateArgs=@('--client-update-root',"`"$root/Updates`"")}
    $pack=Start-Process -FilePath "$root/PackHost/ApfPackHost.exe" -WorkingDirectory "$root/PackHost" -ArgumentList (@('--root',"`"$root/Server/Config`"",'--listen-url',"https://0.0.0.0:$($settings.packPort)",'--certificate-path',"`"$root/secrets/pack.pfx`"")+$updateArgs) -WindowStyle Hidden -RedirectStandardOutput (Get-UpdateChildPath $root 'packhost.log') -RedirectStandardError (Get-UpdateChildPath $root 'packhost-error.log') -PassThru
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName="$root/Server/Server.exe";$start.WorkingDirectory="$root/Server"
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true
    $game=[Diagnostics.Process]::Start($start)
    Write-Host "Server PID $($game.Id); pack host PID $($pack.Id). Run Stop-Server.ps1 for a clean shutdown."
    while(!$game.HasExited -and !$pack.HasExited -and ![IO.File]::Exists($stopFile)){Start-Sleep -Milliseconds 500}
    if(![IO.File]::Exists($stopFile)){throw 'A service exited unexpectedly. Inspect Server/Logs and packhost-error.log.'}
}finally{
    try{
        if($game -and !$game.HasExited){
            try{$game.StandardInput.WriteLine('/shutdown');$game.StandardInput.Flush();if(!$game.WaitForExit(30000)){$shutdownError='Game server did not shut down within 30 seconds. Inspect it before restarting.'}}
            catch{$shutdownError=$_.Exception.Message}
        }
        if($pack -and !$pack.HasExited){Stop-Process -Id $pack.Id -ErrorAction Stop;$pack.WaitForExit()}
    }finally{
        if($transcribing){Stop-Transcript | Out-Null}
        $serverLock.Dispose()
    }
    if($shutdownError){throw $shutdownError}
}
