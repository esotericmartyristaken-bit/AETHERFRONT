using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Aetherfront.Core;
using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Interface;

// The fake owns only a managed DTO and counters. Every transport operation fails
// immediately if reached. Actual candidate HandleMessage and finally execute.
internal sealed class FakeMessage : IServerMessageBase
{
    internal IMessageData Payload;
    internal int Recycles, DataReads, DeliveryReads, Serializations;
    public string ClassName { get { return "IsolatedMonoFixture"; } }
    public IMessageData Data { get { DataReads++; return Payload; } }
    public bool VersionMismatch { get; set; }
    public NetDeliveryMethod NetDeliveryMethod { get { DeliveryReads++; return NetDeliveryMethod.Unreliable; } }
    public int Channel { get { return 0; } }
    public ServerMessageType MessageType { get { return default(ServerMessageType); } }
    public void SetData(IMessageData data) { Payload = data; }
    public IMessageData GetMessageData(ushort subtype) { throw new InvalidOperationException("Fixture cannot allocate transport data"); }
    public void Serialize(NetOutgoingMessage message) { Serializations++; throw new InvalidOperationException("Fixture cannot serialize transport packets"); }
    public void Recycle() { Recycles++; }
    public int GetMessageSize() { throw new InvalidOperationException("Fixture cannot enter a transport send"); }
}

internal static class ReceiverProgram
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly client, helper;
    private static Type system, outbox;
    private static MethodInfo handle;
    private static AetherfrontWireReply sentinelReply;
    private static object sentinelState;
    private static DateTime sentinelTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static FieldInfo Field(Type type, string name) { return type.GetField(name, Static) ?? throw new MissingFieldException(type.FullName, name); }
    private static object Get(string name) { return Field(system, name).GetValue(null); }
    private static void Set(string name, object value) { Field(system, name).SetValue(null, value); }
    private static object Queue { get { return Field(outbox, "receipts").GetValue(null); } }
    private static int Queued { get { return (int)Queue.GetType().GetProperty("Count").GetValue(Queue, null); } }
    private static string Hash(string path) { using (var h = SHA256.Create()) using (var s = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    private static string Receipt(string disposition = "accepted")
    {
        return AetherfrontJson.Serialize(new
        {
            ProtocolVersion = 1, Operation = 3, Accepted = disposition == "accepted", ReasonCode = "fixture-receipt", Revision = 12L, ServerId = "fixture-server",
            ScienceReceipt = new { RequestId = "science:1234567890abcdef1234567890abcdef", AccountId = "fixture-account", Command = 40, SourceId = "science:fixture", RequestedAmount = 19.123456m, RecordedCumulative = 19.123456m, Disposition = disposition }
        });
    }
    private static FakeMessage Message(string payload, string container = "AetherfrontCommandReplyMsgData")
    {
        Type dataType = client.GetType("LmpCommon.Message.Data.Aetherfront." + container, true);
        object data = Activator.CreateInstance(dataType, true);
        var field = dataType.GetField("PayloadJson", BindingFlags.Public | BindingFlags.Instance);
        MonoGate.Check(field != null && field.FieldType == typeof(string), "Actual payload is a public string field");
        field.SetValue(data, payload);
        return new FakeMessage { Payload = (IMessageData)data };
    }
    private static void Prepare()
    {
        Queue.GetType().GetMethod("Clear").Invoke(Queue, null);
        sentinelReply = new AetherfrontWireReply { ReasonCode = "untouched-sentinel" };
        sentinelState = new AetherfrontState { ServerId = "untouched-server", Revision = 777 };
        Set("<LastReply>k__BackingField", sentinelReply); Set("<State>k__BackingField", sentinelState);
        Set("<Status>k__BackingField", "untouched-status"); Set("<LastCommandStatus>k__BackingField", "untouched-command-status"); Set("<LastSnapshotUtc>k__BackingField", sentinelTime);
        Set("_snapshotRequested", true); Set("_progressionApplyPending", 271); Set("_progressionFactionId", "untouched-faction");
    }
    private static void Unchanged(FakeMessage message)
    {
        MonoGate.Equal(1, message.Recycles, "Existing finally recycles exactly once"); MonoGate.Equal(1, message.DataReads, "Actual handler reads real message data once");
        MonoGate.Equal(0, message.Serializations, "No packet serialization"); MonoGate.Equal(0, message.DeliveryReads, "No sender or transport reached");
        MonoGate.Check(object.ReferenceEquals(sentinelReply, Get("<LastReply>k__BackingField")), "Compact receipt must not reach ApplyReply");
        MonoGate.Check(object.ReferenceEquals(sentinelState, Get("<State>k__BackingField")), "Compact receipt preserves state object");
        MonoGate.Equal(true, (bool)Get("_snapshotRequested"), "Compact receipt cannot reset snapshot-request flag");
        MonoGate.Equal(271, (int)Get("_progressionApplyPending"), "Compact receipt cannot alter progression-apply flag");
        MonoGate.Equal("untouched-faction", (string)Get("_progressionFactionId"), "Progression faction untouched");
        MonoGate.Equal("untouched-status", (string)Get("<Status>k__BackingField"), "No error/fallback/status mutation");
        MonoGate.Equal("untouched-command-status", (string)Get("<LastCommandStatus>k__BackingField"), "Command status untouched");
        MonoGate.Equal(sentinelTime, (DateTime)Get("<LastSnapshotUtc>k__BackingField"), "Receipt does not pretend to be a snapshot");
    }
    private static void Valid(string container, string disposition)
    {
        string json = Receipt(disposition); File.WriteAllText(Path.Combine(MonoGate.CaseRoot, "receipt.json"), json);
        var message = Message(json, container); handle.Invoke(null, new object[] { message }); Unchanged(message); MonoGate.Equal(1, Queued, "Actual helper queued compact receipt");
        object envelope = Queue.GetType().GetMethod("Peek").Invoke(Queue, null);
        MonoGate.Equal(12L, (long)envelope.GetType().GetProperty("Revision").GetValue(envelope, null), "Actual receipt revision preserved");
    }
    public static int Main()
    {
        string stage = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        client = typeof(IServerMessageBase).Assembly;
        helper = Assembly.LoadFrom(Path.Combine(stage, "AetherfrontPersonalClient.dll"));
        system = client.GetType("LmpClient.Aetherfront.AetherfrontClientSystem", true);
        outbox = helper.GetType("Aetherfront.Personal.Client.ScienceOutbox", true);
        handle = system.GetMethod("HandleMessage", Static, null, new[] { typeof(IServerMessageBase) }, null);
        MonoGate.Case("receiver", "actual-final-candidate-hashes-and-helper-signatures", () =>
        {
            MonoGate.Equal(Environment.GetEnvironmentVariable("AF_SCIENCE_MONO_CLIENT_HASH") ?? "6eb9ecd45e45fa908ca532eea1d632ddefae7a24547d9bd1d976390fcd86f801", Hash(client.Location), "Actual loaded LmpClient hash pinned");
            MonoGate.Equal(Environment.GetEnvironmentVariable("AF_SCIENCE_MONO_HELPER_HASH") ?? "02157055d47a3fe3c68b05fe8637f666db5af1815b8d5d23e14f2b78eb36da61", Hash(helper.Location), "Actual loaded personal helper hash pinned");
            var capture = helper.GetType("Aetherfront.Personal.Client.PersonalClient", true).GetMethod("CaptureScienceReceipt", Static, null, new[] { typeof(string) }, null);
            MonoGate.Check(capture != null && capture.IsPublic && capture.ReturnType == typeof(bool), "Actual public bool CaptureScienceReceipt(string) contract");
            MonoGate.Check(handle != null && handle.ReturnType == typeof(void), "Actual HandleMessage(IServerMessageBase) contract");
            var pace = helper.GetType("Aetherfront.Pacing.ClientPacing", true).GetMethod("WaitForCapacity", Static, null, new[] { typeof(IMessageBase), typeof(CancellationToken), typeof(int) }, null);
            MonoGate.Check(pace != null && pace.IsPublic && pace.ReturnType == typeof(bool), "Actual pacing helper signature");
        });
        foreach (string container in new[] { "AetherfrontCommandReplyMsgData", "AetherfrontSnapshotMsgData", "AetherfrontEventNoticeMsgData" })
        {
            string name = container;
            MonoGate.Case("receiver", "accepted-" + name, () => Valid(name, "accepted"), Prepare);
        }
        foreach (string disposition in new[] { "terminal", "retry" })
        {
            string value = disposition;
            MonoGate.Case("receiver", value + "-compact-receipt", () => Valid("AetherfrontCommandReplyMsgData", value), Prepare);
        }
        MonoGate.Case("receiver", "ordinary-reply-still-runs-original-ApplyReply", () =>
        {
            string payload = AetherfrontProtocol.Serialize(new AetherfrontWireReply { ProtocolVersion = 1, Operation = AetherfrontWireOperation.SnapshotReply, Accepted = true, ReasonCode = "fixture-normal", ServerId = "fixture-server", Revision = 1 });
            var message = Message(payload); handle.Invoke(null, new object[] { message });
            MonoGate.Equal(1, message.Recycles, "Ordinary reply recycles once"); MonoGate.Equal(0, Queued, "Ordinary reply not captured as receipt");
            MonoGate.Check(!object.ReferenceEquals(sentinelReply, Get("<LastReply>k__BackingField")), "Ordinary reply reaches original ApplyReply");
            MonoGate.Equal(false, (bool)Get("_snapshotRequested"), "Ordinary reply retains original snapshot-flag reset"); MonoGate.Equal("Ready", (string)Get("<Status>k__BackingField"), "Ordinary reply original status");
            MonoGate.Equal(271, (int)Get("_progressionApplyPending"), "State-less ordinary reply does not invent progression update");
        }, Prepare);
        MonoGate.Case("receiver", "empty-message-still-unwinds-original-finally", () =>
        {
            var message = Message(""); handle.Invoke(null, new object[] { message }); Unchanged(message); MonoGate.Equal(0, Queued, "Empty payload not captured");
        }, Prepare);
        MonoGate.Case("receiver", "pacing-cancelled-token-unwinds-original-sender-finally", () =>
        {
            // Unreliable is intentional: hook reads delivery then bypasses NetworkMain.
            // The already-cancelled token exits before any client/session/native access.
            Type sender = client.GetType("LmpClient.Network.NetworkSender", true);
            var method = sender.GetMethod("SendNetworkMessage", Static, null, new[] { typeof(IMessageBase), typeof(CancellationToken), typeof(int) }, null);
            MonoGate.Check(method != null && method.IsPrivate && method.ReturnType == typeof(bool), "Actual private sender signature");
            var message = new FakeMessage();
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool sent = (bool)method.Invoke(null, new object[] { message, cancel.Token, int.MinValue });
                MonoGate.Check(!sent, "Cancelled actual sender returns false");
            }
            MonoGate.Equal(1, message.DeliveryReads, "Actual grafted pacing hook executed once");
            MonoGate.Equal(0, message.DataReads, "Cancellation exits before data/session send path"); MonoGate.Equal(0, message.Serializations, "No packet serialization"); MonoGate.Equal(1, message.Recycles, "Original sender finally alone recycles once");
        });
        return MonoGate.Finish("receiver");
    }
}
