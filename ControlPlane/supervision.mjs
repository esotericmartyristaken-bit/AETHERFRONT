// Pure control-child policy. Native helper authority remains outside this module.
export function brokerRecoveryState({alive,exitCode,ready,heartbeat,startedAt,now=Date.now(),restarts=0,retryAt=0}){
  if(!alive||exitCode!==null)return {brokerState:'exited',action:now>=retryAt?'start':'wait',delay:Math.min(300000,5000*2**Math.min(restarts,6))};
  if(ready&&heartbeat>0&&now-heartbeat<=45000)return {brokerState:'ready',action:'wait'};
  if(!heartbeat&&now-startedAt<90000)return {brokerState:'starting',action:'wait'};
  return {brokerState:'stale',action:'drain'};
}
export async function stopIdleControlChild({idle,drain,stopWorker,terminate}){
  if(!await idle())return 'native-helper-active';
  await drain();
  if(!await stopWorker())return 'worker-not-stopped';
  if(!await idle())return 'native-helper-active';
  await terminate();
  return 'control-child-stopped';
}
export function workerRecoveryState({running,status,diagnostics,version,sha256,startedAt,now=Date.now(),retryAt=0,restarts=0}){
  const observed=Date.parse(status?.observedAt),expires=Date.parse(status?.expiresAt);
  const fresh=Number.isFinite(observed)&&Number.isFinite(expires)&&observed<=now&&now-observed<=45000&&expires>now&&expires-observed<=30000;
  // Liveness only; independently replaced generations do not grant readiness.
  // The reporter still requires a coherent pair for capability admission.
  const matching=status?.controlVersion===version&&status?.controlSha256===sha256&&diagnostics?.controlVersion===version&&diagnostics?.controlSha256===sha256;
  if(running&&fresh&&matching&&['starting','running','draining'].includes(diagnostics?.stage))return {action:'wait'};
  if(now<retryAt||(running&&now-startedAt<90000))return {action:'wait'};
  return {action:running?'restart':'start',delay:Math.min(300000,5000*2**Math.min(restarts,6))};
}
