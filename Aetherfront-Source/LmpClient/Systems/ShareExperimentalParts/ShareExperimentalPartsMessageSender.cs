using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Interface;

namespace LmpClient.Systems.ShareExperimentalParts
{
    public class ShareExperimentalPartsMessageSender : SubSystem<ShareExperimentalPartsSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            if (LmpClient.Apf.ApfClientSettings.Enabled) return;
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<ShareProgressCliMsg>(msg)));
        }

        public void SendExperimentalPartMessage(string partName, int count)
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<ShareProgressExperimentalPartMsgData>();
            msgData.PartName = partName;
            msgData.Count = count;

            SendMessage(msgData);
        }
    }
}
