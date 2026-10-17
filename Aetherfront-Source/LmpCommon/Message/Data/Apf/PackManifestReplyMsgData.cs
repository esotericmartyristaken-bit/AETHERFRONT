using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Apf
{
    public sealed class PackManifestReplyMsgData : ApfPackBaseMsgData
    {
        internal PackManifestReplyMsgData() { }
        public override ApfPackMessageType ApfPackMessageType => ApfPackMessageType.PackManifestReply;
        public override string ClassName { get; } = nameof(PackManifestReplyMsgData);

        public string ServerId = string.Empty;
        public string PackId = string.Empty;
        public string PackVersion = string.Empty;
        public string ManifestUrl = string.Empty;
        public string RequiredClientVersion = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(ServerId);
            lidgrenMsg.Write(PackId);
            lidgrenMsg.Write(PackVersion);
            lidgrenMsg.Write(ManifestUrl);
            lidgrenMsg.Write(RequiredClientVersion);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            ServerId = lidgrenMsg.ReadString();
            PackId = lidgrenMsg.ReadString();
            PackVersion = lidgrenMsg.ReadString();
            ManifestUrl = lidgrenMsg.ReadString();
            RequiredClientVersion = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + ServerId.GetByteCount() + PackId.GetByteCount() + PackVersion.GetByteCount() + ManifestUrl.GetByteCount() + RequiredClientVersion.GetByteCount();
        }
    }
}
