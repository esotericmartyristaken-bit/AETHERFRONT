$ErrorActionPreference='Stop'
$root=Join-Path $PSScriptRoot '../KESAS-VPS-Server'
. "$root/Update.Common.ps1"
$manifestPath="$root/Updates/APFClientUpdate.json"
$live=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
$candidate=Get-Content -LiteralPath "$PSScriptRoot/Candidate/Published/APFClientUpdate.json" -Raw|ConvertFrom-Json
if($live.version-ne 'kesas-rc22'-or $live.files.Count-ne 462){throw 'Live RC22 missing.'}
$expected=@{};foreach($file in $candidate.files){$expected[$file.path]=$file.sha256}
foreach($file in $live.files){if($expected[$file.path]-ne $file.sha256){throw "Published content mismatch: $($file.path)"}}
Invoke-UpdateTool $root @('--verify','--manifest',$manifestPath,'--archive',"$root/Updates/kesas-rc22/APFClient-kesas-rc22.zip",'--public-key-xml',"$root/secrets/update-signing-public.xml")
$pack=Get-Content -LiteralPath "$root/Server/Config/APFPack.json" -Raw|ConvertFrom-Json
if($pack.requiredClientVersion-ne 'kesas-rc22'-or $pack.serverId-ne 'kesas-b6286df413b9426f'){throw 'Live pack version/identity mismatch.'}
$stream=[IO.FileStream]::new("$root/Server/APFDiscordBridge/status.json",[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
$reader=[IO.StreamReader]::new($stream);try{$status=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
$ready=Get-Content -LiteralPath "$root/Server/APFDiscordBridge/ready.json" -Raw|ConvertFrom-Json
if(([DateTime]::UtcNow-([DateTime]$status.updatedAt).ToUniversalTime()).TotalSeconds-gt 8-or $ready.pid-ne $status.serverPid){throw 'Bridge is not fresh/ready.'}
$game=Get-Process -Id $status.serverPid
$udp=@(Get-NetUDPEndpoint -LocalPort 18865|Where-Object OwningProcess -eq $game.Id)
$tcp=@(Get-NetTCPConnection -LocalPort 18866 -State Listen)
if(-not $udp.Count-or $tcp.Count-ne 1){throw 'Game or HTTPS listener missing.'}
$https=Get-Process -Id $tcp[0].OwningProcess
& "$root/CertificateTool/ApfCertificateTool.exe" --probe-server https://127.0.0.1:18866/health 34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09
if($LASTEXITCODE-ne 0){throw 'Pinned health probe failed.'}
$stable=@{
 'Server/Config/GeneralSettings.xml'='0662B1E2E6977F177358E1FB1B4FCEA9659C590DD073BF0B2B8ED9EC27422485'
 'Server/Server.exe'='F009C6FD2D94E49B1E8C33A2DD39167F676C2B9DD0CAEB78976B8531FBE345A4'
 'PackHost/ApfPackHost.dll'='7D6CC04B3379E0BAC46E4857AFE38B89EF5C8EB76FEC97C3B4A321E84D9B7434'
 'deployment.json'='62EC39301135044A910E36FE9FBD382588A35EC507D9CA1EDFBF480F1EA0B80E'
}
foreach($relative in $stable.Keys){if((Get-FileHash -LiteralPath "$root/$relative").Hash-ne $stable[$relative]){throw "Protected file changed: $relative"}}
$vanilla=Get-Process -Id 2320
if($vanilla.StartTime.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')-ne '2026-09-23T00:37:33.1565324Z'){throw 'Vanilla process changed.'}
if(-not @(Get-NetUDPEndpoint -LocalPort 2252|Where-Object OwningProcess -eq 2320).Count){throw 'Vanilla UDP listener missing.'}
$bot=Get-Process -Id 8504
$state=Get-Content -LiteralPath "$root/../APFDiscordBot/state/kesas-update-state.json" -Raw|ConvertFrom-Json
if($state.status-ne 'idle'-or $state.lastDeployment.id-ne 'kesas-rc22'){throw 'Deployment not finalized.'}
$report=[ordered]@{version=$live.version;deployedAt=$state.lastDeployment.deployedAt;signedFiles=462;gamePid=$game.Id;httpsPid=$https.Id;botPid=$bot.Id;vanillaPid=$vanilla.Id;vanillaUnchanged=$true;protectedHashesUnchanged=$true;bridgeUpdatedAt=$status.updatedAt;playerCount=$status.playerCount;connectedCount=$status.connectedCount;verifiedAt=[DateTime]::UtcNow.ToString('o')}
$report|ConvertTo-Json|Set-Content -LiteralPath "$PSScriptRoot/LIVE-RESULT.json" -Encoding utf8
$report|ConvertTo-Json
