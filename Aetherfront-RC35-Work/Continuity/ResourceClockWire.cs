using System.IO;
using Lidgren.Network;

namespace Aetherfront.Continuity
{
    public static class ResourceClockWire
    {
        public static void SerializeResourceClock(object message,NetOutgoingMessage stream)
        {
            byte[] value=ResourceClockPayloads.Get(message);
            stream.Write(value.Length);
            if(value.Length!=0)stream.Write(value,0,value.Length);
        }
        public static void DeserializeResourceClock(object message,NetIncomingMessage stream)
        {
            // Explicitly clear metadata when a pooled message object is reused,
            // including the local legacy-code fixture path with no extension.
            ResourceClockPayloads.Clear(message);
            // Resource FlowState uses bit packing. Compression may also round
            // the last byte up, leaving at most seven unused padding bits.
            long remaining=stream.LengthBits-stream.Position;
            if(remaining>=0 && remaining<=7)return;
            if(remaining<32)throw new InvalidDataException("Truncated resource clock header");
            int length=stream.ReadInt32();
            long payloadBits=stream.LengthBits-stream.Position;
            long trailing=payloadBits-(long)length*8;
            long padding=(8-((stream.Position+(long)length*8)&7))&7;
            // MessageData appends its authenticated shared-world revision after
            // InternalSerialize. Leave that exact aligned Int64 for the native
            // outer reader, which validates padding, range and packet revision.
            bool revisionTail=trailing==padding+64;
            if(length<0 || length>ResourceClockPayloads.MaximumBytes || payloadBits<(long)length*8 ||
               (trailing>7 && !revisionTail))
                throw new InvalidDataException("Resource clock length refused");
            ResourceClockPayloads.Set(message,stream.ReadBytes(length));
        }
        public static int ResourceClockSize(object message) => ResourceClockPayloads.Size(message);
    }
}
