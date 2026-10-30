using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LmpClient;
using LmpClient.Apf;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    // Candidate scoped simulation clocks. Actual multiplayer/transfer and UI
    // compatibility remain release gates, independent of successful patching.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class SimulationAuthority : MonoBehaviour
    {
        private static FieldInfo backgroundVessel;
        private static bool installed;
        public static bool Ready => installed;
        public static bool InMultiplayer => ApfClientSettings.Enabled && MainSystem.NetworkState >= ClientState.Starting;
        public void Awake()
        {
            if (installed) { Destroy(this); return; }
            DontDestroyOnLoad(this);
            var harmony = new Harmony("aetherfront.continuity.authority");
            Type life = AccessTools.TypeByName("LifeSupport.ModuleLifeSupportSystem");
            Type background = AccessTools.TypeByName("BackgroundResources.UnloadedResources");
            Type interested = AccessTools.TypeByName("BackgroundResources.InterestedVessel");
            if (life == null || background == null || interested == null)
            {
                Debug.Log("[AetherfrontContinuity] Pack installation is incomplete. Simulation remains disabled until the complete pack is installed and KSP restarts.");
                return;
            }
            MethodInfo tick = AccessTools.DeclaredMethod(life, "FixedUpdate", Type.EmptyTypes);
            MethodInfo process = AccessTools.DeclaredMethod(background, "ProcessInterestedModules", new[] { interested });
            MethodInfo overflow = AccessTools.DeclaredMethod(background, "UpdateResourceCacheOverflows", new[] { interested });
            backgroundVessel = AccessTools.Field(interested, "vessel");
            if (tick == null || process == null || overflow == null || backgroundVessel == null || backgroundVessel.FieldType != typeof(Vessel))
                throw new InvalidOperationException("Life support authority signatures changed");
            var finalizer=new HarmonyMethod(typeof(SimulationAuthority),nameof(TickFinished));
            harmony.Patch(tick, prefix: new HarmonyMethod(typeof(SimulationAuthority), nameof(LifeSupportTick)),finalizer:finalizer);
            harmony.Patch(process, prefix: new HarmonyMethod(typeof(SimulationAuthority), nameof(BackgroundTick)),finalizer:finalizer);
            harmony.Patch(overflow, prefix: new HarmonyMethod(typeof(SimulationAuthority), nameof(BackgroundTick)),finalizer:finalizer);
            int guards=3, clocks=0;
            var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>new[]{"USILifeSupport","USITools","KolonyTools"}.Contains(a.GetName().Name)).ToArray();
            var modules=assemblies.SelectMany(a=>a.GetTypes()).Where(t=>typeof(PartModule).IsAssignableFrom(t)).Concat(new[]{typeof(BaseConverter),typeof(ModuleGenerator)});
            var targets=new HashSet<MethodInfo>();
            foreach(var type in modules)
                foreach(var method in AccessTools.GetDeclaredMethods(type).Where(m=>!m.IsStatic && new[]{"FixedUpdate","OnFixedUpdate","Update","OnUpdate"}.Contains(m.Name) && m.GetParameters().Length==0 && m.GetMethodBody()!=null))
                    if(targets.Add(method))
                    {
                        harmony.Patch(method,prefix:new HarmonyMethod(typeof(SimulationAuthority),nameof(PartTick)),finalizer:finalizer); guards++;
                    }
            var timeSource=AccessTools.Method(typeof(Planetarium),nameof(Planetarium.GetUniversalTime),Type.EmptyTypes);
            var missionSource=AccessTools.Field(typeof(Vessel),nameof(Vessel.missionTime));
            if(timeSource==null || missionSource==null)throw new InvalidOperationException("KSP survival time signatures changed");
            var timeTypes=assemblies.SelectMany(a=>a.GetTypes()).Concat(new[]{typeof(BaseConverter),typeof(ModuleGenerator)});
            foreach(var method in timeTypes.SelectMany(t=>AccessTools.GetDeclaredMethods(t)).Where(m=>m.GetMethodBody()!=null && !m.ContainsGenericParameters))
                if(PatchProcessor.GetOriginalInstructions(method).Any(code=>code.Calls(timeSource)||(code.opcode==System.Reflection.Emit.OpCodes.Ldfld && Equals(code.operand,missionSource))))
                {
                    harmony.Patch(method,transpiler:new HarmonyMethod(typeof(SimulationAuthority),nameof(ScopedTime))); clocks++;
                }
            if(clocks==0)throw new InvalidOperationException("Survival clock time sources not found");
            var monitor=AccessTools.TypeByName("LifeSupport.LifeSupportMonitor");
            var row=AccessTools.DeclaredMethod(monitor,"GetVesselStats",new[]{typeof(LifeSupport.VesselSupplyStatus)});
            if(row==null)throw new InvalidOperationException("Life support monitor signature changed");
            harmony.Patch(row,prefix:new HarmonyMethod(typeof(SimulationAuthority),nameof(MonitorTick)),finalizer:new HarmonyMethod(typeof(SimulationAuthority),nameof(MonitorFinished)));
            foreach(var type in new[]{AccessTools.TypeByName("LifeSupport.ModuleLifeSupportSystem"),AccessTools.TypeByName("USITools.LogisticsTools")})
            {
                if(type==null)throw new InvalidOperationException("Required nearby-vessel authority target missing");
                foreach(var method in AccessTools.GetDeclaredMethods(type).Where(m=>m.Name=="GetNearbyVessels" && m.ReturnType==typeof(List<Vessel>)))
                    harmony.Patch(method,postfix:new HarmonyMethod(typeof(SimulationAuthority),nameof(FilterNearby)));
            }
            PackCompatibility.Install(harmony);
            EditorInputCompatibility.Install(harmony);
            ClockTransitionCompatibility.Install(harmony);
            ResourceClockClient.Install(harmony);
            DeepFreezeCoordination.Install(harmony);
            SurvivalTopology.Install(harmony);
            VesselEntryRefresh.Install(this,harmony);
            installed = true;
            Debug.Log("[AetherfrontContinuity] Installed "+guards+" simulation authority gates and "+clocks+" scoped clock methods. Candidate multiplayer transfer tests remain required.");
        }

        public static bool MaySimulate(Vessel vessel)
        {
            if (!ApfClientSettings.Enabled || MainSystem.NetworkState < ClientState.Starting) return true;
            if (MainSystem.NetworkState != ClientState.Running || vessel == null) return false;
            var query = LockSystem.LockQuery;
            string control=query.GetControlLockOwner(vessel.id);
            var clock=string.IsNullOrEmpty(control)?vessel.GetComponent<SurvivalClock>():null;
            return AuthorityPolicy.MaySimulate(SettingsSystem.CurrentSettings.PlayerName,
                control, query.GetUpdateLockOwner(vessel.id),
                query.GetUnloadedUpdateLockOwner(vessel.id), vessel.loaded,clock==null?string.Empty:clock.SimulationOwner);
        }

        private static bool Enter(Vessel vessel,out SurvivalClock previous)
        {
            previous=SurvivalClock.Context;
            if(!InMultiplayer)return true;
            if(vessel==null && HighLogic.LoadedSceneIsEditor)return true;
            if(!MaySimulate(vessel))return false;
            var clock=vessel.GetComponent<SurvivalClock>();
            if(clock==null)throw new InvalidOperationException("Required vessel survival clock missing");
            if(!clock.Prepare())return false;
            SurvivalClock.Context=clock; return true;
        }
        public static bool LifeSupportTick(VesselModule __instance,out SurvivalClock __state) => Enter(__instance == null ? null : __instance.Vessel,out __state);
        public static bool BackgroundTick(object __0,out SurvivalClock __state) => Enter(__0 == null ? null : backgroundVessel.GetValue(__0) as Vessel,out __state);
        public static bool PartTick(PartModule __instance,out SurvivalClock __state) => Enter(__instance == null ? null : __instance.vessel,out __state);
        public static Exception TickFinished(Exception __exception,SurvivalClock __state)
        {
            SurvivalClock.Context=__state;
            return __exception;
        }
        public static void MonitorTick(LifeSupport.VesselSupplyStatus __0,out double? __state)
        {
            __state=SurvivalClock.DisplayUT;
            Guid id;
            if(!InMultiplayer || __0==null || !Guid.TryParse(__0.VesselId,out id))return;
            var vessel=FlightGlobals.FindVessel(id);
            var clock=vessel==null?null:vessel.GetComponent<SurvivalClock>();
            if(clock!=null)SurvivalClock.DisplayUT=clock.DisplayTime;
        }
        public static Exception MonitorFinished(Exception __exception,double? __state)
        {
            SurvivalClock.DisplayUT=__state; return __exception;
        }
        public static void FilterNearby(List<Vessel> __result)
        {
            // USI mutiny can decouple nearby ships. Logistics also uses these
            // lists for transfers, so merely gating the source ship is insufficient.
            if(InMultiplayer && __result!=null)__result.RemoveAll(v=>!MaySimulate(v));
        }
        public static IEnumerable<CodeInstruction> ScopedTime(IEnumerable<CodeInstruction> instructions)
        {
            var original=AccessTools.Method(typeof(Planetarium),nameof(Planetarium.GetUniversalTime),Type.EmptyTypes);
            var replacement=AccessTools.Method(typeof(SurvivalClock),nameof(SurvivalClock.UniversalTime));
            var mission=AccessTools.Field(typeof(Vessel),nameof(Vessel.missionTime));
            var missionReplacement=AccessTools.Method(typeof(SurvivalClock),nameof(SurvivalClock.MissionTime));
            foreach(var code in instructions)
            {
                if(code.Calls(original))code.operand=replacement;
                if(code.opcode==System.Reflection.Emit.OpCodes.Ldfld && Equals(code.operand,mission)){code.opcode=System.Reflection.Emit.OpCodes.Call;code.operand=missionReplacement;}
                yield return code;
            }
        }
    }
}
