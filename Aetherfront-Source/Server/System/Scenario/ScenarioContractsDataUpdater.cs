using LmpCommon.Message.Data.ShareProgress;
using LunaConfigNode.CfgNode;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        /// <summary>
        /// Parent node that KSP uses to persist BOTH active and finished contracts.
        /// KSP does not use a separate CONTRACTS_FINISHED parent; instead it stores
        /// finished entries as CONTRACT_FINISHED siblings of CONTRACT entries inside
        /// this single CONTRACTS parent.
        /// </summary>
        private const string ContractsParentNodeName = "CONTRACTS";

        /// <summary>
        /// Child node name KSP expects for active contracts.
        /// </summary>
        private const string ActiveContractNodeName = "CONTRACT";

        /// <summary>
        /// Child node name KSP expects for archived/finished contracts.
        /// Storing finished contracts as CONTRACT here causes them to load as Active
        /// and never appear in the Mission Control "Archived" tab.
        /// </summary>
        private const string FinishedContractNodeName = "CONTRACT_FINISHED";

        /// <summary>
        /// We received a contract message so update the scenario file accordingly.
        ///
        /// Each incoming contract is renamed (CONTRACT vs CONTRACT_FINISHED) based on
        /// its serialized state value, then upserted into a guid-indexed view of the
        /// existing contracts. The CONTRACTS parent is rebuilt from that view via
        /// clear-then-add. This is necessary because LunaConfigNode 1.8.1's
        /// <c>RemoveNode(ConfigNode)</c> overload only updates the lookup dictionaries
        /// and not the underlying serialized list, so any other approach leaves stale
        /// duplicates on disk that KSP loads back as still-active missions.
        /// </summary>
        public static ContractInfo[] WriteContractDataToFile(ShareProgressContractsMsgData contractsMsg)
        {
            try
            {
                lock (GetScenarioLock("ContractSystem"))
                {
                    if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ContractSystem", out var scenario))
                        return Array.Empty<ContractInfo>();

                    var contractsParent = scenario.GetNode(ContractsParentNodeName)?.Value;
                    if (contractsParent == null)
                    {
                        scenario.AddNode(new ConfigNode(ContractsParentNodeName, scenario));
                        contractsParent = scenario.GetNode(ContractsParentNodeName)?.Value;
                        if (contractsParent == null)
                            return Array.Empty<ContractInfo>();
                    }

                    var byGuid = IndexExistingContractsByGuid(contractsParent, out var unidentified);
                    var nonContractChildren = CollectNonContractChildren(contractsParent);
                    var contractCount = Math.Min(Math.Max(contractsMsg?.ContractCount ?? 0, 0), contractsMsg?.Contracts?.Length ?? 0);
                    var changedContracts = new List<ContractInfo>(contractCount);
                    var scenarioChanged = false;

                    for (var i = 0; i < contractCount; i++)
                    {
                        var contractInfo = contractsMsg.Contracts[i];
                        if (contractInfo?.Data == null || contractInfo.NumBytes <= 0 || contractInfo.NumBytes > contractInfo.Data.Length)
                        {
                            LunaLog.Warning($"Ignoring malformed contract payload at index {i}.");
                            continue;
                        }

                        ConfigNode incomingNode;
                        try
                        {
                            incomingNode = new ConfigNode(Encoding.UTF8.GetString(contractInfo.Data, 0, contractInfo.NumBytes));
                        }
                        catch (Exception e)
                        {
                            LunaLog.Warning($"Ignoring unreadable contract {contractInfo.ContractGuid}: {e.Message}");
                            continue;
                        }

                        var stateValue = incomingNode.GetValue("state")?.Value;
                        incomingNode.Name = IsFinishedContractState(stateValue) ? FinishedContractNodeName : ActiveContractNodeName;

                        var guid = incomingNode.GetValue("guid")?.Value;
                        if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out var parsedGuid) ||
                            contractInfo.ContractGuid == Guid.Empty || parsedGuid != contractInfo.ContractGuid)
                        {
                            LunaLog.Warning($"Ignoring contract payload with missing or mismatched guid {contractInfo.ContractGuid}.");
                            continue;
                        }

                        // Do not reintroduce a terminal SCANsat contract that cannot be
                        // loaded by Contract Configurator. Startup migration removes old
                        // copies; this keeps later incremental updates clean.
                        if (ShouldDiscardInvalidFinishedContract(incomingNode))
                        {
                            scenarioChanged |= byGuid.Remove(guid);
                            continue;
                        }

                        if (byGuid.TryGetValue(guid, out var existing) &&
                            ContractNodesAreEquivalent(existing, incomingNode))
                            continue;

                        byGuid[guid] = incomingNode;
                        changedContracts.Add(contractInfo);
                        scenarioChanged = true;
                    }

                    if (scenarioChanged)
                    {
                        var survivors = new List<ConfigNode>(byGuid.Count + unidentified.Count);
                        survivors.AddRange(byGuid.Values);
                        survivors.AddRange(unidentified);
                        RebuildContractsParent(contractsParent, nonContractChildren, survivors);
                    }

                    return changedContracts.ToArray();
                }
            }
            catch (Exception e)
            {
                LunaLog.Error($"Contract update failed: {e}");
                return Array.Empty<ContractInfo>();
            }
        }

        internal static bool ContractNodesAreEquivalent(ConfigNode first, ConfigNode second)
        {
            if (first == null || second == null)
                return first == second;

            // Parent-owned nodes serialize with a wrapper and indentation while
            // standalone network nodes do not. Compare the parsed value/node tree and
            // intentionally ignore only the root wrapper.
            return string.Equals(NormalizeContractNode(first), NormalizeContractNode(second), StringComparison.Ordinal);
        }

        private static string NormalizeContractNode(ConfigNode node)
        {
            var result = new StringBuilder();
            AppendContractNodeContents(node, result);
            return result.ToString();
        }

        private static void AppendContractNodeContents(ConfigNode node, StringBuilder result)
        {
            foreach (var value in node.GetAllValues())
            {
                result.Append("V\0").Append(value.Key).Append('\0').Append(value.Value).Append('\0');
            }

            foreach (var child in node.GetAllNodes())
            {
                result.Append("N\0").Append(child.Name).Append('\0');
                AppendContractNodeContents(child, result);
                result.Append("E\0");
            }
        }

        /// <summary>
        /// Builds a guid-keyed view of the contract children currently under the parent.
        /// Entries without a guid are returned via <paramref name="unidentified"/> so they
        /// can be preserved verbatim during the rebuild.
        /// </summary>
        private static Dictionary<string, ConfigNode> IndexExistingContractsByGuid(ConfigNode contractsParent, out List<ConfigNode> unidentified)
        {
            var index = new Dictionary<string, ConfigNode>(StringComparer.OrdinalIgnoreCase);
            unidentified = new List<ConfigNode>();

            foreach (var child in contractsParent.GetAllNodes())
            {
                if (child.Name != ActiveContractNodeName && child.Name != FinishedContractNodeName)
                    continue;

                var guid = child.GetValue("guid")?.Value;
                if (string.IsNullOrEmpty(guid))
                {
                    unidentified.Add(child);
                    continue;
                }

                index[guid] = child;
            }

            return index;
        }

        /// <summary>
        /// Returns true for contract states that KSP persists as CONTRACT_FINISHED.
        /// Mirrors KSP's Contract.State enum members that drive ContractsFinished bucketing.
        /// </summary>
        internal static bool IsFinishedContractState(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;

            switch (state)
            {
                case "Completed":
                case "Cancelled":
                case "DeadlineExpired":
                case "Failed":
                case "Withdrawn":
                    return true;
                default:
                    return false;
            }
        }
    }
}
