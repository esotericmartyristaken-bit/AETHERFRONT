using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LifeSupport;
using LmpClient;
using LmpClient.Network;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.VesselResourceSys;
using LmpCommon.Message.Data.Vessel;

namespace Aetherfront.Continuity.Client
{
    public static class ResourceClockClient
    {
        private static readonly FieldInfo ConverterTime=AccessTools.Field(typeof(BaseConverter),"lastUpdateTime");
        private static float nextStaleCrewNotice;
        private static float nextTopologyRequest;
        private static readonly Dictionary<Guid,float> TopologyRequests=new Dictionary<Guid,float>();

        // Invoked on the Unity thread after all resource DTO headers are set and
        // before its resource values are captured, within the same frame.
        public static void CaptureResourceClock(VesselResourceMsgData message,Vessel vessel)
        {
            ResourceClockPayloads.Clear(message);
            if(!MainSystem.IsUnityThread || vessel==null || !SimulationAuthority.InMultiplayer || !SimulationAuthority.MaySimulate(vessel))return;
            var clock=vessel.GetComponent<SurvivalClock>();
            if(clock==null || !clock.Prepare())return;
            var root=new ConfigNode();
            var envelope=root.AddNode("APF_RESOURCE_CLOCK");
            envelope.AddValue("schema","1");envelope.AddValue("vesselId",vessel.id.ToString());
            envelope.AddValue("gameTime",message.GameTime.ToString("R",CultureInfo.InvariantCulture));
            clock.Save(envelope.AddNode(nameof(SurvivalClock)));
            foreach(var life in vessel.vesselModules.OfType<ModuleLifeSupportSystem>())
                life.Save(envelope.AddNode(nameof(ModuleLifeSupportSystem)));
            foreach(var part in vessel.parts)
                for(int index=0;index<part.Modules.Count;index++)
                    if(part.Modules[index] is BaseConverter)
                    {
                        var timer=envelope.AddNode("PART_TIMER");
                        timer.AddValue("uid",part.flightID.ToString(CultureInfo.InvariantCulture));
                        timer.AddValue("index",index.ToString(CultureInfo.InvariantCulture));
                        timer.AddValue("module",part.Modules[index].moduleName);
                        timer.AddValue("lastUpdateTime",((double)ConverterTime.GetValue(part.Modules[index])).ToString("R",CultureInfo.InvariantCulture));
                    }
            // KSP's unnamed wrapper prints an anonymous brace pair; serialize the
            // named node so both KSP and the server parser see the same grammar.
            ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(envelope.ToString()));
        }

        public static void CopyResourceClock(VesselResource value,VesselResourceMsgData message)
            => ResourceClockPayloads.Set(value,ResourceClockPayloads.Get(message));

        internal static void Install(Harmony harmony)
        {
            var target=AccessTools.DeclaredMethod(typeof(VesselResource),"UpdateVesselFields",new[]{typeof(Vessel)});
            if(target==null || ConverterTime==null)throw new InvalidOperationException("Resource clock application signatures changed");
            harmony.Patch(target,prefix:new HarmonyMethod(typeof(ResourceClockClient),nameof(BeforeResourceFields)),
                postfix:new HarmonyMethod(typeof(ResourceClockClient),nameof(AfterResourceFields)));
        }

        private static bool BeforeResourceFields(VesselResource __instance,Vessel vessel,out ConfigNode __state)
        {
            __state=null;
            if(!SimulationAuthority.InMultiplayer)return true;
            if(SimulationAuthority.MaySimulate(vessel))return false;
            byte[] data=ResourceClockPayloads.Get(__instance);
            var clock=vessel.GetComponent<SurvivalClock>();
            if(data.Length==0)return clock==null || clock.SimulationOwner==string.Empty;
            var envelope=ValidateEnvelope(data,vessel.id,__instance.GameTime);
            // Validate the complete resource array before a stale topology can
            // cause a benign defer. Invalid numeric/array data still fails.
            ValidateResourceRows(__instance);
            if(clock==null || vessel.protoVessel==null || vessel.protoVessel.vesselModules==null)
                throw new InvalidDataException("Resource clock target missing");
            var snapshot=envelope.GetNode(nameof(SurvivalClock)).GetNode("APF_SURVIVAL");
            var crew=new HashSet<string>(vessel.GetVesselCrew().Select(c=>c.name),StringComparer.Ordinal);
            if(snapshot.GetNodes("CREW").Any(n=>!crew.Contains(n.GetValue("KerbalName"))))
            {
                // Reliable proto/topology changes and resource traffic can be
                // delivered on different schedules. A valid pre-EVA packet can
                // therefore describe crew already removed by a newer proto.
                // Drop the entire old packet before either resource or clock
                // writes. Malformed envelopes still fail validation above.
                if(UnityEngine.Time.realtimeSinceStartup>=nextStaleCrewNotice)
                {
                    nextStaleCrewNotice=UnityEngine.Time.realtimeSinceStartup+5;
                    UnityEngine.Debug.Log("[AetherfrontContinuity] Discarded topology-stale resource clock for "+vessel.id+"; crew changed before resource delivery.");
                }
                return false;
            }
            if(envelope.HasNode(nameof(ModuleLifeSupportSystem)) && !vessel.vesselModules.OfType<ModuleLifeSupportSystem>().Any())
                throw new InvalidDataException("Resource clock life support target missing");
            foreach(var timer in envelope.GetNodes("PART_TIMER"))
            {
                uint uid=uint.Parse(timer.GetValue("uid"),CultureInfo.InvariantCulture);
                int index=int.Parse(timer.GetValue("index"),CultureInfo.InvariantCulture);
                var part=vessel.protoVessel.protoPartSnapshots.FirstOrDefault(p=>p.flightID==uid);
                if(part==null || index>=part.modules.Count || part.modules[index].moduleName!=timer.GetValue("module"))
                    throw new InvalidDataException("Resource clock converter target mismatch");
                var live=vessel.parts.FirstOrDefault(p=>p.flightID==uid);
                if(live!=null && (index>=live.Modules.Count || !(live.Modules[index] is BaseConverter) || live.Modules[index].moduleName!=timer.GetValue("module")))
                    throw new InvalidDataException("Resource clock loaded converter mismatch");
            }
            var destinations=vessel.protoVessel.protoPartSnapshots.ToDictionary(p=>p.flightID);
            if(!ResourceTargetsMatch(__instance,(id,name)=>destinations.TryGetValue(id,out var part)&&part.resources.Any(r=>r.resourceName==name)))
            {
                // A resource-list change can arrive before its reliable proto,
                // just as crew/topology changes can. Never apply half a packet.
                // Refresh only the affected ship using the ordinary permission-
                // checked server sync; do not manufacture missing resources.
                RequestTopology(vessel.id);return false;
            }
            __state=envelope;return true;
        }

        internal static void ValidateResourceRows(VesselResource message)
        {
            if(message.ResourcesCount<0 || (message.ResourcesCount>0&&message.Resources==null) ||
               (message.Resources!=null&&message.ResourcesCount>message.Resources.Length))
                throw new InvalidDataException("Resource clock resource count mismatch");
            for(int index=0;index<message.ResourcesCount;index++)
            {
                var row=message.Resources[index];
                if(row==null||string.IsNullOrEmpty(row.ResourceName)||row.ResourceName.Length>256||double.IsNaN(row.Amount)||double.IsInfinity(row.Amount)||row.Amount<0)
                    throw new InvalidDataException("Resource clock resource fields invalid");
            }
        }
        internal static bool ResourceTargetsMatch(VesselResource message,Func<uint,string,bool> destination)
        {
            ValidateResourceRows(message);
            for(int index=0;index<message.ResourcesCount;index++)
            {
                var row=message.Resources[index];if(!destination(row.PartFlightId,row.ResourceName))return false;
            }
            return true;
        }
        private static void RequestTopology(Guid id)
        {
            float now=UnityEngine.Time.realtimeSinceStartup;
            if(MainSystem.NetworkState!=LmpCommon.Enums.ClientState.Running||now<nextTopologyRequest)return;
            if(TopologyRequests.TryGetValue(id,out var previous)&&now-previous<10)return;
            foreach(var key in TopologyRequests.Where(p=>now-p.Value>=60).Select(p=>p.Key).ToArray())TopologyRequests.Remove(key);
            if(TopologyRequests.Count>=128)return;
            TopologyRequests[id]=now;nextTopologyRequest=now+1;
            var ids=FlightGlobals.Vessels.Where(v=>v!=null&&v.id!=id).Select(v=>v.id).Distinct().ToArray();
            var request=NetworkMain.CliMsgFactory.CreateNewMessageData<VesselSyncMsgData>();request.VesselsCount=ids.Length;request.VesselIds=ids;
            VesselProtoSystem.Singleton.MessageSender.SendMessage(request);
            UnityEngine.Debug.Log("[AetherfrontContinuity] Deferred topology-stale resource packet and requested current proto for "+id);
        }

        internal static ConfigNode ValidateEnvelope(byte[] data,Guid vesselId,double gameTime)
        {
            ResourceClockPayloads.Validate(data);
            var parsed=ConfigNode.Parse(Encoding.UTF8.GetString(data));
            if(parsed.values.Count!=0 || parsed.nodes.Count!=1)throw new InvalidDataException("Resource clock root mismatch");
            var envelope=parsed.GetNode("APF_RESOURCE_CLOCK");
            Fields(envelope,"schema","vesselId","gameTime");
            if(envelope.GetValue("schema")!="1" || envelope.GetValue("vesselId")!=vesselId.ToString() ||
               double.IsNaN(gameTime)||double.IsInfinity(gameTime)||Math.Abs(Number(envelope,"gameTime")-gameTime)>0.000001 ||
               envelope.nodes.Cast<ConfigNode>().Any(n=>n.name!=nameof(SurvivalClock)&&n.name!=nameof(ModuleLifeSupportSystem)&&n.name!="PART_TIMER") ||
               envelope.GetNodes(nameof(SurvivalClock)).Length!=1 || envelope.GetNodes(nameof(ModuleLifeSupportSystem)).Length>1)
                throw new InvalidDataException("Resource clock headers mismatch");
            var clock=envelope.GetNode(nameof(SurvivalClock));Fields(clock);
            if(clock.nodes.Count!=1 || !clock.HasNode("APF_SURVIVAL"))throw new InvalidDataException("Resource clock snapshot missing");
            var snapshot=clock.GetNode("APF_SURVIVAL");var epoch=SurvivalClock.ValidateSnapshot(snapshot);
            if(Math.Abs(epoch.OwnerUT-gameTime)>2 || snapshot.GetNodes("SUPPLY").Any(n=>n.GetValue("VesselId")!=vesselId.ToString()))
                throw new InvalidDataException("Resource clock snapshot identity mismatch");
            foreach(var life in envelope.GetNodes(nameof(ModuleLifeSupportSystem)))
            {
                Fields(life,"LastUpdateTime");double time=Number(life,"LastUpdateTime");
                if(life.nodes.Count!=0||time<0||time>epoch.SurvivalUT+2)throw new InvalidDataException("Invalid resource life support epoch");
            }
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var timer in envelope.GetNodes("PART_TIMER"))
            {
                Fields(timer,"uid","index","module","lastUpdateTime");
                uint uid;int index;double time=Number(timer,"lastUpdateTime");
                if(timer.nodes.Count!=0 || !uint.TryParse(timer.GetValue("uid"),NumberStyles.None,CultureInfo.InvariantCulture,out uid) ||
                   !int.TryParse(timer.GetValue("index"),NumberStyles.None,CultureInfo.InvariantCulture,out index) ||
                   timer.GetValue("module").Length>128 || !seen.Add(uid+":"+index) || time<0||time>epoch.SurvivalUT+2)
                    throw new InvalidDataException("Invalid resource converter epoch");
            }
            return envelope;
        }
        private static void Fields(ConfigNode node,params string[] names)
        {
            if(node==null||node.values.Count!=names.Length||names.Any(n=>node.GetValues(n).Length!=1))throw new InvalidDataException("Unexpected resource clock fields");
        }
        private static double Number(ConfigNode node,string key)
        {
            double value;if(!double.TryParse(node.GetValue(key),NumberStyles.Float,CultureInfo.InvariantCulture,out value)||double.IsNaN(value)||double.IsInfinity(value))
                throw new InvalidDataException("Invalid resource clock number");return value;
        }

        private static void AfterResourceFields(VesselResource __instance,Vessel vessel,ConfigNode __state)
        {
            if(__state==null)return;
            var clockNode=__state.GetNode(nameof(SurvivalClock));
            var clock=vessel.GetComponent<SurvivalClock>();
            if(clock==null || clockNode==null)throw new InvalidDataException("Resource clock module missing");
            // Foreign data remains an inert snapshot. USI scenario state is only
            // restored later if this player deliberately becomes simulation owner.
            clock.Load(clockNode);
            var lifeNode=__state.GetNode(nameof(ModuleLifeSupportSystem));
            foreach(var life in vessel.vesselModules.OfType<ModuleLifeSupportSystem>())if(lifeNode!=null)life.Load(lifeNode);
            foreach(var timer in __state.GetNodes("PART_TIMER"))
            {
                uint uid=uint.Parse(timer.GetValue("uid"),CultureInfo.InvariantCulture);
                int index=int.Parse(timer.GetValue("index"),CultureInfo.InvariantCulture);
                double time=double.Parse(timer.GetValue("lastUpdateTime"),CultureInfo.InvariantCulture);
                var part=vessel.parts.FirstOrDefault(p=>p.flightID==uid);
                if(part!=null && index>=0 && index<part.Modules.Count && part.Modules[index] is BaseConverter && part.Modules[index].moduleName==timer.GetValue("module"))
                    ConverterTime.SetValue(part.Modules[index],time);
            }
            if(vessel.protoVessel!=null)
            {
                // Keep unloaded/next-load state paired with the same resource
                // packet as well; no vessel geometry is rebuilt or instantiated.
                var saved=vessel.protoVessel.vesselModules;
                if(saved!=null)
                {
                    saved.RemoveNodes(nameof(SurvivalClock));saved.AddNode(clockNode.CreateCopy());
                    if(lifeNode!=null){saved.RemoveNodes(nameof(ModuleLifeSupportSystem));saved.AddNode(lifeNode.CreateCopy());}
                }
                foreach(var timer in __state.GetNodes("PART_TIMER"))
                {
                    uint uid=uint.Parse(timer.GetValue("uid"),CultureInfo.InvariantCulture);
                    int index=int.Parse(timer.GetValue("index"),CultureInfo.InvariantCulture);
                    var part=vessel.protoVessel.protoPartSnapshots.FirstOrDefault(p=>p.flightID==uid);
                    if(part!=null && index>=0 && index<part.modules.Count && part.modules[index].moduleName==timer.GetValue("module"))
                        part.modules[index].moduleValues.SetValue("lastUpdateTime",timer.GetValue("lastUpdateTime"),true);
                }
            }
        }
    }
}
