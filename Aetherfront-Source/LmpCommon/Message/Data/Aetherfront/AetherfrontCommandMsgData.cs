using Lidgren.Network;
using LmpCommon;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Aetherfront
{
    public sealed class AetherfrontCommandMsgData : AetherfrontBaseMsgData
    {
        internal AetherfrontCommandMsgData() { }
        public override AetherfrontMessageType AetherfrontMessageType => AetherfrontMessageType.CommandRequest;
        public override string ClassName { get; } = nameof(AetherfrontCommandMsgData);
        public string PayloadJson = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(PayloadJson ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            PayloadJson = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + (PayloadJson ?? string.Empty).GetByteCount();
        }
    }
}
