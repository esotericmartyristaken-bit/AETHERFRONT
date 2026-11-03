using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;

// Portable shared engine: .NET 8 and KSP's embedded Mono execute the same cases.
// No Start, Connect, FlushSendQueue, heartbeat, or socket APIs are called.
public sealed class TransportRun
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Assertions { get; set; }
    public List<string> Errors { get; set; } = new List<string>();
}

public static class ReflectionSuite
{
    const int MiB = 1024 * 1024;
    const long Limit = 64L * MiB;
    const string Reason = "Reliable send buffer limit exceeded; reconnect to resynchronize";
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static TransportRun result;
    static bool patched;
    static Type peerType, configurationType, connectionType, outgoingType, incomingType, kindType, channelType;
    static object dataKind, connected, ordered;
    static MethodInfo getStorage, recycleBytes, createOutgoing, createIncoming, write, reserve, release, enqueue, reset;

    public static TransportRun Run(Assembly assembly, bool isPatched, bool smoke)
    {
        result = new TransportRun(); patched = isPatched;
        peerType = Named(assembly, "NetPeer"); configurationType = Named(assembly, "NetPeerConfiguration");
        connectionType = Named(assembly, "NetConnection"); outgoingType = Named(assembly, "NetOutgoingMessage");
        incomingType = Named(assembly, "NetIncomingMessage"); kindType = Named(assembly, "NetIncomingMessageType");
        channelType = Named(assembly, "NetReliableSenderChannel");
        dataKind = Enum.Parse(kindType, "Data"); connected = Enum.Parse(Named(assembly, "NetConnectionStatus"), "Connected");
        ordered = Enum.Parse(Named(assembly, "NetMessageType"), "UserReliableOrdered1");
        getStorage = Method(peerType, "GetStorage", typeof(int)); recycleBytes = Method(peerType, "Recycle", typeof(byte[]));
        createOutgoing = Method(peerType, "CreateMessage", typeof(int)); createIncoming = Method(peerType, "CreateIncomingMessage", kindType, typeof(int));
        write = Method(outgoingType, "Write", typeof(byte[])); reserve = Method(connectionType, "TryReserveReliableMessage", outgoingType);
        release = Method(connectionType, "ReleaseReliableMessage", outgoingType); enqueue = Method(channelType, "Enqueue", outgoingType); reset = Method(channelType, "Reset");
        Case("helper-contract-and-Int64-boundaries", Helper);
        Case("original-33-failure-or-patched-128-burst", Burst);
        Case("incoming-pool-shared-with-outgoing", Incoming);
        int[,] boundaries = { {64,64}, {64,1088}, {64,1089}, {2048,4096}, {2048,4097}, {1025,2050}, {1025,2051}, {0,1024}, {0,1025} };
        for (int i = 0; i < boundaries.GetLength(0); i++) { int request = boundaries[i,0], capacity = boundaries[i,1]; Case("boundary-" + request + "-" + capacity, delegate { Boundary(request, capacity); }); }
        Case("first-eligible-order-and-large-reuse", FirstFit);
        Case("negative-empty-and-pooled-behavior", Negative);
        Case("large-valid-request-and-disabled-pool", LargeRequest);
        Case("shared-fragment-and-multirecipient-accounting", Shared);
        Case("queue-out-of-order-ACK-and-reset", Acknowledgments);
        Case("reset-queued-and-stored-messages", ResetMixed);
        Case("existing-RC40-receive-compaction-and-cap", ReceiveRetention);
        if (!smoke)
        {
            Case("connection-genuine-64MiB-plus-byte", ConnectionBytes);
            Case("peer-genuine-64MiB-plus-byte", PeerBytes);
            Case("connection-4096-distinct-arrays", ConnectionArrays);
            Case("peer-16384-distinct-arrays", PeerArrays);
            Case("connection-131072-reservations", ConnectionReservations);
            Case("peer-262144-reservations", PeerReservations);
        }
        Console.WriteLine("TARGET " + assembly.GetName().Name + " " + (patched ? "patched" : "original") + " " + result.Passed + "/" + (result.Passed + result.Failed) + "; assertions=" + result.Assertions);
        return result;
    }

    static void Case(string name, Action action)
    {
        try { action(); result.Passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException; result.Failed++; string detail = name + ": " + error.GetType().Name + ": " + error.Message; result.Errors.Add(detail); Console.Error.WriteLine("FAIL " + detail); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    }
    static void Check(bool value, string message) { result.Assertions++; if (!value) throw new InvalidOperationException(message); }
    static Type Named(Assembly a, string n) { return a.GetType("Lidgren.Network." + n, true); }
    static MethodInfo Method(Type t, string name, params Type[] parameters) { var m = t.GetMethod(name, Flags, null, parameters, null); if (m == null) throw new MissingMethodException(t.FullName, name); return m; }
    static FieldInfo Field(Type t, string name) { for (; t != null; t = t.BaseType) { var f = t.GetField(name, Flags | BindingFlags.DeclaredOnly); if (f != null) return f; } throw new MissingFieldException(name); }
    static object Get(object o, string name) { return Field(o.GetType(), name).GetValue(o); }
    static void Set(object o, string name, object value) { Field(o.GetType(), name).SetValue(o, value); }
    static long Number(object o, string name) { return Convert.ToInt64(Get(o, name)); }
    static object Call(MethodInfo m, object o, params object[] values) { return m.Invoke(o, values); }
    static byte[] Data(object o) { return (byte[])Get(o, "m_data"); }
    static bool Suitable(int capacity, int request) { return request < 0 || capacity <= (long)request + 1024 || capacity <= 2L * request; }
    static int Count(object o, string field) { return ((IDictionary)Get(o, field)).Count; }

    static void Helper()
    {
        var helper = peerType.GetMethod("IsPooledStorageSuitable", Flags);
        Check(patched ? helper != null && helper.IsPrivate && helper.IsStatic : helper == null, "Unexpected helper contract");
        if (!patched) return;
        int[,] rows = { {1088,64},{1089,64},{4096,2048},{4097,2048},{2050,1025},{2051,1025},{1024,0},{1025,0},{int.MaxValue,-1},{int.MaxValue,int.MinValue},{int.MaxValue,int.MaxValue},{int.MaxValue,int.MaxValue-1024},{int.MaxValue,1073741824},{int.MaxValue,1073741823} };
        for (int i = 0; i < rows.GetLength(0); i++) Check((bool)Call(helper, null, rows[i,0], rows[i,1]) == Suitable(rows[i,0], rows[i,1]), "Integer predicate mismatch at row " + i);
    }
    static void Burst()
    {
        using (var f = new Fixture())
        {
            var c = f.Connection(); var channel = f.Channel(c); var seeded = new byte[32][];
            for (int i = 0; i < 32; i++) { seeded[i] = new byte[2 * MiB]; f.Seed(seeded[i]); }
            f.PoolCounts(Limit, 32);
            int accepted = 0, attempts = patched ? 128 : 33;
            for (int i = 0; i < attempts; i++)
            {
                var m = f.Message(64, i); byte[] before = Data(m); string status = f.Enqueue(channel, m);
                bool success = status == "Sent" || status == "Queued";
                Check(success == (patched || i < 32), "Wrong burst acceptance at " + (i + 1));
                if (success) accepted++;
                Check(Data(m) == before, "Enqueue mutated an existing backing array"); f.Payload(m, 64, i);
                Check(before.Length == (patched || i == 32 ? 64 : 2 * MiB), "Wrong allocated capacity");
                f.Counts(c, accepted * (patched ? 64L : 2L * MiB), accepted, accepted);
            }
            if (patched) { Check(accepted == 128 && Number(c,"m_reliableOutgoingBytes") == 8192, "128-message burst did not stay within 8 KiB"); Check(!(bool)Get(c,"m_disconnectRequested"), "Patched burst disconnected"); f.PoolCounts(Limit,32); }
            else { Check((bool)Get(c,"m_disconnectRequested") && (string)Get(c,"m_disconnectMessage") == Reason, "Exact original send-guard failure not reproduced"); }
            Call(reset, channel); f.Counts(c,0,0,0); f.PoolInvariant();
        }
    }
    static void Boundary(int request, int capacity)
    {
        using (var f = new Fixture())
        {
            byte[] original = new byte[capacity]; f.Seed(original); byte[] actual = f.Storage(request);
            bool reuse = !patched || Suitable(capacity, request);
            Check(Object.ReferenceEquals(original, actual) == reuse, "Eligibility equality boundary changed");
            Check(actual.Length == (reuse ? capacity : request), "Wrong returned capacity");
            f.PoolCounts(reuse ? 0 : capacity, reuse ? 0 : 1);
        }
    }
    static void FirstFit()
    {
        using (var f = new Fixture())
        {
            byte[] large = new byte[2*MiB], small = new byte[80], later = new byte[72]; f.Seed(large); f.Seed(small); f.Seed(later);
            Check(Object.ReferenceEquals(f.Storage(64), patched ? small : large), "First eligible array order changed");
            if (patched) { f.PoolCounts(2L*MiB+72,2); Check(Object.ReferenceEquals(f.Storage(2*MiB),large), "Skipped large array was lost"); f.PoolCounts(72,1); Check(Object.ReferenceEquals(f.Storage(64),later), "Remaining eligible array lost"); }
            else { Check(Object.ReferenceEquals(f.Storage(64),small), "Original first-fit behavior changed"); Check(Object.ReferenceEquals(f.Storage(64),later), "Original reuse order changed"); }
            f.PoolCounts(0,0);
        }
    }
    static void Incoming()
    {
        using (var f = new Fixture())
        {
            var incoming = Call(createIncoming,f.Peer,dataKind,2*MiB); byte[] original = Data(incoming);
            Call(Method(peerType,"Recycle",incomingType),f.Peer,incoming); f.PoolCounts(2*MiB,1);
            var outgoing = f.Message(64,9); Check(Object.ReferenceEquals(Data(outgoing),original) == !patched,"Incoming pool did not feed outgoing allocation"); f.Payload(outgoing,64,9);
            if (patched) { var next = Call(createIncoming,f.Peer,dataKind,64); Check(Data(next).Length == 64,"New incoming allocation retained oversized slack"); f.PoolCounts(2*MiB,1); }
        }
    }
    static void Negative()
    {
        using (var f = new Fixture())
        {
            bool threw = false; try { f.Storage(-1); } catch (TargetInvocationException e) { Check(e.InnerException is OverflowException,"Negative allocation exception changed"); threw = true; } Check(threw,"Empty-pool negative allocation accepted");
            byte[] large = new byte[2*MiB]; f.Seed(large); Check(Object.ReferenceEquals(f.Storage(-1),large),"Existing negative first-fit behavior changed"); f.PoolCounts(0,0);
        }
    }
    static void LargeRequest()
    {
        using (var f = new Fixture()) { byte[] small = new byte[2*MiB]; f.Seed(small); Check(f.Storage(16*MiB).Length == 16*MiB,"Large valid request changed"); f.PoolCounts(2*MiB,1); }
        using (var f = new Fixture(false)) { Check(f.Storage(0).Length == 0 && f.Storage(64).Length == 64,"Disabled-pool allocation changed"); bool threw=false; try { f.Storage(-1); } catch(TargetInvocationException e) { Check(e.InnerException is OverflowException,"Disabled-pool negative exception changed"); threw=true; } Check(threw,"Disabled pool accepted negative request"); }
    }
    static void Shared()
    {
        using (var f = new Fixture())
        {
            var a=f.Connection(); var b=f.Connection(); byte[] data=new byte[2048]; var m=f.MessageWith(data,64); var n=f.MessageWith(data,32);
            Set(m,"m_fragmentGroup",17); Set(n,"m_fragmentGroup",17);
            Check(f.Reserve(a,m) && f.Reserve(a,n) && f.Reserve(b,m),"Shared reservations rejected");
            f.ConnectionCounts(a,2048,1,2); f.ConnectionCounts(b,2048,1,1); f.PeerCounts(2048,1,3);
            f.Seed(new byte[2*MiB]); f.Storage(64); Check(Object.ReferenceEquals(Data(m),data) && Object.ReferenceEquals(Data(n),data),"Allocator mutated retained aliases");
            f.ReleaseLast(); f.ConnectionCounts(b,0,0,0); f.PeerCounts(2048,1,2); f.ReleaseLast(); f.Counts(a,2048,1,1); f.ReleaseLast(); f.Counts(a,0,0,0);
        }
    }
    static void Acknowledgments()
    {
        using(var f=new Fixture())
        {
            var c=f.Connection();var channel=f.Channel(c); f.PrepareWireBuffer(); var a=f.Message(64,41);var b=f.Message(64,42);
            f.Enqueue(channel,a);f.Enqueue(channel,b);f.Counts(c,128,2,2);
            Call(Method(channelType,"SendQueuedMessages",typeof(double)),channel,1.0); f.Counts(c,128,2,2);f.Payload(a,64,41);f.Payload(b,64,42);
            Check(Number(c,"m_sendBufferWritePtr") < Number(c,"m_currentMTU"),"Test would flush a packet");
            var ack=Method(channelType,"ReceiveAcknowledge",typeof(double),typeof(int)); Call(ack,channel,1.1,1);f.Counts(c,128,2,2);Call(ack,channel,1.2,0);f.Counts(c,0,0,0);Call(ack,channel,1.3,0);f.Counts(c,0,0,0);
        }
    }
    static void ResetMixed()
    {
        using(var f=new Fixture())
        {
            var c=f.Connection();var channel=f.Channel(c);f.PrepareWireBuffer(); f.Enqueue(channel,f.Message(64,1));f.Enqueue(channel,f.Message(64,2));
            Call(Method(channelType,"SendQueuedMessages",typeof(double)),channel,1.0);f.Enqueue(channel,f.Message(64,3));f.Enqueue(channel,f.Message(64,4)); f.Counts(c,256,4,4);Call(reset,channel);f.Counts(c,0,0,0);Call(reset,channel);f.Counts(c,0,0,0);
        }
    }
    static void ReceiveRetention()
    {
        using(var f=new Fixture())
        {
            var c=f.Connection(); var retain=Method(peerType,"TryRetainOrderedMessage",connectionType,incomingType);var unretain=Method(peerType,"ReleaseOrderedMessageStorage",connectionType,incomingType);
            var m=Call(createIncoming,f.Peer,dataKind,64);Set(m,"m_data",new byte[MiB]);Set(m,"m_bitLength",512);
            Check((bool)Call(retain,f.Peer,c,m),"Existing RC40 retention failed");Check(Data(m).Length==64 && Number(c,"m_withheldIncomingBytes")==64,"RC40 receive compaction regressed");Call(unretain,f.Peer,c,m);
            var full=Call(createIncoming,f.Peer,dataKind,4*MiB);Set(full,"m_bitLength",4*MiB*8);Check((bool)Call(retain,f.Peer,c,full),"Existing receive cap equality failed");
            var one=Call(createIncoming,f.Peer,dataKind,1);Set(one,"m_bitLength",8);Check(!(bool)Call(retain,f.Peer,c,one),"Existing receive cap weakened");Call(unretain,f.Peer,c,full);Check(Number(c,"m_withheldIncomingBytes")==0 && Number(f.Peer,"m_withheldIncomingBytes")==0,"Receive cleanup leaked");
        }
    }
    static void ConnectionBytes()
    {
        using(var f=new Fixture()) { var c=f.Connection();byte[] storage=new byte[(int)Limit];var full=f.MessageWith(storage,1);Check(f.Reserve(c,full),"64 MiB equality rejected");Check(!f.Reserve(c,f.MessageWith(new byte[1],1)),"64 MiB plus byte accepted");f.Counts(c,Limit,1,1);Check(Object.ReferenceEquals(Data(full),storage),"Reserved buffer mutated"); }
    }
    static void PeerBytes()
    {
        using(var f=new Fixture()) {var a=f.Connection();var b=f.Connection();var c=f.Connection();Check(f.Reserve(a,f.MessageWith(new byte[32*MiB],1)) && f.Reserve(b,f.MessageWith(new byte[32*MiB],1)),"Peer 64 MiB equality rejected");Check(!f.Reserve(c,f.MessageWith(new byte[1],1)),"Peer 64 MiB plus byte accepted");f.PeerCounts(Limit,2,2);f.ConnectionCounts(c,0,0,0);}
    }
    static void ConnectionArrays()
    {
        using(var f=new Fixture()) {var c=f.Connection();for(int i=0;i<4096;i++) Check(f.Reserve(c,f.MessageWith(new byte[1],1)),"Connection array cap early");Check(!f.Reserve(c,f.MessageWith(new byte[1],1)),"Connection array cap weakened");f.Counts(c,4096,4096,4096);}
    }
    static void PeerArrays()
    {
        using(var f=new Fixture()) {for(int c=0;c<4;c++){var connection=f.Connection();for(int i=0;i<4096;i++)Check(f.Reserve(connection,f.MessageWith(new byte[1],1)),"Peer array cap early");}var extra=f.Connection();Check(!f.Reserve(extra,f.MessageWith(new byte[1],1)),"Peer array cap weakened");f.PeerCounts(16384,16384,16384);f.ConnectionCounts(extra,0,0,0);}
    }
    static void ConnectionReservations()
    {
        using(var f=new Fixture()) {var c=f.Connection();var m=f.MessageWith(new byte[1],1);for(int i=0;i<131072;i++)Check(f.Reserve(c,m),"Connection reservation cap early");Check(!f.Reserve(c,m),"Connection reservation cap weakened");f.Counts(c,1,1,131072);}
    }
    static void PeerReservations()
    {
        using(var f=new Fixture()) {var m=f.MessageWith(new byte[1],1);for(int c=0;c<2;c++){var connection=f.Connection();for(int i=0;i<131072;i++)Check(f.Reserve(connection,m),"Peer reservation cap early");}var extra=f.Connection();Check(!f.Reserve(extra,m),"Peer reservation cap weakened");f.PeerCounts(1,1,262144);f.ConnectionCounts(extra,0,0,0);}
    }

    sealed class Fixture : IDisposable
    {
        public readonly object Peer;
        readonly List<object> connections=new List<object>(),channels=new List<object>();
        readonly List<object[]> held=new List<object[]>();
        public Fixture():this(true){}
        public Fixture(bool recycling)
        {
            var config=Activator.CreateInstance(configurationType,new object[]{"isolated-rc42-regressions"});configurationType.GetProperty("UseMessageRecycling").SetValue(config,recycling,null);
            Call(Method(configurationType,"DisableMessageType",kindType),config,Enum.Parse(kindType,"StatusChanged"));Peer=Activator.CreateInstance(peerType,new object[]{config});Call(Method(peerType,"InitializePools"),Peer);NoNetwork();
        }
        public object Connection(){var ctor=connectionType.GetConstructor(Flags,null,new Type[]{peerType,typeof(IPEndPoint)},null);var c=ctor.Invoke(new object[]{Peer,new IPEndPoint(IPAddress.Loopback,9)});Set(c,"m_status",connected);connections.Add(c);return c;}
        public object Channel(object c){var ctor=channelType.GetConstructor(Flags,null,new Type[]{connectionType,typeof(int)},null);var channel=ctor.Invoke(new object[]{c,64});channels.Add(channel);return channel;}
        public byte[] Storage(int request){return (byte[])Call(getStorage,Peer,request);}
        public void Seed(byte[] bytes){Call(recycleBytes,Peer,new object[]{bytes});}
        public object Message(int length,int seed){var m=Call(createOutgoing,Peer,length);byte[] payload=new byte[length];for(int i=0;i<length;i++)payload[i]=unchecked((byte)(i*73+seed*31));Call(write,m,new object[]{payload});Set(m,"m_messageType",ordered);return m;}
        public object MessageWith(byte[] data,int length){var m=Call(createOutgoing,Peer,0);Set(m,"m_data",data);Set(m,"m_bitLength",length*8);Set(m,"m_messageType",ordered);return m;}
        public void Payload(object m,int length,int seed){Check((int)outgoingType.GetProperty("LengthBytes").GetValue(m,null)==length,"Payload length changed");var data=Data(m);bool same=data.Length>=length;for(int i=0;i<length && same;i++)same=data[i]==unchecked((byte)(i*73+seed*31));Check(same,"Payload bytes changed");}
        public string Enqueue(object channel,object m){Set(m,"m_recyclingCount",1);return Call(enqueue,channel,m).ToString();}
        public bool Reserve(object c,object m){bool ok=(bool)Call(reserve,c,m);if(ok)held.Add(new object[]{c,m});return ok;}
        public void ReleaseLast(){var pair=held[held.Count-1];Call(release,pair[0],pair[1]);held.RemoveAt(held.Count-1);}
        public void ConnectionCounts(object c,long bytes,int arrays,int messages){Check(Number(c,"m_reliableOutgoingBytes")==bytes,"Connection allocated-byte count");Check(Count(c,"m_reliableOutgoingBuffers")==arrays,"Connection distinct-array count");Check(Number(c,"m_reliableOutgoingMessages")==messages,"Connection reservation count");}
        public void PeerCounts(long bytes,int arrays,int messages){Check(Number(Peer,"m_reliableOutgoingBytes")==bytes,"Peer allocated-byte count");Check(Count(Peer,"m_reliableOutgoingBuffers")==arrays,"Peer distinct-array count");Check(Number(Peer,"m_reliableOutgoingMessages")==messages,"Peer reservation count");}
        public void Counts(object c,long bytes,int arrays,int messages){ConnectionCounts(c,bytes,arrays,messages);PeerCounts(bytes,arrays,messages);}
        public void PoolCounts(long bytes,int slots){Check(Number(Peer,"m_storagePoolBytes")==bytes,"Pool byte count");Check(Number(Peer,"m_storageSlotsUsedCount")==slots,"Pool used-slot count");PoolInvariant();}
        public void PoolInvariant(){if(Get(Peer,"m_storagePool")==null)return;long sum=0;int count=0;var unique=new HashSet<byte[]>();foreach(object o in (IEnumerable)Get(Peer,"m_storagePool")){if(o==null)continue;byte[] b=(byte[])o;Check(unique.Add(b),"Pool contains duplicate array reference");sum+=b.Length;count++;}Check(sum==Number(Peer,"m_storagePoolBytes") && sum<=Limit,"Pool accounting or 64 MiB cache limit");Check(count==Number(Peer,"m_storageSlotsUsedCount"),"Pool slot accounting");}
        public void PrepareWireBuffer(){Set(Peer,"m_sendBuffer",new byte[65536]);foreach(var c in connections){Set(c,"m_sendBufferWritePtr",0);Set(c,"m_sendBufferNumMessages",0);Check(Number(c,"m_currentMTU")>=256,"MTU too small for isolated encoding test");}}
        void NoNetwork(){Check(peerType.GetProperty("Status").GetValue(Peer,null).ToString()=="NotRunning","Peer started");Check(Get(Peer,"m_socket")==null,"Socket created");Check(Get(Peer,"m_networkThread")==null,"Network thread created");}
        public void Dispose(){NoNetwork();foreach(var channel in channels)Call(reset,channel);while(held.Count>0)ReleaseLast();foreach(var c in connections)ConnectionCounts(c,0,0,0);PeerCounts(0,0,0);PoolInvariant();NoNetwork();}
    }
}
