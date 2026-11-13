#requires -Version 7.5
param([Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][int]$LiveServerPid)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../KESAS-VPS-Server/Update.Common.ps1')
. (Join-Path $PSScriptRoot '../Aetherfront-RC44-Work/Deployment/Review-NativeRollback.ps1')
$fixture=[IO.Path]::GetFullPath($FixtureRoot)
if(-not $fixture.StartsWith('C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use the isolated fixture root'}
if(Test-Path -LiteralPath $fixture){throw 'Preserve prior fixture'}
$null=[IO.Directory]::CreateDirectory($fixture)
function Put([string]$Path,[object]$Value){
 $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
 $text=if($Value -is [string]){$Value}else{$Value|ConvertTo-Json -Depth 12}
 [IO.File]::WriteAllText($Path,$text,[Text.UTF8Encoding]::new($false))
}
$checks=0
foreach($case in @('version','hash','file','introduced','world','count','heartbeat','mapping','valid')){
 $root=Join-Path $fixture $case
 $backup=Join-Path $root ('Backups/updates/20261113-000000-000-kesas-rc61-'+[Guid]::NewGuid().ToString('N'))
 $rows=@()
 for($i=0;$i -lt 10;$i++){
  $target=Join-Path $root "Replacement/$i.dat";$old=Join-Path $backup "files/$i.dat"
  Put $target "original-$i";Put $old "original-$i"
  $rows+=@{target=$target;backup=$old;existed=$true}
 }
 $states=@()
 foreach($name in @('Universe','Aetherfront')){
  Put (Join-Path $backup "$name/state.dat") 'original-state'
  Put (Join-Path $root "Server/$name/state.dat") 'original-state'
  $states+=@{name=$name;target=(Join-Path $root "Server/$name");existed=$true}
 }
 Put (Join-Path $root 'Server/Config/APFPack.json') @{packVersion='kesas-rc60';requiredClientVersion='kesas-rc60'}
 Put (Join-Path $root 'Updates/APFClientUpdate.json') @{version='kesas-rc60'}
 Put (Join-Path $root 'Server/APFDiscordBridge/status.json') @{updatedAt=[DateTime]::UtcNow.ToString('o');serverPid=$LiveServerPid}
 $pending=@{schema=1;phase='native-activation';version='kesas-rc61';releaseZipSha256='66a7b098e262cdb2a4233b691c5dd8bcdd37dd0cd93e7cbdde343e52c18a1983';backup=$backup;files=$rows;states=$states}
 switch($case){
  version {$pending.version='kesas-rc62'}
  hash {$pending.releaseZipSha256='0'*64}
  file {Put $rows[0].target 'changed'}
  introduced {$pending.files[0].existed=$false}
  world {Put (Join-Path $root 'Server/Universe/state.dat') 'changed'}
  count {Put (Join-Path $root 'Server/Universe/extra.dat') 'new'}
  heartbeat {Put (Join-Path $root 'Server/APFDiscordBridge/status.json') @{updatedAt=[DateTime]::UtcNow.AddMinutes(-2).ToString('o');serverPid=$LiveServerPid}}
  mapping {$pending.states[0].target=Join-Path $root 'Server/Other'}
 }
 $journal=Join-Path $root '.update-transaction.json';Put $journal $pending
 $hashBefore=(Get-FileHash -LiteralPath $journal).Hash
 $record=Join-Path $root 'review.json';$errorText=$null
 try{$result=Complete-ReviewedNativeRollback $root $record}catch{$errorText=$_.Exception.Message}
 if($case -eq 'valid'){
  if($errorText -or -not $result.changed -or (Test-Path -LiteralPath $journal) -or (Get-FileHash -LiteralPath $result.archivedJournal).Hash -cne $hashBefore){throw ('Valid rollback not safely archived: '+$errorText)}
 }else{
  if(-not $errorText -or -not (Test-Path -LiteralPath $journal) -or (Get-FileHash -LiteralPath $journal).Hash -cne $hashBefore){throw ('Unsafe rollback changed: '+$case)}
 }
 $checks++;Write-Output "PASS rollback $case"
}
$empty=Join-Path $fixture 'empty';$null=[IO.Directory]::CreateDirectory($empty)
$result=Complete-ReviewedNativeRollback $empty (Join-Path $empty 'review.json')
if($result.present -or $result.changed){throw 'Missing journal was not a no-op'}
$checks++
Write-Output "PASS $checks isolated journal tests. Production accesses were pinned HTTPS and UDP ownership reads only."
$before=Join-Path $fixture 'crew-before';$live=Join-Path $fixture 'crew-live'
Put (Join-Path $before 'Kerbals/Jangun Kerman.txt') "name = Jangun Kerman`nstate = Available`nbrave = 1`n"
Put (Join-Path $live 'Kerbals/Jangun Kerman.txt') "name = Jangun Kerman`nstate = Assigned`nbrave = 1`n"
Put (Join-Path $before 'Vessels/one.txt') "VESSEL`n{`n crew = Jangun Kerman`n}`n"
Put (Join-Path $live 'Vessels/one.txt') "VESSEL`n{`n crew = Jangun Kerman`n}`n"
if(-not (Test-ReviewedCrewNormalization $before $live 'Kerbals\Jangun Kerman.txt')){throw 'Canonical startup normalization refused'}
Write-Output 'PASS crew availability-only normalization with exact canonical vessel'
Put (Join-Path $live 'Kerbals/Jangun Kerman.txt') "name = Jangun Kerman`nstate = Assigned`nbrave = 0`n"
if(Test-ReviewedCrewNormalization $before $live 'Kerbals\Jangun Kerman.txt'){throw 'Additional crew change accepted'}
Write-Output 'PASS additional crew changes refused'
Put (Join-Path $live 'Kerbals/Jangun Kerman.txt') "name = Jangun Kerman`nstate = Assigned`nbrave = 1`n"
Put (Join-Path $live 'Vessels/one.txt') "VESSEL`n{`n crew = Other`n}`n"
if(Test-ReviewedCrewNormalization $before $live 'Kerbals\Jangun Kerman.txt'){throw 'Changed canonical vessel accepted'}
Write-Output 'PASS missing or changed canonical crew reference refused'
