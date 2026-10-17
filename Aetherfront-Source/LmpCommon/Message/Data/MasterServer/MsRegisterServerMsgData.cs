using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System.Net;

namespace LmpCommon.Message.Data.MasterServer
{
    public class MsRegisterServerMsgData : MsBaseMsgData
    {
        /// <inheritdoc />
        internal MsRegisterServerMsgData() { }
        public override MasterServerMessageSubType MasterServerMessageSubType => MasterServerMessageSubType.RegisterServer;

        public long Id;
        public string ServerVersion;
        public IPEndPoint InternalEndpoint;
        public IPEndPoint InternalEndpoint6;
        public bool Password;
        public bool Cheats;
        public bool ModControl;
        public int GameMode;
        public int MaxPlayers;
        public int PlayerCount;
        public string ServerName;
        public string Description;
        public string CountryCode;
        public string Website;
        public string WebsiteText;
        public int WarpMode;
        public int TerrainQuality;
        public int VesselPositionUpdatesMsInterval;
        public int SecondaryVesselPositionUpdatesMsInterval;
        public bool RainbowEffect;
        public byte[] Color = new byte[3];
        public bool ApfEnabled;
        public string ApfServerId = string.Empty;
        public string ApfPackId = string.Empty;
        public string ApfPackVersion = string.Empty;
        public string ApfManifestUrl = string.Empty;
        public string ApfRequiredClientVersion = string.Empty;

        public override string ClassName { get; } = nameof(MsRegisterServerMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(Id);
            lidgrenMsg.Write(ServerVersion);
            lidgrenMsg.Write(InternalEndpoint);
            lidgrenMsg.Write(InternalEndpoint6);

            lidgrenMsg.Write(Password);
            lidgrenMsg.Write(Cheats);
            lidgrenMsg.Write(ModControl);
            lidgrenMsg.WritePadBits();

            lidgrenMsg.Write(GameMode);
            lidgrenMsg.Write(MaxPlayers);
            lidgrenMsg.Write(PlayerCount);
            lidgrenMsg.Write(ServerName);
            lidgrenMsg.Write(Description);
            lidgrenMsg.Write(CountryCode);
            lidgrenMsg.Write(Website);
            lidgrenMsg.Write(WebsiteText);
            lidgrenMsg.Write(WarpMode);
            lidgrenMsg.Write(TerrainQuality);
            lidgrenMsg.Write(VesselPositionUpdatesMsInterval);
            lidgrenMsg.Write(SecondaryVesselPositionUpdatesMsInterval);

            lidgrenMsg.Write(RainbowEffect);
            for (var i = 0; i < 3; i++)
                lidgrenMsg.Write(Color[i]);

            // Standard LMP masters expect the stock payload length. APF metadata is
            // appended only for an explicitly configured APF server/private master.
            if (ApfEnabled)
            {
                lidgrenMsg.Write(true);
                lidgrenMsg.WritePadBits();
                lidgrenMsg.Write(ApfServerId);
                lidgrenMsg.Write(ApfPackId);
                lidgrenMsg.Write(ApfPackVersion);
                lidgrenMsg.Write(ApfManifestUrl);
                lidgrenMsg.Write(ApfRequiredClientVersion);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            ApfEnabled = false;
            ApfServerId = string.Empty;
            ApfPackId = string.Empty;
            ApfPackVersion = string.Empty;
            ApfManifestUrl = string.Empty;
            ApfRequiredClientVersion = string.Empty;

            Id = lidgrenMsg.ReadInt64();
            ServerVersion = lidgrenMsg.ReadString();
            InternalEndpoint = lidgrenMsg.ReadIPEndPoint();
            // ReadIPEndPoint supports IPv6 addresses despite saying otherwise in the code doc.
            InternalEndpoint6 = lidgrenMsg.ReadIPEndPoint();

            Password = lidgrenMsg.ReadBoolean();
            Cheats = lidgrenMsg.ReadBoolean();
            ModControl = lidgrenMsg.ReadBoolean();
            lidgrenMsg.SkipPadBits();

            GameMode = lidgrenMsg.ReadInt32();
            MaxPlayers = lidgrenMsg.ReadInt32();
            PlayerCount = lidgrenMsg.ReadInt32();
            ServerName = lidgrenMsg.ReadString();
            Description = lidgrenMsg.ReadString();
            CountryCode = lidgrenMsg.ReadString();
            Website = lidgrenMsg.ReadString();
            WebsiteText = lidgrenMsg.ReadString();
            WarpMode = lidgrenMsg.ReadInt32();
            TerrainQuality = lidgrenMsg.ReadInt32();
            VesselPositionUpdatesMsInterval = lidgrenMsg.ReadInt32();
            SecondaryVesselPositionUpdatesMsInterval = lidgrenMsg.ReadInt32();

            RainbowEffect = lidgrenMsg.ReadBoolean();
            for (var i = 0; i < 3; i++)
                Color[i] = lidgrenMsg.ReadByte();

            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                ApfEnabled = lidgrenMsg.ReadBoolean();
                lidgrenMsg.SkipPadBits();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfServerId = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfPackId = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfPackVersion = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfManifestUrl = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfRequiredClientVersion = lidgrenMsg.ReadString();
            }
        }

        internal override int InternalGetMessageSize()
        {
            //We use sizeof(byte) instead of sizeof(bool) because we use the WritePadBits()
            var size = base.InternalGetMessageSize() + sizeof(long) + ServerVersion.GetByteCount() +
                   InternalEndpoint.GetByteCount() + InternalEndpoint6.GetByteCount() + sizeof(byte) +
                   sizeof(int) * 7 + ServerName.GetByteCount() + Description.GetByteCount() +
                   CountryCode.GetByteCount() + Website.GetByteCount() + WebsiteText.GetByteCount() + sizeof(bool) * 3;
            if (ApfEnabled)
                size += sizeof(byte) + ApfServerId.GetByteCount() + ApfPackId.GetByteCount() + ApfPackVersion.GetByteCount() +
                        ApfManifestUrl.GetByteCount() + ApfRequiredClientVersion.GetByteCount();
            return size;
        }
    }
}
