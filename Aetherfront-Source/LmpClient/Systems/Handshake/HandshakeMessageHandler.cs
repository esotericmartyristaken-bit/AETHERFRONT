using System;
using System.Collections.Concurrent;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.Mod;
using LmpClient.Systems.TimeSync;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using LmpCommon.ModFile;
using LmpClient.Apf;

namespace LmpClient.Systems.Handshake
{
    public class HandshakeMessageHandler : SubSystem<HandshakeSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is HandshakeBaseMsgData msgData)) return;

            switch (msgData.HandshakeMessageType)
            {
                case HandshakeMessageType.Reply:
                    HandleHandshakeReplyReceivedMessage((HandshakeReplyMsgData)msgData);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        #region Private

        public void HandleHandshakeReplyReceivedMessage(HandshakeReplyMsgData data)
        {
            TimeSyncSystem.ServerStartTime = data.ServerStartTime;

            switch (data.Response)
            {
                case HandshakeReply.HandshookSuccessfully:
                    if (ApfClientSettings.Enabled && !data.ApfEnabled)
                    {
                        NetworkConnection.Disconnect("APF client can only connect to APF servers");
                        return;
                    }
                    if (!data.ApfEnabled)
                    {
                        ApfCareerSession.Reset();
                    }
                    if (data.ApfEnabled)
                    {
                        if (!ApfCareerSession.Apply(data.ApfCareerProfileId, data.ApfCareerProfileJson, data.ApfPackId, data.ApfPackVersion, out var careerError))
                        {
                            LunaLog.LogError("[APF]: Career profile validation failed: " + careerError);
                            NetworkConnection.Disconnect("APF career profile validation failed");
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(data.ApfCareerProfileId))
                            LunaLog.Log("[APF]: Server career profile " + data.ApfCareerProfileId + "; local save folder " + ApfCareerSession.SaveFolder);
                        ApfSyncCoordinator.Begin(data, () => CompleteHandshakeAfterApf(data));
                        return;
                    }
                    CompleteHandshakeAfterApf(data);
                    break;
                case HandshakeReply.ApfPackRequired:
                    ApfProtocolSystem.RequestManifest(data);
                    break;
                case HandshakeReply.ApfClientRequired:
                    NetworkConnection.Disconnect("This server requires the APF client");
                    break;
                case HandshakeReply.ApfServerMisconfigured:
                    NetworkConnection.Disconnect("APF server configuration invalid: " + data.Reason);
                    break;
                default:
                    var disconnectReason = $"Handshake failure: {data.Reason}";
                    LunaLog.Log(disconnectReason);
                    NetworkConnection.Disconnect(disconnectReason);
                    break;
            }
        }

        public void CompleteHandshakeAfterApf(HandshakeReplyMsgData data)
        {
            try
            {
                ModSystem.Singleton.Clear();
                ModSystem.Singleton.ModControl = data.ModControl;
                if (ModSystem.Singleton.ModControl)
                {
                    if (ModSystem.Singleton.ModFileHandler.ParseModFile(ModFileParser.ReadModFileFromString(data.ModFileData)))
                    {
                        LunaLog.Log("[LMP]: Handshake successful");
                        MainSystem.NetworkState = ClientState.Handshaked;
                    }
                    else
                    {
                        LunaLog.LogError("[LMP]: Failed to pass mod validation");
                        NetworkConnection.Disconnect("[LMP]: Failed mod validation");
                    }
                }
                else
                {
                    LunaLog.Log("[LMP]: Handshake successful");
                    MainSystem.NetworkState = ClientState.Handshaked;
                }
            }
            catch (Exception exception)
            {
                LunaLog.LogError("[APF]: Failed to complete handshake: " + exception);
                NetworkConnection.Disconnect("APF handshake validation failed");
            }
        }

        #endregion
    }
}
