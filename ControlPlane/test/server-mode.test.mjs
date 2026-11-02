import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {validateModeIntent,modeProfile,ServerModeAgent,MODE_SCRIPT,modeFailureEvidence} from '../server-mode.mjs';
import {publicModeStatus,writeJson,readJson} from '../public-status.mjs';
const execute=promisify(execFile);
const intent=()=>({schemaVersion:1,id:'a'.repeat(32),expectedVersion:'kesas-rc39',from:'Sandbox',to:'Science',issuedAt:new Date(Date.now()-1000).toISOString(),expiresAt:new Date(Date.now()+3600000).toISOString()});
const profile=()=>({profileId:'kesas-fixture-sandbox',saveNamespace:'APF/kesas-fixture/Sandbox',mode:'Sandbox',progressionSchemaVersion:1,resetOnPackChange:false,startingScience:17,sharedProgression:false});

test('mode authorization is one bounded, expiring operation, without paths/commands/force',()=>{
  assert.deepEqual(validateModeIntent(intent()).to,'Science');
  for(const patch of [{to:'Career'},{from:'Science'},{id:'../escape'},{expectedVersion:'control-14'},{force:true},{command:'cmd'},{expiresAt:new Date(Date.now()-1).toISOString()},{expiresAt:new Date(Date.now()+7200001).toISOString()},{issuedAt:new Date(Date.now()+10000).toISOString()}])assert.throws(()=>validateModeIntent({...intent(),...patch}));
});
test('mode-specific save namespace changes while all other profile policy survives',()=>{
  const before=profile(),after=modeProfile(before);
  assert.equal(after.profileId,'kesas-fixture-science');assert.equal(after.saveNamespace,'APF/kesas-fixture/Science');
  assert.deepEqual({...after,mode:before.mode,profileId:before.profileId,saveNamespace:before.saveNamespace},before);
  for(const patch of [{mode:'Career'},{profileId:'other'},{saveNamespace:'../Universe'},{resetOnPackChange:'false'},{progressionSchemaVersion:0}])assert.throws(()=>modeProfile({...before,...patch}));
  assert.equal(modeProfile({...before,progressionSchemaVersion:4}).progressionSchemaVersion,4);
});
test('public mode observation projects no private data and cannot assert incomplete success',()=>{
  const row={schemaVersion:1,id:'a'.repeat(32),phase:'healthy',reason:null,mode:'Science',profileId:'kesas-fixture-science',saveNamespace:'APF/kesas-fixture/Science',restartCount:1,updatedAt:new Date().toISOString()};
  assert.deepEqual(publicModeStatus({...row,password:'private',backup:'C:/private'}),row);
  for(const patch of [{restartCount:0},{mode:null},{profileId:null},{saveNamespace:'../private'},{reason:'raw private exception'}])assert.throws(()=>publicModeStatus({...row,...patch}));
});
async function fixture(t){
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-mode-'));
  // Fixtures are retained as compact evidence; do not recursively delete.
  const value=intent(),raw=Buffer.from(JSON.stringify(value));let calls=0,runs=0;
  const agent={cache:path.join(root,'native-cache'),root,code:path.join(root,'release','ControlPlane'),settings:{nativeRoot:path.join(root,'native'),powershell:'pwsh'},
    channel:{bytes:async url=>{calls++;return url.includes('/git/ref/')?Buffer.from(JSON.stringify({ref:'refs/heads/deploy',object:{type:'commit',sha:'b'.repeat(40)}})):raw;}},
    idle:async()=>true,journalPresent:async()=>false,activeMatches:async version=>version===value.expectedVersion,
    run:async()=>{runs++;return {exitCode:0};}};
  return {root,value,agent,runner:new ServerModeAgent(agent),calls:()=>calls,runs:()=>runs};
}
test('host independently authenticates immutable intent; claimed transaction never replays',async t=>{
  const f=await fixture(t);await f.runner.tick();assert.equal(f.calls(),3);assert.equal(f.runs(),1);
  const active=await readJson(path.join(f.agent.cache,'server-mode','active.json'));
  assert.equal(active.phase,'claimed');
  f.runner.nextPoll=0;await f.runner.tick();assert.equal(f.runs(),1);assert.equal(f.calls(),4);
});
test('occupied native lease, active journal and wrong release never invoke config helper',async t=>{
  for(const [key,body] of [['idle',async()=>false],['journalPresent',async()=>true],['activeMatches',async()=>false]]){
    const f=await fixture(t);f.agent[key]=body;
    if(key==='activeMatches')await assert.rejects(f.runner.tick());else await f.runner.tick();
    assert.equal(f.runs(),0);
  }
});
test('changed pinned intent and unfinished different operation are refused',async t=>{
  let f=await fixture(t);const original=f.agent.channel.bytes;
  f.agent.channel.bytes=async url=>url.includes('/'+'b'.repeat(40)+'/')?Buffer.from(JSON.stringify({...f.value,id:'c'.repeat(32)})):original(url);
  await assert.rejects(f.runner.tick());assert.equal(f.runs(),0);
  f=await fixture(t);await writeJson(path.join(f.agent.cache,'server-mode','active.json'),{id:'d'.repeat(32),phase:'applying'});
  await assert.rejects(f.runner.tick());assert.equal(f.runs(),0);
});

const MOCK_COMMON=String.raw`
$ErrorActionPreference='Stop'
$UpdateUtf8=[Text.UTF8Encoding]::new($false)
function Assert-UpdatePath($p){return [IO.Path]::GetFullPath($p)}
function Assert-UpdateTree($p){}
function Read-UpdateText($p,$max=1048576){return [IO.File]::ReadAllText($p)}
function Write-UpdateAtomic($p,$s){[IO.File]::WriteAllText($p,[string]$s,$UpdateUtf8)}
function Get-UpdateDeployment($r){return @{gamePort=49998;packPort=49999}}
function Get-UpdateServiceProcesses($r,$c){return [pscustomobject]@{Path=[IO.Path]::GetFullPath("$r/Server/Server.exe");Id=100}}
function Wait-UpdateServerStopped($r,$c){[IO.File]::WriteAllText("$r/stopped.txt",'stop');if($env:APF_MODE_FAULT -eq 'stop'){throw 'fixture stop failure'}}
function Start-UpdatedServer($r,$c){[IO.File]::WriteAllText("$r/started.txt",'start');if($env:APF_MODE_FAULT -eq 'start'){throw 'fixture start failure'};$b=@{updatedAt=[DateTime]::UtcNow.ToString('o');serverPid=100;playerCount=0;connectedCount=0}|ConvertTo-Json;[IO.File]::WriteAllText("$r/Server/APFDiscordBridge/status.json",$b)}
`;
async function psFixture(overrides={}){
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-mode-ps-')),native=path.join(root,'native'),code=path.join(root,'code'),job=path.join(root,'native-cache','server-mode','a'.repeat(32));
  const put=async(file,value)=>{await fs.mkdir(path.dirname(file),{recursive:true});await fs.writeFile(file,value);};
  await put(path.join(code,'KESAS-VPS-Server','Update.Common.ps1'),MOCK_COMMON);
  const general='<GeneralSettings><GameMode>Sandbox</GameMode><Password>private password</Password><MaxPlayers>8</MaxPlayers></GeneralSettings>';
  await put(path.join(native,'Server','Config','GeneralSettings.xml'),general);
  if(!overrides.automatic)await put(path.join(native,'Server','Config','APFCareer.json'),JSON.stringify({...profile(),...overrides.profile}));
  await put(path.join(native,'Server','Config','APFPack.json'),JSON.stringify({serverId:'kesas-fixture',packVersion:overrides.version??'kesas-rc39',requiredClientVersion:'kesas-rc39'}));
  await put(path.join(native,'Server','APFDiscordBridge','status.json'),JSON.stringify({updatedAt:new Date(Date.now()-(overrides.stale?30000:0)).toISOString(),serverPid:100,playerCount:overrides.players??0,connectedCount:overrides.connected??overrides.players??0}));
  await put(path.join(native,'Server','Universe','sentinel'),'existing universe');
  await put(path.join(job,'request.json'),JSON.stringify(intent()));
  if(overrides.journal)await put(path.join(native,'.update-transaction.json'),'{}');
  const script=path.join(root,'run.ps1');await put(script,MODE_SCRIPT);
  let exit=0;
  try{await execute('pwsh',['-NoProfile','-NonInteractive','-File',script],{windowsHide:true,env:{...process.env,APF_MODE_NATIVE_ROOT:native,APF_MODE_CODE_ROOT:code,APF_MODE_CONTROL_ROOT:root,APF_MODE_JOB:job,APF_MODE_FAULT:overrides.fault??''},timeout:20000,maxBuffer:65536});}
  catch(error){exit=error.code;}
  return {root,native,job,general,exit,read:rel=>fs.readFile(path.join(native,rel),'utf8'),exists:async rel=>!!await fs.stat(path.join(native,rel)).catch(()=>null),result:await readJson(path.join(job,'receipt.json'),null)};
}
test('real PowerShell configuration transaction preserves unrelated settings and universe; one stop/start',async()=>{
  const f=await psFixture();assert.equal(f.exit,0);assert.equal(f.result?.phase,'healthy');assert.equal(f.result?.restartCount,1);
  assert.equal(publicModeStatus(f.result).phase,'healthy');assert.equal(f.result.reason,null);
  assert.equal(await f.read('Server/Config/GeneralSettings.xml'),f.general.replace('<GameMode>Sandbox</GameMode>','<GameMode>Science</GameMode>'));
  assert.deepEqual(JSON.parse(await f.read('Server/Config/APFCareer.json')),modeProfile(profile()));
  assert.equal(await f.read('Server/Universe/sentinel'),'existing universe');
  assert.equal(await fs.readFile(path.join(f.job,'GeneralSettings.before.xml'),'utf8'),f.general);
  assert.equal(await f.exists('stopped.txt'),true);assert.equal(await f.exists('started.txt'),true);
});
test('real helper refuses occupied, pending-connected, stale, mismatched and applying-native states before stop',async()=>{
  for(const options of [{players:1},{connected:1},{stale:true},{version:'kesas-rc38'},{profile:{profileId:'unexpected'}},{journal:true}]){
    const f=await psFixture(options);assert.ok(['waiting','blocked'].includes(f.result?.phase),JSON.stringify(f));
    assert.equal(await f.exists('stopped.txt'),false);assert.equal(await f.read('Server/Config/GeneralSettings.xml'),f.general);
  }
});
test('failed stop never edits config; failed start retains blocked recovery and originals, without another restart',async()=>{
  let f=await psFixture({fault:'stop'});assert.equal(f.result?.phase,'blocked');assert.equal(await f.read('Server/Config/GeneralSettings.xml'),f.general);assert.equal(await f.exists('started.txt'),false);
  f=await psFixture({fault:'start'});assert.equal(f.result?.phase,'blocked');assert.equal(f.result?.reason,'mode-activation-needs-review');assert.equal(await f.exists('started.txt'),true);
  assert.equal(await fs.readFile(path.join(f.job,'GeneralSettings.before.xml'),'utf8'),f.general);
});
test('mode operation preserves a newer authoritative progression schema and exposes only safe failure proof',async()=>{
  let f=await psFixture({profile:{progressionSchemaVersion:4}});assert.equal(f.result.phase,'healthy');assert.equal(JSON.parse(await f.read('Server/Config/APFCareer.json')).progressionSchemaVersion,4);
  f=await psFixture({profile:{profileId:'unexpected'}});
  const evidence=await modeFailureEvidence({root:f.root,nativeRoot:f.native},f.result);
  assert.equal(evidence.backupCount,0);assert.equal(evidence.currentMode,'Sandbox');assert.equal(evidence.errorCode,'profile-baseline-invalid');
  const projected=publicModeStatus({...f.result,failureEvidence:{...evidence,password:'private'}});
  assert.equal(JSON.stringify(projected).includes('private'),false);
  f=await psFixture({fault:'stop'});
  assert.equal((await modeFailureEvidence({root:f.root,nativeRoot:f.native},f.result)).backupCount,2);
});
test('a fresh reviewed authorization can retry only an exact failed receipt before any preparation/stop',async t=>{
  async function setup(f,hashOverride){
    const helper=await fixture(t),old=f.result,evidence=await modeFailureEvidence({root:f.root,nativeRoot:f.native},old);
    const next={...intent(),id:'c'.repeat(32),retryOf:{id:old.id,receiptSha256:hashOverride??evidence.receiptSha256}};
    const raw=Buffer.from(JSON.stringify(next));
    helper.agent.root=f.root;helper.agent.cache=path.join(f.root,'native-cache');helper.agent.settings={root:f.root,nativeRoot:f.native,powershell:'pwsh'};
    helper.agent.channel.bytes=async url=>url.includes('/git/ref/')?Buffer.from(JSON.stringify({ref:'refs/heads/deploy',object:{type:'commit',sha:'b'.repeat(40)}})):raw;
    return helper;
  }
  const f=await psFixture({profile:{profileId:'unexpected'}});
  let helper=await setup(f,'0'.repeat(64));await assert.rejects(helper.runner.tick());assert.equal(helper.runs(),0);
  const original=await fs.readFile(path.join(f.job,'receipt.json'),'utf8');
  helper=await setup(f);await helper.runner.tick();assert.equal(helper.runs(),1);
  assert.equal(await fs.readFile(path.join(f.job,'receipt.json'),'utf8'),original);
  const stopped=await psFixture({fault:'stop'});helper=await setup(stopped);await assert.rejects(helper.runner.tick());assert.equal(helper.runs(),0);
});
test('Windows UTF-8 BOM profile does not hide protected pre-preparation evidence',async()=>{
  const f=await psFixture({profile:{profileId:'unexpected'}});
  const file=path.join(f.native,'Server','Config','APFCareer.json');
  await fs.writeFile(file,'\uFEFF'+await fs.readFile(file,'utf8'));
  const evidence=await modeFailureEvidence({root:f.root,nativeRoot:f.native},f.result);
  assert.equal(evidence.profileMode,'Sandbox');assert.equal(evidence.backupCount,0);
  assert.throws(()=>publicModeStatus({...f.result,evidenceProblem:'C:/private'}));
});
test('native automatic profile stays automatic; no override file is created and failed absent-profile evidence is readable',async()=>{
  let f=await psFixture({automatic:true});assert.equal(f.exit,0);assert.equal(f.result?.phase,'healthy');assert.equal(publicModeStatus(f.result).profileId,'kesas-fixture-science');
  assert.equal(f.result.saveNamespace,'APF/kesas-fixture/Science');assert.equal(await f.exists('Server/Config/APFCareer.json'),false);
  assert.equal(await f.exists('stopped.txt'),true);assert.equal(await f.exists('started.txt'),true);
  assert.equal(await fs.readFile(path.join(f.job,'APFCareer.absent.json'),'utf8'),'{"profileSource":"automatic","fileWasAbsent":true}');
  f=await psFixture({automatic:true,version:'kesas-rc38'});
  const evidence=await modeFailureEvidence({root:f.root,nativeRoot:f.native},f.result);
  assert.equal(evidence.backupCount,0);assert.equal(evidence.profileSource,'automatic');assert.equal(evidence.profileMode,'Sandbox');assert.equal(evidence.errorCode,'optional-profile-file-absent');
});
