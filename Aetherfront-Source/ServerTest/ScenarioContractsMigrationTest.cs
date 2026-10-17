using LunaConfigNode.CfgNode;
using LmpCommon.Locks;
using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.System;
using Server.System.Scenario;
using System;
using System.Linq;
using System.Text;

namespace ServerTest
{
    [TestClass]
    public class ScenarioContractsMigrationTest
    {
        private const string ScenarioText = @"
name = ContractSystem
CONTRACTS
{
    CONTRACT_FINISHED
    {
        guid = invalid-finished
        state = Failed
        PARAM
        {
            name = SCANsatCoverage
            id = SCANsatCoverage_3
            ContractIdentifier = unknown
        }
    }
    CONTRACT_FINISHED
    {
        guid = valid-finished
        state = Completed
        PARAM
        {
            name = SCANsatCoverage
            targetBody = Kerbin
        }
    }
    CONTRACT
    {
        guid = malformed-active
        state = Active
        PARAM
        {
            name = SCANsatCoverage
        }
    }
}";

        [TestMethod]
        public void MigrateContractsScenario_DropsOnlyInvalidFinishedScanSatContracts()
        {
            var scenario = new ConfigNode(ScenarioText);

            ScenarioDataUpdater.MigrateContractsScenario(scenario);

            var contracts = scenario.GetNode("CONTRACTS").Value.GetAllNodes().ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "valid-finished", "malformed-active" },
                contracts.Select(node => node.GetValue("guid").Value).ToArray());
        }

        [TestMethod]
        public void ShouldDiscardInvalidFinishedContract_RequiresTerminalStateAndMissingTarget()
        {
            var contracts = new ConfigNode(ScenarioText).GetNode("CONTRACTS").Value.GetAllNodes().ToArray();

            Assert.IsTrue(ScenarioDataUpdater.ShouldDiscardInvalidFinishedContract(contracts[0]));
            Assert.IsFalse(ScenarioDataUpdater.ShouldDiscardInvalidFinishedContract(contracts[1]));
            Assert.IsFalse(ScenarioDataUpdater.ShouldDiscardInvalidFinishedContract(contracts[2]));
        }

        [TestMethod]
        public void WriteContractDataToFile_DeduplicatesAndAppliesBatchOnce()
        {
            var guid = Guid.NewGuid();
            var scenario = new ConfigNode($@"
name = ContractSystem
CONTRACTS
{{
    CONTRACT
    {{
        guid = {guid}
        state = Active
        type = TestContract
    }}
}}");
            ScenarioStoreSystem.CurrentScenarios["ContractSystem"] = scenario;

            var unchangedText = $@"guid = {guid}
state = Active
type = TestContract";
            var unchanged = CreateMessage(guid, unchangedText);
            Assert.AreEqual(0, ScenarioDataUpdater.WriteContractDataToFile(unchanged).Length);

            var changed = CreateMessage(guid, $@"guid = {guid}
state = Completed
type = TestContract");
            Assert.AreEqual(1, ScenarioDataUpdater.WriteContractDataToFile(changed).Length);

            var stored = scenario.GetNode("CONTRACTS").Value.GetAllNodes().Single();
            Assert.AreEqual("CONTRACT_FINISHED", stored.Name);
            Assert.AreEqual("Completed", stored.GetValue("state").Value);
        }

        [TestMethod]
        public void ContractUpdates_RequireContractLockOwner()
        {
            foreach (var existing in LockSystem.LockQuery.GetAllLocks().ToArray())
                LockSystem.ReleaseLock(existing);

            Assert.IsFalse(ShareContractsSystem.CanAcceptContractUpdate("owner"));
            Assert.IsTrue(LockSystem.AcquireLock(new LockDefinition(LockType.Contract, "owner"), false, out _));
            Assert.IsTrue(ShareContractsSystem.CanAcceptContractUpdate("owner"));
            Assert.IsFalse(ShareContractsSystem.CanAcceptContractUpdate("other"));
        }

        private static ShareProgressContractsMsgData CreateMessage(Guid guid, string nodeText)
        {
            var data = Encoding.UTF8.GetBytes(nodeText);
            var message = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressContractsMsgData>();
            message.Contracts = new[]
            {
                new ContractInfo { ContractGuid = guid, Data = data, NumBytes = data.Length }
            };
            message.ContractCount = 1;
            return message;
        }
    }
}
