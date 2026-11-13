function Test-ReviewedCrewNormalization([string]$Before,[string]$Live,[string]$Relative){
 if($Relative -cnotin @('Kerbals\Jangun Kerman.txt','Kerbals\Surick Kerman.txt')){return $false}
 $a=@((Read-UpdateText (Join-Path $Before $Relative)) -split '\r?\n'|Where-Object {$_})
 $b=@((Read-UpdateText (Join-Path $Live $Relative)) -split '\r?\n'|Where-Object {$_})
 $oldState=@($a|Where-Object {$_ -cmatch '^state\s*='});$newState=@($b|Where-Object {$_ -cmatch '^state\s*='})
 if($oldState.Count -ne 1 -or $newState.Count -ne 1 -or $oldState[0] -cnotmatch '^state\s*=\s*Available$' -or $newState[0] -cnotmatch '^state\s*=\s*Assigned$'){return $false}
 if((($a|Where-Object {$_ -cnotmatch '^state\s*='}) -join "`n") -cne (($b|Where-Object {$_ -cnotmatch '^state\s*='}) -join "`n")){return $false}
 $name=[IO.Path]::GetFileNameWithoutExtension($Relative)
 $pattern='(?m)^\s*crew\s*=\s*'+[regex]::Escape($name)+'\s*$'
 foreach($vessel in Get-ChildItem -LiteralPath (Join-Path $Before 'Vessels') -File){
  if((Read-UpdateText $vessel.FullName) -cmatch $pattern){
   $now=Join-Path $Live ('Vessels/'+$vessel.Name)
   if([IO.File]::Exists($now) -and (Get-FileHash -LiteralPath $vessel.FullName).Hash -ceq (Get-FileHash -LiteralPath $now).Hash){return $true}
  }
 }
 return $false
}
function Complete-ReviewedNativeRollback([string]$Root,[string]$RecordPath){
 $journal=Get-UpdateChildPath $Root '.update-transaction.json'
 if(-not [IO.File]::Exists($journal)){return @{present=$false;changed=$false}}
 $result=[ordered]@{observedAtUtc=[DateTime]::UtcNow.ToString('o');present=$true;changed=$false;checksPassed=$false}
 try{
  $text=Read-UpdateText $journal 8MB;$pending=$text|ConvertFrom-Json
  $result.phase=$pending.phase;$result.failedVersion=$pending.version
  if($pending.schema -ne 1 -or $pending.phase -cne 'native-activation' -or $pending.version -cne 'kesas-rc61' -or
     $pending.releaseZipSha256 -cne '66a7b098e262cdb2a4233b691c5dd8bcdd37dd0cd93e7cbdde343e52c18a1983'){
   throw 'Unrelated native journal; no automatic disposition is authorized'
  }
  $backup=Assert-UpdatePath ([string]$pending.backup)
  $prefix=(Get-UpdateChildPath $Root 'Backups/updates').TrimEnd('\')+'\'
  if(-not $backup.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($backup) -cnotmatch '^20261113-[0-9-]+-kesas-rc61-[a-f0-9]{32}$'){throw 'Rollback backup is outside the expected failed transaction'}
  $result.backup=$backup
  $pack=Read-UpdateText (Get-UpdateChildPath $Root 'Server/Config/APFPack.json')|ConvertFrom-Json
  $feed=Read-UpdateText (Get-UpdateChildPath $Root 'Updates/APFClientUpdate.json')|ConvertFrom-Json
  if($pack.packVersion -cne 'kesas-rc60' -or $pack.requiredClientVersion -cne 'kesas-rc60' -or $feed.version -cne 'kesas-rc60'){throw 'Prior release has not been restored; journal preserved'}
  if([IO.Directory]::Exists((Get-UpdateChildPath $Root 'Updates/kesas-rc61'))){throw 'Failed release remains active; journal preserved'}
  $count=0
  foreach($row in $pending.files){
   $target=Assert-UpdatePath ([string]$row.target);$old=Assert-UpdatePath ([string]$row.backup)
   if(-not $target.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $old.StartsWith($backup.TrimEnd('\')+'\files\',[StringComparison]::OrdinalIgnoreCase)){throw 'Journal replacement path is outside its transaction'}
   if($row.existed){
    if(-not [IO.File]::Exists($target) -or -not [IO.File]::Exists($old) -or (Get-FileHash -LiteralPath $target).Hash -cne (Get-FileHash -LiteralPath $old).Hash){throw ('Rollback file differs: '+[IO.Path]::GetRelativePath($Root,$target))}
   }elseif([IO.File]::Exists($target)){throw 'A newly introduced activation file remains'}
   $count++
  }
  if($count -lt 10){throw 'Rollback replacement inventory is incomplete'};$result.filesMatched=$count
  $result.crewNormalizations=@();$result.worldDifferences=@()
  foreach($name in @('Universe','Aetherfront')){
   $record=@($pending.states|Where-Object name -CEQ $name)
   if($record.Count -ne 1 -or $record[0].existed -ne $true -or [IO.Path]::GetFullPath([string]$record[0].target) -ine (Get-UpdateChildPath $Root ('Server/'+$name))){throw 'Rollback world mapping differs'}
   $before=Get-UpdateChildPath $backup $name;$live=Get-UpdateChildPath $Root ('Server/'+$name)
   $oldFiles=@(Get-ChildItem -LiteralPath $before -Recurse -File -Force);$newFiles=@(Get-ChildItem -LiteralPath $live -Recurse -File -Force)
   if($oldFiles.Count -ne $newFiles.Count){throw ('Rollback world file count differs: '+$name)}
   foreach($file in $oldFiles){
    $relative=[IO.Path]::GetRelativePath($before,$file.FullName);$current=Get-UpdateChildPath $live ($relative.Replace('\','/'))
    if(-not [IO.File]::Exists($current)){throw ('Rollback world file missing: '+$name+'/'+$relative)}
    if((Get-FileHash -LiteralPath $file.FullName).Hash -cne (Get-FileHash -LiteralPath $current).Hash){
     if($name -ceq 'Universe' -and (Test-ReviewedCrewNormalization $before $live $relative)){$result.crewNormalizations+=@{relative=$relative;availabilityOnly=$true;unchangedCanonicalVesselReference=$true}}
     else{$result.worldDifferences+=@{relative=$name+'/'+$relative;backupSha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant();liveSha256=(Get-FileHash -LiteralPath $current).Hash.ToLowerInvariant()}}
    }
   }
   $result[$name+'FilesMatched']=$oldFiles.Count
  }
  if($result.worldDifferences.Count){throw ('Rollback world differs; journal retained: '+(($result.worldDifferences|Select-Object -First 8 -ExpandProperty relative) -join ', '))}
  $bridgePath=Get-UpdateChildPath $Root 'Server/APFDiscordBridge/status.json'
  $stream=[IO.FileStream]::new($bridgePath,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
  try{$reader=[IO.StreamReader]::new($stream);try{$bridge=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}}finally{$stream.Dispose()}
  if(([DateTimeOffset]::UtcNow-[DateTimeOffset]$bridge.updatedAt).TotalSeconds -gt 15 -or $bridge.serverPid -le 0){throw 'Restored server heartbeat is not fresh'}
  $result.serverPid=$bridge.serverPid
  if(@(Get-NetUDPEndpoint -LocalPort 18865|Where-Object OwningProcess -EQ $bridge.serverPid).Count -eq 0){throw 'Restored game does not own UDP 18865'}
  . (Join-Path $UpdateCodeRoot 'Update.ManagedHealth.ps1')
  $result.health=Assert-UpdatedHealth 18866 '34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09'
  # Do not delete evidence or restore state. Only retire the exact failed
  # activation marker after exact restoration, allowing only the two previously
  # audited crew-availability normalizations with unchanged canonical vessels.
  if((Read-UpdateText $journal 8MB) -cne $text){throw 'Native journal changed during review'}
  $saved=Get-UpdateChildPath $backup ('reviewed-native-journal-'+[Guid]::NewGuid().ToString('N')+'.json')
  [IO.File]::Move($journal,$saved)
  $result.archivedJournal=$saved;$result.changed=$true;$result.checksPassed=$true
 }catch{$result.error=$_.Exception.Message;throw}
 finally{[IO.File]::WriteAllText($RecordPath,($result|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))}
 return $result
}
