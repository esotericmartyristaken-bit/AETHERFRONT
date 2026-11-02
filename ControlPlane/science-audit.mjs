// Read-only science accounting audit. Never exports names, account/source IDs,
// individual wallets, vessels, credentials, raw state, or save contents.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
const UNIT=10n**28n,MAX=1000000000000n*UNIT;
const COUNTS=['registeredAccounts','personalLedgers','earners','sources','subjectSources','rewardReceipts','fullySettledLedgers','pendingLedgers','accountsAwaitingLedger','overcreditedLedgers','duplicateSources','sourceMismatches','invalidBalances'];
const DECIMALS=['totalEarnedScience','sourceCreditTotal','earnedCounterTotal','startingScience','newPlayerSharedBaseline','newPlayerTotalBaseline','sharedCreditedTotal','sharedEntitlementTotal','pendingSharedTotal'];
const CHECKS=['globalEarnedMatchesPlayers','sourceCreditsMatchEarned','sharedCreditsWithinHalfEntitlement','balancesValid'];
const ERRORS=['duplicate-player','unknown-account','duplicate-source','source-credit-mismatch','global-earned-mismatch','shared-overcredit','invalid-balance','invalid-source'];
const cache=new Map();
function need(ok){if(!ok)throw new Error('Invalid science audit input');}
export function decimal(value){
  need(typeof value==='string'&&value.length<=96);
  const m=/^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d{1,3}))?$/.exec(value);need(m);
  const digits=m[2]+(m[3]??''),scale=(m[3]?.length??0)-Number(m[4]??0);need(scale<=28&&scale>=-28&&digits.length<=64);
  return (m[1]?-1n:1n)*BigInt(digits)*10n**BigInt(28-scale);
}
export function format(value){
  const sign=value<0n?'-':'';if(value<0n)value=-value;
  const fraction=(value%UNIT).toString().padStart(28,'0').replace(/0+$/,'');
  return sign+(value/UNIT).toString()+(fraction?'.'+fraction:'');
}
function half(value){const q=value/2n,r=value%2n;return q+(r!==0n&&q%2n!==0n?1n:0n);}
export function parseState(text){
  // Node 24 exposes each JSON numeric lexeme. Preserve .NET decimal values
  // exactly rather than rounding a wallet through a JavaScript double.
  const result=JSON.parse(text.replace(/^\uFEFF/,''),(key,value,context)=>{
    if(typeof value==='number'){need(typeof context?.source==='string');return context.source;}
    return value;
  });return result;
}
function field(object,key){need(object&&typeof object==='object'&&!Array.isArray(object));const names=Object.keys(object).filter(k=>k.toLowerCase()===key.toLowerCase());need(names.length===1);return object[names[0]];}
function array(object,key,max){const a=field(object,key);need(Array.isArray(a)&&a.length<=max);return a;}
function number(object,key){return decimal(field(object,key));}
export function auditState(state,{now=Date.now(),stateSha256='0'.repeat(64)}={}){
  const ledger=field(state,'PersonalProgression');need(field(ledger,'Version')==='1');
  const players=array(ledger,'Players',10000),accounts=array(state,'Accounts',10000);
  const total=number(ledger,'TotalEarnedScience'),starting=number(ledger,'StartingScience');need(total>=0n&&total<=MAX&&starting>=0n&&starting<=MAX);
  const accountIds=new Set(accounts.map(a=>field(a,'AccountId')));need(accountIds.size===accounts.length&&[...accountIds].every(s=>typeof s==='string'&&s.length<=256));
  const seen=new Set(),errors=new Set(),counts=Object.fromEntries(COUNTS.map(k=>[k,0]));counts.registeredAccounts=accounts.length;counts.personalLedgers=players.length;
  let earnedSum=0n,sourceTotal=0n,creditedSum=0n,entitlementSum=0n,pendingSum=0n;
  for(const player of players){
    const id=field(player,'AccountId');need(typeof id==='string'&&id.length<=256);
    if(seen.has(id))errors.add('duplicate-player');seen.add(id);if(!accountIds.has(id))errors.add('unknown-account');
    const earned=number(player,'EarnedScience'),credited=number(player,'SharedScienceCredited'),balance=number(player,'Science'),tradable=number(player,'TradableScience');
    earnedSum+=earned;creditedSum+=credited;if(earned>0n)counts.earners++;
    const entitlement=half(total-earned),pending=entitlement-credited;entitlementSum+=entitlement;pendingSum+=pending;
    if(earned<0n||earned>total||credited<0n||credited>entitlement){counts.overcreditedLedgers++;errors.add('shared-overcredit');}
    if(pending>0n)counts.pendingLedgers++;else if(pending===0n)counts.fullySettledLedgers++;
    if(balance<0n||balance>MAX||tradable<0n||tradable>balance||balance+pending>MAX){counts.invalidBalances++;errors.add('invalid-balance');}
    const sources=array(player,'ScienceSources',100000),sourceIds=new Set();let personalSources=0n;
    counts.sources+=sources.length;need(counts.sources<=1000000);
    for(const source of sources){
      const sourceId=field(source,'Id');need(typeof sourceId==='string'&&sourceId.length<=256);
      if(sourceIds.has(sourceId)){counts.duplicateSources++;errors.add('duplicate-source');}sourceIds.add(sourceId);
      if(sourceId.startsWith('award:'))counts.rewardReceipts++;else counts.subjectSources++;
      const value=number(source,'CreditedScience'),cumulative=number(source,'CumulativeScience');
      if(value<0n||cumulative<0n)errors.add('invalid-source');personalSources+=value;
    }
    sourceTotal+=personalSources;if(personalSources!==earned){counts.sourceMismatches++;errors.add('source-credit-mismatch');}
  }
  counts.accountsAwaitingLedger=[...accountIds].filter(id=>!seen.has(id)).length;
  if(earnedSum!==total)errors.add('global-earned-mismatch');
  const revision=field(state,'Revision');need(typeof revision==='string'&&/^\d{1,24}$/.test(revision));
  return projectScienceAudit({schemaVersion:1,status:errors.size?'inconsistent':'consistent',reason:null,observedAt:new Date(now).toISOString(),
    revision,stateSha256,activity:total>0n?'earned-awards-recorded':'no-earned-awards',...counts,
    totalEarnedScience:format(total),sourceCreditTotal:format(sourceTotal),earnedCounterTotal:format(earnedSum),startingScience:format(starting),
    newPlayerSharedBaseline:format(half(total)),newPlayerTotalBaseline:format(half(total)+starting),sharedCreditedTotal:format(creditedSum),sharedEntitlementTotal:format(entitlementSum),pendingSharedTotal:format(pendingSum),
    globalEarnedMatchesPlayers:earnedSum===total,sourceCreditsMatchEarned:counts.sourceMismatches===0,
    sharedCreditsWithinHalfEntitlement:counts.overcreditedLedgers===0,balancesValid:counts.invalidBalances===0,errors:[...errors].sort()});
}
export function projectScienceAudit(value){
  need(value?.schemaVersion===1&&['consistent','inconsistent','unavailable'].includes(value.status)&&typeof value.observedAt==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value.observedAt));
  if(value.status==='unavailable'){
    need(['state-missing','state-unreadable','state-too-large','state-invalid'].includes(value.reason));
    return {schemaVersion:1,status:value.status,reason:value.reason,observedAt:value.observedAt};
  }
  need(value.reason===null&&/^[a-f0-9]{64}$/.test(value.stateSha256)&&/^\d{1,24}$/.test(value.revision)&&['earned-awards-recorded','no-earned-awards'].includes(value.activity));
  const result={schemaVersion:1,status:value.status,reason:null,observedAt:value.observedAt,revision:value.revision,stateSha256:value.stateSha256,activity:value.activity};
  for(const key of COUNTS){need(Number.isSafeInteger(value[key])&&value[key]>=0&&value[key]<=1000000);result[key]=value[key];}
  for(const key of DECIMALS){decimal(value[key]);result[key]=value[key];}
  for(const key of CHECKS){need(typeof value[key]==='boolean');result[key]=value[key];}
  need(Array.isArray(value.errors)&&value.errors.length<=ERRORS.length&&value.errors.every(s=>ERRORS.includes(s)));
  need((value.status==='consistent')===(value.errors.length===0));result.errors=[...value.errors];return result;
}
export async function readScienceAudit(settings,{now=Date.now(),noLinks}={}){
  const key=path.resolve(settings.nativeRoot),old=cache.get(key);if(old&&now<old.next)return old.value;
  const file=path.join(key,'Server','Aetherfront','state.json');let value;
  try{
    need(typeof noLinks==='function');await noLinks(file);
    const handle=await fs.open(file,'r');let raw;
    try{const stat=await handle.stat();need(stat.isFile());if(stat.size>32*1024*1024)throw Object.assign(new Error('size'),{auditCode:'state-too-large'});raw=await handle.readFile();need(raw.length===stat.size);}
    finally{await handle.close();}
    value=auditState(parseState(raw.toString('utf8')),{now,stateSha256:crypto.createHash('sha256').update(raw).digest('hex')});
  }catch(error){
    const reason=error.code==='ENOENT'?'state-missing':error.auditCode??(['EACCES','EPERM','EBUSY'].includes(error.code)?'state-unreadable':'state-invalid');
    value={schemaVersion:1,status:'unavailable',reason,observedAt:new Date(now).toISOString()};
  }
  cache.set(key,{next:now+(value.status==='unavailable'?10000:60000),value});return value;
}
