// GitHub/status transport doubles only. Artifact bytes, controller validation,
// versioned installation and native application publication remain real.
import { REPOSITORY, CAPABILITIES } from '../contract.mjs';
import { statusZip, digest, readJson, fileHash } from '../public-status.mjs';
import path from 'node:path';
import fs from 'node:fs/promises';

export function repositoryFixture() {
  const state={descriptor:null,head:'1'.repeat(40),fileSha:null,release:null,assets:[],bytes:null,writes:0,api:0};
  const snapshots=new Map();
  const remember=()=>snapshots.set(state.head,{descriptor:state.descriptor,fileSha:state.fileSha});remember();
  const gh=async(args,input)=>{
    if(args[0]==='release'&&args[1]==='upload') {
      state.bytes=await fs.readFile(args[3]);
      state.assets=[{name:path.basename(args[3]),size:state.bytes.length,state:'uploaded',digest:`sha256:${digest(state.bytes)}`,browser_download_url:`https://github.com/${REPOSITORY}/releases/download/${state.release.tag_name}/${path.basename(args[3])}`}];return '';
    }
    if(args[0]!=='api')throw new Error('Unexpected fixture GitHub call');
    state.api++;const resource=args.find(value=>value.startsWith('repos/'));const endpoint=resource.slice(`repos/${REPOSITORY}/`.length);
    const method=args[args.indexOf('--method')+1];const body=input?JSON.parse(input):null;
    if(!endpoint)return JSON.stringify({full_name:REPOSITORY,private:false,archived:false,disabled:false});
    if(endpoint==='git/ref/heads/deploy')return JSON.stringify({ref:'refs/heads/deploy',object:{sha:state.head,type:'commit'}});
    if(endpoint.startsWith('git/trees/')) {
      if(endpoint.startsWith(`git/trees/${'d'.repeat(40)}`))return JSON.stringify({truncated:false,tree:state.descriptor?[{path:'control-plane.json',type:'blob',mode:'100644',sha:state.fileSha}]:[]});
      if(!snapshots.has(endpoint.slice(10).split('?')[0]))throw new Error('Unknown fixture commit');
      return JSON.stringify({truncated:false,tree:[{path:'deployment',type:'tree',sha:'d'.repeat(40)}]});
    }
    if(endpoint.startsWith('contents/deployment/control-plane.json?ref=')) {
      const saved=snapshots.get(endpoint.split('?ref=')[1]);return JSON.stringify({sha:saved.fileSha,encoding:'base64',content:Buffer.from(JSON.stringify(saved.descriptor)).toString('base64')});
    }
    if(endpoint.startsWith('releases?'))return JSON.stringify(state.release?[state.release]:[]);
    if(endpoint==='releases'&&method==='POST'){state.release={id:9,tag_name:body.tag_name,draft:false,prerelease:true};state.assets=[];return JSON.stringify(state.release);}
    if(endpoint==='releases/9/assets?per_page=100')return JSON.stringify(state.assets);
    if(endpoint==='contents/deployment/control-plane.json'&&method==='PUT') {
      if((body.sha||null)!==state.fileSha)throw new Error('CAS conflict');
      state.descriptor=JSON.parse(Buffer.from(body.content,'base64'));state.head=String(2+state.writes).repeat(40);state.fileSha=String.fromCharCode(97+state.writes).repeat(40);state.writes++;remember();return '{}';
    }
    throw new Error('Unexpected fixture GitHub endpoint');
  };
  const fetcher=async(url)=>{
    if(url.includes('/git/ref/heads/deploy'))return Response.json({ref:'refs/heads/deploy',object:{sha:state.head,type:'commit'}});
    if(url.includes('/releases/download/'))return new Response(state.bytes);
    return new Response(JSON.stringify(state.descriptor));
  };
  return {state,gh,fetcher,artifactDigest:async()=>digest(state.bytes)};
}
export async function reportFixture(f) {
  const pointer=await f.engine.active();
  const hashes=Object.fromEntries(['watcher','nativeRunner','publisher','verifier','packService','serverRuntime','serverPlugins','clientBootstrap','clientUpdater','clientHelper','catalog','publicMedia','dependencies','updateTool','certificateTool','bootstrap'].map(name=>[name,null]));
  hashes.publisher=await fileHash(path.join(f.root,'releases',pointer.directory,'KESAS-VPS-Server','Publish-Release.ps1'));
  hashes.verifier=await fileHash(path.join(f.root,'releases',pointer.directory,'APFDiscordBot','scripts','Verify-KesasRelease.ps1'));
  return {schemaVersion:1,contractVersion:1,bootstrapVersion:1,observedAt:new Date().toISOString(),expiresAt:new Date(Date.now()+150000).toISOString(),capabilities:CAPABILITIES.filter(name=>pointer.sequence>1||name!=='media-payload-v1'),control:{version:pointer.version,sequence:pointer.sequence,sha256:pointer.bundleSha256,status:'healthy',phase:'healthy',pendingVersion:null,lastSuccess:pointer.version,rollback:null,failure:null},application:{clientVersion:'kesas-rc30',packVersion:'kesas-rc30',status:'idle',phase:null,pendingVersion:null,health:'healthy',lastSuccess:'kesas-rc30',failure:null},components:hashes,runtime:f.settings.runtime};
}
