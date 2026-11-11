using System;
using System.Runtime.CompilerServices;
using LmpCommon.Enums;
using LmpCommon.Message.Interface;
using Server.Client;
using Server.Server;
using Server.Log;

namespace Aetherfront.Synchronization.Server
{
    public static class ServerTraffic
    {
        sealed class State
        {
            internal readonly object Gate = new object();
            internal TrafficScheduler Queue = new TrafficScheduler(_ => { });
            internal DateTime LastProgress = DateTime.UtcNow, NextLog = DateTime.UtcNow.AddSeconds(30);
        }
        static readonly ConditionalWeakTable<ClientStructure, State> States = new ConditionalWeakTable<ClientStructure, State>();
        static readonly TransportCapacity Capacity = new TransportCapacity();
        // Replaced with the pinned native budget getter/TryDequeue by the release graft.
        static bool Drain(ClientStructure client, out IServerMessageBase message) => throw new NotImplementedException("Native budget graft required.");
        public static bool AllowEnqueue(IServerMessageBase message) => message?.Data != null && !TrafficScheduler.Cosmetic(message.Data);
        public static void Enqueue(ClientStructure client, IServerMessageBase message)
        {
            if (client == null || client.DisconnectClient || !AllowEnqueue(message)) return;
            var state = States.GetValue(client, _ => new State());
            string failure = null;
            lock (state.Gate)
            {
                try { state.Queue.Add(message); }
                catch (Exception error) { state.Queue.Clear(); failure = error.Message; }
            }
            if (failure != null) MessageQueuer.SendConnectionEnd(client, failure);
        }
        public static bool TryDequeue(ClientStructure client, out IServerMessageBase result)
        {
            result = null;
            var state = States.GetValue(client, _ => new State());
            string failure = null;
            lock (state.Gate)
            {
            try
            {
                for (int i = 0; i < 256 && state.Queue.Count < 3800 && Drain(client, out var message); i++) state.Queue.Add(message);
                if (state.Queue.TryTake((message, size) => Capacity.CanSend(LidgrenServer.Server, client.Connection, message, size), out var taken))
                { state.LastProgress = DateTime.UtcNow; result = (IServerMessageBase)taken; return true; }
                if (state.Queue.Count == 0) state.LastProgress = DateTime.UtcNow;
                if (state.NextLog <= DateTime.UtcNow && state.Queue.Count > 0)
                {
                    state.NextLog = DateTime.UtcNow.AddSeconds(30);
                    LunaLog.Normal("[Stability] Outbound peer backlog; count=" + state.Queue.Count + "; bytes=" + state.Queue.Bytes + "; coalesced=" + state.Queue.Coalesced + "; cosmeticFiltered=" + state.Queue.CosmeticsFiltered);
                }
                if ((DateTime.UtcNow - state.LastProgress).TotalSeconds >= 30)
                    throw new TimeoutException("Application sender made no progress for 30 seconds; reconnect to resynchronize.");
                return false;
            }
            catch (Exception error)
            {
                state.Queue.Clear();
                failure = error.Message;
            }
            }
            // Native disconnect may take FleetGuard, whose handlers enqueue output.
            // Release our per-peer gate before entering that native lifecycle.
            MessageQueuer.SendConnectionEnd(client, failure); return false;
        }
    }
}
