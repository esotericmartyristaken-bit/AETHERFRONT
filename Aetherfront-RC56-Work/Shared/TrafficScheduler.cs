using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using Aetherfront.Core;
using Lidgren.Network;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Data.Aetherfront;

namespace Aetherfront.Synchronization
{
    // One consumer. Callers synchronize intake with their existing queue/session gate.
    // Reliable ordering is retained WITHIN every transport channel, including proto/remove.
    public sealed class TrafficScheduler
    {
        sealed class Entry { internal IMessageBase Message; internal int Size; internal string Key, Duplicate; internal long Enqueued; }
        sealed class Lane { internal int Channel; internal NetDeliveryMethod Delivery; internal LinkedList<Entry> Items = new LinkedList<Entry>(); }
        readonly List<Lane> lanes = new List<Lane>();
        readonly Dictionary<string, LinkedListNode<Entry>> latest = new Dictionary<string, LinkedListNode<Entry>>();
        readonly Dictionary<string, LinkedListNode<Entry>> duplicates = new Dictionary<string, LinkedListNode<Entry>>();
        readonly Action<IMessageBase> discard;
        readonly Func<double> seconds;
        // Eight times the RC55 byte pacing, still admitted by the unchanged native
        // per-peer memory/fragment windows. This is not an unbounded network queue.
        double previous, messageTokens = 64, bulkTokens = 1048576;
        int cursor, count; long bytes;
        public long Coalesced { get; private set; }
        public long CosmeticsFiltered { get; private set; }
        public long DuplicatesFiltered { get; private set; }
        public long DuplicateBytes { get; private set; }
        public double LastQueueMilliseconds { get; private set; }
        public int Count => count;
        public long Bytes => bytes;
        public TrafficScheduler(Action<IMessageBase> discard, Func<double> seconds = null)
        { this.discard = discard; this.seconds = seconds ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency); previous = this.seconds(); }

        public static bool Cosmetic(IMessageData data) => data is VesselPartSyncFieldMsgData field &&
            field.ModuleName == "FXModuleThrottleEffects" && field.FieldName == "state";
        public static bool Critical(IMessageBase message)
        {
            string name = message.Data.GetType().Name;
            return name.StartsWith("Warp", StringComparison.Ordinal) || name.StartsWith("Lock", StringComparison.Ordinal) ||
                name.StartsWith("TimeSync", StringComparison.Ordinal) || name.StartsWith("Handshake", StringComparison.Ordinal) ||
                name.StartsWith("ConnectionEnd", StringComparison.Ordinal) || name.StartsWith("Heart", StringComparison.Ordinal);
        }
        static string SnapshotKey(IMessageBase message)
        {
            if (message.NetDeliveryMethod != NetDeliveryMethod.UnreliableSequenced || !(message.Data is VesselBaseMsgData vessel)) return null;
            string name = vessel.GetType().Name;
            // Resources, actions, science and proto messages are deliberately excluded.
            return name == "VesselPositionMsgData" || name == "VesselFlightStateMsgData" ? name + ":" + vessel.VesselId : null;
        }
        static string DuplicateKey(IMessageBase message)
        {
            if (message.Data is VesselSyncMsgData sync && sync.VesselIds != null && sync.VesselsCount >= 0 && sync.VesselsCount <= sync.VesselIds.Length)
                return "sync:" + string.Join(",", sync.VesselIds.Take(sync.VesselsCount).Select(id => id.ToString("N")));
            if (message.Data is VesselProtoMsgData proto && proto.Data != null && proto.NumBytes > 0 && proto.NumBytes <= proto.Data.Length)
            {
                using (var sha = SHA256.Create())
                    return "proto:" + proto.VesselId + ":" + proto.GameTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                        proto.SharedWorldRevision + ":" + Convert.ToBase64String(sha.ComputeHash(proto.Data, 0, proto.NumBytes));
            }
            if (message.Data is AetherfrontCommandMsgData command && command.PayloadJson != null && command.PayloadJson.Length <= 1048576)
            {
                try
                {
                    var request = AetherfrontProtocol.DeserializeRequest(command.PayloadJson);
                    if (request?.Operation == AetherfrontWireOperation.SnapshotRequest) return "query:" + command.PayloadJson;
                }
                catch (Exception) { } // The native admission path still handles malformed requests.
            }
            return null;
        }
        public void Add(IMessageBase message)
        {
            if (Cosmetic(message.Data)) { CosmeticsFiltered++; discard(message); return; }
            int size = Math.Max(1, message.GetMessageSize());
            string duplicate = DuplicateKey(message);
            string lanePrefix = message.Channel + ":" + message.NetDeliveryMethod + ":";
            if (duplicate != null)
            {
                duplicate = lanePrefix + duplicate;
                if (duplicates.ContainsKey(duplicate))
                { DuplicatesFiltered++; DuplicateBytes += size; discard(message); return; }
            }
            if (message.NetDeliveryMethod == NetDeliveryMethod.ReliableOrdered && (duplicate == null || message.Data is VesselProtoMsgData))
            {
                // A distinct proto is also a mutation. In A,B,A, the final A must
                // survive; only an unchanged definition before any mutation is redundant.
                foreach (string obsolete in duplicates.Keys.Where(k => k.StartsWith(lanePrefix, StringComparison.Ordinal)).ToArray()) duplicates.Remove(obsolete);
            }
            string key = SnapshotKey(message);
            if (key != null && latest.TryGetValue(key, out var old))
            {
                old.List.Remove(old); latest.Remove(key); count--; bytes -= old.Value.Size;
                discard(old.Value.Message); Coalesced++;
            }
            bool critical = Critical(message);
            if (count >= (critical ? 4096 : 3840) || bytes + size > (critical ? 67108864L : 65011712L))
            {
                discard(message);
                throw new InvalidOperationException("Application send backlog cannot drain safely; reconnect to resynchronize.");
            }
            Lane lane = lanes.Find(l => l.Channel == message.Channel && l.Delivery == message.NetDeliveryMethod);
            if (lane == null) { lane = new Lane { Channel = message.Channel, Delivery = message.NetDeliveryMethod }; lanes.Add(lane); }
            var entry = new Entry { Message = message, Size = size, Key = key, Duplicate = duplicate, Enqueued = Stopwatch.GetTimestamp() };
            var node = lane.Items.AddLast(entry); if (key != null) latest[key] = node;
            if (duplicate != null) duplicates[duplicate] = node;
            count++; bytes += size;
        }
        public bool TryTake(Func<IMessageBase, int, bool> capacity, out IMessageBase message)
        {
            double now = seconds(), elapsed = Math.Max(0, now - previous); previous = now;
            messageTokens = Math.Min(64, messageTokens + elapsed * 96);
            bulkTokens = Math.Min(1048576, bulkTokens + elapsed * 4194304);
            for (int priority = 0; priority < 2; priority++)
                for (int n = 0; n < lanes.Count; n++)
                {
                    int index = (cursor + n) % lanes.Count; var lane = lanes[index]; var node = lane.Items.First;
                    if (node == null) continue;
                    var entry = node.Value; bool critical = Critical(entry.Message);
                    if (critical != (priority == 0)) continue;
                    bool bulk = entry.Size >= 16384;
                    if (!critical && (messageTokens < 1 || (bulk && bulkTokens < Math.Min(entry.Size, 1048576)))) continue;
                    if (!capacity(entry.Message, entry.Size)) continue;
                    lane.Items.RemoveFirst(); if (entry.Key != null) latest.Remove(entry.Key);
                    if (entry.Duplicate != null && duplicates.TryGetValue(entry.Duplicate, out var mapped) && ReferenceEquals(mapped, node)) duplicates.Remove(entry.Duplicate);
                    LastQueueMilliseconds = Math.Max(0, Stopwatch.GetTimestamp() - entry.Enqueued) * 1000d / Stopwatch.Frequency;
                    count--; bytes -= entry.Size; cursor = (index + 1) % lanes.Count;
                    if (!critical) { messageTokens--; if (bulk) bulkTokens -= entry.Size; }
                    message = entry.Message; return true;
                }
            message = null; return false;
        }
        public void Clear()
        {
            foreach (var lane in lanes) foreach (var item in lane.Items) discard(item.Message);
            lanes.Clear(); latest.Clear(); duplicates.Clear(); count = 0; bytes = 0;
        }
    }
}
