using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Aetherfront.Pacing;
using Lidgren.Network;
using LmpClient;
using LmpClient.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Interface;

internal static class Program
{
    private static int checks, cases;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Need(bool condition, string message) { Interlocked.Increment(ref checks); if (!condition) throw new Exception(message); }
    private static void Case(string name, Action body)
    {
        body(); cases++; Console.WriteLine("PASS " + name);
        GC.Collect(); GC.WaitForPendingFinalizers();
    }
    private static void Throws(Action body, string name)
    {
        try { body(); } catch (InvalidDataException) { checks++; return; }
        throw new Exception("Expected fail-closed contract error: " + name);
    }
    private static OutgoingBudgetSnapshot Empty(int mtu = 1408) { return new OutgoingBudgetSnapshot { Mtu = mtu }; }
    private static OutgoingMessageCost Cost(int size, int mtu = 1408)
    {
        OutgoingMessageCost cost; Need(ReliableBudget.TryEstimate(size, mtu, out cost), "Cost not estimated"); return cost;
    }
    private static void BudgetTests()
    {
        Case("allocator bounds and invalid sizes without allocations", () =>
        {
            Need(Cost(64).BackingBytes == 1088, "64-byte backing bound");
            Need(Cost(1024).BackingBytes == 2048, "1024 equality");
            Need(Cost(2048).BackingBytes == 4096, "2x equality");
            Need(Cost(40 * 1024 * 1024).BackingBytes == ReliableBudget.ByteLimit, "Pool maximum");
            Need(Cost((int)ReliableBudget.ByteLimit).BackingBytes == ReliableBudget.ByteLimit, "64MiB exact");
            OutgoingMessageCost ignored;
            foreach (int invalid in new[] { -1, int.MinValue, 0, (int)ReliableBudget.ByteLimit + 1, int.MaxValue })
                Need(!ReliableBudget.TryEstimate(invalid, 1408, out ignored), "Invalid request must follow original path");
            Need(!ReliableBudget.TryEstimate(64, 0, out ignored), "Invalid MTU");
        });
        Case("all six soft budgets include the next message", () =>
        {
            var next = Cost(64);
            var value = Empty(); value.ConnectionBytes = ReliableBudget.ByteLimit - ReliableBudget.ByteHeadroom - next.BackingBytes;
            Need(ReliableBudget.CanSend(value, next), "Connection byte equality"); value.ConnectionBytes++; Need(!ReliableBudget.CanSend(value, next), "Connection byte overflow");
            value = Empty(); value.PeerBytes = ReliableBudget.ByteLimit - ReliableBudget.ByteHeadroom - next.BackingBytes;
            Need(ReliableBudget.CanSend(value, next), "Peer byte equality"); value.PeerBytes++; Need(!ReliableBudget.CanSend(value, next), "Peer byte overflow");
            value = Empty(); value.ConnectionArrays = ReliableBudget.ConnectionArrayLimit - ReliableBudget.ArrayHeadroom - 1;
            Need(ReliableBudget.CanSend(value, next), "Connection array equality"); value.ConnectionArrays++; Need(!ReliableBudget.CanSend(value, next), "Connection array overflow");
            value = Empty(); value.PeerArrays = ReliableBudget.PeerArrayLimit - ReliableBudget.ArrayHeadroom - 1;
            Need(ReliableBudget.CanSend(value, next), "Peer array equality"); value.PeerArrays++; Need(!ReliableBudget.CanSend(value, next), "Peer array overflow");
            value = Empty(); value.ConnectionReservations = ReliableBudget.ConnectionReservationLimit - ReliableBudget.ReservationHeadroom - next.Reservations;
            Need(ReliableBudget.CanSend(value, next), "Connection reservation equality"); value.ConnectionReservations++; Need(!ReliableBudget.CanSend(value, next), "Connection reservation overflow");
            value = Empty(); value.PeerReservations = ReliableBudget.PeerReservationLimit - ReliableBudget.ReservationHeadroom - next.Reservations;
            Need(ReliableBudget.CanSend(value, next), "Peer reservation equality"); value.PeerReservations++; Need(!ReliableBudget.CanSend(value, next), "Peer reservation overflow");
        });
        Case("large valid packet uses hard budget without permanent soft-window stall", () =>
        {
            var next = Cost(40 * 1024 * 1024);
            Need(ReliableBudget.CanSend(Empty(), next), "Large packet blocked when empty");
            var value = Empty(); value.ConnectionBytes = 1;
            Need(!ReliableBudget.CanSend(value, next), "Large packet must wait for local storage");
            value = Empty(); value.PeerBytes = 1;
            Need(!ReliableBudget.CanSend(value, next), "Large packet must wait for peer storage");
            Need(next.Reservations > 1 && next.Reservations < ReliableBudget.ConnectionReservationLimit, "Fragment estimate");
            value = Empty(); value.ConnectionReservations = ReliableBudget.ConnectionReservationLimit - next.Reservations + 1;
            Need(!ReliableBudget.CanSend(value, next), "Whole fragmented message must fit");
        });
        Case("negative and changed counter contracts fail closed", () =>
        {
            var value = Empty(); value.ConnectionBytes = -1;
            Throws(() => ReliableBudget.CanSend(value, Cost(64)), "negative bytes");
            value = Empty(); value.PeerBytes = long.MaxValue;
            Throws(() => ReliableBudget.CanSend(value, Cost(64)), "overflow bytes");
            value = Empty(); value.ConnectionArrays = ReliableBudget.ConnectionArrayLimit + 1;
            Throws(() => ReliableBudget.CanSend(value, Cost(64)), "array contract");
            value = Empty(); value.PeerReservations = int.MaxValue;
            Throws(() => ReliableBudget.CanSend(value, Cost(64)), "reservation contract");
            Throws(() => ReliableCounterReader.RequireField(typeof(NetPeer), "missing", typeof(long)), "missing field");
            Throws(() => ReliableCounterReader.RequireField(typeof(NetPeer), "m_reliableOutgoingBytes", typeof(int)), "changed type");
            Throws(() => ReliableCounterReader.VerifyTransport(typeof(NetPeer).Assembly.Location, new string('0', 64)), "changed binary");
        });
        Case("fragment bound covers actual RC42 helper for sizes, MTUs, group boundaries", () =>
        {
            Type helper = typeof(NetPeer).Assembly.GetType("Lidgren.Network.NetFragmentationHelper", true);
            MethodInfo chunk = helper.GetMethod("GetBestChunkSize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (int mtu in new[] { 64, 512, 1408, 8191 })
            foreach (int size in new[] { 1, 59, 64, 127, 128, 1024, 1403, 1404, 8192, 65535, 1048576, 33554432, 67108864 })
            {
                OutgoingMessageCost bound;
                if (!ReliableBudget.TryEstimate(size, mtu, out bound)) continue;
                foreach (int group in new[] { 1, 127, 128, 16383, 16384, 65534 })
                {
                    int actualCount = 1;
                    if ((long)size + 5 > mtu)
                    {
                        int actualChunk = (int)chunk.Invoke(null, new object[] { group, size, mtu });
                        actualCount = (int)(((long)size + actualChunk - 1) / actualChunk);
                    }
                    Need(bound.Reservations >= actualCount, "Fragment bound underestimated");
                }
            }
        });
        Case("slow relevant ACK drain resets no-progress deadline, preserving first wait", () =>
        {
            var progress = new PacingProgress(30000);
            var next = Cost(64);
            var value = Empty(); value.ConnectionBytes = ReliableBudget.ByteLimit; value.ConnectionReservations = 100;
            Need(!progress.Expired(value, next, 1000), "Initial wait expired");
            value.ConnectionReservations--;
            Need(!progress.Expired(value, next, 30000), "ACK progress not recognized");
            Need(progress.FirstWait == 1000 && progress.LastProgress == 30000, "First wait or progress timestamp incorrect");
            Need(!progress.Expired(value, next, 59000), "Slow draining job timed out on total wait");
            value.ConnectionBytes--;
            Need(!progress.Expired(value, next, 59500), "Byte progress not recognized");
            Need(!progress.Expired(value, next, 89000), "Progress deadline not extended");
            Need(progress.Expired(value, next, 89500), "No-progress equality did not expire");
        });
        Case("enqueue churn and unrelated peer drain never extend blocked deadline", () =>
        {
            var progress = new PacingProgress(30000);
            var next = Cost(64);
            var value = Empty(); value.ConnectionBytes = ReliableBudget.ByteLimit; value.ConnectionReservations = 100; value.PeerReservations = 100;
            Need(!progress.Expired(value, next, 0), "Initial wait");
            value.ConnectionReservations = 101;
            Need(!progress.Expired(value, next, 10000), "Enqueue must not expire early");
            value.ConnectionReservations = 100; value.PeerReservations = 99;
            Need(!progress.Expired(value, next, 20000) && progress.LastProgress == 0, "Churn/unrelated peer reset progress");
            Need(progress.Expired(value, next, 30000), "Heartbeat/churn prevented timeout");
            progress = new PacingProgress(30000);
            value = Empty(); value.PeerBytes = ReliableBudget.ByteLimit; value.PeerReservations = 100;
            Need(!progress.Expired(value, next, 0), "Initial peer wait");
            value.PeerReservations--;
            Need(!progress.Expired(value, next, 29000) && progress.LastProgress == 29000, "Relevant peer ACK drain ignored");
        });
    }

    private sealed class Fixture : IDisposable
    {
        public readonly NetClient Peer;
        public readonly NetConnection Connection;
        public readonly object Channel;
        public readonly ReliableCounterReader Reader = new ReliableCounterReader();
        private readonly Type channelType;
        private int nextAcknowledgement;
        private double now = 1;
        public object Gate { get { return Get(typeof(NetConnection), Connection, "m_reliableOutgoingLock"); } }
        public object PeerGate { get { return Get(typeof(NetPeer), Peer, "m_reliableStorageLock"); } }
        public Fixture()
        {
            NetworkConnection.ResetForTest();
            Peer = new NetClient(new NetPeerConfiguration("isolated-rc43-pacing"));
            typeof(NetPeer).GetMethod("InitializePools", Flags).Invoke(Peer, null);
            Set(typeof(NetPeer), Peer, "m_status", NetPeerStatus.Running);
            Set(typeof(NetPeer), Peer, "m_sendBuffer", new byte[65536]);
            Connection = (NetConnection)typeof(NetConnection).GetConstructor(Flags, null,
                new[] { typeof(NetPeer), typeof(IPEndPoint) }, null).Invoke(new object[] { Peer, new IPEndPoint(IPAddress.Loopback, 9) });
            Set(typeof(NetConnection), Connection, "m_status", NetConnectionStatus.Connected);
            Set(typeof(NetConnection), Connection, "m_visibleStatus", NetConnectionStatus.Connected);
            ((IList)Get(typeof(NetPeer), Peer, "m_connections")).Add(Connection);
            channelType = typeof(NetPeer).Assembly.GetType("Lidgren.Network.NetReliableSenderChannel", true);
            // Sixteen 64-byte messages plus headers stay below the actual 1408-byte MTU.
            Channel = Activator.CreateInstance(channelType, Flags, null, new object[] { Connection, 16 }, null);
            NetworkMain.ClientConnection = Peer;
        }
        public OutgoingBudgetSnapshot Snapshot()
        {
            OutgoingBudgetSnapshot value; Need(Reader.TryRead(Peer, Connection, out value), "Snapshot not connected"); return value;
        }
        public void SetConnectionBytes(long bytes)
        {
            lock (Gate) lock (PeerGate) Set(typeof(NetConnection), Connection, "m_reliableOutgoingBytes", bytes);
        }
        public void SyntheticLimit(string name)
        {
            lock (Gate) lock (PeerGate)
            {
                var local = (IDictionary)Get(typeof(NetConnection), Connection, "m_reliableOutgoingBuffers");
                var peer = (IDictionary)Get(typeof(NetPeer), Peer, "m_reliableOutgoingBuffers");
                local.Clear(); peer.Clear();
                Set(typeof(NetConnection), Connection, "m_reliableOutgoingBytes", 0L);
                Set(typeof(NetPeer), Peer, "m_reliableOutgoingBytes", 0L);
                Set(typeof(NetConnection), Connection, "m_reliableOutgoingMessages", 0);
                Set(typeof(NetPeer), Peer, "m_reliableOutgoingMessages", 0);
                if (name == null) return;
                bool connection = name.StartsWith("connection", StringComparison.Ordinal);
                object target = connection ? (object)Connection : Peer;
                Type type = connection ? typeof(NetConnection) : typeof(NetPeer);
                if (name.EndsWith("bytes", StringComparison.Ordinal))
                    Set(type, target, "m_reliableOutgoingBytes", ReliableBudget.ByteLimit);
                else if (name.EndsWith("reservations", StringComparison.Ordinal))
                    Set(type, target, "m_reliableOutgoingMessages", connection ? ReliableBudget.ConnectionReservationLimit : ReliableBudget.PeerReservationLimit);
                else
                {
                    IDictionary buffers = connection ? local : peer;
                    int count = connection ? ReliableBudget.ConnectionArrayLimit : ReliableBudget.PeerArrayLimit;
                    for (int i = 0; i < count; i++) buffers.Add(new byte[1], 1);
                    Set(type, target, "m_reliableOutgoingBytes", (long)count);
                    Set(type, target, "m_reliableOutgoingMessages", count);
                }
            }
        }
        public NetSendResult Enqueue(FakeMessage message)
        {
            NetOutgoingMessage outgoing = Peer.CreateMessage(message.Size);
            message.Serialize(outgoing);
            Set(typeof(NetOutgoingMessage), outgoing, "m_recyclingCount", 1);
            object ordered = Enum.Parse(typeof(NetPeer).Assembly.GetType("Lidgren.Network.NetMessageType", true), "UserReliableOrdered1");
            Set(typeof(NetOutgoingMessage), outgoing, "m_messageType", ordered);
            return (NetSendResult)channelType.GetMethod("Enqueue", Flags).Invoke(Channel, new object[] { outgoing });
        }
        public int Drain(List<int> received)
        {
            lock (Gate)
            {
                Set(typeof(NetConnection), Connection, "m_sendBufferWritePtr", 0);
                Set(typeof(NetConnection), Connection, "m_sendBufferNumMessages", 0);
                now += .01;
                channelType.GetMethod("SendQueuedMessages", Flags).Invoke(Channel, new object[] { now });
                Need((int)Get(typeof(NetConnection), Connection, "m_sendBufferWritePtr") < Connection.CurrentMTU, "A socket send would be required");
                Array stored = (Array)Get(channelType, Channel, "m_storedMessages");
                var pending = new Dictionary<int, int>();
                foreach (object item in stored)
                {
                    FieldInfo messageField = item.GetType().GetField("Message", Flags);
                    var outgoing = messageField.GetValue(item) as NetOutgoingMessage;
                    if (outgoing != null)
                    {
                        int sequence = (int)item.GetType().GetField("SequenceNumber", Flags).GetValue(item);
                        pending.Add(sequence, BitConverter.ToInt32(outgoing.Data, 0));
                    }
                }
                int count = pending.Count;
                for (int i = 0; i < count; i++)
                {
                    int sequence = nextAcknowledgement++ % 1024;
                    Need(pending.ContainsKey(sequence), "Noncontiguous send sequence");
                    received.Add(pending[sequence]);
                    channelType.GetMethod("ReceiveAcknowledge", Flags).Invoke(Channel, new object[] { now + .001, sequence });
                }
                return count;
            }
        }
        public void Dispose()
        {
            lock (Gate)
            {
                channelType.GetMethod("Reset", Flags).Invoke(Channel, null);
                // Scalar-injection tests restore their artificial budget before disposal.
                Need((int)Get(typeof(NetConnection), Connection, "m_reliableOutgoingMessages") == 0, "Reset local reservations");
                Need((long)Get(typeof(NetConnection), Connection, "m_reliableOutgoingBytes") == 0, "Reset local bytes");
                Need((int)Get(typeof(NetPeer), Peer, "m_reliableOutgoingMessages") == 0, "Reset peer reservations");
                Need((long)Get(typeof(NetPeer), Peer, "m_reliableOutgoingBytes") == 0, "Reset peer bytes");
            }
            Need(Get(typeof(NetPeer), Peer, "m_socket") == null, "Socket created");
            Need(Get(typeof(NetPeer), Peer, "m_networkThread") == null, "Network thread created");
            Set(typeof(NetPeer), Peer, "m_status", NetPeerStatus.NotRunning);
        }
    }
    private static object Get(Type type, object target, string name) { return type.GetField(name, Flags).GetValue(target); }
    private static void Set(Type type, object target, string name, object value) { type.GetField(name, Flags).SetValue(target, value); }
    private static void WaitUntil(Func<bool> condition, int timeout, string name)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > timeout) throw new Exception("Timeout: " + name); Thread.Sleep(1); }
    }
    private static bool SenderInvocation(FakeMessage message, Fixture fixture, CancellationToken token, int generation)
    {
        try
        {
            if (!ClientPacing.WaitForCapacity(message, token, generation)) return false;
            message.Data.SentTime = 123;
            NetSendResult result = fixture.Enqueue(message);
            Need(result == NetSendResult.Sent || result == NetSendResult.Queued, "Transport rejected paced message");
            return true;
        }
        finally { message.Recycle(); }
    }
    private static void HookTests(bool includeDeadline)
    {
        foreach (string name in new[] { "connection-bytes", "peer-bytes", "connection-arrays", "peer-arrays", "connection-reservations", "peer-reservations" })
        {
            string limit = name;
            Case("actual RC42 " + limit + " guard and reflected pacing snapshot", () =>
            {
                using (var fixture = new Fixture())
                using (var cancellation = new CancellationTokenSource())
                {
                    fixture.SyntheticLimit(limit);
                    var before = fixture.Snapshot();
                    NetOutgoingMessage outgoing = fixture.Peer.CreateMessage(64);
                    MethodInfo reserve = typeof(NetConnection).GetMethod("TryReserveReliableMessage", Flags);
                    Need(!(bool)reserve.Invoke(fixture.Connection, new object[] { outgoing }), "Actual hard guard did not reject " + limit);
                    Need(!ReliableBudget.CanSend(before, Cost(64)), "Policy accepted hard limit " + limit);
                    var message = new FakeMessage(17);
                    int generation = NetworkConnection.Generation;
                    Task<bool> worker = Task.Run(() => SenderInvocation(message, fixture, cancellation.Token, generation));
                    Thread.Sleep(40);
                    Need(!worker.IsCompleted && message.Serializations == 0 && message.Recycles == 0, "Limit did not preserve pending job");
                    cancellation.Cancel(); Need(worker.Wait(2000) && !worker.Result, "Limit did not cancel");
                    Need(message.Recycles == 1 && NetworkMain.Disconnects == 0, "Limit cancellation ownership/session failure");
                    var after = fixture.Snapshot();
                    Need(before.ConnectionBytes == after.ConnectionBytes && before.PeerBytes == after.PeerBytes &&
                         before.ConnectionArrays == after.ConnectionArrays && before.PeerArrays == after.PeerArrays &&
                         before.ConnectionReservations == after.ConnectionReservations && before.PeerReservations == after.PeerReservations,
                         "Read-only gate mutated budget");
                    fixture.SyntheticLimit(null);
                }
            });
        }
        Case("4097 FIFO messages, real ACK drain, no cap rejection or socket", () =>
        {
            using (var fixture = new Fixture())
            {
                var messages = Enumerable.Range(0, 4097).Select(i => new FakeMessage(i)).ToArray();
                var applicationQueue = new Queue<FakeMessage>(messages);
                int generation = NetworkConnection.Generation, accepted = 0;
                Task worker = Task.Run(() =>
                {
                    while (applicationQueue.Count != 0)
                    {
                        FakeMessage pending = applicationQueue.Dequeue();
                        Need(SenderInvocation(pending, fixture, CancellationToken.None, generation), "Healthy job aborted");
                        Interlocked.Increment(ref accepted);
                    }
                });
                WaitUntil(() => (fixture.Snapshot().ConnectionArrays == 3584 && Volatile.Read(ref accepted) == 3584 &&
                    (bool)Get(typeof(NetPeer), fixture.Peer, "m_executeFlushSendQueue")) || worker.IsCompleted, 10000, "soft array boundary and flush");
                Need(!worker.IsCompleted, "Expected pacing stall before 4097");
                Need(Volatile.Read(ref accepted) == 3584, "Incorrect soft array boundary");
                Need(messages[3584].Serializations == 0 && messages[3584].Data.SentTime == 0 && messages[3584].Recycles == 0, "Pending job mutated or recycled while blocked");
                Need((bool)Get(typeof(NetPeer), fixture.Peer, "m_executeFlushSendQueue"), "Blocked sender did not request flush");
                var received = new List<int>();
                var timer = Stopwatch.StartNew();
                while (!worker.IsCompleted || fixture.Snapshot().ConnectionReservations != 0)
                {
                    fixture.Drain(received);
                    Need(timer.ElapsedMilliseconds < 10000, "ACK drain stalled");
                    Thread.Yield();
                }
                worker.GetAwaiter().GetResult();
                Need(accepted == 4097 && applicationQueue.Count == 0 && received.Count == 4097, "Message loss");
                for (int i = 0; i < received.Count; i++) Need(received[i] == i, "FIFO changed");
                foreach (FakeMessage message in messages) Need(message.Recycles == 1 && message.Serializations == 1, "Message recycled/serialized more than once");
                Need(NetworkMain.Disconnects == 0, "Healthy paced send disconnected");
            }
        });
        foreach (string reason in new[] { "cancel", "reset", "generation", "peer", "connection", "disconnect", "internal-disconnect", "state" })
        {
            string copy = reason;
            Case("pending job aborts on " + reason + ", original finally recycles once", () =>
            {
                using (var fixture = new Fixture())
                using (var cancellation = new CancellationTokenSource())
                {
                    fixture.SetConnectionBytes(ReliableBudget.ByteLimit);
                    var message = new FakeMessage(42);
                    int generation = NetworkConnection.Generation;
                    Task<bool> worker = Task.Run(() => SenderInvocation(message, fixture, cancellation.Token, generation));
                    Thread.Sleep(50);
                    Need(!worker.IsCompleted && message.Recycles == 0 && message.Serializations == 0, "Job not retained on sender stack");
                    if (copy == "cancel") cancellation.Cancel();
                    else if (copy == "reset") NetworkConnection.ResetRequested = true;
                    else if (copy == "generation") NetworkConnection.Advance();
                    else if (copy == "peer") NetworkMain.ClientConnection = null;
                    else if (copy == "connection") ((IList)Get(typeof(NetPeer), fixture.Peer, "m_connections")).Clear();
                    else if (copy == "disconnect") Set(typeof(NetConnection), fixture.Connection, "m_visibleStatus", NetConnectionStatus.Disconnected);
                    else if (copy == "internal-disconnect") Set(typeof(NetConnection), fixture.Connection, "m_disconnectRequested", true);
                    else NetworkConnection.SessionAllowed = false;
                    Need(worker.Wait(2000) && !worker.Result, "Abort did not return false");
                    Need(message.Recycles == 1 && message.Serializations == 0 && message.Data.SentTime == 0, "Abort ownership violated");
                    Need(NetworkMain.Disconnects == 0, "Aborting old work disconnected another session");
                    fixture.SetConnectionBytes(0);
                }
            });
        }
        Case("master/unreliable unchanged and direct hook never recycles", () =>
        {
            NetworkMain.ClientConnection = null;
            var cancelled = new CancellationToken(true);
            foreach (FakeMessage message in new FakeMessage[] { new FakeMaster(), new FakeMessage(1, NetDeliveryMethod.Unreliable), new FakeMessage(2, NetDeliveryMethod.UnreliableSequenced) })
            {
                Need(ClientPacing.WaitForCapacity(message, cancelled, -1), "Unreliable/master behavior changed");
                Need(message.Recycles == 0 && message.SizeQueries == 0, "Bypass touched ownership/size");
            }
            var reliable = new FakeMessage(3);
            Need(!ClientPacing.WaitForCapacity(reliable, CancellationToken.None, -1), "Missing peer accepted");
            Need(reliable.Recycles == 0, "Hook recycled abort");
        });
        Case("counter contract failure disconnects current generation and fails closed", () =>
        {
            using (var fixture = new Fixture())
            {
                fixture.SetConnectionBytes(-1);
                var message = new FakeMessage(1);
                Need(!SenderInvocation(message, fixture, CancellationToken.None, NetworkConnection.Generation), "Bad counter accepted");
                Need(message.Recycles == 1 && message.Serializations == 0, "Contract failure ownership");
                Need(NetworkMain.Disconnects == 1, "Contract failure did not disconnect safely");
                fixture.SetConnectionBytes(0);
            }
        });
        Case("shared storage remains reference-counted; snapshots never reserve", () =>
        {
            using (var fixture = new Fixture())
            {
                NetOutgoingMessage outgoing = fixture.Peer.CreateMessage(64);
                MethodInfo reserve = typeof(NetConnection).GetMethod("TryReserveReliableMessage", Flags);
                MethodInfo release = typeof(NetConnection).GetMethod("ReleaseReliableMessage", Flags);
                Need((bool)reserve.Invoke(fixture.Connection, new object[] { outgoing }), "Reserve shared first");
                Need((bool)reserve.Invoke(fixture.Connection, new object[] { outgoing }), "Reserve shared second");
                for (int i = 0; i < 100; i++)
                {
                    var value = fixture.Snapshot();
                    Need(value.ConnectionArrays == 1 && value.PeerArrays == 1 && value.ConnectionBytes == 64 && value.PeerBytes == 64 &&
                         value.ConnectionReservations == 2 && value.PeerReservations == 2, "Shared bytes double-counted or snapshot mutated reservation");
                }
                release.Invoke(fixture.Connection, new object[] { outgoing });
                Need(fixture.Snapshot().ConnectionBytes == 64, "First release freed shared bytes");
                release.Invoke(fixture.Connection, new object[] { outgoing });
                Need(fixture.Snapshot().PeerBytes == 0, "Final release retained bytes");
            }
        });
        Case("diagnostics are globally rate limited during stalls", () =>
        {
            using (var fixture = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                typeof(ClientPacing).GetField("nextLogTick", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 0L);
                while (LunaLog.Lines.TryDequeue(out _)) { }
                fixture.SetConnectionBytes(ReliableBudget.ByteLimit);
                var message = new FakeMessage(7);
                int generation = NetworkConnection.Generation;
                Task<bool> worker = Task.Run(() => SenderInvocation(message, fixture, cancellation.Token, generation));
                Thread.Sleep(150);
                cancellation.Cancel(); Need(worker.Wait(2000), "Log test did not abort");
                Need(LunaLog.Lines.Count == 1 && LunaLog.Lines.TryPeek(out string line) && line.Contains("connectionBytes="), "Log spam or missing counters");
                fixture.SetConnectionBytes(0);
            }
        });
        if (includeDeadline) Case("30-second blocked deadline disconnects despite live peer status", () =>
        {
            using (var fixture = new Fixture())
            {
                fixture.SetConnectionBytes(ReliableBudget.ByteLimit);
                var message = new FakeMessage(9);
                int generation = NetworkConnection.Generation;
                var timer = Stopwatch.StartNew();
                Task<bool> worker = Task.Run(() => SenderInvocation(message, fixture, CancellationToken.None, generation));
                Need(worker.Wait(35000) && !worker.Result, "Blocked deadline failed");
                Need(timer.ElapsedMilliseconds >= 29900 && timer.ElapsedMilliseconds < 35000, "Incorrect blocked deadline");
                Need(NetworkMain.Disconnects == 1 && NetworkMain.LastDisconnect is TimeoutException, "Deadline did not use disconnect handler");
                Need(message.Recycles == 1 && message.Serializations == 0, "Deadline lost/double-recycled pending job");
                Need(fixture.Peer.Status == NetPeerStatus.Running, "Fixture peer was not alive during deadline");
                fixture.SetConnectionBytes(0);
            }
        });
    }
    private static int Main(string[] args)
    {
        try
        {
            BudgetTests(); HookTests(!args.Contains("--skip-deadline"));
            Console.WriteLine("RESULT cases=" + cases + " checks=" + checks + " sockets=0 networkThreads=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private class FakeMessage : IMessageBase
    {
        public readonly int Id;
        public int Size = 64, Recycles, Serializations, SizeQueries;
        public FakeMessage(int id, NetDeliveryMethod delivery = NetDeliveryMethod.ReliableOrdered) { Id = id; NetDeliveryMethod = delivery; Data = new FakeData(); }
        public string ClassName { get { return "PacingFixture"; } }
        public IMessageData Data { get; private set; }
        public bool VersionMismatch { get; set; }
        public NetDeliveryMethod NetDeliveryMethod { get; private set; }
        public int Channel { get { return 0; } }
        public void SetData(IMessageData value) { Data = value; }
        public IMessageData GetMessageData(ushort type) { return Data; }
        public void Serialize(NetOutgoingMessage message) { Serializations++; message.Write(Id); message.Write(new byte[60]); }
        public void Recycle() { Recycles++; }
        public int GetMessageSize() { SizeQueries++; return Size; }
    }
    private sealed class FakeMaster : FakeMessage, IMasterServerMessageBase
    {
        public FakeMaster() : base(0) { }
        public MasterServerMessageType MessageType { get { return default(MasterServerMessageType); } }
    }
    private sealed class FakeData : IMessageData
    {
        public string ClassName { get { return "PacingFixtureData"; } }
        public ushort MajorVersion { get { return 0; } }
        public ushort MinorVersion { get { return 0; } }
        public ushort BuildVersion { get { return 0; } }
        public long ReceiveTime { get; set; }
        public ushort SubType { get { return 0; } }
        public long SentTime { get; set; }
        public void Serialize(NetOutgoingMessage message) { }
        public void Deserialize(NetIncomingMessage message) { }
        public int GetMessageSize() { return 64; }
    }
}
