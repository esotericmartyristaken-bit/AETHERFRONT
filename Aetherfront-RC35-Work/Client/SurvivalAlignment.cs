using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LifeSupport;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class SurvivalAlignment
    {
        private static readonly string[] CrewDates={"LastMeal","LastEC","LastUpdate","LastAtHome","LastSOIChange","TimeEnteredVessel"};
        private static readonly string[] SupplyDates={"LastUpdate","LastFeeding","LastECCheck"};
        private static readonly FieldInfo ConverterTime=AccessTools.Field(typeof(BaseConverter),"lastUpdateTime");
        private static readonly FieldInfo[] LifeTimes=new[]{"LastUpdateTime","_lastProcessingTime","_lastUpdate"}
            .Select(name=>AccessTools.Field(typeof(ModuleLifeSupportSystem),name)).ToArray();
        private sealed class TimerChange
        {
            internal object Target;internal FieldInfo Field;internal double Before,After;
            internal void Apply(bool restore)=>Field.SetValue(Target,restore?Before:After);
        }
        private sealed class Plan
        {
            internal SurvivalClock Clock;internal ConfigNode Before,After;internal List<TimerChange> Timers;
            internal List<FreezeTimerAlignment.Change> FreezeTimers;
            internal void Apply(bool restore)
            {
                Clock.RestoreAlignment(restore?Before:After);
                foreach(var timer in Timers)timer.Apply(restore);
                foreach(var timer in FreezeTimers)timer.Apply(restore);
            }
        }
        internal static void Install(Harmony harmony)
        {
            var couple=AccessTools.DeclaredMethod(typeof(Part),nameof(Part.Couple),new[]{typeof(Part)});
            if(couple==null||couple.ReturnType!=typeof(void)||ConverterTime?.FieldType!=typeof(double)||LifeTimes.Any(f=>f?.FieldType!=typeof(double)))
                throw new InvalidOperationException("Survival coupling timer signatures changed");
            harmony.Patch(couple,prefix:new HarmonyMethod(typeof(SurvivalAlignment),nameof(BeforeCouple)){priority=Priority.First});
        }
        private static void BeforeCouple(Part __instance,Part __0)
        {
            if(!SimulationAuthority.InMultiplayer||!HighLogic.LoadedSceneIsFlight||__instance==null||__0==null)return;
            var left=__instance.vessel;var right=__0.vessel;
            if(left==null||right==null||left==right)return;
            // Candidate coverage: both ships under this pilot's simulation
            // authority. Active cross-pilot and frozen-crew joining still need
            // authoritative snapshot handling and remain explicit release gates.
            // Do not forbid those native operations merely to narrow the test.
            if(!SimulationAuthority.MaySimulate(left)||!SimulationAuthority.MaySimulate(right))return;
            var a=left.GetComponent<SurvivalClock>();var b=right.GetComponent<SurvivalClock>();
            if(a==null||b==null)throw new InvalidOperationException("Coupling survival clocks missing");
            var beforeA=a.CaptureAlignment();var beforeB=b.CaptureAlignment();
            var names=beforeA.GetNodes("CREW").Concat(beforeB.GetNodes("CREW")).Select(n=>n.GetValue("KerbalName")).ToArray();
            if(names.Distinct(StringComparer.Ordinal).Count()!=names.Length)throw new InvalidDataException("Coupling snapshots repeat a crewmember");
            double personal=Planetarium.GetUniversalTime();
            double nowA=SurvivalClock.ValidateSnapshot(beforeA).Now(SurvivalClock.Player,personal);
            double nowB=SurvivalClock.ValidateSnapshot(beforeB).Now(SurvivalClock.Player,personal);
            double freezeA=nowA+SurvivalClock.SnapshotFreezeOffset(beforeA),freezeB=nowB+SurvivalClock.SnapshotFreezeOffset(beforeB);
            if(nowA==nowB&&freezeA==freezeB)return;
            double common=Math.Max(nowA,nowB);
            double commonFreeze=Math.Max(freezeA,freezeB);
            // Build and validate both complete plans before changing either
            // ship. Every native pre-couple save then sees consistent separate
            // topologies, with elapsed ages and converter intervals preserved.
            var plans=new[]{Build(a,left,beforeA,common,commonFreeze,personal),Build(b,right,beforeB,common,commonFreeze,personal)};
            try{foreach(var plan in plans)plan.Apply(false);}
            catch(Exception original)
            {
                var failures=new List<Exception>{original};
                foreach(var plan in plans)try{plan.Apply(true);}catch(Exception rollback){failures.Add(rollback);}
                throw new AggregateException("Survival coupling alignment failed before native topology mutation",failures);
            }
            Debug.Log("[AetherfrontContinuity] Aligned locally authoritative coupling clocks without advancing crew ages: "+left.id+" + "+right.id);
        }
        private static Plan Build(SurvivalClock clock,Vessel vessel,ConfigNode before,double common,double commonFreeze,double personal)
        {
            double delta=common-SurvivalClock.ValidateSnapshot(before).Now(SurvivalClock.Player,personal);
            var plan=new Plan{Clock=clock,Before=before,After=RebaseSnapshot(before,common,personal),Timers=new List<TimerChange>()};
            Set(plan.After,"freezeOffset",commonFreeze-common);
            SurvivalClock.ValidateSnapshot(plan.After);
            double beforeFreeze=common-delta+SurvivalClock.SnapshotFreezeOffset(before);
            plan.FreezeTimers=FreezeTimerAlignment.Plan(vessel,commonFreeze-beforeFreeze);
            foreach(var life in vessel.vesselModules.OfType<ModuleLifeSupportSystem>())
                foreach(var field in LifeTimes)plan.Timers.Add(Timer(life,field,delta));
            foreach(var converter in vessel.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<BaseConverter>())
                plan.Timers.Add(Timer(converter,ConverterTime,delta));
            return plan;
        }
        private static TimerChange Timer(object target,FieldInfo field,double delta)
        {
            double value=(double)field.GetValue(target);
            return new TimerChange{Target=target,Field=field,Before=value,After=ShiftTimer(value,delta)};
        }
        internal static double ShiftTimer(double value,double delta)
        {
            if(!Finite(value)||!Finite(delta)||delta<0)throw new InvalidDataException("Invalid coupling timer");
            // Native converter/USI timer zero (or negative) means uninitialized.
            // Keep that sentinel so the normal first tick initializes it.
            double result=value<=1e-9?value:value+delta;
            if(!Finite(result))throw new InvalidDataException("Coupling timer overflow");return result;
        }
        internal static ConfigNode RebaseSnapshot(ConfigNode saved,double target,double personal)
        {
            var epoch=SurvivalClock.ValidateSnapshot(saved);
            if(saved.GetValue("schema")!="2"&&saved.GetValue("schema")!="3")throw new InvalidDataException("Live coupling checkpoint must include its launch anchor");
            _=new SurvivalEpoch(epoch.Owner,personal,target);
            double delta=target-epoch.Now(epoch.Owner,personal);
            if(delta<0)throw new InvalidDataException("Coupling cannot move a survival clock backwards");
            var result=saved.CreateCopy();
            double freezeOffset=SurvivalClock.SnapshotFreezeOffset(saved)-delta;
            result.SetValue("schema","3");result.SetValue("freezeOffset",freezeOffset.ToString("R",CultureInfo.InvariantCulture),true);
            Set(result,"ownerUT",personal);Set(result,"survivalUT",target);
            Shift(result,"launchUT",delta,false);
            foreach(var crew in result.GetNodes("CREW"))
            {
                foreach(var field in CrewDates)Shift(crew,field,delta,false);
                Shift(crew,"MaxOffKerbinTime",delta,true);
            }
            foreach(var supply in result.GetNodes("SUPPLY"))foreach(var field in SupplyDates)Shift(supply,field,delta,false);
            SurvivalClock.ValidateSnapshot(result);return result;
        }
        private static void Shift(ConfigNode node,string key,double delta,bool preserveZero)
        {
            double value=double.Parse(node.GetValue(key),NumberStyles.Float,CultureInfo.InvariantCulture);
            double shifted=value<0||(preserveZero&&value==0)?value:value+delta;
            if(!Finite(shifted))throw new InvalidDataException("Coupling timestamp overflow: "+key);
            Set(node,key,shifted);
        }
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        private static void Set(ConfigNode node,string key,double value)
        {
            if(!node.SetValue(key,value.ToString("R",CultureInfo.InvariantCulture)))throw new InvalidDataException("Coupling timestamp missing: "+key);
        }
    }
}
