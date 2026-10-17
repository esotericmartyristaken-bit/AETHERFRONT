using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Apf
{
    public sealed class PackManifestRequestMsgData : ApfPackBaseMsgData
    {
        internal PackManifestRequestMsgData() { }
        public override ApfPackMessageType ApfPackMessageType => ApfPackMessageType.PackManifestRequest;
        public override string ClassName { get; } = nameof(PackManifestRequestMsgData);

        public string ServerId = string.Empty;
        public string PackId = string.Empty;
        public string PackVersion = string.Empty;
        public string ClientVersion = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(ServerId);
            lidgrenMsg.Write(PackId);
            lidgrenMsg.Write(PackVersion);
            lidgrenMsg.Write(ClientVersion);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            ServerId = lidgrenMsg.ReadString();
            PackId = lidgrenMsg.ReadString();
            PackVersion = lidgrenMsg.ReadString();
            ClientVersion = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + ServerId.GetByteCount() + PackId.GetByteCount() + PackVersion.GetByteCount() + ClientVersion.GetByteCount();
        }
    }
}
