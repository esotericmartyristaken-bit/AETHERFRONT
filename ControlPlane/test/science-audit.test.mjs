import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {auditState,parseState,decimal,format,projectScienceAudit,readScienceAudit} from '../science-audit.mjs';
import {noLinks,diagnosticsZip} from '../public-status.mjs';
const p=(id,earned,shared,sources)=>({AccountId:id,Science:'1000',TradableScience:'10',EarnedScience:earned,SharedScienceCredited:shared,ScienceSources:sources.map((n,i)=>({Id:'subject-'+i,CumulativeScience:n,CreditedScience:n}))});
function fixture(){return {Revision:'37',Accounts:['a','b','c','new'].map(AccountId=>({AccountId,DisplayName:'private-name-'+AccountId})),PersonalProgression:{Version:'1',StartingScience:'5',TotalEarnedScience:'805',Players:[p('a','705','50',['100','500','5','100']),p('b','100','300',['100']),p('c','0','100',[])]},privateKey:'never export',Vessels:[{secret:'never export'}]};}
test('native-shaped ledger distinguishes outstanding lazy credits from lost credits',()=>{
  const a=auditState(fixture());assert.equal(a.status,'consistent');assert.equal(a.totalEarnedScience,'805');assert.equal(a.newPlayerSharedBaseline,'402.5');assert.equal(a.newPlayerTotalBaseline,'407.5');
  assert.equal(a.sharedCreditedTotal,'450');assert.equal(a.sharedEntitlementTotal,'805');assert.equal(a.pendingSharedTotal,'355');assert.equal(a.pendingLedgers,2);assert.equal(a.fullySettledLedgers,1);assert.equal(a.accountsAwaitingLedger,1);
  const text=JSON.stringify(a);for(const secret of ['private-name','privateKey','never export','subject-','ScienceSources','Vessels'])assert.equal(text.includes(secret),false);
});
test('actual 100/50 distribution and late/new player baseline are checked separately from original earner',()=>{
  const s={Revision:'1',Accounts:['a','b','new'].map(AccountId=>({AccountId})),PersonalProgression:{Version:'1',StartingScience:'0',TotalEarnedScience:'100',Players:[p('a','100','0',['100']),p('b','0','50',[])]}};
  let a=auditState(s);assert.equal(a.status,'consistent');assert.equal(a.sharedEntitlementTotal,'50');assert.equal(a.newPlayerSharedBaseline,'50');assert.equal(a.pendingSharedTotal,'0');
  s.PersonalProgression.Players.push(p('new','0','50',[]));a=auditState(s);assert.equal(a.status,'consistent');assert.equal(a.sharedCreditedTotal,'100');
});
test('overcredit, duplicate awards, global counter mismatch and invalid wallets are detected',()=>{
  for(const [change,expected] of [[s=>s.PersonalProgression.Players[1].SharedScienceCredited='400','shared-overcredit'],[s=>s.PersonalProgression.TotalEarnedScience='806','global-earned-mismatch'],[s=>s.PersonalProgression.Players[0].ScienceSources.push(s.PersonalProgression.Players[0].ScienceSources[0]),'duplicate-source'],[s=>s.PersonalProgression.Players[1].TradableScience='1001','invalid-balance'],[s=>s.PersonalProgression.Players[0].ScienceSources[0].CreditedScience='99','source-credit-mismatch']]){
    const s=fixture();change(s);const a=auditState(s);assert.equal(a.status,'inconsistent');assert.ok(a.errors.includes(expected));
  }
});
test('new sessions with no earned awards cannot be described as real award verification',()=>{
  const a=auditState({Revision:'0',Accounts:[],PersonalProgression:{Version:'1',TotalEarnedScience:'0',StartingScience:'0',Players:[]}});
  assert.equal(a.status,'consistent');assert.equal(a.activity,'no-earned-awards');assert.equal(a.earners,0);
});
test('JSON numeric lexemes preserve large revisions and decimal precision without doubles',()=>{
  const s=parseState('{"Revision":9007199254740993,"Accounts":[],"PersonalProgression":{"Version":1,"TotalEarnedScience":0,"StartingScience":0.1234567890123456789012345678,"Players":[]}}');
  const a=auditState(s);assert.equal(a.revision,'9007199254740993');assert.equal(a.startingScience,'0.1234567890123456789012345678');
  assert.equal(format(decimal('8.5830688E-7')),'0.00000085830688');assert.equal(format(decimal('1000000000000')),'1000000000000');assert.throws(()=>decimal('1e-29'));assert.throws(()=>decimal('1e999'));
});
test('case variations are accepted but ambiguous names and malformed schemas fail closed',()=>{
  const s=fixture();s.personalProgression=s.PersonalProgression;delete s.PersonalProgression;assert.equal(auditState(s).status,'consistent');
  s.PERSONALPROGRESSION=s.personalProgression;assert.throws(()=>auditState(s));
  for(const patch of [{Version:'2'},{Players:null},{TotalEarnedScience:'NaN'},{StartingScience:'-1'}]){const x=fixture();Object.assign(x.PersonalProgression,patch);assert.throws(()=>auditState(x));}
});
test('public projection drops arbitrary raw state and rejects invented success/errors',()=>{
  const a=auditState(fixture());assert.deepEqual(projectScienceAudit({...a,rawState:fixture(),privateKey:'secret'}),a);
  assert.throws(()=>projectScienceAudit({...a,errors:['private exception']}));assert.throws(()=>projectScienceAudit({...a,status:'consistent',errors:['shared-overcredit']}));
  assert.deepEqual(projectScienceAudit({schemaVersion:1,status:'unavailable',reason:'state-missing',observedAt:new Date().toISOString(),private:'secret'}).reason,'state-missing');
});
test('read-only disk snapshot is bounded, cached and leaves exact state bytes unchanged',async()=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'apf-science-audit-')),file=path.join(root,'Server','Aetherfront','state.json');await fs.mkdir(path.dirname(file),{recursive:true});
  const raw=Buffer.from(JSON.stringify(fixture()));await fs.writeFile(file,raw);
  const now=Date.now(),settings={nativeRoot:root};let a=await readScienceAudit(settings,{now,noLinks});assert.equal(a.status,'consistent');assert.deepEqual(await fs.readFile(file),raw);
  await fs.writeFile(file,'invalid');assert.equal((await readScienceAudit(settings,{now:now+1000,noLinks})).stateSha256,a.stateSha256);
  a=await readScienceAudit(settings,{now:now+61000,noLinks});assert.equal(a.status,'unavailable');assert.equal(a.reason,'state-invalid');
  assert.equal((await readScienceAudit({nativeRoot:root+'-missing'},{now,noLinks})).reason,'state-missing');
});
