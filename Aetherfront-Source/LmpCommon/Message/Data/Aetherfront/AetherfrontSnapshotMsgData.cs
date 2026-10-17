using Lidgren.Network;
using LmpCommon;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Aetherfront
{
    public sealed class AetherfrontSnapshotMsgData : AetherfrontBaseMsgData
    {
        internal AetherfrontSnapshotMsgData() { }
        public override AetherfrontMessageType AetherfrontMessageType => AetherfrontMessageType.SnapshotReply;
        public override string ClassName { get; } = nameof(AetherfrontSnapshotMsgData);
        public string PayloadJson = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg) { lidgrenMsg.Write(PayloadJson ?? string.Empty); }
        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg) { PayloadJson = lidgrenMsg.ReadString(); }
        internal override int InternalGetMessageSize() { return base.InternalGetMessageSize() + (PayloadJson ?? string.Empty).GetByteCount(); }
    }
}
