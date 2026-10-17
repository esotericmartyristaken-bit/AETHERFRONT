using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Handshake
{
    public class HandshakeRequestMsgData : HandshakeBaseMsgData
    {
        /// <inheritdoc />
        internal HandshakeRequestMsgData() { }
        public override HandshakeMessageType HandshakeMessageType => HandshakeMessageType.Request;

        public string PlayerName;
        public string UniqueIdentifier;
        public string KspVersion;
        public bool ApfEnabled;
        public string ApfClientVersion = string.Empty;
        public string ApfPackId = string.Empty;
        public string ApfPackVersion = string.Empty;

        public override string ClassName { get; } = nameof(HandshakeRequestMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(PlayerName);
            lidgrenMsg.Write(UniqueIdentifier);
            lidgrenMsg.Write(KspVersion);
            lidgrenMsg.Write(ApfEnabled);
            lidgrenMsg.WritePadBits();
            lidgrenMsg.Write(ApfClientVersion);
            lidgrenMsg.Write(ApfPackId);
            lidgrenMsg.Write(ApfPackVersion);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            ApfEnabled = false;
            ApfClientVersion = string.Empty;
            ApfPackId = string.Empty;
            ApfPackVersion = string.Empty;

            PlayerName = lidgrenMsg.ReadString();
            UniqueIdentifier = lidgrenMsg.ReadString();

            //  For backwards compatibility with 0.29.x, only continue reading if there are more bytes to read
            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
                KspVersion = lidgrenMsg.ReadString();
            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                ApfEnabled = lidgrenMsg.ReadBoolean();
                lidgrenMsg.SkipPadBits();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfClientVersion = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfPackId = lidgrenMsg.ReadString();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits) ApfPackVersion = lidgrenMsg.ReadString();
            }
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PlayerName.GetByteCount() + UniqueIdentifier.GetByteCount() + KspVersion.GetByteCount() + sizeof(byte) +
                   ApfClientVersion.GetByteCount() + ApfPackId.GetByteCount() + ApfPackVersion.GetByteCount();
        }
    }
}
