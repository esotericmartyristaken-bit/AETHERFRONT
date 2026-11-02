import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {validateMaintenance,projectMaintenance,MAINTENANCE_SCRIPT} from '../science-maintenance.mjs';
const execute=promisify(execFile);
const old='12345678-1234-1234-1234-123456789012',recent='22345678-1234-1234-1234-123456789012';
const intent=()=>({schemaVersion:1,id:'a'.repeat(32),expectedVersion:'kesas-rc39',modeTransactionId:'b'.repeat(32),disableCheats:true,sandboxVesselIds:[old],issuedAt:new Date(Date.now()-1000).toISOString(),expiresAt:new Date(Date.now()+3600000).toISOString()});
test('maintenance is expiring and bounded to cheats-off and exact safe vessel identities',()=>{
  assert.equal(validateMaintenance(intent()).disableCheats,true);
  for(const patch of [{disableCheats:false},{force:true},{path:'C:/private'},{sandboxVesselIds:['../bad']},{sandboxVesselIds:[old,old]},{expiresAt:new Date(Date.now()-1).toISOString()},{expectedVersion:'control-20'},{modeTransactionId:'../bad'}])assert.throws(()=>validateMaintenance({...intent(),...patch}));
});
test('maintenance readback excludes private payloads and cannot claim incomplete health',()=>{
  const v={schemaVersion:1,id:'a'.repeat(32),phase:'healthy',reason:'',removedCount:1,preservedCount:1,restartCount:1,cheatsEnabled:false,updatedAt:new Date().toISOString()};
  assert.deepEqual(projectMaintenance({...v,password:'private'}),v);
  assert.throws(()=>projectMaintenance({...v,restartCount:0}));
});
const COMMON=String.raw`
$ErrorActionPreference='Stop'
function Assert-UpdatePath($p){return [IO.Path]::GetFullPath($p)}
function Assert-UpdateTree($p){}
function Read-UpdateText($p,$max=1048576){return [IO.File]::ReadAllText($p)}
function Write-UpdateAtomic($p,$s){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))|Out-Null;[IO.File]::WriteAllText($p,[string]$s)}
function New-UpdateDirectory($p){[IO.Directory]::CreateDirectory($p)|Out-Null}
function Get-UpdateDeployment($r){return @{}}
function Get-UpdateServiceProcesses($r,$c){return [pscustomobject]@{Path=[IO.Path]::GetFullPath("$r/Server/Server.exe");Id=100}}
function Wait-UpdateServerStopped($r,$c){[IO.File]::WriteAllText("$r/stopped",'stop');if($env:APF_TEST_FAULT -eq 'stop'){throw 'stop fault'}}
function Start-UpdatedServer($r,$c){[IO.File]::WriteAllText("$r/started",'start');$b=@{serverPid=100;updatedAt=[DateTime]::UtcNow.ToString('o');playerCount=0;connectedCount=0}|ConvertTo-Json;[IO.File]::WriteAllText("$r/Server/APFDiscordBridge/status.json",$b)}
`;
async function runFixture({players=0,postCreation=false,docking=false,natural=false,fault=''}={}){
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-maint-')),native=path.join(root,'native'),code=path.join(root,'code'),job=path.join(root,'native-cache','science-maintenance','a'.repeat(32));
  const put=async(p,s)=>{await fs.mkdir(path.dirname(p),{recursive:true});await fs.writeFile(p,s);};
  const general='<GeneralSettings><GameMode>Science</GameMode><Cheats>true</Cheats><Password>private</Password></GeneralSettings>';
  await put(path.join(code,'KESAS-VPS-Server','Update.Common.ps1'),COMMON);
  await put(path.join(job,'request.json'),JSON.stringify(intent()));
  await put(path.join(native,'Server','Config','GeneralSettings.xml'),general);
  await put(path.join(native,'Server','Config','APFPack.json'),JSON.stringify({packVersion:'kesas-rc39',requiredClientVersion:'kesas-rc39'}));
  await put(path.join(root,'native-cache','server-mode','active.json'),JSON.stringify({id:'b'.repeat(32),phase:'healthy',mode:'Science',updatedAt:new Date(Date.now()-1000).toISOString()}));
  await put(path.join(root,'native-cache','server-mode','b'.repeat(32),'GeneralSettings.before.xml'),'private original');
  await put(path.join(native,'Server','Universe','Vessels',old+'.txt'),natural?'type = SpaceObject\nnatural asteroid':'type = Ship\nold sandbox vessel');
  await put(path.join(native,'Server','Universe','Vessels',recent+'.txt'),'type = Ship\nnew science vessel');
  await put(path.join(native,'Server','Aetherfront','state.json'),'private state sentinel');
  const log='# Server started at 2026-11-02 21:43:00 UTC\n'+(postCreation?`[2026-11-02 22:00:00] Vessel ${old} (ship) created by player Test (Launch)\n`:'')+(docking?`[2026-11-02 22:00:00] Vessel ${recent} (ship) removed by player Test (Coupled/Docked)\n`:'');
  await put(path.join(native,'Server','logs','CraftCreationAndRemoval.txt'),log);
  await put(path.join(native,'Server','APFDiscordBridge','status.json'),JSON.stringify({serverPid:100,updatedAt:new Date().toISOString(),playerCount:players,connectedCount:players}));
  // Set only disposable fixture timestamps to establish a real Windows baseline.
  const script=String.raw`
$ErrorActionPreference='Stop'
$r=$env:APF_MAINT_NATIVE_ROOT;$c=$env:APF_MAINT_CONTROL_ROOT
(Get-Item -LiteralPath "$c/native-cache/server-mode/${'b'.repeat(32)}/GeneralSettings.before.xml").CreationTimeUtc=[DateTime]::UtcNow.AddMinutes(-10)
(Get-Item -LiteralPath "$r/Server/Universe/Vessels/${old}.txt").CreationTimeUtc=[DateTime]::UtcNow.AddHours(-1)
`+MAINTENANCE_SCRIPT;
  const file=path.join(root,'run.ps1');await put(file,script);
  let exit=0;try{await execute('pwsh',['-NoProfile','-NonInteractive','-File',file],{windowsHide:true,env:{...process.env,APF_MAINT_NATIVE_ROOT:native,APF_MAINT_CODE_ROOT:code,APF_MAINT_CONTROL_ROOT:root,APF_MAINT_JOB:job,APF_TEST_FAULT:fault},timeout:20000,maxBuffer:65536});}catch(e){exit=e.code;}
  const result=JSON.parse(await fs.readFile(path.join(job,'receipt.json'),'utf8'));
  return {exit,result,root,native,job,general,read:p=>fs.readFile(path.join(native,p),'utf8'),exists:async p=>!!await fs.stat(path.join(native,p)).catch(()=>null)};
}
test('real helper removes only selected Sandbox vessel, preserves state/new vessel and restarts once',async()=>{
  const f=await runFixture();assert.equal(f.exit,0);assert.equal(f.result.phase,'healthy');assert.equal(f.result.removedCount,1);assert.equal(f.result.preservedCount,1);assert.equal(f.result.restartCount,1);
  assert.equal(await f.exists(`Server/Universe/Vessels/${old}.txt`),false);
  assert.equal(await f.read(`Server/Universe/Vessels/${recent}.txt`),'type = Ship\nnew science vessel');
  assert.equal(await f.read('Server/Aetherfront/state.json'),'private state sentinel');
  assert.equal(await f.read('Server/Config/GeneralSettings.xml'),f.general.replace('<Cheats>true</Cheats>','<Cheats>false</Cheats>'));
  assert.equal(await fs.readFile(path.join(f.job,'removed-vessels',old+'.txt'),'utf8'),'type = Ship\nold sandbox vessel');
});
test('occupied server, new creation, docking ambiguity and failed stop never remove a vessel',async()=>{
  for(const args of [{players:1},{postCreation:true},{docking:true},{fault:'stop'}]){
    const f=await runFixture(args);assert.ok(['blocked','waiting'].includes(f.result.phase));assert.equal(await f.read(`Server/Universe/Vessels/${old}.txt`),'type = Ship\nold sandbox vessel');assert.equal(await f.read(`Server/Universe/Vessels/${recent}.txt`),'type = Ship\nnew science vessel');assert.equal(await f.exists('started'),false);assert.equal(await f.read('Server/Config/GeneralSettings.xml'),f.general);
  }
});

test('natural asteroids and comets cannot be selected as player craft',async()=>{const f=await runFixture({natural:true});assert.equal(f.result.phase,'blocked');assert.equal(await f.exists(`Server/Universe/Vessels/${old}.txt`),true);assert.equal(await f.exists('stopped'),false);});
