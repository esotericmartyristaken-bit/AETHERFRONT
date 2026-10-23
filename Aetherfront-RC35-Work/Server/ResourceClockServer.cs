using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Aetherfront.Continuity;
using LmpCommon.Message.Data.Vessel;
using LunaConfigNode.CfgNode;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.System;
using Server.System.Vessel;
using StoredVessel=Server.System.Vessel.Classes.Vessel;

namespace Aetherfront.Continuity.Server
{
    public static class ResourceClockServer
    {
        // Called only after the reader's original account, DeepFreeze, permission
        // and resource validators. A false result also prevents the original relay.
        public static bool CommitResourceClock(ClientStructure client,VesselBaseMsgData data)
        {
            lock(AetherfrontFleetGuard.Gate)
            {
                AetherfrontDeepFreezeSystem.EnsureHealthy();
                var message=data as VesselResourceMsgData;
                if(message==null || !PersonalClockServer.MayPersistPosition(client) ||
                    !VesselDataUpdater.IsValidIncrementalData(message) || !VesselDataUpdater.IsValidResourceData(message) ||
                    VesselContext.RemovedVessels.ContainsKey(message.VesselId) ||
                    !VesselStoreSystem.CurrentVessels.TryGetValue(message.VesselId,out var vessel))return false;
                byte[] payload=ResourceClockPayloads.Get(message);
                if(payload.Length==0)
                {
                    // Legacy natural/unclocked vessels retain their existing path;
                    // never accept unpaired incremental values for a clocked vessel.
                    if(vessel.VesselModules.GetNode("SurvivalClock")?.Value.GetNode("APF_SURVIVAL")!=null)return false;
                    VesselDataUpdater.WriteResourceDataToFile(message);return true;
                }
                try
                {
                    ResourceClockPayloads.Validate(payload);
                    var parsed=new ConfigNode(Encoding.UTF8.GetString(payload));
                    Nodes(parsed,"APF_RESOURCE_CLOCK");Fields(parsed);
                    var root=Single(parsed,"APF_RESOURCE_CLOCK");
                    Fields(root,"schema","vesselId","gameTime");Nodes(root,"SurvivalClock","ModuleLifeSupportSystem","PART_TIMER");
                    if(Value(root,"schema")!="1" || Value(root,"vesselId")!=message.VesselId.ToString() ||
                       Math.Abs(Number(root,"gameTime")-message.GameTime)>0.000001)return false;
                    if(LockSystem.LockQuery.GetControlLockOwner(message.VesselId)!=client.PlayerName)return false;
                    var clock=Single(root,"SurvivalClock");Fields(clock);Nodes(clock,"APF_SURVIVAL");
                    var survival=Single(clock,"APF_SURVIVAL");
                    string schema=Value(survival,"schema");
                    if(schema=="3")Fields(survival,"schema","owner","ownerUT","survivalUT","launchUT","freezeOffset");
                    else if(schema=="2")Fields(survival,"schema","owner","ownerUT","survivalUT","launchUT");
                    else if(schema=="1")Fields(survival,"schema","owner","ownerUT","survivalUT");
                    else return false;
                    Nodes(survival,"CREW","SUPPLY");
                    double ownerTime=Number(survival,"ownerUT"),survivalTime=Number(survival,"survivalUT");
                    if(Value(survival,"owner")!=client.PlayerName ||
                       ownerTime<0||ownerTime>1e12||survivalTime<0||survivalTime>1e12||Math.Abs(ownerTime-message.GameTime)>2)return false;
                    if(schema!="1" && (Number(survival,"launchUT")<0||Number(survival,"launchUT")>survivalTime+2))return false;
                    if(schema=="3"){
                        double offset=Number(survival,"freezeOffset"),freeze=survivalTime+offset;
                        if(Math.Abs(offset)>1e12||freeze<0||freeze>1e12)return false;
                    }
                    var previousSurvival=vessel.VesselModules.GetNode("SurvivalClock")?.Value.GetNode("APF_SURVIVAL")?.Value;
                    string previousSchema=previousSurvival?.GetValue("schema")?.Value;
                    if(previousSchema=="3"&&schema!="3"||previousSchema=="2"&&schema=="1")return false;
                    if(previousSurvival!=null && survivalTime+0.001<Number(previousSurvival,"survivalUT"))return false;
                    if(previousSchema=="3"&&survivalTime+Number(survival,"freezeOffset")+0.001<
                        Number(previousSurvival,"survivalUT")+Number(previousSurvival,"freezeOffset"))return false;
                    ValidateCrewAndSupply(vessel,survival,message.VesselId);
                    var lifeNodes=root.GetNodes("ModuleLifeSupportSystem");
                    if(lifeNodes.Count>1)throw new InvalidDataException("Duplicate life support module");
                    ConfigNode life=lifeNodes.Count==0?null:lifeNodes[0].Value;
                    if(life!=null)
                    {
                        Fields(life,"LastUpdateTime");Nodes(life);
                        double last=Number(life,"LastUpdateTime");
                        if(last<0||last>survivalTime+2)throw new InvalidDataException("Invalid life support epoch");
                    }
                    var timers=new List<(ConfigNode node,string time)>();
                    var seen=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var entry in root.GetNodes("PART_TIMER"))
                    {
                        var timer=entry.Value;Fields(timer,"uid","index","module","lastUpdateTime");Nodes(timer);
                        uint uid=uint.Parse(Value(timer,"uid"),NumberStyles.None,CultureInfo.InvariantCulture);
                        int index=int.Parse(Value(timer,"index"),NumberStyles.None,CultureInfo.InvariantCulture);
                        var part=vessel.GetPart(uid);var modules=part?.Modules.GetAll();
                        if(modules==null || index<0 || index>=modules.Count || !seen.Add(uid+":"+index))return false;
                        var module=modules[index].Value;
                        double time=Number(timer,"lastUpdateTime");
                        if(module.GetValue("name")?.Value!=Value(timer,"module") || module.GetValue("lastUpdateTime")==null || time<0 || time>survivalTime+2)return false;
                        timers.Add((module,Value(timer,"lastUpdateTime")));
                    }
                    var resources=new List<(ConfigNode node,string amount,string flow)>();
                    for(int index=0;index<message.ResourcesCount;index++)
                    {
                        var resource=message.Resources[index];var node=vessel.GetPart(resource.PartFlightId)?.Resources.GetSingle(resource.ResourceName)?.Value;
                        if(node==null)return false;
                        resources.Add((node,resource.Amount.ToString("R",CultureInfo.InvariantCulture),resource.FlowState.ToString()));
                    }
                    // Validate the complete transaction before the first mutation.
                    // The original store backup and all authorized writers use this
                    // same fleet gate, so snapshots cannot observe a half commit.
                    var previousClock=vessel.VesselModules.GetNode("SurvivalClock")?.Value;
                    var previousLife=vessel.VesselModules.GetNode("ModuleLifeSupportSystem")?.Value;
                    if(life!=null && previousLife==null)return false;
                    if(previousClock==null)vessel.VesselModules.AddNode(clock);else vessel.VesselModules.ReplaceNode(previousClock,clock);
                    if(life!=null)vessel.VesselModules.ReplaceNode(previousLife,life);
                    foreach(var timer in timers)timer.node.UpdateValue("lastUpdateTime",timer.time);
                    foreach(var resource in resources){resource.node.UpdateValue("amount",resource.amount);resource.node.UpdateValue("flowState",resource.flow);}
                    return true;
                }
                catch(Exception error) when(error is InvalidDataException || error is FormatException || error is OverflowException || error is ArgumentException)
                {
                    return false;
                }
            }
        }

        private static void ValidateCrewAndSupply(StoredVessel vessel,ConfigNode survival,Guid id)
        {
            var aboard=new HashSet<string>(vessel.Parts.GetAllValues().SelectMany(p=>p.Fields.GetSeveral("crew").Select(c=>c.Value)),StringComparer.Ordinal);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            var crew=survival.GetNodes("CREW");
            if(crew.Count>128)throw new InvalidDataException("Too many crew records");
            foreach(var item in crew)
            {
                var node=item.Value;Nodes(node);
                Fields(node,"KerbalName","HomeBodyId","LastPlanet","LastMeal","LastEC","LastUpdate","IsGrouchy","OldTrait","LastAtHome","LastSOIChange","MaxOffKerbinTime","TimeEnteredVessel","CurrentVesselId","PreviousVesselId");
                string name=Value(node,"KerbalName");
                if(!aboard.Contains(name)||!seen.Add(name))throw new InvalidDataException("Crew record not aboard");
                foreach(string key in new[]{"LastMeal","LastEC","LastUpdate","LastAtHome","LastSOIChange","MaxOffKerbinTime","TimeEnteredVessel"})Number(node,key);
                int.Parse(Value(node,"HomeBodyId"),CultureInfo.InvariantCulture);int.Parse(Value(node,"LastPlanet"),CultureInfo.InvariantCulture);bool.Parse(Value(node,"IsGrouchy"));
            }
            var supplies=survival.GetNodes("SUPPLY");if(supplies.Count>1)throw new InvalidDataException("Duplicate supply record");
            foreach(var item in supplies)
            {
                var node=item.Value;Nodes(node);Fields(node,"VesselId","VesselName","LastUpdate","NumCrew","RecyclerMultiplier","CrewCap","ExtraHabSpace","VesselHabMultiplier","CachedHabTime","SuppliesLeft","ECLeft","LastFeeding","LastECCheck");
                if(Value(node,"VesselId")!=id.ToString())throw new InvalidDataException("Supply record vessel mismatch");
                foreach(string key in new[]{"LastUpdate","RecyclerMultiplier","ExtraHabSpace","VesselHabMultiplier","CachedHabTime","SuppliesLeft","ECLeft","LastFeeding","LastECCheck"})Number(node,key);
                int count=int.Parse(Value(node,"NumCrew"),CultureInfo.InvariantCulture),capacity=int.Parse(Value(node,"CrewCap"),CultureInfo.InvariantCulture);
                if(count<0||count>128||capacity<0||capacity>10000)throw new InvalidDataException("Supply crew count refused");
            }
        }
        private static ConfigNode Single(ConfigNode node,string key)
        {
            var values=node.GetNodes(key);if(values.Count!=1)throw new InvalidDataException("Missing or duplicate node: "+key);return values[0].Value;
        }
        private static void Fields(ConfigNode node,params string[] names)
        {
            var fields=node.GetAllValues();if(fields.Count!=names.Length)throw new InvalidDataException("Unexpected clock fields");
            foreach(string name in names)if(node.GetValues(name).Count!=1)throw new InvalidDataException("Missing or duplicate clock field");
        }
        private static void Nodes(ConfigNode node,params string[] names)
        {
            if(node.GetAllNodes().Any(n=>!names.Contains(n.Name,StringComparer.Ordinal)))throw new InvalidDataException("Unexpected clock node");
        }
        private static string Value(ConfigNode node,string key)
        {
            var value=node.GetValue(key)?.Value;
            if(value==null||value.Length>256)throw new InvalidDataException("Invalid clock field: "+key);return value;
        }
        private static double Number(ConfigNode node,string key)
        {
            double number=double.Parse(Value(node,key),NumberStyles.Float,CultureInfo.InvariantCulture);
            if(double.IsNaN(number)||double.IsInfinity(number))throw new InvalidDataException("Nonfinite clock value");return number;
        }
    }
}
