import test from 'node:test';
import assert from 'node:assert/strict';
import {generateKeyPairSync,sign,createHash,randomBytes} from 'node:crypto';
import {mkdtemp,mkdir,writeFile,readFile,lstat,unlink} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,dirname} from 'node:path';
import {execFileSync} from 'node:child_process';
import {createRequest,verifyRequest,claimRequest} from '../LocalReleaseRequest.mjs';
import {runOnce,environment,redact,publishProcess} from '../Worker.mjs';

const context=Buffer.from('AETHERFRONT VPS LOCAL RELEASE APPROVAL v3\n');
const keys=generateKeyPairSync('ed25519');
const commit='a'.repeat(40),host='vmi3254434';
const hash=data=>createHash('sha256').update(data).digest('hex');
function request(overrides={}){const now=Date.now();return {host,requestId:randomBytes(16).toString('hex'),action:'verify',version:'kesas-rc54',archiveSha256:'b'.repeat(64),sourceCommit:commit,issuedAt:new Date(now).toISOString(),expiresAt:new Date(now+600000).toISOString(),...overrides};}
const signature=bytes=>sign(null,Buffer.concat([context,bytes]),keys.privateKey);
const verify=(bytes,sig=signature(bytes),extra={})=>verifyRequest(bytes,sig,{host,publicKey:keys.publicKey,activeVersion:'kesas-rc52',...extra});

test('canonical signed approval accepts only exact host, release, nonce and lifetime',()=>{
 const original=request();const bytes=createRequest(original);assert.equal(verify(bytes).version,'kesas-rc54');
 for(const changes of [{host:'different'},{requestId:'../escape'},{action:'shell'},{version:'kesas-rc51'},{sourceCommit:'HEAD'},{expiresAt:new Date(Date.now()-1).toISOString()},{issuedAt:new Date(Date.now()+60000).toISOString()},{expiresAt:new Date(Date.now()+1200000).toISOString()}])assert.throws(()=>verify(createRequest({...original,...changes})));
 assert.throws(()=>verify(Buffer.from(bytes.toString().replace('rc54','rc55')),signature(bytes)));
 assert.throws(()=>verify(Buffer.concat([bytes,Buffer.from(' ')])));
 assert.throws(()=>verify(createRequest({...original,action:'publish',version:'kesas-rc52'})));
 assert.equal(verify(createRequest({...original,version:'kesas-rc52'})).action,'verify');
});

async function fixture(){
 const root=await mkdtemp(join(tmpdir(),'apf-publisher-v3-'));
 const config={schemaVersion:3,host,root,moduleSearch:[],powerShell:join(root,'pwsh.exe'),nodePath:join(root,'node.exe'),gitPath:join(root,'git.exe'),githubCliPath:join(root,'gh.exe')};
 for(const name of ['inbox','privateJobs','claims','rejected','statusRoot','repoRoot','nativeEvidenceRoot']){config[name]=join(root,name);await mkdir(config[name]);}
 for(const name of ['publicKeyPath','activeFeedPath','releaseVerifier','credentialDecryptor'])config[name]=join(root,name);
 await writeFile(config.publicKeyPath,keys.publicKey.export({format:'pem',type:'spki'}));await writeFile(config.activeFeedPath,JSON.stringify({version:'kesas-rc52'}));
 const archive=Buffer.from('verified native archive fixture'),calls=[];
 async function queue(overrides={}){const value=request({archiveSha256:hash(archive),...overrides});const bytes=createRequest(value);await writeFile(join(config.inbox,value.archiveSha256+'.zip'),archive);await writeFile(join(config.inbox,'pending.sig'),signature(bytes));await writeFile(join(config.inbox,'pending.json'),bytes);return value;}
 function exec(exe,args,options){calls.push({exe,args,env:options.env});if(exe===config.gitPath){if(args.includes('rev-parse'))return commit;if(args.includes('status'))return '';return '';}
 if(args.includes(config.credentialDecryptor))return JSON.stringify({githubToken:'fresh-sensitive-credential'});
 if(args.includes(config.releaseVerifier))return 'Verified fixture';throw Error('Unexpected executable');}
 return {root,config,queue,calls,exec};
}
async function withCredentials(fn){const prior={GH_TOKEN:process.env.GH_TOKEN,APF_DISCORD_TOKEN:process.env.APF_DISCORD_TOKEN};process.env.GH_TOKEN='cached-test-github';process.env.APF_DISCORD_TOKEN='private-test-discord';try{await fn();}finally{for(const [k,v]of Object.entries(prior))if(v===undefined)delete process.env[k];else process.env[k]=v;}}

test('verify uses private archive and Git checkout, never invokes publisher, rejects replay',async()=>withCredentials(async()=>{
 const f=await fixture(),r=await f.queue();let publications=0;
 const result=await runOnce(f.config,{exec:f.exec,publish:async()=>{publications++;}});
 assert.equal(result.status,'verified');assert.equal(result.productionActivated,false);assert.equal(publications,0);
 assert(f.calls.some(c=>c.args.includes(f.config.releaseVerifier)&&c.args.includes(join(f.config.privateJobs,r.requestId,'Release.zip'))));
 const fetch=f.calls.find(c=>c.args.includes('fetch'));assert(fetch.args.includes('https://github.com/esotericmartyristaken-bit/AETHERFRONT.git'));
 assert(fetch.args.includes('protocol.file.allow=never'));assert(fetch.args.includes('core.hooksPath=NUL'));assert.equal(fetch.env.GIT_CONFIG_VALUE_2,'false');
 assert(!JSON.stringify(fetch.args).includes('cached-test-github'));
 await assert.rejects(lstat(join(f.config.privateJobs,r.requestId,'Release.zip')),{code:'ENOENT'});
 await f.queue(r);assert.equal((await runOnce(f.config,{exec:f.exec})).code,'request-already-consumed');
}));

test('failed job preserves evidence and permits a fresh signed retry without global reset',async()=>withCredentials(async()=>{
 const f=await fixture();await f.queue();
 const failed=await runOnce(f.config,{exec:()=>{throw Error('Simulated offline GitHub cached-test-github');}});
 assert.equal(failed.status,'failed');assert((await lstat(join(f.config.privateJobs,failed.requestId,'Release.zip'))).isFile());
 const evidence=await readFile(join(f.config.statusRoot,failed.requestId+'.evidence.json'),'utf8');assert(!evidence.includes('cached-test-github'));
 await f.queue();assert.equal((await runOnce(f.config,{exec:f.exec})).status,'verified');
}));

test('publishing requires exact healthy receipt and active feed matching the signed request',async()=>withCredentials(async()=>{
 const f=await fixture();await f.queue({action:'publish'});let seen;
 const result=await runOnce(f.config,{exec:f.exec,publish:async(config,args,options)=>{seen={args,options};
 const reportPath=args[args.indexOf('-ReportPath')+1];await writeFile(reportPath,JSON.stringify({version:'kesas-rc54',stage:'healthy',healthy:true,sourceCommit:commit,archiveSha256:args[args.indexOf('-ArchiveSha256')+1]}));await writeFile(config.activeFeedPath,JSON.stringify({version:'kesas-rc54'}));}});
 assert.equal(result.status,'healthy');assert(seen.args.includes(join(f.config.repoRoot,commit,'Source','Aetherfront-RC44-Work/Deployment/Deploy-LocalRelease.ps1')));
 assert.equal(seen.options.env.GH_TOKEN,'cached-test-github');assert(!JSON.stringify(seen.args).includes('private-test-discord'));
}));

test('fresh encrypted credential is bound to signature and redacted from failure evidence',async()=>withCredentials(async()=>{
 const f=await fixture(),cipher=randomBytes(512),cipherHash=hash(cipher);await writeFile(join(f.config.inbox,cipherHash+'.credentials'),cipher);await f.queue({action:'publish',credentialSha256:cipherHash});
 const result=await runOnce(f.config,{exec:f.exec,publish:async(c,a,o)=>{assert.equal(o.env.GH_TOKEN,'fresh-sensitive-credential');throw Error('Publisher failure fresh-sensitive-credential');}});
 assert.equal(result.status,'failed');const evidence=await readFile(join(f.config.statusRoot,result.requestId+'.evidence.json'),'utf8');assert(!evidence.includes('fresh-sensitive-credential'));assert(evidence.includes('[REDACTED]'));
}));

test('hermetic environment strips injection and proxy settings, preserves explicit secrets only in memory',()=>{
 process.env.NODE_OPTIONS='--require bad.js';process.env.HTTPS_PROXY='http://bad';try{const env=environment({powerShell:'C:/safe/pwsh.exe',nodePath:'C:/safe/node.exe',gitPath:'C:/safe/git.exe',githubCliPath:'C:/safe/gh.exe',moduleSearch:['C:/safe/modules']},'C:/private');assert.equal(env.NODE_OPTIONS,undefined);assert.equal(env.HTTPS_PROXY,undefined);assert.equal(env.GIT_CONFIG_GLOBAL,'NUL');assert.equal(env.TEMP,'C:/private');}finally{delete process.env.NODE_OPTIONS;delete process.env.HTTPS_PROXY;}
 assert.equal(redact('Authorization: Bearer abc\npassword=hello'),'Authorization: [REDACTED]\npassword=[REDACTED]');
});

test('durable nonce claim is atomic and cannot be reused',async()=>{const dir=await mkdtemp(join(tmpdir(),'apf-claim-v3-'));const id='c'.repeat(32);await claimRequest(Buffer.from('one'),id,dir);await assert.rejects(claimRequest(Buffer.from('two'),id,dir),{code:'EEXIST'});assert.equal(await readFile(join(dir,id+'.json'),'utf8'),'one');});

test('actual signer process emits a verifiable envelope without private key output',()=>{
 const r=request(),der=keys.privateKey.export({type:'pkcs8',format:'der'}),publicHash=hash(keys.publicKey.export({type:'spki',format:'der'}));
 const script=new URL('../Sign-LocalReleaseRequest.mjs',import.meta.url);
 const output=execFileSync(process.execPath,[script.pathname.replace(/^\/([A-Za-z]:)/,'$1'),r.host,r.requestId,r.action,r.version,r.archiveSha256,r.sourceCommit,'',r.issuedAt,r.expiresAt,publicHash],{input:der.toString('base64'),encoding:'utf8',windowsHide:true});
 const envelope=JSON.parse(output);assert.equal(verify(Buffer.from(envelope.request,'base64'),Buffer.from(envelope.signature,'base64')).requestId,r.requestId);assert(!output.includes(der.toString('base64')));
});

test('publisher subprocess streams large output directly to private logs',async()=>{
 const work=await mkdtemp(join(tmpdir(),'apf-publish-output-v3-'));
 await publishProcess({powerShell:process.execPath},['-e','process.stdout.write("x".repeat(2*1024*1024));process.stderr.write("finished");'],{work,cwd:work,env:process.env},async()=>{});
 assert.equal((await lstat(join(work,'publisher.stdout.log'))).size,2*1024*1024);assert.equal(await readFile(join(work,'publisher.stderr.log'),'utf8'),'finished');
});
