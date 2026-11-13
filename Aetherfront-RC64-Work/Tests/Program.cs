using System.Diagnostics;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Synchronization.Server;
using Server.Aetherfront;

string root=Path.GetFullPath(args.Single());
if(!root.StartsWith(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC64-FullChain-20261113\Tests-",StringComparison.OrdinalIgnoreCase)||Directory.Exists(root))throw new IOException("Fresh isolated RC64 fixture required.");
Directory.CreateDirectory(root);
int checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
var store=new AetherfrontServerStore(root,"rc64-journal-fixture");
long validations=RecoveryJournal.BootstrapValidations;
var counter=typeof(AetherfrontServerStore).GetField("_journalAppendsSinceCompaction",BindingFlags.Instance|BindingFlags.NonPublic)!;
var times=new List<double>();
for(int i=0;i<80;i++){
 var watch=Stopwatch.StartNew();
 var result=store.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});
 times.Add(watch.Elapsed.TotalMilliseconds);
 Check(result.Accepted,"atomic revision commit "+i);
}
counter.SetValue(store,63);
store.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});
string journal=Path.Combine(root,"Aetherfront/events.jsonl");
var frames=File.ReadLines(journal).Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray();
Check(frames.Length==8,"compaction retains exactly eight committed recovery frames");
Check(RecoveryJournal.BootstrapValidations==validations,"hot compaction never deserializes historical full-state graphs");
Check(RecoveryJournal.Compactions>=2,"threshold and explicit maintenance compaction exercised");
var sequences=frames.Select(x=>long.Parse(Aetherfront.Synchronization.JsonSlices.Object(x)["CommitSequence"])).ToArray();
Check(sequences.SequenceEqual(sequences.Order())&&sequences.Distinct().Count()==8,"recovery commit ordering and identity retained");
long revision=store.ReadSnapshot().Revision;
var restored=new AetherfrontServerStore(root,"rc64-journal-fixture");
Check(restored.ReadSnapshot().Revision==revision,"fresh process-equivalent store reconstructs latest durable state");
Check(RecoveryJournal.BootstrapValidations-validations==8,"existing recovery history validated once at bootstrap");
File.AppendAllText(journal,"\n{broken trailing recovery record\n");
var trailing=new AetherfrontServerStore(root,"rc64-journal-fixture");
Check(trailing.ReadSnapshot().Revision==revision,"malformed trailing frame cannot roll back primary state");
var corrupt=frames.Select(line=>{
 var frame=System.Text.Json.Nodes.JsonNode.Parse(line)!.AsObject();frame["StateJson"]="{broken state";return frame.ToJsonString();
}).ToArray();
File.WriteAllLines(journal,corrupt);
var degraded=new AetherfrontServerStore(root,"rc64-journal-fixture");
Check(degraded.ReadSnapshot().Revision==revision,"valid primary boots despite eight corrupt optional recovery states");
counter.SetValue(degraded,63);degraded.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});revision++;
Check(File.ReadLines(journal).Count()>8,"degraded recovery history retained until eight trusted frames accumulate");
for(int i=0;i<7;i++){degraded.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});revision++;}
counter.SetValue(degraded,63);degraded.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});revision++;
Check(File.ReadLines(journal).Count()==8&&!File.ReadAllText(journal).Contains("broken state"),"trusted live frames safely replenish and compact degraded recovery history");
trailing=degraded;
string primary=Path.Combine(root,"Aetherfront/state.json");
using(var locked=new FileStream(primary,FileMode.Open,FileAccess.Read,FileShare.Read)){
 bool rejected=false;try{trailing.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});}catch(IOException){rejected=true;}catch(UnauthorizedAccessException){rejected=true;}
 Check(rejected&&trailing.ReadSnapshot().Revision==revision,"failed primary replacement never acknowledges or advances in-memory state");
}
Console.WriteLine("MEASURE commitMaxMilliseconds="+times.Max().ToString("F3")+"; commits="+times.Count+"; bootstrapValidations="+RecoveryJournal.BootstrapValidations);
Console.WriteLine("PASS "+checks+" RC64 journal assertions; no production writes.");
