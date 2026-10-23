import fs from 'node:fs/promises';
import path from 'node:path';
import {readJson,noLinks} from './public-status.mjs';
import {validateControlAttempt} from './contract.mjs';
const phases={accepted:'accepted',staging:'staging','waiting-existing-guard':'waiting-existing-guard',activating:'applying','health-check':'applying','rolling-back':'applying',healthy:'healthy','rolled-back':'rolled-back',blocked:'blocked'};
export function projectControlAttempt(transaction){
  if(!transaction)return null;
  try{return validateControlAttempt({version:transaction.descriptor.version,sha256:transaction.descriptor.sha256,transactionId:transaction.id,phase:phases[transaction.phase],stage:transaction.stageCode??transaction.phase,failure:transaction.failure??null,updatedAt:transaction.updatedAt??transaction.acceptedAt});}
  catch{return null;}
}
export async function controlHistory(root,transaction){
  const directory=path.join(root,'transactions');await noLinks(directory);
  const names=(await fs.readdir(directory).catch(error=>{if(error.code==='ENOENT')return [];throw error;})).filter(name=>/^[a-f0-9]{64}\.json$/.test(name));
  if(names.length>10000)throw new Error('CONTROL_HISTORY_OVERSIZED');
  const attempted=projectControlAttempt(transaction);
  let lastFailure=attempted&&['blocked','rolled-back'].includes(attempted.phase)?attempted:null;
  for(const name of names){
    const file=path.join(directory,name);await noLinks(file);const stat=await fs.stat(file);
    if(!stat.isFile()||stat.size>65536)throw new Error('CONTROL_HISTORY_INVALID');
    const value=projectControlAttempt(await readJson(file));
    if(value&&['blocked','rolled-back'].includes(value.phase)&&(!lastFailure||Date.parse(value.updatedAt)>Date.parse(lastFailure.updatedAt)))lastFailure=value;
  }
  return {attempted,lastFailure,recoveryAction:attempted&&['blocked','rolled-back'].includes(attempted.phase)?'publish-reviewed-newer-control':null};
}
