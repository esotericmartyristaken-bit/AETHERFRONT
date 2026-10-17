using Lidgren.Network;
using LmpCommon;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Aetherfront
{
    public sealed class AetherfrontEventNoticeMsgData : AetherfrontBaseMsgData
    {
        internal AetherfrontEventNoticeMsgData() { }
        public override AetherfrontMessageType AetherfrontMessageType => AetherfrontMessageType.EventNotice;
        public override string ClassName { get; } = nameof(AetherfrontEventNoticeMsgData);
        public string PayloadJson = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg) { lidgrenMsg.Write(PayloadJson ?? string.Empty); }
        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg) { PayloadJson = lidgrenMsg.ReadString(); }
        internal override int InternalGetMessageSize() { return base.InternalGetMessageSize() + (PayloadJson ?? string.Empty).GetByteCount(); }
    }
}
