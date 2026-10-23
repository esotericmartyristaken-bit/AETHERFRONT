using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    // Local EVA creation is synchronous. Carry its parent's virtual epoch to the
    // new module before the first life-support tick or proto serialization.
    internal static class SurvivalTopology
    {
        internal sealed class Birth
        {
            internal string Owner;
            internal double PersonalUT,SurvivalUT,LaunchUT,FreezeOffset;
            internal float RecordedAt;
        }
        private static readonly Dictionary<Guid,Birth> Births=new Dictionary<Guid,Birth>();
        private sealed class SplitScope
        {
            internal SplitScope Previous;
            internal SurvivalClock PreviousClock;
            internal Vessel Parent;
            internal HashSet<Part> Parts;
            internal Birth Epoch;
        }
        [ThreadStatic] private static SplitScope splitting;
        // KSP EventData requires a non-null delegate.Target, even for callbacks
        // whose state is static (EvtDelegate calls Target.GetType()).
        private sealed class Callbacks
        {
            internal void Eva(GameEvents.FromToAction<Part,Part> value)=>OnEva(value);
            internal void Scene(GameScenes value)=>OnScene(value);
        }
        private static readonly Callbacks Listener=new Callbacks();
        private static bool installed;
        internal static void Install(Harmony harmony)
        {
            if(installed)return;
            SurvivalAlignment.Install(harmony);
            var decouple=AccessTools.DeclaredMethod(typeof(Part),nameof(Part.decouple),new[]{typeof(float)});
            var undock=AccessTools.DeclaredMethod(typeof(Part),nameof(Part.Undock),new[]{typeof(DockedVesselInfo)});
            var initialize=AccessTools.DeclaredMethod(typeof(Vessel),nameof(Vessel.Initialize),new[]{typeof(bool)});
            if(decouple==null || undock==null || initialize==null || initialize.ReturnType!=typeof(bool))
                throw new InvalidOperationException("Native vessel split signatures changed");
            foreach(var method in new[]{decouple,undock})
                harmony.Patch(method,prefix:new HarmonyMethod(typeof(SurvivalTopology),nameof(BeforeSplit)),
                    finalizer:new HarmonyMethod(typeof(SurvivalTopology),nameof(AfterSplit)));
            harmony.Patch(initialize,postfix:new HarmonyMethod(typeof(SurvivalTopology),nameof(InitializedSplit)));
            GameEvents.onCrewOnEva.Add(Listener.Eva);
            GameEvents.onGameSceneLoadRequested.Add(Listener.Scene);
            installed=true;
        }
        private static void OnScene(GameScenes scene){if(scene==GameScenes.MAINMENU)Births.Clear();}
        // Stock decouple/undock constructs a fresh Vessel and fresh vessel
        // modules. Its normal CopyOverrides does not copy a survival epoch.
        // Scope only the native operation on an authoritative source, then seed
        // its actual new child before any completion/proto publication callback.
        private static void BeforeSplit(Part __instance,out SplitScope __state)
        {
            __state=null;
            if(!SimulationAuthority.InMultiplayer || __instance==null)return;
            var parent=__instance.vessel;
            if(parent==null || !SimulationAuthority.MaySimulate(parent))return;
            var clock=parent.GetComponent<SurvivalClock>();
            if(clock==null || !clock.Prepare())throw new InvalidOperationException("Split parent survival clock unavailable");
            __state=new SplitScope{Previous=splitting,PreviousClock=SurvivalClock.Context,Parent=parent,
                Parts=new HashSet<Part>(parent.parts),Epoch=new Birth{Owner=SurvivalClock.Player,
                    PersonalUT=Planetarium.GetUniversalTime(),SurvivalUT=clock.DisplayTime,
                    LaunchUT=clock.LaunchTime,FreezeOffset=clock.FreezeOffset,RecordedAt=Time.realtimeSinceStartup}};
            splitting=__state;SurvivalClock.Context=clock;
        }
        private static Exception AfterSplit(Exception __exception,SplitScope __state)
        {
            if(__state!=null){splitting=__state.Previous;SurvivalClock.Context=__state.PreviousClock;}
            return __exception;
        }
        private static void InitializedSplit(Vessel __instance,bool __result)
        {
            var scope=splitting;
            if(!__result || scope==null || __instance==null || __instance==scope.Parent ||
                __instance.id==scope.Parent.id || __instance.isEVA ||
                __instance.rootPart==null || !scope.Parts.Contains(__instance.rootPart))return;
            var clock=__instance.GetComponent<SurvivalClock>();
            if(clock==null)throw new InvalidOperationException("New split vessel survival clock missing");
            clock.InheritBirth(scope.Epoch);
            Debug.Log("[AetherfrontContinuity] Split inherited survival epoch from "+scope.Parent.id+" to "+__instance.id+
                " at "+scope.Epoch.SurvivalUT.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        }
        private static void OnEva(GameEvents.FromToAction<Part,Part> transfer)
        {
            if(!SimulationAuthority.InMultiplayer || transfer.from==null || transfer.to==null)return;
            var source=transfer.from.vessel;var child=transfer.to.vessel;
            if(source==null || child==null || !child.isEVA || source.id==child.id || !SimulationAuthority.MaySimulate(source))return;
            var clock=source.GetComponent<SurvivalClock>();
            if(clock==null || !clock.Prepare())throw new InvalidOperationException("EVA parent survival clock unavailable");
            if(Births.Count>=256)throw new InvalidOperationException("Too many pending local survival births");
            double personal=Planetarium.GetUniversalTime(),survival=clock.DisplayTime;
            Births[child.id]=new Birth{Owner=SurvivalClock.Player,PersonalUT=personal,SurvivalUT=survival,LaunchUT=survival,FreezeOffset=clock.FreezeOffset,RecordedAt=Time.realtimeSinceStartup};
            Debug.Log("[AetherfrontContinuity] EVA inherited survival epoch from "+source.id+" to "+child.id+" at "+survival.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        }
        internal static bool TakeBirth(Guid id,string player,out Birth birth)
        {
            if(!Births.TryGetValue(id,out birth))return false;
            if(birth.Owner!=player || Time.realtimeSinceStartup-birth.RecordedAt>300)
                throw new InvalidOperationException("Expired or foreign survival birth refused");
            Births.Remove(id);
            return true;
        }
    }
}
