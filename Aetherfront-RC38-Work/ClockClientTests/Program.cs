using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using Aetherfront.Continuity.Client;
using HarmonyLib;

static class Program
{
    static int checks;
    static void Check(bool good,string text) { checks++;if(!good)throw new Exception(text); }
    static void Main()
    {
        string root=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        var lookup=Directory.GetFiles(Path.Combine(root,"GameData"),"*.dll",SearchOption.AllDirectories).GroupBy(Path.GetFileNameWithoutExtension).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path;return lookup.TryGetValue(new AssemblyName(e.Name).Name,out path)?Assembly.LoadFrom(path):null;};
        Run();
        Console.WriteLine("PASS "+checks+" actual-KSP-Mono forward-only clock acknowledgement checks; no live scene claim.");
    }
    static void Run()
    {
        Check(ClockTransitionCompatibility.ShouldAlign(5,5,true,false,100.05,100),"50 ms forward correction accepted");
        Check(!ClockTransitionCompatibility.ShouldAlign(5,5,true,false,99.95,100),"ack cannot rewind client UT");
        Check(!ClockTransitionCompatibility.ShouldAlign(5,5,true,false,100,100),"equal time does not resync");
        Check(!ClockTransitionCompatibility.ShouldAlign(5,6,true,false,200,100),"stale ack cannot move new subspace");
        Check(!ClockTransitionCompatibility.ShouldAlign(5,5,true,true,200,100),"active warp cannot be clobbered");
        Check(!ClockTransitionCompatibility.ShouldAlign(5,5,false,false,200,100),"disconnected session unchanged");
        foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,1e12+1})
            Check(!ClockTransitionCompatibility.ShouldAlign(5,5,true,false,invalid,100),"invalid acknowledgement time refused");
        Check(!ClockTransitionCompatibility.ShouldAlign(-1,-1,true,false,200,100),"unstable subspace refused");
        var late=typeof(ClockTransitionCompatibility).GetMethod("Update",BindingFlags.NonPublic|BindingFlags.Instance);
        var il=PatchProcessor.GetOriginalInstructions(late);
        Check(!il.Any(c=>c.operand is MethodInfo m&&m.Name=="ProcessNewSubspace"),"does not fire a second warp-stopped event");
        Check(il.Any(c=>c.operand is MethodInfo m&&m.Name=="SetGameTime"),"uses existing Unity-thread vessel alignment");
        var handler=AccessTools.TypeByName("LmpClient.Systems.Warp.WarpMessageHandler");
        Check(handler!=null&&AccessTools.GetDeclaredMethods(handler).Any(m=>m.Name=="HandleMessage"),"actual acknowledgement handler exists");
        var after=typeof(ClockTransitionCompatibility).GetMethod("AfterMessage",BindingFlags.NonPublic|BindingFlags.Static);
        var afterIl=PatchProcessor.GetOriginalInstructions(after);
        Check(!afterIl.Any(c=>c.operand is MethodInfo m&&m.Name=="SetGameTime"),"background handler never changes Unity time");
        Check(ClockTransitionCompatibility.ForwardOffset(100-21.2,100,true)==100,"21-second loading stall cannot propose past own timeline");
        Check(ClockTransitionCompatibility.ForwardOffset(100-120,100,true)==100,"two-minute stall remains monotonic without server tolerance expansion");
        Check(ClockTransitionCompatibility.ForwardOffset(150,100,true)==150,"forward warp preserved");
        Check(ClockTransitionCompatibility.ForwardOffset(50,100,false)==50,"no unrelated floor invented");
        Check(double.IsNaN(ClockTransitionCompatibility.ForwardOffset(double.NaN,100,true)),"non-finite proposal still reaches native rejection");
        var sender=AccessTools.TypeByName("LmpClient.Systems.Warp.WarpMessageSender");
        var send=AccessTools.DeclaredMethod(sender,"SendNewSubspace",Type.EmptyTypes);
        var original=PatchProcessor.GetOriginalInstructions(send);
        var rewritten=ClockTransitionCompatibility.RewriteProposal(original.Select(c=>new CodeInstruction(c))).ToArray();
        Check(rewritten.Count(c=>c.operand is MethodInfo m&&m.Name=="ClampProposal")==1,"exactly one actual packet offset is clamped");
        Check(rewritten.Length==original.Count+1,"factory, identity and send pipeline unchanged");
        var setter=AccessTools.PropertySetter(AccessTools.TypeByName("LmpClient.Systems.Warp.WarpSystem"),"CurrentSubspace");
        Check(setter!=null,"stable-to-warp capture lifecycle exists");
        FloorLifecycle();
    }

    static void FloorLifecycle()
    {
        var type=AccessTools.TypeByName("LmpClient.Systems.Warp.WarpSystem");
        object first=FormatterServices.GetUninitializedObject(type), second=FormatterServices.GetUninitializedObject(type);
        var spaces=AccessTools.Field(type,"<Subspaces>k__BackingField");
        var current=AccessTools.Field(type,"_currentSubspace");
        var a=new ConcurrentDictionary<int,double>();a[4]=100;a[6]=150;
        var b=new ConcurrentDictionary<int,double>();b[4]=50;
        spaces.SetValue(first,a);spaces.SetValue(second,b);current.SetValue(first,4);current.SetValue(second,4);
        var capture=typeof(ClockTransitionCompatibility).GetMethod("CaptureFloor",BindingFlags.NonPublic|BindingFlags.Static);
        var clear=typeof(ClockTransitionCompatibility).GetMethod("ClearFloor",BindingFlags.NonPublic|BindingFlags.Static);
        var table=typeof(ClockTransitionCompatibility).GetField("Floors",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        var lookup=table.GetType().GetMethod("TryGetValue");
        Func<object,double> read=system=>{
            var args=new object[]{system,null};
            Check((bool)lookup.Invoke(table,args),"floor captured for system");
            return (double)args[1].GetType().GetField("Offset",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(args[1]);
        };
        capture.Invoke(null,new object[]{first,-1});
        Check(read(first)==100,"entering warp captures own stable offset before -1");
        capture.Invoke(null,new object[]{second,-1});
        Check(read(second)==50&&read(first)==100,"two client systems keep separate floors");
        capture.Invoke(null,new object[]{first,6});
        Check(read(first)==150,"own canonical acknowledgement advances floor");
        clear.Invoke(null,new object[]{first});
        var argsAfter=new object[]{first,null};
        Check(!(bool)lookup.Invoke(table,argsAfter),"disconnect clears stale floor");
        Check(read(second)==50,"one disconnect does not clear another system");
        clear.Invoke(null,new object[]{second});
    }
}
