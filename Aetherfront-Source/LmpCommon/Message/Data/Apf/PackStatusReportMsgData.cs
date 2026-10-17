using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Apf
{
    public sealed class PackStatusReportMsgData : ApfPackBaseMsgData
    {
        internal PackStatusReportMsgData() { }
        public override ApfPackMessageType ApfPackMessageType => ApfPackMessageType.PackStatusReport;
        public override string ClassName { get; } = nameof(PackStatusReportMsgData);

        public string PackId = string.Empty;
        public string PackVersion = string.Empty;
        public int Status;
        public string Detail = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(PackId);
            lidgrenMsg.Write(PackVersion);
            lidgrenMsg.Write((int)Status);
            lidgrenMsg.Write(Detail);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            PackId = lidgrenMsg.ReadString();
            PackVersion = lidgrenMsg.ReadString();
            Status = lidgrenMsg.ReadInt32();
            Detail = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PackId.GetByteCount() + PackVersion.GetByteCount() + sizeof(int) + Detail.GetByteCount();
        }
    }
}
