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
    public static class ShareFundsSystem
    {
        public static void FundsReceived(ClientStructure client, ShareProgressFundsMsgData data)
        {
            LunaLog.Debug($"Funds received: {data.Funds} Reason: {data.Reason}");

            // Funds are faction-owned in APF. The legacy packet is an
            // absolute client value, not an authenticated transaction.
            if (AetherfrontServerSystem.Enabled)
            {
                LunaLog.Debug("Ignored legacy funds update in APF authoritative mode.");
                return;
            }

            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (AetherfrontServerSystem.Enabled && !string.IsNullOrWhiteSpace(accountId))
            {
                if (AetherfrontProgressionGuard.TryGetFundsDelta(accountId, data.Funds, out var amountMicro))
                {
                    amountMicro = Math.Min(AetherfrontConstants.MaxFundsMicroTransfer, amountMicro);
                    AetherfrontGameplayBridge.RecordFundsDelta(accountId, amountMicro, data.Reason);
                }
            }

            //send the funds update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteFundsDataToFile(data.Funds);
        }
    }
}
