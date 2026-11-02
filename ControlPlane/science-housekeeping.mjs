// Read-only launch provenance. No player names, vessel contents or account data.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
const GUID=/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const HASH=/^[a-f0-9]{64}$/;
const need=(v)=>{if(!v)throw new Error('Invalid science housekeeping evidence');};
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const cache=new Map();
export function classifyVessel(row,{preparedAt,completedAt,createdAfter,anyDocking}){
  need(GUID.test(row.id)&&Number.isFinite(row.birthtimeMs)&&Number.isFinite(row.mtimeMs));
  if(createdAfter.has(row.id)||row.birthtimeMs>=Date.parse(completedAt))return 'science';
  if(row.birthtimeMs>0&&row.birthtimeMs<Date.parse(preparedAt)-1000&&(!anyDocking||row.mtimeMs<Date.parse(preparedAt)))return 'sandbox';
  return 'uncertain';
}
async function bounded(file,max,noLinks){await noLinks(file);const h=await fs.open(file,'r');try{const s=await h.stat();need(s.isFile()&&s.nlink===1&&s.size<=max);const b=await h.readFile();need(b.length<=max);return b;}finally{await h.close();}}
export async function collectHousekeeping(settings,{noLinks,now=Date.now()}={}){
  need(typeof noLinks==='function');
  const base=path.join(settings.root,'native-cache','server-mode');
  const active=JSON.parse(await bounded(path.join(base,'active.json'),16384,noLinks));
  need(active.phase==='healthy'&&active.mode==='Science'&&/^[a-f0-9]{32}$/.test(active.id));
  const job=path.join(base,active.id);
  const receipt=JSON.parse(await bounded(path.join(job,'receipt.json'),16384,noLinks));
  need(receipt.id===active.id&&receipt.phase==='healthy'&&receipt.mode==='Science'&&Number.isFinite(Date.parse(receipt.updatedAt)));
  const backup=path.join(job,'GeneralSettings.before.xml');await noLinks(backup);
  const preparation=await fs.stat(backup);need(preparation.isFile()&&preparation.nlink===1&&preparation.birthtimeMs>0&&preparation.birthtimeMs<Date.parse(receipt.updatedAt));
  const general=(await bounded(path.join(settings.nativeRoot,'Server','Config','GeneralSettings.xml'),1048576,noLinks)).toString('utf8').replace(/^\uFEFF/,'');
  need((general.match(/<GameMode>\s*Science\s*<\/GameMode>/g)||[]).length===1);
  const matches=[...general.matchAll(/<Cheats>\s*(true|false)\s*<\/Cheats>/g)];need(matches.length===1);
  const log=await bounded(path.join(settings.nativeRoot,'Server','logs','CraftCreationAndRemoval.txt'),8*1024*1024,noLinks);
  const logText=log.toString('utf8'),start=/# Server started at (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) UTC/.exec(logText);
  need(start&&Date.parse(start[1].replace(' ','T')+'Z')>=preparation.birthtimeMs-2000);
  const createdAfter=new Set();let dockingEvents=0;
  for(const line of logText.split(/\r?\n/)){
    const m=/^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\] Vessel ([a-f0-9-]{36}) \(.*\) (created|removed) by player /.exec(line);
    if(!m)continue;need(GUID.test(m[2]));
    const at=Date.parse(m[1].replace(' ','T')+'Z');need(Number.isFinite(at));
    if(at>=preparation.birthtimeMs-1000&&m[3]==='created')createdAfter.add(m[2]);
    if(/\(Coupled\/Docked\)\s*$/.test(line))dockingEvents++;
  }
  const dir=path.join(settings.nativeRoot,'Server','Universe','Vessels');await noLinks(dir);
  const names=await fs.readdir(dir);need(names.length<=10000);
  const rows=[];
  for(const name of names.sort()){
    need(/^[a-f0-9-]{36}\.txt$/.test(name)&&GUID.test(name.slice(0,-4)));
    const file=path.join(dir,name);await noLinks(file);const s=await fs.stat(file);need(s.isFile()&&s.nlink===1&&s.size<=16*1024*1024);
    const row={id:name.slice(0,-4),birthtimeMs:s.birthtimeMs,mtimeMs:s.mtimeMs,sizeBytes:s.size};
    row.classification=classifyVessel(row,{preparedAt:preparation.birthtime.toISOString(),completedAt:receipt.updatedAt,createdAfter,anyDocking:dockingEvents>0});
    rows.push(row);
  }
  need(rows.length<=512);
  const evidence={modeTransactionId:active.id,preparedAt:preparation.birthtime.toISOString(),completedAt:receipt.updatedAt,logSha256:hash(log),dockingEvents,rows};
  return {schemaVersion:1,status:'available',observedAt:new Date(now).toISOString(),cheatsEnabled:matches[0][1]==='true',...evidence,evidenceSha256:hash(Buffer.from(JSON.stringify(evidence)))};
}
export function projectHousekeeping(v){
  need(v?.schemaVersion===1&&['available','unavailable'].includes(v.status)&&Number.isFinite(Date.parse(v.observedAt)));
  if(v.status==='unavailable')return {schemaVersion:1,status:'unavailable',observedAt:v.observedAt};
  need(typeof v.cheatsEnabled==='boolean'&&/^[a-f0-9]{32}$/.test(v.modeTransactionId)&&HASH.test(v.evidenceSha256)&&HASH.test(v.logSha256)&&Number.isFinite(Date.parse(v.preparedAt))&&Number.isFinite(Date.parse(v.completedAt))&&Number.isSafeInteger(v.dockingEvents)&&v.dockingEvents>=0&&Array.isArray(v.rows)&&v.rows.length<=512);
  const rows=v.rows.map(r=>{need(GUID.test(r.id)&&['sandbox','science','uncertain'].includes(r.classification)&&[r.birthtimeMs,r.mtimeMs,r.sizeBytes].every(n=>Number.isFinite(n)&&n>=0));return {id:r.id,birthtimeMs:r.birthtimeMs,mtimeMs:r.mtimeMs,sizeBytes:r.sizeBytes,classification:r.classification};});
  return {schemaVersion:1,status:'available',observedAt:v.observedAt,cheatsEnabled:v.cheatsEnabled,modeTransactionId:v.modeTransactionId,preparedAt:v.preparedAt,completedAt:v.completedAt,logSha256:v.logSha256,dockingEvents:v.dockingEvents,rows,evidenceSha256:v.evidenceSha256};
}
export async function readHousekeeping(settings,{noLinks,now=Date.now()}={}){
  const key=path.resolve(settings.root),old=cache.get(key);if(old&&now<old.until)return old.value;
  let value;try{value=await collectHousekeeping(settings,{noLinks,now});}catch{value={schemaVersion:1,status:'unavailable',observedAt:new Date(now).toISOString()};}
  cache.set(key,{until:now+60000,value});return value;
}
