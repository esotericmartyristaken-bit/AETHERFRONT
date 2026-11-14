using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

internal static partial class Program
{
    static void DeepFreezeVesselEventRegression()
    {
        var implementation=typeof(Aetherfront.Continuity.Client.SimulationAuthority).Assembly
            .GetType("Aetherfront.Continuity.Client.DeepFreezeVesselEvents",true);
        var predicate=AccessTools.Method(implementation,"ShouldHandleDestroy");
        Func<bool,object,object,bool> permits=(multiplayer,owner,destroyed)=>
            (bool)predicate.Invoke(null,new[]{(object)multiplayer,owner,destroyed});
        var owner=new EventVessel(Guid.NewGuid());
        var unrelated=new EventVessel(Guid.NewGuid());
        var replacedReplica=new EventVessel(owner.Id);
        Check(!permits(true,owner,unrelated),"unrelated asteroid/vessel destruction does not cancel a multiplayer cryopod");
        Check(!permits(true,owner,replacedReplica),"old replica with the same vessel GUID does not cancel the current cryopod");
        Check(!permits(true,owner,null),"missing event vessel is not mistaken for a known live owner");
        Check(permits(true,owner,owner),"own-vessel destruction/unload retains native cancellation");
        Check(permits(true,null,unrelated),"already-detached freezer retains native teardown cleanup");
        Check(permits(true,null,null),"missing owner and missing event preserve native teardown behavior");
        Check(permits(false,owner,unrelated)&&permits(false,owner,owner)&&permits(false,owner,null),
            "single-player/disconnected behavior is unchanged");
        Check(owner.Id==replacedReplica.Id&&!ReferenceEquals(owner,replacedReplica),"replica regression actually uses equal GUIDs and different objects");

        var registrations=new List<Tuple<MethodInfo,HarmonyMethod>>();
        Action<MethodInfo,HarmonyMethod> register=(target,prefix)=>registrations.Add(Tuple.Create(target,prefix));
        AccessTools.Method(implementation,"Configure").Invoke(null,new object[]{register});
        var freezer=AccessTools.TypeByName("DF.DeepFreezer");
        var destroyed=AccessTools.DeclaredMethod(freezer,"onVesselDestroy",new[]{typeof(Vessel)});
        Check(registrations.Count==1&&registrations[0].Item1==destroyed,"only the native DeepFreeze destruction callback is patched");
        var prefix=registrations.Single().Item2.method;
        Check(prefix.Name=="BeforeVesselDestroyed"&&prefix.ReturnType==typeof(bool),"native callback receives a boolean Harmony prefix");
        Check(prefix.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(PartModule),typeof(Vessel)}),
            "prefix binds the actual freezer instance and event vessel");
        var prefixCode=PatchProcessor.GetOriginalInstructions(prefix);
        Check(prefixCode.Count(c=>c.Calls(predicate))==1,"installed prefix delegates to the tested identity policy exactly once");
        Check(prefixCode.Any(c=>c.Calls(AccessTools.PropertyGetter(typeof(Aetherfront.Continuity.Client.SimulationAuthority),"InMultiplayer"))),
            "installed prefix preserves the real multiplayer/session boundary");
        int sessionRead=prefixCode.FindIndex(c=>c.Calls(AccessTools.PropertyGetter(typeof(Aetherfront.Continuity.Client.SimulationAuthority),"InMultiplayer")));
        int partRead=prefixCode.FindIndex(c=>c.Calls(AccessTools.PropertyGetter(typeof(PartModule),"part"))||
            (c.operand as FieldInfo)?.DeclaringType==typeof(PartModule)&&(c.operand as FieldInfo)?.Name=="part");
        Check(partRead>sessionRead&&prefixCode.Skip(sessionRead+1).Take(partRead-sessionRead-1).Any(c=>c.opcode==System.Reflection.Emit.OpCodes.Ret),
            "single-player/disconnected path returns before reading a detached part");
        Check(!prefixCode.Any(c=>(c.operand as MethodInfo)?.Name=="MaySimulate"),"own-vessel cancellation does not depend on a released control lock");

        var callback=PatchProcessor.GetOriginalInstructions(destroyed);
        foreach(var name in new[]{"FreezeKerbalAbort","ThawKerbalAbort"})
        {
            var abort=AccessTools.DeclaredMethod(freezer,name);
            Check(callback.Count(c=>c.Calls(abort))==1,"native own-vessel "+name+" path is retained exactly once");
        }
        var freezeAbort=AccessTools.DeclaredMethod(freezer,"FreezeKerbalAbort");
        Check(PatchProcessor.GetOriginalInstructions(freezeAbort).Any(c=>c.Calls(AccessTools.DeclaredMethod(freezer,"AddKerbal"))),
            "native freeze rollback still restores the crew through AddKerbal");
        Check(!registrations.Any(r=>new[]{"OnVesselChange","OnVesselSwitching","FreezeKerbalAbort","ThawKerbalAbort","RemoveKerbal","AddKerbal"}.Contains(r.Item1.Name)),
            "real switching, abort/rollback and roster methods are not replaced by the event fix");

        var harmony=new Harmony("aetherfront.fixture.deepfreeze-vessel-events");
        try
        {
            AccessTools.Method(implementation,"Install").Invoke(null,new object[]{harmony});
            Check(Harmony.GetPatchInfo(destroyed).Prefixes.Count(p=>p.owner==harmony.Id&&p.PatchMethod==prefix)==1,
                "event patch installs against the actual shipped native method");
            Check(Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id))==1,
                "event installation changes only one method");
        }
        finally{harmony.UnpatchAll(harmony.Id);}
        Check(Harmony.GetPatchInfo(destroyed)==null||!Harmony.GetPatchInfo(destroyed).Owners.Contains(harmony.Id),
            "fixture removes its native patch after testing");
    }

    private sealed class EventVessel
    {
        internal readonly Guid Id;
        internal EventVessel(Guid id){Id=id;}
    }
}
