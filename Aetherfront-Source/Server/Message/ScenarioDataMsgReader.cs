using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Message.Base;
using Server.System;
using Server.Aetherfront;
using Server.Log;

namespace Server.Message
{
    public class ScenarioDataMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var messageData = message.Data as ScenarioBaseMsgData;
            if (AetherfrontServerSystem.Enabled && messageData?.ScenarioMessageType == ScenarioMessageType.Data)
            {
                // APF progression/state is server-owned. A full scenario
                // upload is a client-authored replacement path and would
                // bypass the ShareProgress authority gate.
                LunaLog.Debug($"Ignored legacy scenario upload from {client?.PlayerName ?? "unknown"} in APF authoritative mode.");
                message.Recycle();
                return;
            }
            switch (messageData?.ScenarioMessageType)
            {
                case ScenarioMessageType.Request:
                    StartupReplyGuard.QueueOnce(client, StartupReplyKind.Scenarios,
                        () => ScenarioSystem.SendScenarioModules(client));
                    break;
                case ScenarioMessageType.Data:
                    ScenarioSystem.ParseReceivedScenarioData(client, messageData);
                    break;
            }

            //We don't use this message anymore so we can recycle it
            message.Recycle();
        }
    }
}
