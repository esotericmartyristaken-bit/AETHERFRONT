using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Server.Base;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Server
{
    public sealed class AetherfrontSrvMsg : SrvMsgBase<AetherfrontBaseMsgData>
    {
        internal AetherfrontSrvMsg() { }
        public override string ClassName { get; } = nameof(AetherfrontSrvMsg);
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)AetherfrontMessageType.SnapshotReply] = typeof(AetherfrontSnapshotMsgData),
            [(ushort)AetherfrontMessageType.CommandReply] = typeof(AetherfrontCommandReplyMsgData),
            [(ushort)AetherfrontMessageType.EventNotice] = typeof(AetherfrontEventNoticeMsgData)
        };
        public override ServerMessageType MessageType => ServerMessageType.Aetherfront;
        protected override int DefaultChannel => 2;
        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
