using Server.Aetherfront;

var root = Path.Combine(Path.GetTempPath(), "apf-checkpoint-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException(name);
    checks++;
}
const string owner = "fixture-owner-a";
Guid first = Guid.NewGuid(), second = Guid.NewGuid();
var now = DateTimeOffset.UtcNow;
PersonalFlightCheckpoints.Snapshot Snapshot(Guid id, string body = "Mun") => new()
{
    Id = id, Owner = owner, CapturedUtc = now, MissionTime = 432100,
    RegistrationsJson = "[]",
    Vessels = new()
    {
        new() { Id = first, Proto = $"pid = {first:N}\nname = {body} lander\n" },
        new() { Id = second, Proto = $"pid = {second:N}\nname = Station\n" }
    }
};
void Rejected(Action operation, string name)
{
    try { operation(); throw new Exception(name); }
    catch (InvalidDataException) { checks++; }
}
try
{
    Guid id = Guid.NewGuid();
    var store = new PersonalFlightCheckpoints(root);
    Check(store.Read(owner, id) == null, "New player has no checkpoint");
    store.Save(Snapshot(id), Guid.Empty);
    var restarted = new PersonalFlightCheckpoints(root);
    var restored = restarted.Read(owner, id);
    Check(restored?.MissionTime == 432100, "Personal mission time persists across store restart");
    Check(restored?.Vessels.Count == 2 && restored.Vessels[0].Id == first && restored.Vessels[1].Id == second,
        "The checkpoint retains the entire personal fleet");
    Check(restored.CapturedUtc == now, "Checkpoint UTC is retained exactly");
    Check(restarted.Read("other-owner", id) == null, "Accounts have separate checkpoints");
    Check(new PersonalFlightCheckpoints(Path.Combine(root, "other-universe")).Read(owner, id) == null,
        "Universe namespaces have separate checkpoints");
    Check(restarted.Save(Snapshot(id, "Duna"), Guid.Empty).Vessels[0].Proto.Contains("Mun"), "Duplicate save retains original snapshot");
    Guid latest = Guid.NewGuid();
    restarted.Save(Snapshot(latest, "Duna"), id);
    Check(restarted.Read(owner, id) == null, "Older request cannot select the latest checkpoint");
    Rejected(() => restarted.Save(Snapshot(id), Guid.Empty), "Delayed request overwrote latest checkpoint");
    Rejected(() => restarted.Save(Snapshot(Guid.NewGuid()), id), "Stale simultaneous save overwrote latest checkpoint");
    Check(restarted.Read(owner, latest).Vessels[0].Proto.Contains("Duna"), "Delayed request preserves latest fleet");
    var oversized = Snapshot(Guid.NewGuid()); oversized.Vessels[0].Proto = new string('x', 2097153);
    Rejected(() => restarted.Save(oversized, latest), "Oversized snapshot accepted");
    var duplicate = Snapshot(Guid.NewGuid()); duplicate.Vessels[1].Id = first;
    Rejected(() => restarted.Save(duplicate, latest), "Duplicate vessel accepted");
    var invalidTime = Snapshot(Guid.NewGuid()); invalidTime.MissionTime = double.NaN;
    Rejected(() => restarted.Save(invalidTime, latest), "Invalid mission time accepted");
    Check(restarted.Read(owner, latest).Vessels.Count == 2, "Rejected save preserves whole prior checkpoint");
    string file = Directory.GetFiles(Path.Combine(root, "FlightCheckpoints"), "*.json").Single();
    byte[] valid = File.ReadAllBytes(file);
    byte[] damaged = (byte[])valid.Clone();
    damaged["APF-FLIGHT-3\n".Length] = damaged["APF-FLIGHT-3\n".Length] == (byte)'A' ? (byte)'B' : (byte)'A';
    File.WriteAllBytes(file, damaged);
    Rejected(() => restarted.Read(owner, latest), "Damaged snapshot passed integrity check");
    File.WriteAllBytes(file, valid);
    Guid launchId = Guid.NewGuid();
    for (int minute = 0; minute <= 40; minute++)
    {
        var point = Snapshot(minute == 0 ? launchId : Guid.NewGuid());
        point.CapturedUtc = now.AddMinutes(minute);
        point.MissionTime = 1000 + minute;
        restarted.SaveTimed(point, minute == 0);
    }
    var manual = restarted.ReadLatest(owner);
    var available = restarted.Available(owner, now.AddMinutes(40), manual);
    Check(available.Count == 5, "Four automatic choices and one manual checkpoint are offered");
    Check(available.Single(point => point.Label == "1 minute ago").MissionTime == 1039, "One-minute rolling point uses the completed minute");
    Check(available.Single(point => point.Label == "5 minutes ago").MissionTime == 1035, "Five-minute rolling point is retained");
    Check(available.Single(point => point.Label == "30 minutes ago").MissionTime == 1010, "Thirty-minute rolling point is retained");
    Check(available.Single(point => point.Label == "Latest launch").Id == launchId, "Launch remains pinned after history rotation");
    Check(restarted.Read(owner, launchId)?.MissionTime == 1000, "An older launch can still be loaded");
    Check(manual.Id == latest, "Automatic snapshots preserve the manual checkpoint");
    string history = Directory.GetDirectories(Path.Combine(root, "FlightCheckpoints")).Single();
    Check(Directory.GetFiles(history, "*.checkpoint").Length == 32, "Rolling history stays bounded after forty minutes");
    string stale = Path.Combine(root, "FlightCheckpoints", "old.pending");
    string active = Path.Combine(root, "FlightCheckpoints", "active.pending");
    File.WriteAllText(stale, "old incomplete write"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-20));
    File.WriteAllText(active, "current write");
    restarted.SaveTimed(Snapshot(Guid.NewGuid()), false);
    Check(!File.Exists(stale) && File.Exists(active), "Cleanup removes abandoned staging files while preserving current work");
    File.Delete(active);
    var transaction = new PersonalFlightCheckpoints.RestoreTransaction { RequestId = Guid.NewGuid(), Snapshot = manual, ExpectedGeneration = 0 };
    restarted.BeginRestore(transaction);
    Check(restarted.Pending(owner)?.RequestId == transaction.RequestId, "Restore journal persists before apply");
    restarted.FinishRestore(transaction);
    var receipt = restarted.Receipt(owner);
    Check(receipt.Generation == 1 && restarted.Pending(owner) == null, "Completed restore publishes one receipt and removes its journal");
    restarted.MarkRehomed(receipt);
    Check(!restarted.Receipt(owner).NeedsRehome && !Directory.GetFiles(Path.Combine(root, "FlightCheckpoints"), "*.restore-source").Any(),
        "Completed resume removes its temporary restore snapshot");
    Check(!Directory.GetFiles(Path.Combine(root, "FlightCheckpoints"), "*.pending").Any(), "No uncommitted staging files remain");
    var durations = new List<double>();
    var fleet = Snapshot(Guid.NewGuid());
    fleet.Owner = "performance-fixture";
    fleet.Vessels.Clear();
    for (int ship = 0; ship < 12; ship++)
    {
        var proto = new System.Text.StringBuilder();
        for (int part = 0; part < 1500; part++) proto.Append("PART\n{\n part = fuelTank_").Append(part)
            .Append("\n RESOURCE\n {\n name = LiquidFuel\n amount = 120.123456789\n maxAmount = 200\n }\n}\n");
        fleet.Vessels.Add(new PersonalFlightCheckpoints.VesselSnapshot { Id = Guid.NewGuid(), Proto = proto.ToString() });
    }
    for (int sample = 0; sample < 12; sample++)
    {
        fleet.Id = Guid.NewGuid(); fleet.CapturedUtc = DateTimeOffset.UtcNow.AddMinutes(sample);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        restarted.SaveTimed(fleet, false);
        durations.Add(clock.Elapsed.TotalMilliseconds);
    }
    durations.Sort();
    long rawBytes = fleet.Vessels.Sum(vessel => (long)System.Text.Encoding.UTF8.GetByteCount(vessel.Proto));
    long diskBytes = Directory.GetFiles(Path.Combine(root, "FlightCheckpoints"), "*.checkpoint", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    Console.WriteLine($"PERF 12-vessel fleet {rawBytes} bytes; 12 compressed samples p50={durations[6]:F1}ms p95={durations[11]:F1}ms; all history disk bytes={diskBytes}");
    var beforeQuota = Directory.GetDirectories(Path.Combine(root, "FlightCheckpoints")).ToHashSet();
    var quota = Snapshot(Guid.NewGuid()); quota.Owner = "quota-fixture"; quota.Vessels.Clear();
    for (int ship = 0; ship < 16; ship++)
        quota.Vessels.Add(new PersonalFlightCheckpoints.VesselSnapshot { Id = Guid.NewGuid(),
            Proto = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(524288)) });
    for (int sample = 0; sample < 18; sample++)
    { quota.Id = Guid.NewGuid(); quota.CapturedUtc = DateTimeOffset.UtcNow.AddMinutes(sample); restarted.SaveTimed(quota, false); }
    string quotaDirectory = Directory.GetDirectories(Path.Combine(root, "FlightCheckpoints")).Single(path => !beforeQuota.Contains(path));
    var retainedFiles = Directory.GetFiles(quotaDirectory, "*.checkpoint");
    Check(retainedFiles.Length < 18 && retainedFiles.Length > 0, "Compressed byte quota prunes older large fleet samples");
    Check(retainedFiles.Sum(path => new FileInfo(path).Length) <= 128L * 1024 * 1024, "Rolling history remains within 128 MiB per player");
    Console.WriteLine($"PASS {checks} personal-fleet checkpoint storage checks; game restore and deployment unperformed");
}
finally
{
    foreach (var directory in Directory.GetDirectories(Path.Combine(root, "FlightCheckpoints")))
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected test link");
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
    foreach (var file in Directory.GetFiles(Path.Combine(root, "FlightCheckpoints"))) File.Delete(file);
    Directory.Delete(Path.Combine(root, "FlightCheckpoints"));
    Directory.Delete(root);
}
