using ByteSizeLib;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Message.Base;
using Server.Server;
using Server.System;
using Server.System.Vessel;
using System;
using System.Linq;
using System.Text;

using Aetherfront.Core;
using Server.Aetherfront;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace Server.Message
{
    public class VesselMsgReader : ReaderBase
    {
        private static readonly ConcurrentDictionary<Guid, string> LegacyVesselOwners =
            new ConcurrentDictionary<Guid, string>();
        private static readonly ConcurrentDictionary<Guid, string> VesselBodyNames =
            new ConcurrentDictionary<Guid, string>();
        private static readonly ConcurrentDictionary<Guid, int> VesselSubspaces =
            new ConcurrentDictionary<Guid, int>();

        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var messageData = message.Data as VesselBaseMsgData;
            switch (messageData?.VesselMessageType)
            {
                case VesselMessageType.Sync:
                    HandleVesselsSync(client, messageData);
                    message.Recycle();
                    break;
                case VesselMessageType.Proto:
                    HandleVesselProto(client, messageData);
                    break;
                case VesselMessageType.Remove:
                    HandleVesselRemove(client, messageData);
                    break;
                case VesselMessageType.Position:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    RecordBodyMilestone(client, (VesselPositionMsgData)messageData);
                    var position = (VesselPositionMsgData)messageData;
                    AetherfrontVesselInterest.UpdatePosition(client, messageData.VesselId, position.BodyName,
                        client.Subspace, position.Landed, position.Splashed);
                    RelayVesselMessage(client, messageData);
                    if (client.Subspace == WarpContext.LatestSubspace.Id)
                        VesselDataUpdater.WritePositionDataToFile(messageData);
                    break;
                case VesselMessageType.Flightstate:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    RelayVesselMessage(client, messageData);
                    VesselDataUpdater.WriteFlightstateDataToFile(messageData);
                    break;
                case VesselMessageType.Update:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    VesselDataUpdater.WriteUpdateDataToFile(messageData);
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.Resource:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    VesselDataUpdater.WriteResourceDataToFile(messageData);
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.PartSyncField:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    VesselDataUpdater.WritePartSyncFieldDataToFile(messageData);
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.PartSyncUiField:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    VesselDataUpdater.WritePartSyncUiFieldDataToFile(messageData);
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.PartSyncCall:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.ActionGroup:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    VesselDataUpdater.WriteActionGroupDataToFile(messageData);
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.Fairing:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    VesselDataUpdater.WriteFairingDataToFile(messageData);
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.Decouple:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    RelayVesselMessage(client, messageData);
                    break;
                case VesselMessageType.Couple:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    HandleVesselCouple(client, messageData);
                    break;
                case VesselMessageType.Undock:
                    if (!CanMutateVessel(client, messageData.VesselId)) break;
                    RelayVesselMessage(client, messageData);
                    break;
                default:
                    throw new NotImplementedException("Vessel message type not implemented");
            }
        }

        private static void HandleVesselRemove(ClientStructure client, VesselBaseMsgData message)
        {
            var data = (VesselRemoveMsgData)message;

            if (!CanMutateVessel(client, data.VesselId))
                return;

            if (LockSystem.LockQuery.ControlLockExists(data.VesselId) && !LockSystem.LockQuery.ControlLockBelongsToPlayer(data.VesselId, client.PlayerName))
                return;

            // Publish the kill-list entry BEFORE touching the store so any in-flight proto task
            // (VesselDataUpdater.RawConfigNodeInsertOrUpdate schedules its store write on Task.Run)
            // observes the flag and aborts instead of resurrecting the vessel we're about to remove.
            if (data.AddToKillList)
                VesselContext.RemovedVessels.TryAdd(data.VesselId, 0);

            if (VesselStoreSystem.VesselExists(data.VesselId))
            {
                // Resolve the vessel name BEFORE RemoveVessel purges the store entry so the audit log line has something useful.
                var vesselName = TryGetVesselName(data.VesselId);

                LunaLog.Debug($"Removing vessel {data.VesselId} from {client.PlayerName}");
                CraftCreationAndRemovalLog.LogRemoved(data.VesselId, vesselName, client.PlayerName, data.Reason);

                VesselStoreSystem.RemoveVessel(data.VesselId);
            }

            if (!string.IsNullOrWhiteSpace(data.Reason) &&
                data.Reason.IndexOf("Recovered", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
                var ownerFaction = AetherfrontServerSystem.Snapshot().FindFactionForAccount(accountId);
                if (ownerFaction != null)
                    AetherfrontGameplayBridge.RecordTrustedEvent(accountId, ownerFaction.Id,
                        AetherfrontEventTypes.VesselRecovered, data.VesselId.ToString());
            }

            //Relay the message.
            RelayVesselMessage(client, data);
            VesselSubspaces.TryRemove(data.VesselId, out _);
            VesselBodyNames.TryRemove(data.VesselId, out _);
            AetherfrontVesselInterest.Remove(data.VesselId);
        }

        private static void HandleVesselProto(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselProtoMsgData)message;

            if (VesselContext.RemovedVessels.ContainsKey(msgData.VesselId)) return;

            if (msgData.NumBytes == 0)
            {
                LunaLog.Warning($"Received a vessel with 0 bytes ({msgData.VesselId}) from {client.PlayerName}.");
                return;
            }

            if (AetherfrontServerSystem.Enabled &&
                AetherfrontServerSystem.Snapshot().FindCraft(msgData.VesselId.ToString()) != null &&
                !CanMutateVessel(client, msgData.VesselId))
            {
                LunaLog.Warning($"Rejected unauthorized vessel replacement {msgData.VesselId} from {client.PlayerName}.");
                return;
            }

            var vesselText = Encoding.UTF8.GetString(msgData.Data, 0, msgData.NumBytes);
            var vesselName = CraftCreationAndRemovalLog.ExtractVesselName(vesselText);

            var boundAccountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (AetherfrontServerSystem.Enabled && !string.IsNullOrWhiteSpace(boundAccountId))
                LegacyVesselOwners.TryAdd(msgData.VesselId, boundAccountId);

            if (KerbalSystem.IsMalformedEvaVessel(vesselText))
            {
                LunaLog.Warning($"Rejected malformed EVA vessel {msgData.VesselId} from {client.PlayerName}; an EVA must contain exactly one crew member.");

                // Preserve an existing valid authoritative vessel. Otherwise permanently
                // suppress the malformed upload and remove stale client copies.
                if (VesselStoreSystem.VesselExists(msgData.VesselId))
                {
                    SendStoredVessel(client, msgData.VesselId);
                }
                else
                {
                    VesselContext.RemovedVessels.TryAdd(msgData.VesselId, 0);
                    SendVesselRemoval(client, msgData.VesselId, "Malformed crewless EVA quarantined");
                }
                return;
            }

            if (!VesselStoreSystem.VesselExists(msgData.VesselId))
            {
                LunaLog.Debug($"Saving vessel {msgData.VesselId} ({ByteSize.FromBytes(msgData.NumBytes).KiloBytes} KB) from {client.PlayerName}.");

                // Audit-log first-time vessel registrations. Use the raw config-node text to pull
                // the name out cheaply without allocating another Vessel instance here - the
                // authoritative parse still happens inside VesselDataUpdater below.
                CraftCreationAndRemovalLog.LogCreated(msgData.VesselId, vesselName, client.PlayerName, msgData.Reason);
            }

            VesselDataUpdater.RawConfigNodeInsertOrUpdate(msgData.VesselId, vesselText);
            RegisterVesselForFaction(client, msgData.VesselId, vesselName, vesselText);
            VesselSubspaces[msgData.VesselId] = client.Subspace;
            AetherfrontVesselInterest.UpdateProto(client, msgData.VesselId, vesselName, client.Subspace);
            RelayVesselMessage(client, msgData);
        }

        /// <summary>
        /// Looks up a vessel's display name from the in-memory store. Returns <c>null</c> if the
        /// vessel is not present or the name field is missing/malformed.
        /// </summary>
        private static string TryGetVesselName(Guid vesselId)
        {
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var vessel))
                return null;

            try
            {
                return vessel.Fields.GetSingle("name")?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static void HandleVesselsSync(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselSyncMsgData)message;
            var state = AetherfrontServerSystem.Enabled ? AetherfrontServerSystem.Snapshot() : null;

            var allVessels = VesselStoreSystem.CurrentVessels.Keys.ToList();

            //Here we only remove the vessels that the client ALREADY HAS so we only send the vessels they DON'T have
            for (var i = 0; i < msgData.VesselsCount; i++)
            {
                var clientVesselId = msgData.VesselIds[i];
                if (VesselContext.RemovedVessels.ContainsKey(clientVesselId))
                    SendVesselRemoval(client, clientVesselId, "Server-side vessel quarantine");
                else if (!CanViewVessel(client, clientVesselId, state))
                    SendVesselRemoval(client, clientVesselId, "Vessel access no longer permitted", false);
                else
                    allVessels.Remove(clientVesselId);
            }

            var vesselsToSend = allVessels.Where(vesselId => CanViewVessel(client, vesselId, state)).ToList();
            foreach (var vesselId in vesselsToSend)
            {
                var vesselData = VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId);
                if (vesselData.Length > 0)
                {
                    var protoMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                    protoMsg.Data = Encoding.UTF8.GetBytes(vesselData);
                    protoMsg.NumBytes = vesselData.Length;
                    protoMsg.VesselId = vesselId;

                    MessageQueuer.SendToClient<VesselSrvMsg>(client, protoMsg);
                }
            }

            if (allVessels.Count > 0)
                LunaLog.Debug($"Sending {client.PlayerName} {vesselsToSend.Count} vessels");
        }

        private static bool CanViewVessel(ClientStructure client, Guid vesselId)
        {
            if (!AetherfrontServerSystem.Enabled) return true;
            return CanViewVessel(client, vesselId, AetherfrontServerSystem.Snapshot());
        }

        private static bool CanViewVessel(ClientStructure client, Guid vesselId, AetherfrontState state)
        {
            if (!AetherfrontServerSystem.Enabled || state == null) return true;
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (AetherfrontVesselInterest.AllowsEventSync(state, vesselId, client.Subspace))
                return true;
            var craft = state.FindCraft(vesselId.ToString());
            var faction = state.FindFactionForAccount(accountId);
            if (craft == null)
                return LegacyVesselOwners.TryGetValue(vesselId, out var owner) &&
                    string.Equals(owner, accountId, StringComparison.OrdinalIgnoreCase);
            var own = string.Equals(craft.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(craft.OwnerFactionId, faction?.Id, StringComparison.OrdinalIgnoreCase);
            if (!own && GetVesselSubspace(vesselId) == int.MinValue)
                return false;
            return AetherfrontDomain.CanControlCraft(state, accountId,
                faction?.Id ?? string.Empty, craft.Id, CraftAccess.ViewFull, DateTime.UtcNow,
                GetVesselSubspace(vesselId), client.Subspace);
        }

        private static bool CanMutateVessel(ClientStructure client, Guid vesselId)
        {
            if (!AetherfrontServerSystem.Enabled) return true;
            var state = AetherfrontServerSystem.Snapshot();
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            var craft = state.FindCraft(vesselId.ToString());
            var faction = state.FindFactionForAccount(accountId);
            if (craft == null)
                return LegacyVesselOwners.TryGetValue(vesselId, out var owner) &&
                    string.Equals(owner, accountId, StringComparison.OrdinalIgnoreCase);
            var own = string.Equals(craft.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(craft.OwnerFactionId, faction?.Id, StringComparison.OrdinalIgnoreCase);
            if (!own && GetVesselSubspace(vesselId) == int.MinValue)
                return false;
            var allowed = AetherfrontDomain.CanControlCraft(state, accountId,
                faction?.Id ?? string.Empty, craft.Id, CraftAccess.Modify, DateTime.UtcNow,
                GetVesselSubspace(vesselId), client.Subspace);
            if (allowed)
                VesselSubspaces[vesselId] = client.Subspace;
            return allowed;
        }

        private static int GetVesselSubspace(Guid vesselId)
        {
            return VesselSubspaces.TryGetValue(vesselId, out var subspace) ? subspace : int.MinValue;
        }

        private static void RegisterVesselForFaction(ClientStructure client, Guid vesselId, string vesselName, string vesselText)
        {
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (!AetherfrontServerSystem.Enabled || string.IsNullOrWhiteSpace(accountId)) return;
            try
            {
                var current = AetherfrontServerSystem.Snapshot();
                if (current.FindCraft(vesselId.ToString()) != null)
                    return;
                var registration = AetherfrontServerSystem.Execute(state =>
                {
                    var faction = state.FindFactionForAccount(accountId);
                    if (state.FindCraft(vesselId.ToString()) != null)
                        return AetherfrontResult.Reject(state.Revision, "NoRegistrationRequired");
                    return AetherfrontDomain.RegisterCraft(state, "vessel-register:" + vesselId.ToString("N"),
                        accountId, faction?.Id ?? string.Empty, vesselId.ToString(), vesselName ?? vesselId.ToString(),
                        ComputeSha256(vesselText), DateTime.UtcNow);
                });
                if (registration.Accepted)
                {
                    var ownerFaction = AetherfrontServerSystem.Snapshot().FindFactionForAccount(accountId);
                    AetherfrontGameplayBridge.RecordTrustedEvent(accountId, ownerFaction?.Id ?? string.Empty,
                        AetherfrontEventTypes.VesselLaunched, vesselId.ToString());
                }
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront vessel ownership registration skipped: " + e.Message);
            }
        }

        private static void RecordBodyMilestone(ClientStructure client, VesselPositionMsgData message)
        {
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (!AetherfrontServerSystem.Enabled || message == null || string.IsNullOrWhiteSpace(message.BodyName) ||
                string.IsNullOrWhiteSpace(accountId)) return;

            var bodyName = message.BodyName.Trim();
            if (bodyName.Length > 96) return;
            var faction = AetherfrontServerSystem.Snapshot().FindFactionForAccount(accountId);
            if (faction == null) return;
            if (VesselBodyNames.TryGetValue(message.VesselId, out var previous) &&
                string.Equals(previous, bodyName, StringComparison.OrdinalIgnoreCase)) return;
            AetherfrontGameplayBridge.RecordTrustedEvent(accountId, faction.Id,
                AetherfrontEventTypes.BodyReached, bodyName);
            VesselBodyNames[message.VesselId] = bodyName;
        }

        private static string ComputeSha256(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void SendStoredVessel(ClientStructure client, Guid vesselId)
        {
            var vesselData = VesselStoreSystem.GetVesselInConfigNodeFormat(vesselId);
            if (string.IsNullOrEmpty(vesselData)) return;

            var protoMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
            protoMsg.Data = Encoding.UTF8.GetBytes(vesselData);
            protoMsg.NumBytes = protoMsg.Data.Length;
            protoMsg.VesselId = vesselId;
            MessageQueuer.SendToClient<VesselSrvMsg>(client, protoMsg);
        }

        private static void SendVesselRemoval(ClientStructure client, Guid vesselId, string reason, bool quarantine = true)
        {
            var removeMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
            removeMsg.VesselId = vesselId;
            removeMsg.AddToKillList = quarantine;
            removeMsg.KillOnReceive = quarantine;
            removeMsg.Reason = reason;
            MessageQueuer.SendToClient<VesselSrvMsg>(client, removeMsg);
        }

        private static void HandleVesselCouple(ClientStructure client, VesselBaseMsgData message)
        {
            var msgData = (VesselCoupleMsgData)message;

            if (!CanMutateVessel(client, msgData.VesselId) || !CanMutateVessel(client, msgData.CoupledVesselId))
                return;

            LunaLog.Debug($"Coupling message received! Dominant vessel: {msgData.VesselId}");
            RelayVesselMessage(client, msgData);

            if (VesselContext.RemovedVessels.ContainsKey(msgData.CoupledVesselId)) return;

            //Now remove the weak vessel but DO NOT add to the removed vessels as they might undock!!!
            LunaLog.Debug($"Removing weak coupled vessel {msgData.CoupledVesselId}");

            // Audit-log the implicit removal triggered by a docking/coupling event. Name must be
            // resolved BEFORE RemoveVessel clears the store entry.
            var coupledVesselName = TryGetVesselName(msgData.CoupledVesselId);
            CraftCreationAndRemovalLog.LogRemoved(msgData.CoupledVesselId, coupledVesselName, client.PlayerName, "Coupled/Docked");

            VesselStoreSystem.RemoveVessel(msgData.CoupledVesselId);
            AetherfrontVesselInterest.Remove(msgData.CoupledVesselId);

            //Tell all clients to remove the weak vessel
            var removeMsgData = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
            removeMsgData.VesselId = msgData.CoupledVesselId;
            removeMsgData.Reason = "Coupled/Docked";

            MessageQueuer.SendToAllClients<VesselSrvMsg>(removeMsgData);
        }

        private static void RelayVesselMessage(ClientStructure source, VesselBaseMsgData message)
        {
            if (message == null) return;
            if (!AetherfrontServerSystem.Enabled)
            {
                MessageQueuer.RelayMessage<VesselSrvMsg>(source, message);
                return;
            }

            var state = AetherfrontServerSystem.Snapshot();
            foreach (var otherClient in ServerContext.Clients.Values.Where(c => !Equals(c, source) && CanViewVessel(c, message.VesselId, state)))
                MessageQueuer.SendToClient<VesselSrvMsg>(otherClient, message);
        }
    }
}
