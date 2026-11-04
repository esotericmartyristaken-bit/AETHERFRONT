import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {snapshot,validateSnapshot} from './LocalBridge.mjs';
const now=Date.parse('2026-11-04T00:00:00Z');
const valid={updatedAt:new Date(now-1000).toISOString(),serverPid:123,playerCount:0,connectedCount:0};
test('fresh empty and occupied authoritative snapshots are accepted',()=>{
 assert.equal(validateSnapshot(valid,now).connectedCount,0);
 assert.equal(validateSnapshot({...valid,playerCount:2,connectedCount:3},now).playerCount,2);
});
for(const [label,value]of Object.entries({missing:null,stale:{...valid,updatedAt:new Date(now-10001).toISOString()},future:{...valid,updatedAt:new Date(now+2001).toISOString()},invalidDate:{...valid,updatedAt:'invalid'},missingPid:{...valid,serverPid:null},negativePid:{...valid,serverPid:-1},unknownPlayers:{...valid,playerCount:null},fractionalPlayers:{...valid,playerCount:0.5},negativeConnections:{...valid,connectedCount:-1},impossibleCounts:{...valid,playerCount:2,connectedCount:1}})){
 test(`reject ${label}; no shutdown authorization`,()=>assert.throws(()=>validateSnapshot(value,now)));
}
for(const code of ['EACCES','EPERM','EBUSY','ENOENT']){
 test(`live snapshot retries transient ${code}, validates fresh result`,async()=>{
  let reads=0,waits=0;
  const value=await snapshot('unused',{readFile:async()=>{
   if(++reads===1)throw Object.assign(Error('transient replacement'),{code});
   return JSON.stringify({...valid,updatedAt:new Date().toISOString()});
  },wait:async milliseconds=>{assert.equal(milliseconds,50);waits++;}});
  assert.equal(reads,2);assert.equal(waits,1);assert.equal(value.serverPid,123);
 });
}
test('persistent live sharing denial fails after exactly 20 reads',async()=>{
 let reads=0,waits=0;
 await assert.rejects(snapshot('unused',{readFile:async()=>{reads++;throw Object.assign(Error('denied'),{code:'EACCES'});},wait:async()=>{waits++;}}),/denied/);
 assert.equal(reads,20);assert.equal(waits,19);
});
for(const [label,text]of [['malformed','{'],['stale',JSON.stringify(valid)],['unknown counts',JSON.stringify({...valid,updatedAt:new Date().toISOString(),connectedCount:null})]]){
 test(`${label} snapshot never becomes empty-server authorization`,async()=>{
  let reads=0;
  await assert.rejects(snapshot('unused',{readFile:async()=>{reads++;return text;},wait:async()=>assert.fail('Invalid contents must not be retried')}));
  assert.equal(reads,1);
 });
}
test('operator initial live snapshot uses validated admission reader',async()=>{
 const source=await fs.readFile(new URL('./Deploy-LocalRelease.ps1',import.meta.url),'utf8');
 assert.match(source,/\$initialBridge=Invoke-LocalDeploymentBridge[^\n]+\$BotConfig 'status'/);
 assert.doesNotMatch(source,/Get-Content[^\n]+APFDiscordBridge\/status\.json/);
});
