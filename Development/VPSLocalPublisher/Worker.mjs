import {createHash,randomUUID} from 'node:crypto';
import {createWriteStream,openSync,closeSync} from 'node:fs';
import {lstat,mkdir,open,readFile,rename,unlink,writeFile,readdir} from 'node:fs/promises';
import {dirname,join,resolve,isAbsolute} from 'node:path';
import {Transform} from 'node:stream';
import {pipeline} from 'node:stream/promises';
import {execFileSync,spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {claimRequest,verifyRequest} from './LocalReleaseRequest.mjs';

const repository='https://github.com/esotericmartyristaken-bit/AETHERFRONT.git';
const publisherRelative='Aetherfront-RC44-Work/Deployment/Deploy-LocalRelease.ps1';
const archiveLimit=2147483647;
const same=(a,b)=>b.isFile()&&a.dev===b.dev&&a.ino===b.ino&&a.size===b.size&&a.mtimeNs===b.mtimeNs&&a.ctimeNs===b.ctimeNs&&a.nlink===1n&&b.nlink===1n;

async function stableRead(path,limit){
 const stat=await lstat(path,{bigint:true});
 if(!stat.isFile()||stat.nlink!==1n||stat.size<=0n||stat.size>BigInt(limit))throw Error('Invalid inbox input');
 const file=await open(path,'r');
 try{if(!same(stat,await file.stat({bigint:true})))throw Error('Input changed');
 const data=await file.readFile();if(data.length>limit||!same(stat,await file.stat({bigint:true})))throw Error('Input changed');return data;
 }finally{await file.close();}
}

async function copyArchive(input,output,expected){
 const stat=await lstat(input,{bigint:true});
 if(!stat.isFile()||stat.nlink!==1n||stat.size<=0n||stat.size>BigInt(archiveLimit))throw Error('Invalid archive');
 const file=await open(input,'r');
 try{if(!same(stat,await file.stat({bigint:true})))throw Error('Archive changed');
 const hash=createHash('sha256');let size=0;
 const measure=new Transform({transform(chunk,encoding,done){size+=chunk.length;if(size>Number(stat.size))return done(Error('Archive grew'));hash.update(chunk);done(null,chunk);}});
 await pipeline(file.createReadStream({autoClose:false}),measure,createWriteStream(output,{flags:'wx',mode:0o600}));
 if(!same(stat,await file.stat({bigint:true}))||size!==Number(stat.size)||hash.digest('hex')!==expected)throw Error('Archive hash differs');
 }finally{await file.close();}
}

export function environment(config,work){
 const names=['SystemRoot','SystemDrive','WINDIR','ComSpec','ProgramFiles','ProgramFiles(x86)','ProgramData','USERPROFILE','APPDATA','LOCALAPPDATA','GH_TOKEN','APF_DISCORD_TOKEN'];
 const env=Object.fromEntries(names.filter(n=>process.env[n]!==undefined).map(n=>[n,process.env[n]]));
 env.PATH=[dirname(config.powerShell),dirname(config.nodePath),dirname(config.gitPath),dirname(config.githubCliPath),join(process.env.SystemRoot||'C:\\Windows','System32')].join(';');
 env.PSModulePath=config.moduleSearch.join(';');env.TEMP=env.TMP=work;
 env.GIT_CONFIG_NOSYSTEM='1';env.GIT_CONFIG_GLOBAL='NUL';env.GIT_TERMINAL_PROMPT='0';return env;
}

function command(exe,args,options={}){return execFileSync(exe,args,{...options,windowsHide:true,encoding:'utf8',stdio:['ignore','pipe','pipe'],timeout:300000,maxBuffer:1048576});}

export async function prepareSource(config,request,env,execute=command){
 const base=join(config.repoRoot,request.sourceCommit),source=join(base,'Source');
 await mkdir(source,{recursive:true,mode:0o700});
 // Never execute Git in the operator-writable checkout.
 const gitEnv={...env,GIT_CONFIG_COUNT:'4',GIT_CONFIG_KEY_0:'safe.directory',GIT_CONFIG_VALUE_0:source,
 GIT_CONFIG_KEY_1:'core.hooksPath',GIT_CONFIG_VALUE_1:'NUL',GIT_CONFIG_KEY_2:'core.fsmonitor',GIT_CONFIG_VALUE_2:'false',
 GIT_CONFIG_KEY_3:'http.https://github.com/.extraheader',GIT_CONFIG_VALUE_3:'AUTHORIZATION: basic '+Buffer.from('x-access-token:'+env.GH_TOKEN).toString('base64')};
 const git=args=>execute(config.gitPath,['-c','core.hooksPath=NUL','-c','core.fsmonitor=false','-c','http.sslVerify=true','-c','http.followRedirects=false','-c','protocol.file.allow=never',...args],{cwd:source,env:gitEnv});
 const ready=join(base,'source-ready.json');let cached=false;
 try{cached=JSON.parse(await readFile(ready,'utf8')).commit===request.sourceCommit;}catch(e){if(e.code!=='ENOENT')throw e;}
 if(!cached){git(['init','--quiet',source]);git(['-C',source,'fetch','--no-tags','--depth=1',repository,request.sourceCommit]);git(['-C',source,'-c','core.symlinks=false','checkout','--quiet','--detach',request.sourceCommit]);}
 if(git(['-C',source,'rev-parse','HEAD']).trim()!==request.sourceCommit||git(['-C',source,'status','--porcelain=v1','--untracked-files=all']).trim())throw Error('Private source verification failed');
 if(!cached)await writeFile(ready,JSON.stringify({commit:request.sourceCommit}),{flag:'wx',mode:0o600});
 const reports=join(base,'UpdateBuilds');await mkdir(reports,{recursive:true,mode:0o700});
 return {source,reports,publisher:join(source,publisherRelative)};
}

export function redact(value,secrets=[]){
 let text=String(value||'').slice(0,16000);for(const secret of secrets)if(typeof secret==='string'&&secret.length>5)text=text.split(secret).join('[REDACTED]');
 return text.replace(/-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----/g,'[PRIVATE KEY REDACTED]')
 .replace(/<RSAKeyValue>[\s\S]*?<\/RSAKeyValue>/g,'[PRIVATE KEY REDACTED]')
 .replace(/(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+/g,'[TOKEN REDACTED]')
 .replace(/(Authorization\s*[:=]\s*)([^\r\n]+)/gi,'$1[REDACTED]')
 .replace(/((?:password|token|secret|privateKey)\s*["']?\s*[:=]\s*)[^\s,;}]+/gi,'$1[REDACTED]')
 .replace(/(https?:\/\/[^\s?#]+)\?[^\s]+/g,'$1?[REDACTED]');
}

async function atomicJson(path,value){
 const temp=path+'.'+randomUUID()+'.tmp';await writeFile(temp,JSON.stringify(value),{flag:'wx',mode:0o600});
 try{for(let attempt=0;;attempt++){try{await rename(temp,path);break;}catch(e){if(attempt>=10||!['EPERM','EACCES','EBUSY'].includes(e.code))throw e;await new Promise(r=>setTimeout(r,50));}}}
 finally{await unlink(temp).catch(e=>{if(e.code!=='ENOENT')throw e;});}
}
async function emit(config,value){const result={...value,observedAtUtc:new Date().toISOString()};await atomicJson(join(config.statusRoot,'latest.json'),result);if(value.requestId)await atomicJson(join(config.statusRoot,value.requestId+'.json'),result);return result;}

async function clearMatching(config,bytes,signature){
 try{if((await stableRead(join(config.inbox,'pending.json'),1024)).equals(bytes)&&(await stableRead(join(config.inbox,'pending.sig'),64)).equals(signature)){await unlink(join(config.inbox,'pending.json'));await unlink(join(config.inbox,'pending.sig'));}}catch(e){if(e.code!=='ENOENT')return false;}
}

async function failureEvidence(config,request,reportPath,work,cause,secrets){
 let report={};try{report=JSON.parse(await readFile(reportPath,'utf8'));}catch{}
 const excerpts=[];let files=0;
 const bundle=join(config.nativeEvidenceRoot,request.sourceCommit+'-'+request.archiveSha256.slice(0,16));
 async function scan(directory,depth=0){
 if(depth>10||files>=24)return;let items;try{items=await readdir(directory,{withFileTypes:true});}catch{return;}
 for(const item of items){if(files>=24)break;if(item.isSymbolicLink())continue;const path=join(directory,item.name);
 if(item.isDirectory()){if(!/^(secrets|credentials|Universe|Aetherfront|node_modules|BotRuntime)$/i.test(item.name))await scan(path,depth+1);}
 else if(item.isFile()&&/(?:\.stdout\.log|\.stderr\.log|\.log)$/i.test(item.name)){
 files++;const handle=await open(path,'r');try{const stat=await handle.stat();const data=Buffer.alloc(Math.min(65536,stat.size));await handle.read(data,0,data.length,Math.max(0,stat.size-data.length));
 const lines=data.toString('utf8').split(/\r?\n/).filter(line=>/Fatal|Exception|Error|failed|timed out|startup|running from|Published/i.test(line)).slice(-20);
 if(lines.length)excerpts.push({file:item.name,lines:lines.map(line=>redact(line,secrets))});}finally{await handle.close();}
 }}}
 await scan(bundle);
 const evidence={requestId:request.requestId,version:request.version,stage:report.stage||'publisher',error:redact(report.error||cause?.message||'Publisher did not produce a healthy receipt',secrets),excerpts};
 await atomicJson(join(config.statusRoot,request.requestId+'.evidence.json'),evidence);
 await writeFile(join(work,'failure-evidence.json'),JSON.stringify(evidence),{flag:'wx',mode:0o600});
}

export async function publishProcess(config,args,options,onProgress){
 const stdout=openSync(join(options.work,'publisher.stdout.log'),'wx',0o600),stderr=openSync(join(options.work,'publisher.stderr.log'),'wx',0o600);
 try{await new Promise((resolve,reject)=>{
 const child=spawn(config.powerShell,args,{cwd:options.cwd,env:options.env,windowsHide:true,stdio:['ignore',stdout,stderr]});
 // No timeout and no output buffer can terminate an applying publisher.
 let busy=false,pending=Promise.resolve();const timer=setInterval(()=>{if(!busy){busy=true;pending=Promise.resolve(onProgress()).catch(()=>{}).finally(()=>{busy=false;});}},2000);
 child.once('error',e=>{clearInterval(timer);pending.finally(()=>reject(e));});child.once('close',code=>{clearInterval(timer);pending.finally(()=>code===0?resolve():reject(Error('Guarded publisher exited unsuccessfully')));});
 });}finally{closeSync(stdout);closeSync(stderr);}
}

export async function runOnce(config,io={}){
 const paths=['root','inbox','privateJobs','claims','rejected','statusRoot','repoRoot','publicKeyPath','activeFeedPath','releaseVerifier','credentialDecryptor','powerShell','nodePath','gitPath','githubCliPath','nativeEvidenceRoot'];
 if(config?.schemaVersion!==3||config.host!=='vmi3254434'||!Array.isArray(config.moduleSearch)||paths.some(n=>typeof config[n]!=='string'||!isAbsolute(config[n])))throw Error('Invalid protected configuration');
 const execute=io.exec||command,publish=io.publish||publishProcess;
 let bytes;try{bytes=await stableRead(join(config.inbox,'pending.json'),1024);}catch(e){if(e.code==='ENOENT')return {status:'idle'};throw e;}
 let signature,request;
 try{signature=await stableRead(join(config.inbox,'pending.sig'),64);const active=JSON.parse(await readFile(config.activeFeedPath,'utf8')).version;
 request=verifyRequest(bytes,signature,{host:config.host,publicKey:await readFile(config.publicKeyPath),activeVersion:active});
 }catch{if(signature)await clearMatching(config,bytes,signature);return emit(config,{status:'rejected',code:'invalid-or-expired-approval'});}
 const base={requestId:request.requestId,action:request.action,version:request.version,archiveSha256:request.archiveSha256,sourceCommit:request.sourceCommit};
 try{await lstat(join(config.claims,request.requestId+'.json'));await clearMatching(config,bytes,signature);return emit(config,{...base,status:'rejected',code:'request-already-consumed'});}catch(e){if(e.code!=='ENOENT')throw e;}
 const work=join(config.privateJobs,request.requestId);
 try{await mkdir(work,{mode:0o700});}catch(e){if(e.code!=='EEXIST')throw e;await clearMatching(config,bytes,signature);return emit(config,{...base,status:'rejected',code:'existing-private-job'});}
 const archive=join(work,'Release.zip');let reportPath=join(work,'not-started.json');
 const secrets=[process.env.GH_TOKEN,process.env.APF_DISCORD_TOKEN];
 try{
 await emit(config,{...base,status:'preparing',stage:'private-archive-copy'});await copyArchive(join(config.inbox,request.archiveSha256+'.zip'),archive,request.archiveSha256);
 const env=environment(config,work);
 if(request.credentialSha256){
 const cipher=await stableRead(join(config.inbox,request.credentialSha256+'.credentials'),4096);
 if(createHash('sha256').update(cipher).digest('hex')!==request.credentialSha256)throw Error('Credential envelope differs from signed approval');
 const privateCipher=join(work,'credentials.cipher');await writeFile(privateCipher,cipher,{flag:'wx',mode:0o600});
 let fresh;try{fresh=JSON.parse(execute(config.powerShell,['-NoLogo','-NoProfile','-NonInteractive','-File',config.credentialDecryptor,'-Payload',privateCipher],{cwd:work,env}));}catch{throw Error('Protected credential envelope could not be read');}
 if(typeof fresh.githubToken==='string')env.GH_TOKEN=fresh.githubToken;
 if(typeof fresh.discordToken==='string')env.APF_DISCORD_TOKEN=fresh.discordToken;
 }
 secrets.push(env.GH_TOKEN,env.APF_DISCORD_TOKEN);
 if(!env.GH_TOKEN||!env.APF_DISCORD_TOKEN)throw Error('Protected publication credentials unavailable');
 execute(config.powerShell,['-NoLogo','-NoProfile','-NonInteractive','-File',config.releaseVerifier,'-Archive',archive,'-ExpectedVersion',request.version,'-ExpectedSha256',request.archiveSha256],{cwd:work,env});
 await emit(config,{...base,status:'preparing',stage:'private-source-fetch'});const source=await prepareSource(config,request,env,execute);
 reportPath=join(source.reports,request.requestId+'.json');await claimRequest(bytes,request.requestId,config.claims);await clearMatching(config,bytes,signature);
 if(request.action==='verify')return await emit(config,{...base,status:'verified',stage:'system-authorization-credentials-archive-source',productionActivated:false,systemIdentity:process.env.APF_PUBLISHER_SYSTEM_VERIFIED==='1'});
 const progress=async()=>{let report;try{report=JSON.parse(await readFile(reportPath,'utf8'));}catch{return;}await emit(config,{...base,status:'publishing',stage:report.stage||'publisher',healthy:false});};
 await emit(config,{...base,status:'publishing',stage:'guarded-publisher'});
 const args=['-NoLogo','-NoProfile','-NonInteractive','-File',source.publisher,'-Archive',archive,'-ArchiveSha256',request.archiveSha256,'-SourceCommit',request.sourceCommit,'-ReportPath',reportPath,'-Notes','VPS-local approved KESAS release'];
 await publish(config,args,{cwd:source.source,env,work},progress);
 const report=JSON.parse(await readFile(reportPath,'utf8')),active=JSON.parse(await readFile(config.activeFeedPath,'utf8')).version;
 if(active!==request.version||report.version!==request.version||report.stage!=='healthy'||report.healthy!==true||report.sourceCommit!==request.sourceCommit||report.archiveSha256!==request.archiveSha256)throw Error('Healthy receipt differs from approval');
 const result={...base,status:'healthy',stage:'complete',healthy:true,reportPath};await writeFile(join(work,'completed.json'),JSON.stringify(result),{flag:'wx',mode:0o600});return await emit(config,result);
 }catch(error){await clearMatching(config,bytes,signature);await failureEvidence(config,request,reportPath,work,error,secrets);return emit(config,{...base,status:'failed',stage:'review-required',code:'read-sanitized-evidence',reviewRequired:true});}
 finally{
 // Retain failed private archives for recovery; successful verification and
 // publication already have native receipts and their own rollback artifacts.
 let completed=false;try{const state=JSON.parse(await readFile(join(config.statusRoot,request.requestId+'.json'),'utf8'));completed=['healthy','verified'].includes(state.status);}catch{}
 if(completed){await unlink(archive).catch(()=>{});await unlink(join(work,'credentials.cipher')).catch(()=>{});}
 for(const name of [request.archiveSha256+'.zip',request.credentialSha256?request.credentialSha256+'.credentials':null].filter(Boolean))await unlink(join(config.inbox,name)).catch(()=>{});
 }
}

if(process.argv[1]&&resolve(process.argv[1])===resolve(fileURLToPath(import.meta.url))){try{if(process.argv.length!==2)throw Error('Unexpected arguments');await runOnce(JSON.parse(await readFile(new URL('./runner.json',import.meta.url),'utf8')));}catch{process.exitCode=1;}}

