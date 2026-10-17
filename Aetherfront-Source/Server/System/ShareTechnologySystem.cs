using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Scenario;
using Server.Aetherfront;
using System;
using System.Linq;

namespace Server.System
{
    public static class ShareTechnologySystem
    {
        public static void TechnologyReceived(ClientStructure client, ShareProgressTechnologyMsgData data)
        {
            if (AetherfrontServerSystem.Enabled)
            {
                LunaLog.Debug($"Ignored legacy technology update {data?.TechNode?.Id ?? "<empty>"} in APF authoritative mode.");
                return;
            }

            LunaLog.Debug($"Technology unlocked: {data.TechNode.Id}");

            //Send the technology update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteTechnologyDataToFile(data);
        }
    }
}
