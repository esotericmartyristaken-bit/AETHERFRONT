using ApfCore;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.PlayerConnection;
using LmpCommon.Message.Server;
using Server.Apf;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Plugin;
using Server.Server;

namespace Server.System
{
    public partial class HandshakeSystem
    {
        public void HandleHandshakeRequest(ClientStructure client, HandshakeRequestMsgData data)
        {
            if (ApfServerSettings.HasPack && !ApfServerSettings.Enabled)
            {
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.ApfServerMisconfigured, ApfServerSettings.Error);
                client.DisconnectClient = true;
                ClientConnectionHandler.DisconnectClient(client, ApfServerSettings.Error);
                return;
            }

            if (ApfServerSettings.Enabled && !data.ApfEnabled)
            {
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.ApfClientRequired, "This server requires the APF client");
                client.DisconnectClient = true;
                ClientConnectionHandler.DisconnectClient(client, "APF client required");
                return;
            }

            if (ApfServerSettings.Enabled && !string.IsNullOrWhiteSpace(ApfServerSettings.Manifest.RequiredClientVersion) &&
                !string.Equals(data.ApfClientVersion, ApfServerSettings.Manifest.RequiredClientVersion, global::System.StringComparison.Ordinal))
            {
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.ApfClientRequired, "APF client version is not supported");
                client.DisconnectClient = true;
                ClientConnectionHandler.DisconnectClient(client, "APF client version is not supported");
                return;
            }

            if (ApfServerSettings.Enabled)
            {
                client.PendingApfHandshake = new ApfPendingHandshake
                {
                    PlayerName = data.PlayerName,
                    UniqueIdentifier = data.UniqueIdentifier,
                    KspVersion = data.KspVersion,
                    MajorVersion = data.MajorVersion,
                    MinorVersion = data.MinorVersion,
                    BuildVersion = data.BuildVersion
                };
                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.ApfPackRequired, "APF pack status required");
                return;
            }

            CompleteHandshake(client, data.PlayerName, data.UniqueIdentifier, data.KspVersion,
                data.MajorVersion, data.MinorVersion, data.BuildVersion);
        }

        public void HandlePackStatusReport(ClientStructure client, PackStatusReportMsgData data)
        {
            if (!ApfServerSettings.Enabled || client.PendingApfHandshake == null)
            {
                ClientConnectionHandler.DisconnectClient(client, "Unexpected APF pack status");
                return;
            }

            if (data.Status != (int)ApfPackStatus.Clean ||
                !string.Equals(data.PackId, ApfServerSettings.Manifest.PackId, global::System.StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(data.PackVersion, ApfServerSettings.Manifest.PackVersion, global::System.StringComparison.Ordinal))
            {
                var syncRequired = ServerContext.ServerMessageFactory.CreateNewMessageData<PackSyncRequiredMsgData>();
                syncRequired.ServerId = ApfServerSettings.Manifest.ServerId;
                syncRequired.PackId = ApfServerSettings.Manifest.PackId;
                syncRequired.PackVersion = ApfServerSettings.Manifest.PackVersion;
                syncRequired.ManifestUrl = ApfServerSettings.ManifestUrl;
                syncRequired.Reason = string.IsNullOrWhiteSpace(data.Detail)
                    ? "APF pack synchronization required"
                    : data.Detail;
                MessageQueuer.SendToClient<ApfPackSrvMsg>(client, syncRequired);
                return;
            }

            var pending = client.PendingApfHandshake;
            client.PendingApfHandshake = null;
            CompleteHandshake(client, pending.PlayerName, pending.UniqueIdentifier, pending.KspVersion,
                pending.MajorVersion, pending.MinorVersion, pending.BuildVersion);
        }

        private void CompleteHandshake(ClientStructure client, string playerName, string uniqueIdentifier, string kspVersion,
            ushort majorVersion, ushort minorVersion, ushort buildVersion)
        {
            var valid = CheckServerFull(client, out var reason);
            valid &= valid && CheckUsernameLength(client, playerName, out reason);
            valid &= valid && CheckUsernameCharacters(client, playerName, out reason);
            valid &= valid && CheckPlayerIsAlreadyConnected(client, playerName, out reason);
            valid &= valid && CheckUsernameIsReserved(client, playerName, out reason);
            valid &= valid && CheckPlayerIsBanned(client, uniqueIdentifier, out reason);

            if (!valid)
            {
                LunaLog.Normal($"Client {playerName} ({uniqueIdentifier}) failed to handshake: {reason}. Disconnecting");
                client.DisconnectClient = true;
                ClientConnectionHandler.DisconnectClient(client, reason);
            }
            else
            {
                client.PlayerName = playerName;
                client.UniqueIdentifier = uniqueIdentifier;
                client.KspVersion = string.IsNullOrWhiteSpace(kspVersion) ? "Unknown" : kspVersion;
                client.Authenticated = true;

                if (AetherfrontServerSystem.Enabled && !AetherfrontServerSystem.BindSession(client))
                {
                    client.Authenticated = false;
                    client.DisconnectClient = true;
                    ClientConnectionHandler.DisconnectClient(client, "Aetherfront account binding failed");
                    return;
                }

                LmpPluginHandler.FireOnClientAuthenticated(client);

                LunaLog.Normal($"Client {playerName} ({uniqueIdentifier}) handshake successful, LMP Version: {majorVersion}.{minorVersion}.{buildVersion}, KSP Version: {client.KspVersion}");

                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.HandshookSuccessfully, "success");

                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<PlayerConnectionJoinMsgData>();
                msgData.PlayerName = client.PlayerName;
                MessageQueuer.RelayMessage<PlayerConnectionSrvMsg>(client, msgData);

                LunaLog.Debug($"Online Players: {ServerContext.PlayerCount}, connected: {ClientRetriever.GetClients().Length}");
            }
        }
    }
}
