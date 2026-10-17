using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Handshake
{
    public class HandshakeReplyMsgData : HandshakeBaseMsgData
    {
        /// <inheritdoc />
        internal HandshakeReplyMsgData() { }
        public override HandshakeMessageType HandshakeMessageType => HandshakeMessageType.Reply;

        public HandshakeReply Response;
        public string Reason;
        public bool ModControl;
        public long ServerStartTime;
        public string ModFileData;
        public bool ApfEnabled;
        public string ApfServerId = string.Empty;
        public string ApfPackId = string.Empty;
        public string ApfPackVersion = string.Empty;
        public string ApfManifestUrl = string.Empty;
        public string ApfRequiredClientVersion = string.Empty;
        public string ApfCareerProfileId = string.Empty;
        public string ApfCareerProfileJson = string.Empty;

        public override string ClassName { get; } = nameof(HandshakeReplyMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write((int)Response);
            lidgrenMsg.Write(Reason);

            lidgrenMsg.Write(ModControl);
            lidgrenMsg.WritePadBits();

            lidgrenMsg.Write(ServerStartTime);
            lidgrenMsg.Write(ModFileData);
            // Preserve the exact stock 0.29.2 wire payload when APF mode is off.
            // Vanilla clients do not know about these trailing fields.
            if (ApfEnabled)
            {
                lidgrenMsg.Write(true);
                lidgrenMsg.WritePadBits();
                lidgrenMsg.Write(ApfServerId);
                lidgrenMsg.Write(ApfPackId);
                lidgrenMsg.Write(ApfPackVersion);
                lidgrenMsg.Write(ApfManifestUrl);
                lidgrenMsg.Write(ApfRequiredClientVersion);
                lidgrenMsg.Write(ApfCareerProfileId);
                lidgrenMsg.Write(ApfCareerProfileJson);
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
            ApfCareerProfileId = string.Empty;
            ApfCareerProfileJson = string.Empty;

            Response = (HandshakeReply)lidgrenMsg.ReadInt32();
            Reason = lidgrenMsg.ReadString();

            ModControl = lidgrenMsg.ReadBoolean();
            lidgrenMsg.SkipPadBits();

            ServerStartTime = lidgrenMsg.ReadInt64();

            ModFileData = lidgrenMsg.ReadString();
            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                ApfEnabled = lidgrenMsg.ReadBoolean();
                lidgrenMsg.SkipPadBits();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfServerId = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfPackId = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfPackVersion = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfManifestUrl = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfRequiredClientVersion = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfCareerProfileId = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfCareerProfileJson = lidgrenMsg.ReadString();
            }
        }

        internal override int InternalGetMessageSize()
        {
            var size = base.InternalGetMessageSize() + sizeof(HandshakeReply) + Reason.GetByteCount() + sizeof(byte) //We write pad bits so it's size of byte
                + sizeof(long) + ModFileData.GetByteCount();
            if (ApfEnabled)
                size += sizeof(byte) + ApfServerId.GetByteCount() + ApfPackId.GetByteCount() + ApfPackVersion.GetByteCount() +
                        ApfManifestUrl.GetByteCount() + ApfRequiredClientVersion.GetByteCount() + ApfCareerProfileId.GetByteCount() + ApfCareerProfileJson.GetByteCount();
            return size;
        }
    }
}
