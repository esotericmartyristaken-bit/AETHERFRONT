using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LmpClient;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class DeepFreezeBackgroundSave
    {
        private const int BatchSize=64;
        private static Type protocol;
        private static FieldInfo scopeField,registryInstance;
        private static PropertyInfo registry,active,pending,restoring,recapture,error;
        private static Guid[] drain;
        private static Guid[] drainBatch;
        private static int cursor;
        [ThreadStatic] private static bool capturing;
        internal static void Install(Harmony harmony)
        {
            protocol=AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeClientSystem");
            scopeField=AccessTools.Field(protocol,"recaptureScope");
            active=AccessTools.Property(protocol,"Active");pending=AccessTools.Property(protocol,"SavePending");
            restoring=AccessTools.Property(protocol,"IsRestoring");recapture=AccessTools.Property(protocol,"NeedsRecapture");error=AccessTools.Property(protocol,"LastSaveError");
            var manager=AccessTools.TypeByName("DF.DeepFreeze");
            registryInstance=AccessTools.Field(manager,"Instance");registry=AccessTools.Property(manager,"KnownVessels");
            var capture=AccessTools.DeclaredMethod(protocol,"CaptureAndSend",Type.EmptyTypes);
            var apply=AccessTools.GetDeclaredMethods(protocol).Single(m=>m.Name=="Apply");
            var scenarios=AccessTools.TypeByName("LmpClient.Systems.Scenario.ScenarioSystem");
            var flush=AccessTools.DeclaredMethod(scenarios,"FlushApprovedScenariosBeforeQuit",new[]{typeof(Action)});
            if(scopeField?.FieldType!=typeof(Guid[])||active==null||pending==null||restoring==null||recapture?.GetSetMethod(true)==null||error==null||
                registryInstance==null||registry==null||capture==null||flush==null)
                throw new InvalidOperationException("DeepFreeze background save signatures changed");
            harmony.Patch(capture,prefix:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(BeforeCapture)){priority=Priority.First},
                finalizer:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(AfterCapture)));
            harmony.Patch(AccessTools.Method(typeof(LmpCommon.Locks.LockQuery),"ControlLockBelongsToPlayer",new[]{typeof(Guid),typeof(string)}),
                prefix:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(CheckpointLock)));
            harmony.Patch(apply,prefix:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(BeforeApply)),
                postfix:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(AfterApply)));
            harmony.Patch(flush,prefix:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(BeforeFlush)));
            harmony.Patch(AccessTools.DeclaredMethod(protocol,"Reset",Type.EmptyTypes),postfix:new HarmonyMethod(typeof(DeepFreezeBackgroundSave),nameof(Reset)));
        }
        private static bool Flag(PropertyInfo property)=>(bool)property.GetValue(null,null);
        private static bool Owns(Guid id)
        {
            var vessel=FlightGlobals.FindVessel(id);
            return vessel!=null&&SimulationAuthority.MaySimulate(vessel);
        }
        private static Guid[] Candidates()
        {
            var instance=registryInstance.GetValue(null);
            var entries=instance==null?null:registry.GetValue(instance,null) as IDictionary;
            if(entries==null)return Array.Empty<Guid>();
            return entries.Keys.Cast<Guid>().Where(Owns).OrderBy(id=>id).ToArray();
        }
        private static void BeforeFlush(object __instance)
        {
            if(!SimulationAuthority.InMultiplayer||!Flag(active)||
                AccessTools.Field(__instance.GetType(),"QuitAfterApprovedSave").GetValue(__instance)!=null)return;
            drain=Candidates();drainBatch=null;
        }
        private static void BeforeCapture(out bool __state)
        {
            __state=capturing;
            if(!SimulationAuthority.InMultiplayer||MainSystem.NetworkState!=LmpCommon.Enums.ClientState.Running||!Flag(active)||Flag(restoring)||Flag(pending))return;
            capturing=true;
            var existing=scopeField.GetValue(null) as Guid[];
            if(existing==null)
            {
                Guid[] selected;
                if(drain!=null)
                {
                    drain=drain.Where(Owns).ToArray();
                    selected=drain.Take(BatchSize).ToArray();drainBatch=selected;
                }
                else
                {
                    var candidates=Candidates();
                    selected=candidates.Skip(candidates.Length==0?0:cursor%candidates.Length).Take(BatchSize).ToArray();
                    cursor=candidates.Length==0?0:(cursor+selected.Length)%candidates.Length;
                }
                if(selected.Length==0)return;
                scopeField.SetValue(null,selected);existing=selected;
            }
            // Native capture uses protoVessel for unloaded ships. Save the live
            // owner clock into that same proto before serializing the tuple.
            foreach(Guid id in existing)
            {
                var vessel=FlightGlobals.FindVessel(id);
                if(vessel==null||vessel.loaded||!Owns(id))continue;
                var clock=vessel.GetComponent<SurvivalClock>();
                if(clock==null||(!clock.Prepare()&&!clock.CanRetainDeferredSnapshot(SurvivalClock.Player)))
                    throw new InvalidOperationException("Unloaded cryopod clock is unavailable");
                var node=new ConfigNode(nameof(SurvivalClock));clock.Save(node);
                var modules=vessel.protoVessel?.vesselModules;
                if(modules==null)throw new InvalidOperationException("Unloaded cryopod module snapshot is unavailable");
                modules.RemoveNodes(nameof(SurvivalClock));modules.AddNode(node);
            }
        }
        private static Exception AfterCapture(Exception __exception,bool __state){capturing=__state;return __exception;}
        private static bool CheckpointLock(Guid vesselId,string playerName,ref bool __result)
        {
            if(!capturing||!SimulationAuthority.InMultiplayer)return true;
            __result=string.Equals(playerName,SettingsSystem.CurrentSettings.PlayerName,StringComparison.Ordinal)&&Owns(vesselId);
            return false;
        }
        private static void BeforeApply(out bool __state)=>__state=Flag(pending);
        private static void AfterApply(bool __state)
        {
            if(!__state||Flag(pending)||Flag(recapture)||error.GetValue(null,null)!=null||drain==null)return;
            if(drainBatch!=null)
            {
                var saved=new HashSet<Guid>(drainBatch);drain=drain.Where(id=>!saved.Contains(id)).ToArray();drainBatch=null;
            }
            if(drain.Length>0)recapture.SetValue(null,true,null);
            else drain=null;
        }
        private static void Reset(){capturing=false;drain=null;drainBatch=null;cursor=0;}
    }
}
