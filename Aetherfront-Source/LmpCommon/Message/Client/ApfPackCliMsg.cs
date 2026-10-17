using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client.Base;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Client
{
    public sealed class ApfPackCliMsg : CliMsgBase<ApfPackBaseMsgData>
    {
        internal ApfPackCliMsg() { }
        public override string ClassName { get; } = nameof(ApfPackCliMsg);
        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)ApfPackMessageType.PackManifestRequest] = typeof(PackManifestRequestMsgData),
            [(ushort)ApfPackMessageType.PackStatusReport] = typeof(PackStatusReportMsgData),
            [(ushort)ApfPackMessageType.PackSyncAccepted] = typeof(PackSyncAcceptedMsgData)
        };
        public override ClientMessageType MessageType => ClientMessageType.ApfPack;
        protected override int DefaultChannel => 1;
        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
