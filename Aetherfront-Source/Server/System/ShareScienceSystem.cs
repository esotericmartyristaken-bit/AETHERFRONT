using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Scenario;
using Server.Aetherfront;
using System;
using Aetherfront.Core;

namespace Server.System
{
    public static class ShareScienceSystem
    {
        public static void ScienceReceived(ClientStructure client, ShareProgressScienceMsgData data)
        {
            LunaLog.Debug($"Science received: {data.Science} Reason: {data.Reason}");

            // APF progression lives in the faction ledger. The legacy LMP
            // ShareProgress packet contains a client-authored absolute value;
            // never persist or relay it in APF mode. Aetherfront commands and
            // trusted server events are the only progression authorities.
            if (AetherfrontServerSystem.Enabled)
            {
                LunaLog.Debug("Ignored legacy science update in APF authoritative mode.");
                return;
            }

            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (AetherfrontServerSystem.Enabled && !string.IsNullOrWhiteSpace(accountId))
            {
                if (AetherfrontProgressionGuard.TryGetScienceDelta(accountId, data.Science, out var wholeDelta))
                {
                    wholeDelta = Math.Min(AetherfrontConstants.MaxScienceTransfer, wholeDelta);
                    AetherfrontGameplayBridge.RecordScienceDelta(accountId, wholeDelta, data.Reason);
                }
            }

            //send the science update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteScienceDataToFile(data.Science);
        }
    }
}
