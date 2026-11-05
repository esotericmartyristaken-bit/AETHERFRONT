using System.Text.Json.Nodes;
using Aetherfront.Core;
using Server.Aetherfront;

if(args.Length!=2 || Directory.Exists(args[0]))throw new ArgumentException("NEW_PRIVATE_ROOT READONLY_RUNTIME");
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(c,n)=>{string p=Path.Combine(args[1],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
string id="key-"+new string('1',64),root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
int checks=0;void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
var store=new AetherfrontServerStore(root,"fixture-science");
store.Execute(s=>{s.Accounts.Add(new AetherfrontAccountRecord{AccountId=id,DisplayName="Fixture"});return AetherfrontResult.Success(s.Revision);});
store.Execute(s=>PersonalProgression.RecordEarnedScience(s,id,"science:test",100,100));
string path=Path.Combine(root,"Aetherfront/state.json"),before=File.ReadAllText(path);
string plan=new JsonObject{{"schemaVersion",1},{"applicationVersion","kesas-rc48"},{"serverId","fixture-science"},{"transactionId","fixture-942"},{"accountId",id},{"playerName","Fixture"},{"amount",942}}.ToJsonString();
string bindings=new JsonObject{{"version",1},{"accounts",new JsonArray(new JsonObject{{"accountId",id},{"playerNames",new JsonArray("Fixture")}})}}.ToJsonString();
string next=ScienceCompensation.Build(before,bindings,plan,"kesas-rc48");
File.WriteAllText(path,next);
var reloaded=new AetherfrontServerStore(root,"fixture-science");var player=PersonalProgression.Find(reloaded.ReadSnapshot(),id);
Check(player.Science==1042 && player.EarnedScience==100 && player.TradableScience==100,"Compensation lost or changed earned science on startup");
Check(reloaded.Execute(s=>PersonalProgression.RecordEarnedScience(s,id,"science:test",100,100)).Accepted,"Existing source replay rejected");
Check(PersonalProgression.Find(reloaded.ReadSnapshot(),id).Science==1042,"Replay duplicated science");
Check(reloaded.Execute(s=>PersonalProgression.RecordEarnedScience(s,id,"science:new",5,5)).Accepted,"Post-compensation mutation failed");
var again=new AetherfrontServerStore(root,"fixture-science");player=PersonalProgression.Find(again.ReadSnapshot(),id);
Check(player.Science==1047 && player.EarnedScience==105 && player.TradableScience==105,"Credit did not survive durable subsequent save/reload");
Console.WriteLine($"PASS actual-native-store-compensation-reload {checks} checks");
