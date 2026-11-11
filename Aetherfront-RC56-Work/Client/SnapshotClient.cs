using System;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Synchronization;
using HarmonyLib;
using LmpClient.Aetherfront;
using LmpClient.Network;

namespace Aetherfront.Stability.Client
{
    public static class SnapshotClient
    {
        const string Capability = "apf-state-delta-1";
        static bool fullRequired;
        static int generation = -1;
        static readonly PropertyInfo Generation = AccessTools.Property(typeof(NetworkConnection), "ConnectionGeneration");
        static readonly PropertyInfo Freshness = AccessTools.Property(typeof(AetherfrontClientSystem), "LastSnapshotUtc");
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AetherfrontClientSystem), "Send"), prefix: new HarmonyMethod(typeof(SnapshotClient), nameof(BeforeSend)));
            harmony.Patch(AccessTools.Method(typeof(AetherfrontClientSystem), "ApplyReply"),
                prefix: new HarmonyMethod(typeof(SnapshotClient), nameof(BeforeApply)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(SnapshotClient), nameof(AfterApply)));
        }
        static void BeforeSend(AetherfrontWireRequest request)
        {
            if (request == null || request.Operation != AetherfrontWireOperation.SnapshotRequest) return;
            int current = (int)Generation.GetValue(null, null);
            if (current != generation) { generation = current; fullRequired = true; }
            request.Body = Capability;
            request.ExpectedRevision = fullRequired || string.IsNullOrEmpty(AetherfrontClientSystem.State?.ServerId) ? -1 : AetherfrontClientSystem.State.Revision;
        }
        static void BeforeApply(AetherfrontWireReply reply)
        {
            if (reply == null || string.IsNullOrEmpty(reply.StateJson)) return;
            if (!reply.StateJson.StartsWith(StateDelta.Prefix, StringComparison.Ordinal)) return;
            try
            {
                var delta = AetherfrontJson.Deserialize<StateDelta>(reply.StateJson.Substring(StateDelta.Prefix.Length));
                if (delta == null || delta.ServerId != reply.ServerId || delta.Revision != reply.Revision) throw new InvalidOperationException("Delta envelope mismatch.");
                reply.StateJson = AetherfrontJson.Serialize(delta.Apply(AetherfrontClientSystem.State));
            }
            catch (Exception)
            {
                // Do not partially apply a failed patch or fabricate a command result.
                reply.StateJson = string.Empty; fullRequired = true;
            }
        }
        static void AfterApply(AetherfrontWireReply reply)
        {
            var state = AetherfrontClientSystem.State;
            if (reply == null || !reply.Accepted || state == null || state.ServerId != reply.ServerId || state.Revision != reply.Revision) return;
            if (!string.IsNullOrEmpty(reply.StateJson)) fullRequired = false;
            if (reply.Operation == AetherfrontWireOperation.SnapshotReply) Freshness?.SetValue(null, DateTime.UtcNow, null);
        }
    }
}
