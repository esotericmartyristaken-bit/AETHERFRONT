using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private const int MiB = 1024 * 1024;
    private const int ConnectionLimit = 4 * MiB;
    private const long PeerLimit = 64L * MiB;
    private const string LimitReason = "Reliable receive buffer limit exceeded";
    private const string ClientHash = "c7e85dc6139b7f37d92a6b5ac0797c53cfb56c9c440df7cbeb70f1f866982330";
    private const string ServerHash = "90a61a0e2db1f7e62aa8b63aa4d4e1f10c877a7a60ce5338d278c2bbc1fe6dc0";
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<CaseResult> Results = new();
    private static readonly List<TargetResult> Targets = new();
    private static int assertions;

    private sealed record CaseResult(string Target, string Test, bool Passed, int Assertions, string? Error);
    private sealed record TargetResult(string Target, string Path, string Sha256, string AssemblyName);

    // These tests never call Start, Connect, heartbeat, or any socket API. Each of
    // the four assemblies has its own collectible context, even when names match.
    public static int Main(string[] args)
    {
        Results.Clear();
        Targets.Clear();
        assertions = 0;
        if (args.Length is not (4 or 5))
        {
            Console.Error.WriteLine("Usage: TransportTests ORIGINAL_CLIENT PATCHED_CLIENT ORIGINAL_SERVER PATCHED_SERVER [SUMMARY.json]");
            return 2;
        }

        RunTarget("original-client", args[0], false, ClientHash);
        RunTarget("patched-client", args[1], true, ClientHash);
        RunTarget("original-server", args[2], false, ServerHash);
        RunTarget("patched-server", args[3], true, ServerHash);

        int failures = Results.Count(result => !result.Passed);
        Console.WriteLine($"SUMMARY {Results.Count - failures}/{Results.Count} cases passed; {assertions} assertions; sockets=0");
        if (args.Length == 5)
        {
            try
            {
                string output = Path.GetFullPath(args[4]);
                if (!string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Summary must have a .json extension");
                if (args.Take(4).Any(input => string.Equals(Path.GetFullPath(input), output,
                    StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Summary must not overwrite an input assembly");
                var summary = new
                {
                    schemaVersion = 1,
                    completedAt = DateTime.UtcNow,
                    ok = failures == 0,
                    socketStarted = false,
                    productionModified = false,
                    passed = Results.Count - failures,
                    failed = failures,
                    assertions,
                    targets = Targets,
                    cases = Results,
                    limitation = "Isolated reflection tests of actual assemblies, not a captured traffic replay or live-network test."
                };
                File.WriteAllText(output, JsonSerializer.Serialize(summary, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL summary: {error.GetType().Name}: {error.Message}");
                return 1;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void RunTarget(string label, string path, bool patched, string baselineHash)
    {
        AssemblyLoadContext? context = null;
        Transport? transport = null;
        bool loaded = RunCase(label, "assembly-contract", () =>
        {
            string fullPath = Path.GetFullPath(path);
            byte[] image = File.ReadAllBytes(fullPath);
            string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            Check(patched ? hash != baselineHash : hash == baselineHash,
                patched ? "Patched assembly still has baseline hash" : $"Baseline SHA256 mismatch: {hash}");
            context = new AssemblyLoadContext($"transport-tests-{label}-{Guid.NewGuid():N}", isCollectible: true);
            using var stream = new MemoryStream(image, writable: false);
            Assembly assembly = context.LoadFromStream(stream);
            Check(ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), context), "Assembly not isolated");
            Check(!ReferenceEquals(context, AssemblyLoadContext.Default), "Default load context used");
            transport = new Transport(assembly, patched);
            MethodInfo? helper = transport.PeerType.GetMethod("CompactOrderedMessageStorage", Members);
            Check(patched ? helper is { IsPrivate: true, IsStatic: true } : helper == null,
                "Unexpected compaction helper contract");
            if (helper != null)
            {
                Check(helper.ReturnType == typeof(void) && helper.GetParameters().Length == 1 &&
                    helper.GetParameters()[0].ParameterType == transport.MessageType, "Helper signature changed");
            }
            Targets.Add(new TargetResult(label, fullPath, hash, assembly.FullName ?? assembly.GetName().Name ?? ""));
        });
        try
        {
            if (!loaded || transport == null) return;
            Transport api = transport;
            RunCase(label, "small-buffer-control", () => SmallBuffers(api));
            RunCase(label, "five-recycled-1MiB-buffers", () => OversizedPool(api));
            foreach ((int active, int capacity, string name) in new[]
            {
                (64, 64, "exact-small"),
                (64, 1087, "slack-1023"),
                (64, 1088, "slack-1024"),
                (64, 1089, "slack-1025"),
                (2048, 4095, "below-double"),
                (2048, 4096, "exact-double"),
                (2048, 4097, "above-double")
            })
                RunCase(label, name, () => BufferBoundary(api, active, capacity));
            RunCase(label, "partial-byte-payload", () => PartialByte(api));
            RunCase(label, "large-payload-preserved", () => BufferBoundary(api, MiB + 123, 3 * MiB));
            RunCase(label, "connection-4MiB-plus-one", () => ConnectionCap(api));
            RunCase(label, "actual-array-cap-not-payload-cap", () => ActualArrayCap(api));
            RunCase(label, "active-length-over-4MiB", () => OversizedActiveLength(api));
            RunCase(label, "peer-64MiB-17-connections", () => GlobalCap(api));
            RunCase(label, "zero-length-safe-compaction", () => ZeroLength(api));
            RunCase(label, "malformed-lengths-and-null-storage", () => MalformedLengths(api));
            RunCase(label, "receiver-reorder-duplicate-release", () => ReceiverOrder(api, false));
            RunCase(label, "receiver-sequence-wrap", () => ReceiverOrder(api, true));
            RunCase(label, "receiver-discard-cleanup", () => ReceiverDiscard(api));
        }
        finally
        {
            transport = null;
            context?.Unload();
            // Reclaim the 64 MiB cap fixture before loading the next assembly.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static bool RunCase(string target, string name, Action action)
    {
        int before = assertions;
        try
        {
            action();
            Results.Add(new CaseResult(target, name, true, assertions - before, null));
            Console.WriteLine($"PASS {target}/{name} ({assertions - before})");
            return true;
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException { InnerException: not null } wrapped)
                error = wrapped.InnerException!;
            string detail = $"{error.GetType().Name}: {error.Message}";
            Results.Add(new CaseResult(target, name, false, assertions - before, detail));
            Console.Error.WriteLine($"FAIL {target}/{name}: {detail}");
            return false;
        }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = unchecked((byte)((i * 73 + seed * 31) ^ (i >> 8)));
        return bytes;
    }

    private static void Preserved(Fixture fixture, object message, byte[] expected)
    {
        Check(fixture.Length(message) == expected.Length, "Payload length changed");
        Check(fixture.Data(message) is byte[] data && data.Length >= expected.Length &&
            data.AsSpan(0, expected.Length).SequenceEqual(expected), "Payload bytes changed");
    }

    private static void SmallBuffers(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        for (int i = 0; i < 5; i++)
        {
            byte[] expected = Payload(64, i + 1);
            object message = fixture.Message(expected);
            byte[]? original = fixture.Data(message);
            Check(fixture.Retain(connection, message), $"Small message {i + 1} rejected");
            Check(ReferenceEquals(original, fixture.Data(message)), "Small buffer unnecessarily replaced");
            Check(original?.Length == 64, "Small allocation not 64 bytes");
            Preserved(fixture, message, expected);
            fixture.Counts(connection, (i + 1) * 64, (i + 1) * 64);
        }
        Check(!fixture.Disconnected(connection), "Small-buffer control disconnected");
        fixture.ReleaseAll();
        fixture.Counts(connection, 0, 0);
    }

    private static void OversizedPool(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        byte[][] oversized = Enumerable.Range(0, 5).Select(_ => new byte[MiB]).ToArray();
        foreach (byte[] bytes in oversized) fixture.Seed(bytes);
        int accepted = 0;
        for (int i = 0; i < 5; i++)
        {
            byte[] expected = Payload(64, i + 23);
            object message = fixture.Message(expected);
            byte[]? original = fixture.Data(message);
            Check(original?.Length == MiB && oversized.Any(buffer => ReferenceEquals(buffer, original)),
                "Allocator did not reuse a seeded 1 MiB array");
            bool retained = fixture.Retain(connection, message);
            Check(retained == (api.Patched || i < 4), $"Unexpected retention result for message {i + 1}");
            if (retained) accepted++;
            Check(fixture.Data(message)?.Length == (api.Patched ? 64 : MiB), "Unexpected retained capacity");
            Check(ReferenceEquals(original, fixture.Data(message)) == !api.Patched, "Unexpected buffer identity");
            Preserved(fixture, message, expected);
            fixture.Counts(connection, accepted * (api.Patched ? 64 : MiB), accepted * (api.Patched ? 64 : MiB));
        }
        Check(accepted == (api.Patched ? 5 : 4), "Wrong accepted-message count");
        if (api.Patched)
        {
            Check(!fixture.Disconnected(connection), "Compacted messages disconnected");
            Check(!fixture.Pool().Any(buffer => oversized.Any(old => ReferenceEquals(old, buffer))),
                "Compaction returned an oversized array to the pool");
            Check(fixture.Number(fixture.Peer, "m_storagePoolBytes") == 0, "Oversized pool bytes remain");
            object next = fixture.Message(Payload(64, 99));
            Check(fixture.Data(next)?.Length == 64, "Next allocation reused discarded oversized storage");
        }
        else fixture.ExpectLimitDisconnect(connection);
        fixture.ReleaseAll();
        fixture.Counts(connection, 0, 0);
    }

    private static void BufferBoundary(Transport api, int active, int capacity)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        byte[] original = new byte[capacity];
        fixture.Seed(original);
        byte[] expected = Payload(active, 57);
        object message = fixture.Message(expected);
        Check(ReferenceEquals(original, fixture.Data(message)), "Seeded boundary buffer was not reused");
        bool compact = api.Patched && capacity - active > 1024 && capacity > 2L * active;
        Check(fixture.Retain(connection, message), "Boundary payload rejected");
        Check(ReferenceEquals(original, fixture.Data(message)) == !compact, "Compaction threshold/identity mismatch");
        Check(fixture.Data(message)?.Length == (compact ? active : capacity), "Compaction size mismatch");
        Preserved(fixture, message, expected);
        // A read cursor does not change the active payload length or accounting.
        fixture.Set(message, "m_readPosition", 17);
        Check(fixture.Number(message, "m_readPosition") == 17, "Read cursor assignment failed");
        fixture.Counts(connection, compact ? active : capacity, compact ? active : capacity);
        fixture.ReleaseAll();
        fixture.Counts(connection, 0, 0);
        if (compact) Check(!fixture.Pool().Any(buffer => ReferenceEquals(buffer, original)), "Old array re-pooled");
    }

    private static void PartialByte(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        fixture.Seed(new byte[8192]);
        byte[] expected = Payload(64, 76);
        object message = fixture.Message(expected);
        fixture.Set(message, "m_bitLength", 505);
        fixture.Set(message, "m_readPosition", 13);
        Check(fixture.Retain(connection, message), "Partial-byte message rejected");
        Preserved(fixture, message, expected);
        Check(fixture.Number(message, "m_bitLength") == 505, "Bit length changed");
        Check(fixture.Number(message, "m_readPosition") == 13, "Read position changed");
        Check(fixture.Data(message)?.Length == (api.Patched ? 64 : 8192), "Partial-byte capacity mismatch");
        fixture.ReleaseAll();
        fixture.Counts(connection, 0, 0);
    }

    private static void ConnectionCap(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        byte[] expected = Payload(ConnectionLimit, 101);
        object exact = fixture.Message(expected);
        byte[]? original = fixture.Data(exact);
        Check(original?.Length == ConnectionLimit, "4 MiB allocation not exact");
        Check(fixture.Retain(connection, exact), "Exactly 4 MiB rejected");
        Check(ReferenceEquals(original, fixture.Data(exact)), "Exact 4 MiB array replaced");
        Preserved(fixture, exact, expected);
        fixture.Counts(connection, ConnectionLimit, ConnectionLimit);
        object extra = fixture.Message(Payload(1, 102));
        Check(!fixture.Retain(connection, extra), "4 MiB plus one byte accepted");
        fixture.ExpectLimitDisconnect(connection);
        fixture.Counts(connection, ConnectionLimit, ConnectionLimit);
        fixture.ReleaseAll();
        fixture.Counts(connection, 0, 0);

        object second = fixture.Connection();
        object tooBig = fixture.Message(Payload(ConnectionLimit + 1, 103));
        Check(!fixture.Retain(second, tooBig), "Single array larger than 4 MiB accepted");
        fixture.ExpectLimitDisconnect(second);
        fixture.Counts(second, 0, 0);
    }

    private static void ActualArrayCap(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        byte[] original = new byte[ConnectionLimit + 1];
        // More than half full: compaction must not disguise a >4 MiB array.
        object message = fixture.Message(Payload(2 * MiB + 1, 104), original);
        Check(!fixture.Retain(connection, message), "Guard counted payload instead of actual allocation");
        Check(ReferenceEquals(original, fixture.Data(message)), "Noneligible oversized array replaced");
        fixture.ExpectLimitDisconnect(connection);
        fixture.Counts(connection, 0, 0);
    }

    private static void OversizedActiveLength(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        byte[] original = new byte[2 * (ConnectionLimit + 1) + 1];
        byte[] expected = Payload(ConnectionLimit + 1, 105);
        object message = fixture.Message(expected, original);
        Check(!fixture.Retain(connection, message), "Active payload over 4 MiB accepted");
        Check(ReferenceEquals(original, fixture.Data(message)), "Compaction copied an active payload over 4 MiB");
        Preserved(fixture, message, expected);
        fixture.ExpectLimitDisconnect(connection);
        fixture.Counts(connection, 0, 0);
    }

    private static void GlobalCap(Transport api)
    {
        using var fixture = new Fixture(api);
        var connections = new List<object>();
        for (int i = 0; i < 17; i++)
        {
            object connection = fixture.Connection();
            connections.Add(connection);
            // Real distinct 4 MiB arrays, not artificial counter manipulation.
            object message = fixture.Message(Payload(ConnectionLimit, i + 110));
            Check(fixture.Retain(connection, message) == (i < 16), $"Global cap result wrong at connection {i + 1}");
            fixture.Counts(connection, i < 16 ? ConnectionLimit : 0, Math.Min(i + 1, 16) * (long)ConnectionLimit);
            if (i < 16) Check(!fixture.Disconnected(connection), "Connection below global cap disconnected");
            else fixture.ExpectLimitDisconnect(connection);
        }
        Check(fixture.Number(fixture.Peer, "m_withheldIncomingBytes") == PeerLimit, "Global cap not exactly 64 MiB");
        fixture.ReleaseAll();
        foreach (object connection in connections) fixture.Counts(connection, 0, 0);
    }

    private static void ZeroLength(Transport api)
    {
        using var fixture = new Fixture(api);
        foreach (int capacity in new[] { 0, 1024, 1025, ConnectionLimit + 1 })
        {
            object connection = fixture.Connection();
            byte[] original = new byte[capacity];
            object message = fixture.Message(Array.Empty<byte>(), original);
            bool compact = api.Patched && capacity > 1024;
            bool accepted = fixture.Retain(connection, message);
            Check(accepted == (compact || capacity <= ConnectionLimit), "Zero-length retention does not match actual allocation");
            Check(ReferenceEquals(original, fixture.Data(message)) == !compact, "Zero-length compaction threshold wrong");
            Check(fixture.Data(message)?.Length == (compact ? 0 : capacity), "Zero-length compaction did not produce empty storage");
            Check(fixture.Length(message) == 0, "Zero payload length changed");
            int tracked = accepted ? (compact ? 0 : capacity) : 0;
            fixture.Counts(connection, tracked, tracked);
            if (!accepted) fixture.ExpectLimitDisconnect(connection);
            fixture.ReleaseAll();
            fixture.Counts(connection, 0, 0);
        }
    }

    private static void MalformedLengths(Transport api)
    {
        using var fixture = new Fixture(api);
        if (api.Patched)
            Invoke(RequiredMethod(api.PeerType, "CompactOrderedMessageStorage", api.MessageType), null, new object?[] { null });
        foreach (int bits in new[] { -8, int.MinValue, int.MaxValue, (8192 + 1) * 8 })
        {
            object connection = fixture.Connection();
            byte[] original = new byte[8192];
            object message = fixture.Message(Payload(64, 130), original);
            fixture.Set(message, "m_bitLength", bits);
            Check(fixture.Retain(connection, message), $"Original array guard changed for malformed length {bits}");
            Check(ReferenceEquals(original, fixture.Data(message)), $"Malformed length {bits} copied storage");
            Check(fixture.Number(message, "m_bitLength") == bits, "Malformed bit length mutated");
            fixture.Counts(connection, original.Length, original.Length);
            fixture.ReleaseAll();
            fixture.Counts(connection, 0, 0);
        }

        object nullConnection = fixture.Connection();
        object nullStorage = fixture.Message(Payload(64, 131));
        fixture.Set(nullStorage, "m_data", null);
        Check(fixture.Retain(nullConnection, nullStorage), "Original null-storage accounting changed");
        Check(fixture.Data(nullStorage) == null, "Null storage replaced");
        fixture.Counts(nullConnection, 0, 0);
        fixture.ReleaseAll();
        fixture.Counts(nullConnection, 0, 0);
    }

    private static void ReceiverOrder(Transport api, bool wrap)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        object receiver = fixture.Receiver(connection);
        int first = wrap ? 1023 : 0;
        if (wrap) fixture.Set(receiver, "m_windowStart", first);
        fixture.Seed(new byte[MiB]);
        fixture.Seed(new byte[MiB]);
        byte[][] payloads = { Payload(64, 140), Payload(64, 141), Payload(64, 142) };
        object later = fixture.Received(payloads[2], connection, (first + 2) % 1024);
        object next = fixture.Received(payloads[1], connection, (first + 1) % 1024);
        fixture.Receive(receiver, later);
        fixture.Receive(receiver, next);
        int retainedBytes = api.Patched ? 128 : 2 * MiB;
        fixture.Counts(connection, retainedBytes, retainedBytes);
        Check(fixture.Read() == null, "Early packet delivered before gap filled");

        object duplicate = fixture.Received(Payload(64, 199), connection, (first + 1) % 1024);
        fixture.Receive(receiver, duplicate);
        fixture.Counts(connection, retainedBytes, retainedBytes);
        Check(fixture.Withheld(receiver).Count(message => message != null) == 2, "Duplicate changed withheld count");
        Check(ReferenceEquals(fixture.Withheld(receiver)[(first + 1) % 1024 % 64], next), "Duplicate replaced original packet");

        object initial = fixture.Received(payloads[0], connection, first);
        fixture.Receive(receiver, initial);
        fixture.Counts(connection, 0, 0);
        Check(fixture.Withheld(receiver).All(message => message == null), "Released slots not cleared");
        object[] expected = { initial, next, later };
        for (int i = 0; i < expected.Length; i++)
        {
            object? actual = fixture.Read();
            Check(ReferenceEquals(actual, expected[i]), "Receiver delivered wrong order or duplicate");
            Preserved(fixture, actual!, payloads[i]);
        }
        Check(fixture.Read() == null, "Unexpected fourth delivered message");
        Check(fixture.Number(fixture.Peer, "m_pendingIncomingBytes") == 0, "Released queue accounting did not drain");

        fixture.Receive(receiver, fixture.Received(Payload(64, 200), connection, (first + 1) % 1024));
        fixture.Receive(receiver, fixture.Received(Payload(64, 201), connection, (first + 3 + 64) % 1024));
        Check(fixture.Read() == null, "Late duplicate/outside-window message delivered");
        fixture.Counts(connection, 0, 0);
        Check(!fixture.Disconnected(connection), "Reordering or duplicate disconnected");
    }

    private static void ReceiverDiscard(Transport api)
    {
        using var fixture = new Fixture(api);
        object connection = fixture.Connection();
        object receiver = fixture.Receiver(connection);
        byte[][] oversized = { new byte[MiB], new byte[MiB] };
        foreach (byte[] buffer in oversized) fixture.Seed(buffer);
        fixture.Receive(receiver, fixture.Received(Payload(64, 210), connection, 1));
        fixture.Receive(receiver, fixture.Received(Payload(64, 211), connection, 3));
        fixture.Counts(connection, api.Patched ? 128 : 2 * MiB, api.Patched ? 128 : 2 * MiB);
        fixture.Discard(receiver);
        fixture.Counts(connection, 0, 0);
        Check(fixture.Withheld(receiver).All(message => message == null), "Discard left withheld references");
        fixture.Discard(receiver);
        fixture.Counts(connection, 0, 0);
        Check(fixture.Read() == null, "Discard delivered withheld messages");
        if (api.Patched)
            Check(!fixture.Pool().Any(buffer => oversized.Any(old => ReferenceEquals(old, buffer))), "Discard re-pooled oversized array");
        object zero = fixture.Received(Payload(64, 212), connection, 0);
        object one = fixture.Received(Payload(64, 213), connection, 1);
        fixture.Receive(receiver, zero);
        Check(ReferenceEquals(fixture.Read(), zero), "Discard left stale early-received bit");
        Check(fixture.Read() == null, "Discard caused stale delivery");
        fixture.Receive(receiver, one);
        Check(ReferenceEquals(fixture.Read(), one), "Receiver unusable after discard");
        Check(fixture.Read() == null, "Unexpected cleanup delivery");
        fixture.Counts(connection, 0, 0);
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(name, Members | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    private static MethodInfo RequiredMethod(Type type, string name, params Type[] arguments) =>
        type.GetMethod(name, Members, binder: null, types: arguments, modifiers: null) ??
        throw new MissingMethodException(type.FullName, name);

    private static object? Invoke(MethodInfo method, object? target, params object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private sealed class Transport
    {
        public readonly Assembly Assembly;
        public readonly bool Patched;
        public readonly Type PeerType, ConfigurationType, ConnectionType, MessageType, KindType, ReceiverType;
        public readonly object DataKind, ConnectedStatus, OrderedKind;
        public readonly MethodInfo Create, CreateWithStorage, Write, Retain, Release, RecycleBytes, RecycleMessage, Read;

        public Transport(Assembly assembly, bool patched)
        {
            Assembly = assembly;
            Patched = patched;
            Type Named(string name) => assembly.GetType("Lidgren.Network." + name, throwOnError: true)!;
            PeerType = Named("NetPeer");
            ConfigurationType = Named("NetPeerConfiguration");
            ConnectionType = Named("NetConnection");
            MessageType = Named("NetIncomingMessage");
            KindType = Named("NetIncomingMessageType");
            ReceiverType = Named("NetReliableOrderedReceiver");
            DataKind = Enum.Parse(KindType, "Data");
            ConnectedStatus = Enum.Parse(Named("NetConnectionStatus"), "Connected");
            OrderedKind = Enum.Parse(Named("NetMessageType"), "UserReliableOrdered1");
            Create = RequiredMethod(PeerType, "CreateIncomingMessage", KindType, typeof(int));
            CreateWithStorage = RequiredMethod(PeerType, "CreateIncomingMessage", KindType, typeof(byte[]));
            Write = RequiredMethod(MessageType, "Write", typeof(byte[]));
            Retain = RequiredMethod(PeerType, "TryRetainOrderedMessage", ConnectionType, MessageType);
            Release = RequiredMethod(PeerType, "ReleaseOrderedMessageStorage", ConnectionType, MessageType);
            RecycleBytes = RequiredMethod(PeerType, "Recycle", typeof(byte[]));
            RecycleMessage = RequiredMethod(PeerType, "Recycle", MessageType);
            Read = RequiredMethod(PeerType, "ReadMessage");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Transport api;
        private readonly List<(object Connection, object Message)> held = new();
        private readonly List<object> receivers = new();
        private readonly List<object> connections = new();
        public readonly object Peer;

        public Fixture(Transport transport)
        {
            api = transport;
            object configuration = Activator.CreateInstance(api.ConfigurationType, new object[] { "isolated-transport-regression" })!;
            MethodInfo disable = RequiredMethod(api.ConfigurationType, "DisableMessageType", api.KindType);
            foreach (string kind in new[] { "StatusChanged", "DebugMessage", "VerboseDebugMessage", "WarningMessage", "ErrorMessage" })
                Invoke(disable, configuration, Enum.Parse(api.KindType, kind));
            Peer = Activator.CreateInstance(api.PeerType, new[] { configuration })!;
            Invoke(RequiredMethod(api.PeerType, "InitializePools"), Peer);
            Check(RequiredField(api.PeerType, "m_releasedIncomingMessages").GetValue(Peer) != null,
                "Peer constructor did not initialize release queue");
            Check(RequiredField(api.PeerType, "m_incomingQueueLock").GetValue(Peer) != null,
                "Peer constructor did not initialize queue lock");
            NoNetwork();
        }

        public object Connection()
        {
            ConstructorInfo constructor = api.ConnectionType.GetConstructor(Members, binder: null,
                types: new[] { api.PeerType, typeof(IPEndPoint) }, modifiers: null) ??
                throw new MissingMethodException("NetConnection(NetPeer, IPEndPoint)");
            object connection = constructor.Invoke(new object[] { Peer, new IPEndPoint(IPAddress.Loopback, 1) });
            Set(connection, "m_status", api.ConnectedStatus);
            connections.Add(connection);
            return connection;
        }

        public object Message(byte[] payload, byte[]? storage = null)
        {
            object message = storage == null
                ? Invoke(api.Create, Peer, api.DataKind, payload.Length)!
                : Invoke(api.CreateWithStorage, Peer, api.DataKind, storage)!;
            Invoke(api.Write, message, new object?[] { payload });
            return message;
        }

        public object Received(byte[] payload, object connection, int sequence)
        {
            object message = Message(payload);
            Set(message, "m_sequenceNumber", sequence);
            Set(message, "m_receivedMessageType", api.OrderedKind);
            Set(message, "m_senderConnection", connection);
            return message;
        }

        public object Receiver(object connection)
        {
            ConstructorInfo constructor = api.ReceiverType.GetConstructor(Members, binder: null,
                types: new[] { api.ConnectionType, typeof(int) }, modifiers: null) ??
                throw new MissingMethodException("NetReliableOrderedReceiver(NetConnection, int)");
            object receiver = constructor.Invoke(new[] { connection, (object)64 });
            receivers.Add(receiver);
            return receiver;
        }

        public object?[] Withheld(object receiver) =>
            ((Array)RequiredField(api.ReceiverType, "m_withheldMessages").GetValue(receiver)!).Cast<object?>().ToArray();
        public void Receive(object receiver, object message) =>
            Invoke(RequiredMethod(api.ReceiverType, "ReceiveMessage", api.MessageType), receiver, message);
        public void Discard(object receiver) => Invoke(RequiredMethod(api.ReceiverType, "DiscardMessages"), receiver);
        public void Seed(byte[] bytes) => Invoke(api.RecycleBytes, Peer, new object?[] { bytes });
        public IEnumerable<byte[]> Pool() =>
            ((IEnumerable)RequiredField(api.PeerType, "m_storagePool").GetValue(Peer)!).Cast<object?>().OfType<byte[]>();
        public object? Read() => Invoke(api.Read, Peer);
        public byte[]? Data(object message) => (byte[]?)RequiredField(api.MessageType, "m_data").GetValue(message);
        public int Length(object message) => (int)api.MessageType.GetProperty("LengthBytes", Members)!.GetValue(message)!;
        public long Number(object target, string name) => Convert.ToInt64(RequiredField(target.GetType(), name).GetValue(target));
        public void Set(object target, string name, object? value) => RequiredField(target.GetType(), name).SetValue(target, value);
        public bool Disconnected(object connection) => (bool)RequiredField(api.ConnectionType, "m_disconnectRequested").GetValue(connection)!;

        public bool Retain(object connection, object message)
        {
            bool accepted = (bool)Invoke(api.Retain, Peer, connection, message)!;
            if (accepted) held.Add((connection, message));
            return accepted;
        }

        public void Counts(object connection, long expectedConnection, long expectedPeer)
        {
            long actualConnection = Number(connection, "m_withheldIncomingBytes");
            long actualPeer = Number(Peer, "m_withheldIncomingBytes");
            Check(actualConnection == expectedConnection,
                $"Connection bytes: expected {expectedConnection}, got {actualConnection}");
            Check(actualPeer == expectedPeer, $"Peer bytes: expected {expectedPeer}, got {actualPeer}");
        }

        public void ExpectLimitDisconnect(object connection)
        {
            Check(Disconnected(connection), "Limit did not request disconnect");
            Check((string?)RequiredField(api.ConnectionType, "m_disconnectMessage").GetValue(connection) == LimitReason,
                "Disconnect reason is not exactly: " + LimitReason);
        }

        public void ReleaseAll()
        {
            foreach ((object connection, object message) in held) Invoke(api.Release, Peer, connection, message);
            held.Clear();
        }

        private void NoNetwork()
        {
            Check(api.PeerType.GetProperty("Status", Members)!.GetValue(Peer)!.ToString() == "NotRunning", "Peer started");
            Check(RequiredField(api.PeerType, "m_socket").GetValue(Peer) == null, "Socket created");
            Check(RequiredField(api.PeerType, "m_networkThread").GetValue(Peer) == null, "Network thread created");
        }

        public void Dispose()
        {
            NoNetwork();
            foreach (object receiver in receivers) Discard(receiver);
            ReleaseAll();
            int drained = 0;
            for (object? message = Read(); message != null; message = Read())
            {
                Check(++drained <= 4096, "Unexpected unbounded release queue");
                Invoke(api.RecycleMessage, Peer, message);
            }
            foreach (object connection in connections) Counts(connection, 0, 0);
            Check(Number(Peer, "m_pendingIncomingBytes") == 0, "Queue accounting remains after cleanup");
            NoNetwork();
        }
    }
}
