using ApfCore;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Server.Apf;
using Server.Client;
using Server.Context;
using Server.Message.Base;
using Server.Server;
using Server.System;

namespace Server.Message
{
    public sealed class ApfPackMsgReader : ReaderBase
    {
        private static readonly HandshakeSystem HandshakeHandler = new HandshakeSystem();

        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var data = message.Data as ApfPackBaseMsgData;
            if (data != null)
            {
                switch (data.ApfPackMessageType)
                {
                    case ApfPackMessageType.PackManifestRequest:
                        SendManifestReply(client, (PackManifestRequestMsgData)data);
                        break;
                    case ApfPackMessageType.PackStatusReport:
                        HandshakeHandler.HandlePackStatusReport(client, (PackStatusReportMsgData)data);
                        break;
                    case ApfPackMessageType.PackSyncAccepted:
                        // Consent is recorded client-side; the server still requires a later clean status report.
                        break;
                }
            }

            message.Recycle();
        }

        private static void SendManifestReply(ClientStructure client, PackManifestRequestMsgData request)
        {
            if (!ApfServerSettings.Enabled || client.PendingApfHandshake == null)
            {
                ClientConnectionHandler.DisconnectClient(client, "APF pack manifest request is not allowed");
                return;
            }

            if (!string.Equals(request.ServerId, ApfServerSettings.Manifest.ServerId, global::System.StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.PackId, ApfServerSettings.Manifest.PackId, global::System.StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.PackVersion, ApfServerSettings.Manifest.PackVersion, global::System.StringComparison.Ordinal))
            {
                SendSyncRequired(client, "APF manifest request does not match the current server pack");
                return;
            }

            if (!string.IsNullOrWhiteSpace(ApfServerSettings.Manifest.RequiredClientVersion) &&
                !string.Equals(request.ClientVersion, ApfServerSettings.Manifest.RequiredClientVersion, global::System.StringComparison.Ordinal))
            {
                ClientConnectionHandler.DisconnectClient(client, "APF client version is not supported");
                return;
            }

            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<PackManifestReplyMsgData>();
            data.ServerId = ApfServerSettings.Manifest.ServerId;
            data.PackId = ApfServerSettings.Manifest.PackId;
            data.PackVersion = ApfServerSettings.Manifest.PackVersion;
            data.ManifestUrl = ApfServerSettings.ManifestUrl;
            data.RequiredClientVersion = ApfServerSettings.Manifest.RequiredClientVersion;
            MessageQueuer.SendToClient<ApfPackSrvMsg>(client, data);
        }

        private static void SendSyncRequired(ClientStructure client, string reason)
        {
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<PackSyncRequiredMsgData>();
            data.ServerId = ApfServerSettings.Manifest.ServerId;
            data.PackId = ApfServerSettings.Manifest.PackId;
            data.PackVersion = ApfServerSettings.Manifest.PackVersion;
            data.ManifestUrl = ApfServerSettings.ManifestUrl;
            data.Reason = reason ?? "APF pack synchronization required";
            MessageQueuer.SendToClient<ApfPackSrvMsg>(client, data);
        }
    }
}
