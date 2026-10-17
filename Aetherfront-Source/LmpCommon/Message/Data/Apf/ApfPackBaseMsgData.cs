using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Apf
{
    public abstract class ApfPackBaseMsgData : MessageData
    {
        internal ApfPackBaseMsgData() { }

        public override ushort SubType => (ushort)ApfPackMessageType;
        public virtual ApfPackMessageType ApfPackMessageType => throw new NotImplementedException();

        internal override void InternalSerialize(Lidgren.Network.NetOutgoingMessage lidgrenMsg) { }
        internal override void InternalDeserialize(Lidgren.Network.NetIncomingMessage lidgrenMsg) { }
        internal override int InternalGetMessageSize() => 0;
    }
}
