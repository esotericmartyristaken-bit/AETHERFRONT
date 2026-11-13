using System;
using System.Reflection;
using HarmonyLib;
using LmpClient;
using LmpClient.Network;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Interface;
using UnityEngine;

namespace Aetherfront.Stability.Client
{
    public static class VesselPersistence
    {
        static readonly PropertyInfo Generation=AccessTools.Property(typeof(NetworkConnection),"ConnectionGeneration");
        static Guid lastVessel;
        static int lastGeneration=-1;
        static double next;
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp()/System.Diagnostics.Stopwatch.Frequency;
        public static void Install(Harmony harmony)
        {
            // The exact RC63 native sender already serializes synchronously and
            // carries the captured connection generation through its queue.
            // Preserve that implementation; supplement its part-count-only
            // periodic trigger with bounded complete-state publication below.
            var ordered=AccessTools.Method(typeof(VesselProtoMessageSender),"SendVesselMessage",new[]{typeof(ProtoVessel),typeof(bool),typeof(string),typeof(int)});
            var prepare=AccessTools.Method(typeof(VesselProtoMessageSender),"PrepareAndSendProtoVessel",new[]{typeof(ProtoVessel),typeof(bool),typeof(string),typeof(long),typeof(int)});
            if(Generation==null||ordered==null||prepare==null)throw new MissingMemberException("Ordered vessel publication API changed.");
        }
        public static void Tick()
        {
            if(MainSystem.NetworkState!=ClientState.Running){lastVessel=Guid.Empty;lastGeneration=-1;return;}
            if(FlightSafety.SimulationProtected||!FlightSafety.ClockReady||!HighLogic.LoadedSceneIsFlight||!FlightGlobals.ready||VesselCommon.IsSpectating||TimeWarp.CurrentRateIndex!=0)return;
            var vessel=FlightGlobals.ActiveVessel;
            if(vessel==null||vessel.id==Guid.Empty||vessel.state==Vessel.State.DEAD)return;
            int generation=(int)Generation.GetValue(null,null);
            bool first=lastVessel!=vessel.id||lastGeneration!=generation;
            if(!first&&Now<next)return;
            next=Now+10;
            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel,false,first?"Flight clock ready; ordered complete state":"Periodic complete vessel state");
            lastVessel=vessel.id;lastGeneration=generation;
        }
    }
}
