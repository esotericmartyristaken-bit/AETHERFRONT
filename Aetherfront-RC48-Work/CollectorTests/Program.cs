using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Personal.Client;
using LmpClient;
using LmpClient.Aetherfront;

internal static class CollectorProgram
{
 static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static ScienceEvents collector;
 static void Call(string name){typeof(ScienceEvents).GetMethod(name,Private).Invoke(collector,null);}
 static T Field<T>(string name){return (T)typeof(ScienceOutbox).GetField(name,Private).GetValue(null);}
 static List<PendingScienceClaim> Claims {get{return Field<PendingScienceClaims>("pending").Claims;}}
 static ScienceSubject Subject(string id="test@MunInSpaceHigh",float science=0){return new ScienceSubject{id=id,science=science};}
 static void Reset(){
  if(collector!=null)Call("OnDestroy");GameEvents.OnScienceRecieved.Clear();
  foreach(var field in typeof(ScienceOutbox).GetFields(Private).Where(f=>!f.IsInitOnly))field.SetValue(null,field.FieldType.IsValueType?Activator.CreateInstance(field.FieldType):null);
  Field<IDictionary>("flights").Clear();var receipts=Field<object>("receipts");receipts.GetType().GetMethod("Clear").Invoke(receipts,null);
  PersonalClient.Active=true;PersonalClient.Credits.Clear();PersonalClient.Sends.Clear();
  MainSystem.UniqueIdentifier="fixture-account";AetherfrontClientSystem.State=new AetherfrontState{ServerId="fixture-server"};
  HighLogic.CurrentGame=new Game();KSPUtil.ApplicationRootPath=MonoGate.CaseRoot;
  UnityEngine.Time.frameCount=1;UnityEngine.Time.realtimeSinceStartup=0;UnityEngine.Debug.Messages.Clear();KesasInstall.Installer.Saves=0;
  collector=new ScienceEvents();Call("Awake");
  // Install once with the real shipping Harmony on this real KSP Mono runtime.
  if(!installed){Call("Update");MonoGate.Check((bool)typeof(ScienceEvents).GetField("subjectHookInstalled",Private).GetValue(collector),"All three genuine Harmony hooks installed: "+string.Join(" | ",UnityEngine.Debug.Messages.ToArray()));installed=true;}
  else typeof(ScienceEvents).GetField("subjectHookInstalled",Private).SetValue(collector,true);
 }
 static bool installed;
 static void Tick(){UnityEngine.Time.frameCount++;UnityEngine.Time.realtimeSinceStartup++;Call("Update");}
 static void Case(string name,Action test){MonoGate.Case("collector",name,test,Reset);}
 public static int Main(){
  Case("recovery-without-private-subject-getter-is-durable",()=>{
   var s=Subject();new ResearchAndDevelopment().SubmitScienceData(25,1,s,1,null,false);
   MonoGate.Check(File.Exists(Path.Combine(MonoGate.CaseRoot,"APF","PendingScience-v1.json")),"Saved during recovery, before any Update");
   MonoGate.Equal(1,Claims.Count,"One cumulative claim");MonoGate.Equal(25m,Claims[0].Amount,"Exact cumulative");Tick();MonoGate.Check(Claims.All(c=>c.Command==40),"No duplicate generic reward");
  });
  Case("transmission-shares-direct-submission-capture",()=>{
   var s=Subject();new ResearchAndDevelopment().SubmitScienceData(17,1,s,.5f,null,false);Tick();
   MonoGate.Equal(1,Claims.Count,"Transmission deduplicated");MonoGate.Equal(17m,Claims[0].Amount,"Transmission cumulative");
  });
  Case("stale-cache-restored-before-stock-reward-calculation",()=>{
   var s=Subject(science:0);PersonalClient.Credits[s.id]=80;var rd=new ResearchAndDevelopment();
   float award=rd.SubmitScienceData(100,1,s,1,null,false);
   MonoGate.Check(Math.Abs(award-20)<.001,"Stock GUI cannot promise already credited science");MonoGate.Equal(100m,Claims[0].Amount,"Canonical cumulative after history restore");
  });
  Case("pending-history-survives-scene-reload-before-receipt",()=>{
   var first=Subject();new ResearchAndDevelopment().SubmitScienceData(30,1,first,1,null,false);
   var reloaded=Subject();new ResearchAndDevelopment().SubmitScienceData(100,1,reloaded,1,null,false);
   MonoGate.Equal(1,Claims.Count,"Source monotonic merge");MonoGate.Equal(100m,Claims[0].Amount,"Unconfirmed local history not reset");
  });
  Case("earlier-event-handler-throws-without-losing-or-doubling-award",()=>{
   Call("OnDestroy");GameEvents.OnScienceRecieved.Add((a,b,c,d)=>{throw new InvalidOperationException("earlier mod callback");});Call("Awake");
   bool threw=false;try{new ResearchAndDevelopment().SubmitScienceData(25,1,Subject(),1,null,false);}catch(InvalidOperationException){threw=true;}
   MonoGate.Check(threw,"Original exception not suppressed");MonoGate.Equal(25m,Claims.Single().Amount,"Finalizer captured earned evidence");Tick();MonoGate.Equal(1,Claims.Count,"Exception does not add a generic duplicate");
  });
  Case("exception-before-wallet-award-cannot-mint-science",()=>{
   var rd=new ResearchAndDevelopment{ThrowBeforeAward=true};try{rd.SubmitScienceData(25,1,Subject(),1,null,false);}catch(InvalidOperationException){}
   Tick();MonoGate.Equal(0,Claims.Count,"No AddScience evidence means no award");
  });
  Case("currency-event-exception-after-wallet-mutation-is-durable",()=>{
   var rd=new ResearchAndDevelopment{ThrowInsideCurrencyEvent=true};bool threw=false;
   try{rd.SubmitScienceData(23,1,Subject(),1,null,false);}catch(InvalidOperationException){threw=true;}
   MonoGate.Check(threw,"Original currency exception retained");MonoGate.Equal(23m,Claims.Single().Amount,"Wallet award saved despite AddScience exception");
   Tick();MonoGate.Check(Claims.Count==1 && Claims[0].Command==40,"No generic duplicate after currency failure");
  });
  Case("AddScience-failure-before-wallet-mutation-cannot-mint-science",()=>{
   var rd=new ResearchAndDevelopment{ThrowBeforeWallet=true};try{rd.SubmitScienceData(23,1,Subject(),1,null,false);}catch(InvalidOperationException){}
   Tick();MonoGate.Equal(0,Claims.Count,"No wallet mutation means no science evidence");
  });
  Case("currency-exception-tiny-reward-at-large-cumulative-keeps-evidence",()=>{
   var subject=Subject(science:1000);subject.scienceCap=2000;
   var rd=new ResearchAndDevelopment{ThrowInsideCurrencyEvent=true};
   try{rd.SubmitScienceData(.0001f,1,subject,1,null,false);}catch(InvalidOperationException){}
   MonoGate.Check(Claims.Count==1 && Claims[0].Command==40 && Claims[0].Amount>1000,"Cumulative float ULP must not lose the subject receipt");
  });
  Case("disconnect-during-submission-keeps-original-identity",()=>{
   var rd=new ResearchAndDevelopment();rd.AfterAward=()=>{PersonalClient.Active=false;MainSystem.UniqueIdentifier="other-account";AetherfrontClientSystem.State.ServerId="other-server";};
   rd.SubmitScienceData(19,1,Subject(),1,null,false);
   MonoGate.Equal("fixture-account",Claims.Single().AccountId,"Captured account retained");MonoGate.Equal("fixture-server",Claims.Single().ServerId,"Captured server retained");
   Tick();MonoGate.Equal(0,PersonalClient.Sends.Count,"Old evidence cannot send as another account");
  });
  Case("nested-equal-rewards-and-outer-exception-do-not-double-credit",()=>{
   var rd=new ResearchAndDevelopment();bool nested=false;rd.AfterAward=()=>{if(nested)return;nested=true;new ResearchAndDevelopment().SubmitScienceData(10,1,Subject("nested@MunInSpaceHigh"),1,null,false);throw new InvalidOperationException("outer callback");};
   try{rd.SubmitScienceData(10,1,Subject(),1,null,false);}catch(InvalidOperationException){}
   Tick();MonoGate.Equal(2,Claims.Count,"Only two source claims");MonoGate.Check(Claims.All(c=>c.Command==40),"Nested award ownership cannot leave generic duplicate");
  });
  Case("complete-25-experiment-recovery-has-no-duplicate-awards",()=>{
   var rd=new ResearchAndDevelopment();for(int i=0;i<25;i++)rd.SubmitScienceData(10+i,1,Subject("experiment"+i+"@MunInSpaceHighHighlands"),1,null,false);
   MonoGate.Equal(25,Claims.Count,"Every experiment durable before scene transition");Tick();MonoGate.Equal(25,Claims.Count,"No generic fallback duplicates");MonoGate.Check(Claims.All(c=>c.Command==40),"All cumulative sources");
  });
  Case("lab-award-without-subject-still-uses-single-receipt",()=>{
   new ResearchAndDevelopment().AddScience(125,TransactionReasons.ScienceTransmission);Tick();MonoGate.Equal(1,Claims.Count,"One lab award");MonoGate.Equal(42,Claims[0].Command,"Generic reward path retained");MonoGate.Equal(125m,Claims[0].Amount,"Lab amount preserved");
  });
  Case("sandbox-cannot-create-science-claims",()=>{
   HighLogic.CurrentGame.Mode=Game.Modes.SANDBOX;new ResearchAndDevelopment().SubmitScienceData(25,1,Subject(),1,null,false);Tick();MonoGate.Check(Field<PendingScienceClaims>("pending")==null||Claims.Count==0,"Sandbox grant rejected");
  });
  return MonoGate.Finish("collector");
 }
}
