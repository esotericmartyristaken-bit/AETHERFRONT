using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LmpClient;
using LmpClient.Network;
using LmpCommon.Message.Interface;
using LmpClient.Systems.VesselPartSyncFieldSys;

namespace Aetherfront.Stability.Client
{
    public static class ClientTraffic
    {
        sealed class State { internal TrafficScheduler Queue; internal DateTime LastProgress = DateTime.UtcNow; }
        static readonly ConditionalWeakTable<ConcurrentQueue<IMessageBase>, State> States = new ConditionalWeakTable<ConcurrentQueue<IMessageBase>, State>();
        static readonly TransportCapacity Capacity = new TransportCapacity();
        static readonly ConcurrentDictionary<IMessageBase, int> Generations = (ConcurrentDictionary<IMessageBase, int>)AccessTools.Field(typeof(NetworkSender), "MessageGenerations").GetValue(null);
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(NetworkSender), "SendMain"), transpiler: new HarmonyMethod(typeof(ClientTraffic), nameof(RewriteDequeue)));
            harmony.Patch(AccessTools.Method(typeof(NetworkSender), "QueueOutgoingMessage", new[] { typeof(IMessageBase), typeof(int) }), prefix: new HarmonyMethod(typeof(ClientTraffic), nameof(Filter)));
            harmony.Patch(AccessTools.Method(typeof(NetworkSender), "ResetOutgoingMessages"), prefix: new HarmonyMethod(typeof(ClientTraffic), nameof(Reset)));
        }
        public static void InstallFieldHook(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(VesselPartSyncFieldEvents), "CallIsValid"), prefix: new HarmonyMethod(typeof(ClientTraffic), nameof(FilterField)));
        static bool FilterField(PartModule module, string fieldName, ref bool __result)
        { if (module == null || module.moduleName != "FXModuleThrottleEffects" || fieldName != "state") return true; __result = false; return false; }
        static bool Filter(IMessageBase message)
        {
            if (message == null || !TrafficScheduler.Cosmetic(message.Data)) return true;
            message.Recycle(); return false;
        }
        static void Discard(IMessageBase message) { Generations.TryRemove(message, out _); message.Recycle(); }
        static void Reset()
        {
            var old = NetworkSender.OutgoingMessages;
            if (old != null && States.TryGetValue(old, out var state)) lock (state) { state.Queue.Clear(); States.Remove(old); }
        }
        public static IEnumerable<CodeInstruction> RewriteDequeue(IEnumerable<CodeInstruction> instructions)
        {
            int changed = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && method.Name == "TryDequeue" && method.DeclaringType == typeof(ConcurrentQueue<IMessageBase>))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ClientTraffic), nameof(TryDequeue)); changed++; }
                yield return instruction;
            }
            if (changed != 1) throw new InvalidOperationException("Unreviewed client send-loop contract: " + changed);
        }
        public static bool TryDequeue(ConcurrentQueue<IMessageBase> queue, out IMessageBase result)
        {
            var state = States.GetValue(queue, _ => new State { Queue = new TrafficScheduler(Discard) });
            Exception failure = null;
            lock (state)
            {
                try
                {
                    for (int i = 0; i < 512 && state.Queue.Count < 3800 && queue.TryDequeue(out var message); i++) state.Queue.Add(message);
                    bool taken = state.Queue.TryTake((message, size) => message is IMasterServerMessageBase ||
                        Capacity.CanSend(NetworkMain.ClientConnection, NetworkMain.ClientConnection?.ServerConnection, message, size), out result);
                    if (taken || state.Queue.Count == 0) state.LastProgress = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - state.LastProgress).TotalSeconds >= 30)
                        throw new TimeoutException("Application sender made no progress for 30 seconds; local flight recovery preserved.");
                    return taken;
                }
                catch (Exception error) { result = null; state.Queue.Clear(); failure = error; }
            }
            // Reset/disconnect takes the native session gate before clearing this queue.
            // Never call it while holding the queue gate in the reverse order.
            NetworkMain.HandleDisconnectException(failure); return false;
        }
    }
}
