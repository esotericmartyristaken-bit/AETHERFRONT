using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetherfront.Core;
using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Interface;

if (args.Length != 2 || args[0] is not ("--loopback" or "--live-review"))
    throw new ArgumentException("Explicit --loopback or authorized --live-review and a new evidence directory required.");
int testPort = Environment.GetEnvironmentVariable("APF_CHECKPOINT_TEST_PORT") == "28875" ? 28875 : 28865;
var target = args[0] == "--loopback" ? new IPEndPoint(IPAddress.Loopback, testPort) : new IPEndPoint(IPAddress.Parse("217.216.49.250"), 18865);
var evidence = Path.GetFullPath(args[1]);
if (Directory.Exists(evidence)) throw new IOException("Use new evidence; preserve earlier results.");
Directory.CreateDirectory(evidence);
using var key = RSA.Create(2048);
using var observerKey = RSA.Create(2048);
string name = "CP53" + Guid.NewGuid().ToString("N").Substring(0, 7);
int checks = 0;
void Check(bool value, string what) { checks++; if (!value) throw new InvalidOperationException(what); Console.WriteLine("PASS " + what); }
using var peer = new Peer(target, name, key);
using var observer = new Peer(target, name + "B", observerKey);
observer.Resume();
double observerTime = observer.Checkpoint(80).GetProperty("FlightCheckpoint").GetProperty("MissionTime").GetDouble();
double startTime = peer.Resume();
Guid vessel = Guid.NewGuid(), secondVessel = Guid.NewGuid();
peer.Proto(vessel, "VAB Launch", startTime, 5);
Thread.Sleep(300);
peer.Proto(secondVessel, "SPH Launch", startTime, 5);
Thread.Sleep(1200);
var launch = peer.Checkpoint(81);
Check(launch.GetProperty("Accepted").GetBoolean(), "Metadata received from authenticated server");
Check(launch.GetProperty("FlightCheckpoint").GetProperty("Points").EnumerateArray().Any(point => point.GetProperty("Label").GetString() == "Latest launch"), "Native accepted launch pins a whole-fleet checkpoint");
string launchId = launch.GetProperty("FlightCheckpoint").GetProperty("Points").EnumerateArray().Single(point => point.GetProperty("Label").GetString() == "Latest launch").GetProperty("CheckpointId").GetString();
var saved = peer.Checkpoint(80);
Check(saved.GetProperty("Accepted").GetBoolean(), "Manual fleet checkpoint acknowledged over UDP");
var info = saved.GetProperty("FlightCheckpoint");
string checkpoint = info.GetProperty("CheckpointId").GetString();
Check(info.GetProperty("VesselCount").GetInt32() == 2, "New test account checkpoint contains both of its own vessels");
peer.Proto(vessel, "VAB Launch", startTime + 2, 1);
Thread.Sleep(500);
var revisited = peer.Checkpoint(81);
Check(revisited.GetProperty("FlightCheckpoint").GetProperty("Points").EnumerateArray().Single(point => point.GetProperty("Label").GetString() == "Latest launch").GetProperty("CheckpointId").GetString() == launchId,
    "Returning to an older craft with stale launch metadata cannot overwrite the launch checkpoint");
Thread.Sleep(8000);
peer.Checkpoint(82, checkpoint, info.GetProperty("Generation").GetInt64(), true);
Check(peer.WaitForRestoreDisconnect(), "Restore completes through the native reconnect path");
Thread.Sleep(700);
using var resumed = new Peer(target, name, key);
resumed.Resume();
var returned = resumed.Checkpoint(81);
Check(returned.GetProperty("Accepted").GetBoolean() && returned.GetProperty("FlightCheckpoint").GetProperty("Generation").GetInt64() == 1,
    "One completed restore receipt survives reconnect");
var current = resumed.Checkpoint(80);
Check(current.GetProperty("Accepted").GetBoolean() && Math.Abs(current.GetProperty("FlightCheckpoint").GetProperty("MissionTime").GetDouble() - info.GetProperty("MissionTime").GetDouble()) < 3,
    "Authenticated reconnect resumes saved personal mission time after eight elapsed seconds");
string proto = resumed.Fetch(vessel);
Check(proto.Contains("amount = 5", StringComparison.Ordinal), "Saved vessel resource state is served after reconnect");
Check(observer.Checkpoint(81).GetProperty("Accepted").GetBoolean(), "Second player stays connected throughout personal restore");
Check(observer.Checkpoint(80).GetProperty("FlightCheckpoint").GetProperty("MissionTime").GetDouble() >= observerTime + 8,
    "Second player's personal clock advances throughout the other player's rewind");
resumed.Remove(vessel);
resumed.Remove(secondVessel);
File.WriteAllText(Path.Combine(evidence, "RESULTS.json"), JsonSerializer.Serialize(new {
    checks, passed = true, endpoint = target.ToString(), release = "kesas-rc53", capturedUtc = DateTimeOffset.UtcNow,
    protocolClients = true, unityGameplayVerified = false, transientCraftRemoved = true,
    identityPrivateKeysPersisted = false, personalClockRestored = true, nativeLaunchHook = true
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS " + checks + " actual network checkpoint assertions; no Unity gameplay claim.");

sealed class Peer : IDisposable
{
    readonly NetClient connection = new(new NetPeerConfiguration("LMP"));
    readonly ClientMessageFactory outgoing = new();
    readonly ServerMessageFactory incoming = new();
    readonly string name;
    bool disposed;
    long worldRevision = -1;
    string previous = "";
    public Peer(IPEndPoint endpoint, string name, RSA key)
    {
        this.name = name;
        connection.Start(); var hail = connection.CreateMessage(); hail.Write(""); connection.Connect(endpoint, hail);
        var watch = Stopwatch.StartNew();
        while (connection.ConnectionStatus != NetConnectionStatus.Connected && watch.Elapsed.TotalSeconds < 15) { Drain(); Thread.Sleep(10); }
        if (connection.ConnectionStatus != NetConnectionStatus.Connected) throw new TimeoutException("UDP connection");
        string pub = IdentityProof.ExportPublicKey(key), account = IdentityProof.AccountId(pub);
        var request = outgoing.CreateNewMessageData<HandshakeRequestMsgData>();
        request.PlayerName = name; request.IdentityPublicKey = pub; request.UniqueIdentifier = account; request.KspVersion = "1.12.5";
        request.DeepFreezeProtocolVersion = 1;
        request.ApfEnabled = true; request.ApfClientVersion = request.ApfPackVersion = "kesas-rc53"; request.ApfPackId = "kesas";
        Send(outgoing.CreateNew<HandshakeCliMsg>(request));
        var challenge = Receive<HandshakeIdentityChallengeMsgData>();
        var proof = outgoing.CreateNewMessageData<HandshakeIdentityProofMsgData>(); proof.Signature = IdentityProof.Sign(key, challenge.Nonce, account, name);
        Send(outgoing.CreateNew<HandshakeCliMsg>(proof));
        var reply = Receive<HandshakeReplyMsgData>();
        if (reply.Response == HandshakeReply.ApfPackRequired)
        {
            var report = outgoing.CreateNewMessageData<PackStatusReportMsgData>();
            report.PackId = reply.ApfPackId; report.PackVersion = reply.ApfPackVersion; report.Status = 1;
            report.Detail = "Authorized checkpoint protocol acceptance; synthetic client, not a physical mod compatibility test";
            Send(outgoing.CreateNew<ApfPackCliMsg>(report)); reply = Receive<HandshakeReplyMsgData>();
        }
        if (reply.Response != HandshakeReply.HandshookSuccessfully) throw new InvalidOperationException("Admission: " + reply.Reason);
        var snapshot = outgoing.CreateNewMessageData<AetherfrontCommandMsgData>();
        snapshot.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireRequest { ProtocolVersion = 1, Operation = AetherfrontWireOperation.SnapshotRequest });
        Send(outgoing.CreateNew<AetherfrontCliMsg>(snapshot));
        ReceiveWhere<AetherfrontBaseMsgData>(message =>
        {
            string json = (message as AetherfrontSnapshotMsgData)?.PayloadJson ?? (message as AetherfrontCommandReplyMsgData)?.PayloadJson;
            return json != null && AetherfrontProtocol.DeserializeReply(json).Operation == AetherfrontWireOperation.SnapshotReply;
        });
        Send(outgoing.CreateNew<ScenarioCliMsg>(outgoing.CreateNewMessageData<ScenarioRequestMsgData>()));
        Receive<ScenarioDataMsgData>();
    }
    void Drain() { NetIncomingMessage packet; while ((packet = connection.ReadMessage()) != null) connection.Recycle(packet); }
    void Send(IMessageBase message)
    {
        var buffer = connection.CreateMessage(message.GetMessageSize()); message.Serialize(buffer);
        connection.SendMessage(buffer, message.NetDeliveryMethod, message.Channel); connection.FlushSendQueue(); message.Recycle();
    }
    T Receive<T>() where T : class => ReceiveWhere<T>(_ => true);
    T ReceiveWhere<T>(Func<T, bool> accept) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 20)
        {
            var packet = connection.ReadMessage(); if (packet == null) { Thread.Sleep(10); continue; }
            try
            {
                if (packet.MessageType == NetIncomingMessageType.StatusChanged)
                { if ((NetConnectionStatus)packet.ReadByte() == NetConnectionStatus.Disconnected) throw new IOException("Disconnected: " + packet.ReadString()); continue; }
                if (packet.MessageType != NetIncomingMessageType.Data) continue;
                var message = incoming.Deserialize(packet, DateTime.UtcNow.Ticks) as IServerMessageBase;
                if (message?.Data is DeepFreezeCheckpointMsgData frozen)
                    worldRevision = Math.Max(worldRevision, DeepFreezeCheckpointEnvelope.Decode(frozen.Payload).Revision);
                if (message?.Data is T data && accept(data)) return data;
                message?.Recycle();
            }
            finally { connection.Recycle(packet); }
        }
        throw new TimeoutException("Awaiting " + typeof(T).Name);
    }
    public double Resume()
    {
        Send(outgoing.CreateNew<WarpCliMsg>(outgoing.CreateNewMessageData<WarpSubspacesRequestMsgData>()));
        var reply = Receive<WarpSubspacesReplyMsgData>();
        var space = reply.Subspaces.Take(reply.SubspaceCount).Single(s => s.Players.Take(s.PlayerCount).Contains(name));
        return space.SubspaceTime;
    }
    public JsonElement Checkpoint(int command, string entity = "", long generation = 0, bool disconnect = false)
    {
        var request = new AetherfrontWireRequest { ProtocolVersion = 1, Operation = AetherfrontWireOperation.CommandRequest,
            CommandType = (AetherfrontCommandType)command, CommandId = Guid.NewGuid().ToString("N"),
            EntityId = command == 80 ? previous : entity, Amount = generation };
        var data = outgoing.CreateNewMessageData<AetherfrontCommandMsgData>(); data.PayloadJson = AetherfrontProtocol.Serialize(request);
        Send(outgoing.CreateNew<AetherfrontCliMsg>(data));
        if (disconnect) return default;
        var result = ReceiveWhere<AetherfrontCommandReplyMsgData>(reply => reply.PayloadJson.Contains(request.CommandId, StringComparison.Ordinal));
        var parsed = JsonDocument.Parse(result.PayloadJson).RootElement.Clone();
        if (parsed.TryGetProperty("FlightCheckpoint", out var info)) previous = info.GetProperty("CheckpointId").GetString();
        return parsed;
    }
    public void Proto(Guid vessel, string reason, double ut, int supplies)
    {
        uint partId = BitConverter.ToUInt32(vessel.ToByteArray(), 0) | 1u;
        string text = $"pid = {vessel:N}\nname = RC53 checkpoint acceptance\ntype = Ship\nsit = ORBITING\nlastUT = {ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\nORBIT\n{{\nSMA = 700000\nECC = 0\nREF = 1\nbody = Kerbin\n}}\nPART\n{{\nname = mk1pod\nuid = {partId}\nRESOURCE\n{{\nname = Supplies\namount = {supplies}\nmaxAmount = 10\nflowState = True\n}}\n}}\nACTIONGROUPS\n{{\n}}\nDISCOVERY\n{{\n}}\nFLIGHTPLAN\n{{\n}}\nCTRLSTATE\n{{\n}}\nVESSELMODULES\n{{\n}}\n";
        var data = outgoing.CreateNewMessageData<VesselProtoMsgData>(); data.VesselId = vessel; data.Reason = reason; data.Data = Encoding.UTF8.GetBytes(text); data.NumBytes = data.Data.Length; data.SharedWorldRevision = worldRevision;
        Send(outgoing.CreateNew<VesselCliMsg>(data));
    }
    public bool WaitForRestoreDisconnect()
    {
        try { Receive<AetherfrontCommandReplyMsgData>(); return false; }
        catch (IOException error) { return error.Message.Contains("Loading your personal checkpoint", StringComparison.Ordinal); }
    }
    public string Fetch(Guid vessel)
    {
        var data = outgoing.CreateNewMessageData<VesselSyncMsgData>(); data.VesselsCount = 0;
        Send(outgoing.CreateNew<VesselCliMsg>(data));
        var proto = ReceiveWhere<VesselProtoMsgData>(message => message.VesselId == vessel);
        return Encoding.UTF8.GetString(proto.Data, 0, proto.NumBytes);
    }
    public void Remove(Guid vessel)
    {
        var data = outgoing.CreateNewMessageData<VesselRemoveMsgData>(); data.VesselId = vessel; data.AddToKillList = true; data.Reason = "Completed RC53 acceptance test"; data.SharedWorldRevision = worldRevision;
        Send(outgoing.CreateNew<VesselCliMsg>(data)); Thread.Sleep(500);
    }
    public void Dispose() { if (disposed) return; disposed = true; connection.Shutdown("Checkpoint acceptance complete"); Thread.Sleep(200); }
}
