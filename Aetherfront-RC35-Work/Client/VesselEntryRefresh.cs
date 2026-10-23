using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.IO;
using HarmonyLib;
using LmpClient;
using LmpClient.Extensions;
using LmpClient.Network;
using LmpClient.Systems.KerbalSys;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.VesselResourceSys;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Vessel;
using KSP.UI.Screens;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    // A tracking-station boarding operation refreshes one authorized vessel.
    // Native sync replies use GameTime=0, so an earlier pilot does not wait
    // thousands of seconds for a former pilot's future-subspace queue to drain.
    public static class VesselEntryRefresh
    {
        private sealed class Request { internal Guid Id; internal byte[] Bytes; }
        private static readonly object Gate=new object();
        private static Request pending;
        private static SimulationAuthority runner;
        private static MethodInfo fly;
        private static Guid reentry;
        private static bool installed;

        public static void Install(SimulationAuthority owner,Harmony harmony)
        {
            if(installed)return;
            runner=owner;
            fly=AccessTools.DeclaredMethod(typeof(SpaceTracking),"FlyVessel",new[]{typeof(Vessel)});
            var assign=AccessTools.DeclaredMethod(typeof(VesselProtoQueue),"AssignFromMessage",new[]{typeof(VesselProto),typeof(VesselProtoMsgData)});
            if(fly==null||assign==null)throw new InvalidOperationException("Vessel boarding refresh signatures changed");
            harmony.Patch(fly,prefix:new HarmonyMethod(typeof(VesselEntryRefresh),nameof(BeforeFly)));
            harmony.Patch(assign,postfix:new HarmonyMethod(typeof(VesselEntryRefresh),nameof(ReceivedProto)));
            installed=true;
        }
        private static bool BeforeFly(SpaceTracking __instance,Vessel __0)
        {
            if(!SimulationAuthority.InMultiplayer)return true;
            if(__0!=null && reentry==__0.id){reentry=Guid.Empty;return true;}
            if(__0==null || MainSystem.NetworkState!=ClientState.Running || HighLogic.LoadedScene!=GameScenes.TRACKSTATION)return false;
            string controller=LockSystem.LockQuery.GetControlLockOwner(__0.id);
            if(!string.IsNullOrEmpty(controller) && controller!=SettingsSystem.CurrentSettings.PlayerName)
            {ScreenMessages.PostScreenMessage("This vessel is currently controlled by "+controller+".",5,ScreenMessageStyle.UPPER_CENTER);return false;}
            Request request;
            lock(Gate){if(pending!=null)return false;pending=request=new Request{Id=__0.id};}
            runner.StartCoroutine(Refresh(__instance,request));return false;
        }
        private static void ReceivedProto(VesselProtoMsgData __1)
        {
            lock(Gate)
            {
                if(pending==null || pending.Bytes!=null || __1.VesselId!=pending.Id || __1.GameTime!=0)return;
                if(__1.Data==null||__1.NumBytes<=0||__1.NumBytes>__1.Data.Length||__1.NumBytes>10*1024*1000)return;
                pending.Bytes=new byte[__1.NumBytes];Array.Copy(__1.Data,pending.Bytes,__1.NumBytes);
            }
        }
        private static IEnumerator Refresh(SpaceTracking tracking,Request request)
        {
            try
            {
                ScreenMessages.PostScreenMessage("Refreshing vessel, crew and supplies...",3,ScreenMessageStyle.UPPER_CENTER);
                // Retain all known IDs except the selected ship. The server's
                // existing permission checks send that one current full snapshot.
                var ids=FlightGlobals.Vessels.Where(v=>v!=null&&v.id!=request.Id).Select(v=>v.id).Distinct().ToArray();
                var message=NetworkMain.CliMsgFactory.CreateNewMessageData<VesselSyncMsgData>();
                message.VesselsCount=ids.Length;message.VesselIds=ids;
                VesselProtoSystem.Singleton.MessageSender.SendMessage(message);
                float deadline=Time.realtimeSinceStartup+30;
                while(true)
                {
                    byte[] bytes;lock(Gate)bytes=request.Bytes;
                    if(bytes!=null)break;
                    if(MainSystem.NetworkState!=ClientState.Running || HighLogic.LoadedScene!=GameScenes.TRACKSTATION || tracking==null || Time.realtimeSinceStartup>=deadline)
                    {ScreenMessages.PostScreenMessage("Vessel refresh did not complete. Please try again from Tracking Station.",6,ScreenMessageStyle.UPPER_CENTER);yield break;}
                    yield return null;
                }
                bool refreshed=false;
                try
                {
                    if(MainSystem.NetworkState!=ClientState.Running || HighLogic.LoadedScene!=GameScenes.TRACKSTATION || tracking==null)
                        throw new InvalidOperationException("Boarding refresh was cancelled by a scene or connection change");
                    if(!KerbalSystem.Singleton.KerbalsToProcess.IsEmpty)KerbalSystem.Singleton.LoadKerbalsIntoGame();
                    byte[] bytes;lock(Gate)bytes=request.Bytes;
                    var wire=new VesselProto{VesselId=request.Id,RawData=bytes,NumBytes=bytes.Length,GameTime=0};
                    var proto=wire.CreateProtoVessel();var current=FlightGlobals.FindVessel(request.Id);
                    if(proto==null||current==null||current.loaded||!proto.Validate(true)||proto.HasInvalidParts(true))throw new InvalidOperationException("Fresh vessel could not be validated safely");
                    string controller=LockSystem.LockQuery.GetControlLockOwner(request.Id);
                    if(!string.IsNullOrEmpty(controller)&&controller!=SettingsSystem.CurrentSettings.PlayerName)throw new InvalidOperationException("Another pilot took control during refresh");
                    var clockNode=proto.vesselModules.GetNode(nameof(SurvivalClock));
                    var clock=current.GetComponent<SurvivalClock>();
                    if(clockNode!=null)
                    {
                        if(clock==null)throw new InvalidOperationException("Vessel survival module is unavailable");
                        var saved=clockNode.GetNode("APF_SURVIVAL");
                        if(saved!=null)
                        {
                            AccessTools.Method(typeof(SurvivalClock),"ValidateSnapshot").Invoke(null,new object[]{saved});
                            var crew=proto.GetVesselCrew().Select(c=>c.name).ToArray();
                            if(saved.GetNodes("CREW").Any(n=>!crew.Contains(n.GetValue("KerbalName"),StringComparer.Ordinal)) ||
                               saved.GetNodes("SUPPLY").Any(n=>n.GetValue("VesselId")!=request.Id.ToString()))
                                throw new InvalidDataException("Fresh survival snapshot does not belong to this ship");
                        }
                    }
                    var lifeNode=proto.vesselModules.GetNode("ModuleLifeSupportSystem");
                    if(lifeNode!=null)
                    {
                        double time;
                        if(lifeNode.nodes.Count!=0 || lifeNode.values.Count!=1 || lifeNode.GetValues("LastUpdateTime").Length!=1 ||
                           !double.TryParse(lifeNode.GetValue("LastUpdateTime"),NumberStyles.Float,CultureInfo.InvariantCulture,out time) ||
                           double.IsNaN(time)||double.IsInfinity(time)||time<0||time>1e12)
                            throw new InvalidDataException("Fresh life support timestamp is invalid");
                    }
                    if(!VesselLoader.UpdateProtoInPlace(current,proto))throw new InvalidOperationException("Fresh vessel could not be installed safely");
                    // The native pointer swap intentionally preserves the live
                    // VesselModule objects. Refresh their inert saved data too,
                    // so the imminent native SaveGame cannot write old timers.
                    if(clock!=null)clock.Load(clockNode??new ConfigNode(nameof(SurvivalClock)));
                    foreach(var module in current.vesselModules)
                        if(module.GetType().FullName=="LifeSupport.ModuleLifeSupportSystem")
                        {var node=proto.vesselModules.GetNode("ModuleLifeSupportSystem");if(node!=null)module.Load(node);}
                    if(VesselProtoSystem.Singleton.VesselProtos.TryGetValue(request.Id,out var protos))protos.Clear();
                    if(VesselResourceSystem.Singleton.VesselResources.TryGetValue(request.Id,out var resources))resources.Clear();
                    Debug.Log("[AetherfrontContinuity] Refreshed selected vessel before boarding: "+request.Id+" ("+bytes.Length+" bytes)");
                    reentry=request.Id;
                    try{fly.Invoke(tracking,new object[]{current});refreshed=true;}finally{reentry=Guid.Empty;}
                }
                catch(Exception error){Debug.LogError("[AetherfrontContinuity] Boarding refresh refused: "+error);}
                if(!refreshed)ScreenMessages.PostScreenMessage("Vessel refresh failed. Remain in Tracking Station and try again.",6,ScreenMessageStyle.UPPER_CENTER);
            }
            finally{lock(Gate){if(ReferenceEquals(pending,request))pending=null;}}
        }
    }
}
