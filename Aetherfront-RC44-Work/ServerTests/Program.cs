using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aetherfront.Continuity;
using Aetherfront.Continuity.Server;
using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Warp;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;

// Compile the existing five-file integration harness unchanged. Reuse its
// authenticated synthetic client factory, never its resource/scale/DF entry point.
internal static class MissionTimeProgram
{
    const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    const string ShippingHash = "98a5541f2701b7961039da3261f6c4bb74149d313f33dcafbeafb6950d824fa1";
    const string CandidateHash = "ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9";
    static string root, caseRoot;
    static bool baseline;
    static int assertions;
    static readonly List<object> results = new List<object>();
    static readonly List<object> timings = new List<object>();
    static IDictionary Sessions => (IDictionary)typeof(PersonalClockServer).GetField("Sessions", Private).GetValue(null);
    static string Metadata(string key) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == key).Value;
    static void Check(bool value, string name) { assertions++; if (!value) throw new InvalidOperationException(name); }
    static void Equal<T>(T expected, T actual, string name) => Check(EqualityComparer<T>.Default.Equals(expected, actual), name + "; expected=" + expected + "; actual=" + actual);
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static double LiveTime(ClientStructure client) => PersonalClockServer.ServerSeconds + WarpContext.Subspaces[client.Subspace].Time;
    static bool Disconnected(ClientStructure client) => client.DisconnectClient || client.ConnectionStatus == ConnectionStatus.Disconnected;
    static string ClockPath => Directory.GetFiles(Path.Combine(ServerContext.UniverseDirectory, "AetherfrontTime"), "*.clock").Single();
    static ClockStore Store => new ClockStore(ServerContext.UniverseDirectory);
    static double Durable(ClientStructure c) => Store.Resume(c.AetherfrontAccountId, 900000000);
    static void RequirePrivate(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture escaped isolated run root: " + full);
        for (string p = Path.GetDirectoryName(full); p != null && p.StartsWith(root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture reparse point forbidden");
    }
    static void Setup(string name)
    {
        caseRoot = Path.Combine(root, results.Count.ToString("D2") + "-" + name); RequirePrivate(caseRoot);
        if (Directory.Exists(caseRoot)) throw new IOException("Never overwrite test evidence");
        Directory.CreateDirectory(caseRoot); Directory.SetCurrentDirectory(caseRoot);
        ServerContext.ConfigDirectory = Path.Combine(caseRoot, "Config"); Directory.CreateDirectory(ServerContext.ConfigDirectory);
        ServerContext.UniverseDirectory = Path.Combine(caseRoot, "Universe"); Directory.CreateDirectory(ServerContext.UniverseDirectory);
        ServerContext.Clients.Clear(); Sessions.Clear();
        // Keep native last-player BackupSystem.RunBackup unreachable. This
        // unauthenticated sentinel has no constructor, sender task, peer or socket.
        var sentinel = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
        sentinel.ConnectionStatus = ConnectionStatus.Disconnected;
        var sentinelEndpoint = new IPEndPoint(IPAddress.Loopback, 39999);
        var sentinelConnection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
        typeof(NetConnection).GetField("m_remoteEndPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sentinelConnection, sentinelEndpoint);
        typeof(ClientStructure).GetField("<Connection>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sentinel, sentinelConnection);
        ServerContext.Clients.TryAdd(sentinelEndpoint, sentinel);
        ((IDictionary)typeof(PersonalClockServer).GetField("Stores", Private).GetValue(null)).Clear();
        GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox; GeneralSettings.SettingsStore.ModControl = false;
        typeof(AetherfrontServerSystem).GetMethod("Initialize", Private, null, new[] { typeof(string), typeof(string) }, null).Invoke(null, new object[] { caseRoot, "mission-time-integration" });
        TimeContext.StartTime = DateTime.UtcNow;
        WarpContext.Subspaces.Clear(); WarpContext.Subspaces.TryAdd(0, new Subspace(0, 1000)); WarpContext.NextSubspaceId = 1;
    }
    static ClientStructure Client(string name = "ClockFixture")
    {
        var client = (ClientStructure)typeof(Program).GetMethod("Client", Private).Invoke(null, new object[] { name + "_" + results.Count.ToString("D2") });
        // The legacy client deliberately has no transport. Supply only Endpoint
        // for the actual disconnect bookkeeping, never construct a NetPeer.
        var connection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
        typeof(NetConnection).GetField("m_remoteEndPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(connection,
            ServerContext.Clients.Single(p => object.ReferenceEquals(p.Value, client)).Key);
        typeof(ClientStructure).GetField("<Connection>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, connection);
        Check(typeof(NetConnection).GetField("m_peer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(connection) == null && connection.Status == NetConnectionStatus.None,
            "Managed endpoint shell has no peer/socket; transport disconnect remains a no-op");
        // Actual shipping server hook, not a reimplementation of clock admission.
        Server.System.WarpSystemSender.SendAllSubspaces(client);
        Check(!Disconnected(client) && Sessions.Contains(client), "Actual server hook admitted authenticated private clock session");
        Check(client.Subspace > 0, "Actual server hook allocated isolated subspace");
        return client;
    }
    static void Case(string name, Action body)
    {
        int before = assertions;
        try { Setup(name); body(); results.Add(new { name, passed = true, assertions = assertions - before }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { results.Add(new { name, passed = false, assertions = assertions - before, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    }
    static void NoTemps() => Equal(0, Directory.GetFiles(Path.Combine(ServerContext.UniverseDirectory, "AetherfrontTime"), "*.tmp").Length, "ClockStore finally cleans only its synthetic temporary record");
    static void FailedSafely(ClientStructure c, byte[] previous)
    {
        Check(Disconnected(c), "Existing fail-closed behavior disconnects client");
        Check(!Sessions.Contains(c), "Failure removes session before disconnect hook can reenter");
        Check(!PersonalClockServer.MayPersistPosition(c), "Failed session loses position-persistence authority");
        Check(previous.SequenceEqual(File.ReadAllBytes(ClockPath)), "Last durable clock bytes unchanged"); NoTemps();
    }
    static void TransientReadLock()
    {
        var c = Client(); string path = ClockPath; byte[] previous = File.ReadAllBytes(path); double saved = Durable(c);
        WarpContext.Subspaces[c.Subspace].Time += 25;
        Task task; var watch = Stopwatch.StartNew();
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var entered = new ManualResetEventSlim())
        {
            task = Task.Run(() => { entered.Set(); PersonalClockServer.Checkpoint(c); });
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "Checkpoint worker entered without starting a server worker/listener");
            if (baseline) Check(task.Wait(TimeSpan.FromSeconds(1)), "Original File.Replace fails immediately under read lock");
            else Check(!task.Wait(TimeSpan.FromMilliseconds(200)), "Candidate retains checkpoint while metadata lock is held");
            Check(previous.SequenceEqual(File.ReadAllBytes(path)), "Read lock preserves previous bytes until released");
        }
        Check(task.Wait(TimeSpan.FromSeconds(4)), "Checkpoint completes after transient read lock release"); watch.Stop();
        timings.Add(new { name = "transient-read-lock", elapsedMilliseconds = watch.ElapsedMilliseconds, disconnected = Disconnected(c) });
        if (baseline) { FailedSafely(c, previous); Equal(saved, Durable(c), "Original failed replacement retains durable time"); }
        else
        {
            Check(!Disconnected(c), "Transient replacement conflict no longer disconnects flight");
            Check(Sessions.Contains(c) && PersonalClockServer.MayPersistPosition(c), "Successful retry retains existing session/authority");
            Check(Durable(c) >= saved + 24 && Durable(c) <= saved + 28, "Retry persists same bounded proposed flight time");
            Check(!previous.SequenceEqual(File.ReadAllBytes(path)), "Successful retry replaced durable record"); NoTemps();
        }
    }
    static void PermanentReadLock()
    {
        var c = Client(); string path = ClockPath; byte[] previous = File.ReadAllBytes(path); double saved = Durable(c);
        WarpContext.Subspaces[c.Subspace].Time += 25;
        var watch = Stopwatch.StartNew(); Task task;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            task = Task.Run(() => PersonalClockServer.Checkpoint(c));
            bool completed = task.Wait(TimeSpan.FromSeconds(5));
            Check(completed, "Permanent lock cannot cause unbounded retry");
            watch.Stop(); FailedSafely(c, previous);
        }
        Check(task.IsCompleted, "No checkpoint task escapes fixture");
        if (!baseline) Check(watch.ElapsedMilliseconds >= 1700 && watch.ElapsedMilliseconds < 4500, "Candidate metadata retry bounded near 2 seconds");
        Equal(saved, Durable(c), "Long lock preserves exact last durable UT");
        PersonalClockServer.BeforeDisconnect(c); Equal(saved, Durable(c), "Reentrant disconnect cannot advance failed session");
        timings.Add(new { name = "permanent-read-lock", elapsedMilliseconds = watch.ElapsedMilliseconds, disconnected = Disconnected(c) });
    }
    static void CorruptRecord()
    {
        var c = Client(); string path = ClockPath; byte[] previous = File.ReadAllBytes(path); double saved = Durable(c);
        string good = Encoding.ASCII.GetString(previous); string[] lines = good.Split('\n'); lines[4] = new string('0', 64);
        byte[] corrupt = Encoding.ASCII.GetBytes(string.Join("\n", lines)); File.WriteAllBytes(path, corrupt);
        File.WriteAllBytes(Path.Combine(caseRoot, "last-good.clock"), previous);
        var watch = Stopwatch.StartNew(); PersonalClockServer.Checkpoint(c); watch.Stop();
        Check(Disconnected(c) && !Sessions.Contains(c), "Corruption still disconnects and clears session");
        Check(corrupt.SequenceEqual(File.ReadAllBytes(path)), "Corrupt evidence never overwritten by retry");
        Check(watch.ElapsedMilliseconds < 1500, "Validation failure does not enter File.Replace retry");
        bool rejected = false; try { Durable(c); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Corrupt record cannot resume");
        string recovery = Path.Combine(caseRoot, "read-only-last-good-evidence"); string recoveryDir = Path.Combine(recovery, "AetherfrontTime"); Directory.CreateDirectory(recoveryDir);
        File.WriteAllBytes(Path.Combine(recoveryDir, Path.GetFileName(path)), previous);
        Equal(saved, new ClockStore(recovery).Resume(c.AetherfrontAccountId, 900000000), "Preserved last-good fixture retains exact durable UT"); NoTemps();
        timings.Add(new { name = "corrupt-record", elapsedMilliseconds = watch.ElapsedMilliseconds });
    }
    public static int Main(string[] args)
    {
        if (args.Length != 2 || (args[0] != "--baseline" && args[0] != "--candidate") || !Path.IsPathRooted(args[1])) throw new ArgumentException("--baseline|--candidate NEW_PRIVATE_ROOT");
        baseline = args[0] == "--baseline"; root = Path.GetFullPath(args[1]);
        if (!root.StartsWith(Path.GetFullPath(Metadata("Artifacts")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Private evidence must remain under MissionTime-20261104/server-tests");
        if (Directory.Exists(root)) throw new IOException("Never overwrite existing fixture run");
        Directory.CreateDirectory(root); Directory.SetCurrentDirectory(root);
        string runtime = Metadata("RuntimeInput");
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) => { string path = Path.Combine(runtime, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null; };
        string actualContinuity = typeof(ClockStore).Assembly.Location;
        Equal(baseline ? ShippingHash : CandidateHash, Hash(actualContinuity), "Actual loaded continuity hash pinned");
        Equal(Hash(Path.Combine(runtime, "Server.dll")), Hash(typeof(ClientStructure).Assembly.Location), "Actual shipping Server.dll loaded");
        Equal(Hash(Path.Combine(runtime, "AetherfrontCore.dll")), Hash(typeof(Aetherfront.Core.AetherfrontState).Assembly.Location), "Actual current Core loaded");
        Case("existing-flight-checkpoint-and-peer-isolation", () =>
        {
            var alice = Client("ClockAlice"); var bob = (ClientStructure)typeof(Program).GetMethod("Client", Private).Invoke(null, new object[] { "ClockBob" });
            Server.System.WarpSystemSender.SendAllSubspaces(bob);
            Check(alice.Subspace != bob.Subspace, "Existing independent personal subspaces retained");
            double before = LiveTime(alice); WarpContext.Subspaces[bob.Subspace].Time += 100000000;
            Check(PersonalClockServer.MayPersistPosition(alice), "Past-timeline authenticated position remains allowed");
            PersonalClockServer.Checkpoint(alice); Check(Math.Abs(Durable(alice) - before) < 1, "Peer warp does not advance personal checkpoint"); NoTemps();
        });
        Case("existing-offline-resume-does-not-advance", () =>
        {
            var c = Client(); PersonalClockServer.BeforeDisconnect(c); double saved = Durable(c); int old = c.Subspace;
            Check(!Sessions.Contains(c), "Normal disconnect clears session"); c.Subspace = int.MinValue; TimeContext.StartTime = TimeContext.StartTime.AddDays(-90);
            Server.System.WarpSystemSender.SendAllSubspaces(c);
            Check(!Disconnected(c) && Sessions.Contains(c), "Existing resume hook accepts original bound identity");
            Check(c.Subspace != old && Math.Abs(LiveTime(c) - saved) < 1, "90 offline days cannot advance personal mission UT");
            Equal(saved, Durable(c), "Resume alone does not advance durable clock");
        });
        Case("flight-checkpoint-transient-read-lock", TransientReadLock);
        Case("flight-checkpoint-long-lock-fails-closed", PermanentReadLock);
        Case("corruption-fails-closed-with-evidence", CorruptRecord);
        Case("rollback-guard-unchanged", () =>
        {
            var c = Client(); byte[] previous = File.ReadAllBytes(ClockPath); double saved = Durable(c);
            WarpContext.Subspaces[c.Subspace].Time = saved - PersonalClockServer.ServerSeconds - 30;
            var watch = Stopwatch.StartNew(); PersonalClockServer.Checkpoint(c); watch.Stop(); FailedSafely(c, previous);
            Equal(saved, Durable(c), "Rollback never modifies durable UT"); Check(watch.ElapsedMilliseconds < 1500, "Rollback validation does not enter metadata retry");
        });
        Case("missing-active-subspace-still-fails-closed", () =>
        {
            var c = Client(); byte[] previous = File.ReadAllBytes(ClockPath); WarpContext.Subspaces.TryRemove(c.Subspace, out _); PersonalClockServer.Checkpoint(c); FailedSafely(c, previous);
        });
        Case("mid-warp-checkpoint-retains-stable-time", () =>
        {
            var c = Client(); PersonalClockServer.Checkpoint(c); double saved = Durable(c); c.Subspace = -1;
            PersonalClockServer.Checkpoint(c); Check(!Disconnected(c), "Mid-warp checkpoint does not disconnect"); Equal(saved, Durable(c), "Mid-warp uses previous stable UT");
            PersonalClockServer.BeforeDisconnect(c); Check(!Sessions.Contains(c), "Mid-warp disconnect clears session"); Equal(saved, Durable(c), "Mid-warp disconnect preserves stable UT");
        });
        int failures = results.Count(r => !(bool)r.GetType().GetProperty("passed").GetValue(r));
        int legacyAssertions = (int)typeof(Program).GetField("checks", Private).GetValue(null);
        var binaries = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location)).Select(a => new { name = a.GetName().Name, path = a.Location, sha256 = Hash(a.Location) }).ToArray();
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { mode = baseline ? "baseline" : "candidate", passed = results.Count - failures, failed = failures, assertions, legacyAuthenticatedClientAssertions = legacyAssertions, totalAssertions = assertions + legacyAssertions, timings, binaries, cases = results, sockets = 0, serverListeners = 0, productionInitialized = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("RESULT " + (baseline ? "baseline" : "candidate") + " passed=" + (results.Count - failures) + " failed=" + failures + " assertions=" + (assertions + legacyAssertions));
        Console.WriteLine("FIXTURES " + root);
        return failures == 0 ? 0 : 1;
    }
}
