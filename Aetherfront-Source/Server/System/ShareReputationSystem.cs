using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Scenario;
using Server.Aetherfront;
using System;

namespace Server.System
{
    public static class ShareReputationSystem
    {
        public static void ReputationReceived(ClientStructure client, ShareProgressReputationMsgData data)
        {
            LunaLog.Debug($"Reputation received: {data.Reputation} Reason: {data.Reason}");

            // Reputation is faction-owned in APF. Do not accept a raw
            // client-authored absolute value as a server transaction.
            if (AetherfrontServerSystem.Enabled)
            {
                LunaLog.Debug("Ignored legacy reputation update in APF authoritative mode.");
                return;
            }

            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (AetherfrontServerSystem.Enabled && !string.IsNullOrWhiteSpace(accountId))
            {
                if (AetherfrontProgressionGuard.TryGetReputationDelta(accountId, data.Reputation, out var amount))
                    AetherfrontGameplayBridge.RecordReputationDelta(accountId, amount, data.Reason);
            }

            //send the reputation update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteReputationDataToFile(data.Reputation);
        }
    }
}
