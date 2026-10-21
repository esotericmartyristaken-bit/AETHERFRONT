// Versioned privileged business logic. The fixed anchor verifies every byte.
import path from 'node:path';
import {NativeAgent} from './native-agent.mjs';
import {readJson,writeJson} from './public-status.mjs';
import {validatePointer} from './engine.mjs';
import {publishHostReport} from './host-reporter.mjs';
const settings=await readJson(process.argv[process.argv.indexOf('--settings')+1]);
const pointer=validatePointer(await readJson(path.join(settings.root,'managed','active.json')));
const brokerDirectory=path.dirname(import.meta.dirname);
const agent=new NativeAgent(settings,{codeDirectory:import.meta.dirname});
let stopping=false,health='unknown',reporting=false,lastHealth=0;
process.on('message',message=>{if(message?.type==='drain'){stopping=true;agent.stop=true;}});
process.on('disconnect',()=>{stopping=true;agent.stop=true;});
async function report(){
  if(reporting)return;reporting=true;
  try{
    if(Date.now()-lastHealth>30000){health=await agent.health().catch(()=>'unknown');lastHealth=Date.now();}
    await publishHostReport(settings,{pointer,brokerDirectory,brokerReady:!stopping,health});
    process.send?.({type:'broker-health',version:pointer.version,sha256:pointer.bundleSha256,ready:true});
  }catch(error){
    await writeJson(path.join(settings.root,'native-cache','reporter-error.json'),{at:new Date().toISOString(),message:String(error?.stack||error).slice(0,12000)}).catch(()=>{});
    process.send?.({type:'broker-health',version:pointer.version,sha256:pointer.bundleSha256,ready:false});
  }finally{reporting=false;}
}
await agent.initialize();await report();
const timer=setInterval(()=>report().catch(()=>{}),10000);
try{
  while(!stopping){
    try{await agent.tick();}
    catch(error){await writeJson(path.join(settings.root,'native-cache','broker-error.json'),{at:new Date().toISOString(),message:String(error?.stack||error).slice(0,12000)}).catch(()=>{});}
    if(!stopping)await new Promise(resolve=>setTimeout(resolve,1000));
  }
}finally{
  clearInterval(timer);
  while(reporting)await new Promise(resolve=>setTimeout(resolve,100));
  if(process.connected)process.disconnect();
}
