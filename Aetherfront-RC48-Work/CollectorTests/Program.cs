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
 static ProtoVessel Vessel(string id="test@MunInSpaceHigh",float amount=25){var vessel=new ProtoVessel();var part=new ProtoPartSnapshot();var module=new ProtoPartModuleSnapshot();var data=module.moduleValues.AddNode("ScienceData");data.AddValue("subjectID",id);data.AddValue("data",amount.ToString(System.Globalization.CultureInfo.InvariantCulture));part.modules.Add(module);vessel.parts.Add(part);return vessel;}
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
  Case("reconnect-missing-RnD-subject-cache-does-not-skip-recovery",()=>{
   var rd=new ResearchAndDevelopment();rd.Recover(Vessel());MonoGate.Equal(25m,Claims.Single().Amount,"Previously stock skipped the data before any capture hook");
   MonoGate.Check(Directory.GetFiles(Path.Combine(MonoGate.CaseRoot,"APF","RecoveryEvidence-v1")).Length==1,"Raw carried science saved before recovery callbacks");
  });
  Case("recovered-subject-restores-personal-high-water-before-award",()=>{
   PersonalClient.Credits["test@MunInSpaceHigh"]=80;var rd=new ResearchAndDevelopment();rd.Recover(Vessel(amount:100));
   MonoGate.Check(Math.Abs(rd.Science-20)<.001,"Only unearned remainder granted by stock");MonoGate.Equal(100m,Claims.Single().Amount,"Cumulative high-water retained");
  });
  Case("unscoped-award-flushed-after-disconnect-is-durable",()=>{
   new ResearchAndDevelopment().AddScience(13,TransactionReasons.ScienceTransmission);PersonalClient.Active=false;Tick();
   MonoGate.Check(File.Exists(Path.Combine(MonoGate.CaseRoot,"APF","PendingScience-v1.json")),"Inactive Tick must not discard dirty persistence");MonoGate.Equal(13m,Claims.Single().Amount,"Late lab award retained");
  });
  Case("award-completing-after-disconnect-keeps-bound-game-identity",()=>{
   PersonalClient.Active=false;new ResearchAndDevelopment().AddScience(17,TransactionReasons.ScienceTransmission);Tick();
   MonoGate.Equal(17m,Claims.Single().Amount,"Same multiplayer Game retains capture during disconnect");MonoGate.Equal("fixture-account",Claims.Single().AccountId,"Bound account remains original");
  });
  Case("disconnected-new-singleplayer-game-cannot-borrow-identity",()=>{
   PersonalClient.Active=false;HighLogic.CurrentGame=new Game();new ResearchAndDevelopment().AddScience(17,TransactionReasons.ScienceTransmission);Tick();
   MonoGate.Check(Field<PendingScienceClaims>("pending")==null||Claims.Count==0,"Different Game cannot mint multiplayer claims");
  });
  Case("recovery-lab-evidence-saved-before-next-Update",()=>{
   var rd=new ResearchAndDevelopment{LabAward=31};rd.AfterRecovery=()=>{PersonalClient.Active=false;};rd.Recover(new ProtoVessel());
   MonoGate.Equal(31m,Claims.Single().Amount,"Recovery finalizer flushes non-subject lab science immediately");
   MonoGate.Check(File.Exists(Path.Combine(MonoGate.CaseRoot,"APF","PendingScience-v1.json")),"No later active Update required");
  });
  Case("interrupted-recovery-preserves-unprocessed-raw-experiments",()=>{
   var vessel=Vessel(amount:11);var extra=Vessel(amount:19);vessel.parts.AddRange(extra.parts);var rd=new ResearchAndDevelopment{ThrowAfterAward=true};
   try{rd.Recover(vessel);}catch(InvalidOperationException){}
   var file=Directory.GetFiles(Path.Combine(MonoGate.CaseRoot,"APF","RecoveryEvidence-v1")).Single();var text=File.ReadAllText(file);
   MonoGate.Check(text.Contains("data=11")&&text.Contains("data=19"),"Even an interrupted batch retains the remaining experiment evidence");
   MonoGate.Equal(11m,Claims.Single().Amount,"Unprocessed evidence is not automatically minted as science");
  });
  Case("unknown-or-source-UID-subject-is-preserved-not-fabricated",()=>{
   new ResearchAndDevelopment().Recover(Vessel("unknown@MunInSpaceHigh_asteroid-uid"));
   MonoGate.Check(Field<PendingScienceClaims>("pending")==null||Claims.Count==0,"Unknown subject cannot mint a wallet award");
   MonoGate.Check(Directory.GetFiles(Path.Combine(MonoGate.CaseRoot,"APF","RecoveryEvidence-v1")).Length==1,"Unresolved data still recoverable by operator");
  });
  Case("mirror-bootstrap-does-not-create-duplicate-collectors",()=>{
   var method=typeof(ScienceEvents).GetMethod("EnsureStarted");MonoGate.Check(method!=null,"Runtime fallback exists in addition to addon metadata");
   int before=UnityEngine.GameObject.Created;method.Invoke(null,null);method.Invoke(null,null);MonoGate.Equal(before,UnityEngine.GameObject.Created,"Existing collector retained");
  });
  Case("collector-recreation-does-not-stack-Harmony-award-hooks",()=>{
   Call("OnDestroy");collector=new ScienceEvents();Call("Awake");Call("Update");
   var target=typeof(ResearchAndDevelopment).GetMethod("AddScience");var info=HarmonyLib.Harmony.GetPatchInfo(target);
   MonoGate.Equal(1,info.Prefixes.Count(p=>p.owner=="aetherfront.personal.subject-history"),"Only one native award prefix after recreation");
   MonoGate.Equal(1,info.Finalizers.Count(p=>p.owner=="aetherfront.personal.subject-history"),"Only one native award finalizer after recreation");
   new ResearchAndDevelopment().AddScience(7,TransactionReasons.ScienceTransmission);Tick();MonoGate.Equal(7m,Claims.Single().Amount,"Lab award not doubled after lifecycle recovery");
  });
  Case("valid-bounded-outbox-over-four-MiB-remains-reloadable",()=>{
   var state=new PendingScienceClaims();for(int i=0;i<4096;i++)state.Claims.Add(new PendingScienceClaim{ServerId=new string('s',256),AccountId=new string('a',256),SourceId=i.ToString().PadRight(256,'x'),Command=40,Amount=1,LastRejection=new string('r',128),HeldVersion=new string('v',64)});
   string path=Path.Combine(MonoGate.CaseRoot,"APF","PendingScience-v1.json");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,AetherfrontJson.Serialize(state));
   MonoGate.Check(new FileInfo(path).Length>4*1024*1024,"Fixture exceeds former inconsistent reader limit");
   bool loaded=(bool)typeof(ScienceOutbox).GetMethod("Load",Private).Invoke(null,null);MonoGate.Check(loaded,"Valid bounded rewards must remain readable after restart");MonoGate.Equal(4096,Claims.Count,"Every existing reward retained");
  });
  return MonoGate.Finish("collector");
 }
}
