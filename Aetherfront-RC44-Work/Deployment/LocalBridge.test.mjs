import test from 'node:test';
import assert from 'node:assert/strict';
import {validateSnapshot} from './LocalBridge.mjs';
const now=Date.parse('2026-11-04T00:00:00Z');
const valid={updatedAt:new Date(now-1000).toISOString(),serverPid:123,playerCount:0,connectedCount:0};
test('fresh empty and occupied authoritative snapshots are accepted',()=>{
 assert.equal(validateSnapshot(valid,now).connectedCount,0);
 assert.equal(validateSnapshot({...valid,playerCount:2,connectedCount:3},now).playerCount,2);
});
for(const [label,value]of Object.entries({missing:null,stale:{...valid,updatedAt:new Date(now-10001).toISOString()},future:{...valid,updatedAt:new Date(now+2001).toISOString()},invalidDate:{...valid,updatedAt:'invalid'},missingPid:{...valid,serverPid:null},negativePid:{...valid,serverPid:-1},unknownPlayers:{...valid,playerCount:null},fractionalPlayers:{...valid,playerCount:0.5},negativeConnections:{...valid,connectedCount:-1},impossibleCounts:{...valid,playerCount:2,connectedCount:1}})){
 test(`reject ${label}; no shutdown authorization`,()=>assert.throws(()=>validateSnapshot(value,now)));
}
