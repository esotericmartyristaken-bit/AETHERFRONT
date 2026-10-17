using Aetherfront.Core;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Message.Base;
using Server.Server;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Message
{
    public sealed class AetherfrontMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            StagedFlagAsset stagedFlag = null;
            try
            {
                if (!client.Authenticated)
                {
                    ClientConnectionHandler.DisconnectClient(client, "Aetherfront requires an authenticated LMP session");
                    return;
                }
                var boundAccountId = AetherfrontServerSystem.GetBoundAccountId(client);
                if (string.IsNullOrWhiteSpace(boundAccountId))
                {
                    ClientConnectionHandler.DisconnectClient(client, "Aetherfront account session is not bound");
                    return;
                }

                var data = message.Data as AetherfrontCommandMsgData;
                if (data == null || string.IsNullOrWhiteSpace(data.PayloadJson) || data.PayloadJson.Length > 1024 * 1024)
                {
                    SendError(client, "InvalidAetherfrontPayload");
                    return;
                }

                var request = AetherfrontProtocol.DeserializeRequest(data.PayloadJson);
                if (request == null || request.ProtocolVersion != AetherfrontConstants.CurrentSchemaVersion)
                {
                    SendError(client, "UnsupportedAetherfrontProtocol");
                    return;
                }

                if (request.Operation == AetherfrontWireOperation.SnapshotRequest)
                {
                    SendSnapshot(client);
                    return;
                }
                if (request.Operation != AetherfrontWireOperation.CommandRequest)
                {
                    SendError(client, "InvalidAetherfrontOperation");
                    return;
                }

                if (request.CommandType == AetherfrontCommandType.RequestFlagAsset)
                {
                    SendFlagAsset(client, request.FlagAssetId);
                    return;
                }

                var currentState = AetherfrontServerSystem.Snapshot();
                var replay = currentState.CommandReceipts.Any(receipt =>
                    string.Equals(receipt.CommandId, request.CommandId, StringComparison.Ordinal));
                if (request.ExpectedRevision < 0 && !replay)
                {
                    SendError(client, "ExpectedRevisionRequired");
                    return;
                }
                if (!replay && request.ExpectedRevision != currentState.Revision)
                {
                    SendError(client, "StaleAetherfrontRevision");
                    return;
                }

                stagedFlag = AetherfrontFlagAssetStore.Stage(request, AetherfrontServerSystem.Store.RootPath);

                var result = AetherfrontServerSystem.Execute(state => Apply(state, request, boundAccountId));
                if (result.Accepted)
                    AetherfrontFlagAssetStore.Commit(stagedFlag, AetherfrontServerSystem.Store.RootPath);
                else
                    AetherfrontFlagAssetStore.Discard(stagedFlag);
                stagedFlag = null;
                var objectiveEvents = AetherfrontGameplayBridge.RecordCommandOutcome(request, result, boundAccountId);
                var eventsForWire = new List<AetherfrontEvent>(result.Events ?? new List<AetherfrontEvent>());
                eventsForWire.AddRange(objectiveEvents);

                var reply = new AetherfrontWireReply
                {
                    Operation = AetherfrontWireOperation.CommandReply,
                    Accepted = result.Accepted,
                    ReasonCode = result.ReasonCode,
                    Revision = result.Revision,
                    FactionId = result.FactionId,
                    TradeId = result.TradeId,
                    ServerId = AetherfrontServerSystem.Snapshot().ServerId,
                    StateJson = AetherfrontJson.Serialize(AetherfrontVisibility.ForAccount(AetherfrontServerSystem.Snapshot(), boundAccountId)),
                    EventsJson = AetherfrontJson.Serialize(eventsForWire),
                    InterestJson = AetherfrontVesselInterest.ForClient(AetherfrontServerSystem.Snapshot(), boundAccountId, client.Subspace)
                };
                SendReply(client, reply);
                if (result.Accepted && eventsForWire.Count > 0)
                    BroadcastEvents(eventsForWire);
            }
            catch (Exception e)
            {
                AetherfrontFlagAssetStore.Discard(stagedFlag);
                LunaLog.Error($"Aetherfront command failed for {client?.PlayerName}: {e}");
                SendError(client, "AetherfrontCommandFailed");
            }
            finally
            {
                message.Recycle();
            }
        }

        private static AetherfrontResult Apply(AetherfrontState state, AetherfrontWireRequest request, string accountId)
        {
            var now = DateTime.UtcNow;
            var commandId = request.CommandId ?? string.Empty;
            switch (request.CommandType)
            {
                case AetherfrontCommandType.CreateFaction:
                    return AetherfrontDomain.CreateFaction(state, commandId, accountId, request.Name, request.Abbreviation, request.Description, request.ColorHex, request.FlagAssetId, request.FlagSha256, now,
                        string.IsNullOrWhiteSpace(request.RecruitmentPolicy) ? RecruitmentPolicies.Application : request.RecruitmentPolicy, request.Public);
                case AetherfrontCommandType.JoinFaction:
                    return AetherfrontDomain.JoinFaction(state, commandId, accountId, request.FactionId, request.DisplayName, now);
                case AetherfrontCommandType.LeaveFaction:
                    return AetherfrontDomain.LeaveFaction(state, commandId, accountId, now);
                case AetherfrontCommandType.UpdateFactionProfile:
                    return AetherfrontDomain.UpdateFactionProfile(state, commandId, accountId, request.FactionId, request.Name, request.Abbreviation, request.Description, request.ColorHex, request.FlagAssetId, request.FlagSha256, now,
                        string.IsNullOrWhiteSpace(request.RecruitmentPolicy) ? null : request.RecruitmentPolicy, request.Public);
                case AetherfrontCommandType.InviteMember:
                    return AetherfrontDomain.InviteMember(state, commandId, accountId, request.FactionId, request.TargetAccountId, now);
                case AetherfrontCommandType.AcceptInvitation:
                    return AetherfrontDomain.AcceptInvitation(state, commandId, accountId, request.InvitationId, request.DisplayName, now);
                case AetherfrontCommandType.RemoveMember:
                    return AetherfrontDomain.RemoveMember(state, commandId, accountId, request.FactionId, request.TargetAccountId, now);
                case AetherfrontCommandType.SetMemberRole:
                    return AetherfrontDomain.SetMemberRole(state, commandId, accountId, request.FactionId, request.TargetAccountId, request.Role, now);
                case AetherfrontCommandType.TransferLeadership:
                    return AetherfrontDomain.TransferLeadership(state, commandId, accountId, request.FactionId, request.TargetAccountId, now);
                case AetherfrontCommandType.TransferScience:
                    return AetherfrontDomain.TransferScience(state, commandId, accountId, request.FactionId, request.TargetFactionId, request.Amount, now);
                case AetherfrontCommandType.TransferFunds:
                    return AetherfrontDomain.TransferFunds(state, commandId, accountId, request.FactionId, request.TargetFactionId, request.Amount, now);
                case AetherfrontCommandType.RegisterCraft:
                    return AetherfrontDomain.RegisterCraft(state, commandId, accountId, request.FactionId, request.EntityId, request.Name, request.ContentSha256, now);
                case AetherfrontCommandType.GrantCraftAccess:
                    return AetherfrontDomain.GrantCraftAccess(state, commandId, accountId, request.EntityId, request.TargetFactionId,
                        DeserializeList<string>(request.PermissionsJson), ToDateTime(request.ExpiresAtUtcTicks), request.AgreementId, now);
                case AetherfrontCommandType.ProposeAlliance:
                    return AetherfrontDomain.ProposeAlliance(state, commandId, accountId, request.FactionId, request.TargetFactionId, ToDateTime(request.ExpiresAtUtcTicks), now);
                case AetherfrontCommandType.RespondToAgreement:
                    return AetherfrontDomain.RespondToAgreement(state, commandId, accountId, request.AgreementId, request.Accept, now);
                case AetherfrontCommandType.RevokeAgreement:
                    return AetherfrontDomain.RevokeAgreement(state, commandId, accountId, request.AgreementId, now);
                case AetherfrontCommandType.CreateTradeOffer:
                    return AetherfrontDomain.CreateTradeOffer(state, commandId, accountId, request.FactionId, request.TargetFactionId,
                        DeserializeList<TradeAsset>(request.AssetJson), DeserializeList<TradeAsset>(request.CounterAssetJson), now);
                case AetherfrontCommandType.ConfirmTrade:
                    return AetherfrontDomain.ConfirmTrade(state, commandId, accountId, request.TradeId, now);
                case AetherfrontCommandType.CancelTrade:
                    return AetherfrontDomain.CancelTrade(state, commandId, accountId, request.TradeId, now);
                case AetherfrontCommandType.PublishStory:
                    return AetherfrontDomain.PublishStory(state, commandId, accountId, request.FactionId, request.Title, request.Body, request.Public,
                        DeserializeList<string>(request.TagsJson), DeserializeList<string>(request.RelatedEventIdsJson), now);
                case AetherfrontCommandType.ReviewStory:
                    return AetherfrontDomain.ReviewStory(state, commandId, accountId, request.StoryId, request.Accept, now);
                case AetherfrontCommandType.RequestLoan:
                    return AetherfrontDomain.RequestLoan(state, commandId, accountId, request.FactionId, request.Amount,
                        request.AprBasisPoints, request.TermDays, now);
                case AetherfrontCommandType.ApproveLoan:
                    return AetherfrontDomain.ApproveLoan(state, commandId, accountId, request.LoanId, now);
                case AetherfrontCommandType.RepayLoan:
                    return AetherfrontDomain.RepayLoan(state, commandId, accountId, request.LoanId, request.Amount, now);
                case AetherfrontCommandType.ContributeResearch:
                    return AetherfrontDomain.ContributeResearch(state, commandId, accountId, request.FactionId,
                        request.ResearchProjectId, request.Amount, now);
                case AetherfrontCommandType.RecordObjectiveEvent:
                    return AetherfrontResult.Reject(state.Revision, "ServerGeneratedObjectiveEventsOnly");
                case AetherfrontCommandType.UnlockTechnology:
                    return AetherfrontDomain.UnlockTechnology(state, commandId, accountId, request.FactionId, request.TechnologyId, now);
                case AetherfrontCommandType.RequestFlagAsset:
                    return AetherfrontResult.Reject(state.Revision, "FlagAssetRequestMustUseSnapshotReply");
                case AetherfrontCommandType.ApplyToFaction:
                    return AetherfrontDomain.ApplyToFaction(state, commandId, accountId, request.FactionId, request.DisplayName, now);
                case AetherfrontCommandType.ReviewFactionApplication:
                    return AetherfrontDomain.ReviewFactionApplication(state, commandId, accountId, request.ApplicationId, request.Accept, now);
                case AetherfrontCommandType.UpsertObjective:
                    return AetherfrontDomain.UpsertObjective(state, commandId, accountId, request.ObjectiveId,
                        request.SeasonId, request.Title, request.Description, request.EventType, request.ObjectiveTarget,
                        request.ScienceReward, request.FundsRewardMicro, request.ReputationReward,
                        request.ObjectivePublicProgress, ToDateTime(request.StartsAtUtcTicks), ToDateTime(request.EndsAtUtcTicks),
                        request.RepeatPeriodDays, request.CatchUpWindowDays, now);
                case AetherfrontCommandType.ArchiveObjective:
                    return AetherfrontDomain.ArchiveObjective(state, commandId, accountId, request.ObjectiveId, now);
                case AetherfrontCommandType.SetEventMode:
                    return AetherfrontDomain.SetEventMode(state, commandId, accountId, request.EventModeId,
                        request.EventModeName, request.EventModeBodyName, request.EventModeSubspaceId,
                        request.EventModeAllowAllCrafts, ToDateTime(request.EventModeStartsAtUtcTicks),
                        ToDateTime(request.EventModeEndsAtUtcTicks), now);
                case AetherfrontCommandType.ClearEventMode:
                    return AetherfrontDomain.ClearEventMode(state, commandId, accountId, now);
                case AetherfrontCommandType.PurchaseStoreItem:
                    return AetherfrontDomain.PurchaseStoreItem(state, commandId, accountId, request.StoreItemId, now);
                default:
                    return AetherfrontResult.Reject(state.Revision, "UnknownAetherfrontCommand");
            }
        }

        private static void SendSnapshot(ClientStructure client)
        {
            var state = AetherfrontServerSystem.Snapshot();
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            SendReply(client, new AetherfrontWireReply
            {
                Operation = AetherfrontWireOperation.SnapshotReply,
                Accepted = true,
                ReasonCode = "Accepted",
                Revision = state.Revision,
                ServerId = state.ServerId,
                StateJson = AetherfrontJson.Serialize(AetherfrontVisibility.ForAccount(state, accountId)),
                EventsJson = "[]",
                InterestJson = AetherfrontVesselInterest.ForClient(state, accountId, client.Subspace)
            });
        }

        private static void SendError(ClientStructure client, string reason)
        {
            if (client == null) return;
            var state = AetherfrontServerSystem.Snapshot();
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            SendReply(client, new AetherfrontWireReply
            {
                Operation = AetherfrontWireOperation.CommandReply,
                Accepted = false,
                ReasonCode = reason,
                Revision = state.Revision,
                ServerId = state.ServerId,
                StateJson = AetherfrontJson.Serialize(AetherfrontVisibility.ForAccount(state, accountId)),
                EventsJson = "[]",
                InterestJson = AetherfrontVesselInterest.ForClient(state, accountId, client.Subspace)
            });
        }

        private static void SendFlagAsset(ClientStructure client, string assetId)
        {
            var state = AetherfrontServerSystem.Snapshot();
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            var found = AetherfrontFlagAssetStore.TryRead(AetherfrontServerSystem.Store.RootPath, assetId, out var hash, out var data);
            SendReply(client, new AetherfrontWireReply
            {
                Operation = AetherfrontWireOperation.CommandReply,
                Accepted = found,
                ReasonCode = found ? "Accepted" : "FlagAssetNotFound",
                Revision = state.Revision,
                ServerId = state.ServerId,
                FlagAssetId = assetId ?? string.Empty,
                FlagSha256 = hash,
                FlagDataBase64 = found ? data : string.Empty,
                StateJson = AetherfrontJson.Serialize(AetherfrontVisibility.ForAccount(state, accountId)),
                EventsJson = "[]",
                InterestJson = AetherfrontVesselInterest.ForClient(state, accountId, client.Subspace)
            });
        }

        private static void SendReply(ClientStructure client, AetherfrontWireReply reply)
        {
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(reply);
            MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
        }

        private static void BroadcastEvents(List<AetherfrontEvent> events)
        {
            events = events == null ? new List<AetherfrontEvent>() : events.Where(e => e != null && e.Public).ToList();
            if (events.Count == 0) return;
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontEventNoticeMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireReply
            {
                Operation = AetherfrontWireOperation.EventNotice,
                Accepted = true,
                Revision = AetherfrontServerSystem.Snapshot().Revision,
                ServerId = AetherfrontServerSystem.Snapshot().ServerId,
                EventsJson = AetherfrontJson.Serialize(events)
            });
            MessageQueuer.SendToAllClients<AetherfrontSrvMsg>(data);
        }

        private static List<T> DeserializeList<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<T>();
            return AetherfrontJson.Deserialize<List<T>>(json) ?? new List<T>();
        }

        private static DateTime? ToDateTime(long ticks)
        {
            if (ticks <= 0) return null;
            try { return new DateTime(ticks, DateTimeKind.Utc); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
