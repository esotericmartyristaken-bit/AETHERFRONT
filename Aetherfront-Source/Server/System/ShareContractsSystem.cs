using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.System.Scenario;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaConfigNode.CfgNode;
using Server.Aetherfront;
using System.Text;

namespace Server.System
{
    public static class ShareContractsSystem
    {
        private const int MaxContractsPerMessage = 4096;
        private const int CoalesceDelayMs = 100;

        private static readonly ConcurrentDictionary<Guid, PendingContractUpdate> PendingContracts =
            new ConcurrentDictionary<Guid, PendingContractUpdate>();

        private static int _flushScheduled;

        public static void ContractsReceived(ClientStructure client, ShareProgressContractsMsgData data)
        {
            if (client == null || data == null)
                return;

            // Only the contract-lock owner may author contract state. Vanilla clients
            // can emit every loaded contract while they do not own this lock. Relaying
            // that storm previously queued hundreds of expensive scenario rebuilds and
            // starved heartbeat traffic until every player timed out.
            if (!CanAcceptContractUpdate(client.PlayerName))
            {
                LunaLog.Warning($"Rejected contract update from {client.PlayerName}; contract lock owner is {LockSystem.LockQuery.ContractLockOwner() ?? "none"}.");
                return;
            }

            var contractCount = Math.Min(Math.Min(Math.Max(data.ContractCount, 0), data.Contracts?.Length ?? 0), MaxContractsPerMessage);
            for (var i = 0; i < contractCount; i++)
            {
                var contract = data.Contracts[i];
                if (contract == null || contract.ContractGuid == Guid.Empty || contract.Data == null ||
                    contract.NumBytes <= 0 || contract.NumBytes > contract.Data.Length)
                {
                    LunaLog.Warning($"Rejected malformed contract payload at index {i} from {client.PlayerName}.");
                    continue;
                }

                PendingContracts[contract.ContractGuid] = new PendingContractUpdate(client, new ContractInfo(contract));
            }

            ScheduleFlush();
        }

        internal static bool CanAcceptContractUpdate(string playerName)
        {
            return !string.IsNullOrWhiteSpace(playerName) && LockSystem.LockQuery.ContractLockBelongsToPlayer(playerName);
        }

        private static void ScheduleFlush()
        {
            if (PendingContracts.IsEmpty || Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
                return;

            _ = Task.Run(FlushPendingContractsAsync);
        }

        private static async Task FlushPendingContractsAsync()
        {
            try
            {
                await Task.Delay(CoalesceDelayMs).ConfigureAwait(false);

                while (ServerContext.ServerRunning && !PendingContracts.IsEmpty)
                {
                    var batch = DrainPendingContracts();
                    if (batch.Count == 0)
                        break;

                    var source = batch[batch.Count - 1].Client;
                    var message = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressContractsMsgData>();
                    message.Contracts = batch.Select(item => item.Contract).ToArray();
                    message.ContractCount = message.Contracts.Length;

                    // One lock + one indexed rebuild for the complete burst. The
                    // returned array omits unchanged payloads, so reconnect/load noise
                    // is neither persisted repeatedly nor relayed to every client.
                    var changedContracts = ScenarioDataUpdater.WriteContractDataToFile(message);
                    if (changedContracts.Length > 0)
                    {
                        message.Contracts = changedContracts;
                        message.ContractCount = changedContracts.Length;
                        RecordCompletedContracts(source, changedContracts);
                        LunaLog.Debug($"Relaying {message.ContractCount} changed contract(s) from {source.PlayerName}.");
                        MessageQueuer.RelayMessage<ShareProgressSrvMsg>(source, message);
                    }

                    if (!PendingContracts.IsEmpty)
                        await Task.Delay(CoalesceDelayMs).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                LunaLog.Error($"Contract coalescer failed: {e}");
            }
            finally
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
                if (!ServerContext.ServerRunning)
                    PendingContracts.Clear();
                else if (!PendingContracts.IsEmpty)
                    ScheduleFlush();
            }
        }

        private static List<PendingContractUpdate> DrainPendingContracts()
        {
            var batch = new List<PendingContractUpdate>(PendingContracts.Count);
            foreach (var entry in PendingContracts.ToArray())
            {
                if (PendingContracts.TryRemove(entry.Key, out var pending))
                    batch.Add(pending);
            }

            return batch;
        }

        private static void RecordCompletedContracts(ClientStructure client, ContractInfo[] contracts)
        {
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (!AetherfrontServerSystem.Enabled || string.IsNullOrWhiteSpace(accountId))
                return;

            foreach (var contract in contracts ?? Array.Empty<ContractInfo>())
            {
                if (contract == null || contract.Data == null || contract.NumBytes <= 0 || contract.NumBytes > contract.Data.Length)
                    continue;
                try
                {
                    var node = new ConfigNode(Encoding.UTF8.GetString(contract.Data, 0, contract.NumBytes));
                    var state = node.GetValue("state")?.Value;
                    if (string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase))
                        AetherfrontGameplayBridge.RecordMissionCompleted(accountId, contract.ContractGuid.ToString("N"));
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"Aetherfront contract objective parse failed for {contract.ContractGuid}: {e.Message}");
                }
            }
        }

        private sealed class PendingContractUpdate
        {
            public ClientStructure Client { get; }
            public ContractInfo Contract { get; }

            public PendingContractUpdate(ClientStructure client, ContractInfo contract)
            {
                Client = client;
                Contract = contract;
            }
        }
    }
}
