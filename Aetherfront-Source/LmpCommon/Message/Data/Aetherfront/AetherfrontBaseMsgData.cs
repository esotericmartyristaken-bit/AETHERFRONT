using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Aetherfront
{
    public abstract class AetherfrontBaseMsgData : MessageData
    {
        internal AetherfrontBaseMsgData() { }

        public override ushort SubType => (ushort)AetherfrontMessageType;
        public virtual AetherfrontMessageType AetherfrontMessageType => throw new NotImplementedException();

        internal override void InternalSerialize(Lidgren.Network.NetOutgoingMessage lidgrenMsg) { }
        internal override void InternalDeserialize(Lidgren.Network.NetIncomingMessage lidgrenMsg) { }
        internal override int InternalGetMessageSize() => 0;
    }
}
