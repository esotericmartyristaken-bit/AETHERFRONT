using LmpCommon.Enums;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;
using Server.Settings.Structures;
using Server.Apf;

namespace Server.System
{
    public class HandshakeSystemSender
    {
        public static void SendHandshakeReply(ClientStructure client, HandshakeReply enumResponse, string reason)
        {
            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<HandshakeReplyMsgData>();
            msgData.Response = enumResponse;
            msgData.Reason = reason;
            msgData.ApfEnabled = ApfServerSettings.Enabled;
            msgData.ApfServerId = ApfServerSettings.Manifest?.ServerId ?? string.Empty;
            msgData.ApfPackId = ApfServerSettings.Manifest?.PackId ?? string.Empty;
            msgData.ApfPackVersion = ApfServerSettings.Manifest?.PackVersion ?? string.Empty;
            msgData.ApfManifestUrl = ApfServerSettings.ManifestUrl;
            msgData.ApfRequiredClientVersion = ApfServerSettings.Manifest?.RequiredClientVersion ?? string.Empty;
            msgData.ApfCareerProfileId = ApfServerSettings.CareerProfileId;
            msgData.ApfCareerProfileJson = ApfServerSettings.CareerProfile?.ToJson() ?? string.Empty;

            if (enumResponse == HandshakeReply.HandshookSuccessfully)
            {
                msgData.ModControl = GeneralSettings.SettingsStore.ModControl;
                msgData.ServerStartTime = TimeContext.StartTime.Ticks;

                if (GeneralSettings.SettingsStore.ModControl)
                {
                    msgData.ModFileData = FileHandler.ReadFileText(ServerContext.ModFilePath);
                }
            }

            MessageQueuer.SendToClient<HandshakeSrvMsg>(client, msgData);
        }
    }
}
