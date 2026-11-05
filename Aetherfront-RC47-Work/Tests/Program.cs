using System.Diagnostics;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Continuity;
using Server.Aetherfront;

if(args.Length!=2||!Path.IsPathRooted(args[0])||!Path.IsPathRooted(args[1])||Directory.Exists(args[0]))throw new ArgumentException("NEW_PRIVATE_EVIDENCE READONLY_RUNTIME");
string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(c,n)=>{string p=Path.Combine(args[1],n.Name+".dll");return File.Exists(p)?c.LoadFromAssemblyPath(p):null;};
int checks=0,cases=0;
void Check(bool condition,string reason){checks++;if(!condition)throw new Exception(reason);}
void Case(string name,Action<string> body){string p=Path.Combine(root,name);Directory.CreateDirectory(p);body(p);cases++;Console.WriteLine("PASS "+name);}
Case("clock-subsecond-corrections-never-accumulate-backward",p=>{
 var c=new ClockStore(p);c.Resume("clock",564971196.6980764);
 for(int i=0;i<100;i++)c.Checkpoint("clock",564971196.0922885);
 Check(c.Resume("clock",0)==564971196.6980764,"Small corrections decreased high-water");
 c.Checkpoint("clock",564971194.6980764);Check(c.Resume("clock",0)==564971196.6980764,"Two-second boundary");
 bool rejected=false;try{c.Checkpoint("clock",564971194.6);}catch(InvalidDataException){rejected=true;}
 Check(rejected&&c.Resume("clock",0)==564971196.6980764,"Accumulating rollback not rejected");
 foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d,ClockStore.MaximumTime+1}){rejected=false;try{c.Checkpoint("clock",bad);}catch(InvalidDataException){rejected=true;}Check(rejected,"Invalid clock accepted");}
});
Case("actual-store-science-purchase-replay-and-reload",p=>{
 var store=new AetherfrontServerStore(p,"fixture");
 store.Execute(s=>{s.Accounts.Add(new AetherfrontAccountRecord{AccountId="alice"});s.Accounts.Add(new AetherfrontAccountRecord{AccountId="bob"});s.TechnologyDefinitions.Add(new TechnologyDefinition{Id="node",ScienceCost=20});return AetherfrontResult.Success(s.Revision);});
 Check(store.Execute(s=>PersonalProgression.RecordEarnedScience(s,"alice","subject",100,100)).Accepted,"Award failed");
 Check(store.Execute(s=>PersonalProgression.UnlockTechnology(s,"alice",1,"node")).Accepted,"Purchase failed");
 Check(!store.Execute(s=>PersonalProgression.UnlockTechnology(s,"alice",1,"node")).Accepted,"Replay accepted");
 var reload=new AetherfrontServerStore(p,"fixture");var state=reload.ReadSnapshot();var a=PersonalProgression.Ensure(state,"alice");var b=PersonalProgression.Ensure(state,"bob");
 Check(a.Science==80&&a.EarnedScience==100&&a.NextCommandSequence==2&&a.TechnologyUnlocks.Contains("node"),"Durable science/purchase lost");Check(b.Science==50,"Shared half lost");
 Check(reload.Execute(s=>PersonalProgression.RecordEarnedScience(s,"alice","subject",100,100)).Accepted&&PersonalProgression.Find(reload.ReadSnapshot(),"alice").Science==80,"Replayed transmission minted science");
});
Case("actual-store-transient-save-lock",p=>{
 var store=new AetherfrontServerStore(p,"fixture");string file=Path.Combine(p,"Aetherfront","state.json");Task<AetherfrontResult> task;
 using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
  task=Task.Run(()=>store.Execute(s=>{s.Revision++;s.Accounts.Add(new AetherfrontAccountRecord{AccountId="saved"});return AetherfrontResult.Success(s.Revision);}));
  Check(!task.Wait(150),"Transient lock was not retried");Check(!File.ReadAllText(file).Contains("saved"),"Uncommitted state visible");
 }
 Check(task.Wait(3000)&&task.Result.Accepted&&store.ReadSnapshot().Accounts.Count==1,"Released lock failed to commit");
 Check(new AetherfrontServerStore(p,"fixture").ReadSnapshot().Accounts.Count==1,"Committed replacement lost on reload");
});
Case("actual-store-permanent-save-failure-no-false-commit",p=>{
 var store=new AetherfrontServerStore(p,"fixture");string file=Path.Combine(p,"Aetherfront","state.json");byte[] before=File.ReadAllBytes(file);var watch=Stopwatch.StartNew();bool failed=false;
 using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
  try{store.Execute(s=>{s.Revision++;return AetherfrontResult.Success(s.Revision);});}catch(IOException){failed=true;}
  Check(before.SequenceEqual(File.ReadAllBytes(file)),"Failed write changed primary");
 }
 Check(failed&&watch.ElapsedMilliseconds<2500&&store.ReadSnapshot().Revision==0,"Failed write committed or blocked indefinitely");Check(!Directory.GetFiles(Path.GetDirectoryName(file),"*.tmp").Any(),"Temporary file leaked");
});
Console.WriteLine($"RESULT {cases} cases / {checks} checks");
