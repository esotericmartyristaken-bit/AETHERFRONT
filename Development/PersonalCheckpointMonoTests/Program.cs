using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using Aetherfront.Personal.Client;

internal static class Program
{
    static int checks;
    static string candidate, game;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string root in new[] { candidate, Path.Combine(game, "KSP_x64_Data/Managed"), Path.Combine(game, "GameData/LunaMultiplayer/Plugins") })
        { string path = Path.Combine(root, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
        return null;
    }
    static void Main()
    {
        candidate = Environment.GetEnvironmentVariable("APF_CHECKPOINT_CANDIDATE");
        game = Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        if (!Directory.Exists(candidate) || !Directory.Exists(game)) throw new ArgumentException("Explicit isolated runtime and reviewed candidate required.");
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var type = typeof(FlightCheckpointClient);
        Check(type.Assembly.GetName().Name == "AetherfrontPersonalClient", "Shipping assembly identity");
        foreach (var item in type.Assembly.GetTypes().Where(t => t.FullName.StartsWith(type.FullName, StringComparison.Ordinal)))
        foreach (var method in item.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.GetMethodBody() == null || method.ContainsGenericParameters) continue;
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            checks++;
        }
        var stock = typeof(QuickSaveLoad).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "QuickSave" || m.Name == "QuickLoad").ToArray();
        Check(stock.Length > 0, "Actual KSP quicksave targets exist");
        Check(FlightCheckpointClient.AllowStockCheckpoint(), "Offline stock quicksave remains available");
        string id = Guid.NewGuid().ToString("N");
        string point = "{\"CheckpointId\":\"" + id + "\",\"Label\":\"1 minute ago\",\"SavedUtc\":\"2026-11-09T21:00:00Z\",\"MissionTime\":1000,\"VesselCount\":2}";
        string reply = "{\"ProtocolVersion\":1,\"ServerId\":\"isolated-checkpoint\",\"Accepted\":true,\"ReasonCode\":\"\",\"FlightCheckpoint\":{\"RequestId\":\"" + id + "\",\"CheckpointId\":\"" + id + "\",\"SavedUtc\":\"2026-11-09T21:00:00Z\",\"MissionTime\":1000,\"VesselCount\":2,\"Generation\":0,\"Points\":[" + point + "]}}";
        Check(FlightCheckpointClient.Capture(reply), "Native Mono parses a server checkpoint receipt");
        var queue = (ICollection)type.GetField("Receipts", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Check(queue.Count == 1, "Network receipt queues without accessing Unity");
        Check(!FlightCheckpointClient.Capture("{\"FlightCheckpoint\":null}"), "Incomplete receipt ignored");
        Check(!FlightCheckpointClient.Capture(reply.Replace("\"VesselCount\":2", "\"VesselCount\":513")), "Out-of-range receipt ignored");
        for (int i = 0; i < 30; i++) Check(FlightCheckpointClient.Capture(reply), "Repeated metadata receipt accepted");
        Check(queue.Count == 16, "Queued metadata stays bounded");
        Console.WriteLine("PASS " + checks + " shipped-client actual-KSP Mono checks; Unity scene and live deployment not claimed.");
    }
}
