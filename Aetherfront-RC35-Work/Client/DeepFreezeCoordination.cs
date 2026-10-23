using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    // DeepFreeze deliberately represents frozen crew as Unowned/Dead. Those
    // intermediate ordinary roster messages must not precede its atomic world
    // checkpoint, or the ordinary applicant guard restores an awake crew record.
    internal static class DeepFreezeCoordination
    {
        private static PropertyInfo active;
        private static FieldInfo instance;
        private static PropertyInfo knownVessels;
        private static bool available;
        [ThreadStatic] private static string transitionCrew;
        internal static void Install(Harmony harmony)
        {
            var freezer=AccessTools.TypeByName("DF.DeepFreezer");
            if(freezer==null)return;
            var protocol=AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeClientSystem");
            var manager=AccessTools.TypeByName("DF.DeepFreeze");
            instance=AccessTools.Field(manager,"Instance");knownVessels=AccessTools.Property(manager,"KnownVessels");
            if(instance==null||knownVessels==null)throw new InvalidOperationException("DeepFreeze registry signatures changed");
            active=AccessTools.Property(protocol,"Active");
            var suppress=AccessTools.DeclaredMethod(protocol,"SuppressOrdinaryCrew",new[]{typeof(string)});
            if(active==null||suppress==null)throw new InvalidOperationException("Shared DeepFreeze protocol signatures changed");
            harmony.Patch(suppress,prefix:new HarmonyMethod(typeof(DeepFreezeCoordination),nameof(OrdinaryCrew)));
            var capture=AccessTools.DeclaredMethod(protocol,"CaptureAndSend",Type.EmptyTypes);
            if(capture==null)throw new InvalidOperationException("DeepFreeze checkpoint signature changed");
            harmony.Patch(capture,prefix:new HarmonyMethod(typeof(DeepFreezeCoordination),nameof(CaptureReady)));
            foreach(var name in new[]{"USIUntrackKerbal","USIUntrackVessel"})
            {
                var method=AccessTools.DeclaredMethod(freezer,name,new[]{typeof(string)});
                if(method==null)throw new InvalidOperationException("DeepFreeze USI integration changed: "+name);
                harmony.Patch(method,prefix:new HarmonyMethod(typeof(DeepFreezeCoordination),name=="USIUntrackKerbal"?nameof(UntrackKerbal):nameof(UntrackVessel)));
            }
            foreach(var name in new[]{"RemoveKerbal","AddKerbal","ThawKerbalStep0"})
            {
                var arguments=name=="ThawKerbalStep0"?new[]{typeof(string)}:new[]{typeof(ProtoCrewMember),typeof(int)};
                var method=AccessTools.DeclaredMethod(freezer,name,arguments);
                if(method==null)throw new InvalidOperationException("DeepFreeze roster transition signature changed: "+name);
                harmony.Patch(method,prefix:new HarmonyMethod(typeof(DeepFreezeCoordination),nameof(Transition)),
                    finalizer:new HarmonyMethod(typeof(DeepFreezeCoordination),nameof(Finished)));
            }
            foreach(var name in new[]{"beginFreezeKerbal","beginThawKerbal"})
            {
                var method=AccessTools.DeclaredMethod(freezer,name,name=="beginFreezeKerbal"?new[]{typeof(ProtoCrewMember)}:new[]{typeof(string)});
                if(method==null)throw new InvalidOperationException("DeepFreeze action signature changed: "+name);
                harmony.Patch(method,prefix:new HarmonyMethod(typeof(DeepFreezeCoordination),nameof(CanBegin)));
            }
            var sender=AccessTools.TypeByName("LmpClient.Systems.Handshake.HandshakeMessageSender");
            var handshake=AccessTools.DeclaredMethod(sender,"SendHandshakeRequest",Type.EmptyTypes);
            if(handshake==null)throw new InvalidOperationException("DeepFreeze handshake signature changed");
            harmony.Patch(handshake,transpiler:new HarmonyMethod(typeof(DeepFreezeCoordination),nameof(HandshakeCapability)));
            available=SupportsMod(freezer.Assembly);
            if(available){DeepFreezeAuthority.Install(harmony);DeepFreezeBackgroundSave.Install(harmony);}
        }
        private static bool SupportsMod(Assembly assembly) => assembly.GetName().Version.ToString()=="1.32.0.0" &&
            assembly.ManifestModule.ModuleVersionId==new Guid("3dfd9a02-2102-4022-9ef5-8326bd22a723");
        private static int ProtocolVersion() => available?1:0;
        private static bool CaptureReady(ref string __result)
        {
            var vessel=FlightGlobals.ActiveVessel;
            if(!SimulationAuthority.InMultiplayer || vessel==null || UnityEngine.Time.timeSinceLevelLoad>=10f)return true;
            var freezers=vessel.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).Where(m=>m.GetType().FullName=="DF.DeepFreezer").ToArray();
            if(freezers.Length==0 || freezers.Any(m=>(int)AccessTools.Field(m.GetType(),"TotalFrozen").GetValue(m)>0))return true;
            var current=instance.GetValue(null);
            var registry=current==null?null:knownVessels.GetValue(current,null) as System.Collections.IDictionary;
            if(registry!=null && registry.Contains(vessel.id))return true;
            // Native registry discovery runs after five seconds. Defer only an
            // empty startup vessel, for at most ten seconds; retain failures for
            // missing frozen state or an initialization that never completes.
            __result=null;return false;
        }
        private static bool UntrackKerbal(PartModule __instance,string __0)
        {
            if(!SimulationAuthority.InMultiplayer)return true;
            if(SimulationAuthority.MaySimulate(__instance.vessel))LifeSupport.LifeSupportManager.Instance.UntrackKerbal(__0);
            return false;
        }
        private static bool UntrackVessel(PartModule __instance,string __0)
        {
            if(!SimulationAuthority.InMultiplayer)return true;
            if(SimulationAuthority.MaySimulate(__instance.vessel))LifeSupport.LifeSupportManager.Instance.UntrackVessel(__0);
            return false;
        }
        private static IEnumerable<CodeInstruction> HandshakeCapability(IEnumerable<CodeInstruction> source)
        {
            var code=source.ToList();int changed=0;
            for(int index=1;index<code.Count;index++)
            {
                var field=code[index].operand as FieldInfo;
                if(code[index].opcode!=OpCodes.Stfld||field?.Name!="DeepFreezeProtocolVersion")continue;
                if(field.DeclaringType.FullName!="LmpCommon.Message.Data.Handshake.HandshakeRequestMsgData" || code[index-1].opcode!=OpCodes.Ldc_I4_0)
                    throw new InvalidOperationException("Unreviewed DeepFreeze capability writer");
                code[index-1].opcode=OpCodes.Call;code[index-1].operand=AccessTools.Method(typeof(DeepFreezeCoordination),nameof(ProtocolVersion));changed++;
            }
            if(changed!=1)throw new InvalidOperationException("Unreviewed DeepFreeze capability count: "+changed);
            return code;
        }
        private static bool CanBegin(PartModule __instance)
        {
            if(!SimulationAuthority.InMultiplayer)return true;
            if(!SimulationAuthority.MaySimulate(__instance.vessel))return false;
            if((bool)active.GetValue(null,null))return true;
            ScreenMessages.PostScreenMessage("Cryopod actions require this server's shared DeepFreeze update.",5f,ScreenMessageStyle.UPPER_CENTER);
            return false;
        }
        private static bool Transition(PartModule __instance,object __0,out string __state)
        {
            __state=transitionCrew;
            if(!SimulationAuthority.InMultiplayer)return true;
            if(!SimulationAuthority.MaySimulate(__instance.vessel)||(bool)active.GetValue(null,null)==false)return false;
            transitionCrew=(__0 as ProtoCrewMember)?.name??(__0 as string);
            if(string.IsNullOrEmpty(transitionCrew))throw new InvalidOperationException("DeepFreeze transition crew missing");
            return true;
        }
        private static bool OrdinaryCrew(string name,ref bool __result)
        {
            if(transitionCrew==null || !string.Equals(transitionCrew,name,StringComparison.Ordinal))return true;
            __result=true;return false;
        }
        private static Exception Finished(Exception __exception,string __state)
        {
            transitionCrew=__state;return __exception;
        }
    }
}
