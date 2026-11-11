using System;
using System.Reflection;
using Lidgren.Network;
using LmpCommon.Message.Data.Warp;

namespace Aetherfront.Synchronization
{
    // The graft adds these two Int64 fields to both shipping wire implementations.
    // Keeping access here checked also makes an accidental old-assembly build fail closed.
    public static class BoundaryWire
    {
        static readonly FieldInfo SessionField = Required("BoundarySession");
        static readonly FieldInfo RequestField = Required("BoundaryRequest");
        static FieldInfo Required(string name) => typeof(WarpNewSubspaceMsgData).GetField(name)
            ?? throw new MissingFieldException(typeof(WarpNewSubspaceMsgData).FullName, name);
        public static long Session(WarpNewSubspaceMsgData data) => (long)SessionField.GetValue(data);
        public static long Request(WarpNewSubspaceMsgData data) => (long)RequestField.GetValue(data);
        public static void Set(WarpNewSubspaceMsgData data, long session, long request)
        { SessionField.SetValue(data, session); RequestField.SetValue(data, request); }
        public static void Write(WarpNewSubspaceMsgData data, NetOutgoingMessage message)
        { message.Write(Session(data)); message.Write(Request(data)); }
        public static void Read(WarpNewSubspaceMsgData data, NetIncomingMessage message)
        { Set(data, message.ReadInt64(), message.ReadInt64()); }
    }
}
