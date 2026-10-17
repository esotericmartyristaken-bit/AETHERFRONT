using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client.Base;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Client
{
    public sealed class AetherfrontCliMsg : CliMsgBase<AetherfrontBaseMsgData>
    {
        internal AetherfrontCliMsg() { }
        public override string ClassName { get; } = nameof(AetherfrontCliMsg);
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)AetherfrontMessageType.CommandRequest] = typeof(AetherfrontCommandMsgData)
        };
        public override ClientMessageType MessageType => ClientMessageType.Aetherfront;
        protected override int DefaultChannel => 2;
        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
