// Independent, fixed-path readback transport. No application or command intent.
import fs from 'node:fs/promises';
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify,isDeepStrictEqual} from 'node:util';
import {pathToFileURL} from 'node:url';
import {readJson,writeJson,noLinks,statusZip,publicDiagnostics} from './public-status.mjs';
import {verifyReadbackEnvelope,OBSERVATION_BRANCH,OBSERVATION_PATH} from './signed-readback.mjs';
const execute=promisify(execFile),repository='esotericmartyristaken-bit/AETHERFRONT';
const delay=milliseconds=>new Promise(resolve=>setTimeout(resolve,milliseconds));
const ref=`heads/${OBSERVATION_BRANCH}`;
export async function publishObservation(settings,options={}){
  const token=options.token??process.env.GH_TOKEN;
  if(typeof token!=='string'||token.length<20)throw new Error('OBSERVATION_CREDENTIAL_UNAVAILABLE');
  const fetcher=options.fetch??fetch;
  const api=async(endpoint,method='GET',body)=>{
    const response=await fetcher(`https://api.github.com/repos/${repository}/${endpoint}`,{method,redirect:'error',signal:AbortSignal.timeout(15000),headers:{'User-Agent':'Aetherfront-Control','Accept':'application/vnd.github+json',Authorization:`Bearer ${token}`,...(body?{'Content-Type':'application/json'}:{})},...(body?{body:JSON.stringify(body)}:{})});
    if(response.status===404){await response.body?.cancel();return null;}
    if(!response.ok){await response.body?.cancel();throw new Error('OBSERVATION_GITHUB_UNAVAILABLE');}
    let size=0;const chunks=[];
    for await(const bytes of response.body){size+=bytes.length;if(size>262144)throw new Error('OBSERVATION_RESPONSE_OVERSIZED');chunks.push(Buffer.from(bytes));}
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  };
  let branch=await api(`git/ref/${ref}`);
  if(!branch){
    const seed=await api('git/ref/heads/deploy');
    if(seed?.object?.type!=='commit'||!/^[a-f0-9]{40}$/.test(seed.object.sha))throw new Error('OBSERVATION_SEED_INVALID');
    try{await api('git/refs','POST',{ref:`refs/${ref}`,sha:seed.object.sha});}catch{/* Reconcile a lost acknowledgement below. */}
    branch=await api(`git/ref/${ref}`);
  }
  if(branch?.ref!==`refs/${ref}`||branch.object?.type!=='commit'||!/^[a-f0-9]{40}$/.test(branch.object.sha))throw new Error('OBSERVATION_BRANCH_INVALID');
  const existing=await api(`contents/${OBSERVATION_PATH}?ref=${branch.object.sha}`);
  if(existing&&(existing.path!==OBSERVATION_PATH||existing.type!=='file'||!/^[a-f0-9]{40}$/.test(existing.sha)))throw new Error('OBSERVATION_PATH_INVALID');
  const payload=await readJson(path.join(settings.root,'host-observation.json'));
  // These serializers validate and project bounded public fields, never raw logs.
  statusZip(payload.status);
  const diagnostics=publicDiagnostics(payload.diagnostics);
  // Reject fields the shared public projection would discard, at every depth.
  // Object key order is irrelevant; no unprojected source object is signed.
  if(!isDeepStrictEqual(diagnostics,payload.diagnostics))throw new Error('OBSERVATION_DIAGNOSTICS_INVALID');
  if(payload.status.observedAt!==diagnostics.observedAt||payload.status.expiresAt!==diagnostics.expiresAt||Date.parse(payload.status.expiresAt)<=Date.now()+5000)throw new Error('OBSERVATION_NOT_FRESH');
  await writeJson(path.join(settings.root,'observation-payload.json'),{schemaVersion:1,status:payload.status,diagnostics});
  const result=await (options.execute??execute)(settings.powershell,['-NoProfile','-NonInteractive','-File',path.join(settings.root,'bootstrap','Sign-HostObservation.ps1'),'-Settings',path.join(settings.root,'host.json')],{windowsHide:true,cwd:settings.root,timeout:15000,maxBuffer:131072});
  const envelope=Buffer.from(result.stdout.trim());
  const pin=(await fs.readFile(path.join(settings.nativeRoot,'secrets','pin.txt'),'utf8')).trim().replaceAll(':','').toLowerCase();
  verifyReadbackEnvelope(envelope,{pin,resource:'status'});
  verifyReadbackEnvelope(envelope,{pin,resource:'diagnostics'});
  const body={branch:OBSERVATION_BRANCH,message:'Refresh signed host observation',content:Buffer.concat([envelope,Buffer.from('\n')]).toString('base64'),...(existing?{sha:existing.sha}:{})};
  const written=await api(`contents/${OBSERVATION_PATH}`,'PUT',body);
  if(written?.content?.path!==OBSERVATION_PATH||!/^[a-f0-9]{40}$/.test(written?.commit?.sha??''))throw new Error('OBSERVATION_WRITE_UNCONFIRMED');
  return {published:true,observedAt:payload.status.observedAt,expiresAt:payload.status.expiresAt,commit:written.commit.sha};
}
export async function observationLoop(settings){
  let stopping=false;
  const owner=process.ppid;
  process.on('disconnect',()=>{stopping=true;});process.on('SIGTERM',()=>{stopping=true;});
  while(!stopping){
    try{process.kill(owner,0);}catch{break;}
    try{
      const result=await publishObservation(settings);
      await writeJson(path.join(settings.root,'native-cache','observation-transport.json'),{schemaVersion:1,...result});
    }catch(error){
      await writeJson(path.join(settings.root,'native-cache','observation-transport.json'),{schemaVersion:1,published:false,observedAt:new Date().toISOString(),reason:/^OBSERVATION_[A-Z_]+$/.test(error.message)?error.message:'OBSERVATION_FAILED'}).catch(()=>{});
    }
    for(let index=0;index<10&&!stopping;index++)await delay(1000);
  }
}
if(process.argv[1]&&import.meta.url===pathToFileURL(path.resolve(process.argv[1])).href){
  const settingsPath=process.argv[process.argv.indexOf('--settings')+1];await noLinks(settingsPath);
  await observationLoop(await readJson(settingsPath));
}
