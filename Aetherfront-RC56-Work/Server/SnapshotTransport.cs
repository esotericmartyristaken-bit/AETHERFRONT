using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Aetherfront.Core;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Server;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;

namespace Aetherfront.Synchronization.Server
{
    public static class SnapshotTransport
    {
        sealed class Peer
        {
            internal readonly object Gate = new object();
            internal ClientStructure Client;
            internal bool Scheduled, Pending, Delta;
            internal long Expected;
            internal string Account;
            internal AetherfrontState Last, Previous;
            internal string LastJson, PreviousJson;
            internal long LastExpected = long.MinValue, LastRevision = -1, LastSent;
        }
        static readonly ConditionalWeakTable<ClientStructure, Peer> Peers = new ConditionalWeakTable<ClientStructure, Peer>();
        static readonly ConcurrentQueue<Peer> Ready = new ConcurrentQueue<Peer>();
        static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        static readonly Lazy<Thread> Worker = new Lazy<Thread>(() => { var thread = new Thread(Run) { IsBackground = true, Name = "Aetherfront snapshot encoder" }; thread.Start(); return thread; });
        static readonly object Fleet = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontFleetGuard", true)
            .GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static readonly MethodInfo Interest = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontVesselInterest", true)
            .GetMethod("ForClient", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo Visibility = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontVisibility", true)
            .GetMethod("ForAccount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        public static long Requests, Coalesced, FullReplies, DeltaReplies, UnchangedReplies;

        public static void Enqueue(ClientStructure client, AetherfrontWireRequest request)
        {
            if (client == null || request == null || !client.Authenticated || client.DisconnectClient) return;
            var peer = Peers.GetValue(client, c => new Peer { Client = c });
            lock (peer.Gate)
            {
                Interlocked.Increment(ref Requests);
                if (peer.Pending) Interlocked.Increment(ref Coalesced);
                peer.Expected = request.ExpectedRevision; peer.Delta = request.Body == "apf-state-delta-1"; peer.Pending = true;
                if (!peer.Scheduled) { peer.Scheduled = true; Ready.Enqueue(peer); }
            }
            var worker = Worker.Value; Wake.Set();
        }
        static void Run()
        {
            for (;;)
            {
                if (!Ready.TryDequeue(out var peer)) { Wake.WaitOne(1000); continue; }
                long expected; bool delta;
                lock (peer.Gate) { expected = peer.Expected; delta = peer.Delta; peer.Pending = false; }
                try { Encode(peer, expected, delta); }
                catch (Exception error) { LunaLog.Error("[Synchronization] Snapshot encoding failed; type=" + error.GetType().Name); }
                finally
                {
                    lock (peer.Gate) { if (peer.Pending) Ready.Enqueue(peer); else peer.Scheduled = false; }
                    SyncTimings.ReportIfDue();
                }
            }
        }
        static void Encode(Peer peer, long expected, bool delta)
        {
            var client = peer.Client;
            string account; long revision;
            using (SyncTimings.FleetScope())
            {
                if (!client.Authenticated || client.DisconnectClient) return;
                account = AetherfrontServerSystem.GetBoundAccountId(client);
                if (string.IsNullOrEmpty(account)) return;
                revision = SnapshotStore.Revision(AetherfrontServerSystem.Store);
            }
            long now = SyncTimings.Start();
            if (peer.Account == account && expected < peer.LastRevision && peer.LastExpected == expected &&
                now - peer.LastSent < System.Diagnostics.Stopwatch.Frequency * 10)
            { Interlocked.Increment(ref Coalesced); return; }
            var state = AetherfrontServerSystem.Snapshot();
            if (state.FindAccount(account)?.Suspended != false) return;
            long filter = SyncTimings.Start();
            var visible = (AetherfrontState)Visibility.Invoke(null, new object[] { state, account });
            SyncTimings.Complete("snapshot.filter.outside-lock", filter);
            string stateJson;
            var previous = peer.Account == account ? (peer.Last?.Revision == expected ? peer.Last : peer.Previous?.Revision == expected ? peer.Previous : null) : null;
            var previousJson = ReferenceEquals(previous, peer.Last) ? peer.LastJson : peer.PreviousJson;
            long serialize = SyncTimings.Start();
            string visibleJson = AetherfrontJson.Serialize(visible);
            if (delta && previous != null && previous.ServerId == visible.ServerId)
            {
                // Lease expiry can change the authorized projection without changing
                // the ledger revision. Equality must include the actual projection.
                var difference = StateDelta.Between(previous, visible, previousJson, visibleJson);
                if (difference.Sections.Count == 0 && difference.CraftOrder == null)
                { stateJson = string.Empty; Interlocked.Increment(ref UnchangedReplies); }
                else
                {
                    stateJson = StateDelta.Prefix + AetherfrontJson.Serialize(difference);
                    if (stateJson.Length >= visibleJson.Length)
                    { stateJson = visibleJson; Interlocked.Increment(ref FullReplies); }
                    else Interlocked.Increment(ref DeltaReplies);
                }
            }
            else { stateJson = visibleJson; Interlocked.Increment(ref FullReplies); }
            SyncTimings.Complete("snapshot.serialize.outside-lock", serialize);
            string interest; int subspace;
            using (SyncTimings.FleetScope())
            {
                if (!client.Authenticated || client.DisconnectClient || account != AetherfrontServerSystem.GetBoundAccountId(client)) return;
                // Do not publish an obsolete permissions projection after a concurrent state commit.
                if (!SnapshotStore.Current(AetherfrontServerSystem.Store, account, state.Revision))
                { lock (peer.Gate) peer.Pending = true; return; }
                subspace = client.Subspace;
                interest = (string)Interest.Invoke(null, new object[] { state, account, subspace });
            }
            var reply = new AetherfrontWireReply
                {
                    Operation = AetherfrontWireOperation.SnapshotReply, Accepted = true, ReasonCode = "Accepted",
                    Revision = visible.Revision, ServerId = visible.ServerId, StateJson = stateJson, EventsJson = "[]",
                    InterestJson = interest
                };
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
            long envelope = SyncTimings.Start();
            data.PayloadJson = AetherfrontProtocol.Serialize(reply);
            SyncTimings.Complete("snapshot.envelope.outside-lock", envelope);
            using (SyncTimings.FleetScope())
            {
                if (!client.Authenticated || client.DisconnectClient || account != AetherfrontServerSystem.GetBoundAccountId(client)) return;
                if (client.Subspace != subspace || !SnapshotStore.Current(AetherfrontServerSystem.Store, account, state.Revision))
                { lock (peer.Gate) peer.Pending = true; return; }
                MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
                peer.Previous = peer.Account == account ? peer.Last : null; peer.Last = visible; peer.Account = account;
                peer.PreviousJson = peer.Previous == null ? null : peer.LastJson; peer.LastJson = visibleJson;
                peer.LastExpected = expected; peer.LastRevision = visible.Revision; peer.LastSent = SyncTimings.Start();
            }
        }
    }
}
