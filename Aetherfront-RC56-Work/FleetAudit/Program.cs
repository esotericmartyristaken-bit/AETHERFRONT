using System.Runtime.CompilerServices;
using System.Text.Json;
using Aetherfront.Core;
using Aetherfront.Synchronization.Server;
using Server.Context;
using Server.System;

if (args.Length != 2) throw new ArgumentException("NEW_ISOLATED_AUDIT_DIRECTORY READ_ONLY_CANDIDATE_RUNTIME");
string output = Path.GetFullPath(args[0]);
if (!output.StartsWith(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\", StringComparison.OrdinalIgnoreCase) || Directory.Exists(output))
    throw new IOException("Fresh isolated audit directory required.");
string runtime = Path.GetFullPath(args[1]);
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => {
    string path = Path.Combine(runtime, name.Name + ".dll");
    return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
Run();
[MethodImpl(MethodImplOptions.NoInlining)] void Run()
{
    const string production = @"C:\Users\Administrator\Desktop\APF\KESAS-VPS-Server\Server";
    using var input = new FileStream(Path.Combine(production, "Aetherfront/state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(input);
    var state = AetherfrontJson.Deserialize<AetherfrontState>(reader.ReadToEnd());
    string before = AetherfrontJson.Serialize(state);
    var account = state.Accounts.Single(a => a.GetType().GetProperties().Any(p => p.PropertyType == typeof(string) && string.Equals(p.GetValue(a) as string, "HD9652AirFryer", StringComparison.OrdinalIgnoreCase)));
    string actor = account.AccountId;
    Directory.CreateDirectory(Path.Combine(output, "Vessels")); Directory.CreateDirectory(Path.Combine(output, "DeepFreeze"));
    foreach (string path in Directory.GetFiles(Path.Combine(production, "Universe/Vessels"), "*.txt"))
        if (Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id))
            VesselStoreSystem.CurrentVessels[id] = (Server.System.Vessel.Classes.Vessel)RuntimeHelpers.GetUninitializedObject(typeof(Server.System.Vessel.Classes.Vessel));
    string scenario = Path.Combine(production, "Universe/DeepFreeze/Scenario.txt");
    if (File.Exists(scenario)) File.Copy(scenario, Path.Combine(output, "DeepFreeze/Scenario.txt"));
    ServerContext.UniverseDirectory = output; VesselStoreSystem.VesselsPath = Path.Combine(output, "Vessels");
    var selected = CheckpointFleet.Owned(state, actor);
    if (AetherfrontJson.Serialize(state) != before) throw new Exception("Audit changed source registry memory.");
    var result = new { utc = DateTime.UtcNow, productionReadOnly = true, sourceRegistryUnchanged = true,
        registered = state.Crafts.Count(c => c.OwnerAccountId == actor && !c.Archived), liveCheckpoint = selected.Count,
        status = CheckpointFleet.Status("", actor), recoveryArchives = Directory.GetFiles(Path.Combine(output, "FlightCheckpoints/RegistryHistory"), "*.json").Length };
    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
}
