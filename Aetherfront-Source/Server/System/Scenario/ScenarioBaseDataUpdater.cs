using LunaConfigNode.CfgNode;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        #region Semaphore

        /// <summary>
        /// To not overwrite our own data we use a lock
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> Semaphore = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// Returns the synchronization object shared by scenario mutations, backups,
        /// and network serialization. ConfigNode collections cannot be safely
        /// enumerated while another task modifies them.
        /// </summary>
        internal static object GetScenarioLock(string scenarioModule)
        {
            return Semaphore.GetOrAdd(scenarioModule, _ => new object());
        }

        #endregion

        /// <summary>
        /// Raw updates a scenario in the dictionary
        /// </summary>
        public static void RawConfigNodeInsertOrUpdate(string scenarioModule, string scenarioAsConfigNode)
        {
            Task.Run(() =>
            {
                var scenario = new ConfigNode(scenarioAsConfigNode);
                lock (GetScenarioLock(scenarioModule))
                {
                    if (scenarioModule == "ContractSystem")
                        MigrateContractsScenario(scenario);

                    ScenarioStoreSystem.CurrentScenarios.AddOrUpdate(scenarioModule, scenario, (key, existingVal) => scenario);
                }
            });
        }
    }
}
