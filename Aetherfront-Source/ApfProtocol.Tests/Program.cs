using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using ApfCore;
using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.MasterServer;
using LmpCommon.Message.Interface;
using LmpCommon.Message.MasterServer;
using LmpCommon.Message.Server;
using LmpCommon.Message.Data.Aetherfront;
using Aetherfront.Core;

namespace ApfProtocolTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var masterPort = ReadPort(args, "--master-port", 18700);
            var serverPort = ReadPort(args, "--server-port", 18800);
            var serverPassword = ReadValue(args, "--password", "123");
            var clientVersion = ReadValue(args, "--client-version", "apf-client-1");
            var client = new NetClient(new NetPeerConfiguration("LMP"));
            var masterProbeConfiguration = new NetPeerConfiguration("LMP");
            masterProbeConfiguration.EnableMessageType(NetIncomingMessageType.UnconnectedData);
            var masterProbe = new NetClient(masterProbeConfiguration);
            try
            {
                client.Start();
                masterProbe.Start();
                ProbePrivateMasterIngress(masterProbe, masterPort);
                var password = client.CreateMessage(16);
                password.Write(serverPassword);
                client.Connect(new IPEndPoint(IPAddress.Loopback, serverPort), password);
                var connected = WaitFor(client, message => message.MessageType == NetIncomingMessageType.StatusChanged &&
                    message.ReadByte() == (byte)NetConnectionStatus.Connected, 10000);
                Require(connected != null, "client connected");

                var clientFactory = new ClientMessageFactory();
                var serverFactory = new ServerMessageFactory();
                var handshake = clientFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.PlayerName = "APF_smoke_" + handshake.UniqueIdentifier.Substring(0, 8);
                handshake.KspVersion = "1.12.5";
                handshake.ApfEnabled = true;
                handshake.ApfClientVersion = clientVersion;
                handshake.ApfPackId = "";
                handshake.ApfPackVersion = "";
                Send(client, clientFactory.CreateNew<HandshakeCliMsg>(handshake));

                var firstReply = ReceiveServerMessage(client, serverFactory, 10000);
                Require(firstReply?.Data is HandshakeReplyMsgData && ((HandshakeReplyMsgData)firstReply.Data).Response == HandshakeReply.ApfPackRequired,
                    "server gates handshake on APF pack status");
                var gate = (HandshakeReplyMsgData)firstReply.Data;

                var staleRequest = clientFactory.CreateNewMessageData<PackManifestRequestMsgData>();
                staleRequest.ServerId = gate.ApfServerId;
                staleRequest.PackId = "stale-pack";
                staleRequest.PackVersion = gate.ApfPackVersion;
                staleRequest.ClientVersion = clientVersion;
                Send(client, clientFactory.CreateNew<ApfPackCliMsg>(staleRequest));
                var staleReply = ReceiveServerMessage(client, serverFactory, 10000);
                Require(staleReply?.Data is PackSyncRequiredMsgData &&
                    ((PackSyncRequiredMsgData)staleReply.Data).Reason.Contains("does not match"),
                    "stale APF manifest request is rejected with sync-required metadata");

                var manifestRequest = clientFactory.CreateNewMessageData<PackManifestRequestMsgData>();
                manifestRequest.ServerId = gate.ApfServerId;
                manifestRequest.PackId = gate.ApfPackId;
                manifestRequest.PackVersion = gate.ApfPackVersion;
                manifestRequest.ClientVersion = clientVersion;
                Send(client, clientFactory.CreateNew<ApfPackCliMsg>(manifestRequest));

                var manifestReply = ReceiveServerMessage(client, serverFactory, 10000);
                Require(manifestReply?.Data is PackManifestReplyMsgData, "server replies with APF manifest metadata");
                var manifest = (PackManifestReplyMsgData)manifestReply.Data;
                Require(manifest.ManifestUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "manifest URL is HTTPS");

                var status = clientFactory.CreateNewMessageData<PackStatusReportMsgData>();
                status.PackId = manifest.PackId;
                status.PackVersion = manifest.PackVersion;
                status.Status = (int)ApfPackStatus.SyncRequired;
                status.Detail = "synthetic GameData mismatch";
                Send(client, clientFactory.CreateNew<ApfPackCliMsg>(status));

                var blocked = ReceiveServerMessage(client, serverFactory, 10000);
                Require(blocked?.Data is PackSyncRequiredMsgData &&
                    ((PackSyncRequiredMsgData)blocked.Data).PackId == manifest.PackId,
                    "non-clean APF status emits an explicit sync-required message");

                status.Status = (int)ApfPackStatus.Clean;
                status.Detail = "synthetic GameData scan clean";
                Send(client, clientFactory.CreateNew<ApfPackCliMsg>(status));

                var success = ReceiveServerMessage(client, serverFactory, 10000);
                Require(success?.Data is HandshakeReplyMsgData && ((HandshakeReplyMsgData)success.Data).Response == HandshakeReply.HandshookSuccessfully,
                    "clean APF status unlocks normal handshake");

                SmokeAetherfrontCommands(client, clientFactory, serverFactory, handshake.UniqueIdentifier);

                if (Array.Exists(args ?? new string[0], value => string.Equals(value, "--deep", StringComparison.OrdinalIgnoreCase)))
                    DeepProtocolScenario.Run(masterPort, serverPort, serverPassword);

                client.Disconnect("protocol smoke complete");
                Console.WriteLine("APF protocol smoke passed");
                return 0;
            }
            finally
            {
                client.Shutdown("protocol smoke complete");
                masterProbe.Shutdown("protocol smoke complete");
            }
        }

        private static void SmokeAetherfrontCommands(NetClient client, ClientMessageFactory clientFactory,
            ServerMessageFactory serverFactory, string accountId)
        {
            var snapshotRequest = new AetherfrontWireRequest
            {
                Operation = AetherfrontWireOperation.SnapshotRequest,
                ProtocolVersion = AetherfrontConstants.CurrentSchemaVersion
            };
            SendAetherfront(client, clientFactory, snapshotRequest);
            var snapshot = ReceiveAetherfrontReply(client, serverFactory, 10000);
            Require(snapshot != null && snapshot.Accepted, "Aetherfront snapshot reply");

            var command = new AetherfrontWireRequest
            {
                Operation = AetherfrontWireOperation.CommandRequest,
                ProtocolVersion = AetherfrontConstants.CurrentSchemaVersion,
                CommandType = AetherfrontCommandType.CreateFaction,
                CommandId = "protocol-faction-" + Guid.NewGuid().ToString("N"),
                AccountId = accountId,
                ExpectedRevision = snapshot.Revision,
                Name = "Protocol Faction " + DateTime.UtcNow.Ticks,
                Abbreviation = "PROTO",
                Description = "protocol smoke",
                ColorHex = "#123456",
                Public = true,
                RecruitmentPolicy = RecruitmentPolicies.Open
            };
            SendAetherfront(client, clientFactory, command);
            var created = ReceiveAetherfrontReply(client, serverFactory, 10000);
            Require(created != null && created.Accepted && !string.IsNullOrWhiteSpace(created.FactionId),
                "Aetherfront faction command accepted");

            SendAetherfront(client, clientFactory, command);
            var replay = ReceiveAetherfrontReply(client, serverFactory, 10000);
            Require(replay != null && replay.Accepted && replay.Revision == created.Revision,
                "duplicate Aetherfront command is idempotent");

            command.CommandId += "-stale";
            command.ExpectedRevision = snapshot.Revision;
            SendAetherfront(client, clientFactory, command);
            var stale = ReceiveAetherfrontReply(client, serverFactory, 10000);
            Require(stale != null && !stale.Accepted && stale.ReasonCode == "StaleAetherfrontRevision",
                "stale Aetherfront revision is rejected");
        }

        private static void SendAetherfront(NetClient client, ClientMessageFactory factory, AetherfrontWireRequest request)
        {
            var data = factory.CreateNewMessageData<AetherfrontCommandMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(request);
            Send(client, factory.CreateNew<AetherfrontCliMsg>(data));
        }

        private static AetherfrontWireReply ReceiveAetherfrontReply(NetClient client, ServerMessageFactory factory, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var incoming = client.ReadMessage();
                if (incoming == null)
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    if (incoming.MessageType != NetIncomingMessageType.Data) continue;
                    var decoded = factory.Deserialize(incoming, DateTime.UtcNow.Ticks) as IServerMessageBase;
                    if (!(decoded?.Data is AetherfrontBaseMsgData data)) continue;
                    var payload = (data as AetherfrontCommandReplyMsgData)?.PayloadJson ??
                        (data as AetherfrontSnapshotMsgData)?.PayloadJson;
                    if (string.IsNullOrWhiteSpace(payload)) continue;
                    var reply = AetherfrontProtocol.DeserializeReply(payload);
                    if (reply != null && (reply.Operation == AetherfrontWireOperation.CommandReply ||
                        reply.Operation == AetherfrontWireOperation.SnapshotReply)) return reply;
                }
                finally { client.Recycle(incoming); }
            }
            return null;
        }

        private static void ProbePrivateMasterIngress(NetClient probe, int masterPort)
        {
            var factory = new MasterServerMessageFactory();
            var fakeId = DateTime.UtcNow.Ticks;
            var registration = factory.CreateNewMessageData<MsRegisterServerMsgData>();
            registration.Id = fakeId;
            registration.ServerVersion = "0.29.2";
            registration.InternalEndpoint = new IPEndPoint(IPAddress.Loopback, 19999);
            registration.InternalEndpoint6 = new IPEndPoint(IPAddress.IPv6Loopback, 19999);
            registration.Password = false;
            registration.Cheats = false;
            registration.ModControl = true;
            registration.GameMode = 0;
            registration.MaxPlayers = 1;
            registration.PlayerCount = 0;
            registration.ServerName = "public-injection";
            registration.Description = "must not enter APF directory";
            registration.CountryCode = "";
            registration.Website = "";
            registration.WebsiteText = "";
            registration.Color = new byte[] { 1, 2, 3 };
            registration.ApfEnabled = false;
            SendMaster(probe, factory.CreateNew<MainMstSrvMsg>(registration), new IPEndPoint(IPAddress.Loopback, masterPort));

            var request = factory.CreateNewMessageData<MsRequestServersMsgData>();
            SendMaster(probe, factory.CreateNew<MainMstSrvMsg>(request), new IPEndPoint(IPAddress.Loopback, masterPort));

            var deadline = Stopwatch.StartNew();
            var sawList = false;
            while (deadline.ElapsedMilliseconds < 10000)
            {
                var incoming = probe.ReadMessage();
                if (incoming == null)
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    if (incoming.MessageType != NetIncomingMessageType.UnconnectedData) continue;
                    var decoded = factory.Deserialize(incoming, DateTime.UtcNow.Ticks) as MainMstSrvMsg;
                    if (!(decoded?.Data is MsReplyServersMsgData entry)) continue;
                    sawList = true;
                    Require(entry.Id != fakeId, "private APF master rejects non-APF registration at ingress");
                }
                finally { probe.Recycle(incoming); }
            }
            Require(sawList, "private APF master answered directory probe");
        }

        private static int ReadPort(string[] args, string name, int fallback)
        {
            for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
            {
                if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(args[index + 1], out var port) && port > 0 && port <= 65535) return port;
                throw new ArgumentException("Invalid port for " + name);
            }
            return fallback;
        }

        private static string ReadValue(string[] args, string name, string fallback)
        {
            for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1] ?? fallback;
                }
            }
            return fallback;
        }

        private static void SendMaster(NetClient client, IMessageBase message, IPEndPoint endpoint)
        {
            var outgoing = client.CreateMessage(message.GetMessageSize());
            message.Serialize(outgoing);
            client.SendUnconnectedMessage(outgoing, endpoint);
            client.FlushSendQueue();
            message.Recycle();
        }

        private static void Send(NetClient client, IMessageBase message)
        {
            var outgoing = client.CreateMessage(message.GetMessageSize());
            message.Serialize(outgoing);
            client.SendMessage(outgoing, message.NetDeliveryMethod, message.Channel);
            client.FlushSendQueue();
            message.Recycle();
        }

        private static IServerMessageBase ReceiveServerMessage(NetClient client, ServerMessageFactory factory, int timeoutMs)
        {
            var incoming = WaitFor(client, message => message.MessageType == NetIncomingMessageType.Data, timeoutMs);
            if (incoming == null) return null;
            try { return factory.Deserialize(incoming, DateTime.UtcNow.Ticks) as IServerMessageBase; }
            finally { client.Recycle(incoming); }
        }

        private static NetIncomingMessage WaitFor(NetClient client, Func<NetIncomingMessage, bool> predicate, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var message = client.ReadMessage();
                if (message == null)
                {
                    Thread.Sleep(20);
                    continue;
                }
                if (predicate(message)) return message;
                client.Recycle(message);
            }
            return null;
        }

        private static void Require(bool condition, string test)
        {
            if (!condition) throw new InvalidOperationException("Failed: " + test);
        }
    }
}
