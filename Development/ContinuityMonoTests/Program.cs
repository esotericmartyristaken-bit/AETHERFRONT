using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using LifeSupport;
using Aetherfront.Continuity.Client;

internal static class Program
{
    static int checks;
    static string candidatePath;
    static readonly Dictionary<string,string> Lookup=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    static void Check(bool test,string name){checks++;if(!test)throw new Exception(name);}
    static void Main()
    {
        string game=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        string candidate=Environment.GetEnvironmentVariable("APF_CONTINUITY_CANDIDATE");
        if(!Directory.Exists(game)||!Directory.Exists(candidate))throw new ArgumentException("Explicit owned runtime and candidate required");
        candidatePath=candidate;
        foreach(var path in Directory.GetFiles(Path.Combine(game,"GameData"),"*.dll",SearchOption.AllDirectories))Lookup[Path.GetFileNameWithoutExtension(path)]=path;
        foreach(var path in Directory.GetFiles(Path.Combine(game,"KSP_x64_Data/Managed"),"*.dll"))Lookup[Path.GetFileNameWithoutExtension(path)]=path;
        AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
        string audit=Environment.GetEnvironmentVariable("APF_DF_TIMER_AUDIT");
        if(!string.IsNullOrEmpty(audit)){DeepFreezeTimerAudit.Write(game,audit);return;}
        Run(game);
    }
    static Assembly Resolve(object sender,ResolveEventArgs arguments)
    {
        string name=new AssemblyName(arguments.Name).Name;
        string path=Path.Combine(candidatePath,name+".dll");
        if(!File.Exists(path) && !Lookup.TryGetValue(name,out path))return null;
        return Assembly.LoadFrom(path);
    }
    static void Run(string game)
    {
        var now=AccessTools.Method(typeof(Planetarium),nameof(Planetarium.GetUniversalTime),Type.EmptyTypes);
        var mission=AccessTools.Field(typeof(Vessel),nameof(Vessel.missionTime));
        Check(now!=null&&now.IsStatic&&now.ReturnType==typeof(double),"actual KSP clock signature");
        Check(mission!=null&&mission.FieldType==typeof(double),"actual KSP mission time is a field, not a property");
        var assemblies=new[]{
            Assembly.LoadFrom(Path.Combine(game,"GameData/UmbraSpaceIndustries/LifeSupport/USILifeSupport.dll")),
            Assembly.LoadFrom(Path.Combine(game,"GameData/000_USITools/USITools.dll")),
            Assembly.LoadFrom(Path.Combine(game,"GameData/UmbraSpaceIndustries/MKS/KolonyTools.dll"))};
        int transformed=0;
        foreach(var method in assemblies.SelectMany(a=>a.GetTypes()).Concat(new[]{typeof(BaseConverter),typeof(ModuleGenerator)})
            .SelectMany(t=>AccessTools.GetDeclaredMethods(t)).Where(m=>m.GetMethodBody()!=null&&!m.ContainsGenericParameters))
        {
            var original=PatchProcessor.GetOriginalInstructions(method);
            int calls=original.Count(c=>c.Calls(now)||(c.opcode==System.Reflection.Emit.OpCodes.Ldfld&&Equals(c.operand,mission)));
            if(calls==0)continue;
            var rewritten=SimulationAuthority.ScopedTime(original).ToArray();
            Check(!rewritten.Any(c=>c.Calls(now)||(c.opcode==System.Reflection.Emit.OpCodes.Ldfld&&Equals(c.operand,mission))),"actual time call rewritten: "+method.DeclaringType.FullName+"."+method.Name);
            Check(rewritten.Count(c=>c.operand is MethodInfo && ((MethodInfo)c.operand).DeclaringType==typeof(SurvivalClock))>=calls,"replacement clock call count");
            transformed++;
        }
        Check(transformed>10,"actual shipped USI timer coverage");
        var write=typeof(SurvivalClock).GetMethod("Write",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(typeof(LifeSupportStatus));
        var read=typeof(SurvivalClock).GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(typeof(LifeSupportStatus));
        var input=new LifeSupportStatus{KerbalName="Clock Test Kerbal",LastMeal=12345.678901,LastEC=2222,LastUpdate=987654321,LastAtHome=99,LastSOIChange=555,MaxOffKerbinTime=1e100,TimeEnteredVessel=777,CurrentVesselId=Guid.NewGuid().ToString(),PreviousVesselId="unknown",OldTrait="Pilot",IsGrouchy=true,HomeBodyId=1,LastPlanet=7};
        var node=new ConfigNode("CREW");write.Invoke(null,new object[]{node,input});
        var result=(LifeSupportStatus)read.Invoke(null,new object[]{node.CreateCopy()});
        foreach(var property in typeof(LifeSupportStatus).GetProperties())Check(Equals(property.GetValue(input,null),property.GetValue(result,null)),"crew state round trip: "+property.Name);
        foreach(var mutate in new Action<ConfigNode>[] {n=>n.AddValue("LastMeal","123"),n=>n.SetValue("LastMeal","NaN"),n=>n.SetValue("KerbalName",new string('x',257)),n=>n.AddNode("unrecognized")})
        {
            var bad=node.CreateCopy();mutate(bad);bool rejected=false;
            try{read.Invoke(null,new object[]{bad});}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
            Check(rejected,"malformed crew snapshot refused");
        }
        ClientWire();
        ValidateResourceEnvelope();
        ResourceTopologyRace();
        KerbalStatsSeats(game);
        TopologyEventTargets();
        SplitInitializationAndCleanup();
        ProbeRoomCleanup(game);
        MapCleanup(game);
        MapUpdateEntry(game);
        ScattererSettingsShutdown(game);
        TrackingLabels();
        CapturedDockingEpochs();
        SoundPrefabs(game);
        DeepFreezeRosterTransitions(game);
        NativeFrozenMerge();
        DeepFreezeAuthorityGates();
        CryopodClockDomains(game);
        DisconnectCleanupOrder();
        CryopodComputer(game);
        DeepFreezeBackgroundPatches();
        DeepFreezeBatchAcknowledgements();
        DeferredTrackingCheckpoint();
        Console.WriteLine("PASS "+checks+" actual-KSP-Mono signature, timer-IL and crew serialization assertions; "+transformed+" timer methods. No Unity scene/gameplay claim.");
    }
    static void DeferredTrackingCheckpoint()
    {
        var clock=(SurvivalClock)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SurvivalClock));
        var check=AccessTools.Method(typeof(SurvivalClock),"CanRetainDeferredSnapshot");
        var incoming=AccessTools.Field(typeof(SurvivalClock),"incoming");
        var epoch=AccessTools.Field(typeof(SurvivalClock),"epoch");
        var restored=AccessTools.Field(typeof(SurvivalClock),"restored");
        Func<string,bool> allowed=owner=>(bool)check.Invoke(clock,new object[]{owner});
        Check(!allowed("Player"),"missing Tracking snapshot cannot be accepted");
        var saved=new ConfigNode("APF_SURVIVAL");
        saved.AddValue("schema","3");saved.AddValue("owner","Player");saved.AddValue("ownerUT","2000");
        saved.AddValue("survivalUT","500");saved.AddValue("launchUT","0");saved.AddValue("freezeOffset","1500");
        incoming.SetValue(clock,saved);epoch.SetValue(clock,new Aetherfront.Continuity.SurvivalEpoch("Player",2000,500));
        string original=saved.ToString();
        Check(allowed("Player"),"validated un-restored Tracking checkpoint remains saveable without USI initialization");
        Check(saved.ToString()==original,"deferred save does not advance timers or change survival data");
        Check(!allowed("ClockBob"),"deferred checkpoint cannot be saved under a different owner");
        restored.SetValue(clock,true);Check(!allowed("Player"),"live restored state cannot fall back to an old snapshot");
        restored.SetValue(clock,false);epoch.SetValue(clock,new Aetherfront.Continuity.SurvivalEpoch("Player",2001,500));
        Check(!allowed("Player"),"changed owner clock cannot masquerade as untouched deferred data");
        epoch.SetValue(clock,new Aetherfront.Continuity.SurvivalEpoch("Player",2000,501));
        Check(!allowed("Player"),"changed virtual clock cannot discard simulation through deferred save");
        epoch.SetValue(clock,new Aetherfront.Continuity.SurvivalEpoch("Player",2000,500));saved.SetValue("freezeOffset","NaN");
        bool rejected=false;try{allowed("Player");}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
        Check(rejected,"corrupt deferred snapshot still fails closed");
    }
    static void DisconnectCleanupOrder()
    {
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.DisconnectLifecycle",true);
        var stop=AccessTools.Method(AccessTools.TypeByName("LmpClient.MainSystem"),"StopGame");
        var original=PatchProcessor.GetOriginalInstructions(stop).ToArray();
        var changed=((IEnumerable<CodeInstruction>)AccessTools.Method(implementation,"DeferredCleanup").Invoke(null,new object[]{original})).ToArray();
        Check(changed.Count(c=>(c.operand as MethodInfo)?.DeclaringType==implementation&&(c.operand as MethodInfo)?.Name=="RetainList")==3,"native multiplayer stop defers all three vessel-list clears");
        Check(changed.Count(c=>(c.operand as MethodInfo)?.DeclaringType==implementation&&(c.operand as MethodInfo)?.Name=="RetainActive")==1,"native multiplayer stop preserves active vessel through scene-exit callbacks");
        Check(changed.Count(c=>c.Calls(AccessTools.Method(typeof(HighLogic),"LoadScene",new[]{typeof(GameScenes)})))==original.Count(c=>c.Calls(AccessTools.Method(typeof(HighLogic),"LoadScene",new[]{typeof(GameScenes)}))),"native scene request remains in disconnect path");
        bool refused=false;try{AccessTools.Method(implementation,"DeferredCleanup").Invoke(null,new object[]{Array.Empty<CodeInstruction>()});}catch(TargetInvocationException e){refused=e.InnerException is InvalidOperationException;}
        Check(refused,"disconnect compatibility refuses unreviewed native method shape");
        var listener=implementation.GetNestedType("Callbacks",BindingFlags.NonPublic);
        Check(listener!=null&&!AccessTools.Method(listener,"Loaded").IsStatic,"deferred cleanup uses a valid instance target for KSP EventData callbacks");
    }
    static void DeepFreezeBackgroundPatches()
    {
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.DeepFreezeBackgroundSave",true);
        var protocol=AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeClientSystem");
        var capture=AccessTools.DeclaredMethod(protocol,"CaptureAndSend",Type.EmptyTypes);
        var query=AccessTools.Method(typeof(LmpCommon.Locks.LockQuery),"ControlLockBelongsToPlayer",new[]{typeof(Guid),typeof(string)});
        var harmony=new Harmony("aetherfront.fixture.mono-freeze-background-save");
        try
        {
            AccessTools.Method(implementation,"Install").Invoke(null,new object[]{harmony});
            Check(Harmony.GetPatchInfo(capture).Prefixes.Any(p=>p.owner==harmony.Id),"native frozen save capture receives background scope hook");
            Check(Harmony.GetPatchInfo(capture).Finalizers.Any(p=>p.owner==harmony.Id),"native frozen save clears scoped authority after exceptions");
            Check(Harmony.GetPatchInfo(query).Prefixes.Any(p=>p.owner==harmony.Id),"reviewed client lock predicate patch signatures compile against actual runtime");
            Check(Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id))==5,"background save patches exactly capture, scoped lock, acknowledgement, disconnect barrier and reset");
        }
        finally{harmony.UnpatchAll(harmony.Id);}
    }
    static void DeepFreezeBatchAcknowledgements()
    {
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.DeepFreezeBackgroundSave",true);
        var protocol=AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeClientSystem");
        var pending=AccessTools.Field(protocol,"pendingId");
        var recapture=AccessTools.Property(protocol,"NeedsRecapture");var error=AccessTools.Property(protocol,"LastSaveError");
        var drain=AccessTools.Field(implementation,"drain");var batch=AccessTools.Field(implementation,"drainBatch");
        var after=AccessTools.Method(implementation,"AfterApply");
        object oldPending=pending.GetValue(null),oldRecapture=recapture.GetValue(null,null),oldError=error.GetValue(null,null);
        try
        {
            var ids=Enumerable.Range(0,129).Select(_=>Guid.NewGuid()).ToArray();
            drain.SetValue(null,ids);batch.SetValue(null,ids.Take(64).ToArray());
            pending.SetValue(null,Guid.Empty);recapture.SetValue(null,false,null);error.SetValue(null,null,null);
            after.Invoke(null,new object[]{false});
            Check(((Guid[])drain.GetValue(null)).Length==129,"unsolicited shared checkpoint cannot acknowledge local drain batch");
            pending.SetValue(null,Guid.NewGuid());after.Invoke(null,new object[]{true});
            Check(((Guid[])drain.GetValue(null)).Length==129,"pending save retains every queued cryopod");
            pending.SetValue(null,Guid.Empty);recapture.SetValue(null,true,null);after.Invoke(null,new object[]{true});
            Check(((Guid[])drain.GetValue(null)).Length==129,"stale revision keeps exact batch for retry");
            recapture.SetValue(null,false,null);error.SetValue(null,"Rejected",null);after.Invoke(null,new object[]{true});
            Check(((Guid[])drain.GetValue(null)).Length==129,"rejected save cannot discard queued cryopods");
            error.SetValue(null,null,null);after.Invoke(null,new object[]{true});
            Check(((Guid[])drain.GetValue(null)).SequenceEqual(ids.Skip(64)),"accepted save removes only its first64 cryopods");
            Check((bool)recapture.GetValue(null,null),"more than64 cryopods require another acknowledged disconnect batch");
            recapture.SetValue(null,false,null);batch.SetValue(null,ids.Skip(64).Take(64).ToArray());after.Invoke(null,new object[]{true});
            Check(((Guid[])drain.GetValue(null)).SequenceEqual(ids.Skip(128)),"second acknowledged batch leaves final cryopod queued");
            recapture.SetValue(null,false,null);batch.SetValue(null,ids.Skip(128).ToArray());after.Invoke(null,new object[]{true});
            Check(drain.GetValue(null)==null&&batch.GetValue(null)==null,"last accepted batch completes drain without a phantom restart or acknowledgement");
        }
        finally
        {
            AccessTools.Method(implementation,"Reset").Invoke(null,null);
            pending.SetValue(null,oldPending);recapture.SetValue(null,oldRecapture,null);error.SetValue(null,oldError,null);
        }
    }
    static void CryopodClockDomains(string game)
    {
        var offset=AccessTools.Method(typeof(SurvivalClock),"SnapshotFreezeOffset");
        var legacy=new ConfigNode("APF_SURVIVAL");
        legacy.AddValue("schema","2");legacy.AddValue("owner","Player");
        legacy.AddValue("ownerUT","17788.075351305884");legacy.AddValue("survivalUT","6426.88552271589");legacy.AddValue("launchUT","0");
        string original=legacy.ToString();
        double origin=(double)offset.Invoke(null,new object[]{legacy});
        Check(Math.Abs(Date(legacy,"survivalUT")+origin-Date(legacy,"ownerUT"))<1e-8,"legacy cryopod origin retains original native source timeline");
        var epoch=new Aetherfront.Continuity.SurvivalEpoch("Player",Date(legacy,"ownerUT"),Date(legacy,"survivalUT"));
        epoch.Bind("ClockBob",14686.676315600849);
        double start=epoch.Now("ClockBob",14686.676315600849)+origin;
        Check(Math.Abs(start-Date(legacy,"ownerUT"))<1e-8,"earlier pilot takeover does not move cryopod dates backwards");
        double finish=epoch.Now("ClockBob",14737.076315601949)+origin;
        Check(Math.Abs(finish-start-50.4000000011)<1e-8,"measured native failure interval now advances exactly50.4 seconds");
        Check(14737.076315601949-17786.875<0&&finish-17786.875>50,"native negative power interval becomes valid without rewriting old power date");
        Check(epoch.Now("ClockBob",14686.676315600849)+origin==start,"another pilot's later world time does not alter paused cryopod clock");
        var current=legacy.CreateCopy();current.SetValue("schema","3");current.AddValue("freezeOffset",origin.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        Check((double)offset.Invoke(null,new object[]{current})==origin,"persisted cryopod origin survives a new owner");
        foreach(var bad in new[]{"NaN","Infinity","-1e99","1e99","-7000"})
        {
            var node=current.CreateCopy();node.SetValue("freezeOffset",bad);bool refused=false;
            try{offset.Invoke(null,new object[]{node});}catch(TargetInvocationException e){refused=e.InnerException is InvalidDataException;}
            Check(refused,"invalid cryopod clock origin refused: "+bad);
        }
        Check(legacy.ToString()==original,"legacy origin calculation does not mutate evidence");
        var rebase=AccessTools.Method(typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.SurvivalAlignment",true),"RebaseSnapshot");
        var aligned=(ConfigNode)rebase.Invoke(null,new object[]{current,20000.0,Date(current,"ownerUT")});
        Check(Math.Abs(Date(aligned,"survivalUT")+(double)offset.Invoke(null,new object[]{aligned})-start)<1e-8,"USI coupling rebase alone preserves independent cryopod time");
        var native=Assembly.LoadFrom(Path.Combine(game,"GameData/REPOSoftTech/DeepFreeze/Plugins/DeepFreeze.dll"));
        Assembly.LoadFrom(Path.Combine(game,"GameData/REPOSoftTech/BackgroundResources/BackgroundResources.dll"));
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.DeepFreezeTime",true);
        var registered=new List<Tuple<MethodInfo,HarmonyMethod,HarmonyMethod>>();
        // FlightGlobals' static initializer requires a real Unity runtime.
        // Audit registrations and transform the actual shipped IL here; the
        // normal owned game run must separately prove installation/execution.
        {
            Action<MethodInfo,HarmonyMethod,HarmonyMethod> collect=(target,prefix,transpiler)=>registered.Add(Tuple.Create(target,prefix,transpiler));
            AccessTools.Method(implementation,"Configure").Invoke(null,new object[]{collect});
            foreach(var registration in registered.Where(r=>r.Item3!=null))
            {
                var transformed=((IEnumerable<CodeInstruction>)registration.Item3.method.Invoke(null,new object[]{PatchProcessor.GetOriginalInstructions(registration.Item1)})).ToArray();
                Check(transformed.Length>0,"actual native cryopod IL rewrite completes: "+registration.Item1.DeclaringType.Name+"."+registration.Item1.Name);
            }
            var freezer=native.GetType("DF.DeepFreezer",true);
            foreach(var name in new[]{"FixedUpdate","ChkOngoingEC","ChkOngoingTemp","OnStart","ThawKerbalStep4","RemoveKerbal","onVesselGoOffRails","resetCryopods","FixedBackgroundUpdate"})
                Check(registered.Count(r=>r.Item1==AccessTools.DeclaredMethod(freezer,name)&&r.Item3!=null)==1,"actual loaded/native-background DF clock hook registered: "+name);
            var background=AccessTools.TypeByName("BackgroundResources.DeepFreezer");var method=AccessTools.DeclaredMethod(background,"ProcessHandler");
            var codes=((IEnumerable<CodeInstruction>)AccessTools.Method(implementation,"UnloadedTime").Invoke(null,new object[]{PatchProcessor.GetOriginalInstructions(method)})).ToArray();
            Check(!codes.Any(c=>c.Calls(AccessTools.PropertyGetter(typeof(TimeWarp),nameof(TimeWarp.fixedDeltaTime))))&&codes.Count(c=>(c.operand as MethodInfo)?.Name=="StepFactor")==1,"native unloaded elapsed integral has scoped timestep factor");
            Check(codes.Count(c=>(c.operand as MethodInfo)?.Name=="SelectedCrew")==1&&codes.Count(c=>(c.operand as MethodInfo)?.Name=="get_numFrozenCrew")==1,"native unloaded power has scoped part crew count, remaining vessel guard retained");
            int resourceCall=Array.FindIndex(codes,c=>(c.operand as MethodInfo)?.DeclaringType.FullName=="BackgroundResources.UnloadedResourceProcessing"&&(c.operand as MethodInfo)?.Name=="RequestResource");
            Check(resourceCall>0&&(codes[resourceCall-1].operand as MethodInfo)?.Name=="UsesLegacyPush","native unloaded cryopod power uses scoped withdrawal direction");
            foreach(var pair in new[]{new[]{"DF.DFIntMemory","CheckComaUpdate"},new[]{"DF.DeepFreezeGUI","windowDF"}})
                Check(registered.Count(r=>r.Item1==AccessTools.DeclaredMethod(native.GetType(pair[0],true),pair[1])&&r.Item3!=null)==1,"native coma/monitor date-domain guard registered: "+pair[1]);
            var settings=native.GetType("DF.DFSettings",true);
            Check(!registered.Any(r=>r.Item1==AccessTools.Method(settings,"ApplySettings")),"native UI settings timeout remains on personal game time");
            var solar=AccessTools.TypeByName("BackgroundResources.SolarPanel");
            Check(!registered.Any(r=>r.Item1==AccessTools.Method(solar,"VesselPosition")),"orbital sunlight prediction not rewritten into survival time");
            foreach(string name in new[]{"ComaAge","MonitorAge","UnloadedTime","PredictionTime"})
            {
                bool refused=false;try{AccessTools.Method(implementation,name).Invoke(null,new object[]{Array.Empty<CodeInstruction>()});}catch(TargetInvocationException e){refused=e.InnerException is InvalidOperationException;}
                Check(refused,"native cryopod timer patch refuses unexpected IL: "+name);
            }
        }
        var alignment=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.FreezeTimerAlignment",true);
        var shift=AccessTools.Method(alignment,"ShiftDate");
        Check((double)shift.Invoke(null,new object[]{0.0,3000.0,false})==3000,"actual cryopod timestamp zero preserves elapsed age on alignment");
        Check((double)shift.Invoke(null,new object[]{0.0,3000.0,true})==0,"uninitialized reset schedule sentinel preserved");
        Check((double)shift.Invoke(null,new object[]{-1.0,3000.0,false})==-1,"negative unset cryopod date preserved");
        var moduleType=native.GetType("DF.DeepFreezer",true);
        var detached=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(moduleType);
        string[] dates={"deathCounter","tmpdeathCounter","timeLoadedOffrails","timeSinceLastECtaken","timeSinceLastTmpChk","cryopodResetTime"};
        foreach(string name in dates)
        {
            var field=AccessTools.Field(moduleType,name);double value=name=="cryopodResetTime"?0:12000;
            field.SetValue(detached,field.FieldType==typeof(float)?(object)(float)value:value);
        }
        AccessTools.Field(moduleType,"FrznChargeRequired").SetValue(detached,10);
        AccessTools.Field(moduleType,"lastUpdate").SetValue(detached,17f);
        var change=alignment.GetNestedType("Change",BindingFlags.NonPublic);
        var changes=(System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(change));
        AccessTools.Method(alignment,"Add").Invoke(null,new object[]{changes,detached,3000.0,dates});
        Check(changes.Count==6,"actual native mixed float/double cryopod timer plan is complete");
        Check((float)AccessTools.Field(moduleType,"timeSinceLastECtaken").GetValue(detached)==12000,"planning does not mutate native cryopod fields");
        foreach(object item in changes)AccessTools.Method(change,"Apply").Invoke(item,new object[]{false});
        foreach(string name in dates)Check(Convert.ToDouble(AccessTools.Field(moduleType,name).GetValue(detached))==(name=="cryopodResetTime"?0:15000),"native cryopod date shifted with correct numeric type: "+name);
        Check((int)AccessTools.Field(moduleType,"FrznChargeRequired").GetValue(detached)==10,"native power rate not shifted as a timestamp");
        Check((float)AccessTools.Field(moduleType,"lastUpdate").GetValue(detached)==17,"Unity realtime frame timer not shifted as a game date");
        foreach(object item in changes)AccessTools.Method(change,"Apply").Invoke(item,new object[]{true});
        foreach(string name in dates)Check(Convert.ToDouble(AccessTools.Field(moduleType,name).GetValue(detached))==(name=="cryopodResetTime"?0:12000),"native cryopod timer rollback restores exact field: "+name);
    }
    static void DeepFreezeAuthorityGates()
    {
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.DeepFreezeAuthority",true);
        var memory=AccessTools.TypeByName("DF.DFIntMemory");
        foreach(var pair in new[]{new[]{"CheckVslUpdate","VesselEnumeration","OwnedVessels"},new[]{"CheckComaUpdate","CrewEnumeration","OwnedCrew"}})
        {
            var original=PatchProcessor.GetOriginalInstructions(AccessTools.Method(memory,pair[0]));
            var transformed=((IEnumerable<CodeInstruction>)AccessTools.Method(implementation,pair[1]).Invoke(null,new object[]{original})).ToArray();
            Check(transformed.Count(c=>(c.operand as MethodInfo)?.DeclaringType==implementation&&(c.operand as MethodInfo)?.Name==pair[2])==1,"actual native DeepFreeze "+pair[0]+" has exactly one controller-filtered registry");
            if(pair[0]=="CheckVslUpdate")Check(transformed.Count(c=>(c.operand as MethodInfo)?.DeclaringType==implementation&&(c.operand as MethodInfo)?.Name=="OwnedLoaded")==1,"native DeepFreeze discovery also excludes observed replicas");
            bool refused=false;try{AccessTools.Method(implementation,pair[1]).Invoke(null,new object[]{Array.Empty<CodeInstruction>()});}catch(TargetInvocationException e){refused=e.InnerException is InvalidOperationException;}
            Check(refused,"DeepFreeze authority refuses an unreviewed iteration shape");
        }
        var own=Guid.NewGuid();var foreign=Guid.NewGuid();var absent=Guid.NewGuid();
        var ownedObject=new object();var foreignObject=new object();var source=new Dictionary<Guid,object>{{own,ownedObject},{foreign,foreignObject}};
        var selected=(Dictionary<Guid,object>)AccessTools.Method(implementation,"SelectVessels").MakeGenericMethod(typeof(object)).Invoke(null,new object[]{source,new HashSet<Guid>{own,absent}});
        Check(selected.Count==1&&ReferenceEquals(selected[own],ownedObject),"controller-filtered registry excludes foreign and absent vessels without cloning live records");
        Check(source.Count==2&&ReferenceEquals(source[foreign],foreignObject),"filter does not delete or mutate canonical foreign records");
    }
    static void NativeFrozenMerge()
    {
        string folder=Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location),"Fixtures/DeepFreezeNative");
        var currentBytes=File.ReadAllBytes(Path.Combine(folder,"previous-scenario.txt"));var bytes=File.ReadAllBytes(Path.Combine(folder,"Scenario.txt"));
        var current=LmpCommon.DeepFreezeScenarioCodec.Parse(currentBytes,currentBytes.Length);var incoming=LmpCommon.DeepFreezeScenarioCodec.Parse(bytes,bytes.Length);
        var id=incoming.Vessels.Keys.Single();var parts=incoming.Parts.Values.ToDictionary(p=>p.FlightId,p=>p.VesselGuid);
        var nativeMap=AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeNativeMapAdapter");
        var validate=nativeMap.GetMethod("ValidateSnapshot",BindingFlags.Static|BindingFlags.NonPublic);
        validate.Invoke(null,new object[]{incoming,new HashSet<Guid>{id}});
        Check(true,"actual KSP Mono native-map validation accepts captured Dead tuple");
        var freezerAssembly=AccessTools.TypeByName("DF.KerbalInfo").Assembly;
        var built=(System.Collections.IDictionary)nativeMap.GetMethod("BuildKerbals",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{freezerAssembly,incoming.Kerbals.Values});
        var nativeCrew=built["Jebediah Kerman"];
        Check(AccessTools.Field(nativeCrew.GetType(),"status").GetValue(nativeCrew).ToString()=="Dead"&&AccessTools.Field(nativeCrew.GetType(),"type").GetValue(nativeCrew).ToString()=="Unowned","actual native map constructor retains original enum values");
        bool wrongScope=false;try{validate.Invoke(null,new object[]{incoming,new HashSet<Guid>{Guid.NewGuid()}});}catch(TargetInvocationException e){wrongScope=e.InnerException is InvalidDataException;}
        Check(wrongScope,"native-map preparation still rejects foreign vessel scope");
        Check(LmpCommon.DeepFreezeScenarioMerge.Replace(current,incoming,new[]{id},parts).Kerbals.Values.Single().Status=="Dead","actual KSP Mono merge accepts captured native frozen status unchanged");
        incoming.Kerbals.Values.Single().Status="Missing";
        validate.Invoke(null,new object[]{incoming,new HashSet<Guid>{id}});Check(true,"native-map legacy Missing compatibility preserved");
        Check(LmpCommon.DeepFreezeScenarioMerge.Replace(current,incoming,new[]{id},parts).Kerbals.Values.Single().Status=="Missing","actual KSP Mono merge retains legacy frozen status compatibility");
        incoming.Kerbals.Values.Single().Status="Dead";incoming.Kerbals.Values.Single().Type="Crew";
        bool wrongCrew=false;try{validate.Invoke(null,new object[]{incoming,new HashSet<Guid>{id}});}catch(TargetInvocationException e){wrongCrew=e.InnerException is InvalidDataException;}
        Check(wrongCrew,"native-map preparation still rejects awake identity in frozen registry");
        bool rejected=false;try{LmpCommon.DeepFreezeScenarioMerge.Replace(current,incoming,new[]{id},parts);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"native client merge still rejects awake crew masquerading as frozen");
    }
    static void CryopodComputer(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/MOARdV/AvionicsSystems/AvionicsSystems.dll"));
        var start=AccessTools.Method(assembly.GetType("AvionicsSystems.MASFlightComputer",true),"Start",Type.EmptyTypes);
        var compat=typeof(SimulationAuthority).Assembly.GetType("Aetherfront.Continuity.Client.PackCompatibility",true);
        var rewrite=compat.GetMethod("ControlPointLookup",BindingFlags.Static|BindingFlags.NonPublic);
        var code=((IEnumerable<CodeInstruction>)rewrite.Invoke(null,new object[]{PatchProcessor.GetOriginalInstructions(start)})).ToArray();
        Check(code.Count(c=>c.operand is MethodInfo && ((MethodInfo)c.operand).Name=="ControlPointName")==1,"actual MAS startup unsafe command access replaced once");
        Check((string)compat.GetMethod("ControlPointName",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{null})==string.Empty,"non-command cryopod IVA retains default control-point behavior");
    }
    static void DeepFreezeRosterTransitions(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/REPOSoftTech/DeepFreeze/Plugins/DeepFreeze.dll"));
        var freezer=assembly.GetType("DF.DeepFreezer",true);
        foreach(var name in new[]{"RemoveKerbal","AddKerbal","ThawKerbalStep0"})
        {
            var method=AccessTools.DeclaredMethod(freezer,name,name=="ThawKerbalStep0"?new[]{typeof(string)}:new[]{typeof(ProtoCrewMember),typeof(int)});
            Check(method!=null,"actual DeepFreeze native roster transition exists: "+name);
            var il=PatchProcessor.GetOriginalInstructions(method);
            Check(il.Any(c=>c.Calls(AccessTools.PropertySetter(typeof(ProtoCrewMember),"type")))&&il.Any(c=>c.Calls(AccessTools.PropertySetter(typeof(ProtoCrewMember),"rosterStatus"))),"actual native transition changes both roster fields: "+name);
        }
        var type=typeof(SimulationAuthority).Assembly.GetType("Aetherfront.Continuity.Client.DeepFreezeCoordination",true);
        var supported=type.GetMethod("SupportsMod",BindingFlags.Static|BindingFlags.NonPublic);
        Check((bool)supported.Invoke(null,new object[]{assembly}),"actual approved DeepFreeze build may advertise protocol");
        Check(!(bool)supported.Invoke(null,new object[]{typeof(Program).Assembly}),"unrecognized mod assembly does not advertise protocol");
        var predicate=AccessTools.DeclaredMethod(AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeClientAdapter"),"IsActuallyFrozen",new[]{typeof(LmpCommon.DeepFreezeScenarioCodec.KerbalRecord)});
        foreach(var kind in new[]{"Crew","Applicant","Unowned","Tourist"})foreach(var status in new[]{"Assigned","Dead"})
        {
            var record=new LmpCommon.DeepFreezeScenarioCodec.KerbalRecord{Type=kind,Status=status};
            Check((bool)predicate.Invoke(null,new object[]{record})==(kind=="Unowned"&&status=="Dead"),"native frozen status accepted only with frozen identity: "+kind+"/"+status);
        }
        var remove=AccessTools.DeclaredMethod(freezer,"RemoveKerbal",new[]{typeof(ProtoCrewMember),typeof(int)});
        var removeCode=PatchProcessor.GetOriginalInstructions(remove);
        int rosterWrite=removeCode.FindIndex(c=>c.Calls(AccessTools.PropertySetter(typeof(ProtoCrewMember),"rosterStatus")));
        Check(rosterWrite>0&&removeCode[rosterWrite-1].opcode==System.Reflection.Emit.OpCodes.Ldc_I4_2,"approved native freeze writes Dead enum value2");
        foreach(var name in new[]{"USIUntrackKerbal","USIUntrackVessel"})Check(AccessTools.DeclaredMethod(freezer,name,new[]{typeof(string)})!=null,"actual legacy USI bridge target exists: "+name);
        var handshake=AccessTools.DeclaredMethod(AccessTools.TypeByName("LmpClient.Systems.Handshake.HandshakeMessageSender"),"SendHandshakeRequest",Type.EmptyTypes);
        var capability=type.GetMethod("HandshakeCapability",BindingFlags.Static|BindingFlags.NonPublic);
        var rewritten=((IEnumerable<CodeInstruction>)capability.Invoke(null,new object[]{PatchProcessor.GetOriginalInstructions(handshake)})).ToArray();
        Check(rewritten.Count(c=>c.operand is MethodInfo&&((MethodInfo)c.operand).Name=="ProtocolVersion")==1,"actual hardcoded handshake flag replaced once");
        Check((int)type.GetMethod("ProtocolVersion",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null)==0,"protocol not advertised before bridge installation");
        var scope=type.GetField("transitionCrew",BindingFlags.Static|BindingFlags.NonPublic);
        var suppress=type.GetMethod("OrdinaryCrew",BindingFlags.Static|BindingFlags.NonPublic);
        var finish=type.GetMethod("Finished",BindingFlags.Static|BindingFlags.NonPublic);
        scope.SetValue(null,"Freeze Regression Kerbal");
        var own=new object[]{"Freeze Regression Kerbal",false};
        Check(!(bool)suppress.Invoke(null,own)&&(bool)own[1],"intermediate native freeze roster dispatch suppressed");
        var other=new object[]{"Unrelated Kerbal",false};
        Check((bool)suppress.Invoke(null,other)&&!(bool)other[1],"unrelated crew dispatch unchanged during freeze");
        var error=new InvalidOperationException("test transition failure");
        Check(ReferenceEquals(finish.Invoke(null,new object[]{error,"Outer Kerbal"}),error),"native transition exception preserved");
        Check((string)scope.GetValue(null)=="Outer Kerbal","nested transition scope restored");
        finish.Invoke(null,new object[]{null,null});
        var after=new object[]{"Freeze Regression Kerbal",false};
        Check((bool)suppress.Invoke(null,after)&&!(bool)after[1],"ordinary dispatch resumes after finalizer");
    }
    static void SoundPrefabs(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/RocketSoundEnhancement/Plugins/RocketSoundEnhancement.dll"));
        var type=assembly.GetType("RocketSoundEnhancement.PartModules.RSE_Module",true);
        var eva=assembly.GetType("RocketSoundEnhancement.PartModules.RSE_KerbalEVA",true);
        var compatibility=typeof(SimulationAuthority).Assembly.GetType("Aetherfront.Continuity.Client.PackCompatibility",true);
        var rewrite=compatibility.GetMethod("SoundPrefabLookup",BindingFlags.Static|BindingFlags.NonPublic);
        var method=AccessTools.DeclaredMethod(type,"OnStart",new[]{typeof(PartModule.StartState)});
        var rewritten=((IEnumerable<CodeInstruction>)rewrite.Invoke(null,new object[]{PatchProcessor.GetOriginalInstructions(method)})).ToArray();
        Check(rewritten.Count(c=>c.operand is MethodInfo&&((MethodInfo)c.operand).Name=="SoundPrefab")==1,"actual RSE prefab index replaced once");
        Check(!rewritten.Any(c=>c.Calls(AccessTools.Method(typeof(PartModuleList),"get_Item",new[]{typeof(int)}))),"unsafe real RSE index lookup removed");
        Func<Type,PartModule> module=t=>(PartModule)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
        var liveA=module(eva);var liveB=module(eva);var prefabA=module(eva);var prefabB=module(eva);var other=module(type);
        var select=compatibility.GetMethod("SelectSoundPrefab",BindingFlags.Static|BindingFlags.NonPublic);
        Func<PartModule[],PartModule[],PartModule,PartModule> run=(p,a,l)=>(PartModule)select.Invoke(null,new object[]{p,a,l});
        Check(ReferenceEquals(run(new[]{prefabA},new[]{other,liveA},liveA),prefabA),"inserted live module does not shift sound prefab");
        Check(ReferenceEquals(run(new[]{prefabA,prefabB},new[]{other,liveA,liveB},liveB),prefabB),"duplicate concrete sound modules preserve occurrence");
        Check(ReferenceEquals(run(new[]{other},new[]{liveA},liveA),liveA),"missing concrete prefab uses own loaded config");
        Check(ReferenceEquals(run(new[]{prefabA},new[]{other},liveA),liveA),"detached module does not borrow a prefab");
        Check(ReferenceEquals(run(new[]{prefabA},new[]{liveA,liveB},liveB),liveB),"extra dynamic sound module uses own config");
        Check(ReferenceEquals(run(new[]{prefabA,other},new[]{liveA},liveA),prefabA),"matching original sound layout unchanged");
    }
    static double Date(ConfigNode node,string key)=>double.Parse(node.GetValue(key),System.Globalization.CultureInfo.InvariantCulture);
    static void CapturedDockingEpochs()
    {
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.SurvivalAlignment",true);
        var rebase=AccessTools.Method(implementation,"RebaseSnapshot");var timer=AccessTools.Method(implementation,"ShiftTimer");
        double personal=16000,common=16000;
        foreach(var file in new[]{"parent-before.cfg","child-before.cfg"})
        {
            var root=ConfigNode.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Fixtures/Docking",file));
            var before=root.GetNode("APF_SURVIVAL")??root;string original=before.ToString();
            var after=(ConfigNode)rebase.Invoke(null,new object[]{before,common,personal});
            double elapsed=personal-Date(before,"ownerUT");double offset=common-(Date(before,"survivalUT")+elapsed);
            var oldCrew=before.GetNodes("CREW").Single();var crew=after.GetNodes("CREW").Single();
            foreach(var name in new[]{"LastMeal","LastEC","LastUpdate","LastAtHome","LastSOIChange","TimeEnteredVessel"})
            {
                double expected=Date(before,"survivalUT")-Date(oldCrew,name)+elapsed;
                Check(Math.Abs(common-Date(crew,name)-expected)<1e-8,"captured docking preserves elapsed crew clock "+file+":"+name);
            }
            foreach(var name in new[]{"KerbalName","CurrentVesselId","PreviousVesselId","OldTrait","IsGrouchy","HomeBodyId","LastPlanet"})
                Check(crew.GetValue(name)==oldCrew.GetValue(name),"captured docking preserves non-time crew state "+name);
            Check(Math.Abs((Date(crew,"MaxOffKerbinTime")-common)-(Date(oldCrew,"MaxOffKerbinTime")-Date(before,"survivalUT")-elapsed))<1e-8,"captured deadline remaining follows only personal elapsed time");
            Check(Math.Abs((common-Date(after,"launchUT"))-(Date(before,"survivalUT")-Date(before,"launchUT")+elapsed))<1e-8,"captured launch anchor preserves mission age");
            var oldSupply=before.GetNode("SUPPLY");var supply=after.GetNode("SUPPLY");
            foreach(ConfigNode.Value value in oldSupply.values)
            {
                if(new[]{"LastUpdate","LastFeeding","LastECCheck"}.Contains(value.name))
                    Check(Math.Abs(Date(supply,value.name)-Date(oldSupply,value.name)-offset)<1e-8,"captured supply timestamp alignment "+value.name);
                else Check(supply.GetValue(value.name)==value.value,"supply duration/count/identity remains unchanged "+value.name);
            }
            Check(before.ToString()==original,"alignment planning does not mutate captured input");
            bool refused=false;try{rebase.Invoke(null,new object[]{before,1000.0,personal});}catch(TargetInvocationException e){refused=e.InnerException is InvalidDataException;}
            Check(refused&&before.ToString()==original,"backwards common clock refused before any input mutation");
        }
        Check((double)timer.Invoke(null,new object[]{0.0,2500.0})==0,"uninitialized converter timer zero retained");
        Check((double)timer.Invoke(null,new object[]{-1.0,2500.0})==-1,"uninitialized converter timer negative retained");
        Check((double)timer.Invoke(null,new object[]{12000.0,2500.0})==14500,"initialized converter interval shifts with epoch");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity})
        {
            bool refused=false;try{timer.Invoke(null,new object[]{bad,1.0});}catch(TargetInvocationException e){refused=e.InnerException is InvalidDataException;}
            Check(refused,"nonfinite native converter timer refused");
        }
    }
    static void MapUpdateEntry(string game)
    {
        Assembly.LoadFrom(Path.Combine(game,"GameData/KSPCommunityFixes/Plugins/KSPCommunityFixes.dll"));
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.MapUpdateCompatibility",true);
        var registrations=new List<Tuple<MethodInfo,HarmonyMethod>>();
        Action<MethodInfo,HarmonyMethod> collect=(target,prefix)=>registrations.Add(Tuple.Create(target,prefix));
        AccessTools.Method(implementation,"Configure").Invoke(null,new object[]{collect});
        Check(registrations.Count==1,"map compatibility registers one render-entry guard");
        Check(registrations[0].Item1==AccessTools.DeclaredMethod(typeof(OrbitRendererBase),"LateUpdate",Type.EmptyTypes),"guard targets actual renderer entry rather than an already compiled Harmony prefix");
        Check(registrations[0].Item2.priority==Priority.First,"null-map guard precedes native KSPCF dereference");
        var native=AccessTools.DeclaredMethod(AccessTools.TypeByName("KSPCommunityFixes.Performance.DisableMapUpdateInFlight"),"OrbitRendererBase_LateUpdate_Prefix");
        var code=PatchProcessor.GetOriginalInstructions(native).ToArray();
        Check(code.Any(c=>c.Calls(AccessTools.PropertyGetter(typeof(MapView),"fetch")))&&code.Any(c=>(c.operand as FieldInfo)?.Name=="updateMap"),"installed native map prefix contains the reproduced unguarded singleton read");
    }
    static void TrackingLabels()
    {
        Check(typeof(VesselLabels).Assembly.ManifestModule.ModuleVersionId==new Guid("4b449f28-41f8-4227-adfa-ad3149c8fdba"),"reviewed native KSP label lifecycle image");
        var awake=AccessTools.DeclaredMethod(typeof(VesselLabels),"Awake",Type.EmptyTypes);
        var destroy=AccessTools.DeclaredMethod(typeof(VesselLabels),"OnDestroy",Type.EmptyTypes);
        var nativeDestroy=AccessTools.Method(typeof(UnityEngine.Object),"Destroy",new[]{typeof(UnityEngine.Object)});
        Check(PatchProcessor.GetOriginalInstructions(awake).Count(c=>c.Calls(nativeDestroy))==1,"actual native duplicate-label branch can destroy shared station root");
        Check(PatchProcessor.GetOriginalInstructions(destroy).Count(c=>(c.operand as MethodInfo)?.Name=="Remove")==3,"actual native label destruction unsubscribes its three event handlers");
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.TrackingLabelCompatibility",true);
        var harmony=new Harmony("aetherfront.fixture.mono-tracking-labels");
        try
        {
            AccessTools.Method(implementation,"Install").Invoke(null,new object[]{harmony});
            Check(Harmony.GetPatchInfo(awake).Prefixes.Count(p=>p.owner==harmony.Id)==1,"single native label Awake guard installed");
            Check(Harmony.GetPatchInfo(destroy)?.Owners.Contains(harmony.Id)!=true,"native label cleanup and singleton release retained");
        }
        finally{harmony.UnpatchAll(harmony.Id);}
    }
    static void ScattererSettingsShutdown(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/scatterer/Scatterer.dll"));
        Check(assembly.ManifestModule.ModuleVersionId==new Guid("6c952942-cf2d-4994-afb0-f8c8e483d93a"),"reviewed actual Scatterer settings image");
        var type=assembly.GetType("Scatterer.MainSettingsReadWrite",true);
        var save=AccessTools.DeclaredMethod(type,"saveMainSettingsIfChanged",Type.EmptyTypes);
        var settings=Activator.CreateInstance(type);
        Check(GameDatabase.Instance==null,"shutdown regression has no live game database");
        bool reproduced=false;
        try{save.Invoke(settings,null);}catch(TargetInvocationException e){reproduced=e.InnerException is NullReferenceException;}
        Check(reproduced,"actual native Scatterer settings save reproduces null-database exception");
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.ScattererSettingsCompatibility",true);
        var harmony=new Harmony("aetherfront.fixture.mono-scatterer-shutdown");
        try
        {
            AccessTools.Method(implementation,"Install").Invoke(null,new object[]{harmony});
            Check(Harmony.GetPatchInfo(save).Prefixes.Count(p=>p.owner==harmony.Id)==1,"one scoped native settings database guard");
            save.Invoke(settings,null);save.Invoke(settings,null);
            Check(true,"actual native settings save safely skips unavailable database on repeated shutdown");
            Check(Harmony.GetPatchInfo(AccessTools.Method(type,"loadMainSettings"))?.Owners.Contains(harmony.Id)!=true,"Scatterer settings loading retained");
            var plugin=assembly.GetType("Scatterer.PluginDataReadWrite",true);
            Check(Harmony.GetPatchInfo(AccessTools.Method(plugin,"savePluginData"))?.Owners.Contains(harmony.Id)!=true,"separate native plugin settings saving retained");
        }
        finally{harmony.UnpatchAll(harmony.Id);}
    }
    static void MapCleanup(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/SpaceDust/Plugins/SpaceDust.dll"));
        Check(assembly.ManifestModule.ModuleVersionId==new Guid("4258eaad-b1df-4e0b-910c-d13847f31a48"),"reviewed actual SpaceDust image");
        var type=assembly.GetType("SpaceDust.MapOverlay",true);
        var target=AccessTools.DeclaredMethod(type,"DestroyInspectorPanel",Type.EmptyTypes);
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.MapCleanupCompatibility",true);
        var harmony=new Harmony("aetherfront.fixture.mono-map-cleanup");
        try
        {
            AccessTools.Method(implementation,"Install").Invoke(null,new object[]{harmony});
            Check(Harmony.GetPatchInfo(target).Prefixes.Count(p=>p.owner==harmony.Id)==1,"actual native inspector cleanup has exactly one targeted guard");
            var empty=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            target.Invoke(empty,null);
            Check(AccessTools.Field(type,"inspector").GetValue(empty)==null,"never-opened native map panel cleanup completes safely");
            target.Invoke(empty,null);
            Check(AccessTools.Field(type,"inspector").GetValue(empty)==null,"repeated native map panel cleanup is idempotent");
            foreach(var name in new[]{"OnMapExited","OnDestroy"})
                Check(Harmony.GetPatchInfo(AccessTools.Method(type,name))?.Owners.Contains(harmony.Id)!=true,"native overlay lifecycle preserved: "+name);
        }
        finally{harmony.UnpatchAll(harmony.Id);}
    }
    static void ProbeRoomCleanup(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/ProbeControlRoom/Plugins/ProbeControlRoomRecontrolled.dll"));
        Check(assembly.ManifestModule.ModuleVersionId==new Guid("0e7106b9-f3db-4e98-98f3-c2de0e7b3db1"),"actual Probe Control Room reviewed image");
        var method=AccessTools.Method(assembly.GetType("ProbeControlRoom.ProbeControlRoom",true),"stopIVA");
        var implementation=typeof(SurvivalClock).Assembly.GetType("Aetherfront.Continuity.Client.ProbeRoomCompatibility",true);
        var original=PatchProcessor.GetOriginalInstructions(method).ToArray();
        var rewrite=AccessTools.Method(implementation,"SafeCameraRoom");
        var changed=((IEnumerable<CodeInstruction>)rewrite.Invoke(null,new object[]{original})).ToArray();
        Check(changed.Count(c=>(c.operand as MethodInfo)?.DeclaringType==implementation)==1,"probe cleanup replaces only unsafe room ancestry predicate");
        Check(changed.Count(c=>(c.operand as MethodInfo)?.Name=="IsChildOf")==0,"native direct ancestry dereference removed");
        foreach(var name in new[]{"SetSun","RemoveControlLock","StartReset","SetCameraFlight","Activate","SetTexture"})
            Check(changed.Count(c=>(c.operand as MethodInfo)?.Name==name)==original.Count(c=>(c.operand as MethodInfo)?.Name==name),"native probe cleanup retained: "+name);
        Check((bool)AccessTools.Method(implementation,"CameraInsideRoom").Invoke(null,new object[]{null})==false,"missing probe room safely bypasses ancestry check");
        bool rejected=false;
        try{rewrite.Invoke(null,new object[]{new[]{new CodeInstruction(System.Reflection.Emit.OpCodes.Ret)}});}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException;}
        Check(rejected,"changed probe cleanup method shape refused");
    }
    static void SplitInitializationAndCleanup()
    {
        var topology=typeof(SimulationAuthority).Assembly.GetType("Aetherfront.Continuity.Client.SurvivalTopology",true);
        var initialize=AccessTools.DeclaredMethod(typeof(Vessel),nameof(Vessel.Initialize),new[]{typeof(bool)});
        Check(initialize!=null&&initialize.ReturnType==typeof(bool),"actual public vessel construction hook returns success");
        foreach(var method in new[]{AccessTools.DeclaredMethod(typeof(Part),nameof(Part.decouple),new[]{typeof(float)}),AccessTools.DeclaredMethod(typeof(Part),nameof(Part.Undock),new[]{typeof(DockedVesselInfo)})})
        {
            Check(method!=null,"actual split method signature");
            var codes=PatchProcessor.GetOriginalInstructions(method).ToArray();
            int creation=Array.FindIndex(codes,c=>c.Calls(initialize));
            Check(creation>=0,"native split constructs through hooked public initializer: "+method.Name);
            Check(codes.Skip(creation+1).Any(c=>c.operand is MethodInfo&&((MethodInfo)c.operand).DeclaringType.FullName.StartsWith("EventData")),"child inherited epoch precedes native completion events: "+method.Name);
        }
        var scopeType=topology.GetNestedType("SplitScope",BindingFlags.NonPublic);
        var scope=Activator.CreateInstance(scopeType,true);var previous=Activator.CreateInstance(scopeType,true);
        var clock=(SurvivalClock)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SurvivalClock));
        AccessTools.Field(scopeType,"Previous").SetValue(scope,previous);
        AccessTools.Field(scopeType,"PreviousClock").SetValue(scope,clock);
        var current=AccessTools.Field(topology,"splitting");var context=AccessTools.Field(typeof(SurvivalClock),"Context");
        var oldScope=current.GetValue(null);var oldClock=context.GetValue(null);
        try
        {
            current.SetValue(null,scope);context.SetValue(null,null);
            var failure=new InvalidOperationException("simulated native split failure");
            var result=AccessTools.Method(topology,"AfterSplit").Invoke(null,new object[]{failure,scope});
            Check(ReferenceEquals(result,failure),"split finalizer preserves native failure");
            Check(ReferenceEquals(current.GetValue(null),previous),"failed nested split restores previous operation scope");
            Check(ReferenceEquals(context.GetValue(null),clock),"failed split restores previous scoped survival clock");
            AccessTools.Method(topology,"AfterSplit").Invoke(null,new object[]{null,null});
            Check(ReferenceEquals(current.GetValue(null),previous)&&ReferenceEquals(context.GetValue(null),clock),"unscoped split leaves surrounding context intact");
        }
        finally{current.SetValue(null,oldScope);context.SetValue(null,oldClock);}
    }
    static void TopologyEventTargets()
    {
        var topology=typeof(SimulationAuthority).Assembly.GetType("Aetherfront.Continuity.Client.SurvivalTopology",true);
        var listener=topology.GetField("Listener",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        foreach(var entry in new[]{new{Argument=typeof(GameScenes),Method="Scene"},new{Argument=typeof(GameEvents.FromToAction<Part,Part>),Method="Eva"}})
        {
            var eventType=typeof(EventData<>).MakeGenericType(entry.Argument);
            var delegateType=eventType.GetNestedType("OnEvent");
            if(delegateType.ContainsGenericParameters)delegateType=delegateType.MakeGenericType(entry.Argument);
            var callback=Delegate.CreateDelegate(delegateType,listener,listener.GetType().GetMethod(entry.Method,BindingFlags.Instance|BindingFlags.NonPublic));
            var wrapper=eventType.GetNestedType("EvtDelegate",BindingFlags.NonPublic);
            if(wrapper.ContainsGenericParameters)wrapper=wrapper.MakeGenericType(entry.Argument);
            var native=Activator.CreateInstance(wrapper,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{callback},null);
            Check(ReferenceEquals(callback.Target,listener)&&native!=null,"actual KSP EventData wrapper accepts topology callback: "+entry.Method);
        }
    }
    static void KerbalStatsSeats(string game)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(game,"GameData/KerbalStats/Plugins/KerbalStats.dll"));
        var type=assembly.GetType("KerbalStats.Experience.ExperienceTrackerEvents",true);
        var method=AccessTools.DeclaredMethod(type,"ScanVesselCrew",new[]{typeof(Vessel)});
        Check(method!=null,"actual KerbalStats crew scanner signature");
        var compatibility=typeof(SimulationAuthority).Assembly.GetType("Aetherfront.Continuity.Client.PackCompatibility",true);
        var transpiler=compatibility.GetMethod("SafeSeatIndices",BindingFlags.Static|BindingFlags.NonPublic);
        var source=PatchProcessor.GetOriginalInstructions(method);
        var rewritten=((IEnumerable<CodeInstruction>)transpiler.Invoke(null,new object[]{source})).ToArray();
        Check(rewritten.Count(c=>c.operand is MethodInfo && ((MethodInfo)c.operand).Name=="SafeSeatIndex")==2,"both real unsafe seat-index reads rewritten");
        var safe=compatibility.GetMethod("SafeSeatIndex",BindingFlags.Static|BindingFlags.NonPublic);
        var crew=(ProtoCrewMember)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ProtoCrewMember));
        foreach(int index in new[]{-1,int.MinValue,0,1,12,int.MaxValue})
        {
            crew.seatIdx=index;
            Check((int)safe.Invoke(null,new object[]{crew})==(index<0?int.MaxValue:index),"unassigned seats fall back while valid seat numbers stay unchanged");
        }
    }
    static void ResourceTopologyRace()
    {
        var method=AccessTools.Method(typeof(ResourceClockClient),"ResourceTargetsMatch");
        var packet=new LmpClient.Systems.VesselResourceSys.VesselResource();packet.ResourcesCount=2;
        packet.Resources=new[]{new LmpCommon.Message.Data.Vessel.VesselResourceInfo{PartFlightId=1,ResourceName="ElectricCharge",Amount=100},new LmpCommon.Message.Data.Vessel.VesselResourceInfo{PartFlightId=1,ResourceName="Supplies",Amount=99.5}};
        Func<uint,string,bool> old=(id,name)=>id==1&&name=="ElectricCharge";
        Check(!(bool)method.Invoke(null,new object[]{packet,old}),"valid added-resource packet defers against old proto instead of disconnecting");
        Check(packet.Resources[0].Amount==100&&packet.Resources[1].Amount==99.5,"stale destination classification does not mutate resource values");
        Func<uint,string,bool> fresh=(id,name)=>id==1&&(name=="ElectricCharge"||name=="Supplies");
        Check((bool)method.Invoke(null,new object[]{packet,fresh}),"same native packet matches after fresh authoritative proto arrives");
        foreach(var amount in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d})
        {
            packet.Resources[1].Amount=amount;int lookups=0;Func<uint,string,bool> missing=(id,name)=>{lookups++;return false;};bool refused=false;
            try{method.Invoke(null,new object[]{packet,missing});}catch(TargetInvocationException e){refused=e.InnerException is InvalidDataException;}
            Check(refused&&lookups==0,"malformed resource numbers rejected before stale topology can defer them");
        }
        packet.Resources[1].Amount=1;
        foreach(int count in new[]{-1,3})
        {
            packet.ResourcesCount=count;bool refused=false;try{method.Invoke(null,new object[]{packet,fresh});}catch(TargetInvocationException e){refused=e.InnerException is InvalidDataException;}
            Check(refused,"invalid native resource count remains rejected");
        }
        packet.ResourcesCount=2;packet.Resources[1].ResourceName="";bool emptyRefused=false;
        try{method.Invoke(null,new object[]{packet,fresh});}catch(TargetInvocationException e){emptyRefused=e.InnerException is InvalidDataException;}
        Check(emptyRefused,"empty resource identity remains rejected");
        packet.Resources[1]=null;bool nullRefused=false;
        try{method.Invoke(null,new object[]{packet,fresh});}catch(TargetInvocationException e){nullRefused=e.InnerException is InvalidDataException;}
        Check(nullRefused,"null resource row rejected as malformed data rather than a topology defer");
    }
    static void ValidateResourceEnvelope()
    {
        var id=Guid.NewGuid();var document=new ConfigNode();var root=document.AddNode("APF_RESOURCE_CLOCK");
        root.AddValue("schema","1");root.AddValue("vesselId",id.ToString());root.AddValue("gameTime","1000");
        var saved=root.AddNode("SurvivalClock").AddNode("APF_SURVIVAL");
        saved.AddValue("schema","1");saved.AddValue("owner","ClockAlice");saved.AddValue("ownerUT","1000");saved.AddValue("survivalUT","2000");
        root.AddNode("ModuleLifeSupportSystem").AddValue("LastUpdateTime","2000");
        var timer=root.AddNode("PART_TIMER");timer.AddValue("uid","1");timer.AddValue("index","0");timer.AddValue("module","ModuleResourceConverter");timer.AddValue("lastUpdateTime","2000");
        var validate=typeof(ResourceClockClient).GetMethod("ValidateEnvelope",BindingFlags.Static|BindingFlags.NonPublic);
        Func<ConfigNode,ConfigNode> run=n=>(ConfigNode)validate.Invoke(null,new object[]{System.Text.Encoding.UTF8.GetBytes(n.GetNode("APF_RESOURCE_CLOCK").ToString()),id,1000d});
        Check(run(document).GetNode("SurvivalClock")!=null,"real KSP ConfigNode envelope accepted before resource mutation");
        var versionTwo=document.CreateCopy();
        var newer=versionTwo.GetNode("APF_RESOURCE_CLOCK").GetNode("SurvivalClock").GetNode("APF_SURVIVAL");
        newer.SetValue("schema","2");newer.AddValue("launchUT","1900");
        Check(run(versionTwo).GetNode("SurvivalClock").GetNode("APF_SURVIVAL").GetValue("launchUT")=="1900","actual KSP Mono accepts persisted virtual launch anchor");
        foreach(string launch in new[]{"NaN","Infinity","-1","9999999"})
        {
            var bad=versionTwo.CreateCopy();bad.GetNode("APF_RESOURCE_CLOCK").GetNode("SurvivalClock").GetNode("APF_SURVIVAL").SetValue("launchUT",launch);
            bool refused=false;try{run(bad);}catch(TargetInvocationException e){refused=e.InnerException is InvalidDataException;}
            Check(refused,"invalid virtual launch anchor refused by actual KSP parser");
        }
        foreach(var mutate in new Action<ConfigNode>[] {
            n=>n.GetNode("APF_RESOURCE_CLOCK").SetValue("vesselId",Guid.NewGuid().ToString()),
            n=>n.GetNode("APF_RESOURCE_CLOCK").AddValue("gameTime","1000"),
            n=>n.GetNode("APF_RESOURCE_CLOCK").GetNode("ModuleLifeSupportSystem").SetValue("LastUpdateTime","NaN"),
            n=>n.GetNode("APF_RESOURCE_CLOCK").GetNode("PART_TIMER").SetValue("index","-1"),
            n=>n.GetNode("APF_RESOURCE_CLOCK").GetNode("PART_TIMER").SetValue("lastUpdateTime","999999"),
            n=>n.GetNode("APF_RESOURCE_CLOCK").GetNode("SurvivalClock").GetNode("APF_SURVIVAL").AddValue("owner","forged"),
            n=>n.GetNode("APF_RESOURCE_CLOCK").AddNode("unexpected")})
        {
            var bad=document.CreateCopy();mutate(bad);bool rejected=false;
            try{run(bad);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
            Check(rejected,"malformed client clock envelope rejected before any resource mutation");
        }
    }
    static void ClientWire()
    {
        var factory=new LmpCommon.Message.ClientMessageFactory();
        var source=factory.CreateNewMessageData<LmpCommon.Message.Data.Vessel.VesselResourceMsgData>();
        source.VesselId=Guid.NewGuid();source.GameTime=1234;source.ResourcesCount=1;
        source.Resources=new[]{new LmpCommon.Message.Data.Vessel.VesselResourceInfo{PartFlightId=1,ResourceName="Supplies",Amount=91.25,FlowState=true}};
        var payload=System.Text.Encoding.UTF8.GetBytes("SurvivalClock\n{\n owner = ClockTest\n}\n");
        int emptySize=source.GetMessageSize();Aetherfront.Continuity.ResourceClockPayloads.Set(source,payload);
        Check(source.GetMessageSize()==emptySize+payload.Length,"actual patched client message size includes metadata");
        var write=(Lidgren.Network.NetOutgoingMessage)Activator.CreateInstance(typeof(Lidgren.Network.NetOutgoingMessage),true);
        typeof(Lidgren.Network.NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(write,new byte[16]);
        source.Serialize(write);
        var read=(Lidgren.Network.NetIncomingMessage)Activator.CreateInstance(typeof(Lidgren.Network.NetIncomingMessage),true);
        typeof(Lidgren.Network.NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(read,(byte[])write.Data.Clone());read.LengthBits=write.LengthBytes*8;
        var target=factory.CreateNewMessageData<LmpCommon.Message.Data.Vessel.VesselResourceMsgData>();target.Deserialize(read);
        Check(Aetherfront.Continuity.ResourceClockPayloads.Get(target).SequenceEqual(payload),"actual client serialize/deserialize hook round trip on KSP Mono");
        Check(target.VesselId==source.VesselId&&target.GameTime==source.GameTime&&target.Resources[0].Amount==91.25,"original client resource fields unchanged");
        foreach(long revision in new[]{0L,17L,long.MaxValue})
        {
            source.SharedWorldRevision=revision;write.LengthBits=0;
            source.Serialize(write);
            typeof(Lidgren.Network.NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(read,(byte[])write.Data.Clone());
            read.LengthBits=write.LengthBytes*8;read.Position=0;target.Deserialize(read);
            Check(target.SharedWorldRevision==revision && read.Position==read.LengthBits,"actual client outer shared revision reader preserved");
            Check(Aetherfront.Continuity.ResourceClockPayloads.Get(target).SequenceEqual(payload),"actual resource payload coexists with native shared revision");
        }
        var queue=new LmpClient.Systems.VesselResourceSys.VesselResourceQueue();var cached=new LmpClient.Systems.VesselResourceSys.VesselResource();
        typeof(LmpClient.Systems.VesselResourceSys.VesselResourceQueue).GetMethod("AssignFromMessage",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(queue,new object[]{cached,target});
        Check(Aetherfront.Continuity.ResourceClockPayloads.Get(cached).SequenceEqual(payload),"actual resource interpolation queue retains matching metadata");
        Aetherfront.Continuity.ResourceClockPayloads.Clear(target);
        typeof(LmpClient.Systems.VesselResourceSys.VesselResourceQueue).GetMethod("AssignFromMessage",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(queue,new object[]{cached,target});
        Check(Aetherfront.Continuity.ResourceClockPayloads.Get(cached).Length==0,"recycled client interpolation object cannot retain another packet's clock");
    }
}
