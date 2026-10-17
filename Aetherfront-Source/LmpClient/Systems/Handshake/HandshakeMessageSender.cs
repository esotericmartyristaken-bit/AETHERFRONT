using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using LmpClient.Apf;

namespace LmpClient.Systems.Handshake
{
    public class HandshakeMessageSender : SubSystem<HandshakeSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<HandshakeCliMsg>(msg)));
        }

        public void SendHandshakeRequest()
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<HandshakeRequestMsgData>();
            msgData.PlayerName = SettingsSystem.CurrentSettings.PlayerName;
            msgData.UniqueIdentifier = MainSystem.UniqueIdentifier;
            msgData.KspVersion = $"{CompatibilityChecker.KspVersion}";
            msgData.ApfEnabled = ApfClientSettings.Enabled;
            msgData.ApfClientVersion = ApfClientSettings.ClientVersion;
            var state = ApfClientSettings.LoadState();
            msgData.ApfPackId = state.PackId ?? string.Empty;
            msgData.ApfPackVersion = state.PackVersion ?? string.Empty;

            SendMessage(msgData);
        }
    }
}
