using System;
using System.Linq;
using System.Collections.Generic;
using Aetherfront.Core;
using Aetherfront.Personal.Client;

public static class Program
{
 const string Actor="rc64-research-fixture";
 static int checks;
 static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
 static AetherfrontState State()
 {
  var s=new AetherfrontState{ServerId="rc64-fixture"};s.PersonalProgression.StartingScience=10000;
  s.Accounts.Add(new AetherfrontAccountRecord{AccountId=Actor});
  for(int i=0;i<80;i++)s.TechnologyDefinitions.Add(new TechnologyDefinition{Id="node-"+i,ScienceCost=10});
  PersonalProgression.Ensure(s,Actor);return s;
 }
 static ResearchQueue Queue(AetherfrontState s){var q=new ResearchQueue();q.Context(s.ServerId,Actor,true);return q;}
 static PlayerProgression Player(AetherfrontState s)=>PersonalProgression.Find(s,Actor);
 public static int Main()
 {
  var s=State();var q=Queue(s);for(int i=0;i<64;i++)Check(q.Enqueue("node-"+i,10),"purchase intent beyond former 32-node cliff "+i);
  var projected=q.Project(s,Player(s),out var visible);
  Check(projected.Count==64&&visible==9360&&Player(s).Science==10000,"64-node projection reserves cost without spending authoritative science");
  for(int i=0;i<64;i++){
   var request=q.Step(s,Actor,i*2,i);
   Check(request!=null&&request.TechnologyId=="node-"+i&&PersonalProgression.UnlockTechnology(s,Actor,request.Amount2,request.TechnologyId).Accepted,"ordered authoritative purchase "+i);
  }
  Check(q.Step(s,Actor,130,65)==null&&q.Count==0&&Player(s).Science==9360,"64 purchases complete with exactly one charge each");
  s=State();q=Queue(s);q.Enqueue("node-0",10);var first=q.Step(s,Actor,0,0);
  q.Step(s,Actor,10,0);Check(q.SnapshotNeeded,"missing acknowledgement requests authoritative refresh");q.Step(s,Actor,11,1);
  var retry=q.Step(s,Actor,12,1);Check(retry.CommandId==first.CommandId&&retry.Amount2==first.Amount2,"same sequence retry retains exact idempotency identity");
  Check(PersonalProgression.UnlockTechnology(s,Actor,first.Amount2,first.TechnologyId).Accepted&&!PersonalProgression.UnlockTechnology(s,Actor,retry.Amount2,retry.TechnologyId).Accepted&&Player(s).Science==9990,"late original and retry cannot double-charge");
  q.Step(s,Actor,13,2);Check(q.Count==0,"late authoritative unlock retires pending intent");
  s=State();q=Queue(s);q.Enqueue("node-1",10);first=q.Step(s,Actor,0,0);q.Context(s.ServerId,Actor,false);
  Check(q.Count==1,"disconnect retains already-authorized research intent");q.Context(s.ServerId,Actor,true);
  Check(q.Step(s,Actor,2,0)==null&&q.SnapshotNeeded&&q.Step(s,Actor,3,0)==null,"reconnect cannot replay against cached pre-disconnect snapshot");
  retry=q.Step(s,Actor,4,1);Check(retry!=null&&retry.CommandId==first.CommandId,"fresh reconnect snapshot resumes exact unconfirmed purchase");
  var serialized=AetherfrontJson.Serialize(q.Export());var saved=AetherfrontJson.Deserialize<ResearchQueue.Intent[]>(serialized);
  var restored=Queue(s);restored.Restore(saved);Check(restored.Count==1&&restored.Export()[0].CommandId==retry.CommandId,"process-restart journal round trip retains command identity and cost ceiling");
  Check(restored.Step(s,Actor,0,0)==null&&restored.SnapshotNeeded,"restored process also requires a fresh server snapshot");
  restored.Context("different-server",Actor,true);Check(restored.Count==0,"pending purchases never migrate to another server");
  s=State();q=Queue(s);q.Enqueue("node-0",10);q.Step(s,Actor,0,0);q.Step(s,Actor,10,0);q.Step(s,Actor,30,0);
  projected=q.Project(s,Player(s),out visible);Check(q.Count==1&&projected.Contains("node-0")&&visible==9990,"slow confirmation does not unresearch projected node or lose intent");
  Check(q.Step(s,Actor,45,1)!=null,"slow confirmation recovers automatically without manual re-purchase");
  s=State();q=Queue(s);q.Enqueue("node-0",10);s.TechnologyDefinitions[0].ScienceCost=11;
  Check(q.Step(s,Actor,0,0)==null&&q.Count==0&&Player(s).Science==10000&&q.Warning.Contains("price changed"),"changed price requires new user approval and cannot silently spend more");
  q=Queue(s);for(int i=0;i<ResearchQueue.MaximumPending+1;i++)q.Enqueue("bounded-"+i,10);
  Check(q.Count==ResearchQueue.MaximumPending&&q.Warning.Contains("safety limit"),"large pending list remains explicitly bounded");
  var before=q.Export();bool rejected=false;try{q.Restore(new[]{new ResearchQueue.Intent{TechnologyId="x",MaximumCost=-1}});}catch(ArgumentException){rejected=true;}
  Check(rejected&&q.Count==before.Length,"invalid persisted intent rejected before mutating retained list");
  var sync=typeof(ResearchQueue).Assembly.GetType("Aetherfront.Personal.Client.ResearchSync");
  if(sync!=null){
   var flags=System.Reflection.BindingFlags.NonPublic;
   var journalType=sync.GetNestedType("Journal",flags);var contextType=sync.GetNestedType("SavedContext",flags);
   var journal=Activator.CreateInstance(journalType,true);var context=Activator.CreateInstance(contextType,true);
   contextType.GetProperty("ServerId").SetValue(context,s.ServerId,null);contextType.GetProperty("AccountId").SetValue(context,Actor,null);
   contextType.GetProperty("Intents").SetValue(context,new[]{new ResearchQueue.Intent{TechnologyId="node-1",MaximumCost=10,CommandId="research:"+Guid.NewGuid().ToString("N"),Sequence=17}},null);
   ((System.Collections.IList)journalType.GetProperty("Contexts").GetValue(journal,null)).Add(context);
   string text=AetherfrontJson.Serialize(journal);
   var deserialize=typeof(AetherfrontJson).GetMethods().Single(m=>m.Name=="Deserialize"&&m.IsGenericMethodDefinition).MakeGenericMethod(journalType);
   var decoded=deserialize.Invoke(null,new object[]{text});
   Check((int)journalType.GetProperty("Version").GetValue(decoded,null)==1,"actual grafted private journal schema survives JsonFx round trip");
   var decodedContext=((System.Collections.IList)journalType.GetProperty("Contexts").GetValue(decoded,null))[0];
   var decodedIntents=(ResearchQueue.Intent[])contextType.GetProperty("Intents").GetValue(decodedContext,null);
   Check(decodedIntents.Length==1&&decodedIntents[0].MaximumCost==10&&decodedIntents[0].Sequence==17,"actual shipping journal retains exact consent and replay sequence");
  }
  Console.WriteLine("PASS "+checks+" RC64 research assertions; no production writes.");return 0;
 }
}
