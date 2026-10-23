using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Lidgren.Network;
using Aetherfront.Continuity;

internal static class Program
{
    static int checks;
    static void Check(bool test,string name){checks++;if(!test)throw new Exception(name);}
    static NetOutgoingMessage Outgoing()
    {
        var message=(NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage),true);
        typeof(NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(message,new byte[16]);
        return message;
    }
    static NetIncomingMessage Incoming(NetOutgoingMessage source,bool rounded)
    {
        var message=(NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage),true);
        typeof(NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(message,(byte[])source.Data.Clone());
        message.LengthBits=rounded?source.LengthBytes*8:source.LengthBits;
        return message;
    }
    static void Main()
    {
        byte[] payload=Encoding.UTF8.GetBytes("SurvivalClock\n{\n APF_SURVIVAL\n {\n schema = 1\n owner = Test\n ownerUT = 1234.5\n survivalUT = 5678.9\n }\n}\n");
        foreach(int alignment in Enumerable.Range(0,8))foreach(bool rounded in new[]{false,true})foreach(bool empty in new[]{false,true})
        {
            var key=new object();if(!empty)ResourceClockPayloads.Set(key,payload);
            var write=Outgoing();for(int i=0;i<alignment;i++)write.Write((i&1)!=0);
            ResourceClockWire.SerializeResourceClock(key,write);
            Check(write.LengthBits==alignment+ResourceClockPayloads.Size(key)*8,"exact serializer bit count");
            var read=Incoming(write,rounded);for(int i=0;i<alignment;i++)read.ReadBoolean();
            var result=new object();ResourceClockWire.DeserializeResourceClock(result,read);
            Check(ResourceClockPayloads.Get(result).SequenceEqual(empty?new byte[0]:payload),"bit-aligned and compressed-byte-rounded round trip");
            var legacy=Outgoing();for(int i=0;i<alignment;i++)legacy.Write(true);
            var old=Incoming(legacy,rounded);for(int i=0;i<alignment;i++)old.ReadBoolean();
            ResourceClockWire.DeserializeResourceClock(result,old);
            Check(ResourceClockPayloads.Get(result).Length==0,"legacy packet clears pooled metadata");
        }
        foreach(int count in new[]{-1,int.MinValue,int.MaxValue,ResourceClockPayloads.MaximumBytes+1,1,65536})
        {
            var write=Outgoing();write.Write(count);var read=Incoming(write,false);var key=new object();ResourceClockPayloads.Set(key,payload);
            bool refused=false;try{ResourceClockWire.DeserializeResourceClock(key,read);}catch(InvalidDataException){refused=true;}
            Check(refused,"malformed length refused before allocation");Check(ResourceClockPayloads.Get(key).Length==0,"invalid input cannot retain prior pooled state");
        }
        foreach(int alignment in Enumerable.Range(0,8))foreach(bool empty in new[]{false,true})
        {
            var key=new object();if(!empty)ResourceClockPayloads.Set(key,payload);
            var write=Outgoing();for(int i=0;i<alignment;i++)write.Write(true);
            ResourceClockWire.SerializeResourceClock(key,write);
            int end=write.LengthBits;write.WritePadBits();write.Write(1234567890123L);
            var read=Incoming(write,true);for(int i=0;i<alignment;i++)read.ReadBoolean();
            var result=new object();ResourceClockWire.DeserializeResourceClock(result,read);
            Check(read.Position==end,"extension leaves native revision and its padding untouched");
            Check(ResourceClockPayloads.Get(result).SequenceEqual(empty?new byte[0]:payload),"shared-revision resource payload round trip");
            read.SkipPadBits();Check(read.ReadInt64()==1234567890123L && read.Position==read.LengthBits,"outer native revision reader retains exact revision");
            write.Write((byte)1);bool refused=false;
            try{var extra=Incoming(write,true);for(int i=0;i<alignment;i++)extra.ReadBoolean();ResourceClockWire.DeserializeResourceClock(new object(),extra);}catch(InvalidDataException){refused=true;}
            Check(refused,"extra data beyond native revision refused");
        }
        var truncated=Outgoing();truncated.Write((byte)1);
        bool rejected=false;try{ResourceClockWire.DeserializeResourceClock(new object(),Incoming(truncated,false));}catch(InvalidDataException){rejected=true;}
        Check(rejected,"truncated header refused");
        Console.WriteLine("PASS "+checks+" actual Lidgren resource-clock framing assertions, including every bit alignment and compressed-byte rounding.");
    }
}
