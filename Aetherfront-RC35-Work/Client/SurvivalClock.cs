using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using LifeSupport;
using LmpClient.Systems.SettingsSys;

namespace Aetherfront.Continuity.Client
{
    // Saved in the normal ProtoVessel, alongside the resources it describes.
    // No FixedUpdate, fleet scan, scene object or extra network message per vessel.
    public sealed class SurvivalClock : VesselModule
    {
        private const string NodeName="APF_SURVIVAL";
        private SurvivalEpoch epoch;
        private ConfigNode incoming;
        private bool restored;
        private string invalid;
        private double? virtualLaunch;
        private double? freezeOffset;
        [ThreadStatic] internal static SurvivalClock Context;
        [ThreadStatic] internal static double? DisplayUT;
        internal static string Player => SettingsSystem.CurrentSettings.PlayerName;
        internal string SimulationOwner => epoch==null?string.Empty:epoch.Owner;
        internal double LaunchTime => virtualLaunch??Vessel.launchTime;
        internal double FreezeOffset => freezeOffset??(epoch==null?0:epoch.OwnerUT-epoch.SurvivalUT);
        internal double FreezeTime => (epoch!=null&&!restored?epoch.SurvivalUT:DisplayTime)+FreezeOffset;
        internal static SurvivalEpoch ValidateSnapshot(ConfigNode saved)
        {
            string schema=saved?.GetValue("schema");
            var fields=schema=="3"?new[]{"schema","owner","ownerUT","survivalUT","launchUT","freezeOffset"}:
                schema=="2"?new[]{"schema","owner","ownerUT","survivalUT","launchUT"}:new[]{"schema","owner","ownerUT","survivalUT"};
            if(saved==null || (schema!="1"&&schema!="2"&&schema!="3") || saved.values.Count!=fields.Length ||
               fields.Any(key=>saved.GetValues(key).Length!=1) ||
               saved.nodes.Cast<ConfigNode>().Any(n=>n.name!="CREW"&&n.name!="SUPPLY") ||
               saved.GetNodes("CREW").Length>128 || saved.GetNodes("SUPPLY").Length>1)
                throw new InvalidDataException("Invalid survival snapshot schema or count");
            var result=new SurvivalEpoch(saved.GetValue("owner"),Number(saved,"ownerUT"),Number(saved,"survivalUT"));
            if(schema=="2"||schema=="3")
            {
                double launch=Number(saved,"launchUT");
                if(launch<0||launch>result.SurvivalUT+2)throw new InvalidDataException("Invalid virtual launch epoch");
            }
            if(schema=="3")ValidateFreezeOffset(result.SurvivalUT,Number(saved,"freezeOffset"));
            var crew=saved.GetNodes("CREW").Select(Read<LifeSupportStatus>).ToArray();
            if(crew.Select(c=>c.KerbalName).Distinct(StringComparer.Ordinal).Count()!=crew.Length)
                throw new InvalidDataException("Duplicate survival crew");
            foreach(var supply in saved.GetNodes("SUPPLY"))Read<VesselSupplyStatus>(supply);
            return result;
        }
        internal static double SnapshotFreezeOffset(ConfigNode saved)
        {
            var value=ValidateSnapshot(saved);
            return saved.GetValue("schema")=="3"?Number(saved,"freezeOffset"):value.OwnerUT-value.SurvivalUT;
        }
        internal static void ValidateFreezeOffset(double survival,double offset)
        {
            double time=survival+offset;
            if(double.IsNaN(offset)||double.IsInfinity(offset)||Math.Abs(offset)>1e12||time<0||time>1e12||double.IsNaN(time)||double.IsInfinity(time))
                throw new InvalidDataException("Invalid cryopod clock origin");
        }
        public static double UniversalTime() => DisplayUT ?? (Context == null ? Planetarium.GetUniversalTime() : Context.epoch.Now(Player,Planetarium.GetUniversalTime()));
        internal double DisplayTime => epoch==null?Planetarium.GetUniversalTime():SimulationAuthority.MaySimulate(Vessel)&&epoch.Owner==Player?epoch.Now(Player,Planetarium.GetUniversalTime()):epoch.SurvivalUT;
        public static double MissionTime(Vessel vessel)
        {
            if(!SimulationAuthority.InMultiplayer || vessel==null)return vessel==null?0:vessel.missionTime;
            var clock=vessel.GetComponent<SurvivalClock>();
            if(clock==null || clock.epoch==null)return vessel.missionTime;
            if(!vessel.isEVA && vessel.situation==Vessel.Situations.PRELAUNCH)return 0;
            return Math.Max(0,clock.DisplayTime-(clock.virtualLaunch??vessel.launchTime));
        }
        public override int GetOrder() => 0;

        protected override void OnLoad(ConfigNode node)
        {
            incoming=null; epoch=null; restored=false; invalid=null;virtualLaunch=null;freezeOffset=null;
            var saved=node.GetNode(NodeName);
            if(saved==null)return; // First migration retains the existing local USI epoch.
            try
            {
                epoch=ValidateSnapshot(saved);
                if(saved.GetValue("schema")!="1")virtualLaunch=Number(saved,"launchUT");
                // Legacy DF timers were written in the saved pilot's personal
                // UT. Keep that origin while changing authority, rather than
                // rewriting timestamps or granting a catch-up-free interval.
                freezeOffset=SnapshotFreezeOffset(saved);
                incoming=saved.CreateCopy();
            }
            catch(Exception e){invalid=e.GetType().Name+": "+e.Message;}
        }

        internal bool CanRetainDeferredSnapshot(string authority)
        {
            // Tracking has the vessel proto but USI's scene settings are absent.
            // Nothing has simulated against this un-restored snapshot. Retain it
            // verbatim until USI is available, including its catch-up timestamps.
            if(restored||incoming==null||epoch==null||epoch.Owner!=authority)return false;
            var saved=ValidateSnapshot(incoming);
            return saved.Owner==epoch.Owner&&saved.OwnerUT==epoch.OwnerUT&&saved.SurvivalUT==epoch.SurvivalUT;
        }
        internal bool Prepare()
        {
            if(invalid!=null)throw new InvalidDataException("Vessel survival snapshot refused: "+invalid);
            // USI intentionally uses a managed-only MonoBehaviour as a data
            // container. Unity's overloaded == calls it null despite valid data.
            if(LifeSupportScenario.Instance==null || ReferenceEquals(LifeSupportScenario.Instance.settings,null))return false;
            AdoptBirth();
            if(epoch==null)epoch=new SurvivalEpoch(Player,Planetarium.GetUniversalTime(),Planetarium.GetUniversalTime());
            if(!freezeOffset.HasValue)freezeOffset=epoch.OwnerUT-epoch.SurvivalUT;
            if(!restored)epoch.RecoverLoadedCheckpoint(Player,Planetarium.GetUniversalTime());
            epoch.Bind(Player,Planetarium.GetUniversalTime());
            if(!Vessel.isEVA && Vessel.situation==Vessel.Situations.PRELAUNCH)
                virtualLaunch=epoch.Now(Player,Planetarium.GetUniversalTime());
            if(restored)return true;
            var old=Context; Context=this;
            try
            {
                if(incoming!=null)
                {
                    // Validate every record before mutating the USI scenario.
                    var crew=Vessel.GetVesselCrew();
                    var data=incoming.GetNodes("CREW").Select(Read<LifeSupportStatus>).ToArray();
                    if(data.Select(x=>x.KerbalName).Distinct(StringComparer.Ordinal).Count()!=data.Length ||
                       data.Any(x=>!crew.Any(k=>k.name==x.KerbalName)))throw new InvalidDataException("Crew survival record does not belong to this vessel");
                    var supplyNode=incoming.GetNode("SUPPLY");
                    var supply=supplyNode==null?null:Read<VesselSupplyStatus>(supplyNode);
                    if(supply!=null && supply.VesselId!=Vessel.id.ToString())throw new InvalidDataException("Supply record belongs to another vessel");
                    var manager=LifeSupportManager.Instance;
                    foreach(var status in data)
                    {
                        var target=manager.FetchKerbal(crew.Single(k=>k.name==status.KerbalName));
                        Copy(status,target); manager.TrackKerbal(target);
                    }
                    if(supply!=null)
                    {
                        var target=manager.FetchVessel(supply.VesselId); Copy(supply,target); manager.TrackVessel(target);
                        foreach(var module in Vessel.vesselModules.OfType<ModuleLifeSupportSystem>())module.VesselStatus=target;
                    }
                }
                restored=true; incoming=null; return true;
            }
            finally {Context=old;}
        }

        protected override void OnSave(ConfigNode node)
        {
            // The normal EVA callback can request a proto before its control
            // lock reply arrives. Persist the trusted parent epoch even then.
            if(SimulationAuthority.InMultiplayer)AdoptBirth();
            if(SimulationAuthority.InMultiplayer && SimulationAuthority.MaySimulate(Vessel) && Prepare())
            {
                incoming=CaptureSnapshot();
            }
            if(incoming!=null)node.AddNode(incoming.CreateCopy());
        }
        private void AdoptBirth()
        {
            SurvivalTopology.Birth birth;
            if(!SurvivalTopology.TakeBirth(Vessel.id,Player,out birth))return;
            InheritBirth(birth);
        }
        internal void InheritBirth(SurvivalTopology.Birth birth)
        {
            epoch=new SurvivalEpoch(birth.Owner,birth.PersonalUT,birth.SurvivalUT);
            virtualLaunch=birth.LaunchUT;freezeOffset=birth.FreezeOffset;incoming=null;restored=true;invalid=null;
            incoming=CaptureSnapshot();
        }
        internal ConfigNode CaptureAlignment()
        {
            if(!SimulationAuthority.MaySimulate(Vessel)||!Prepare())throw new InvalidOperationException("Authoritative coupling checkpoint unavailable");
            return CaptureSnapshot();
        }
        internal void RestoreAlignment(ConfigNode saved)
        {
            var wrapper=new ConfigNode(nameof(SurvivalClock));wrapper.AddNode(saved.CreateCopy());
            OnLoad(wrapper);
            if(!Prepare())throw new InvalidOperationException("Coupling checkpoint could not be restored");
        }
        private ConfigNode CaptureSnapshot()
        {
                epoch.Checkpoint(Player,Planetarium.GetUniversalTime());
                var saved=new ConfigNode(NodeName);
                ValidateFreezeOffset(epoch.SurvivalUT,FreezeOffset);
                saved.AddValue("schema","3"); saved.AddValue("owner",epoch.Owner);
                saved.AddValue("ownerUT",epoch.OwnerUT.ToString("R",CultureInfo.InvariantCulture));
                saved.AddValue("survivalUT",epoch.SurvivalUT.ToString("R",CultureInfo.InvariantCulture));
                saved.AddValue("launchUT",(virtualLaunch??Vessel.launchTime).ToString("R",CultureInfo.InvariantCulture));
                saved.AddValue("freezeOffset",FreezeOffset.ToString("R",CultureInfo.InvariantCulture));
                var manager=LifeSupportManager.Instance;
                foreach(var crew in Vessel.GetVesselCrew())
                    if(manager.IsKerbalTracked(crew.name))Write(saved.AddNode("CREW"),manager.FetchKerbal(crew));
                if(manager.IsVesselTracked(Vessel.id.ToString()))Write(saved.AddNode("SUPPLY"),manager.FetchVessel(Vessel.id.ToString()));
                return saved;
        }

        private static double Number(ConfigNode node,string name)
        {
            double number;
            if(!double.TryParse(node.GetValue(name),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||double.IsNaN(number)||double.IsInfinity(number))
                throw new InvalidDataException("Invalid survival number: "+name);
            return number;
        }
        private static class Properties<T>
        {
            internal static readonly PropertyInfo[] Values=typeof(T).GetProperties(BindingFlags.Instance|BindingFlags.Public);
        }
        private static void Write<T>(ConfigNode node,T value)
        {
            foreach(var property in Properties<T>.Values)
            {
                object data=property.GetValue(value,null);
                string text=data is double?((double)data).ToString("R",CultureInfo.InvariantCulture):data is float?((float)data).ToString("R",CultureInfo.InvariantCulture):Convert.ToString(data,CultureInfo.InvariantCulture);
                node.AddValue(property.Name,text??"");
            }
        }
        private static T Read<T>(ConfigNode node) where T:new()
        {
            var value=new T();
            if(node.nodes.Count!=0 || node.values.Count!=Properties<T>.Values.Length)throw new InvalidDataException("Unexpected survival fields");
            foreach(var property in Properties<T>.Values)
            {
                if(node.GetValues(property.Name).Length!=1)throw new InvalidDataException("Missing/duplicate survival field");
                string text=node.GetValue(property.Name);
                if(text.Length>256)throw new InvalidDataException("Oversized survival field");
                object data;
                if(property.PropertyType==typeof(double))data=Number(node,property.Name);
                else if(property.PropertyType==typeof(float)){double number=Number(node,property.Name);if(Math.Abs(number)>float.MaxValue)throw new InvalidDataException("Survival float overflow");data=(float)number;}
                else if(property.PropertyType==typeof(string))data=text;
                else if(property.PropertyType==typeof(int))data=int.Parse(text,NumberStyles.Integer,CultureInfo.InvariantCulture);
                else if(property.PropertyType==typeof(bool))data=bool.Parse(text);
                else throw new InvalidDataException("Unsupported survival field type");
                property.SetValue(value,data,null);
            }
            return value;
        }
        private static void Copy<T>(T source,T target)
        {
            foreach(var property in Properties<T>.Values)property.SetValue(target,property.GetValue(source,null),null);
        }
    }
}
