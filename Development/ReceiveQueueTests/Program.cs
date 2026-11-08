using System.Net;
using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2 || args[0] is not ("reproduce" or "verify" or "verify-client" or "udp" or "impaired-udp" or "reproduce-pong-timeout" or "verify-pong-timeout"))
    throw new ArgumentException("Expected a queue or UDP regression mode and a transport DLL path");

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
if (args[0] is "reproduce-pong-timeout" or "verify-pong-timeout")
{
    Loopback.RunReceiveOnly(assembly, args[0] == "reproduce-pong-timeout");
    return;
}
if (args[0] is "udp" or "impaired-udp")
{
    Loopback.Run(assembly, args[0] == "impaired-udp");
    return;
}
var transport = new Transport(assembly);
if (args[0] == "verify-client")
{
    var server = transport.Connection(20001);
    for (int sequence = 0; sequence < 6000; sequence++)
        transport.Enqueue(server, sequence, "UserUnreliable");
    Check(!transport.Disconnecting(server), "Client unreliable overflow disconnected the server");
    for (int sequence = 0; sequence < 3000; sequence++)
        transport.Enqueue(server, sequence);
    Check(!transport.Disconnecting(server), "Client reliable allowance regressed");
    Check(transport.DrainBatch() == 3512 && transport.Bytes == 0, "Client dropped reliable data or leaked accounting");
    Console.WriteLine("PASS: client unreliable flood preserved all 3,000 reliable messages and left its connection open.");
    return;
}
var source = transport.Connection(20001);
var other = transport.Connection(20002);
for (int sequence = 0; sequence < 4096; sequence++)
    transport.Enqueue(source, sequence);
var sample = transport.Enqueue(other, 63);
var retained = transport.Drain();
bool otherDisconnected = transport.Disconnecting(other);
bool sampleDelivered = retained.Contains(sample);

if (args[0] == "reproduce")
{
    Check(transport.Disconnecting(source) == false, "Exactly filling the legacy shared queue should not disconnect its source");
    Check(otherDisconnected && !sampleDelivered, "Legacy shared queue did not disconnect the unrelated connection");
    Console.WriteLine("Reproduced: one sender filled 4,096 entries; another sender's first reliable message was discarded and its connection was terminated.");
}
else
{
    Check(transport.Disconnecting(source), "Reliable sender exceeding its own backlog was not isolated");
    Check(!otherDisconnected && sampleDelivered, "Unrelated peer lost reliable data or its connection");
    Check(transport.Bytes == 0, "Draining the queue leaked byte accounting");
    var next = transport.Enqueue(other, 64);
    Check(transport.Drain().Single() == next, "Healthy peer did not resume after overload");
    var updated = transport.Connection(20003);
    for (int sequence = 0; sequence < 6000; sequence++)
        transport.Enqueue(updated, sequence, "UserUnreliable");
    Check(!transport.Disconnecting(updated), "Unreliable overflow terminated its connection");
    for (int sequence = 0; sequence < 300; sequence++)
        transport.Enqueue(updated, sequence);
    Check(transport.DrainBatch() == 556, "Unreliable burst consumed reliable capacity");
    Check(transport.Bytes == 0, "Batch read leaked byte accounting");
    transport.Enqueue(updated, 301);
    Check(transport.DrainBatch() == 1, "Batch read failed to release the sender's budget");
    for (int index = 0; index < 4; index++)
    {
        var heavy = transport.Connection(21000 + index);
        for (int sequence = 0; sequence < 1024; sequence++)
            transport.Enqueue(heavy, sequence);
    }
    for (int index = 0; index < 20; index++)
    {
        var light = transport.Connection(22000 + index);
        transport.Enqueue(light, 0);
        Check(!transport.Disconnecting(light), "Concurrent heavy senders forced a healthy peer out");
    }
    Check(transport.DrainBatch() == 3072 + 64 + 20, "High-pressure queue lost its healthy-peer reserve");
    Check(transport.Bytes == 0, "High-pressure queue leaked byte accounting");
    Console.WriteLine("PASS: reliable offender isolated, unrelated connection served, accounting recovered after drain.");
    Console.WriteLine("PASS: 6,000 unreliable updates preserve reliable headroom and leave batch accounting balanced.");
    Console.WriteLine("PASS: four overlapping reliable floods preserve headroom for 20 newly active peers.");
}

static void Check(bool expected, string message)
{
    if (!expected) throw new InvalidOperationException(message);
}

sealed class Transport
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Type peerType;
    private readonly Type connectionType;
    private readonly Type messageType;
    private readonly Type incomingKind;
    private readonly Type deliveryKind;
    private readonly object peer;
    private readonly MethodInfo create;
    private readonly MethodInfo release;
    private readonly MethodInfo read;
    private readonly MethodInfo recycle;

    internal Transport(Assembly assembly)
    {
        Type Get(string name) => assembly.GetType("Lidgren.Network." + name, true)!;
        var configurationType = Get("NetPeerConfiguration");
        peerType = Get("NetPeer");
        connectionType = Get("NetConnection");
        messageType = Get("NetIncomingMessage");
        incomingKind = Get("NetIncomingMessageType");
        deliveryKind = Get("NetMessageType");
        var configuration = Activator.CreateInstance(configurationType, "receive-queue-fixture")!;
        foreach (string name in new[] { "StatusChanged", "DebugMessage", "VerboseDebugMessage", "WarningMessage", "Error" })
            configurationType.GetMethod("DisableMessageType", Flags)!.Invoke(configuration, new[] { Enum.Parse(incomingKind, name) });
        peer = Activator.CreateInstance(peerType, configuration)!;
        Method("InitializePools").Invoke(peer, null);
        create = Method("CreateIncomingMessage", incomingKind, typeof(int));
        release = Method("ReleaseMessage", messageType);
        read = Method("ReadMessage");
        recycle = Method("Recycle", messageType);
    }

    internal object Connection(int port)
    {
        var connection = connectionType.GetConstructor(Flags, null, new[] { peerType, typeof(IPEndPoint) }, null)!
            .Invoke(new[] { peer, new IPEndPoint(IPAddress.Loopback, port) });
        var status = connectionType.GetField("m_status", Flags)!;
        status.SetValue(connection, Enum.Parse(status.FieldType, "Connected"));
        return connection;
    }

    internal object Enqueue(object connection, int sequence, string delivery = "UserReliableOrdered1")
    {
        var message = create.Invoke(peer, new[] { Enum.Parse(incomingKind, "Data"), (object)64 })!;
        messageType.GetField("m_senderConnection", Flags)!.SetValue(message, connection);
        messageType.GetField("m_receivedMessageType", Flags)!.SetValue(message, Enum.Parse(deliveryKind, delivery));
        messageType.GetField("m_sequenceNumber", Flags)!.SetValue(message, sequence % 1024);
        messageType.GetMethod("Write", new[] { typeof(byte[]) })!.Invoke(message, new object[] { new byte[64] });
        release.Invoke(peer, new[] { message });
        return message;
    }

    internal List<object> Drain()
    {
        var drained = new List<object>();
        while (read.Invoke(peer, null) is object message)
        {
            drained.Add(message);
            recycle.Invoke(peer, new[] { message });
        }
        return drained;
    }

    internal long Bytes => (long)peerType.GetField("m_pendingIncomingBytes", Flags)!.GetValue(peer)!;
    internal int DrainBatch()
    {
        var listType = typeof(List<>).MakeGenericType(messageType);
        var messages = (System.Collections.IList)Activator.CreateInstance(listType)!;
        int count = (int)Method("ReadMessages", typeof(IList<>).MakeGenericType(messageType))
            .Invoke(peer, new object[] { messages })!;
        foreach (object message in messages)
            recycle.Invoke(peer, new[] { message });
        return count;
    }
    internal bool Disconnecting(object connection) => (bool)connectionType.GetField("m_disconnectRequested", Flags)!.GetValue(connection)!;
    private MethodInfo Method(string name, params Type[] parameters) => peerType.GetMethod(name, Flags, null, parameters, null)!;
}
