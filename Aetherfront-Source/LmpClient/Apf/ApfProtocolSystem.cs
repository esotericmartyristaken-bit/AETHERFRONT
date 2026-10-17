using ApfCore;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;

namespace LmpClient.Apf
{
    public static class ApfProtocolSystem
    {
        public static void RequestManifest(HandshakeReplyMsgData handshake)
        {
            if (!ApfSyncCoordinator.PrepareForManifestRequest()) return;
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<PackManifestRequestMsgData>();
            data.ServerId = handshake.ApfServerId;
            data.PackId = handshake.ApfPackId;
            data.PackVersion = handshake.ApfPackVersion;
            data.ClientVersion = ApfClientSettings.ClientVersion;
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<ApfPackCliMsg>(data));
        }

        public static void SendStatusReport(ApfPackManifest manifest, ApfPackStatus status, string detail)
        {
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<PackStatusReportMsgData>();
            data.PackId = manifest.PackId;
            data.PackVersion = manifest.PackVersion;
            data.Status = (int)status;
            data.Detail = detail ?? string.Empty;
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<ApfPackCliMsg>(data));
        }

        public static void SendSyncAccepted(ApfPackManifest manifest)
        {
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<PackSyncAcceptedMsgData>();
            data.PackId = manifest.PackId;
            data.PackVersion = manifest.PackVersion;
            data.Accepted = true;
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<ApfPackCliMsg>(data));
        }

        public static void HandleMessage(IServerMessageBase message)
        {
            if (!(message.Data is ApfPackBaseMsgData data)) return;

            switch (data.ApfPackMessageType)
            {
                case ApfPackMessageType.PackManifestReply:
                    var reply = (PackManifestReplyMsgData)data;
                    ApfSyncCoordinator.BeginFromProtocol(reply.ServerId, reply.PackId, reply.PackVersion, reply.ManifestUrl, reply.RequiredClientVersion);
                    break;
                case ApfPackMessageType.PackSyncRequired:
                    var required = (PackSyncRequiredMsgData)data;
                    ApfSyncCoordinator.BeginFromProtocol(required.ServerId, required.PackId, required.PackVersion, required.ManifestUrl, string.Empty);
                    break;
            }
        }
    }
}
