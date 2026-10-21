// Explicit one-time operator bootstrap. Never included in remote control payloads.
import fs from 'node:fs/promises';
import path from 'node:path';
import { ControlEngine, verifyRelease } from './engine.mjs';
import { readJson, writeJson, digest, noLinks, fileHash } from './public-status.mjs';
import { validateControlDescriptor } from './contract.mjs';

const flags={};
for(let index=2;index<process.argv.length;index+=2){const key=process.argv[index];if(!['--settings','--archive','--commit'].includes(key)||flags[key]||!process.argv[index+1])throw new Error('Invalid bootstrap argument');flags[key]=process.argv[index+1];}
const settings=await readJson(flags['--settings']);const archive=path.resolve(flags['--archive']);await noLinks(archive);
if(!/^[a-f0-9]{40}$/.test(flags['--commit']||''))throw new Error('Invalid source provenance');
const bytes=await fs.readFile(archive);
const descriptor=validateControlDescriptor({schemaVersion:1,transactionVersion:1,version:'control-1',releaseTag:'host-control-1',assetName:'Aetherfront-Control-control-1.zip',sha256:digest(bytes),sizeBytes:bytes.length,minimumBootstrap:1});
const intent={descriptor,commit:flags['--commit'],descriptorSha256:digest(Buffer.from(JSON.stringify(descriptor)))};
const engine=new ControlEngine(settings,{channel:{archive:async()=>bytes}});
const transaction={schemaVersion:1,transactionVersion:1,id:intent.descriptorSha256,descriptor,commit:intent.commit,phase:'accepted',from:null,to:null,failure:null,acceptedAt:new Date().toISOString(),updatedAt:new Date().toISOString()};
const pointer=await engine.stage(transaction);await verifyRelease(settings.root,pointer);
await writeJson(path.join(settings.root,'active.json'),pointer);
await engine.phase(transaction,'healthy',{from:pointer,to:pointer,completedAt:new Date().toISOString()});
await writeJson(path.join(settings.root,'bootstrap','seed.json'),{schemaVersion:1,pointer,sourceCommit:intent.commit});
await writeJson(path.join(settings.root,'native-cache','current-native.json'),{directory:path.join(settings.root,'bootstrap','native'),sha256:await fileHash(path.join(settings.root,'bootstrap','native','Start-Server.ps1'))});
process.stdout.write(JSON.stringify({version:pointer.version,sha256:pointer.bundleSha256})+'\n');
