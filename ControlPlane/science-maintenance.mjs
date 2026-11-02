// Bounded, authenticated maintenance: Cheats=false and exact Sandbox vessel IDs.
// No arbitrary commands, paths, other settings, or progression/state mutations.
import fs from 'node:fs/promises';
import path from 'node:path';
import {REPOSITORY,exact,requireValue} from './contract.mjs';
import {parseStrictJson} from './github.mjs';
import {noLinks,readJson,writeJson,digest} from './public-status.mjs';
import {collectHousekeeping} from './science-housekeeping.mjs';
const GUID=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const CHANNEL='deployment/science-maintenance.json';
export function validateMaintenance(value,now=Date.now()){
  exact(value,['schemaVersion','id','expectedVersion','modeTransactionId','disableCheats','sandboxVesselIds','issuedAt','expiresAt'],'science maintenance');
  const start=Date.parse(value.issuedAt),end=Date.parse(value.expiresAt);
  requireValue(value.schemaVersion===1&&/^[a-f0-9]{32}$/.test(value.id)&&/^kesas-rc[1-9][0-9]{0,14}$/.test(value.expectedVersion)&&/^[a-f0-9]{32}$/.test(value.modeTransactionId)&&value.disableCheats===true&&
    Array.isArray(value.sandboxVesselIds)&&value.sandboxVesselIds.length<=512&&value.sandboxVesselIds.every(id=>GUID.test(id))&&new Set(value.sandboxVesselIds).size===value.sandboxVesselIds.length&&
    Number.isFinite(start)&&Number.isFinite(end)&&new Date(start).toISOString()===value.issuedAt&&new Date(end).toISOString()===value.expiresAt&&start<=now+5000&&end>now&&end-start>0&&end-start<=7200000,'Invalid science maintenance authorization');
  return value;
}
export const MAINTENANCE_SCRIPT=String.raw`
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=$env:APF_MAINT_NATIVE_ROOT
$job=$env:APF_MAINT_JOB
$control=$env:APF_MAINT_CONTROL_ROOT
. (Join-Path $env:APF_MAINT_CODE_ROOT 'KESAS-VPS-Server/Update.Common.ps1')
$root=Assert-UpdatePath $root;$job=Assert-UpdatePath $job
Assert-UpdateTree $job
$r=(Read-UpdateText (Join-Path $job 'request.json') 65536)|ConvertFrom-Json
$receipt=Join-Path $job 'receipt.json'
$active=Join-Path $control 'native-cache/science-maintenance/active.json'
$restartCount=0;$removed=0;$preserved=0;$lock=$null;$applying=$false
function Record([string]$phase,[string]$reason) {
  $value=[ordered]@{schemaVersion=1;id=$r.id;phase=$phase;reason=$reason;removedCount=$removed;preservedCount=$preserved;restartCount=$restartCount;cheatsEnabled=$null;updatedAt=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')}
  if($phase -ceq 'healthy'){$value.cheatsEnabled=$false}
  Write-UpdateAtomic $receipt ($value|ConvertTo-Json -Compress)
  Write-UpdateAtomic $active ($value|ConvertTo-Json -Compress)
}
function Bridge {
  $p=Join-Path $root 'Server/APFDiscordBridge/status.json';Assert-UpdatePath $p|Out-Null
  for($i=0;$i -lt 20;$i++){
    $stream=$null;$reader=$null
    try{$stream=[IO.FileStream]::new($p,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));if($stream.Length -gt 4194304){return $null};$reader=[IO.StreamReader]::new($stream);return ($reader.ReadToEnd()|ConvertFrom-Json)}
    catch{if($i -lt 19){Start-Sleep -Milliseconds 100}}
    finally{if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
  }
  return $null
}
function IsEmpty($cfg) {
  $b=Bridge;if($null -eq $b){return $false}
  $age=([DateTimeOffset]::UtcNow-([DateTimeOffset]$b.updatedAt).ToUniversalTime()).TotalSeconds
  $ids=@(Get-UpdateServiceProcesses $root $cfg|Where-Object Path -ieq ([IO.Path]::GetFullPath("$root/Server/Server.exe"))|ForEach-Object Id)
  return ($age -ge -2 -and $age -le 10 -and $ids -contains $b.serverPid -and [string]$b.playerCount -ceq '0' -and [string]$b.connectedCount -ceq '0')
}
function Hash([string]$p){Assert-UpdatePath $p|Out-Null;return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
function Inventory {
  $dir=Join-Path $root 'Server/Universe/Vessels';Assert-UpdateTree $dir
  $files=@(Get-ChildItem -LiteralPath $dir -File)
  if($files.Count -gt 512 -or @(Get-ChildItem -LiteralPath $dir -Directory).Count){throw 'Unreviewed vessel directory'}
  $result=@{}
  foreach($f in $files){if($f.Name -cnotmatch '^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}\.txt$'){throw 'Unreviewed vessel filename'};$result[$f.BaseName]=[pscustomobject]@{file=$f;sha256=(Hash $f.FullName)}}
  return $result
}
function ValidateTargets($inventory,[string]$log) {
  $docking=$log -match '\(Coupled/Docked\)'
  foreach($id in $r.sandboxVesselIds){
    if(-not $inventory.ContainsKey([string]$id)){throw 'Selected vessel changed'}
    $f=$inventory[[string]$id].file
    $content=Read-UpdateText $f.FullName 16777216
    $type=[regex]::Match($content,'(?m)^type\s*=\s*([A-Za-z]+)\s*$').Groups[1].Value
    if($type -cnotmatch '^(Ship|Plane|Probe|Debris|EVA|Flag|Base|Lander|Station|Relay|Rover)$'){throw 'Selected vessel is not a player craft'}
    if($f.CreationTimeUtc -ge $prepared.AddSeconds(-1) -or ($docking -and $f.LastWriteTimeUtc -ge $prepared)){throw 'Selected vessel provenance ambiguous'}
    if($log -match ('(?m)^\[[^\r\n]+\] Vessel '+[regex]::Escape([string]$id)+' \([^\r\n]*\) created by player ')){throw 'Selected vessel was created in Science'}
  }
}
try {
  try{$lock=[IO.FileStream]::new((Join-Path $root '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{Record 'waiting' 'native-busy';exit 0}
  if(Test-Path -LiteralPath (Join-Path $root '.update-transaction.json')){Record 'waiting' 'native-journal-present';exit 0}
  if($r.schemaVersion -ne 1 -or $r.disableCheats -ne $true -or $r.id -cnotmatch '^[a-f0-9]{32}$' -or $r.modeTransactionId -cnotmatch '^[a-f0-9]{32}$' -or [DateTimeOffset]$r.expiresAt -le [DateTimeOffset]::UtcNow){throw 'Invalid maintenance request'}
  $ids=@($r.sandboxVesselIds);if($ids.Count -gt 512 -or @($ids|Sort-Object -Unique).Count -ne $ids.Count){throw 'Invalid vessel selection'}
  foreach($id in $ids){if([string]$id -cnotmatch '^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$'){throw 'Invalid vessel selection'}}
  $cfg=Get-UpdateDeployment $root
  $pack=(Read-UpdateText (Join-Path $root 'Server/Config/APFPack.json') 1048576)|ConvertFrom-Json
  if($pack.packVersion -cne $r.expectedVersion -or $pack.requiredClientVersion -cne $r.expectedVersion){throw 'Application changed'}
  $mode=(Read-UpdateText (Join-Path $control 'native-cache/server-mode/active.json') 16384)|ConvertFrom-Json
  if($mode.id -cne $r.modeTransactionId -or $mode.phase -cne 'healthy' -or $mode.mode -cne 'Science'){throw 'Science transition changed'}
  $original=Join-Path $control ('native-cache/server-mode/'+$r.modeTransactionId+'/GeneralSettings.before.xml');Assert-UpdatePath $original|Out-Null
  $prepared=(Get-Item -LiteralPath $original).CreationTimeUtc
  if($prepared.Year -lt 2026 -or $prepared -ge ([DateTimeOffset]$mode.updatedAt).UtcDateTime){throw 'Invalid cutover evidence'}
  $generalPath=Join-Path $root 'Server/Config/GeneralSettings.xml'
  $generalRaw=Read-UpdateText $generalPath 1048576
  [xml]$general=$generalRaw
  if($general.SelectNodes('//Cheats').Count -ne 1 -or $general.SelectNodes('//GameMode').Count -ne 1 -or $general.SelectSingleNode('//GameMode').InnerText -cne 'Science'){throw 'Unexpected settings'}
  $logPath=Join-Path $root 'Server/logs/CraftCreationAndRemoval.txt'
  $log=Read-UpdateText $logPath 8388608
  $before=Inventory;ValidateTargets $before $log
  if(-not (IsEmpty $cfg)){Record 'waiting' 'players-present-or-status-unavailable';exit 0}
  $backup=Join-Path $job 'GeneralSettings.before.xml'
  if(Test-Path -LiteralPath $backup){throw 'Prior maintenance preparation requires review'}
  [IO.File]::Copy($generalPath,$backup,$false)
  Write-UpdateAtomic (Join-Path $job 'CraftCreation.before.txt') $log
  Record 'prepared' ''
  if((Read-UpdateText $generalPath 1048576) -cne $generalRaw -or -not (IsEmpty $cfg)){throw 'Preparation changed'}
  Record 'applying' ''; $applying=$true
  Wait-UpdateServerStopped $root $cfg
  # Final save is complete. Recheck exact targets and protect every other file.
  $log=Read-UpdateText $logPath 8388608
  Write-UpdateAtomic (Join-Path $job 'CraftCreation.final.txt') $log
  $before=Inventory;ValidateTargets $before $log
  $statePath=Join-Path $root 'Server/Aetherfront/state.json';$stateHash=Hash $statePath
  Write-UpdateAtomic (Join-Path $job 'vessel-manifest.before.json') (@($before.Keys|Sort-Object|ForEach-Object {@{id=$_;sha256=$before[$_].sha256;selected=($ids -ccontains $_)}})|ConvertTo-Json -Depth 5)
  $general.SelectSingleNode('//Cheats').InnerText='false'
  $removedDir=Join-Path $job 'removed-vessels';New-UpdateDirectory $removedDir
  foreach($id in $ids){$f=$before[[string]$id].file;[IO.File]::Move($f.FullName,(Join-Path $removedDir $f.Name));$removed++}
  Write-UpdateAtomic $generalPath $general.OuterXml
  $after=Inventory
  foreach($id in $before.Keys){if($ids -ccontains $id){if($after.ContainsKey($id)){throw 'Sandbox vessel still active'}}else{if(-not $after.ContainsKey($id) -or $after[$id].sha256 -cne $before[$id].sha256){throw 'Protected vessel changed'};$preserved++}}
  if($after.Count -ne $preserved -or (Hash $statePath) -cne $stateHash){throw 'State preservation failed'}
  Write-UpdateAtomic (Join-Path $job 'preservation.json') (@{protectedVessels=$preserved;removedVessels=$removed;stateSha256=$stateHash;verifiedBeforeStart=$true}|ConvertTo-Json)
  $env:APF_SERVER_TASK='KESAS Server';$env:APF_CONTROL_ROOT=$control
  Start-UpdatedServer $root $cfg;$restartCount=1
  $deadline=[DateTime]::UtcNow.AddSeconds(90)
  do{$b=Bridge;if($null -ne $b){$age=([DateTimeOffset]::UtcNow-([DateTimeOffset]$b.updatedAt).ToUniversalTime()).TotalSeconds;$pids=@(Get-UpdateServiceProcesses $root $cfg|Where-Object Path -ieq ([IO.Path]::GetFullPath("$root/Server/Server.exe"))|ForEach-Object Id);if($age -ge -2 -and $age -le 10 -and $pids -contains $b.serverPid){break}};if([DateTime]::UtcNow -gt $deadline){throw 'Fresh bridge did not return'};Start-Sleep -Milliseconds 500}while($true)
  [xml]$actual=Read-UpdateText $generalPath 1048576
  if($actual.SelectSingleNode('//Cheats').InnerText -cne 'false' -or $actual.SelectSingleNode('//GameMode').InnerText -cne 'Science'){throw 'Settings readback failed'}
  foreach($id in $ids){if([IO.File]::Exists((Join-Path $root ('Server/Universe/Vessels/'+$id+'.txt')))){throw 'Removed vessel returned'}}
  Record 'healthy' ''
}catch{
  Record 'blocked' $(if($applying){'maintenance-activation-needs-review'}else{'maintenance-preparation-needs-review'})
  $_|Out-String|Set-Content -LiteralPath (Join-Path $job 'private-error.txt');exit 1
}finally{if($lock){$lock.Dispose()}}
`;
export class ScienceMaintenanceAgent{
  constructor(agent){this.agent=agent;this.nextPoll=0;}
  async tick(now=Date.now()){
    if(now<this.nextPoll)return;this.nextPoll=now+60000;
    const a=this.agent,base=path.join(a.cache,'science-maintenance');await noLinks(base);await fs.mkdir(base,{recursive:true});
    let raw;try{raw=await a.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/${CHANNEL}`,65536);}catch(e){if(e.httpStatus===404)return;throw e;}
    const candidate=parseStrictJson(raw.toString('utf8')),active=await readJson(path.join(base,'active.json'),null);
    if(active&&active.id===candidate.id&&active.phase!=='waiting')return;
    const intent=validateMaintenance(candidate,now);
    requireValue(!active||active.id===intent.id||active.phase==='healthy','Unfinished maintenance needs review');
    const ref=parseStrictJson((await a.channel.bytes(`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`,16384)).toString('utf8'));
    const commit=ref.object?.sha;requireValue(ref.ref==='refs/heads/deploy'&&ref.object?.type==='commit'&&/^[a-f0-9]{40}$/.test(commit),'Invalid maintenance reference');
    const pinned=await a.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/${commit}/${CHANNEL}`,65536);
    requireValue(digest(pinned)===digest(raw),'Maintenance intent changed');validateMaintenance(parseStrictJson(pinned.toString('utf8')));
    if(!await a.idle()||await a.journalPresent())return;
    requireValue(await a.activeMatches(intent.expectedVersion),'Maintenance application changed');
    const inventory=await collectHousekeeping(a.settings,{noLinks});
    requireValue(inventory.modeTransactionId===intent.modeTransactionId&&intent.sandboxVesselIds.every(id=>inventory.rows.some(r=>r.id===id&&r.classification==='sandbox'&&!['SpaceObject','Unknown'].includes(r.vesselType))),'Selected vessel provenance changed');
    const job=path.join(base,intent.id);await noLinks(job);await fs.mkdir(job,{recursive:true});
    const request=path.join(job,'request.json'),previous=await readJson(request,null);
    if(previous)requireValue(JSON.stringify(previous)===JSON.stringify(intent),'Maintenance identity changed');else await writeJson(request,intent);
    await writeJson(path.join(job,'authorization.json'),{commit,intentSha256:digest(pinned),evidenceSha256:inventory.evidenceSha256});
    await writeJson(path.join(base,'active.json'),{schemaVersion:1,id:intent.id,phase:'claimed',reason:'',removedCount:0,preservedCount:0,restartCount:0,cheatsEnabled:null,updatedAt:new Date().toISOString()});
    await a.run(a.settings.powershell,['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(MAINTENANCE_SCRIPT,'utf16le').toString('base64')],{env:{...process.env,APF_MAINT_NATIVE_ROOT:a.settings.nativeRoot,APF_MAINT_CODE_ROOT:path.dirname(a.code),APF_MAINT_CONTROL_ROOT:a.root,APF_MAINT_JOB:job},captureOutput:false});
  }
}
export function projectMaintenance(v){
  if(v===null)return null;
  requireValue(v?.schemaVersion===1&&/^[a-f0-9]{32}$/.test(v.id)&&['claimed','waiting','prepared','applying','blocked','healthy'].includes(v.phase)&&['','native-busy','native-journal-present','players-present-or-status-unavailable','maintenance-preparation-needs-review','maintenance-activation-needs-review'].includes(v.reason)&&Number.isFinite(Date.parse(v.updatedAt))&&[v.removedCount,v.preservedCount,v.restartCount].every(n=>Number.isSafeInteger(n)&&n>=0&&n<=512)&&[null,false].includes(v.cheatsEnabled)&&v.restartCount<=1,'Invalid maintenance status');
  requireValue(v.phase!=='healthy'||(v.cheatsEnabled===false&&v.restartCount===1),'Incomplete maintenance');
  return {schemaVersion:1,id:v.id,phase:v.phase,reason:v.reason,removedCount:v.removedCount,preservedCount:v.preservedCount,restartCount:v.restartCount,cheatsEnabled:v.cheatsEnabled,updatedAt:v.updatedAt};
}
