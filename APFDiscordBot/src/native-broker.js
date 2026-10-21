import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { atomic, noLinks, writeJson, readJson, fileHash } from '../../ControlPlane/public-status.mjs';

export async function publicationRequest(archive, notes, options={}) {
  const match=path.basename(archive).match(/kesas-rc[1-9][0-9]{0,14}/);
  if(!match || typeof notes!=='string' || notes.length>500 || /[\p{Cc}\p{Cf}]/u.test(notes)) throw new Error('Invalid native publication request');
  const sourceCommit=options.sourceCommit||null;
  if(sourceCommit!==null && !/^[a-f0-9]{40}$/.test(sourceCommit))throw new Error('Invalid native publication provenance');
  await noLinks(archive);const sizeBytes=(await fs.stat(archive)).size;
  if(sizeBytes<1||sizeBytes>512*1024*1024)throw new Error('Invalid native publication size');
  const sha256=await fileHash(archive);
  const body={schemaVersion:1,transactionVersion:1,version:match[0],sha256,sizeBytes,notes,sourceCommit};
  const id=crypto.createHash('sha256').update(JSON.stringify(body)).digest('hex');
  return {...body,id};
}
export async function observePublication(config,archive,notes,options={}) {
  const request=await publicationRequest(archive,notes,options);
  const result=await readJson(path.join(config.__controlRoot,'native-results',`${request.id}.json`),null);
  const pending=await readJson(path.join(config.__controlRoot,'native-requests',`${request.id}.json`),null);
  if(result && (result.id!==request.id||result.version!==request.version||result.sha256!==request.sha256||result.schemaVersion!==1||result.transactionVersion!==1))throw new Error('Native recovery identity mismatch');
  return {request,result,requested:pending!==null};
}
export async function publishThroughBroker(config, archive, notes, options={}) {
  const request=await publicationRequest(archive,notes,options);
  const {id,sizeBytes,sha256}=request;const root=config.__controlRoot;
  const directory=path.join(root,'native-requests');await noLinks(directory);await fs.mkdir(directory,{recursive:true});
  const target=path.join(directory,`${id}.zip`);
  const previous=await readJson(path.join(root,'native-results',`${id}.json`),null);
  if(!previous) {
    // The publisher verifies the private staged copy again against authenticated
    // GitHub intent. This local request never supplies executable code or paths.
    const bytes=await fs.readFile(archive);
    if(bytes.length!==sizeBytes||crypto.createHash('sha256').update(bytes).digest('hex')!==sha256)throw new Error('Native archive changed during handoff');
    await atomic(target,bytes);await writeJson(path.join(directory,`${id}.json`),request);
  }
  const deadline=Date.now()+30*60*1000;
  while(Date.now()<deadline) {
    const result=await readJson(path.join(root,'native-results',`${id}.json`),null);
    if(result?.status==='healthy') {
      if(result.version!==request.version||result.sha256!==sha256)throw new Error('Native completion identity mismatch');
      return {ok:true,rollbackRoot:path.join(config.lmp.deploymentRoot,'Backups','updates')};
    }
    if(result?.status==='blocked')throw new Error('Native publisher blocked; preserve deployment journals for review');
    await new Promise(resolve=>setTimeout(resolve,500));
  }
  throw new Error('Native publication outcome pending; no helper was terminated');
}
