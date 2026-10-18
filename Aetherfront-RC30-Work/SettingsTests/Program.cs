using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private const string Version = "kesas-rc30";
    // These are the sixteen observed preference paths, not a broad PluginData exemption.
    private static string[] Settings = {
        "BonVoyage/Plugins/PluginData/BonVoyage/config.xml",
        "ConnectedLivingSpace/Plugins/PluginData/CLS_Settings.dat",
        "ExtraplanetaryLaunchpads/Plugins/PluginData/Launchpad/Settings.cfg",
        "KerbalKonstructs/PluginDataWindowPositions.cfg",
        "kOS/Plugins/PluginData/kOS/config.xml",
        "ShipManifest/Plugins/PluginData/SMSettings.dat",
        "TriggerTech/KerbalAlarmClock/Plugins/PluginData/settings.cfg",
        "WaypointManager/PluginData/WaypointManager.cfg",
        "Astrogator/AstrogatorSettings.cfg",
        "EditorExtensionsRedux/PluginData/RememEditor.cfg",
        "EditorExtensionsRedux/Plugins/config.xml",
        "Hangar/Plugins/PluginData/Hangar/config.xml",
        "TweakScale/plugins/PluginData/Scale/config.xml",
        "REPOSoftTech/DeepFreeze/PluginData/Config.cfg",
        "[x]_Science!/PluginData/settings.cfg",
        "PlanetShine/Plugins/PluginData/Settings.cfg"
    };
    private static readonly List<string> Failures = new List<string>();
    private static string baseline, engine, nextEngine, output, mode, filter, legacyFaultCheckpoint;
    private static int checks, cases;
    private static RSA signingKey;

    private static int Main(string[] args)
    {
        if (args.Length == 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APF_SETTINGS_ARGUMENTS")))
            args = Environment.GetEnvironmentVariable("APF_SETTINGS_ARGUMENTS").Split('\n');
        bool stub = args.Contains("--startup-stub");
        string plugins = stub ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData/LunaMultiplayer/Plugins") : Path.Combine(Option(args, "--baseline"), "GameData/LunaMultiplayer/Plugins");
        string selected = stub ? Path.Combine(plugins, "KesasInstall.dll") : Option(args, "--engine");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            string path = name == "KesasInstall.dll" ? selected : Path.Combine(plugins, name);
            if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KSP_x64_Data/Managed", name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return stub ? StartupStub() : Run(args); }
        catch (Exception error) {
            // Dependency/JIT failures can occur before StartupStub enters its
            // own try block. They still need an atomic outcome for the parent.
            if (stub) PublishStartupFailure(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), error);
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1;
        }
    }
    private static string Option(string[] args, string name, string fallback = "")
    { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        baseline = Path.GetFullPath(Option(args, "--baseline")); engine = Path.GetFullPath(Option(args, "--engine"));
        nextEngine = Path.GetFullPath(Option(args, "--new-engine", engine)); output = Path.GetFullPath(Option(args, "--output"));
        mode = Option(args, "--mode", "new"); filter = Option(args, "--case");
        if (Option(args,"--profile")=="potato") Settings=Settings.Where(path=>!path.StartsWith("PlanetShine/",StringComparison.Ordinal)).ToArray();
        legacyFaultCheckpoint = Option(args, "--legacy-fault-checkpoint"); Directory.CreateDirectory(output);
        if (!string.IsNullOrEmpty(legacyFaultCheckpoint) &&
            (!(mode == "migration26" || mode == "migration27" || mode == "migration28" || mode == "migration29") ||
             !new[] { "plan-saved", "file-written:0", "file-recorded:0", "file-written:8", "file-recorded:8" }.Contains(legacyFaultCheckpoint, StringComparer.Ordinal)))
            throw new ArgumentException("Checkpoint runs require migration26/migration27 and a supported legacy checkpoint.");
        signingKey = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
        Check(RuntimeName() == Option(args, "--expected-runtime", "CLR"), "Requested CLR actually executes suite");
        Check(ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location) == ApfJson.Sha256File(engine), "Exact selected production Engine loaded");
        Check(ApfJson.Sha256File(typeof(ApfPackScanner).Assembly.Location) == ApfJson.Sha256File(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/ApfCore.dll")), "Normal baseline Core loaded, no test Core");
        if (mode == "old27") Case("old27-eight-rejections", NegativeControl);
        else if (mode == "migration26" || mode == "migration27" || mode == "migration28" || mode == "migration29" || mode == "fresh26" || mode == "fresh27" || mode == "fresh28" || mode == "fresh29") Case(mode + "-signed-one-restart", Migration);
        else if (mode == "new") {
            Case("prepared-directory-rename-retry", DirectoryRetry);
            Case("exact-sixteen-contract", Contract);
            Case("unchanged-sixteen-clean", Unchanged);
            Case("prepared-latest-shutdown-bytes", Replacement);
            Case("prepared-immutable-corruption", PreparedCorruption);
            Case("prepared-setting-tamper", PreparedSettingTamper);
            Case("prepared-setting-hardlink", PreparedSettingHardlink);
            foreach (string change in new[] { "unknown.cfg", "unknown.dll", "changed-dll", "changed-model", "missing-model" }) {
                string captured = change; Case("integrity-" + change, () => Integrity(captured));
            }
            Case("same-filenames-other-root", OtherRoot);
            Case("mutable-only-missing-root", MutableOnlyMissingRoot);
            foreach (string unsafeKind in new[] { "locked", "oversized", "directory", "reparse" }) {
                string captured = unsafeKind; Case("refuse-" + unsafeKind, () => Refusal(captured));
            }
        } else throw new ArgumentException("Unknown test mode.");
        Check(cases > 0, "Selection executes at least one case");
        File.WriteAllText(Path.Combine(output, "summary.json"), ApfJson.Serialize(new {
            mode, cases, checks, legacyFaultCheckpoint, failures = Failures, runtime = RuntimeName(), enginePath = engine,
            engineSha256 = ApfJson.Sha256File(engine), coreSha256 = ApfJson.Sha256File(typeof(ApfPackScanner).Assembly.Location),
            unityGameplayTested = false, fixturesRetained = true
        }));
        Console.WriteLine("RESULT mode=" + mode + " cases=" + cases + " checks=" + checks + " failures=" + Failures.Count);
        signingKey.Dispose(); return Failures.Count == 0 ? 0 : 1;
    }
    private static string RuntimeName() { return Type.GetType("Mono.Runtime") == null ? "CLR" : "KSP-Mono"; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    private static void DirectoryRetry()
    {
        string root=Path.Combine(output,"directory-retry"), source=Path.Combine(root,"source"), target=Path.Combine(root,"target");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source,"sentinel"),"immutable");
        var method=typeof(NativeInstall).Assembly.GetType("KesasInstall.AtomicFiles",true).GetMethod("MoveDirectory",BindingFlags.NonPublic|BindingFlags.Static);
        Action move=()=>method.Invoke(null,new object[]{root,source,target});
        IntPtr handle=CreateFileW(source,0x80000000,3,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        Check(handle!=new IntPtr(-1),"Owned directory locked without delete sharing");
        var release=new System.Threading.Thread(()=>{System.Threading.Thread.Sleep(120);CloseHandle(handle);});
        release.Start(); try { move(); } finally { release.Join(); }
        Check(!Directory.Exists(source)&&File.ReadAllText(Path.Combine(target,"sentinel"))=="immutable","Transient directory lock retries atomic rename without copying");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source,"sentinel"),"source");
        Rejected(move,"Existing destination never overwritten");
        Check(File.ReadAllText(Path.Combine(target,"sentinel"))=="immutable","Existing destination remains intact");
        string held=Path.Combine(root,"held"); Directory.CreateDirectory(held);
        var timer=Stopwatch.StartNew(); handle=CreateFileW(held,0x80000000,3,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        Check(handle!=new IntPtr(-1),"Persistent owned directory handle valid");
        try { Rejected(()=>method.Invoke(null,new object[]{root,held,Path.Combine(root,"absent")}),"Persistent directory lock fails within bounded retry"); }
        finally { CloseHandle(handle); }
        Check(timer.ElapsedMilliseconds<3000&&Directory.Exists(held)&&!Directory.Exists(Path.Combine(root,"absent")),"Persistent denial preserves source and does not create destination");
        string link=Path.Combine(root,"junction"); Junction(link,target);
        Rejected(()=>method.Invoke(null,new object[]{root,link,Path.Combine(root,"unsafe")}),"Directory retry rejects junction source");
    }
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidDataException(name); checks++; Console.WriteLine("PASS " + name); }
    private static void Case(string name, Action action)
    {
        if (!string.IsNullOrEmpty(filter) && !filter.Split(',').Contains(name, StringComparer.Ordinal)) return;
        cases++; Console.WriteLine("CASE " + name);
        try { action(); } catch (Exception error) {
            var cause = Unwrap(error); string detail = name + ": " + cause.GetType().Name + ": " + cause.Message;
            Failures.Add(detail); Console.Error.WriteLine("FAIL " + detail);
            File.WriteAllText(Path.Combine(output, "failure-" + name + ".txt"), error.ToString());
        } finally { PreparedPack.Close(); }
    }
    private static Exception Unwrap(Exception error)
    { return error is TargetInvocationException call && call.InnerException != null ? Unwrap(call.InnerException) : error; }
    private static void Rejected(Action action, string label)
    {
        bool refused = false;
        try { action(); } catch (Exception error) {
            var cause = Unwrap(error);
            if (!(cause is IOException || cause is InvalidDataException || cause is UnauthorizedAccessException)) throw;
            refused = true;
        }
        Check(refused, label);
    }
    private static string Put(string root, string relative, byte[] bytes)
    { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); return path; }
    private static string Put(string root, string relative, string text) { return Put(root, relative, Encoding.UTF8.GetBytes(text)); }
    private static void Copy(string source, string target)
    { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, true); }
    private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return ApfJson.ToHex(sha.ComputeHash(bytes)); }
    private static byte[] UserBytes(string path, string phase)
    { return Encoding.UTF8.GetBytes("\uFEFFsynthetic setting\r\npath=" + path + "\r\nphase=" + phase + "\r\nvalue=\u00e4\u03bb\r\n"); }
    private static string RootOf(string path) { return path.Split('/')[0]; }
    private static bool IsSetting(string path) { return Settings.Contains(path.StartsWith("GameData/", StringComparison.OrdinalIgnoreCase) ? path.Substring(9) : path, StringComparer.OrdinalIgnoreCase); }

    private sealed class Fixture
    {
        public string Root, GameData, Data;
        public string Helper { get { return Path.Combine(GameData, "LunaMultiplayer"); } }
        public ApfSyncTransaction Transaction;
        public Dictionary<string, byte[]> Expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> Payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public Fixture(string label, bool replace = false) {
            // Production AtomicText adds its own GUID suffix. Keep synthetic roots
            // short enough for the Windows CLR's MAX_PATH behavior as well as Mono.
            Root = Path.Combine(output, "F" + Guid.NewGuid().ToString("N").Substring(0, 8));
            GameData = Path.Combine(Root, "GameData"); Data = Path.Combine(GameData, "LunaMultiplayer/Data"); Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Path.Combine(GameData, "Squad"));
            Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(Root, "KSP_x64.exe"));
            Put(Root, "settings.cfg", "ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = retained\n");
            Put(Root, "saves/Fixture/persistent.sfs", "synthetic save sentinel"); Put(Data, "privatekey.txt", "synthetic identity sentinel");
            Put(Data, "APFServer.txt", "127.0.0.1:18865"); Put(Data, "APFTlsCertificatePins.txt", new string('a', 64));
            Put(Data, "APFClientUpdateManifestUrl.txt", "https://127.0.0.1:18866/no-network.json");
            Copy(Path.Combine(baseline, "GameData/LunaMultiplayer/Data/APFClient.json"), Path.Combine(Data, "APFClient.json"));
            ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(Data, "APFHelper.key"));
            Transaction = new ApfSyncTransaction {
                GameRoot = Root, GameDataPath = GameData, KspExecutable = Path.Combine(Root, "KSP_x64.exe"), ProcessId = 0,
                KspArguments = new List<string> { "--startup-stub" }, StatePath = Path.Combine(Data, "APFPackState.json"),
                Manifest = Manifest(), ExistingState = new ApfPackState { PackId = "kesas", PackVersion = "kesas-rc26" },
                Packages = new List<ApfStagedPackage>(), Deletes = new List<ApfDeleteChange>()
            };
            foreach (string root in Settings.Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase)) {
                string setting = Settings.First(path => RootOf(path)==root);
                var contents = new Dictionary<string, byte[]> {
                    { root + "/Parts/part.cfg", Encoding.UTF8.GetBytes("PART { name = synthetic-" + root + " }") },
                    { root + "/Parts/model.mu", Encoding.UTF8.GetBytes("model-v1:" + root) },
                    { root + "/Plugins/immutable.dll", Encoding.UTF8.GetBytes("not loaded; immutable assembly fixture:" + root) }
                };
                if (root == "ConnectedLivingSpace") contents.Add(setting, Encoding.UTF8.GetBytes("CLS shipped default\r\n"));
                string archive = Path.Combine(Root, "APF/.staging/archives/" + root + ".zip"); Directory.CreateDirectory(Path.GetDirectoryName(archive));
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) foreach (var item in contents) {
                    using (var stream = zip.CreateEntry(item.Key).Open()) stream.Write(item.Value, 0, item.Value.Length);
                    Put(GameData, item.Key, item.Value); Payloads.Add(item.Key, item.Value);
                }
                var package = new ApfPackage { Id = "fixture-" + new string(root.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant(), Version = replace ? "1-settings30" : "1", Destination = "GameData", StripPrefix = root,
                    ArchiveUrl = "https://127.0.0.1:18866/" + root + ".zip", Sha256 = ApfJson.Sha256File(archive), SizeBytes = new FileInfo(archive).Length };
                Transaction.Manifest.ManagedPackages.Add(package);
                Transaction.ExistingState.Packages.Add(new ApfPackageState { Id = package.Id, Version = "1", ArchiveSha256 = package.Sha256,
                    Files = ApfPackScanner.BuildFileState(GameData, Path.Combine(GameData, root)) });
                if (replace) Transaction.Packages.Add(new ApfStagedPackage { Package = package, ArchivePath = archive });
                foreach(string path in Settings.Where(value => RootOf(value)==root)){Expected.Add(path, UserBytes(path, "before-prepare")); Put(GameData, path, Expected[path]);}
            }
            Put(Data, "APFPackState.json", ApfJson.Serialize(Transaction.ExistingState));
            Put(Data, "KesasInstallProfile.json", ApfJson.Serialize(new Dictionary<string, string> { { "mode", "full" }, { "catalogVersion", "kesas-rc26" } }));
        }
        public string Save() {
            ApfTransactionSecurity.Sign(Transaction, ApfTransactionSecurity.LoadKey(Path.Combine(Data, "APFHelper.key")));
            Check(ApfTransactionSecurity.Verify(Transaction, ApfTransactionSecurity.LoadKey(Path.Combine(Data, "APFHelper.key"))), "Real local HMAC transaction verifies");
            return Put(Root, "APF/.staging/connect/" + Guid.NewGuid().ToString("N").Substring(0, 12) + "/transaction.json", ApfJson.Serialize(Transaction));
        }
        public void LateSettings() {
            foreach (string path in Settings) { Expected[path] = UserBytes(path, "latest-shutdown"); Put(GameData, path, Expected[path]); }
        }
        public void AssertSettings(string label) {
            foreach (string path in Settings) Check(File.ReadAllBytes(Path.Combine(GameData, path)).SequenceEqual(Expected[path]), label + ": " + path);
        }
        public ApfPackState State() { return ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(Data, "APFPackState.json"))); }
    }
    private static ApfPackManifest Manifest()
    { return new ApfPackManifest { ServerId = "kesas-b6286df413b9426f", PackId = "kesas", PackVersion = Version, RequiredClientVersion = Version,
        KspVersion = "1.12.5", ProtectedRoots = new List<string> { "Squad", "SquadExpansion", "LunaMultiplayer", "000_Harmony" },
        ManagedPackages = new List<ApfPackage>(), ClientOnly = new List<ApfClientOnlyRule>() }; }
    private static bool Present(Fixture fixture, ApfPackage package, ApfPackageState state)
    {
        var installer = new Installer(fixture.Root, new IPEndPoint(IPAddress.Loopback, 18865), Version);
        return (bool)typeof(Installer).GetMethod("PackagePresent", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(installer, new object[] { package, state });
    }
    private static void Agreement(Fixture fixture, bool clean, string label)
    {
        var state = fixture.State();
        bool present = fixture.Transaction.Manifest.ManagedPackages.All(package => Present(fixture, package, state.Packages.Single(item => item.Id == package.Id)));
        var plan = RuntimeFiles.ScanFinal(fixture.GameData, fixture.Transaction.Manifest, state);
        Check(present == clean && plan.IsClean == clean, label + ": production preflight/final agreement");
    }
    private static void Contract()
    {
        var type = typeof(NativeInstall).Assembly.GetType("KesasInstall.UserSettings", true);
        var property = type.GetProperty("Paths");
        string[] actual = (string[])(property != null ? property.GetValue(null, null) : type.GetField("Paths").GetValue(null));
        Check(Settings.SequenceEqual(actual), "Production exact-sixteen list matches observed contract");
        foreach (string setting in Settings) {
            Check(RuntimeFiles.IsMutable(setting) && RuntimeFiles.IsMutable("GameData/" + setting.ToUpperInvariant()), "Exact normalized mutable setting: " + setting);
            foreach (string suffix in new[] { ".dll", ".bak", "/child.cfg" }) Check(!RuntimeFiles.IsMutable("GameData/" + setting + suffix), "No suffix exemption: " + setting + suffix);
            Check(!RuntimeFiles.IsMutable("GameData/OtherAddon/" + setting.Substring(setting.IndexOf('/') + 1)), "No other-root exemption: " + setting);
        }
    }
    private static void Unchanged()
    {
        var fixture = new Fixture("unchanged"); Agreement(fixture, true, "All selected changed/generated settings");
        var before = Snapshot(fixture.GameData); string request = fixture.Save();
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false);
        using (NativeInstall.Run(fixture.Helper, request, "pack")) { }
        fixture.AssertSettings("Unchanged package preserves original bytes"); Agreement(fixture, true, "Committed unchanged packages");
        Check(fixture.State().Packages.Count == Settings.Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "All selected unchanged packages remain recorded");
        Check(NoRollback(fixture), "Unchanged commit never rolls back");
        Check(before.Where(item => !item.Contains("LunaMultiplayer/")).SequenceEqual(Snapshot(fixture.GameData).Where(item => !item.Contains("LunaMultiplayer/"))), "Unchanged mod trees byte-identical");
    }
    private static void NegativeControl()
    {
        var fixture = new Fixture("old27-negative");
        var plan = RuntimeFiles.ScanFinal(fixture.GameData, fixture.Transaction.Manifest, fixture.Transaction.ExistingState);
        Check(plan.Errors.Count == 0 && plan.Delete.Count == 0 && plan.Install.Count == Settings.Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Old27 rejects all selected observed packages");
        Check(new HashSet<string>(plan.Install.Select(change => change.Package.Id)).SetEquals(fixture.Transaction.Manifest.ManagedPackages.Select(package => package.Id)), "Old27 rejection set is exactly the eight settings packages");
        int unrecorded = 0, changed = 0;
        foreach (string setting in Settings) {
            Check(!RuntimeFiles.IsMutable("GameData/" + setting), "Old27 has no observed-setting exemption: " + setting);
            var state = fixture.Transaction.ExistingState.Packages.Single(item => item.Id == "fixture-" + RootOf(setting).ToLowerInvariant());
            var record = state.Files.SingleOrDefault(item => item.Path.Equals("GameData/" + setting, StringComparison.OrdinalIgnoreCase));
            if (record == null) unrecorded++; else if (record.Sha256 != Hash(fixture.Expected[setting])) changed++;
        }
        Check(unrecorded == 7 && changed == 1, "Negative control reproduces seven unrecorded plus changed CLS default hash");
        string request = fixture.Save(); PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false);
        var before = Snapshot(fixture.GameData);
        Rejected(() => { using (NativeInstall.Run(fixture.Helper, request, "pack")) { } }, "Real old27 helper refuses final scan");
        Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Old27 rollback retains settings and installed state");
        Check(!NoRollback(fixture), "Old27 helper records actual rollback");
        fixture.AssertSettings("Negative control retains all settings");
    }

    private sealed class Signed
    {
        public ApfClientUpdateManifest Feed;
        public string Archive;
        public Signed(Fixture fixture, bool install) {
            string catalog = ApfJson.Serialize(new InstallCatalog { Version = Version, Full = fixture.Transaction.Manifest, Potato = fixture.Transaction.Manifest });
            var files = new Dictionary<string, byte[]> {
                { "GameData/LunaMultiplayer/Launcher/install-catalog.json", Encoding.UTF8.GetBytes(catalog) },
                { "GameData/LunaMultiplayer/Plugins/LmpClient.dll", File.ReadAllBytes(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/LmpClient.dll")) },
                { "GameData/LunaMultiplayer/Plugins/ApfCore.dll", File.ReadAllBytes(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/ApfCore.dll")) },
                { "GameData/LunaMultiplayer/Plugins/KesasInstall.dll", File.ReadAllBytes(nextEngine) }
            };
            Archive = Path.Combine(fixture.Root, "APF/.client-update-staging/connect/client.zip"); Directory.CreateDirectory(Path.GetDirectoryName(Archive));
            using (var zip = ZipFile.Open(Archive, ZipArchiveMode.Create)) foreach (var item in files) using (var stream = zip.CreateEntry(item.Key).Open()) stream.Write(item.Value, 0, item.Value.Length);
            Feed = new ApfClientUpdateManifest { SchemaVersion = 2, Version = Version, KspVersion = "1.12.5", PackageUrl = "https://127.0.0.1:18866/no-network.zip",
                Sha256 = ApfJson.Sha256File(Archive), SizeBytes = new FileInfo(Archive).Length,
                Files = files.Select(item => new ApfClientUpdateFile { Path = item.Key, Sha256 = Hash(item.Value), SizeBytes = item.Value.Length }).ToList() };
            ApfClientUpdateSecurity.Sign(Feed, signingKey);
            Check(ApfClientUpdateSecurity.Verify(Feed, signingKey) && ApfClientUpdateValidator.Validate(Feed).IsValid, "Synthetic local release passes real RSA verification and validator");
            Put(fixture.Root, "APF/.client-update-staging/connect/feed.json", Feed.ToJson());
            Put(fixture.Data, "APFClientUpdatePublicKey.xml", ApfClientUpdateSecurity.ExportXml(signingKey, false));
            Copy(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins/JsonFx.dll"), Path.Combine(fixture.Helper, "Plugins/JsonFx.dll"));
            if (install) foreach (var item in files) Put(fixture.Root, item.Key, item.Value);
        }
    }
    private static PreparedReceipt ReadPrepared(Fixture fixture, string request)
    {
        var envelope = ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(request + ".prepared"));
        byte[] secret = ApfTransactionSecurity.LoadKey(Path.Combine(fixture.Data, "APFHelper.key"));
        using (var hmac = new HMACSHA256(secret)) Check(envelope.Mac == Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(envelope.Payload))), "Real prepared envelope HMAC verifies");
        return ApfJson.Deserialize<PreparedReceipt>(envelope.Payload);
    }
    private static void Replacement()
    {
        var fixture = new Fixture("replacement", true); new Signed(fixture, true);
        string request = fixture.Save(); var before = Snapshot(fixture.GameData);
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false);
        Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Prepare does not mutate installed files");
        var prepared = ReadPrepared(fixture, request);
        Check(prepared.Packages.SelectMany(package => package.Files).Count(file => IsSetting(file.Path)) == 1, "Prepare authenticates archives, not stale in-game user-setting overlays");
        fixture.LateSettings(); fixture.AssertSettings("Late shutdown writes exist before helper");
        using (NativeInstall.Run(fixture.Helper, request, "pack")) { }
        fixture.AssertSettings("Replacement installs latest shutdown bytes"); Agreement(fixture, true, "Replacement verifies custom settings and immutable payload");
        var state = fixture.State();
        foreach (string setting in Settings) Check(state.Packages.SelectMany(package => package.Files).Any(file => file.Path.Equals("GameData/" + setting, StringComparison.OrdinalIgnoreCase) && file.Sha256 == Hash(fixture.Expected[setting])), "Final authenticated state includes refreshed setting: " + setting);
        Check(!File.ReadAllBytes(Path.Combine(fixture.GameData, Settings[1])).SequenceEqual(fixture.Payloads[Settings[1]]), "Latest CLS setting overrides shipped default");
        Check(NoRollback(fixture), "New28 replacement commits without rollback");
        var receipt = ReadReceipt(fixture.Root);
        Check(receipt["settingsPreservation"] == "prepared30", "New28 authenticated receipt marks prepared28 preservation");
        AssertReceiptMac(fixture.Root, receipt);
        var backups = Snapshot(Path.Combine(fixture.Root, "APF/.transaction-backup"));
        Restore(fixture.Root); fixture.AssertSettings("Prepared28 startup does not replay legacy backups");
        foreach (string setting in Settings) { fixture.Expected[setting] = UserBytes(setting, "after-completed-install"); Put(fixture.GameData, setting, fixture.Expected[setting]); }
        Restore(fixture.Root); fixture.AssertSettings("Repeated prepared28 startup retains newer user edits");
        Check(backups.SequenceEqual(Snapshot(Path.Combine(fixture.Root, "APF/.transaction-backup"))), "Prepared28 startup leaves recovery backups unchanged");
        Agreement(fixture, true, "Newer settings do not trigger repeated repair");
    }
    private static void PreparedCorruption()
    {
        var fixture = new Fixture("immutable-corruption", true); string request = fixture.Save();
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false); var prepared = ReadPrepared(fixture, request);
        Put(Path.Combine(fixture.Root, prepared.DataDirectory), "BonVoyage/Parts/model.mu", "corrupt immutable model"); fixture.LateSettings();
        var before = Snapshot(fixture.GameData);
        Rejected(() => { using (NativeInstall.Run(fixture.Helper, request, "pack")) { } }, "Corrupt prepared immutable file rejected before overlay/commit");
        Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Corrupt immutable preparation leaves installed tree/state byte-identical");
        fixture.AssertSettings("Corruption rejection retains latest user settings");
        Check(!File.Exists(Path.Combine(fixture.Root, prepared.DataDirectory, Settings[0])), "No user-setting overlay occurs after immutable validation fails");
    }
    private static void PreparedSettingTamper()
    {
        var fixture = new Fixture("prepared-setting-tamper", true); string request = fixture.Save();
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false); var prepared = ReadPrepared(fixture, request);
        Put(Path.Combine(fixture.Root, prepared.DataDirectory), Settings[1], "forged archive setting"); var before = Snapshot(fixture.GameData);
        Rejected(() => { using (NativeInstall.Run(fixture.Helper, request, "pack")) { } }, "Mutable setting in authenticated archive still requires preparation hash");
        Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Prepared setting tamper refused before installed mutation");
    }
    private static void PreparedSettingHardlink()
    {
        var fixture = new Fixture("prepared-setting-hardlink", true); string request = fixture.Save();
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false); var prepared = ReadPrepared(fixture, request);
        string target = Path.GetFullPath(Path.Combine(fixture.Root, prepared.DataDirectory, Settings[1]));
        string victim = Put(fixture.Root, "owned-hardlink-target.dat", fixture.Payloads[Settings[1]]);
        Check(ApfPaths.IsUnder(target, fixture.Root) && ApfPaths.IsUnder(victim, fixture.Root), "Both hardlink fixture targets stay inside owned game root");
        File.Delete(target); Check(CreateHardLink(target, victim, IntPtr.Zero), "Owned prepared setting hardlink created");
        string victimHash = ApfJson.Sha256File(victim); fixture.LateSettings(); var before = Snapshot(fixture.GameData);
        Rejected(() => { using (NativeInstall.Run(fixture.Helper, request, "pack")) { } }, "Prepared setting hardlink refused before refresh truncation");
        Check(ApfJson.Sha256File(victim) == victimHash, "Owned hardlink victim never truncated or overwritten");
        Check(before.SequenceEqual(Snapshot(fixture.GameData)), "Hardlinked staging setting refused before installed mutation");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
    private static void Integrity(string kind)
    {
        var fixture = new Fixture("integrity-" + kind); Agreement(fixture, true, "Initial clean baseline");
        if (kind == "unknown.cfg" || kind == "unknown.dll") Put(fixture.GameData, "BonVoyage/Plugins/PluginData/BonVoyage/" + kind, "injected");
        else if (kind == "changed-dll") Put(fixture.GameData, "BonVoyage/Plugins/immutable.dll", "changed immutable DLL");
        else if (kind == "changed-model") {
            string relative = "BonVoyage/Parts/model.mu"; var bytes = File.ReadAllBytes(Path.Combine(fixture.GameData, relative)); bytes[0] ^= 1; Put(fixture.GameData, relative, bytes);
        } else File.Delete(Path.Combine(fixture.GameData, "BonVoyage/Parts/model.mu"));
        Agreement(fixture, false, kind);
        var plan = RuntimeFiles.ScanFinal(fixture.GameData, fixture.Transaction.Manifest, fixture.State());
        Check(plan.Errors.Count == 0 && plan.Install.Count == 1 && plan.Install[0].Package.Id == "fixture-bonvoyage", "Only affected package detected: " + kind);
    }
    private static void OtherRoot()
    {
        var fixture = new Fixture("other-root"); string required = Put(fixture.GameData, "OtherAddon/required.cfg", "immutable");
        var package = new ApfPackage { Id = "fixture-other", Version = "1", Destination = "GameData", StripPrefix = "OtherAddon", ArchiveUrl = "https://127.0.0.1:18866/other.zip", SizeBytes = 1, Sha256 = new string('a', 64) };
        var state = new ApfPackageState { Id = package.Id, Version = "1", ArchiveSha256 = package.Sha256, Files = new List<ApfFileState> { new ApfFileState { Path = "GameData/OtherAddon/required.cfg", Sha256 = ApfJson.Sha256File(required) } } };
        fixture.Transaction.Manifest.ManagedPackages.Add(package); fixture.Transaction.ExistingState.Packages.Add(state);
        Put(fixture.Data, "APFPackState.json", ApfJson.Serialize(fixture.Transaction.ExistingState));
        foreach (string setting in Settings) {
            string relative = "OtherAddon/" + setting.Substring(setting.IndexOf('/') + 1); Put(fixture.GameData, relative, "not an exempt setting");
            Check(!RuntimeFiles.IsMutable("GameData/" + relative), "Copied setting filename not exempt under another root: " + setting);
            Agreement(fixture, false, "Other-root setting filename " + setting); File.Delete(Path.Combine(fixture.GameData, relative));
        }
    }
    private static void Refusal(string kind)
    {
        var fixture = new Fixture("refusal-" + kind, true); string request = fixture.Save();
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false);
        string path = Path.Combine(fixture.GameData, Settings[0]); FileStream held = null;
        try {
            if (kind == "locked") held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else if (kind == "oversized") using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)) stream.SetLength(4L * 1024 * 1024 + 1);
            else if (kind == "directory") { File.Delete(path); Directory.CreateDirectory(path); }
            else {
                string parent = Path.GetDirectoryName(path); string target = Path.Combine(fixture.Root, "owned-junction-target");
                Directory.Move(parent, target); Junction(parent, target);
            }
            var before = Snapshot(fixture.GameData, kind == "locked" ? path : null);
            Rejected(() => { using (NativeInstall.Run(fixture.Helper, request, "pack")) { } }, "Post-exit preservation refuses " + kind + " setting");
            Check(before.SequenceEqual(Snapshot(fixture.GameData, kind == "locked" ? path : null)), "Unsafe setting refused before any installed mutation: " + kind);
            string backup = Path.Combine(fixture.Root, "APF/.transaction-backup");
            Check(!Directory.Exists(backup) || Directory.GetFiles(backup, "recovery.json", SearchOption.AllDirectories).Length == 0, "Unsafe setting refused before recovery journal creation: " + kind);
            if (held != null) { held.Dispose(); held = null; fixture.AssertSettings("Locked setting bytes remain unchanged"); }
        } finally { held?.Dispose(); }
    }
    private static void MutableOnlyMissingRoot()
    {
        var fixture = new Fixture("mutable-only");
        var package = fixture.Transaction.Manifest.ManagedPackages.Single(item => item.Id == "fixture-connectedlivingspace");
        var state = fixture.Transaction.ExistingState.Packages.Single(item => item.Id == package.Id);
        foreach (var file in state.Files.Where(item => !IsSetting(item.Path))) File.Delete(Path.Combine(fixture.Root, file.Path));
        state.Files = new List<ApfFileState> { new ApfFileState { Path = "GameData/" + Settings[1], Sha256 = Hash(fixture.Expected[Settings[1]]) } };
        fixture.Transaction.Manifest.ProtectedRoots.AddRange(Settings.Where(path => RootOf(path) != "ConnectedLivingSpace").Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase));
        fixture.Transaction.Manifest.ManagedPackages = new List<ApfPackage> { package };
        fixture.Transaction.ExistingState.Packages = new List<ApfPackageState> { state };
        Put(fixture.Data, "APFPackState.json", ApfJson.Serialize(fixture.Transaction.ExistingState));
        Agreement(fixture, true, "Present mutable-only package");
        Directory.Move(Path.Combine(fixture.GameData, "ConnectedLivingSpace"), Path.Combine(fixture.Root, "owned-missing-root"));
        Agreement(fixture, false, "Missing root must not match two filtered empty inventories");
    }
    private static void Junction(string link, string target)
    {
        using (var child = Process.Start(new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = ApfProcessArguments.Join(new[] { "/c", "mklink", "/J", link, target }), UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        })) Check(child != null && child.WaitForExit(10000) && child.ExitCode == 0, "Owned junction safety fixture created");
    }
    private static string[] Snapshot(string root, string locked = null)
    {
        if (locked != null) locked = Path.GetFullPath(locked);
        var values = new List<string>(); if (!Directory.Exists(root)) return values.ToArray();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0) foreach (string path in Directory.GetFileSystemEntries(pending.Pop())) {
            string relative = path.Substring(root.Length + 1).Replace('\\', '/'); var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) values.Add("R:" + relative);
            else if ((attributes & FileAttributes.Directory) != 0) { values.Add("D:" + relative); pending.Push(path); }
            else if (string.Equals(Path.GetFullPath(path), locked, StringComparison.OrdinalIgnoreCase)) values.Add("LOCKED:" + relative);
            else values.Add(relative + ":" + ApfJson.Sha256File(path));
        }
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
    private static bool NoRollback(Fixture fixture)
    { string path = Path.Combine(fixture.Root, "APF/APFTransaction.log"); return File.Exists(path) && !File.ReadAllText(path).Contains("ROLLED-BACK"); }
    private static Dictionary<string, string> ReadReceipt(string root)
    { return ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "APF/LastInstallResult.json"))); }
    private static void AssertReceiptMac(string root, Dictionary<string, string> receipt)
    {
        string expected = (string)typeof(Installer).GetMethod("ReceiptAuthorization", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
            new object[] { receipt, ApfTransactionSecurity.LoadKey(Path.Combine(root, "GameData/LunaMultiplayer/Data/APFHelper.key")) });
        Check(receipt["status"] == "complete" && receipt["authorization"] == expected, "Actual completed receipt HMAC verifies");
    }
    private static void Restore(string root)
    { typeof(NativeInstall).Assembly.GetType("KesasInstall.PackageCasing", true).GetMethod("RestoreAtStartup").Invoke(null, new object[] { root, Version }); }

    private static void Migration()
    {
        bool fresh = mode.StartsWith("fresh", StringComparison.Ordinal);
        var fixture = new Fixture(mode, true);
        // Exercise the real original pack revision, not RC26 for every loaded helper.
        if (!fresh && (mode=="migration28" || mode=="migration29")) {
            fixture.Transaction.ExistingState.PackVersion="kesas-rc"+mode.Substring(mode.Length-2);
            Put(fixture.Data,"APFPackState.json",ApfJson.Serialize(fixture.Transaction.ExistingState));
        }
        if (fresh) {
            foreach (string setting in Settings.Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase)) {
                string target = Path.GetFullPath(Path.Combine(fixture.GameData, setting));
                if (!ApfPaths.IsUnder(target, fixture.GameData) || !target.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe owned fresh-fixture target.");
                Directory.Delete(target, true);
            }
            File.Delete(Path.Combine(fixture.Data, "APFPackState.json")); File.Delete(Path.Combine(fixture.Data, "KesasInstallProfile.json"));
            fixture.Transaction.ExistingState = new ApfPackState();
            Check(Settings.All(path => !Directory.Exists(Path.Combine(fixture.GameData, RootOf(path)))) && !File.Exists(fixture.Transaction.StatePath), "Fresh old starter has no previous pack state or settings package roots");
        }
        var signed = new Signed(fixture, false);
        foreach (string name in new[] { "LmpClient.dll", "ApfCore.dll" }) Copy(Path.Combine(baseline, "GameData/LunaMultiplayer/Plugins", name), Path.Combine(fixture.Helper, "Plugins", name));
        Copy(engine, Path.Combine(fixture.Helper, "Plugins/KesasInstall.dll"));
        if (mode == "migration27") {
            // The handoff is mixed: RC27 code/catalog, RC26 state/profile, failed pack receipt.
            Put(fixture.Helper, "Launcher/install-catalog.json", ApfJson.Serialize(new InstallCatalog { Version = "kesas-rc27", Full = fixture.Transaction.Manifest, Potato = fixture.Transaction.Manifest }));
            Put(fixture.Root, "APF/LastInstallResult.json", ApfJson.Serialize(new Dictionary<string, string> { { "status", "failed" }, { "kind", "pack" } }));
        }
        var update = new ApfClientUpdateTransaction { GameRoot = fixture.Root, GameDataPath = fixture.GameData, ProcessId = 0,
            KspExecutable = Path.Combine(fixture.Root, "KSP_x64.exe"), KspArguments = new List<string> { "--startup-stub" },
            ArchivePath = signed.Archive, Manifest = signed.Feed, Approval = ApfClientUpdateApproval.Capture(fixture.Root) };
        ApfTransactionSecurity.Sign(update, ApfTransactionSecurity.LoadKey(Path.Combine(fixture.Data, "APFHelper.key")));
        string request = fixture.Save(); File.WriteAllText(request + ".client", ApfJson.Serialize(update));
        Check(fixture.Transaction.Packages.Count == Settings.Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() && (fresh || fixture.Transaction.Packages.All(staged => {
            var previous = fixture.Transaction.ExistingState.Packages.Single(item => item.Id == staged.Package.Id);
            return previous.ArchiveSha256 == staged.Package.Sha256 && previous.Version != staged.Package.Version;
        })), fresh ? "Exactly eight first-install settings packages" : "Exactly eight version-only replacements, immutable archive hashes unchanged");
        PreparedPack.Prepare(fixture.Transaction, request, delegate { }, () => false); if (!fresh) fixture.LateSettings();
        var expectedHashes = fresh ? new Dictionary<string, string> { { Settings[1], Hash(fixture.Payloads[Settings[1]]) } } : fixture.Expected.ToDictionary(item => item.Key, item => Hash(item.Value));
        Put(fixture.Root, "expected-settings-hashes.json", ApfJson.Serialize(expectedHashes));
        if (fresh) Put(fixture.Root, "fresh-starter.enabled", "No previous pack roots or state");
        Put(fixture.Root, "expected-engine.sha256", ApfJson.Sha256File(nextEngine));
        if (!string.IsNullOrEmpty(legacyFaultCheckpoint)) Put(fixture.Root, "legacy-fault-checkpoint.txt", legacyFaultCheckpoint);
        using (var completion = NativeInstall.Run(fixture.Helper, request, "pack")) {
            Check(NoRollback(fixture), "Old helper commits combined signed upgrade without rollback");
            Check(mode.EndsWith("28") || mode.EndsWith("29") ? ReadReceipt(fixture.Root)["settingsPreservation"]=="prepared28" : !ReadReceipt(fixture.Root).ContainsKey("settingsPreservation"), "Actual old helper exposes its real preservation protocol"); AssertReceiptMac(fixture.Root, ReadReceipt(fixture.Root));
            for (int index = 0; index < Settings.Length; index++) {
                string path = Path.Combine(fixture.GameData, Settings[index]);
                bool preservedByOld=!fresh&&(mode.EndsWith("28")||mode.EndsWith("29"))&&index<8;
                Check(preservedByOld ? File.ReadAllBytes(path).SequenceEqual(fixture.Expected[Settings[index]]) : (index==1 ? File.ReadAllBytes(path).SequenceEqual(fixture.Payloads[Settings[index]]) : !File.Exists(path)), "Old helper real preservation/loss before startup recovery: " + Settings[index]);
            }
            var backups = Snapshot(Path.Combine(fixture.Root, "APF/.transaction-backup"));
            Check(backups.Length > 0, "Old helper retains real replacement recovery backups");
            File.WriteAllText(Path.Combine(fixture.Root, "expected-backup-snapshot.json"), ApfJson.Serialize(backups));
            Check(ApfJson.Sha256File(Path.Combine(fixture.Helper, "Plugins/KesasInstall.dll")) == ApfJson.Sha256File(nextEngine), "Old loaded helper installs exact signed new28 Engine");
            completion.Restart(); Check(completion.RestartedProcessId > 0, "One explicit production Completion.Restart launches headless startup stub");
            bool rejected = false; try { completion.Restart(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, "Second explicit restart rejected");
            WaitForStartup(fixture.Root, completion.RestartedProcessId);
            Check(true, "Single restarted headless stub completes recovery");
        }
        string result = Path.Combine(fixture.Root, "startup-result.json");
        Check(File.Exists(result) && File.ReadAllText(result) == "passed", "New28 authenticated startup restores all selected settings");
        var recovered = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(fixture.Root, "recovered-settings-hashes.json")));
        Check(expectedHashes.Count == recovered.Count && expectedHashes.All(item => recovered.ContainsKey(item.Key) && recovered[item.Key] == item.Value), "One-restart recovery proof matches original bytes/defaults, not a fabricated repair");
        foreach (string setting in Settings) fixture.Expected[setting] = UserBytes(setting, "new-user-edit-after-recovery");
        fixture.AssertSettings("Completed recovery preserves later user edits");
        Check(File.ReadAllLines(Path.Combine(fixture.Root, "startup-launch.txt")).Length == 1, "Exactly one game launch, no hidden restart");
        Check(NoRollback(fixture), "Migration does not enter repeated rollback");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StartupStub()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string launch = Path.Combine(root, "startup-launch.txt"); File.AppendAllText(launch, "startup\n");
        try {
            if (File.ReadAllLines(launch).Length != 1) throw new InvalidDataException("Unexpected second game launch.");
            Check(ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location) == File.ReadAllText(Path.Combine(root, "expected-engine.sha256")), "Restart resolves exact new28 Engine, not already-loaded old helper");
            var hashes = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "expected-settings-hashes.json")));
            string gameData = Path.Combine(root, "GameData");
            RestoreWithCheckpoint(root);
            foreach (var item in hashes) Check(ApfJson.Sha256File(Path.Combine(gameData, item.Key)) == item.Value, "Authenticated startup restores exact setting: " + item.Key);
            if (File.Exists(Path.Combine(root, "fresh-starter.enabled"))) foreach (string setting in Settings.Where(path => path != Settings[1])) Check(!File.Exists(Path.Combine(gameData, setting)), "Fresh startup does not invent missing original settings: " + setting);
            var recovered = Settings.Where(path => File.Exists(Path.Combine(gameData, path))).ToDictionary(path => path, path => ApfJson.Sha256File(Path.Combine(gameData, path)));
            File.WriteAllText(Path.Combine(root, "recovered-settings-hashes.json"), ApfJson.Serialize(recovered));
            string marker = Path.Combine(root, "APF/LegacySettingsRecovery-rc30.json");
            var envelope = ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(marker));
            using (var hmac = new HMACSHA256(ApfTransactionSecurity.LoadKey(Path.Combine(gameData, "LunaMultiplayer/Data/APFHelper.key"))))
                Check(envelope.Mac == Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(envelope.Payload))), "Durable legacy recovery marker HMAC verifies");
            Check(ApfJson.Deserialize<Dictionary<string, object>>(envelope.Payload)["status"].ToString() == "complete", "Durable legacy recovery marker is complete");
            string markerHash = ApfJson.Sha256File(marker);
            var catalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(gameData, "LunaMultiplayer/Launcher/install-catalog.json")));
            var state = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(gameData, "LunaMultiplayer/Data/APFPackState.json")));
            Check(RuntimeFiles.ScanFinal(gameData, catalog.Full, state).IsClean, "Actual new28 final scanner accepts recovered settings");
            string[] installed = Snapshot(gameData); Restore(root);
            Check(installed.SequenceEqual(Snapshot(gameData)), "Repeated authenticated startup is idempotent");
            foreach (string path in Settings) Put(gameData, path, UserBytes(path, "new-user-edit-after-recovery"));
            string[] edited = Snapshot(gameData); Restore(root);
            Check(edited.SequenceEqual(Snapshot(gameData)), "Durable recovery marker never replays stale settings over later user edits");
            Check(ApfJson.Sha256File(marker) == markerHash, "Repeated startups do not rewrite completed recovery marker");
            var backupExpected = ApfJson.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "expected-backup-snapshot.json")));
            Check(backupExpected.SequenceEqual(Snapshot(Path.Combine(root, "APF/.transaction-backup"))), "Recovery backups remain byte-identical after startup/repeats");
            File.WriteAllText(Path.Combine(root, "startup-proof.json"), ApfJson.Serialize(new { runtime = RuntimeName(), unityGameplayTested = false, checks, settings = hashes.Count,
                startupCalls = File.Exists(Path.Combine(root, "legacy-fault-checkpoint.txt")) ? 4 : 3 }));
            Publish(root, "passed"); return 0;
        } catch (Exception error) { PublishStartupFailure(root, error); return 1; }
    }
    private static void RestoreWithCheckpoint(string root)
    {
        string configuration = Path.Combine(root, "legacy-fault-checkpoint.txt");
        if (!File.Exists(configuration)) { Restore(root); return; }
        string checkpoint = File.ReadAllText(configuration);
        Check(new[] { "plan-saved", "file-written:0", "file-recorded:0", "file-written:8", "file-recorded:8" }.Contains(checkpoint, StringComparer.Ordinal), "Supported real recovery checkpoint selected");
        var recovery = typeof(NativeInstall).Assembly.GetType("KesasInstall.LegacySettingsRecovery", true);
        var field = recovery.GetField("TestCheckpoint", BindingFlags.NonPublic | BindingFlags.Static);
        Check(field != null && field.GetValue(null) == null, "Production recovery checkpoint hook exists and starts clear");
        string[] backups = Snapshot(Path.Combine(root, "APF/.transaction-backup"));
        var interruption = new IOException("Synthetic interruption at legacy recovery checkpoint " + checkpoint);
        bool reached = false, observed = false;
        Action<string> callback = value => { if (value == checkpoint) { reached = true; throw interruption; } };
        field.SetValue(null, callback);
        try { Restore(root); }
        catch (Exception error) { if (!ReferenceEquals(Unwrap(error), interruption)) throw; observed = true; }
        finally { field.SetValue(null, null); }
        Check(reached && observed, "Actual authenticated recovery reaches and throws at " + checkpoint);
        Check(backups.SequenceEqual(Snapshot(Path.Combine(root, "APF/.transaction-backup"))), "Interrupted recovery leaves original backups byte-identical");
        string marker = Path.Combine(root, "APF/LegacySettingsRecovery-rc30.json");
        var envelope = ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(marker));
        using (var hmac = new HMACSHA256(ApfTransactionSecurity.LoadKey(Path.Combine(root, "GameData/LunaMultiplayer/Data/APFHelper.key"))))
            Check(envelope.Mac == Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(envelope.Payload))), "Interrupted durable recovery marker HMAC verifies");
        Check(ApfJson.Deserialize<Dictionary<string, object>>(envelope.Payload)["status"].ToString() == "pending", "Interrupted recovery retains authenticated pending progress");
        // The only resume is the production startup path, in this same child.
        // Do not edit a receipt, marker, package state, backup, or user setting.
        Restore(root);
        Check(File.ReadAllLines(Path.Combine(root, "startup-launch.txt")).Length == 1, "Checkpoint resumes in same startup without a second game launch");
        File.WriteAllText(Path.Combine(root, "startup-fault-proof.json"), ApfJson.Serialize(new {
            checkpoint, reached, observed, sameStartup = true, runtime = RuntimeName(), recoveryCalls = 2, unityGameplayTested = false
        }));
    }
    private static void WaitForStartup(string root, int pid)
    {
        // Do not reopen by PID and inspect ExitCode: that can race process exit,
        // differs between runtimes, and can mask the child's real exception.
        string outcome = Path.Combine(root, "startup-result.json");
        string failure = Path.Combine(root, "startup-failure.txt");
        File.WriteAllText(Path.Combine(output, "startup-child.json"), ApfJson.Serialize(new {
            processId = pid, fixtureRoot = root, outcomePath = outcome, failurePath = failure
        }));
        var clock = Stopwatch.StartNew(); string result = null;
        while (clock.ElapsedMilliseconds < 45000) {
            result = ReadStartupText(outcome);
            if (result != null) break;
            System.Threading.Thread.Sleep(20);
        }
        // One final read closes the deadline/publication race.
        if (result == null) result = ReadStartupText(outcome);
        if (result == "passed") return;
        string detail = ReadStartupText(failure);
        string first = detail == null ? "No startup exception file was published." : detail.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Empty startup exception file.";
        string status = result == null ? "timed out after 45000 ms" : "reported " + result;
        throw new InvalidDataException("Headless startup " + status + " (PID " + pid + "). Evidence: " + failure + ". " + first);
    }
    private static string ReadStartupText(string path)
    {
        try {
            // The child renames a closed temporary file. Transient sharing or
            // observation races are retried without treating them as game errors.
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(input)) return reader.ReadToEnd();
        } catch (IOException) { return null; }
    }
    private static void PublishStartupFailure(string root, Exception error)
    {
        try { File.WriteAllText(Path.Combine(root, "startup-failure.txt"), error.ToString()); }
        catch (Exception captureError) { Console.Error.WriteLine("Could not preserve startup exception: " + captureError.Message); }
        try { Publish(root, "failed"); }
        catch (Exception publishError) { Console.Error.WriteLine("Could not publish startup outcome: " + publishError.Message); }
    }
    private static void Publish(string root, string result)
    { string path = Path.Combine(root, "startup-result.tmp"); File.WriteAllText(path, result); File.Move(path, Path.Combine(root, "startup-result.json")); }
}
