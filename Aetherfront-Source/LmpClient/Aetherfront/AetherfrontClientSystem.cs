using Aetherfront.Core;
using LmpClient.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LmpClient.Aetherfront
{
    public static class AetherfrontClientSystem
    {
        private static readonly object Gate = new object();
        private static DateTime _lastSnapshotRequestUtc = DateTime.MinValue;
        private static bool _snapshotRequested;
        private static int _progressionApplyPending;
        private static readonly Dictionary<string, string> FlagData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RequestedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static AetherfrontState State { get; private set; } = new AetherfrontState();
        public static AetherfrontVesselInterestSnapshot VesselInterest { get; private set; } = new AetherfrontVesselInterestSnapshot();
        public static AetherfrontWireReply LastReply { get; private set; }
        public static string Status { get; private set; } = "Not connected";
        public static bool Ready => MainSystem.NetworkState >= ClientState.Running && State != null && !string.IsNullOrEmpty(State.ServerId);

        public static bool TryGetFlagData(string assetId, out string dataBase64)
        {
            lock (Gate) return FlagData.TryGetValue(assetId ?? string.Empty, out dataBase64);
        }

        public static void RequestFlag(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId) || MainSystem.NetworkState < ClientState.Running) return;
            lock (Gate)
            {
                if (FlagData.ContainsKey(assetId) || !RequestedFlags.Add(assetId)) return;
            }
            SendCommand(new AetherfrontWireRequest
            {
                CommandType = AetherfrontCommandType.RequestFlagAsset,
                FlagAssetId = assetId
            });
        }

        public static void Update()
        {
            if (MainSystem.NetworkState < ClientState.Running)
            {
                lock (Gate) _snapshotRequested = false;
                Interlocked.Exchange(ref _progressionApplyPending, 0);
                return;
            }
            if (!_snapshotRequested && (DateTime.UtcNow - _lastSnapshotRequestUtc).TotalSeconds >= 2)
                RequestSnapshot();

            // NetworkReceiver applies wire state off-thread. Mirror only from
            // this Unity-thread tick so KSP objects are never touched by the
            // Lidgren receive thread.
            if (Interlocked.Exchange(ref _progressionApplyPending, 0) == 1)
                ApplyAuthoritativeProgression();
        }

        public static void RequestSnapshot()
        {
            if (MainSystem.NetworkState < ClientState.Connected) return;
            lock (Gate)
            {
                if (_snapshotRequested && (DateTime.UtcNow - _lastSnapshotRequestUtc).TotalSeconds < 2) return;
                _snapshotRequested = true;
                _lastSnapshotRequestUtc = DateTime.UtcNow;
            }
            Send(new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest });
        }

        public static void SendCommand(AetherfrontWireRequest request)
        {
            if (request == null || MainSystem.NetworkState < ClientState.Running)
            {
                Status = "Aetherfront is not connected";
                return;
            }
            request.Operation = AetherfrontWireOperation.CommandRequest;
            request.ProtocolVersion = AetherfrontConstants.CurrentSchemaVersion;
            request.CommandId = string.IsNullOrWhiteSpace(request.CommandId) ? Guid.NewGuid().ToString("N") : request.CommandId;
            request.AccountId = MainSystem.UniqueIdentifier ?? string.Empty;
            if (request.ExpectedRevision < 0)
                request.ExpectedRevision = State?.Revision ?? 0;
            Send(request);
        }

        public static void HandleMessage(IServerMessageBase message)
        {
            try
            {
                var data = message.Data as AetherfrontBaseMsgData;
                if (data == null) return;
                var payload = string.Empty;
                if (data is AetherfrontSnapshotMsgData snapshot) payload = snapshot.PayloadJson;
                else if (data is AetherfrontCommandReplyMsgData reply) payload = reply.PayloadJson;
                else if (data is AetherfrontEventNoticeMsgData notice) payload = notice.PayloadJson;
                if (string.IsNullOrWhiteSpace(payload)) return;

                var wire = AetherfrontProtocol.DeserializeReply(payload);
                if (wire == null) return;
                if (wire.Operation == AetherfrontWireOperation.EventNotice)
                {
                    Status = "Faction activity updated";
                    lock (Gate) _snapshotRequested = false;
                    RequestSnapshot();
                }
                else
                {
                    ApplyReply(wire);
                }
            }
            catch (Exception e)
            {
                Status = "Aetherfront response error";
                LunaLog.LogError("[Aetherfront] Response handling failed: " + e);
            }
            finally
            {
                message.Recycle();
            }
        }

        private static void ApplyReply(AetherfrontWireReply reply)
        {
            LastReply = reply;
            if (!string.IsNullOrWhiteSpace(reply.StateJson))
            {
                var next = AetherfrontJson.Deserialize<AetherfrontState>(reply.StateJson);
                if (next != null) State = next;
            }
            if (!string.IsNullOrWhiteSpace(reply.InterestJson))
            {
                var interest = AetherfrontJson.Deserialize<AetherfrontVesselInterestSnapshot>(reply.InterestJson);
                if (interest != null) VesselInterest = interest;
            }
            Interlocked.Exchange(ref _progressionApplyPending, 1);
            if (!string.IsNullOrWhiteSpace(reply.FlagAssetId))
            {
                lock (Gate)
                {
                    RequestedFlags.Remove(reply.FlagAssetId);
                    if (reply.Accepted && !string.IsNullOrWhiteSpace(reply.FlagDataBase64))
                        FlagData[reply.FlagAssetId] = reply.FlagDataBase64;
                }
            }
            lock (Gate) _snapshotRequested = false;
            Status = reply.Accepted ? "Ready" : "Rejected: " + reply.ReasonCode;
        }

        private static void ApplyAuthoritativeProgression()
        {
            if (!LmpClient.Apf.ApfClientSettings.Enabled || State == null || MainSystem.NetworkState < ClientState.Running)
                return;

            var faction = State.FindFactionForAccount(MainSystem.UniqueIdentifier ?? string.Empty);
            if (faction == null || faction.Progression == null)
                return;

            try
            {
                if (ResearchAndDevelopment.Instance != null)
                {
                    var science = LmpClient.Systems.ShareScience.ShareScienceSystem.Singleton;
                    science.StartIgnoringEvents();
                    ResearchAndDevelopment.Instance.SetScience(faction.Progression.Science, TransactionReasons.None);
                    science.StopIgnoringEvents();

                    var technology = LmpClient.Systems.ShareTechnology.ShareTechnologySystem.Singleton;
                    technology.StartIgnoringEvents();
                    foreach (var technologyId in faction.Progression.TechnologyUnlocks ?? new List<string>())
                    {
                        var node = AssetBase.RnDTechTree.GetTreeTechs().ToList().Find(t =>
                            string.Equals(t.techID, technologyId, StringComparison.OrdinalIgnoreCase));
                        if (node != null)
                            ResearchAndDevelopment.Instance.UnlockProtoTechNode(node);
                    }
                    technology.StopIgnoringEvents();
                    ResearchAndDevelopment.RefreshTechTreeUI();
                }

                if (Funding.Instance != null)
                {
                    var funds = LmpClient.Systems.ShareFunds.ShareFundsSystem.Singleton;
                    funds.StartIgnoringEvents();
                    Funding.Instance.SetFunds(faction.Progression.FundsMicro / (double)AetherfrontConstants.FundsMicroPerKspFund,
                        TransactionReasons.None);
                    funds.StopIgnoringEvents();
                }

                if (Reputation.Instance != null)
                {
                    var reputation = LmpClient.Systems.ShareReputation.ShareReputationSystem.Singleton;
                    reputation.StartIgnoringEvents();
                    Reputation.Instance.SetReputation(faction.Progression.Reputation, TransactionReasons.None);
                    reputation.StopIgnoringEvents();
                }
            }
            catch (Exception exception)
            {
                LunaLog.LogError("[Aetherfront] Authoritative progression mirror failed: " + exception);
            }
        }

        private static void Send(AetherfrontWireRequest request)
        {
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AetherfrontCommandMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(request);
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AetherfrontCliMsg>(data));
        }
    }
}
