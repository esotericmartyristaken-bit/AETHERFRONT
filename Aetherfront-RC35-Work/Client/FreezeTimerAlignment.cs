using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    internal static class FreezeTimerAlignment
    {
        internal sealed class Change
        {
            internal object Target,Before,After;internal FieldInfo Field;
            internal void Apply(bool restore)=>Field.SetValue(Target,restore?Before:After);
        }
        internal static List<Change> Plan(Vessel vessel,double delta)
        {
            var result=new List<Change>();
            if(!Finite(delta)||delta<0)throw new InvalidDataException("Invalid cryopod coupling offset");
            if(delta==0)return result;
            foreach(var part in vessel.parts)
                foreach(PartModule module in part.Modules)
                    if(module.GetType().FullName=="DF.DeepFreezer")
                        Add(result,module,delta,"deathCounter","tmpdeathCounter","timeLoadedOffrails","timeSinceLastECtaken","timeSinceLastTmpChk","cryopodResetTime");
            var type=AccessTools.TypeByName("DF.DeepFreeze");var instance=AccessTools.Field(type,"Instance")?.GetValue(null);
            if(instance==null)
            {
                if(result.Count!=0)throw new InvalidOperationException("Cryopod coupling registry unavailable");
                return result;
            }
            var registry=AccessTools.Field(type,"DFgameSettings").GetValue(instance);
            var vessels=(IDictionary)AccessTools.Field(registry.GetType(),"knownVessels").GetValue(registry);
            if(vessels.Contains(vessel.id))Add(result,vessels[vessel.id],delta,"lastUpdate");
            foreach(var name in new[]{"KnownFrozenKerbals","knownFreezerParts"})
            {
                var rows=(IDictionary)AccessTools.Field(registry.GetType(),name).GetValue(registry);
                foreach(DictionaryEntry row in rows)
                    if((Guid)AccessTools.Field(row.Value.GetType(),"vesselID").GetValue(row.Value)==vessel.id)
                        if(name=="KnownFrozenKerbals")Add(result,row.Value,delta,"lastUpdate");
                        else Add(result,row.Value,delta,"timeLastElectricity","timeLastTempCheck","deathCounter","tmpdeathCounter","lastUpdate");
            }
            return result;
        }
        private static void Add(List<Change> result,object target,double delta,params string[] names)
        {
            foreach(string name in names)
            {
                var field=AccessTools.Field(target.GetType(),name);
                if(field==null||field.FieldType!=typeof(double)&&field.FieldType!=typeof(float))
                    throw new InvalidOperationException("Native cryopod timer signature changed: "+name);
                object before=field.GetValue(target);double value=Convert.ToDouble(before);
                double after=ShiftDate(value,delta,name=="cryopodResetTime");
                object converted=field.FieldType==typeof(float)?(object)(float)after:after;
                if(!Finite(Convert.ToDouble(converted)))throw new InvalidDataException("Native cryopod float date overflow");
                result.Add(new Change{Target=target,Field=field,Before=before,After=converted});
            }
        }
        internal static double ShiftDate(double value,double delta,bool preserveZero)
        {
            if(!Finite(value)||!Finite(delta)||delta<0)throw new InvalidDataException("Invalid cryopod date alignment");
            double after=value<0||preserveZero&&value==0?value:value+delta;
            if(!Finite(after)||after>1e12)throw new InvalidDataException("Cryopod date overflow");return after;
        }
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
