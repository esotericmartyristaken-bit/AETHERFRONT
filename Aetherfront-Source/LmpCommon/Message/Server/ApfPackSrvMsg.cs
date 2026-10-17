using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Server.Base;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Server
{
    public sealed class ApfPackSrvMsg : SrvMsgBase<ApfPackBaseMsgData>
    {
        internal ApfPackSrvMsg() { }
        public override string ClassName { get; } = nameof(ApfPackSrvMsg);
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)ApfPackMessageType.PackManifestReply] = typeof(PackManifestReplyMsgData),
            [(ushort)ApfPackMessageType.PackSyncRequired] = typeof(PackSyncRequiredMsgData)
        };
        public override ServerMessageType MessageType => ServerMessageType.ApfPack;
        protected override int DefaultChannel => 1;
        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
