using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string baselineHash = "98a5541f2701b7961039da3261f6c4bb74149d313f33dcafbeafb6950d824fa1";
const string usage = "--evidence PRIVATE_DIRECTORY --baseline PINNED_DLL [--candidate CANDIDATE_DLL]";
var options = new Dictionary<string, string>(StringComparer.Ordinal);
if (args.Length % 2 != 0) throw new ArgumentException(usage);
for (int index = 0; index < args.Length; index += 2)
    if ((args[index] != "--evidence" && args[index] != "--baseline" && args[index] != "--candidate") ||
        string.IsNullOrWhiteSpace(args[index + 1]) || !options.TryAdd(args[index], args[index + 1]))
        throw new ArgumentException(usage);
if (!options.ContainsKey("--evidence") || !options.ContainsKey("--baseline")) throw new ArgumentException(usage);
string evidenceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options["--evidence"]));
if (evidenceRoot == Path.GetPathRoot(evidenceRoot)) throw new ArgumentException("Evidence directory must not be a filesystem root");
string baseline = Path.GetFullPath(options["--baseline"]);
byte[] image = File.ReadAllBytes(baseline);
string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
if (hash != baselineHash) throw new InvalidDataException("Baseline DLL hash differs");
var context = new AssemblyLoadContext("clock-fixture", isCollectible: true);
using var assemblyBytes = new MemoryStream(image);
var assembly = context.LoadFromStream(assemblyBytes);
var clockType = assembly.GetType("Aetherfront.Continuity.ClockStore", throwOnError: true)!;
string run = Path.Combine(evidenceRoot, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(run);
object clock = Activator.CreateInstance(clockType, run)!;
object? Invoke(string name, params object[] values)
{
    try { return clockType.GetMethod(name)!.Invoke(clock, values); }
    catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
    { ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw(); return null; }
}
const string account = "isolated-clock-account";
Invoke("Resume", account, 1200d);
string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant();
string destination = Path.Combine(run, "AetherfrontTime", key + ".clock");
byte[] before = File.ReadAllBytes(destination);
Exception? conflict = null;
var timer = Stopwatch.StartNew();
using (var reader = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    try { Invoke("Checkpoint", account, 1201d); } catch (Exception error) { conflict = error; }
}
timer.Stop();
if (conflict is not IOException) throw new InvalidOperationException("Baseline did not reproduce IOException", conflict);
bool unchanged = File.ReadAllBytes(destination).SequenceEqual(before);
bool temporaryCleaned = !Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.tmp").Any();
if (!unchanged || !temporaryCleaned) throw new InvalidOperationException("Baseline failed-write evidence changed");
Invoke("Checkpoint", account, 1201d);
double resumed = (double)Invoke("Resume", account, 9999d)!;
if (resumed != 1201d) throw new InvalidOperationException("Write did not recover after closing reader");
var result = new { baseline, baselineSha256 = hash, runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, reader = "FileMode.Open, FileAccess.Read, FileShare.Read",
    exception = conflict.GetType().FullName, conflict.Message, hresult = "0x" + conflict.HResult.ToString("X8"),
    nativeError = conflict.HResult & 65535, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, stack = conflict.StackTrace,
    destinationUnchangedOnFailure = unchanged, temporaryCleanedOnFailure = temporaryCleaned,
    recoveredAfterReaderClosed = resumed == 1201d, productionStateOpened = false, productionInstancesStarted = false };
string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(run, "baseline-conflict.json"), json);
Console.WriteLine(json); Console.WriteLine("RESULTS=" + Path.Combine(run, "baseline-conflict.json"));
if (options.TryGetValue("--candidate", out string? candidate)) CandidateCases.Run(candidate, run, evidenceRoot);

internal static class CandidateCases
{
    private const string CandidateHash = "ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9";
    private static int assertions;
    private static string fixtureBoundary = string.Empty;
    private static void Need(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
    private static Exception? Error(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Key(string account) => Digest(Encoding.UTF8.GetBytes(account));
    private static string Record(string key, string time, string sequence = "1")
    {
        string content = "APF-CLOCK-1\n" + key + "\n" + time + "\n" + sequence + "\n";
        return content + Digest(Encoding.ASCII.GetBytes(content)) + "\n";
    }
    internal static void Run(string candidate, string run, string evidenceRoot)
    {
        string path = Path.GetFullPath(candidate);
        Need(path.StartsWith(evidenceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Candidate must be under the supplied private evidence directory");
        fixtureBoundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(run)) + Path.DirectorySeparatorChar;
        byte[] image = File.ReadAllBytes(path); string hash = Digest(image);
        Need(hash == CandidateHash, "Candidate hash differs from authorized pin");
        var context = new AssemblyLoadContext("candidate-clock-fixture", isCollectible: true);
        using var assemblyBytes = new MemoryStream(image);
        var assembly = context.LoadFromStream(assemblyBytes);
        var type = assembly.GetType("Aetherfront.Continuity.ClockStore", throwOnError: true)!;
        Need(type.TypeInitializer == null, "Unexpected ClockStore static initialization");
        var cases = new List<object>(); int failed = 0;
        void Test(string name, Action<Fixture> body)
        {
            int initial = assertions; var elapsed = Stopwatch.StartNew();
            var fixture = new Fixture(type, Path.Combine(run, "candidate", name));
            Exception? error = Error(() => body(fixture)); elapsed.Stop();
            if (error != null) failed++;
            cases.Add(new { name, passed = error == null, assertions = assertions - initial, elapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, error = error?.ToString() });
            Console.WriteLine((error == null ? "PASS " : "FAIL ") + name + " " + elapsed.ElapsedMilliseconds + "ms" + (error == null ? "" : " " + error.Message));
        }
        Test("first-join-move-and-sequence-one", f =>
        {
            Need(f.Resume("account", 1200) == 1200, "First join time changed");
            f.Check("account", 1200, 1); f.Clean();
            Need(Directory.GetFiles(f.ClockRoot).Length == 1, "First join created extra files");
        });
        Test("short-reader-150ms-bounded-retry-one-sequence", f =>
        {
            f.Resume("account", 1200); byte[] before = f.Bytes("account");
            using var reader = new FileStream(f.PathFor("account"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var elapsed = Stopwatch.StartNew();
            Task release = Task.Run(async () => { await Task.Delay(150); reader.Dispose(); });
            Exception? error = Error(() => f.Checkpoint("account", 1201));
            elapsed.Stop(); release.GetAwaiter().GetResult();
            Need(error == null, "Short reader did not recover: " + error);
            Need(elapsed.Elapsed.TotalMilliseconds >= 125 && elapsed.Elapsed.TotalMilliseconds <= 2500, "Short reader timing outside bound");
            Need(!f.Bytes("account").SequenceEqual(before), "Successful checkpoint did not commit");
            f.Check("account", 1201, 2); f.Clean();
            Need(f.NewStore().Resume("account", 999999) == 1201, "Restart failed to restore committed time");
        });
        Test("permanent-reader-fail-closed-within-2500ms", f =>
        {
            f.Resume("account", 1200); byte[] before = f.Bytes("account");
            Exception? error; var elapsed = Stopwatch.StartNew();
            using (var reader = new FileStream(f.PathFor("account"), FileMode.Open, FileAccess.Read, FileShare.Read))
            { error = Error(() => f.Checkpoint("account", 1201)); elapsed.Stop(); }
            Need(error is IOException, "Permanent reader did not fail closed with IOException");
            Need(error!.HResult == unchecked((int)0x80070020), "Permanent reader HRESULT changed");
            Need(elapsed.Elapsed.TotalMilliseconds >= 1500 && elapsed.Elapsed.TotalMilliseconds <= 2500, "Permanent conflict deadline outside bound");
            Need(f.Bytes("account").SequenceEqual(before), "Permanent conflict changed authoritative record");
            f.Check("account", 1200, 1); f.Clean();
            f.Checkpoint("account", 1201); f.Check("account", 1201, 2); f.Clean();
        });
        Test("no-offline-advance", f =>
        {
            f.Resume("account", 1200); f.Checkpoint("account", 1500);
            byte[] before = f.Bytes("account"); File.SetLastWriteTimeUtc(f.PathFor("account"), DateTime.UtcNow.AddDays(-30));
            Need(f.NewStore().Resume("account", 999999999999) == 1500, "Offline resume advanced clock");
            Need(f.Bytes("account").SequenceEqual(before), "Resume rewrote checkpoint"); f.Check("account", 1500, 2);
        });
        Test("own-ut-account-and-universe-isolation", f =>
        {
            f.Resume("account", 1200); f.Resume("ACCOUNT", 90000000); f.Checkpoint("account", 1201);
            Need(f.Resume("account", 90000001) == 1201, "Other account/global seed leaked into own UT");
            Need(f.Resume("ACCOUNT", 1201) == 90000000, "Ordinal account identities merged");
            var other = new Fixture(type, Path.Combine(f.Universe, "separate-universe"));
            Need(other.Resume("account", 777) == 777, "Universe clock leaked");
            Need(f.NewStore().Resume("account", 777) == 1201, "Original universe changed");
            f.Check("account", 1201, 2); f.Check("ACCOUNT", 90000000, 1); other.Check("account", 777, 1);
        });
        Test("backward-checkpoint-rejected-no-sequence-change", f =>
        {
            f.Resume("account", 1200); var before = f.Bytes("account");
            Need(Error(() => f.Checkpoint("account", 1199)) is InvalidDataException, "Backward clock accepted");
            Need(f.Bytes("account").SequenceEqual(before), "Backward checkpoint changed record"); f.Clean();
            f.Checkpoint("account", 1199.9995); f.Check("account", 1200, 2);
        });
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 1000000000001d })
        {
            string label = invalid.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace("-", "negative-");
            Test("invalid-time-" + label, f =>
            {
                f.Resume("account", 1200); var before = f.Bytes("account");
                Need(Error(() => f.Checkpoint("account", invalid)) is InvalidDataException, "Invalid checkpoint time accepted");
                Need(Error(() => f.Resume("account", invalid)) is InvalidDataException, "Invalid resume time accepted");
                Need(f.Bytes("account").SequenceEqual(before), "Invalid time changed record"); f.Clean();
            });
        }
        foreach (string invalidAccount in new[] { "", " ", new string('x', 513) })
            Test("invalid-account-length-" + invalidAccount.Length, f =>
            {
                Need(Error(() => f.Resume(invalidAccount, 1200)) is ArgumentException, "Invalid resume account accepted");
                Need(Error(() => f.Checkpoint(invalidAccount, 1201)) is ArgumentException, "Invalid checkpoint account accepted");
            });
        var corruptions = new (string Name, Func<string, string> Text)[]
        {
            ("empty", _ => ""), ("truncated", k => Record(k,"1200")[..32]),
            ("over-512", _ => new string('x',513)), ("header", k => Record(k,"1200").Replace("APF-CLOCK-1","APF-CLOCK-2")),
            ("checksum", k => Record(k,"1200").Replace("1200","1201")),
            ("wrong-account-key", _ => Record(Key("different-account"),"1200")),
            ("extra-line", k => Record(k,"1200") + "\n"), ("missing-newline", k => Record(k,"1200").TrimEnd('\n')),
            ("nan", k => Record(k,"NaN")), ("infinity", k => Record(k,"Infinity")),
            ("negative", k => Record(k,"-1")), ("too-large", k => Record(k,"1000000000001")),
            ("sequence-zero", k => Record(k,"1200","0")), ("sequence-negative", k => Record(k,"1200","-1")),
            ("sequence-overflow", k => Record(k,"1200","9223372036854775808"))
        };
        foreach (var item in corruptions)
            Test("corrupt-record-" + item.Name, f =>
            {
                f.Resume("account", 1200); byte[] corrupt = Encoding.ASCII.GetBytes(item.Text(Key("account")));
                File.WriteAllBytes(f.PathFor("account"), corrupt);
                Need(Error(() => f.Resume("account", 9999)) is InvalidDataException, "Corrupt record resume accepted");
                Need(Error(() => f.Checkpoint("account", 1201)) is InvalidDataException, "Corrupt record checkpoint accepted");
                Need(f.Bytes("account").SequenceEqual(corrupt), "Corrupt evidence overwritten"); f.Clean();
            });
        Test("sequence-overflow-fails-before-write", f =>
        {
            f.Resume("account", 1200); File.WriteAllText(f.PathFor("account"), Record(Key("account"), "1200", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)), Encoding.ASCII);
            byte[] before = f.Bytes("account");
            Need(Error(() => f.Checkpoint("account", 1201)) is OverflowException, "Sequence overflow accepted");
            Need(f.Bytes("account").SequenceEqual(before), "Overflow changed record"); f.Clean();
        });
        Test("retry-hresult-filter-narrow", f =>
        {
            var filter = type.GetMethod("ReplaceClockTransient", BindingFlags.NonPublic | BindingFlags.Static)!;
            Need(filter != null, "Missing actual grafted filter");
            foreach (int code in new[] { 0, 5, 17, 21, 32, 33, 80, 87, 112, 1175, 1176, 1177 })
            {
                int hresult = unchecked((int)0x80070000) | code;
                bool actual = (bool)filter!.Invoke(null, new object[] { new IOException("isolated filter input", hresult) })!;
                Need(actual == (code is 32 or 33 or 1175), "Retry filter wrong for native error " + code);
            }
            Need(!(bool)filter!.Invoke(null, new object[] { new IOException("wrong facility", 1175) })!, "Filter accepted wrong HRESULT facility");
        });
        Test("replacement-image-bounds-and-regular-file", f =>
        {
            var read = type.GetMethod("ReplaceClockReadImage", BindingFlags.NonPublic | BindingFlags.Static)!;
            Need(read != null, "Missing actual grafted image reader");
            Exception? Call(string file) => Error(() => Invoke(read!, null, file));
            string file = Path.Combine(f.Universe, "image");
            foreach (int size in new[] { 0, 513 })
            { File.WriteAllBytes(file, new byte[size]); Need(Call(file) is InvalidDataException, "Invalid image size accepted"); }
            File.WriteAllBytes(file, new byte[512]); Need(((byte[])Invoke(read!, null, file)!).Length == 512, "512-byte bound rejected");
            Need(Call(f.Universe) is InvalidDataException, "Directory accepted as replacement file");
        });
        string report = JsonSerializer.Serialize(new { candidate = path, candidateSha256 = hash, runtime = RuntimeInformation.FrameworkDescription,
            passed = cases.Count - failed, failed, assertions, cases, productionStateOpened = false, productionInstancesStarted = false,
            scope = "Actual candidate ClockStore and five private grafted helpers only; no PersonalClockServer session initialization" }, new JsonSerializerOptions { WriteIndented = true });
        string output = Path.Combine(run, "candidate-results.json"); File.WriteAllText(output, report);
        Console.WriteLine("CANDIDATE_RESULTS=" + output); Console.WriteLine("passed=" + (cases.Count - failed) + " failed=" + failed + " assertions=" + assertions);
        if (failed != 0) Environment.ExitCode = 1;
        context.Unload();
    }
    private static object? Invoke(MethodInfo method, object? target, params object[] values)
    {
        try { return method.Invoke(target, values); }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
        { ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw(); return null; }
    }
    private sealed class Fixture
    {
        private readonly Type type;
        private readonly object store;
        internal string Universe { get; }
        internal string ClockRoot => Path.Combine(Universe, "AetherfrontTime");
        internal Fixture(Type type, string universe)
        {
            this.type = type; Universe = Path.GetFullPath(universe);
            Need(Universe.StartsWith(fixtureBoundary, StringComparison.OrdinalIgnoreCase), "Fixture escaped private run directory");
            Directory.CreateDirectory(Universe); store = Activator.CreateInstance(type, Universe)!;
        }
        internal Fixture NewStore() => new(type, Universe);
        internal double Resume(string account, double time) => (double)Invoke(type.GetMethod("Resume")!, store, account, time)!;
        internal void Checkpoint(string account, double time) => Invoke(type.GetMethod("Checkpoint")!, store, account, time);
        internal string PathFor(string account) => Path.Combine(ClockRoot, Key(account) + ".clock");
        internal byte[] Bytes(string account) => File.ReadAllBytes(PathFor(account));
        internal void Clean() => Need(!Directory.Exists(ClockRoot) || !Directory.GetFiles(ClockRoot, "*.tmp").Any(), "Temporary clock file leaked");
        internal void Check(string account, double time, long sequence)
        {
            string text = Encoding.ASCII.GetString(Bytes(account)); string[] fields = text.Split('\n');
            Need(fields.Length == 6 && fields[0] == "APF-CLOCK-1" && fields[1] == Key(account) && fields[5] == "", "Clock format/identity changed");
            Need(double.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) == time, "Committed UT differs");
            Need(long.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture) == sequence, "Sequence advanced more or less than exactly once");
            Need(fields[4] == Digest(Encoding.ASCII.GetBytes(string.Join("\n", fields, 0, 4) + "\n")), "Committed checksum differs");
        }
    }
}
