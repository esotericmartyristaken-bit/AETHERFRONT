function Measure-ReleasePreservation([string]$Root,[string]$Version,[string]$RecordPath){
 $result=[ordered]@{observedAtUtc=[DateTime]::UtcNow.ToString('o');version=$Version;checksPassed=$false}
 function Shared-Text([string]$Path){
  $s=[IO.FileStream]::new($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
  try{$r=[IO.StreamReader]::new($s);try{$r.ReadToEnd()}finally{$r.Dispose()}}finally{$s.Dispose()}
 }
 function Same-Json($a,$b){($a|ConvertTo-Json -Depth 100 -Compress) -ceq ($b|ConvertTo-Json -Depth 100 -Compress)}
 try{
  if($Version -cnotmatch '^kesas-rc[1-9][0-9]{0,8}$'){throw 'Invalid preservation version'}
  $backup=Get-ChildItem -LiteralPath (Join-Path $Root 'Backups/updates') -Directory|Where-Object Name -CLike "*-$Version-*"|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1
  if(-not $backup){throw 'Activation backup missing'};$result.backup=$backup.FullName
  $old=(Shared-Text (Join-Path $backup.FullName 'Aetherfront/state.json'))|ConvertFrom-Json -DateKind String
  $state=(Shared-Text (Join-Path $Root 'Server/Aetherfront/state.json'))|ConvertFrom-Json -DateKind String
  $result.accountsUnchanged=Same-Json $old.Accounts $state.Accounts
  $result.craftRegistryUnchanged=Same-Json $old.Crafts $state.Crafts
  $result.personalProgressionUnchanged=Same-Json $old.PersonalProgression $state.PersonalProgression
  $result.accountCount=@($state.Accounts).Count;$result.craftCount=@($state.Crafts).Count
  $result.arenasEnabled=$state.EventMode.Active
  $result.personalProgressionSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($state.PersonalProgression|ConvertTo-Json -Depth 100 -Compress)))).ToLowerInvariant()
  $before=Join-Path $backup.FullName 'Universe';$live=Join-Path $Root 'Server/Universe';$missing=0;$changed=0;$count=0
  foreach($file in Get-ChildItem -LiteralPath $before -File -Recurse -Force){
   $count++;$target=Join-Path $live ([IO.Path]::GetRelativePath($before,$file.FullName))
   if(-not [IO.File]::Exists($target)){$missing++}elseif((Get-FileHash -LiteralPath $target).Hash -cne (Get-FileHash -LiteralPath $file.FullName).Hash){$changed++}
  }
  $result.universeFiles=$count;$result.universeFilesMissing=$missing;$result.universeFilesChanged=$changed
  $result.configurationsMatched=0
  $oldConfig=Join-Path $backup.FullName 'files/Server/Config'
  foreach($file in Get-ChildItem -LiteralPath $oldConfig -File){
   if($file.Name -ceq 'APFPack.json'){continue}
   if((Get-FileHash -LiteralPath $file.FullName).Hash -cne (Get-FileHash -LiteralPath (Join-Path $Root ('Server/Config/'+$file.Name))).Hash){throw 'A production configuration differs from activation backup'}
   $result.configurationsMatched++
  }
  $bridge=(Shared-Text (Join-Path $Root 'Server/APFDiscordBridge/status.json'))|ConvertFrom-Json
  $result.bridge=$bridge|Select-Object serverPid,updatedAt,playerCount,connectedCount
  $result.checksPassed=$result.accountsUnchanged -and $result.craftRegistryUnchanged -and $result.personalProgressionUnchanged -and $state.EventMode.Active -eq $false -and $missing -eq 0
  if(-not $result.checksPassed){$result.error='Player state differs from activation backup; inspect legitimate live activity before taking action'}
 }catch{$result.error=$_.Exception.Message}
 finally{[IO.File]::WriteAllText($RecordPath,($result|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))}
 return $result
}
