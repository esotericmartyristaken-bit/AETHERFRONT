using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private const string Version = "kesas-rc28";
    private static readonly string[] Roots = { "InterstellarTechnologies", "ModularLaunchPads", "TundraSpaceCenter", "UmbraSpaceIndustries" };
    private static readonly List<string> Failures = new List<string>();
    private static string baseline, engine, nextEngine, output, mode, caseFilter;
    private static RSA testSigningKey;
    private static int checks, cases;

    // Keep dependency resolution ahead of JIT compilation of every typed helper.
    private static int Main(string[] args)
    {
        // The existing RC22 MonoHost forwards only argv[0]. Environment is
        // used solely to transport these same test arguments, before loading.
        if (args.Length == 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APF_CASING_ARGUMENTS"))) args = Environment.GetEnvironmentVariable("APF_CASING_ARGUMENTS").Split('\n');
        bool stub = args.Contains("--startup-stub");
        string pluginDirectory = stub ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData/LunaMultiplayer/Plugins") : Path.Combine(Option(args, "--baseline"), "GameData/LunaMultiplayer/Plugins");
        string selectedEngine = stub ? Path.Combine(pluginDirectory, "KesasInstall.dll") : Option(args, "--engine");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            string path = name == "KesasInstall.dll" ? selectedEngine : Path.Combine(pluginDirectory, name);
            if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KSP_x64_Data/Managed", name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return stub ? StartupStub() : Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static string Option(string[] args, string name, string fallback = "")
    { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        baseline = Path.GetFullPath(Option(args, "--baseline")); engine = Path.GetFullPath(Option(args, "--engine"));
        nextEngine = Path.GetFullPath(Option(args, "--new-engine", engine)); output = Path.GetFullPath(Option(args, "--output"));
        mode = Option(args, "--mode", "new"); caseFilter = Option(args, "--case"); Directory.CreateDirectory(output);
        // Each process uses one ephemeral key; every fixture is still signed
        // and verified independently through the production security API.
        if (mode != "old") testSigningKey = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
        Check((Type.GetType("Mono.Runtime") != null) == (Option(args, "--expected-runtime", "CLR") == "KSP-Mono"), "Requested runtime actually executes suite");
        Check(ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location) == ApfJson.Sha256File(engine), "Selected Engine bytes actually loaded");
        if (mode == "migration") Case("old26-one-restart-new27", Migration);
        else {
            Case("prepared-four-roots", () => Prepared(mode == "old"));
            if (mode == "new") {
                Case("signed-startup-idempotent", Startup);
                Case("locked-diagnostic-log", LockedLog);
                foreach (string invalid in new[] { "feed", "catalog", "code", "receipt", "profile", "status", "missing-feed" }) {
                    string captured = invalid; Case("invalid-" + invalid, () => Invalid(captured));
                }
                Case("empty-child-junction", Junction);
                Case("flat-file-casing", FlatFile);
                Case("missing-root-no-payload", MissingRoot);
                Case("forged-journal", ForgedJournal);
                Case("authenticated-pending-journal", PendingJournal);
                Case("existing-source-journal-junction", PendingSourceJunction);
                Case("HETTN-runtime-files", Mutable);
                Case("HETTN-selected", () => Resources(true, true));
                Case("HETTN-unselected", () => Resources(false, true));
                Case("HETTN-absent", () => Resources(true, false));
                Case("HETTN-untrusted", UntrustedResources);
                Case("canonical-root-contract", CanonicalContract);
            }
        }
        Check(cases > 0, "Requested case selection executes at least one case");
        var summary = new { mode, cases, checks, failures = Failures, runtime = Type.GetType("Mono.Runtime") == null ? "CLR" : "KSP-Mono", enginePath = engine, engineSha256 = ApfJson.Sha256File(engine), unityGameplayTested = false, fixturesRetained = true };
        File.WriteAllText(Path.Combine(output, "summary-" + mode + ".json"), ApfJson.Serialize(summary));
        Console.WriteLine("RESULT mode=" + mode + " cases=" + cases + " checks=" + checks + " failures=" + Failures.Count);
        testSigningKey?.Dispose();
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool value, string name)
    { if (!value) throw new InvalidDataException(name); checks++; Console.WriteLine("PASS " + name); }
    private static void Case(string name, Action action)
    {
        if (!string.IsNullOrEmpty(caseFilter) && !caseFilter.Split(',').Contains(name, StringComparer.Ordinal)) return;
        cases++; Console.WriteLine("CASE " + name);
        try { action(); } catch (Exception error) {
            string detail = name + ": " + error; var cause = Unwrap(error);
            string first = name + ": " + cause.GetType().Name + ": " + cause.Message;
            Failures.Add(first); Console.Error.WriteLine("FAIL " + first);
            File.WriteAllText(Path.Combine(output, "failure-" + name + ".txt"), detail);
        } finally { PreparedPack.Close(); }
    }
    private static Exception Unwrap(Exception error)
    { return error is TargetInvocationException invocation && invocation.InnerException != null ? Unwrap(invocation.InnerException) : error; }
    private static void Rejected(Action action, string label)
    {
        bool rejected = false;
        try { action(); } catch (Exception error) { var cause = Unwrap(error); if (!(cause is InvalidDataException || cause is IOException || cause is UnauthorizedAccessException)) throw; rejected = true; }
        Check(rejected, label);
    }
    private static string Put(string root, string relative, string text)
    { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); return path; }
    private static void Copy(string source, string target)
    { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, true); }

    private sealed class Fixture
    {
        public string Root, Data, GameData;
        public string Helper => Path.Combine(GameData, "LunaMultiplayer");
        public string Journal => Path.Combine(Root, "APF/RootCasingRepair.json");
        public Fixture(string name) {
            Root = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            GameData = Path.Combine(Root, "GameData"); Data = Path.Combine(GameData, "LunaMultiplayer/Data"); Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Path.Combine(GameData, "Squad"));
            Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(Root, "KSP_x64.exe"));
            Put(Root, "GameData/LunaMultiplayer/Data/APFServer.txt", "127.0.0.1:18865");
            Put(Root, "GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt", new string('a', 64));
            Put(Root, "GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt", "https://127.0.0.1:18866/unused-feed.json");
            Copy(Path.Combine(baseline, "GameData/LunaMultiplayer/Data/APFClient.json"), Path.Combine(Data, "APFClient.json"));
            Put(Root, "settings.cfg", "ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nSENTINEL = preserved\n");
            ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(Data, "APFHelper.key"));
        }
    }
    private static ApfPackage Package(string root, string archive = null)
    {
        return new ApfPackage { Id = "test-" + root.ToLowerInvariant().Replace('.', '-'), Version = "1", ArchiveUrl = "https://127.0.0.1:18866/unused.zip", Sha256 = archive == null ? new string('a', 64) : ApfJson.Sha256File(archive), SizeBytes = archive == null ? 1 : new FileInfo(archive).Length, Destination = "GameData", StripPrefix = root };
    }
    private static ApfPackManifest Manifest(IEnumerable<ApfPackage> packages)
    {
        return new ApfPackManifest { ServerId = "kesas-b6286df413b9426f", PackId = "kesas", PackVersion = Version, RequiredClientVersion = Version, KspVersion = "1.12.5", ProtectedRoots = new List<string> { "Squad", "SquadExpansion", "LunaMultiplayer", "000_Harmony" }, ManagedPackages = packages.ToList(), ClientOnly = new List<ApfClientOnlyRule>() };
    }
    private static ApfSyncTransaction Transaction(Fixture fixture, ApfPackManifest manifest)
    {
        return new ApfSyncTransaction { GameRoot = fixture.Root, GameDataPath = fixture.GameData, KspExecutable = Path.Combine(fixture.Root, "KSP_x64.exe"), ProcessId = 0, KspArguments = new List<string> { "--startup-stub" }, StatePath = Path.Combine(fixture.Data, "APFPackState.json"), Manifest = manifest, ExistingState = new ApfPackState(), Packages = new List<ApfStagedPackage>(), Deletes = new List<ApfDeleteChange>() };
    }
    private static ApfSyncTransaction TinyPack(Fixture fixture)
    {
        var transaction = Transaction(fixture, Manifest(new ApfPackage[0]));
        foreach (string root in Roots) {
            string archive = Path.Combine(fixture.Root, "APF/.staging/" + root + ".zip"); Directory.CreateDirectory(Path.GetDirectoryName(archive));
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) {
                using (var writer = new StreamWriter(zip.CreateEntry(root + "/Parts/Model/model.mu").Open())) writer.Write("model-sentinel:" + root);
                using (var writer = new StreamWriter(zip.CreateEntry(root + "/Parts/part.cfg").Open())) writer.Write("PART { MODEL { model = " + root + "/Parts/Model/model } }");
            }
            var package = Package(root, archive); transaction.Manifest.ManagedPackages.Add(package); transaction.Packages.Add(new ApfStagedPackage { Package = package, ArchivePath = archive });
        }
        return transaction;
    }
    private static string Save(Fixture fixture, ApfSyncTransaction transaction)
    {
        ApfTransactionSecurity.Sign(transaction, ApfTransactionSecurity.LoadKey(Path.Combine(fixture.Data, "APFHelper.key")));
        return Put(fixture.Root, "APF/.staging/transaction/transaction.json", ApfJson.Serialize(transaction));
    }
    private static void Prepared(bool old)
    {
        var fixture = new Fixture(old ? "old26" : "new27"); var transaction = TinyPack(fixture); string request = Save(fixture, transaction);
        PreparedPack.Prepare(transaction, request, Console.WriteLine, () => false);
        Check(!Roots.Any(root => Directory.Exists(Path.Combine(fixture.GameData, root))), "Preparation leaves installed roots absent");
        using (NativeInstall.Run(fixture.Helper, request, "pack")) { }
        var actual = Directory.GetDirectories(fixture.GameData).Select(Path.GetFileName).ToArray();
        var index = ModelIndex(fixture.GameData);
        foreach (string root in Roots) {
            Check(actual.Contains(old ? root.ToLowerInvariant() : root, StringComparer.Ordinal), (old ? "Old26 lowercases " : "New27 preserves Ordinal ") + root);
            Check(File.ReadAllText(Path.Combine(fixture.GameData, root, "Parts/Model/model.mu")) == "model-sentinel:" + root, "Installed model bytes preserved: " + root);
            Check(index.Contains(root + "/Parts/Model/model") != old, "Strict-case URL " + (old ? "miss reproduced: " : "resolves: ") + root);
        }
        Check(!File.ReadAllText(Path.Combine(fixture.Root, "APF/APFTransaction.log")).Contains("ROLLED-BACK"), "Prepared transaction commits without rollback");
    }
    private static HashSet<string> ModelIndex(string gameData)
    { return new HashSet<string>(Directory.GetFiles(gameData, "*.mu", SearchOption.AllDirectories).Select(path => path.Substring(gameData.Length + 1, path.Length - gameData.Length - 4).Replace('\\', '/')), StringComparer.Ordinal); }

    private sealed class Signed : IDisposable
    {
        public Fixture Fixture; public ApfPackManifest Manifest; public ApfClientUpdateManifest Feed; public string Archive;
        public RSA Key = testSigningKey;
        public Signed(Fixture fixture, IEnumerable<ApfPackage> packages, bool install = true) {
            Fixture = fixture; Manifest = Program.Manifest(packages);
            string catalog = ApfJson.Serialize(new InstallCatalog { Version = Version, Full = Manifest, Potato = Manifest });
            var files = new Dictionary<string, byte[]> {
                { "GameData/LunaMultiplayer/Launcher/install-catalog.json", Encoding.UTF8.GetBytes(catalog) },
                { "GameData/LunaMultiplayer/Plugins/LmpClient.dll", File.ReadAllBytes(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/LmpClient.dll")) },
                { "GameData/LunaMultiplayer/Plugins/ApfCore.dll", File.ReadAllBytes(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/ApfCore.dll")) },
                { "GameData/LunaMultiplayer/Plugins/KesasInstall.dll", File.ReadAllBytes(nextEngine) }
            };
            Archive = Path.Combine(fixture.Root, "APF/.client-update-staging/connect/client.zip"); Directory.CreateDirectory(Path.GetDirectoryName(Archive));
            using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create)) foreach (var item in files) using (var stream = zip.CreateEntry(item.Key).Open()) stream.Write(item.Value, 0, item.Value.Length);
            Feed = new ApfClientUpdateManifest { SchemaVersion = 2, Version = Version, KspVersion = "1.12.5", PackageUrl = "https://127.0.0.1:18866/no-network.zip", Sha256 = ApfJson.Sha256File(Archive), SizeBytes = new FileInfo(Archive).Length, Files = files.Select(item => new ApfClientUpdateFile { Path = item.Key, Sha256 = Hash(item.Value), SizeBytes = item.Value.Length }).ToList() };
            ApfClientUpdateSecurity.Sign(Feed, Key);
            Check(ApfClientUpdateSecurity.Verify(Feed, Key), "Real RSA signature verifies");
            var validation = ApfClientUpdateValidator.Validate(Feed);
            Check(validation.IsValid, "Signed four-file feed passes actual validator: " + string.Join("; ", validation.Errors));
            Put(fixture.Root, "APF/.client-update-staging/connect/feed.json", Feed.ToJson());
            Put(fixture.Root, "GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml", ApfClientUpdateSecurity.ExportXml(Key, false));
            Copy(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/JsonFx.dll"), Path.Combine(fixture.Helper, "Plugins/JsonFx.dll"));
            if (install) foreach (var item in files) { string path = Path.Combine(fixture.Root, item.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, item.Value); }
        }
        public void Complete() {
            Put(Fixture.Root, "GameData/LunaMultiplayer/Data/KesasInstallProfile.json", ApfJson.Serialize(new Dictionary<string, string> { { "mode", "full" }, { "catalogVersion", Version } }));
            Put(Fixture.Root, "GameData/LunaMultiplayer/Data/APFPackState.json", ApfJson.Serialize(new ApfPackState()));
            Installer.WriteCompletedReceipt(Transaction(Fixture, Manifest), "full");
            var receipt = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Fixture.Root, "APF/LastInstallResult.json")));
            string expected = (string)typeof(Installer).GetMethod("ReceiptAuthorization", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { receipt, ApfTransactionSecurity.LoadKey(Path.Combine(Fixture.Data, "APFHelper.key")) });
            Check(receipt["status"] == "complete" && receipt["authorization"] == expected, "Actual completed receipt HMAC verifies");
        }
        public void Dispose() { }
    }
    private static string Hash(byte[] bytes)
    { using (var sha = SHA256.Create()) return ApfJson.ToHex(sha.ComputeHash(bytes)); }
    private static void Seed(Fixture fixture)
    { foreach (string root in Roots) Put(fixture.Root, "GameData/" + root.ToLowerInvariant() + "/Parts/Model/model.mu", "model-sentinel:" + root); }
    private static void Restore(Fixture fixture)
    {
        var type = typeof(NativeInstall).Assembly.GetType("KesasInstall.PackageCasing", true);
        type.GetMethod("RestoreAtStartup", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { fixture.Root, Version });
    }
    private static string[] Snapshot(string root, bool paths = true)
    {
        var result = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0) foreach (string path in Directory.GetFileSystemEntries(pending.Pop())) {
            string relative = path.Substring(root.Length + 1).Replace('\\', '/'); var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) result.Add("R:" + relative);
            else if ((attributes & FileAttributes.Directory) != 0) { if (paths) result.Add("D:" + relative); pending.Push(path); }
            else result.Add((paths ? relative : relative.ToLowerInvariant()) + ":" + ApfJson.Sha256File(path));
        }
        return result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
    private static void AssertRoots(Fixture fixture)
    {
        var actual = Directory.GetDirectories(fixture.GameData).Select(Path.GetFileName).ToArray();
        foreach (string root in Roots) Check(actual.Contains(root, StringComparer.Ordinal) && ModelIndex(fixture.GameData).Contains(root + "/Parts/Model/model"), "Canonical Ordinal root and URL: " + root);
    }
    private static void Startup()
    {
        var fixture = new Fixture("startup"); Seed(fixture);
        using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) {
            signed.Complete(); var bytes = Snapshot(fixture.GameData, false); Restore(fixture); AssertRoots(fixture);
            Check(bytes.SequenceEqual(Snapshot(fixture.GameData, false)), "Startup changes no file contents");
            var once = Snapshot(fixture.Root); Restore(fixture); Check(once.SequenceEqual(Snapshot(fixture.Root)), "Startup repair is idempotent including journal and log");
            Check(!File.Exists(Path.Combine(fixture.Root, "startup-launch.txt")), "Synchronous startup repair performs no restart");
        }
    }
    private static void Invalid(string invalid)
    {
        var fixture = new Fixture("invalid-" + invalid); Seed(fixture);
        using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) {
            signed.Complete(); string receipt = Path.Combine(fixture.Root, "APF/LastInstallResult.json");
            if (invalid == "feed") { signed.Feed.ReleaseNotes = "unsigned alteration"; File.WriteAllText(Path.Combine(fixture.Root, "APF/.client-update-staging/connect/feed.json"), signed.Feed.ToJson()); }
            if (invalid == "catalog") File.AppendAllText(Path.Combine(fixture.Helper, "Launcher/install-catalog.json"), " ");
            if (invalid == "code") File.AppendAllText(Path.Combine(fixture.Helper, "Plugins/LmpClient.dll"), "tampered");
            if (invalid == "receipt" || invalid == "status") { var value = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(receipt)); value[invalid == "status" ? "status" : "packVersion"] = "invalid"; File.WriteAllText(receipt, ApfJson.Serialize(value)); }
            if (invalid == "profile") Put(fixture.Root, "GameData/LunaMultiplayer/Data/KesasInstallProfile.json", "{\"mode\":\"invalid\",\"catalogVersion\":\"kesas-rc28\"}");
            if (invalid == "missing-feed") File.Move(Path.Combine(fixture.Root, "APF/.client-update-staging/connect/feed.json"), Path.Combine(fixture.Root, "retained-feed.json"));
            var before = Snapshot(fixture.Root);
            if (invalid == "status" || invalid == "missing-feed") Restore(fixture); else Rejected(() => Restore(fixture), "Invalid " + invalid + " rejected");
            Check(before.SequenceEqual(Snapshot(fixture.Root)), "Invalid " + invalid + " causes zero filesystem mutation");
        }
    }
    private static void LockedLog()
    {
        var fixture = new Fixture("locked-log"); Seed(fixture);
        using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) {
            signed.Complete(); string log = Put(fixture.Root, "APF/APFUpdater.log", "locked diagnostic sentinel");
            using (var lease = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Restore(fixture);
            AssertRoots(fixture); Check(File.ReadAllText(log) == "locked diagnostic sentinel", "Locked diagnostic logging cannot abort authenticated repair");
        }
    }
    private static void Junction()
    {
        var fixture = new Fixture("junction"); Seed(fixture); string target = Path.Combine(fixture.Root, "empty-target"); Directory.CreateDirectory(target);
        string link = Path.Combine(fixture.GameData, Roots[0].ToLowerInvariant(), "empty-child");
        using (var process = Process.Start(new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Arguments = ApfProcessArguments.Join(new[] { "/c", "mklink", "/J", link, target }), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }))
            Check(process.WaitForExit(10000) && process.ExitCode == 0, "Owned empty child junction created");
        using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) { signed.Complete(); var before = Snapshot(fixture.GameData); Rejected(() => Restore(fixture), "Empty child junction refused before rename"); Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Junction refusal preserves payload and casing"); }
    }
    private static void FlatFile()
    {
        var fixture = new Fixture("flat"); Put(fixture.Root, "GameData/modulemanager.4.2.3.dll", "flat sentinel");
        using (var signed = new Signed(fixture, new[] { Package("ModuleManager.4.2.3.dll") })) { signed.Complete(); Restore(fixture); Check(Directory.GetFiles(fixture.GameData).Select(Path.GetFileName).Contains("ModuleManager.4.2.3.dll", StringComparer.Ordinal), "Flat package filename restored Ordinal"); Check(File.ReadAllText(Path.Combine(fixture.GameData, "ModuleManager.4.2.3.dll")) == "flat sentinel", "Flat package content unchanged"); }
    }
    private static void MissingRoot()
    {
        var fixture = new Fixture("missing"); using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) { signed.Complete(); var before = Snapshot(fixture.GameData); Restore(fixture); Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Absent declared roots create no payload"); }
    }
    private static void ForgedJournal()
    {
        var fixture = new Fixture("forged"); Seed(fixture); using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) {
            signed.Complete(); Put(fixture.Root, "APF/RootCasingRepair.json", ApfJson.Serialize(new { Payload = "{}", Mac = new string('0', 64) }));
            var before = Snapshot(fixture.GameData); string journal = File.ReadAllText(fixture.Journal); Rejected(() => Restore(fixture), "Forged journal HMAC rejected");
            Check(before.SequenceEqual(Snapshot(fixture.GameData)) && File.ReadAllText(fixture.Journal) == journal, "Forged journal cannot rename or alter payload");
        }
    }
    private static void PendingJournal()
    {
        var fixture = new Fixture("pending"); Seed(fixture); using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) {
            signed.Complete(); string temporary = ".aetherfront-case-" + Guid.NewGuid().ToString("N");
            Directory.Move(Path.Combine(fixture.GameData, Roots[0].ToLowerInvariant()), Path.Combine(fixture.GameData, temporary));
            var recordType = typeof(NativeInstall).Assembly.GetType("KesasInstall.PackageCasing", true).GetNestedType("RenameRecord", BindingFlags.NonPublic);
            object record = Activator.CreateInstance(recordType, true);
            foreach (var field in new Dictionary<string, object> { { "Original", Roots[0].ToLowerInvariant() }, { "Canonical", Roots[0] }, { "Temporary", temporary }, { "Status", "applying" }, { "Directory", true } }) recordType.GetProperty(field.Key).SetValue(record, field.Value, null);
            string payload = ApfJson.Serialize(record); string mac; using (var hmac = new HMACSHA256(ApfTransactionSecurity.LoadKey(Path.Combine(fixture.Data, "APFHelper.key")))) mac = ApfJson.ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            Put(fixture.Root, "APF/RootCasingRepair.json", ApfJson.Serialize(new { Payload = payload, Mac = mac }));
            Restore(fixture); AssertRoots(fixture); Check(!Directory.Exists(Path.Combine(fixture.GameData, temporary)), "Authenticated interrupted first rename recovers temporary-held payload");
            var envelope = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(fixture.Journal)); Check(ApfJson.Deserialize<Dictionary<string, object>>(envelope["payload"])["status"].ToString() == "complete", "Recovery journal ends complete");
        }
    }
    private static void PendingSourceJunction()
    {
        var fixture = new Fixture("pending-junction"); Seed(fixture);
        using (var signed = new Signed(fixture, Roots.Select(root => Package(root)))) {
            signed.Complete(); string temporary = ".aetherfront-case-" + Guid.NewGuid().ToString("N");
            string target = Path.Combine(fixture.Root, "empty-target"); Directory.CreateDirectory(target);
            string link = Path.Combine(fixture.GameData, Roots[0].ToLowerInvariant(), "empty-child");
            using (var process = Process.Start(new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Arguments = ApfProcessArguments.Join(new[] { "/c", "mklink", "/J", link, target }), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }))
                Check(process.WaitForExit(10000) && process.ExitCode == 0, "Pending-journal empty child junction created");
            var recordType = typeof(NativeInstall).Assembly.GetType("KesasInstall.PackageCasing", true).GetNestedType("RenameRecord", BindingFlags.NonPublic);
            object record = Activator.CreateInstance(recordType, true);
            foreach (var field in new Dictionary<string, object> { { "Original", Roots[0].ToLowerInvariant() }, { "Canonical", Roots[0] }, { "Temporary", temporary }, { "Status", "applying" }, { "Directory", true } }) recordType.GetProperty(field.Key).SetValue(record, field.Value, null);
            string payload = ApfJson.Serialize(record); string mac;
            using (var hmac = new HMACSHA256(ApfTransactionSecurity.LoadKey(Path.Combine(fixture.Data, "APFHelper.key")))) mac = ApfJson.ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            Put(fixture.Root, "APF/RootCasingRepair.json", ApfJson.Serialize(new { Payload = payload, Mac = mac }));
            var before = Snapshot(fixture.GameData); string journal = File.ReadAllText(fixture.Journal); Exception rejected = null;
            try { Restore(fixture); } catch (Exception error) { rejected = Unwrap(error); }
            Check(rejected is InvalidDataException && rejected.Message.IndexOf("reparse", StringComparison.OrdinalIgnoreCase) >= 0, "Existing-source recovery rejects the child junction specifically, not an unrelated I/O failure");
            Check(before.SequenceEqual(Snapshot(fixture.GameData)) && File.ReadAllText(fixture.Journal) == journal && !Directory.Exists(Path.Combine(fixture.GameData, temporary)), "Existing-source journal rejects empty child junction before any rename or journal mutation");
        }
    }
    private static void Mutable()
    {
        const string prefix = "GameData/HideEmptyTechTreeNodes/Resources/";
        foreach (string name in new[] { "HETTN.TechTree", "HETTN.TechTree.tmp" }) Check(RuntimeFiles.IsMutable(prefix + name), "Exact generated runtime positive: " + name);
        foreach (string name in new[] { "HETTN.TechTree.cfg", "HETTN.TechTree.dll", "HETTN.TechTree.bak", "HETTN.TechTree.tmp.cfg", "HETTN.TechTree.tmp.dll", "HETTN.TechTree.tmp.bak", "other.tmp", "child/HETTN.TechTree", "HETTN.TechTree.tmp.more" }) Check(!RuntimeFiles.IsMutable(prefix + name), "Runtime negative: " + name);
        Check(!RuntimeFiles.IsMutable("GameData/OtherAddon/Resources/HETTN.TechTree"), "Other addon cannot inherit HETTN exemption");
        var fixture = new Fixture("mutable"); string required = Put(fixture.Root, prefix + "required.cfg", "immutable");
        var expected = new[] { new ApfFileState { Path = "GameData/HideEmptyTechTreeNodes/Resources/required.cfg", Sha256 = ApfJson.Sha256File(required) } };
        Put(fixture.Root, prefix + "HETTN.TechTree", "generated"); Put(fixture.Root, prefix + "HETTN.TechTree.tmp", "interrupted");
        Check(RuntimeFiles.SameFiles(expected, RuntimeFiles.ScanFiles(fixture.GameData, Path.Combine(fixture.GameData, "HideEmptyTechTreeNodes"))), "Actual scanner excludes exactly two generated outputs");
        Put(fixture.Root, prefix + "HETTN.TechTree.dll", "injected");
        Check(!RuntimeFiles.SameFiles(expected, RuntimeFiles.ScanFiles(fixture.GameData, Path.Combine(fixture.GameData, "HideEmptyTechTreeNodes"))), "Actual scanner detects added DLL beside generated outputs");
    }
    private static void Resources(bool selected, bool present)
    {
        var fixture = new Fixture("resources-" + selected + "-" + present); var packages = new List<ApfPackage>();
        if (selected) packages.Add(Package("HideEmptyTechTreeNodes")); if (present) Put(fixture.Root, "GameData/hideemptytechtreenodes/Plugins/addon.dll", "sentinel");
        using (var signed = new Signed(fixture, packages)) { signed.Complete(); Restore(fixture); string path = Path.Combine(fixture.GameData, "HideEmptyTechTreeNodes/Resources"); Check(Directory.Exists(path) == (selected && present), "Resources exists only for selected, installed trusted HETTN: " + selected + "/" + present); if (selected && present) Check(!Directory.GetFileSystemEntries(path).Any(), "HETTN Resources is empty; no generated payload fabricated"); }
    }
    private static void UntrustedResources()
    {
        var fixture = new Fixture("resources-untrusted"); Put(fixture.Root, "GameData/hideemptytechtreenodes/Plugins/addon.dll", "sentinel");
        using (var signed = new Signed(fixture, new[] { Package("HideEmptyTechTreeNodes") })) { signed.Complete(); signed.Feed.ReleaseNotes = "forged"; Put(fixture.Root, "APF/.client-update-staging/connect/feed.json", signed.Feed.ToJson()); var before = Snapshot(fixture.GameData); Rejected(() => Restore(fixture), "Untrusted HETTN catalog refuses Resources creation"); Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Untrusted HETTN leaves all paths unchanged"); }
    }
    private static void CanonicalContract()
    {
        var method = typeof(NativeInstall).Assembly.GetType("KesasInstall.PackageCasing", true).GetMethod("CanonicalRoot");
        foreach (string root in Roots) Check((string)method.Invoke(null, new object[] { Package(root) }) == root, "CanonicalRoot preserves signed spelling: " + root);
        foreach (string root in new[] { "../Escape", "/Rooted", " Padded", "Padded " }) Rejected(() => method.Invoke(null, new object[] { Package(root) }), "Unsafe canonical root refused: " + root);
    }

    private static void Migration()
    {
        var fixture = new Fixture("migration"); var transaction = TinyPack(fixture);
        const string hetRoot = "HideEmptyTechTreeNodes";
        string hetArchive = Path.Combine(fixture.Root, "APF/.staging/HETTN.zip");
        using (var zip = ZipFile.Open(hetArchive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(hetRoot + "/required.cfg").Open())) writer.Write("HETTN immutable sentinel");
        var hetPackage = Package(hetRoot, hetArchive); hetPackage.Version = "1-repair27";
        transaction.Manifest.ManagedPackages.Add(hetPackage);
        transaction.Packages.Add(new ApfStagedPackage { Package = hetPackage, ArchivePath = hetArchive });
        string hetRequired = Put(fixture.Root, "GameData/" + hetRoot + "/required.cfg", "HETTN immutable sentinel");
        var previous = new ApfPackageState { Id = hetPackage.Id, Version = "1", ArchiveSha256 = hetPackage.Sha256, Files = new List<ApfFileState> { new ApfFileState { Path = "GameData/hideemptytechtreenodes/required.cfg", Sha256 = ApfJson.Sha256File(hetRequired) } } };
        transaction.ExistingState = new ApfPackState { PackId = "kesas", PackVersion = "kesas-rc26", Packages = new List<ApfPackageState> { previous } };
        Put(fixture.Root, "GameData/LunaMultiplayer/Data/APFPackState.json", ApfJson.Serialize(transaction.ExistingState));
        foreach (string name in new[] { "HETTN.TechTree", "HETTN.TechTree.tmp" }) {
            Put(fixture.Root, "GameData/" + hetRoot + "/Resources/" + name, "old generated cache: " + name);
            Check(!RuntimeFiles.IsMutable("GameData/" + hetRoot + "/Resources/" + name), "Loaded old26 does not exempt existing HETTN output: " + name);
        }
        Check(previous.ArchiveSha256 == hetPackage.Sha256 && previous.Version != hetPackage.Version && transaction.Manifest.ManagedPackages.Count(package => package.Version.EndsWith("-repair27", StringComparison.Ordinal)) == 1, "Only HETTN version bumps to repair27; archive SHA256 unchanged");
        Put(fixture.Root, "HETTN-migration.enabled", "verify cache replacement and regenerated runtime files");
        using (var signed = new Signed(fixture, transaction.Manifest.ManagedPackages, false)) {
            foreach (string name in new[] { "LmpClient.dll", "ApfCore.dll", "KesasInstall.dll" }) Copy(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins", name), Path.Combine(fixture.Helper, "Plugins", name));
            var update = new ApfClientUpdateTransaction { GameRoot = fixture.Root, GameDataPath = fixture.GameData, ProcessId = 0, KspExecutable = Path.Combine(fixture.Root, "KSP_x64.exe"), KspArguments = new List<string> { "--startup-stub" }, ArchivePath = signed.Archive, Manifest = signed.Feed, Approval = ApfClientUpdateApproval.Capture(fixture.Root) };
            ApfTransactionSecurity.Sign(update, ApfTransactionSecurity.LoadKey(Path.Combine(fixture.Data, "APFHelper.key")));
            string request = Save(fixture, transaction); File.WriteAllText(request + ".client", ApfJson.Serialize(update));
            PreparedPack.Prepare(transaction, request, Console.WriteLine, () => false);
            Check(File.ReadAllText(Path.Combine(fixture.GameData, hetRoot, "Resources/HETTN.TechTree")) == "old generated cache: HETTN.TechTree", "Preparation retains installed old generated HETTN cache");
            using (var completion = NativeInstall.Run(fixture.Helper, request, "pack")) {
                Check(Directory.GetDirectories(fixture.GameData).Select(Path.GetFileName).Where(name => Roots.Select(root => root.ToLowerInvariant()).Contains(name, StringComparer.Ordinal)).Count() == 4, "Loaded old26 helper lowercases all four roots before restart");
                Check(!Directory.Exists(Path.Combine(fixture.GameData, hetRoot, "Resources")) && File.ReadAllText(Path.Combine(fixture.GameData, hetRoot, "required.cfg")) == "HETTN immutable sentinel", "Old26 prepared replacement removes both generated caches before successful final scan");
                var installed = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(fixture.Data, "APFPackState.json"))).Packages.Single(package => package.Id == hetPackage.Id);
                Check(installed.Version == hetPackage.Version && installed.ArchiveSha256 == previous.ArchiveSha256, "Committed HETTN receipt records version-only repair27 replacement");
                Check(ApfJson.Sha256File(Path.Combine(fixture.Helper, "Plugins/KesasInstall.dll")) == ApfJson.Sha256File(nextEngine), "Old helper installs signed new27 Engine bytes");
                completion.Restart(); Check(completion.RestartedProcessId > 0, "Exactly one explicit Completion.Restart starts startup stub");
                bool second = false; try { completion.Restart(); } catch (InvalidOperationException) { second = true; } Check(second, "Second restart explicitly rejected");
                string result = Path.Combine(fixture.Root, "startup-result.json"); var clock = Stopwatch.StartNew();
                string outcome = null;
                while (clock.ElapsedMilliseconds < 20000) {
                    try { if (File.Exists(result)) { outcome = File.ReadAllText(result); if (outcome == "passed" || outcome == "failed") break; } }
                    catch (IOException error) when ((error.HResult & 65535) == 32 || (error.HResult & 65535) == 33) { }
                    System.Threading.Thread.Sleep(20);
                }
                Check(outcome == "passed", "Restarted stub resolves new Engine before typed startup call");
            }
            AssertRoots(fixture);
            Check(File.ReadAllLines(Path.Combine(fixture.Root, "startup-launch.txt")).Length == 1, "One launch marker proves no hidden second restart");
            Check(File.ReadAllText(Path.Combine(fixture.Root, "startup-engine.sha256")) == ApfJson.Sha256File(nextEngine), "Startup executes exact new27 DLL, not loaded old26 assembly");
            var proof = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(fixture.Root, "startup-hettn-proof.json")));
            Check(proof["canonicalRoot"] == hetRoot && proof["resourcesInitiallyEmpty"] == "true", "New27 startup restores HETTN casing and creates empty Resources");
            Check(proof["scanClean"] == "true" && proof["generatedFiles"] == "2" && proof["engineSha256"] == ApfJson.Sha256File(nextEngine), "New27 ScanFinal remains clean after both runtime outputs regenerate");
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StartupStub()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string marker = Path.Combine(root, "startup-launch.txt"); File.AppendAllText(marker, "startup\n");
        try {
            if (File.ReadAllLines(marker).Length != 1) throw new InvalidDataException("Unexpected second game launch.");
            File.WriteAllText(Path.Combine(root, "startup-engine.sha256"), ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location));
            typeof(NativeInstall).Assembly.GetType("KesasInstall.PackageCasing", true).GetMethod("RestoreAtStartup").Invoke(null, new object[] { root, Version });
            string gameData = Path.Combine(root, "GameData"); var actual = Directory.GetDirectories(gameData).Select(Path.GetFileName).ToArray();
            File.WriteAllText(Path.Combine(root, "startup-roots.json"), ApfJson.Serialize(actual));
            if (!Roots.All(name => actual.Contains(name, StringComparer.Ordinal) && ModelIndex(gameData).Contains(name + "/Parts/Model/model"))) throw new InvalidDataException("Startup did not restore strict-case model URLs.");
            if (File.Exists(Path.Combine(root, "HETTN-migration.enabled"))) {
                const string addon = "HideEmptyTechTreeNodes";
                string resources = Path.Combine(gameData, addon, "Resources");
                if (!actual.Contains(addon, StringComparer.Ordinal) || !Directory.Exists(resources) || Directory.GetFileSystemEntries(resources).Length != 0) throw new InvalidDataException("Startup did not create canonical empty HETTN Resources.");
                foreach (string name in new[] { "HETTN.TechTree", "HETTN.TechTree.tmp" }) Put(root, "GameData/" + addon + "/Resources/" + name, "new generated cache: " + name);
                var catalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(gameData, "LunaMultiplayer/Launcher/install-catalog.json")));
                var state = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(gameData, "LunaMultiplayer/Data/APFPackState.json")));
                if (!RuntimeFiles.ScanFinal(gameData, catalog.Full, state).IsClean) throw new InvalidDataException("New27 final scan rejects regenerated HETTN output.");
                File.WriteAllText(Path.Combine(root, "startup-hettn-proof.json"), ApfJson.Serialize(new Dictionary<string, string> { { "canonicalRoot", addon }, { "resourcesInitiallyEmpty", "true" }, { "scanClean", "true" }, { "generatedFiles", "2" }, { "engineSha256", ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location) } }));
            }
            PublishStartupResult(root, "passed"); return 0;
        } catch (Exception error) { File.WriteAllText(Path.Combine(root, "startup-failure.txt"), error.ToString()); PublishStartupResult(root, "failed"); return 1; }
    }
    private static void PublishStartupResult(string root, string result)
    {
        string temporary = Path.Combine(root, "startup-result.tmp");
        File.WriteAllText(temporary, result);
        File.Move(temporary, Path.Combine(root, "startup-result.json"));
    }
}
