using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using LmpCommon;
using System.Threading;
using Aetherfront.Core;
using ApfCore;
using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;

namespace ApfProtocolTests
{
    internal static class DeepProtocolScenario
    {
        private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

        public static void SeedLocalReview(string privateKeyPath,string playerName) {
            using(var identity=RSA.Create()) {
                identity.FromXmlString(System.IO.File.ReadAllText(privateKeyPath));
                using(var client=new DeepClient("review",0,28865,"",identity,playerName)) {
                    client.Connect();
                    if(client.Current().FindFactionForAccount(client.AccountId)==null) {
                        var created=client.Execute(new AetherfrontWireRequest {CommandType=AetherfrontCommandType.CreateFaction,
                          Name="Controller Review",Abbreviation="RVW",Description="Disposable local UI review faction",ColorHex="#3377BB",Public=true,RecruitmentPolicy=RecruitmentPolicies.Open});
                        Require(created.Accepted,"local reviewer faction created: "+created.ReasonCode);
                    }
                    Console.WriteLine("Local review identity ready; private key remained in its existing client file and was not exported");
                }
            }
        }

        public static void Run(int masterPort, int serverPort, string password)
        {
            var first = new DeepClient("deep-first", masterPort, serverPort, password);
            var second = new DeepClient("deep-second", masterPort, serverPort, password);
            var third = new DeepClient("deep-third", masterPort, serverPort, password);
            try
            {
                first.Connect();
                second.Connect();
                third.Connect();

                var firstFaction = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.CreateFaction,
                    Name = "Deep First " + DateTime.UtcNow.Ticks,
                    Abbreviation = "DFST",
                    Description = "multi-client protocol scenario",
                    ColorHex = "#2255AA",
                    Public = true,
                    RecruitmentPolicy = RecruitmentPolicies.Open
                });
                Require(firstFaction.Accepted && !string.IsNullOrWhiteSpace(firstFaction.FactionId), "deep faction one create");

                var secondFaction = second.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.CreateFaction,
                    Name = "Deep Second " + DateTime.UtcNow.Ticks,
                    Abbreviation = "DSND",
                    Description = "second fake faction",
                    ColorHex = "#AA5522",
                    Public = true,
                    RecruitmentPolicy = RecruitmentPolicies.Open
                });
                Require(secondFaction.Accepted && !string.IsNullOrWhiteSpace(secondFaction.FactionId), "deep faction two create");

                var joined = third.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.JoinFaction,
                    FactionId = firstFaction.FactionId,
                    DisplayName = third.Name
                });
                Require(joined.Accepted, "deep third account joins first faction");

                var role = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.SetMemberRole,
                    FactionId = firstFaction.FactionId,
                    TargetAccountId = third.AccountId,
                    Role = FactionRoles.Scientist
                });
                Require(role.Accepted, "deep member role assignment");

                var funds = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.TransferFunds,
                    FactionId = firstFaction.FactionId,
                    TargetFactionId = secondFaction.FactionId,
                    Amount = 1000000
                });
                Require(funds.Accepted, "deep faction funds transfer");

                var craftId = "deep-craft-" + Guid.NewGuid().ToString("N");
                var craft = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.RegisterCraft,
                    FactionId = firstFaction.FactionId,
                    EntityId = craftId,
                    Name = "Deep Test Craft",
                    ContentSha256 = new string('a', 64)
                });
                Require(!craft.Accepted, "nonexistent craft cannot be claimed through metadata registration: "+craft.ReasonCode);

                var alliance = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.ProposeAlliance,
                    FactionId = firstFaction.FactionId,
                    TargetFactionId = secondFaction.FactionId
                });
                Require(alliance.Accepted, "deep alliance proposal");
                var allianceState = first.State(alliance);
                var agreement = allianceState.Agreements.LastOrDefault(item =>
                    string.Equals(item.ProposerFactionId, firstFaction.FactionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.RecipientFactionId, secondFaction.FactionId, StringComparison.OrdinalIgnoreCase));
                Require(agreement != null, "deep alliance visible in state");
                var allianceReply = second.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.RespondToAgreement,
                    AgreementId = agreement.Id,
                    Accept = true
                });
                Require(allianceReply.Accepted, "deep alliance acceptance");

                var flagged = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.UpdateFactionProfile,
                    FactionId = firstFaction.FactionId,
                    Name = allianceState.FindFaction(firstFaction.FactionId).Name,
                    Abbreviation = "DFST",
                    Description = "flag upload path tested",
                    ColorHex = "#2255AA",
                    Public = true,
                    RecruitmentPolicy = RecruitmentPolicies.Open,
                    FlagDataBase64 = TinyPng
                });
                Require(flagged.Accepted && !string.IsNullOrWhiteSpace(first.State(flagged).FindFaction(firstFaction.FactionId).FlagAssetId),
                    "deep PNG flag upload and persistence");

                var trade = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.CreateTradeOffer,
                    FactionId = firstFaction.FactionId,
                    TargetFactionId = secondFaction.FactionId,
                    AssetJson = AetherfrontJson.Serialize(new List<TradeAsset> { new TradeAsset { Type = TradeAssetTypes.FundsMicro, Amount = 1000000 } }),
                    CounterAssetJson = "[]"
                });
                Require(trade.Accepted && !string.IsNullOrWhiteSpace(trade.TradeId), "deep trade offer");
                var recipientConfirm = second.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.ConfirmTrade,
                    TradeId = trade.TradeId
                });
                Require(recipientConfirm.Accepted, "deep recipient trade confirmation");
                var proposerConfirm = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.ConfirmTrade,
                    TradeId = trade.TradeId
                });
                Require(proposerConfirm.Accepted && string.Equals(first.State(proposerConfirm).FindTrade(trade.TradeId).State, TradeStates.Committed, StringComparison.OrdinalIgnoreCase),
                    "deep trade settlement");

                var unauthorized = third.Execute(new AetherfrontWireRequest {
                    CommandType=AetherfrontCommandType.SetMemberRole,
                    FactionId=firstFaction.FactionId,TargetAccountId=third.AccountId,Role=FactionRoles.Founder
                });
                Require(!unauthorized.Accepted,"ordinary member cannot escalate to founder over network: "+unauthorized.ReasonCode);
                var spoof=first.ExecuteAs(new AetherfrontWireRequest {
                    CommandType=AetherfrontCommandType.TransferFunds,FactionId=secondFaction.FactionId,
                    TargetFactionId=firstFaction.FactionId,Amount=1
                },second.AccountId);
                Require(!spoof.Accepted,"authenticated client cannot impersonate another account: "+spoof.ReasonCode);
                var beforeReplay=first.Current().FindFaction(firstFaction.FactionId).Progression.FundsMicro;
                var transfer=first.Execute(new AetherfrontWireRequest {
                    CommandType=AetherfrontCommandType.TransferFunds,FactionId=firstFaction.FactionId,
                    TargetFactionId=secondFaction.FactionId,Amount=1
                });
                Require(transfer.Accepted,"network replay fixture transfer accepted");
                var replay=first.ReplayLast();
                Require(replay.Accepted&&first.Current().FindFaction(firstFaction.FactionId).Progression.FundsMicro==beforeReplay-1,
                    "network command replay never transfers funds twice");
                var malformed=first.SendMalformed();
                Require(malformed!=null&&!malformed.Accepted,"malformed JSON command rejected without server crash: "+malformed?.ReasonCode);
                Require(second.Current()!=null,"other connected client remains responsive after malformed request");

                var story = first.Execute(new AetherfrontWireRequest
                {
                    CommandType = AetherfrontCommandType.PublishStory,
                    FactionId = firstFaction.FactionId,
                    Title = "Deep scenario story",
                    Body = "Three fake clients completed a controlled protocol story.",
                    Public = true,
                    TagsJson = AetherfrontJson.Serialize(new List<string> { "deep", "test" })
                });
                Require(!story.Accepted, "removed story command is rejected: "+story.ReasonCode);
                Console.WriteLine("SKIPPED research contribution: no science was earned by these synthetic clients; domain suite verifies this separately");

                Console.WriteLine("APF deep multi-client scenario passed: factions, membership, roles, funds, phantom craft denial, alliance, flag, trade, removed story denial");
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                third.Dispose();
            }
        }

        private static void Require(bool condition, string test)
        {
            if (!condition) throw new InvalidOperationException("Failed: " + test); Console.WriteLine("PASS "+test);
        }

        private sealed class DeepClient : IDisposable
        {
            private readonly int _masterPort;
            private readonly int _serverPort;
            private readonly string _password;
            private readonly NetClient _client;
            private readonly RSA _identity = RSA.Create(2048);
            private readonly ClientMessageFactory _clientFactory = new ClientMessageFactory();
            private readonly ServerMessageFactory _serverFactory = new ServerMessageFactory();

            public string Name { get; }
            public string AccountId { get; private set; }

            public DeepClient(string prefix, int masterPort, int serverPort, string password, RSA suppliedIdentity=null, string exactName=null)
            {
                Name = exactName ?? (prefix.Substring(0, Math.Min(5, prefix.Length)) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                if(suppliedIdentity!=null){_identity.Dispose();_identity=suppliedIdentity;}
                _masterPort = masterPort;
                _serverPort = serverPort;
                _password = password;
                _client = new NetClient(new NetPeerConfiguration("LMP"));
            }

            public void Connect()
            {
                _client.Start();
                var password = _client.CreateMessage(16);
                password.Write(_password);
                _client.Connect(new IPEndPoint(IPAddress.Loopback, _serverPort), password);
                Require(WaitFor(message => message.MessageType == NetIncomingMessageType.StatusChanged &&
                    message.ReadByte() == (byte)NetConnectionStatus.Connected, 10000) != null, Name + " connected");

                AccountId = IdentityProof.AccountId(IdentityProof.ExportPublicKey(_identity));
                var handshake = _clientFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = Name;
                handshake.UniqueIdentifier = AccountId;
                handshake.IdentityPublicKey = IdentityProof.ExportPublicKey(_identity);
                handshake.KspVersion = "1.12.5";
                handshake.ApfEnabled = true;
                handshake.ApfClientVersion = "kesas-rc29";
                Send(_clientFactory.CreateNew<HandshakeCliMsg>(handshake));
                var initial = ReceiveLmp(10000);
                if(initial?.Data is HandshakeIdentityChallengeMsgData challenge) {
                    var proof=_clientFactory.CreateNewMessageData<HandshakeIdentityProofMsgData>();
                    proof.Signature=IdentityProof.Sign(_identity,challenge.Nonce,AccountId,Name);
                    Send(_clientFactory.CreateNew<HandshakeCliMsg>(proof));
                    initial=ReceiveLmp(10000);
                }
                Require(initial?.Data is HandshakeReplyMsgData, Name + " handshake reply");
                var reply = (HandshakeReplyMsgData)initial.Data;
                if (reply.Response == HandshakeReply.ApfPackRequired)
                {
                    var manifest = _clientFactory.CreateNewMessageData<PackManifestRequestMsgData>();
                    manifest.ServerId = reply.ApfServerId;
                    manifest.PackId = reply.ApfPackId;
                    manifest.PackVersion = reply.ApfPackVersion;
                    manifest.ClientVersion = "kesas-rc29";
                    Send(_clientFactory.CreateNew<ApfPackCliMsg>(manifest));
                    var manifestReply = ReceiveLmp(10000);
                    Require(manifestReply?.Data is PackManifestReplyMsgData, Name + " manifest reply");

                    var status = _clientFactory.CreateNewMessageData<PackStatusReportMsgData>();
                    status.PackId = ((PackManifestReplyMsgData)manifestReply.Data).PackId;
                    status.PackVersion = ((PackManifestReplyMsgData)manifestReply.Data).PackVersion;
                    status.Status = (int)ApfPackStatus.Clean;
                    status.Detail = "deep protocol GameData clean";
                    Send(_clientFactory.CreateNew<ApfPackCliMsg>(status));
                    var final = ReceiveLmp(10000);
                    Require(final?.Data is HandshakeReplyMsgData && ((HandshakeReplyMsgData)final.Data).Response == HandshakeReply.HandshookSuccessfully,
                        Name + " clean APF handshake");
                }
                else Require(reply.Response == HandshakeReply.HandshookSuccessfully, Name + " accepted handshake");
            }

            private AetherfrontWireRequest _lastRequest;
            public AetherfrontState Current(){return Snapshot();}
            public AetherfrontWireReply ReplayLast(){SendAetherfront(_lastRequest);return ReceiveWire(10000);}
            public AetherfrontWireReply SendMalformed(){var data=_clientFactory.CreateNewMessageData<AetherfrontCommandMsgData>();data.PayloadJson="{";Send(_clientFactory.CreateNew<AetherfrontCliMsg>(data));return ReceiveWire(10000);}
            public AetherfrontWireReply ExecuteAs(AetherfrontWireRequest request,string account){
                var state=Snapshot();request.Operation=AetherfrontWireOperation.CommandRequest;request.ProtocolVersion=AetherfrontConstants.CurrentSchemaVersion;
                request.CommandId="spoof-"+Guid.NewGuid().ToString("N");request.AccountId=account;request.ExpectedRevision=state.Revision;
                SendAetherfront(request);return ReceiveWire(10000);
            }
            public AetherfrontWireReply Execute(AetherfrontWireRequest request)
            {
                var state = Snapshot();
                request.Operation = AetherfrontWireOperation.CommandRequest;
                request.ProtocolVersion = AetherfrontConstants.CurrentSchemaVersion;
                request.CommandId = "deep-" + Guid.NewGuid().ToString("N");
                request.AccountId = AccountId;
                request.ExpectedRevision = state.Revision;
                _lastRequest=request;
                SendAetherfront(request);
                return ReceiveWire(10000);
            }

            public AetherfrontState State(AetherfrontWireReply reply)
            {
                return AetherfrontJson.Deserialize<AetherfrontState>(reply?.StateJson ?? string.Empty);
            }

            private AetherfrontState Snapshot()
            {
                SendAetherfront(new AetherfrontWireRequest
                {
                    Operation = AetherfrontWireOperation.SnapshotRequest,
                    ProtocolVersion = AetherfrontConstants.CurrentSchemaVersion
                });
                var reply = ReceiveWire(10000);
                Require(reply != null && reply.Accepted, Name + " snapshot");
                return State(reply);
            }

            private void SendAetherfront(AetherfrontWireRequest request)
            {
                var data = _clientFactory.CreateNewMessageData<AetherfrontCommandMsgData>();
                data.PayloadJson = AetherfrontProtocol.Serialize(request);
                Send(_clientFactory.CreateNew<AetherfrontCliMsg>(data));
            }

            private AetherfrontWireReply ReceiveWire(int timeoutMs)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < timeoutMs)
                {
                    var incoming = _client.ReadMessage();
                    if (incoming == null) { Thread.Sleep(20); continue; }
                    try
                    {
                        if (incoming.MessageType != NetIncomingMessageType.Data) continue;
                        var decoded = _serverFactory.Deserialize(incoming, DateTime.UtcNow.Ticks) as IServerMessageBase;
                        var data = decoded?.Data as AetherfrontBaseMsgData;
                        var payload = (data as AetherfrontCommandReplyMsgData)?.PayloadJson ??
                            (data as AetherfrontSnapshotMsgData)?.PayloadJson ??
                            (data as AetherfrontEventNoticeMsgData)?.PayloadJson;
                        if (string.IsNullOrWhiteSpace(payload)) continue;
                        var reply = AetherfrontProtocol.DeserializeReply(payload);
                        if (reply != null && (reply.Operation == AetherfrontWireOperation.CommandReply ||
                            reply.Operation == AetherfrontWireOperation.SnapshotReply)) return reply;
                    }
                    finally { _client.Recycle(incoming); }
                }
                throw new TimeoutException(Name + " Aetherfront reply timeout");
            }

            private IServerMessageBase ReceiveLmp(int timeoutMs)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < timeoutMs)
                {
                    var incoming = _client.ReadMessage();
                    if (incoming == null) { Thread.Sleep(20); continue; }
                    try
                    {
                        if (incoming.MessageType != NetIncomingMessageType.Data) continue;
                        return _serverFactory.Deserialize(incoming, DateTime.UtcNow.Ticks) as IServerMessageBase;
                    }
                    finally { _client.Recycle(incoming); }
                }
                return null;
            }

            private NetIncomingMessage WaitFor(Func<NetIncomingMessage, bool> predicate, int timeoutMs)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < timeoutMs)
                {
                    var incoming = _client.ReadMessage();
                    if (incoming == null) { Thread.Sleep(20); continue; }
                    if (predicate(incoming)) return incoming;
                    _client.Recycle(incoming);
                }
                return null;
            }

            private void Send(IMessageBase message)
            {
                var outgoing = _client.CreateMessage(message.GetMessageSize());
                message.Serialize(outgoing);
                _client.SendMessage(outgoing, message.NetDeliveryMethod, message.Channel);
                _client.FlushSendQueue();
                message.Recycle();
            }

            public void Dispose()
            {
                try { _client.Disconnect("deep scenario complete"); } catch { }
                _client.Shutdown("deep scenario complete");
                _identity.Dispose();
            }
        }
    }
}
