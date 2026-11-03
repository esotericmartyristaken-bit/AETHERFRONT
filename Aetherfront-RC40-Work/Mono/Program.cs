using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;

// Compile against Unity's .NET 4 reference assemblies. Run the original and
// patched client in separate MonoHost processes; no modern runtime APIs needed.
internal static class Program
{
    private const int MiB = 1024 * 1024;
    private const int Limit = 4 * MiB;
    private const string Reason = "Reliable receive buffer limit exceeded";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;
    private static Type peerType, configurationType, connectionType, messageType, kindType;
    private static object dataKind, connectedStatus;
    private static MethodInfo create, createWithStorage, write, retain, release, recycle;
    private static bool patched;
    private static int assertions, passed, failed;

    public static int Main()
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("AF_TRANSPORT_PROBE_ASSEMBLY");
            string mode = Environment.GetEnvironmentVariable("AF_TRANSPORT_PROBE_PATCHED");
            if (String.IsNullOrEmpty(path) || !Boolean.TryParse(mode, out patched))
            {
                Console.Error.WriteLine("FAIL config: set AF_TRANSPORT_PROBE_ASSEMBLY and AF_TRANSPORT_PROBE_PATCHED=true/false");
                return 2;
            }
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Transport assembly not found", path);
            Assembly assembly = Assembly.LoadFrom(path);
            peerType = Named(assembly, "NetPeer");
            configurationType = Named(assembly, "NetPeerConfiguration");
            connectionType = Named(assembly, "NetConnection");
            messageType = Named(assembly, "NetIncomingMessage");
            kindType = Named(assembly, "NetIncomingMessageType");
            dataKind = Enum.Parse(kindType, "Data");
            connectedStatus = Enum.Parse(Named(assembly, "NetConnectionStatus"), "Connected");
            create = Method(peerType, "CreateIncomingMessage", kindType, typeof(int));
            createWithStorage = Method(peerType, "CreateIncomingMessage", kindType, typeof(byte[]));
            write = Method(messageType, "Write", typeof(byte[]));
            retain = Method(peerType, "TryRetainOrderedMessage", connectionType, messageType);
            release = Method(peerType, "ReleaseOrderedMessageStorage", connectionType, messageType);
            recycle = Method(peerType, "Recycle", typeof(byte[]));

            Run("assembly-contract", delegate
            {
                MethodInfo helper = peerType.GetMethod("CompactOrderedMessageStorage", Flags);
                Check(patched ? helper != null && helper.IsStatic && helper.IsPrivate : helper == null,
                    "Unexpected compaction helper contract");
            });
            Run("five-recycled-1MiB-buffers", OversizedPool);
            Run("normal-and-slack-buffers", BufferBoundaries);
            Run("actual-4MiB-cap-plus-one", ConnectionCap);
            Console.WriteLine("SUMMARY mono " + (patched ? "patched" : "original") + ": " +
                passed + "/" + (passed + failed) + " cases; " + assertions + " assertions; sockets=0");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL setup: " + Detail(error));
            return 1;
        }
    }

    private static void Run(string name, Action test)
    {
        int before = assertions;
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name + " (" + (assertions - before) + ")");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine("FAIL " + name + ": " + Detail(error));
        }
    }

    private static string Detail(Exception error)
    {
        while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
        return error.GetType().Name + ": " + error.Message;
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Payload(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = unchecked((byte)((i * 73 + seed * 31) ^ (i >> 8)));
        return bytes;
    }

    private static void Preserved(object message, byte[] expected)
    {
        byte[] bytes = Data(message);
        Check((int)messageType.GetProperty("LengthBytes", Flags).GetValue(message, null) == expected.Length,
            "Payload length changed");
        Check(bytes != null && bytes.Length >= expected.Length, "Storage too short");
        bool same = true;
        for (int i = 0; i < expected.Length; i++)
            if (bytes[i] != expected[i]) { same = false; break; }
        Check(same, "Payload bytes changed");
    }

    private static void OversizedPool()
    {
        using (Fixture fixture = new Fixture())
        {
            object connection = fixture.Connection();
            byte[][] seeded = new byte[5][];
            for (int i = 0; i < seeded.Length; i++)
            {
                seeded[i] = new byte[MiB];
                recycle.Invoke(fixture.Peer, new object[] { seeded[i] });
            }
            int accepted = 0;
            for (int i = 0; i < 5; i++)
            {
                byte[] payload = Payload(64, 20 + i);
                object message = fixture.Message(payload, null);
                byte[] original = Data(message);
                Check(Object.ReferenceEquals(original, seeded[i]), "Allocator did not reuse seeded 1 MiB array");
                bool retained = fixture.Retain(connection, message);
                Check(retained == (patched || i < 4), "Wrong retention result for message " + (i + 1));
                if (retained) accepted++;
                Check(Data(message).Length == (patched ? 64 : MiB), "Wrong backing-array size");
                Check(Object.ReferenceEquals(original, Data(message)) == !patched, "Wrong backing-array identity");
                Preserved(message, payload);
                fixture.Counts(connection, accepted * (patched ? 64 : MiB));
            }
            Check(accepted == (patched ? 5 : 4), "Wrong accepted-message count");
            if (patched)
            {
                Check(!(bool)Field(connectionType, "m_disconnectRequested").GetValue(connection), "Patched peer disconnected");
                Check(Number(fixture.Peer, "m_storagePoolBytes") == 0, "Oversized storage returned to pool");
                foreach (object item in (IEnumerable)Field(peerType, "m_storagePool").GetValue(fixture.Peer))
                    for (int i = 0; i < seeded.Length; i++)
                        Check(!Object.ReferenceEquals(item, seeded[i]), "Old oversized array re-pooled");
            }
            else fixture.ExpectDisconnect(connection);
            fixture.ReleaseAll();
            fixture.Counts(connection, 0);
        }
    }

    private static void BufferBoundaries()
    {
        using (Fixture fixture = new Fixture())
        {
            object connection = fixture.Connection();
            int[] lengths = { 64, 64, 64, 2048, 2048 };
            int[] capacities = { 64, 1088, 1089, 4096, 4097 };
            for (int i = 0; i < lengths.Length; i++)
            {
                byte[] original = new byte[capacities[i]];
                recycle.Invoke(fixture.Peer, new object[] { original });
                byte[] payload = Payload(lengths[i], 50 + i);
                object message = fixture.Message(payload, null);
                Check(Object.ReferenceEquals(original, Data(message)), "Boundary buffer not reused");
                Field(messageType, "m_readPosition").SetValue(message, 13);
                bool compact = patched && capacities[i] - lengths[i] > 1024 && capacities[i] > 2L * lengths[i];
                Check(fixture.Retain(connection, message), "Normal/slack message rejected");
                Check(Object.ReferenceEquals(original, Data(message)) == !compact, "Threshold/identity mismatch");
                Check(Data(message).Length == (compact ? lengths[i] : capacities[i]), "Wrong threshold capacity");
                Check(Number(message, "m_readPosition") == 13, "Read cursor changed");
                Preserved(message, payload);
                fixture.Counts(connection, compact ? lengths[i] : capacities[i]);
                fixture.ReleaseAll();
                fixture.Counts(connection, 0);
            }
        }
    }

    private static void ConnectionCap()
    {
        using (Fixture fixture = new Fixture())
        {
            object connection = fixture.Connection();
            byte[] payload = Payload(Limit, 90);
            object message = fixture.Message(payload, null);
            byte[] original = Data(message);
            Check(original.Length == Limit, "4 MiB allocation not exact");
            Check(fixture.Retain(connection, message), "Exactly 4 MiB rejected");
            Check(Object.ReferenceEquals(original, Data(message)), "Full 4 MiB buffer replaced");
            Preserved(message, payload);
            fixture.Counts(connection, Limit);
            Check(!fixture.Retain(connection, fixture.Message(Payload(1, 91), null)), "4 MiB plus one byte accepted");
            fixture.ExpectDisconnect(connection);
            fixture.Counts(connection, Limit);
            fixture.ReleaseAll();
            fixture.Counts(connection, 0);

            connection = fixture.Connection();
            // More than half full: compaction is ineligible. The guard must count
            // the >4 MiB backing array, not its smaller active payload.
            original = new byte[Limit + 1];
            payload = Payload(2 * MiB + 1, 92);
            message = fixture.Message(payload, original);
            Check(!fixture.Retain(connection, message), "Guard counted payload instead of actual array");
            Check(Object.ReferenceEquals(original, Data(message)), "Ineligible oversized array replaced");
            Preserved(message, payload);
            fixture.ExpectDisconnect(connection);
            fixture.Counts(connection, 0);
        }
    }

    private static Type Named(Assembly assembly, string name)
    {
        return assembly.GetType("Lidgren.Network." + name, true);
    }

    private static MethodInfo Method(Type type, string name, params Type[] arguments)
    {
        MethodInfo method = type.GetMethod(name, Flags, null, arguments, null);
        if (method == null) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static FieldInfo Field(Type type, string name)
    {
        for (Type current = type; current != null; current = current.BaseType)
        {
            FieldInfo field = current.GetField(name, Flags | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    private static byte[] Data(object message) { return (byte[])Field(messageType, "m_data").GetValue(message); }
    private static long Number(object target, string name) { return Convert.ToInt64(Field(target.GetType(), name).GetValue(target)); }

    private sealed class Fixture : IDisposable
    {
        public readonly object Peer;
        private readonly List<object> connections = new List<object>();
        private readonly List<object[]> held = new List<object[]>();

        public Fixture()
        {
            object configuration = Activator.CreateInstance(configurationType, new object[] { "isolated-unity-mono-transport-probe" });
            Method(configurationType, "DisableMessageType", kindType).Invoke(configuration,
                new object[] { Enum.Parse(kindType, "StatusChanged") });
            Peer = Activator.CreateInstance(peerType, new object[] { configuration });
            Method(peerType, "InitializePools").Invoke(Peer, null);
            NoNetwork();
        }

        public object Connection()
        {
            ConstructorInfo constructor = connectionType.GetConstructor(Flags, null,
                new Type[] { peerType, typeof(IPEndPoint) }, null);
            if (constructor == null) throw new MissingMethodException("NetConnection(NetPeer, IPEndPoint)");
            object connection = constructor.Invoke(new object[] { Peer, new IPEndPoint(IPAddress.Loopback, 1) });
            Field(connectionType, "m_status").SetValue(connection, connectedStatus);
            connections.Add(connection);
            return connection;
        }

        public object Message(byte[] payload, byte[] storage)
        {
            object message = storage == null
                ? create.Invoke(Peer, new object[] { dataKind, payload.Length })
                : createWithStorage.Invoke(Peer, new object[] { dataKind, storage });
            write.Invoke(message, new object[] { payload });
            return message;
        }

        public bool Retain(object connection, object message)
        {
            bool accepted = (bool)retain.Invoke(Peer, new object[] { connection, message });
            if (accepted) held.Add(new object[] { connection, message });
            return accepted;
        }

        public void Counts(object connection, long expected)
        {
            Check(Number(connection, "m_withheldIncomingBytes") == expected, "Wrong connection storage accounting");
            Check(Number(Peer, "m_withheldIncomingBytes") == expected, "Wrong peer storage accounting");
        }

        public void ExpectDisconnect(object connection)
        {
            Check((bool)Field(connectionType, "m_disconnectRequested").GetValue(connection), "Disconnect not requested");
            Check((string)Field(connectionType, "m_disconnectMessage").GetValue(connection) == Reason, "Wrong exact disconnect reason");
        }

        public void ReleaseAll()
        {
            foreach (object[] pair in held) release.Invoke(Peer, pair);
            held.Clear();
        }

        private void NoNetwork()
        {
            Check(peerType.GetProperty("Status", Flags).GetValue(Peer, null).ToString() == "NotRunning", "Peer started");
            Check(Field(peerType, "m_socket").GetValue(Peer) == null, "Socket created");
            Check(Field(peerType, "m_networkThread").GetValue(Peer) == null, "Network thread created");
        }

        public void Dispose()
        {
            NoNetwork();
            ReleaseAll();
            foreach (object connection in connections) Counts(connection, 0);
            NoNetwork();
        }
    }
}
