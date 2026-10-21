import test from 'node:test';
import assert from 'node:assert/strict';
import { fstatSync } from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { NativeAgent, requestId, validateRequest, validateDeployment, runChild, acquireOwner, runCli,
  MAX_APPLICATION_BYTES } from '../native-agent.mjs';
import { CAPABILITIES, REQUIRED_CONTROL_FILES, REPOSITORY } from '../contract.mjs';
import { digest, writeJson } from '../public-status.mjs';

const commit = '1'.repeat(40), controlCommit = '2'.repeat(40);
const bytes = Buffer.from('disposable application archive');
const pin = 'a'.repeat(64);
const recoveryUrl = `https://raw.githubusercontent.com/${REPOSITORY}/refs/heads/deploy/deployment/recovery.json`;
function publicationUrls(state) {
  return state.urls.filter(url => url !== recoveryUrl);
}
function request(overrides = {}) {
  const value = {schemaVersion:1,transactionVersion:1,version:'kesas-rc32',sha256:digest(bytes),sizeBytes:bytes.length,
    notes:'Reviewed release; spaces, quotes " and $ remain literal.',sourceCommit:commit,...overrides};
  return {...value,id:requestId(value)};
}
function deployment(value = request()) {
  return {schemaVersion:1,version:value.version,releaseTag:`host-${value.version}`,assetName:`Aetherfront-Release-${value.version}.zip`,
    sha256:value.sha256,sizeBytes:value.sizeBytes,notes:value.notes};
}
function control(version) {
  const files = new Map(REQUIRED_CONTROL_FILES.map(name => [name,Buffer.from(`protected control file ${name}`)]));
  const manifest = {schemaVersion:1,transactionVersion:1,version,runtime:{nodeMajor:24,powerShellMajor:7,dotnetMajor:8},
    capabilities:[...CAPABILITIES],files:[...files].map(([name,content]) => ({path:name,sha256:digest(content),sizeBytes:content.length}))};
  const raw = Buffer.from(JSON.stringify(manifest)), archive = Buffer.from(`authenticated control archive ${version}`);
  const descriptor = {schemaVersion:1,transactionVersion:1,version,releaseTag:`host-${version}`,assetName:`Aetherfront-Control-${version}.zip`,
    sha256:digest(archive),sizeBytes:archive.length,minimumBootstrap:1};
  const receipt = {schemaVersion:1,version,bundleSha256:descriptor.sha256,manifestSha256:digest(raw),commit:controlCommit,dependencies:[]};
  const pointer = {schemaVersion:1,version,sequence:Number(version.slice(8)),directory:`${version}-${descriptor.sha256}`,
    bundleSha256:descriptor.sha256,manifestSha256:digest(raw),receiptSha256:digest(Buffer.from(JSON.stringify(receipt)))};
  return {files,manifest,raw,archive,descriptor,receipt,pointer};
}
async function put(file, content) {
  await fs.mkdir(path.dirname(file),{recursive:true});
  await fs.writeFile(file,content);
}
async function fixture(t, options = {}) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(),'apf-native-agent-'));
  t.after(async () => {
    // Every fixture path resolves under this exact disposable directory.
    assert.ok(path.basename(directory).startsWith('apf-native-agent-'));
    await fs.rm(directory,{recursive:true,force:true});
  });
  const settings = {root:path.join(directory,'control'),botRoot:path.join(directory,'bot'),nativeRoot:path.join(directory,'native'),
    powershell:path.join(directory,'runtime','pwsh.exe'),node:process.execPath,npm:path.join(directory,'runtime','npm-cli.js'),
    runtime:{node:'24.19.0',powershell:'7.5.3',dotnet:'8.0.29'}};
  const bootstrap = path.join(settings.root,'bootstrap');
  const initial = control('control-1'), next = control('control-2');
  const value = request(options.request);
  const state = {idle:true,publish:0,verify:0,extract:0,health:0,calls:[],urls:[],pending:null};
  await fs.mkdir(path.join(settings.root,'native-cache'),{recursive:true});
  await writeJson(path.join(settings.root,'host.json'),settings);
  await writeJson(path.join(bootstrap,'seed.json'),{pointer:initial.pointer,provenance:{commit:controlCommit}});
  await writeJson(path.join(settings.root,'active.json'),options.updatedControl ? next.pointer : initial.pointer);
  await put(path.join(bootstrap,'native-agent.mjs'),'frozen native agent');
  await put(path.join(bootstrap,'Invoke-NativePublisher.ps1'),'frozen publisher wrapper');
  await put(path.join(bootstrap,'native','Publish-Release.ps1'),'frozen publisher');
  await put(path.join(bootstrap,'native','Start-Server.ps1'),'frozen server starter');
  await put(path.join(bootstrap,'Verify-KesasRelease.ps1'),'frozen verifier');
  await put(path.join(bootstrap,'Expand-ControlBundle.ps1'),'frozen extractor');
  if (options.updatedControl) await writeJson(path.join(settings.root,'releases',next.pointer.directory,'.verified.json'),next.receipt);
  const active = async version => {
    await writeJson(path.join(settings.nativeRoot,'Updates','APFClientUpdate.json'),{version});
    await writeJson(path.join(settings.nativeRoot,'Server','Config','APFPack.json'),{packVersion:version,requiredClientVersion:version});
  };
  await active('kesas-rc31');
  await writeJson(path.join(settings.nativeRoot,'deployment.json'),{packPort:18866,certificateSha256:pin,privateToken:'fixture-private-token'});
  await put(path.join(settings.nativeRoot,'CertificateTool','ApfCertificateTool.exe'),'fake probe executable');
  const enqueue = async (candidate = value, archive = options.archive || bytes, text = JSON.stringify(candidate)) => {
    await put(path.join(settings.root,'native-requests',`${candidate.id}.zip`),archive);
    await put(path.join(settings.root,'native-requests',`${candidate.id}.json`),text);
  };
  await enqueue();
  const fetcher = async (url, init) => {
    state.urls.push(url);
    assert.equal(init.redirect,'manual');
    assert.equal(Object.keys(init.headers).some(key => /authorization|cookie/i.test(key)),false);
    assert.equal(new URL(url).protocol,'https:');
    if (url === recoveryUrl) return new Response('not found',{status:404});
    if (options.fetch) { const response = await options.fetch(url,init); if (response) return response; }
    if (url === `https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`)
      return Response.json({ref:'refs/heads/deploy',object:{type:'commit',sha:commit}});
    if (url === `https://raw.githubusercontent.com/${REPOSITORY}/${commit}/deployment/production.json`)
      return new Response(options.descriptorText || JSON.stringify(options.descriptor || deployment(value)));
    if (url === `https://raw.githubusercontent.com/${REPOSITORY}/${controlCommit}/deployment/control-plane.json`)
      return Response.json(options.controlDescriptor || next.descriptor);
    if (url === `https://github.com/${REPOSITORY}/releases/download/${next.descriptor.releaseTag}/${next.descriptor.assetName}`)
      return new Response(options.controlArchive || next.archive);
    return new Response('not found',{status:404});
  };
  const spawner = (file,args,spawnOptions) => {
    assert.equal(spawnOptions.shell,false); assert.equal(spawnOptions.windowsHide,true);
    assert.equal(spawnOptions.timeout,undefined); assert.equal(spawnOptions.killSignal,undefined);
    const script = args.includes('-File') ? args[args.indexOf('-File')+1] : null;
    if (script === path.join(bootstrap,'Invoke-NativePublisher.ps1')) {
      assert.equal(spawnOptions.stdio.length,3); assert.equal(spawnOptions.stdio[0],'ignore');
      for (const fd of spawnOptions.stdio.slice(1)) {
        assert.ok(Number.isSafeInteger(fd) && fd >= 0);
        assert.ok(fstatSync(fd).isFile());
      }
      assert.notEqual(spawnOptions.stdio[1],spawnOptions.stdio[2]);
    } else assert.deepEqual(spawnOptions.stdio,['ignore','pipe','pipe']);
    const child = new EventEmitter(); child.pid = 4242;
    child.stdout = spawnOptions.stdio[1] === 'pipe' ? new PassThrough() : null;
    child.stderr = spawnOptions.stdio[2] === 'pipe' ? new PassThrough() : null;
    child.kill = () => assert.fail('A native child must never be killed');
    state.calls.push({file,args,options:spawnOptions});
    const complete = (code = 0, stdout = '') => { child.stdout?.end(stdout); child.stderr?.end(); child.emit('exit',code,null); child.emit('close',code,null); };
    setImmediate(async () => {
      try {
        if (args.includes('-EncodedCommand')) {
          assert.equal(spawnOptions.env.APF_NATIVE_LEASE_ROOT,settings.nativeRoot);
          return complete(0,state.idle ? 'idle\n' : 'busy\n');
        }
        if (file.endsWith('ApfCertificateTool.exe')) {
          state.health++;
          assert.deepEqual(args,['--probe-server','https://127.0.0.1:18866/health',pin]);
          return complete(options.healthFailure ? 1 : 0,'private health output discarded');
        }
        if (script === path.join(bootstrap,'Verify-KesasRelease.ps1')) {
          state.verify++;
          assert.deepEqual(await fs.readFile(args[args.indexOf('-Archive')+1]),bytes);
          return complete(options.verifyFailure ? 1 : 0);
        }
        if (script === path.join(bootstrap,'Expand-ControlBundle.ps1')) {
          state.extract++;
          const destination = args[args.indexOf('-Destination')+1];
          assert.ok(destination.startsWith(path.join(settings.root,'native-cache','control')+path.sep));
          assert.deepEqual(await fs.readFile(args[args.indexOf('-Archive')+1]),next.archive);
          await put(path.join(destination,'CONTROL.json'),next.raw);
          for (const [name,content] of next.files) await put(path.join(destination,...name.split('/')),content);
          if (options.tamperExtraction) await put(path.join(destination,'KESAS-VPS-Server','Publish-Release.ps1'),'unverified replacement');
          return complete();
        }
        assert.equal(file,settings.powershell);
        assert.equal(script,path.join(bootstrap,'Invoke-NativePublisher.ps1'));
        const publisher = args[args.indexOf('-Publisher')+1];
        assert.equal(path.basename(publisher),'Publish-Release.ps1');
        state.publish++;
        assert.equal(spawnOptions.detached,process.platform !== 'win32');
        assert.equal(spawnOptions.env.APF_SERVER_TASK,'KESAS Server');
        assert.equal(spawnOptions.env.APF_CONTROL_ROOT,settings.root);
        assert.deepEqual(JSON.parse(await fs.readFile(path.join(settings.root,'native-cache','current-native.json'),'utf8')),
          {directory:path.dirname(publisher),sha256:digest(await fs.readFile(path.join(path.dirname(publisher),'Start-Server.ps1')))});
        assert.deepEqual(args,['-NoProfile','-NonInteractive','-File',path.join(bootstrap,'Invoke-NativePublisher.ps1'),
          '-Settings',path.join(settings.root,'host.json'),'-RequestId',value.id,'-Attempt','1','-Publisher',publisher,'-Archive',
          path.join(settings.root,'native-cache','applications',`${value.id}.zip`),'-Notes',value.notes,...(options.guarded ? ['-AllowActivePlayers'] : [])]);
        if (options.updatedControl) {
          assert.ok(publisher.startsWith(path.join(settings.root,'native-cache','control')+path.sep));
          assert.equal(publisher.includes(path.join(settings.root,'releases')),false);
        } else assert.equal(publisher,path.join(bootstrap,'native','Publish-Release.ps1'));
        const completePublication = async (code = 0) => {
          if (code === 0) await writeJson(path.join(settings.root,'native-cache','diagnostics',value.id,'1','receipt.json'),
            {requestId:value.id,attempt:1,status:'succeeded',exitCode:0});
          complete(code);
        };
        if (options.holdPublisher) { state.pending = completePublication; return; }
        if (!options.publishFailure) await active(value.version);
        return await completePublication(options.publishFailure ? 1 : 0);
      } catch (error) { state.childError = error; complete(1); }
    });
    return child;
  };
  const agentOptions = {bootstrapDirectory:bootstrap,fetch:fetcher,spawn:spawner,isAlive:async pid => pid === process.pid,...options.agentOptions};
  const agent = new NativeAgent(settings,agentOptions);
  const tick = async () => { const result = await agent.tick(); if (state.childError) throw state.childError; return result; };
  return {directory,settings,bootstrap,value,state,agent,agentOptions,next,active,enqueue,tick};
}

test('requestId uses exact canonical order; requests reject extra fields and invalid metadata', () => {
  const value = request();
  assert.equal(requestId(value),digest(Buffer.from(JSON.stringify({schemaVersion:1,transactionVersion:1,version:value.version,
    sha256:value.sha256,sizeBytes:value.sizeBytes,notes:value.notes,sourceCommit:value.sourceCommit}))));
  assert.equal(requestId(Object.fromEntries(Object.entries(value).reverse())),value.id);
  assert.equal(validateRequest(value),value);
  for (const change of [{force:true},{destination:'C:\\other'},{schemaVersion:2},{transactionVersion:2},{id:'A'.repeat(64)},
    {sourceCommit:'A'.repeat(40)},{sourceCommit:undefined},{version:'kesas-rc0'},{version:'kesas-rc'+'1'.repeat(16)},
    {sha256:'A'.repeat(64)},{sizeBytes:0},{sizeBytes:MAX_APPLICATION_BYTES+1},{sizeBytes:1.5},
    {notes:'\u0000'},{notes:'\u202e'},{notes:'x'.repeat(501)}]) {
    const changed = {...value,...change};
    if (!Object.hasOwn(change,'id')) changed.id = requestId(changed);
    assert.throws(() => validateRequest(changed));
  }
});
test('schema 1 exact seven fields; schema 2 strict known capabilities', () => {
  assert.equal(validateDeployment(deployment()).schemaVersion,1);
  const value = {...deployment(),schemaVersion:2,requirements:{contractVersion:1,capabilities:['application-v1','media-payload-v1']}};
  assert.equal(validateDeployment(value),value);
  for (const change of [{schemaVersion:3},{url:'https://evil.invalid'},{assetName:'other.zip'},{releaseTag:'other'},
    {requirements:{contractVersion:2,capabilities:['application-v1']}},{requirements:{contractVersion:1,capabilities:[]}},
    {requirements:{contractVersion:1,capabilities:['future-v99']}},{requirements:{contractVersion:1,capabilities:['application-v1','application-v1']}},
    {requirements:{contractVersion:1,capabilities:['application-v1'],force:true}}]) assert.throws(() => validateDeployment({...value,...change}));
  const missing = deployment(); delete missing.notes; assert.throws(() => validateDeployment(missing));
});
test('fixed destination, protected archive, pinned health, safe result; duplicate publishes once', async t => {
  const f = await fixture(t);
  const result = await f.tick();
  assert.equal(result.status,'healthy'); assert.equal(result.phase,'healthy');
  assert.equal(result.version,f.value.version); assert.equal(result.sha256,f.value.sha256);
  assert.equal(result.health,'healthy'); assert.equal(result.authenticatedCommit,commit);
  assert.equal(f.state.publish,1); assert.equal(f.state.verify,1); assert.equal(f.state.health,1);
  assert.equal((await f.tick()).status,'healthy'); assert.equal(f.state.publish,1);
  const text = await fs.readFile(path.join(f.settings.root,'native-results',`${f.value.id}.json`),'utf8');
  assert.equal(text.includes(f.directory),false); assert.equal(text.includes(pin),false); assert.equal(text.includes('fixture-private-token'),false);
  assert.equal(publicationUrls(f.state).length,1);
});
test('null sourceCommit resolves fixed deploy head, then authenticates pinned production descriptor', async t => {
  const f = await fixture(t,{request:{sourceCommit:null}});
  assert.equal((await f.tick()).status,'healthy');
  assert.deepEqual(publicationUrls(f.state),[`https://api.github.com/repos/${REPOSITORY}/git/ref/heads/deploy`,
    `https://raw.githubusercontent.com/${REPOSITORY}/${commit}/deployment/production.json`]);
});
test('schema 2 authenticated intent supported', async t => {
  const f = await fixture(t,{descriptor:{...deployment(),schemaVersion:2,requirements:{contractVersion:1,capabilities:['application-v1']}}});
  assert.equal((await f.tick()).status,'healthy');
});
test('mismatched application intent blocks, never retries same id', async t => {
  const f = await fixture(t,{descriptor:{...deployment(),notes:'Different authorized notes'}});
  assert.equal((await f.tick()).status,'blocked'); assert.equal(f.state.publish,0); assert.equal(f.state.verify,0);
  await f.enqueue(); assert.equal((await f.tick()).status,'blocked'); assert.equal(publicationUrls(f.state).length,1);
});
test('bad application hash or length cannot reach verifier/publisher', async t => {
  for (const archive of [Buffer.from('x'.repeat(bytes.length)),Buffer.from('short')]) {
    const f = await fixture(t,{archive});
    assert.equal((await f.tick()).status,'blocked'); assert.equal(f.state.publish,0); assert.equal(f.state.verify,0);
  }
});
test('duplicate JSON keys, invalid request id, payload arguments never execute', async t => {
  for (const variant of ['duplicate','force','identity']) {
    const f = await fixture(t);
    const changed = {...f.value}; if (variant === 'force') changed.force = true; if (variant === 'identity') changed.notes = 'changed after id';
    const text = variant === 'duplicate' ? JSON.stringify(changed).replace('"schemaVersion":1','"schemaVersion":1,"schemaVersion":1') : JSON.stringify(changed);
    await f.enqueue(f.value,bytes,text);
    assert.equal((await f.tick()).status,'blocked'); assert.equal(f.state.calls.length,0);
  }
});
test('anonymous GitHub transport rejects foreign HTTPS redirects and duplicate descriptor fields', async t => {
  const redirect = await fixture(t,{fetch:async () => new Response(null,{status:302,headers:{location:'https://evil.invalid/production.json'}})});
  assert.equal((await redirect.tick()).status,'blocked'); assert.equal(publicationUrls(redirect.state).length,1);
  const text = JSON.stringify(deployment()).replace('"schemaVersion":1','"schemaVersion":1,"schemaVersion":1');
  const duplicate = await fixture(t,{descriptorText:text});
  assert.equal((await duplicate.tick()).status,'blocked'); assert.equal(duplicate.state.publish,0);
});
test('non-seed control authenticated at receipt commit; only protected extracted scripts execute', async t => {
  const f = await fixture(t,{updatedControl:true});
  assert.equal((await f.tick()).status,'healthy'); assert.equal(f.state.extract,1); assert.equal(f.state.publish,1);
  assert.ok(f.state.urls.includes(`https://raw.githubusercontent.com/${REPOSITORY}/${controlCommit}/deployment/control-plane.json`));
});
test('bad control pointer authorization, archive hash or extracted file prevents publication', async t => {
  for (const options of [{updatedControl:true,controlDescriptor:{...control('control-2').descriptor,sha256:'f'.repeat(64)}},
    {updatedControl:true,controlArchive:Buffer.from('bad control bytes')},{updatedControl:true,tamperExtraction:true}]) {
    const f = await fixture(t,options);
    assert.equal((await f.tick()).status,'blocked'); assert.equal(f.state.publish,0);
  }
});
test('recovery waits for live helper; never replays even when active version still old', async t => {
  const f = await fixture(t,{agentOptions:{fault:async phase => { if (phase === 'applying') throw Object.assign(new Error('crash'),{simulatedCrash:true}); }}});
  await assert.rejects(f.tick(),/crash/);
  assert.equal(f.state.publish,0);
  f.state.idle = false;
  const recovered = new NativeAgent(f.settings,{...f.agentOptions,fault:undefined});
  assert.equal((await recovered.tick()).status,'applying'); assert.equal(f.state.publish,0); assert.equal(f.state.health,0);
  f.state.idle = true;
  assert.equal((await recovered.tick()).status,'blocked'); assert.equal(f.state.publish,0);
});
test('recovery recognizes committed healthy application without rerunning publisher', async t => {
  const f = await fixture(t,{agentOptions:{fault:async phase => { if (phase === 'published') throw Object.assign(new Error('crash'),{simulatedCrash:true}); }}});
  await assert.rejects(f.tick(),/crash/); assert.equal(f.state.publish,1);
  const recovered = new NativeAgent(f.settings,{...f.agentOptions,fault:undefined});
  assert.equal((await recovered.tick()).status,'healthy'); assert.equal(f.state.publish,1); assert.equal(f.state.health,1);
});
test('recovery blocks committed application without private successful receipt', async t => {
  const f = await fixture(t,{agentOptions:{fault:async phase => { if (phase === 'published') throw Object.assign(new Error('crash'),{simulatedCrash:true}); }}});
  await assert.rejects(f.tick(),/crash/); assert.equal(f.state.publish,1);
  await fs.unlink(path.join(f.settings.root,'native-cache','diagnostics',f.value.id,'1','receipt.json'));
  const recovered = new NativeAgent(f.settings,{...f.agentOptions,fault:undefined});
  const result = await recovered.tick();
  assert.equal(result.status,'blocked'); assert.equal(result.failure,'native-publication-blocked');
  assert.equal(f.state.publish,1); assert.equal(f.state.health,0);
  assert.equal((await recovered.tick()).status,'blocked'); assert.equal(f.state.publish,1);
});
test('recovery waits for recorded starting child even before native OS lease is acquired', async t => {
  const f = await fixture(t,{agentOptions:{fault:async phase => { if (phase === 'published') throw Object.assign(new Error('crash'),{simulatedCrash:true}); }}});
  await assert.rejects(f.tick(),/crash/);
  const recovered = new NativeAgent(f.settings,{...f.agentOptions,fault:undefined,isAlive:async pid => pid === 4242 || pid === process.pid});
  assert.equal((await recovered.tick()).status,'applying'); assert.equal(f.state.publish,1); assert.equal(f.state.health,0);
});
test('abandoned native journal preserved and blocked after lease becomes idle', async t => {
  const f = await fixture(t,{agentOptions:{fault:async phase => { if (phase === 'published') throw Object.assign(new Error('crash'),{simulatedCrash:true}); }}});
  await assert.rejects(f.tick(),/crash/);
  const journal = path.join(f.settings.nativeRoot,'.update-transaction.json');
  await put(journal,'preserve this exact journal');
  const recovered = new NativeAgent(f.settings,{...f.agentOptions,fault:undefined});
  assert.equal((await recovered.tick()).status,'blocked');
  assert.equal(await fs.readFile(journal,'utf8'),'preserve this exact journal'); assert.equal(f.state.publish,1);
});
test('native failure and failed pinned health publish only static failure', async t => {
  for (const options of [{publishFailure:true},{verifyFailure:true},{healthFailure:true}]) {
    const f = await fixture(t,options);
    const result = await f.tick();
    assert.equal(result.status,'blocked'); assert.equal(result.failure,'native-publication-blocked'); assert.equal(result.health,'unknown');
    const count = f.state.publish; await f.tick(); assert.equal(f.state.publish,count);
  }
});
test('single owner stays held for asynchronous publisher; no second agent or timeout kill', async t => {
  const f = await fixture(t,{holdPublisher:true});
  const pending = f.tick();
  while (!f.state.pending) await new Promise(resolve => setImmediate(resolve));
  const second = new NativeAgent(f.settings,f.agentOptions);
  assert.equal((await second.tick()).status,'busy');
  assert.equal((await f.agent.tick()).status,'busy');
  await f.active(f.value.version); await f.state.pending();
  assert.equal((await pending).status,'healthy'); assert.equal(f.state.publish,1);
});
test('public result forgery cannot suppress request verification or reset protected failure', async t => {
  const f = await fixture(t,{descriptor:{...deployment(),notes:'not authorized'}});
  await writeJson(path.join(f.settings.root,'native-results',`${f.value.id}.json`),{...f.value,status:'healthy'});
  assert.equal((await f.tick()).status,'blocked');
  await writeJson(path.join(f.settings.root,'native-results',`${f.value.id}.json`),{...f.value,status:'accepted'});
  assert.equal((await f.tick()).status,'blocked'); assert.equal(publicationUrls(f.state).length,1);
});
test('owner reclaim handles dead owner; live or uncertain owners cannot be stolen', async t => {
  const f = await fixture(t); const cache = path.join(f.settings.root,'native-cache');
  await writeJson(path.join(cache,'owner.json'),{pid:123,token:'old'});
  assert.equal(await acquireOwner(cache,{isAlive:async () => true}),null);
  const release = await acquireOwner(cache,{isAlive:async () => false}); assert.equal(typeof release,'function');
  await release();
  await writeJson(path.join(cache,'owner.json'),{pid:0,token:'fixture-uncertain-owner'});
  await assert.rejects(acquireOwner(cache),/Uncertain/);
});
test('runner waits for close without a timeout and does not expose child errors', async () => {
  let child;
  const pending = runChild('fake.exe',[],{},() => {
    child = new EventEmitter(); child.stdout = new PassThrough(); child.pid = 123;
    child.kill = () => assert.fail('kill forbidden'); return child;
  });
  let completed = false; pending.then(() => { completed = true; },() => {});
  await new Promise(resolve => setImmediate(resolve)); assert.equal(completed,false);
  child.emit('error',new Error('private-token and secret path'));
  await assert.rejects(pending,error => error.message === 'native-publication-blocked');
});
test('CLI errors sanitized; rejects missing, duplicate and unknown settings flags', async () => {
  for (const args of [[],['--settings'],['--token','secret'],['--settings','relative.json'],['--settings','C:\\private.json','--force']]) {
    let output = '';
    assert.equal(await runCli(args,{stderr:{write:text => { output += text; }}}),1);
    assert.equal(output,'Native publication agent blocked; journals preserved.\n');
  }
});
test('only completed matching 15-minute countdown with fresh known bridge counts permits occupied publication', async t => {
  const f = await fixture(t,{guarded:true});
  const now = Date.now();
  const state = {schemaVersion:1,status:'deploying',version:f.value.version,force:true,countdownMinutes:15,
    countdownStartedAt:new Date(now-901000).toISOString(),deployAt:new Date(now-1000).toISOString(),countdownAnnouncements:[900000]};
  const bridge = {updatedAt:new Date(now).toISOString(),playerCount:2,connectedCount:3};
  const stateFile = path.join(f.settings.botRoot,'state','kesas-update-state.json');
  const bridgeFile = path.join(f.settings.nativeRoot,'Server','APFDiscordBridge','status.json');
  await writeJson(stateFile,state); await writeJson(bridgeFile,bridge);
  assert.deepEqual(await f.agent.guardArgs(f.value,now),['-AllowActivePlayers']);
  for (const change of [{schemaVersion:2},{status:'queued'},{version:'kesas-rc31'},{force:false},{force:'true'},
    {countdownMinutes:14},{countdownAnnouncements:[]},{countdownAnnouncements:['900000']},{countdownStartedAt:'invalid'},
    {countdownStartedAt:new Date(now-899999).toISOString()},{deployAt:new Date(now+1).toISOString()}]) {
    await writeJson(stateFile,{...state,...change});
    assert.deepEqual(await f.agent.guardArgs(f.value,now),[]);
  }
  await writeJson(stateFile,state);
  for (const change of [{updatedAt:new Date(now-10001).toISOString()},{updatedAt:new Date(now+1).toISOString()},
    {updatedAt:'invalid'},{playerCount:null},{playerCount:-1},{connectedCount:null},{connectedCount:-1},{connectedCount:1}]) {
    await writeJson(bridgeFile,{...bridge,...change});
    assert.deepEqual(await f.agent.guardArgs(f.value,now),[]);
  }
  await writeJson(bridgeFile,{...bridge,updatedAt:new Date().toISOString()});
  assert.equal((await f.tick()).status,'healthy'); assert.equal(f.state.publish,1);
});
