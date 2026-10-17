using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Apf
{
    public sealed class PackSyncAcceptedMsgData : ApfPackBaseMsgData
    {
        internal PackSyncAcceptedMsgData() { }
        public override ApfPackMessageType ApfPackMessageType => ApfPackMessageType.PackSyncAccepted;
        public override string ClassName { get; } = nameof(PackSyncAcceptedMsgData);

        public string PackId = string.Empty;
        public string PackVersion = string.Empty;
        public bool Accepted;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(PackId);
            lidgrenMsg.Write(PackVersion);
            lidgrenMsg.Write(Accepted);
            lidgrenMsg.WritePadBits();
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            PackId = lidgrenMsg.ReadString();
            PackVersion = lidgrenMsg.ReadString();
            Accepted = lidgrenMsg.ReadBoolean();
            lidgrenMsg.SkipPadBits();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PackId.GetByteCount() + PackVersion.GetByteCount() + sizeof(byte);
        }
    }
}
