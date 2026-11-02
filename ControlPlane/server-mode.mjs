// One bounded configuration operation, authenticated like deployment intents.
// No remote commands, paths, credentials, arbitrary config fields or force flag.
import fs from 'node:fs/promises';
import path from 'node:path';
import {REPOSITORY,exact,requireValue} from './contract.mjs';
import {parseStrictJson} from './github.mjs';
import {noLinks,readJson,writeJson,digest,fileHash} from './public-status.mjs';

const CHANNEL='deployment/server-mode.json';
export function validateModeIntent(value,now=Date.now()) {
  exact(value,['schemaVersion','id','expectedVersion','from','to','issuedAt','expiresAt',...(Object.hasOwn(value,'retryOf')?['retryOf']:[])],'server mode intent');
  const start=Date.parse(value.issuedAt),end=Date.parse(value.expiresAt);
  requireValue(value.schemaVersion===1&&/^[a-f0-9]{32}$/.test(value.id)&&/^kesas-rc[1-9][0-9]{0,14}$/.test(value.expectedVersion)&&
    value.from==='Sandbox'&&value.to==='Science'&&Number.isFinite(start)&&Number.isFinite(end)&&
    new Date(start).toISOString()===value.issuedAt&&new Date(end).toISOString()===value.expiresAt&&
    start<=now+5000&&end>now&&end-start>0&&end-start<=2*60*60*1000,'Invalid or expired server mode intent');
  if(Object.hasOwn(value,'retryOf')){exact(value.retryOf,['id','receiptSha256'],'mode retry proof');requireValue(/^[a-f0-9]{32}$/.test(value.retryOf.id)&&value.retryOf.id!==value.id&&/^[a-f0-9]{64}$/.test(value.retryOf.receiptSha256),'Invalid mode retry identity');}
  return value;
}
export function modeProfile(profile) {
  requireValue(profile&&profile.mode==='Sandbox'&&/^[a-zA-Z0-9_.-]{1,120}-sandbox$/.test(profile.profileId)&&
    /^APF\/[a-zA-Z0-9_.-]{1,120}\/Sandbox$/.test(profile.saveNamespace)&&
    (!Object.hasOwn(profile,'progressionSchemaVersion')||(Number.isSafeInteger(profile.progressionSchemaVersion)&&profile.progressionSchemaVersion>0))&&
    (!Object.hasOwn(profile,'resetOnPackChange')||typeof profile.resetOnPackChange==='boolean'),'Unreviewed Sandbox profile');
  return {...profile,mode:'Science',profileId:profile.profileId.slice(0,-8)+'-science',saveNamespace:profile.saveNamespace.slice(0,-7)+'Science'};
}

// Runs only from authenticated immutable control code, under its protected cache.
// Same native publisher lock and exact launcher/port checks as normal deployment.
export const MODE_SCRIPT=String.raw`
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Set-StrictMode -Version Latest
$root=$env:APF_MODE_NATIVE_ROOT
$code=$env:APF_MODE_CODE_ROOT
$job=$env:APF_MODE_JOB
. (Join-Path $code 'KESAS-VPS-Server/Update.Common.ps1')
$root=Assert-UpdatePath $root
$job=Assert-UpdatePath $job
Assert-UpdateTree $job
$request=(Read-UpdateText (Join-Path $job 'request.json') 16384)|ConvertFrom-Json
$receipt=Join-Path $job 'receipt.json'
$state=Join-Path $env:APF_MODE_CONTROL_ROOT 'native-cache/server-mode/active.json'
$lock=$null;$changed=$false;$stopped=$false;$restartCount=0
function Record([string]$Phase,$Reason,$Profile=$null) {
  $value=[ordered]@{schemaVersion=1;id=$request.id;phase=$Phase;reason=$Reason;mode=$null;profileId=$null;saveNamespace=$null;restartCount=$restartCount;updatedAt=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')}
  if($Profile){$value.mode=$Profile.mode;$value.profileId=$Profile.profileId;$value.saveNamespace=$Profile.saveNamespace}
  Write-UpdateAtomic $receipt ($value|ConvertTo-Json -Compress)
  Write-UpdateAtomic $state ($value|ConvertTo-Json -Compress)
  return $value
}
function Empty($cfg) {
  $bridgePath=Join-Path $root 'Server/APFDiscordBridge/status.json'
  Assert-UpdatePath $bridgePath|Out-Null
  $bridge=Read-ModeBridge $bridgePath
  if($null -eq $bridge){return $false}
  $age=([DateTimeOffset]::UtcNow-([DateTimeOffset]$bridge.updatedAt).ToUniversalTime()).TotalSeconds
  $ids=@(Get-UpdateServiceProcesses $root $cfg|Where-Object Path -ieq ([IO.Path]::GetFullPath("$root/Server/Server.exe"))|ForEach-Object Id)
  return ($age -ge -2 -and $age -le 10 -and $ids -contains $bridge.serverPid -and [string]$bridge.playerCount -ceq '0' -and [string]$bridge.connectedCount -ceq '0')
}
function Read-ModeBridge([string]$Path) {
  for($i=0;$i -lt 20;$i++){
    $stream=$null;$reader=$null
    try{
      $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
      if($stream.Length -gt 4194304){return $null}
      $reader=[IO.StreamReader]::new($stream)
      return ($reader.ReadToEnd()|ConvertFrom-Json)
    }catch{if($i -lt 19){Start-Sleep -Milliseconds 100}}
    finally{if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
  }
  return $null
}
try {
  try{$lock=[IO.FileStream]::new((Join-Path $root '.publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
  catch [IO.IOException] {Record 'waiting' 'native-busy'|ConvertTo-Json -Compress;exit 0}
  if(Test-Path -LiteralPath (Join-Path $root '.update-transaction.json')){Record 'waiting' 'native-journal-present'|ConvertTo-Json -Compress;exit 0}
  if($request.schemaVersion -ne 1 -or $request.from -cne 'Sandbox' -or $request.to -cne 'Science' -or $request.id -cnotmatch '^[a-f0-9]{32}$' -or [DateTimeOffset]$request.expiresAt -le [DateTimeOffset]::UtcNow){throw 'Invalid mode request'}
  $cfg=Get-UpdateDeployment $root
  $pack=(Read-UpdateText (Join-Path $root 'Server/Config/APFPack.json') 1048576)|ConvertFrom-Json
  if($pack.packVersion -cne $request.expectedVersion -or $pack.requiredClientVersion -cne $request.expectedVersion){throw 'Application version changed'}
  $generalPath=Join-Path $root 'Server/Config/GeneralSettings.xml'
  $careerPath=Join-Path $root 'Server/Config/APFCareer.json'
  $generalRaw=Read-UpdateText $generalPath 1048576
  $careerRaw=Read-UpdateText $careerPath 1048576
  [xml]$general=$generalRaw
  $profile=$careerRaw|ConvertFrom-Json
  $modeNode=$general.SelectNodes('//GameMode')
  if($modeNode.Count -ne 1 -or $modeNode[0].InnerText -cne 'Sandbox' -or $profile.mode -cne 'Sandbox' -or
    $profile.profileId -cnotmatch '^[a-zA-Z0-9_.-]{1,120}-sandbox$' -or $profile.saveNamespace -cnotmatch '^APF/[a-zA-Z0-9_.-]{1,120}/Sandbox$' -or
    ($profile.PSObject.Properties['progressionSchemaVersion'] -and [int]$profile.progressionSchemaVersion -le 0) -or
    ($profile.PSObject.Properties['resetOnPackChange'] -and $profile.resetOnPackChange -isnot [bool])){throw 'Unreviewed mode/profile baseline'}
  if(-not (Empty $cfg)){Record 'waiting' 'players-present-or-status-unavailable'|ConvertTo-Json -Compress;exit 0}
  $profile.mode='Science'
  $profile.profileId=$profile.profileId.Substring(0,$profile.profileId.Length-8)+'-science'
  $profile.saveNamespace=$profile.saveNamespace.Substring(0,$profile.saveNamespace.Length-7)+'Science'
  $modeNode[0].InnerText='Science'
  # Preserve private original configuration, never copy Universe, identity or keys.
  foreach($row in @(@('GeneralSettings.before.xml',$generalRaw),@('APFCareer.before.json',$careerRaw))){
    $backup=Join-Path $job $row[0]
    if(Test-Path -LiteralPath $backup){throw 'Prior mode preparation requires review'}
    $stream=[IO.FileStream]::new($backup,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$bytes=$UpdateUtf8.GetBytes($row[1]);$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
  }
  $null=Record 'prepared' $null
  if((Read-UpdateText $generalPath 1048576) -cne $generalRaw -or (Read-UpdateText $careerPath 1048576) -cne $careerRaw){throw 'Configuration changed during preparation'}
  if(-not (Empty $cfg)){throw 'Player activity resumed before stop'}
  $null=Record 'applying' $null
  Wait-UpdateServerStopped $root $cfg
  $stopped=$true
  # Stop is proven before either config write. No state/DLL/media changes.
  $changed=$true
  Write-UpdateAtomic $generalPath $general.OuterXml
  Write-UpdateAtomic $careerPath ($profile|ConvertTo-Json -Depth 12)
  $env:APF_SERVER_TASK='KESAS Server'
  $env:APF_CONTROL_ROOT=$env:APF_MODE_CONTROL_ROOT
  Start-UpdatedServer $root $cfg
  $restartCount=1
  $until=[DateTime]::UtcNow.AddSeconds(60)
  do {
    $bridge=Read-ModeBridge (Join-Path $root 'Server/APFDiscordBridge/status.json')
    if($null -eq $bridge){if([DateTime]::UtcNow -gt $until){throw 'Fresh game bridge did not return'};Start-Sleep -Milliseconds 500;continue}
    $age=([DateTimeOffset]::UtcNow-([DateTimeOffset]$bridge.updatedAt).ToUniversalTime()).TotalSeconds
    $ids=@(Get-UpdateServiceProcesses $root $cfg|Where-Object Path -ieq ([IO.Path]::GetFullPath("$root/Server/Server.exe"))|ForEach-Object Id)
    if($age -ge -2 -and $age -le 10 -and $ids -contains $bridge.serverPid){break}
    if([DateTime]::UtcNow -gt $until){throw 'Fresh game bridge did not return'}
    Start-Sleep -Milliseconds 500
  }while($true)
  [xml]$actual=Read-UpdateText $generalPath 1048576
  $actualProfile=(Read-UpdateText $careerPath 1048576)|ConvertFrom-Json
  if($actual.SelectSingleNode('//GameMode').InnerText -cne 'Science' -or $actualProfile.mode -cne 'Science' -or $actualProfile.profileId -cne $profile.profileId -or $actualProfile.saveNamespace -cne $profile.saveNamespace){throw 'Mode readback mismatch'}
  Record 'healthy' $null $actualProfile|ConvertTo-Json -Compress
}catch {
  # Never guess whether shutdown/start completed. Keep private evidence and do
  # not auto-repeat an applying transaction or overwrite potentially new state.
  $null=Record 'blocked' $(if($changed){'mode-activation-needs-review'}elseif($stopped){'mode-start-needs-review'}else{'mode-preparation-needs-review'})
  $_|Out-String|Set-Content -LiteralPath (Join-Path $job 'private-error.txt')
  exit 1
}finally{if($lock){$lock.Dispose()}}
`;

export class ServerModeAgent {
  constructor(agent){this.agent=agent;this.nextPoll=0;}
  async tick(now=Date.now()) {
    if(now<this.nextPoll)return;
    this.nextPoll=now+60000;
    const agent=this.agent,base=path.join(agent.cache,'server-mode');
    await noLinks(base);await fs.mkdir(base,{recursive:true});
    let observed;
    try{observed=await agent.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/${CHANNEL}`,16384);}
    catch(error){if(error.httpStatus===404)return;throw error;}
    const candidate=parseStrictJson(observed.toString('utf8'));
    const active=await readJson(path.join(base,'active.json'),null);
    // Ignored/consumed requests authorize nothing and need no new GitHub API
    // call, even after the original one-hour authorization expires.
    if(active&&active.id===candidate.id&&active.phase!=='waiting')return;
    const intent=validateModeIntent(candidate,now);
    if(active&&active.id!==intent.id&&active.phase!=='healthy'){
      const proof=await modeFailureEvidence(agent.settings,active);
      requireValue(active.phase==='blocked'&&intent.retryOf?.id===active.id&&proof?.backupCount===0&&
        intent.retryOf.receiptSha256===proof.receiptSha256,'Unfinished mode transaction requires reviewed pre-preparation proof');
    }else requireValue(!intent.retryOf,'Mode retry requires a matching blocked operation');
    const ref=parseStrictJson((await agent.channel.bytes(`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`,16384)).toString('utf8'));
    const commit=ref.object?.sha;
    requireValue(ref.ref==='refs/heads/deploy'&&ref.object?.type==='commit'&&/^[a-f0-9]{40}$/.test(commit),'Invalid mode intent reference');
    const pinned=await agent.channel.bytes(`https://raw.githubusercontent.com/${REPOSITORY}/${commit}/${CHANNEL}`,16384);
    requireValue(digest(pinned)===digest(observed),'Mode intent changed during authentication');
    validateModeIntent(parseStrictJson(pinned.toString('utf8')),Date.now());
    if(!await agent.idle()||await agent.journalPresent())return;
    requireValue(await agent.activeMatches(intent.expectedVersion),'Mode application version mismatch');
    const job=path.join(base,intent.id);await noLinks(job);await fs.mkdir(job,{recursive:true});
    const request=path.join(job,'request.json');await noLinks(request);
    const previous=await readJson(request,null);
    if(previous)requireValue(JSON.stringify(previous)===JSON.stringify(intent),'Mode request identity changed');
    else await writeJson(request,intent);
    await writeJson(path.join(job,'authorization.json'),{commit,intentSha256:digest(pinned)});
    // Claim before spawn. A crash cannot cause a second stop/apply.
    await writeJson(path.join(base,'active.json'),{schemaVersion:1,id:intent.id,phase:'claimed',reason:null,mode:null,profileId:null,saveNamespace:null,restartCount:0,updatedAt:new Date().toISOString()});
    await agent.run(agent.settings.powershell,['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(MODE_SCRIPT,'utf16le').toString('base64')],
      {env:{...process.env,APF_MODE_NATIVE_ROOT:agent.settings.nativeRoot,APF_MODE_CODE_ROOT:path.dirname(agent.code),APF_MODE_CONTROL_ROOT:agent.root,APF_MODE_JOB:job},captureOutput:false});
  }
}

// Read-only bounded projection. Never expose raw errors, settings or paths.
export async function modeFailureEvidence(settings,active){
  if(!active||active.phase!=='blocked'||!/^[a-f0-9]{32}$/.test(active.id))return null;
  const job=path.join(settings.root,'native-cache','server-mode',active.id);
  const receipt=path.join(job,'receipt.json');await noLinks(receipt);
  const row=await readJson(receipt,null);
  requireValue(row?.id===active.id&&row.phase==='blocked','Mode receipt mismatch');
  let backupCount=0;
  for(const name of ['GeneralSettings.before.xml','APFCareer.before.json']){
    const file=path.join(job,name);await noLinks(file);if(await fs.stat(file).catch(e=>{if(e.code==='ENOENT')return null;throw e;}))backupCount++;
  }
  const error=path.join(job,'private-error.txt');await noLinks(error);
  const stat=await fs.stat(error);requireValue(stat.size<=32768,'Mode error exceeds bound');
  const text=await fs.readFile(error,'utf8');
  const errorCode=/Unreviewed mode\/profile baseline/.test(text)?'profile-baseline-invalid':/progressionSchemaVersion|resetOnPackChange/.test(text)?'profile-policy-field-invalid':/being used by another process|sharing violation/i.test(text)?'bridge-file-busy':/Application version changed/.test(text)?'application-version-changed':/Player activity resumed/.test(text)?'players-resumed':/property.*cannot be found/i.test(text)?'runtime-property-missing':'unclassified-preparation-error';
  const config=path.join(settings.nativeRoot,'Server','Config');
  const careerPath=path.join(config,'APFCareer.json'),generalPath=path.join(config,'GeneralSettings.xml');
  await noLinks(careerPath);await noLinks(generalPath);
  requireValue((await fs.stat(careerPath)).size<=1048576&&(await fs.stat(generalPath)).size<=1048576,'Mode configuration exceeds bound');
  const profile=await readJson(careerPath),general=await fs.readFile(generalPath,'utf8');
  const result={receiptSha256:await fileHash(receipt),backupCount,errorCode,currentMode:/<GameMode>\s*(Sandbox|Science|Career)\s*<\/GameMode>/.exec(general)?.[1]??'unknown',
    profileMode:['Sandbox','Science','Career'].includes(profile.mode)?profile.mode:'unknown',
    progressionSchemaVersion:Number.isSafeInteger(profile.progressionSchemaVersion)?profile.progressionSchemaVersion:null,
    resetOnPackChange:typeof profile.resetOnPackChange==='boolean'?profile.resetOnPackChange:null};
  return result;
}
