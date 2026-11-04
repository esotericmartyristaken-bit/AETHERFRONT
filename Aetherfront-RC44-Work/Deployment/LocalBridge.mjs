import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { LmpController } from '../../APFDiscordBot/src/lmp.js';

const parse = value => JSON.parse(value.replace(/^\uFEFF/, ''));
export function validateSnapshot(value, now = Date.now()) {
  const age = now - Date.parse(value?.updatedAt);
  if (!value || !Number.isSafeInteger(value.serverPid) || value.serverPid < 1 ||
      !Number.isFinite(age) || age < -2000 || age > 10000 ||
      !Number.isSafeInteger(value.playerCount) || value.playerCount < 0 ||
      !Number.isSafeInteger(value.connectedCount) || value.connectedCount < value.playerCount)
    throw Error('Fresh authoritative player status required; no shutdown authorized');
  return { serverPid:value.serverPid, updatedAt:value.updatedAt,
    playerCount:value.playerCount, connectedCount:value.connectedCount };
}
export async function snapshot(root) {
  const file=path.join(root,'Server','APFDiscordBridge','status.json');
  for(let attempt=0;attempt<20;attempt++){
    try{return validateSnapshot(parse(await fs.readFile(file,'utf8')));}
    catch(error){
      if(!['EACCES','EPERM','EBUSY'].includes(error.code)||attempt===19)throw error;
      await new Promise(resolve=>setTimeout(resolve,50));
    }
  }
  throw Error('Authoritative player status unavailable');
}
async function post(channel, content, token) {
  if (!/^\d{16,22}$/.test(channel || '') || !token) throw Error('Discord announcement destination unavailable');
  const response=await fetch(`https://discord.com/api/v10/channels/${channel}/messages`, {
    method:'POST',headers:{Authorization:`Bot ${token}`,'Content-Type':'application/json'},
    body:JSON.stringify({content,allowed_mentions:{parse:[]}}),signal:AbortSignal.timeout(15000)
  });
  if(!response.ok)throw Error(`Discord announcement rejected (${response.status})`);
}
export async function announce(config, content, options={}) {
  if(typeof content!=='string'||content.length<1||content.length>500||/[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(content))throw Error('Invalid local deployment announcement');
  const live=await snapshot(config.lmp.deploymentRoot);
  const controller=new LmpController(config);
  const game=await controller.say(content);
  if(!game?.ok&&live.connectedCount>0)throw Error('In-game warning not acknowledged; shutdown not authorized');
  const channels=[config.discord.channels.publicStatus,config.discord.channels.lunaChat];
  const token=process.env[config.discord.tokenEnv||'APF_DISCORD_TOKEN'];
  const sent=[];
  for(const channel of new Set(channels.filter(Boolean))){await post(channel,content,token);sent.push(channel);}
  if(!sent.includes(config.discord.channels.publicStatus))throw Error('Public Discord warning not delivered');
  await fs.appendFile(path.join(config.__root,'state','events.jsonl'),JSON.stringify({at:new Date().toISOString(),type:'public-update-announcement',channelId:config.discord.channels.publicStatus,text:content,source:'local-operator'})+'\n');
  return {ok:true,gameAcknowledged:game?.ok===true,discordChannels:sent.length,serverPid:live.serverPid};
}
async function main(){
  const [action,configPath,inputPath]=process.argv.slice(2);
  if(!['status','announce','complete'].includes(action)||!path.isAbsolute(configPath||''))throw Error('Invalid local deployment operation');
  const config=parse(await fs.readFile(configPath,'utf8'));
  config.__root=path.dirname(configPath);config.__file=configPath;config.__controlRoot=null;
  config.security??={};config.discord??={};config.discord.channels??={};
  if(config.updates?.githubDeployment?.enabled!==false)throw Error('Git-triggered host deployment must be disabled for local operation');
  if(action==='status'){console.log(JSON.stringify(await snapshot(config.lmp.deploymentRoot)));return;}
  const input=parse(await fs.readFile(inputPath,'utf8'));
  if(action==='announce'){console.log(JSON.stringify(await announce(config,input.message)));return;}
  if(!/^kesas-rc[1-9][0-9]*$/.test(input.version||''))throw Error('Invalid completed local release');
  const root=config.lmp.deploymentRoot;
  const [pack,feed]=await Promise.all(['Server/Config/APFPack.json','Updates/APFClientUpdate.json'].map(p=>fs.readFile(path.join(root,p),'utf8').then(parse)));
  if(pack.packVersion!==input.version||pack.requiredClientVersion!==input.version||feed.version!==input.version)throw Error('Active local release is inconsistent');
  await snapshot(root);
  const statePath=path.join(config.__root,'state','kesas-update-state.json');
  const previous=parse(await fs.readFile(statePath,'utf8'));
  if(!['idle','failed'].includes(previous.status))throw Error('Another update operation remains active');
  const history=path.join(config.__root,'state',`local-prior-update-${Date.now()}.json`);
  await fs.writeFile(history,JSON.stringify(previous,null,2),{flag:'wx'});
  const next={...previous,status:'idle',packageId:null,version:null,phase:'local-completion-health',error:null,
    lastDeployment:{id:input.version,version:input.version,deployedAt:new Date().toISOString(),notes:input.notes||'',provider:'local-operator'}};
  const temporary=statePath+`.local-${process.pid}.tmp`;
  await fs.writeFile(temporary,JSON.stringify(next,null,2),{flag:'wx'});await fs.rename(temporary,statePath);
  console.log(JSON.stringify(await announce(config,`Server is back online. Update ${input.version} deployed successfully.`)));
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))main().catch(error=>{console.error(error.message);process.exitCode=1;});
