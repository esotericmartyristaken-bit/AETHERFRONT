using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    // The native manager treats its local vessel list as the entire universe.
    // Multiplayer replicas may be absent or still loading. Only a controller may
    // advance their simulation; absence from this client's list is not deletion.
    internal static class DeepFreezeAuthority
    {
        private static FieldInfo comaList;
        internal static void Install(Harmony harmony)
        {
            DeepFreezeTime.Install(harmony);
            var memory=AccessTools.TypeByName("DF.DFIntMemory");
            comaList=AccessTools.Field(memory,"comaKerbals");
            var vessel=AccessTools.DeclaredMethod(memory,"CheckVslUpdate",Type.EmptyTypes);
            var crew=AccessTools.DeclaredMethod(memory,"CheckComaUpdate",Type.EmptyTypes);
            var tick=AccessTools.DeclaredMethod(AccessTools.TypeByName("DF.DeepFreezer"),"FixedUpdate",Type.EmptyTypes);
            if(comaList==null||vessel==null||crew==null||tick==null)throw new InvalidOperationException("DeepFreeze authority signatures changed");
            harmony.Patch(vessel,transpiler:new HarmonyMethod(typeof(DeepFreezeAuthority),nameof(VesselEnumeration)));
            harmony.Patch(crew,prefix:new HarmonyMethod(typeof(DeepFreezeAuthority),nameof(ClearComaWork)),
                transpiler:new HarmonyMethod(typeof(DeepFreezeAuthority),nameof(CrewEnumeration)));
            harmony.Patch(tick,prefix:new HarmonyMethod(typeof(DeepFreezeAuthority),nameof(PartTick)));
        }
        private static bool PartTick(PartModule __instance) => !SimulationAuthority.InMultiplayer||SimulationAuthority.MaySimulate(__instance.vessel);
        private static void ClearComaWork(object __instance)
        {
            // Native code appends each tourist every tick and never clears its
            // work list. Rebuild it from the current authoritative registry.
            if(SimulationAuthority.InMultiplayer)((IList)comaList.GetValue(__instance)).Clear();
        }
        private static HashSet<Guid> ControlledIds() => new HashSet<Guid>(FlightGlobals.Vessels.Where(SimulationAuthority.MaySimulate).Select(v=>v.id));
        private static Dictionary<Guid,T>.Enumerator OwnedVessels<T>(Dictionary<Guid,T> source)
        {
            if(!SimulationAuthority.InMultiplayer)return source.GetEnumerator();
            return SelectVessels(source,ControlledIds()).GetEnumerator();
        }
        internal static Dictionary<Guid,T> SelectVessels<T>(Dictionary<Guid,T> source,HashSet<Guid> controlled)
            => source.Where(p=>controlled.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value);
        private static Dictionary<string,T>.Enumerator OwnedCrew<T>(Dictionary<string,T> source)
        {
            if(!SimulationAuthority.InMultiplayer)return source.GetEnumerator();
            var controlled=ControlledIds();var vessel=AccessTools.Field(typeof(T),"vesselID");
            if(vessel==null||vessel.FieldType!=typeof(Guid))throw new InvalidOperationException("DeepFreeze crew vessel identity changed");
            return source.Where(p=>controlled.Contains((Guid)vessel.GetValue(p.Value))).ToDictionary(p=>p.Key,p=>p.Value,source.Comparer).GetEnumerator();
        }
        private static List<Vessel> OwnedLoaded()
            => !SimulationAuthority.InMultiplayer?FlightGlobals.VesselsLoaded:FlightGlobals.VesselsLoaded.Where(SimulationAuthority.MaySimulate).ToList();
        private static IEnumerable<CodeInstruction> VesselEnumeration(IEnumerable<CodeInstruction> source)
            => FilterEnumeration(source,typeof(Guid),"DF.VesselInfo",nameof(OwnedVessels),true);
        private static IEnumerable<CodeInstruction> CrewEnumeration(IEnumerable<CodeInstruction> source)
            => FilterEnumeration(source,typeof(string),"DF.KerbalInfo",nameof(OwnedCrew),false);
        private static IEnumerable<CodeInstruction> FilterEnumeration(IEnumerable<CodeInstruction> source,Type key,string value,string replacement,bool loaded)
        {
            var code=source.ToList();int dictionaries=0,lists=0;
            foreach(var instruction in code)
            {
                var method=instruction.operand as MethodInfo;
                if(method==null)continue;
                var type=method.DeclaringType;
                if(method.Name=="GetEnumerator"&&type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(Dictionary<,>))
                {
                    var arguments=type.GetGenericArguments();
                    if(arguments[0]==key&&arguments[1].FullName==value)
                    {
                        instruction.opcode=OpCodes.Call;
                        instruction.operand=AccessTools.Method(typeof(DeepFreezeAuthority),replacement).MakeGenericMethod(arguments[1]);dictionaries++;
                    }
                }
                if(loaded&&method==AccessTools.PropertyGetter(typeof(FlightGlobals),nameof(FlightGlobals.VesselsLoaded)))
                {
                    instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(DeepFreezeAuthority),nameof(OwnedLoaded));lists++;
                }
            }
            if(dictionaries!=1||lists!=(loaded?1:0))throw new InvalidOperationException("Unreviewed DeepFreeze iteration shape: "+dictionaries+"/"+lists);
            return code;
        }
    }
}
