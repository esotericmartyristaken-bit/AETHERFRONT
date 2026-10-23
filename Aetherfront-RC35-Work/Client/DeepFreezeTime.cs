using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    // DF dates retain their original origin, but elapsed time follows the
    // vessel's survival clock. No rewrite of legacy crew/power dates on load.
    internal static class DeepFreezeTime
    {
        private static FieldInfo handlerVessel,interestedVessel,partVessel,crewVessel,partElectricity,crewUpdate,partDeadline;
        private static readonly MethodInfo Utc=AccessTools.Method(typeof(Planetarium),nameof(Planetarium.GetUniversalTime));
        private static MethodInfo vesselCrewCount,partCrewCount;
        internal static void Install(Harmony harmony)=>Configure((target,prefix,transpiler)=>harmony.Patch(target,prefix:prefix,transpiler:transpiler));
        internal static void Configure(Action<MethodInfo,HarmonyMethod,HarmonyMethod> patch)
        {
            var freezer=AccessTools.TypeByName("DF.DeepFreezer");
            if(freezer==null||freezer.Assembly.ManifestModule.ModuleVersionId!=new Guid("3dfd9a02-2102-4022-9ef5-8326bd22a723"))
                throw new InvalidOperationException("Unreviewed DeepFreeze timer image");
            var part=freezer.Assembly.GetType("DF.PartInfo",true);var crew=freezer.Assembly.GetType("DF.KerbalInfo",true);
            partVessel=AccessTools.Field(part,"vesselID");crewVessel=AccessTools.Field(crew,"vesselID");
            partElectricity=AccessTools.Field(part,"timeLastElectricity");crewUpdate=AccessTools.Field(crew,"lastUpdate");
            partDeadline=AccessTools.Field(part,"deathCounter");
            if(partVessel?.FieldType!=typeof(Guid)||crewVessel?.FieldType!=typeof(Guid)||partElectricity?.FieldType!=typeof(double)||crewUpdate?.FieldType!=typeof(double)||partDeadline?.FieldType!=typeof(double))
                throw new InvalidOperationException("DeepFreeze date identity signatures changed");
            var expected=new Dictionary<string,int>{{"FixedUpdate",5},{"ChkOngoingEC",1},{"ChkOngoingTemp",1},{"OnStart",1},{"ThawKerbalStep4",1},{"RemoveKerbal",1},{"onVesselGoOffRails",1},{"resetCryopods",1}};
            foreach(var item in expected)
            {
                var method=AccessTools.GetDeclaredMethods(freezer).Single(m=>m.Name==item.Key);
                RequireReads(method,item.Value);
                patch(method,null,new HarmonyMethod(typeof(DeepFreezeTime),nameof(PartTime)));
            }
            var background=AccessTools.DeclaredMethod(freezer,"FixedBackgroundUpdate");RequireReads(background,1);
            patch(background,new HarmonyMethod(typeof(DeepFreezeTime),nameof(BackgroundAuthority)),
                new HarmonyMethod(typeof(DeepFreezeTime),nameof(BackgroundTime)));
            var memory=freezer.Assembly.GetType("DF.DFIntMemory",true);
            foreach(var name in new[]{"UpdatePredictedVesselEC","UpdateVesselInfo","UpdateVesselCounts"})
            {
                var method=AccessTools.DeclaredMethod(memory,name);
                if(method==null||method.GetParameters().Length!=3||method.GetParameters()[1].ParameterType!=typeof(Vessel)||method.GetParameters()[2].ParameterType!=typeof(double))
                    throw new InvalidOperationException("DeepFreeze vessel timer helper changed: "+name);
                RequireReads(method,name=="UpdateVesselCounts"?0:1);
                patch(method,new HarmonyMethod(typeof(DeepFreezeTime),nameof(VesselTime)),
                    name=="UpdateVesselCounts"?null:new HarmonyMethod(typeof(DeepFreezeTime),name=="UpdatePredictedVesselEC"?nameof(PredictionTime):nameof(RegistryTime)));
            }
            var coma=AccessTools.DeclaredMethod(memory,"CheckComaUpdate");RequireReads(coma,1);
            patch(coma,null,new HarmonyMethod(typeof(DeepFreezeTime),nameof(ComaAge)));
            var gui=freezer.Assembly.GetType("DF.DeepFreezeGUI",true);
            var window=AccessTools.DeclaredMethod(gui,"windowDF");RequireReads(window,8);
            patch(window,null,new HarmonyMethod(typeof(DeepFreezeTime),nameof(MonitorAge)));
            var handler=AccessTools.TypeByName("BackgroundResources.DeepFreezer");
            if(handler==null||handler.Assembly.ManifestModule.ModuleVersionId!=new Guid("bf880902-51b0-4db1-9084-ddc4be8e00e6"))
                throw new InvalidOperationException("Unreviewed unloaded freezer image");
            handlerVessel=AccessTools.Field(handler,"vessel");
            interestedVessel=AccessTools.Field(handlerVessel?.FieldType,"vessel");
            if(interestedVessel?.FieldType!=typeof(Vessel))throw new InvalidOperationException("Unloaded freezer vessel lookup changed");
            var process=AccessTools.DeclaredMethod(handler,"ProcessHandler");RequireReads(process,1);
            patch(process,null,new HarmonyMethod(typeof(DeepFreezeTime),nameof(UnloadedTime)));
        }
        private static void RequireReads(MethodInfo method,int count)
        {
            if(method==null||PatchProcessor.GetOriginalInstructions(method).Count(c=>c.Calls(Utc))!=count)
                throw new InvalidOperationException("Unreviewed native cryopod UTC reader: "+method);
        }
        internal static double For(Vessel vessel)
        {
            if(!SimulationAuthority.InMultiplayer||vessel==null)return Planetarium.GetUniversalTime();
            var clock=vessel.GetComponent<SurvivalClock>();
            if(clock==null)
            {
                if(!FlightGlobals.ready)return Planetarium.GetUniversalTime();
                throw new InvalidOperationException("Cryopod vessel survival clock unavailable");
            }
            if(FlightGlobals.ready&&SimulationAuthority.MaySimulate(vessel))clock.Prepare();
            return clock.FreezeTime;
        }
        private static double ForPart(PartModule part)=>For(part.vessel);
        private static double ForHandler(object handler)=>For(interestedVessel.GetValue(handlerVessel.GetValue(handler)) as Vessel);
        private static bool BackgroundAuthority(Vessel __0)=>!SimulationAuthority.InMultiplayer||SimulationAuthority.MaySimulate(__0);
        private static bool VesselTime(Vessel __1,ref double __2)
        {
            if(!SimulationAuthority.InMultiplayer)return true;
            if(!SimulationAuthority.MaySimulate(__1))return false;
            __2=For(__1);return true;
        }
        private static double RecordAge(object record,FieldInfo identity,FieldInfo date)
        {
            double last=(double)date.GetValue(record);
            if(!SimulationAuthority.InMultiplayer)return Planetarium.GetUniversalTime()-last;
            var vessel=FlightGlobals.FindVessel((Guid)identity.GetValue(record));
            return For(vessel)-last;
        }
        private static double SinceCrewUpdate(object record)=>RecordAge(record,crewVessel,crewUpdate);
        private static double SinceElectricity(object record)=>RecordAge(record,partVessel,partElectricity);
        private static IEnumerable<CodeInstruction> PartTime(IEnumerable<CodeInstruction> source)=>Rewrite(source,OpCodes.Ldarg_0,nameof(ForPart));
        private static IEnumerable<CodeInstruction> BackgroundTime(IEnumerable<CodeInstruction> source)=>Rewrite(source,OpCodes.Ldarg_0,nameof(For));
        private static IEnumerable<CodeInstruction> RegistryTime(IEnumerable<CodeInstruction> source)=>Rewrite(source,OpCodes.Ldarg_2,nameof(For));
        private static IEnumerable<CodeInstruction> HandlerTime(IEnumerable<CodeInstruction> source)=>Rewrite(source,OpCodes.Ldarg_0,nameof(ForHandler));
        private static void KeepFailureDeadline(object record,double nativeTime)
        {
            // A prediction refresh must not restart the starvation countdown.
            // The real power handlers own its initialization and reset.
            if(!SimulationAuthority.InMultiplayer)partDeadline.SetValue(record,nativeTime);
        }
        private static IEnumerable<CodeInstruction> PredictionTime(IEnumerable<CodeInstruction> source)
        {
            int changed=0;var code=RegistryTime(source).ToArray();
            foreach(var instruction in code)
                if(instruction.opcode==OpCodes.Stfld&&Equals(instruction.operand,partDeadline))
                {
                    instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(DeepFreezeTime),nameof(KeepFailureDeadline));changed++;
                }
            if(changed!=1)throw new InvalidOperationException("Native cryopod prediction deadline changed");return code;
        }
        private static IEnumerable<CodeInstruction> UnloadedTime(IEnumerable<CodeInstruction> source)
        {
            var code=HandlerTime(source).ToArray();int changed=0;int insert=-1;
            var step=AccessTools.PropertyGetter(typeof(TimeWarp),nameof(TimeWarp.fixedDeltaTime));
            for(int i=4;i+2<code.Length;i++)
                if(code[i].Calls(step))
                {
                    var count=code[i-3].operand as MethodInfo;
                    if(code[i-4].opcode!=OpCodes.Ldloc_0||count?.DeclaringType.FullName!="BackgroundResources.DFWrapper+VesselInfo"||count.Name!="get_numFrozenCrew"||
                        code[i-2].opcode!=OpCodes.Conv_R8||code[i-1].opcode!=OpCodes.Mul||code[i+1].opcode!=OpCodes.Conv_R8||code[i+2].opcode!=OpCodes.Mul)
                        throw new InvalidOperationException("Unreviewed unloaded cryopod power calculation");
                    // The expression already integrates elapsed seconds. Do not
                    // multiply by another timestep or by all pods' crew again.
                    var part=count.DeclaringType.Assembly.GetType("BackgroundResources.DFWrapper+PartInfo",true);
                    vesselCrewCount=count;partCrewCount=AccessTools.PropertyGetter(part,"numFrznCrew");
                    if(partCrewCount==null)throw new InvalidOperationException("Native unloaded cryopod part count missing");
                    code[i-3].opcode=OpCodes.Ldloc_1;code[i-3].operand=null;
                    insert=i-2;
                    code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(DeepFreezeTime),nameof(StepFactor));changed++;
                }
            if(changed!=1)throw new InvalidOperationException("Native unloaded cryopod power multiplier changed");
            int withdrawals=0;
            for(int i=1;i<code.Length;i++)
            {
                var request=code[i].operand as MethodInfo;
                if(request?.DeclaringType.FullName!="BackgroundResources.UnloadedResourceProcessing"||request.Name!="RequestResource")continue;
                if(code[i-1].opcode!=OpCodes.Ldc_I4_1||request.GetParameters().Length!=5||request.GetParameters()[4].ParameterType!=typeof(bool))
                    throw new InvalidOperationException("Native unloaded cryopod resource direction changed");
                // Native true means add resource. A running multiplayer freezer
                // must withdraw its maintenance charge rather than generate it.
                code[i-1].opcode=OpCodes.Call;code[i-1].operand=AccessTools.Method(typeof(DeepFreezeTime),nameof(UsesLegacyPush));withdrawals++;
            }
            if(withdrawals!=1)throw new InvalidOperationException("Native unloaded cryopod request count changed");
            var result=code.ToList();result.Insert(insert,new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(DeepFreezeTime),nameof(SelectedCrew))));return result;
        }
        private static int SelectedCrew(object vessel,object part)
        {
            bool multiplayer=SimulationAuthority.InMultiplayer;
            return (int)(multiplayer?partCrewCount:vesselCrewCount).Invoke(multiplayer?part:vessel,null);
        }
        private static float StepFactor()=>SimulationAuthority.InMultiplayer?1f:TimeWarp.fixedDeltaTime;
        private static bool UsesLegacyPush()=>!SimulationAuthority.InMultiplayer;
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source,OpCode load,string replacement)
        {
            foreach(var code in source)
            {
                if(code.Calls(Utc))
                {
                    code.opcode=load;code.operand=null;yield return code;
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(DeepFreezeTime),replacement));
                }
                else yield return code;
            }
        }
        private static IEnumerable<CodeInstruction> ComaAge(IEnumerable<CodeInstruction> source)=>RewriteAge(source,"DF.KerbalInfo","lastUpdate",nameof(SinceCrewUpdate),1);
        private static IEnumerable<CodeInstruction> MonitorAge(IEnumerable<CodeInstruction> source)=>RewriteAge(source,"DF.PartInfo","timeLastElectricity",nameof(SinceElectricity),8);
        private static IEnumerable<CodeInstruction> RewriteAge(IEnumerable<CodeInstruction> source,string record,string date,string replacement,int expected)
        {
            var code=source.ToArray();int rewritten=0;bool pending=false;
            for(int i=0;i<code.Length;i++)
            {
                if(code[i].Calls(Utc))
                {
                    if(pending)throw new InvalidOperationException("Overlapping native cryopod date calculations");
                    code[i].opcode=OpCodes.Nop;code[i].operand=null;pending=true;
                }
                var field=code[i].operand as FieldInfo;
                if(pending&&code[i].opcode==OpCodes.Ldfld&&field?.DeclaringType.FullName==record&&field.Name==date)
                {
                    if(i+1>=code.Length||code[i+1].opcode!=OpCodes.Sub)throw new InvalidOperationException("Native cryopod elapsed-date calculation changed");
                    code[i].opcode=OpCodes.Call;code[i].operand=AccessTools.Method(typeof(DeepFreezeTime),replacement);
                    code[i+1].opcode=OpCodes.Nop;pending=false;rewritten++;
                }
            }
            if(pending||rewritten!=expected)throw new InvalidOperationException("Native cryopod elapsed-date count changed: "+rewritten);
            return code;
        }
    }
}
