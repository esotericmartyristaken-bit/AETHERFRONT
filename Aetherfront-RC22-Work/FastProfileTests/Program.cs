using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private const string Version = "kesas-rc22";
    private const string Repository = "esotericmartyristaken-bit/AETHERFRONT";
    private const string Endpoint = "217.216.49.250:18865";
    private const string CatalogPath = "GameData/LunaMultiplayer/Launcher/install-catalog.json";
    private const string DataPath = "GameData/LunaMultiplayer/Data/";
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly Uri Origin = new Uri("https://217.216.49.250:18866/");
    private static readonly object LogGate = new object();
    private static readonly object ProgressGate = new object();
    private static readonly Dictionary<string, Dictionary<string, FileProof>> Inventories =
        new Dictionary<string, Dictionary<string, FileProof>>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Preserved = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> ArchiveHashes = new Dictionary<string, string>();
    private static readonly FieldInfo GitHubReady = typeof(Installer).GetField("githubReady", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly string InitialSettings = "QUALITY_PRESET = 3\nANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nSYNC_VBL = 1\nLIGHT_QUALITY = 8\nSHADOWS_QUALITY = 3\nFRAMERATE_LIMIT = 120\nCELESTIAL_BODIES_CAST_SHADOWS = True\nPLANET_SCATTER = True\nPLANET_SCATTER_FACTOR = 1\nAERO_FX_QUALITY = 2\nSURFACE_FX = True\nREFLECTION_PROBE_REFRESH_MODE = 1\nREFLECTION_PROBE_TEXTURE_RESOLUTION = 1\nTERRAIN_SHADER_QUALITY = 2\nKEY_BINDING = live-profile-test-preserve\nSOUND_VOLUME = 0.37\n";
    private const int BufferBytes = 1024 * 1024;
    private const string FixtureName = "LiveProfileFixture-8ae6532fec3643778f37c0288970177b";
    private const string StarterSha256 = "10de444c12e8708b74fbdb35fc6ba4fb7da9520f42b8762d312a7b6badc84b6b";
    private const string FrozenFeedSha256 = "d187de805ecac00f6a0bbbf7921cb708b6fe83c1463119ccdd120dfcf47d8ee6";
    private const string ClientSha256 = "2580f1c30480c0d44ee4a051d3078b6f2c9fb90b7a7429b2a23cefb84cb37ee9";
    private const string ProductionPin = "34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09";
    private const string CompletedTransaction = "APF/.staging/connect/5e74e081ffc241938368e6182e92389a/transaction.json";
    private static readonly Dictionary<string, string> OriginalEvidence = new Dictionary<string, string>();
    private static readonly MethodInfo ResolveZipEntry = typeof(ApfArchiveInstaller).GetMethod("ResolveEntryPath", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo ZipPathsCollide = typeof(ApfArchiveInstaller).GetMethod("PathsCollide", BindingFlags.Static | BindingFlags.NonPublic);
    [ThreadStatic] private static byte[] hashBuffer;
    private static string work, root, evidence, originalNativeLog, phase = "startup", stubHash;
    private static StreamWriter log;
    private static InstallCatalog catalog;
    private static ApfClientUpdateManifest feed;
    private static HashSet<string> pins, starterRoots;
    private static ProgressInfo progress;
    private static TransportAudit transport;

    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--resume-fixture" || args[2] != "--run-production" || args[3] != "--ack-headless")
        {
            Console.Error.WriteLine("NOT RUN. Reviewed continuation only; never stop the original harness here.\n" +
                "From the APF workspace: Aetherfront-RC22-Work\\FastProfileTests\\bin\\Release\\net472\\FastProfileTests.exe " +
                "--resume-fixture \".\\Aetherfront-RC22-Work\\" + FixtureName + "\" --run-production --ack-headless\n" +
                "Requires the original harness to be stopped externally only after review and quiescence. Existing evidence is retained. HEADLESS placeholders only.");

            return 2;
        }
        using (var mutex = new Mutex(false, "Local\\Aetherfront-RC22-LiveProfileTests"))
        {
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                Require(owned, "Another live profile harness is active; refusing duplicate large downloads.");
                using (var current = Process.GetCurrentProcess()) current.PriorityClass = ProcessPriorityClass.BelowNormal;
                Run(args[1]);
                Say("PASS HEADLESS remaining live-profile assertions only; original cancellation/fresh-Potato transport evidence remains original. Unity scenes/rendering/gameplay NOT verified.");
                return 0;
            }
            catch (Exception error)
            {
                Say("FAIL " + error);
                return 1;
            }
            finally
            {
                if (root != null) Say("Fixture retained; no automatic cleanup: " + root);
                lock (LogGate) { log?.Dispose(); log = null; }
                if (owned) mutex.ReleaseMutex();
            }
        }
    }

    private static void Run(string requestedRoot)
    {
        work = Path.GetFullPath("Aetherfront-RC22-Work");
        Require(Directory.Exists(Path.Combine(work, "LiveProfileTests")), "Run from the APF workspace root.");
        Require(new DriveInfo(Path.GetPathRoot(work)).DriveType == DriveType.Fixed, "Local fixed disk required.");
        Require(Path.IsPathRooted(requestedRoot) && !requestedRoot.StartsWith(@"\\", StringComparison.Ordinal),
            "An explicit absolute local fixture path is required.");
        string expectedRoot = Path.Combine(work, FixtureName);
        Require(string.Equals(requestedRoot, expectedRoot, StringComparison.OrdinalIgnoreCase),
            "Only the exact original GUID fixture path is accepted; no aliases, trailing separators, UNC, or relative paths.");
        root = expectedRoot;
        ApfPaths.EnsureNoReparsePoints(Path.GetPathRoot(work), root);
        Require(Directory.Exists(root) && File.Exists(Owned(".live-profile-owned")) &&
            new FileInfo(Owned(".live-profile-owned")).Length == 0, "Exact owned fixture marker required.");
        EnsureIdle();
        ReadOriginalCheckpoint();
        Require(GitHubReady != null && ResolveZipEntry != null && ZipPathsCollide != null, "Exact shipped diagnostic/safety methods required.");
        var archiveType = typeof(ApfArchiveInstaller);
        // Historical Cecil patches changed inlined limits, not stale const-field metadata.
        byte[] validationIl=archiveType.GetMethod("ValidateArchive").GetMethodBody().GetILAsByteArray();
        Require(Equal(HashFile(archiveType.Assembly.Location),"bd036b5e176cf465d30b8275e50bafd37552814a6917fdc13efb26c97aead99d") &&
            Equal(Hash(validationIl),"18dbaf787cbe6bb485396e46574dc116408698934f819b0ad656d44efd8c6622") &&
            validationIl[267]==0x20 && BitConverter.ToInt32(validationIl,268)==100000 &&
            validationIl[327]==0x21 && BitConverter.ToInt64(validationIl,328)==32*GiB,
            "Frozen shipped archive validator implementation/limits changed.");

        string starter = Path.GetFullPath("client-artifacts/Aetherfront-RC22/Aetherfront-Starter-rc22.zip");
        ApfPaths.EnsureNoReparsePoints(Path.GetPathRoot(work), starter);
        Require(Equal(HashFile(starter), StarterSha256), "Frozen public RC22 starter SHA-256 mismatch.");
        Require(Equal(File.ReadAllText(starter + ".sha256").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0], StarterSha256),
            "Public starter sidecar differs from frozen RC22.");
        using (var zip = OpenZip(starter))
        {
            catalog = ApfJson.Deserialize<InstallCatalog>(ReadEntry(zip, CatalogPath));
            ValidateCatalog();
            Require(ReadEntry(zip, DataPath + "APFServer.txt").Trim() == Endpoint, "Starter endpoint mismatch.");
            Require(ReadEntry(zip, CatalogPath) == File.ReadAllText(Owned(CatalogPath)), "Installed catalog differs from frozen shipped catalog.");
            foreach (string name in new[] { "APFServer.txt", "APFTlsCertificatePins.txt", "APFClientUpdatePublicKey.xml" })
                Require(ReadEntry(zip, DataPath + name) == File.ReadAllText(Owned(DataPath + name)), "Shipped trust/endpoint changed: " + name);
            foreach (var assembly in new[] { typeof(Installer).Assembly, typeof(ApfJson).Assembly, typeof(JsonFx.Json.JsonReader).Assembly })
            {
                string name = Path.GetFileName(assembly.Location);
                using (var input = Entry(zip, "GameData/LunaMultiplayer/Plugins/" + name).Open())
                    Require(Equal(Hash(input), HashFile(assembly.Location)), "Loaded assembly is not exact shipped RC22: " + name);
            }
            starterRoots = new HashSet<string>(zip.Entries.Where(e => e.FullName.StartsWith("GameData/", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName.Substring(9).Split('/')[0]).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var all = UnionPackages();
            long expanded = zip.Entries.Sum(e => e.Length);
            SpaceGuard(checked(all.Sum(p => p.SizeBytes) + 2 * all.Sum(p => catalog.Sizes.Single(s => Equal(s.Id, p.Id)).ExpandedBytes)
                + all.Max(p => p.SizeBytes) + expanded + 2 * GiB), "complete retained fixture budget");
        }
        pins = new HashSet<string>(File.ReadAllLines(Owned(DataPath + "APFTlsCertificatePins.txt")).Select(s => s.Trim()).Where(s => s.Length != 0), StringComparer.OrdinalIgnoreCase);
        Require(pins.Count > 0 && pins.Count <= 8 && pins.All(IsHash) && pins.SetEquals(new[] { ProductionPin }), "Frozen production TLS pin required.");
        Require(Equal(HashFile(Owned("evidence/initial-signed-feed.json")), FrozenFeedSha256), "Frozen original signed feed changed.");
        feed = ReadSignedFeed(File.ReadAllText(Owned("evidence/initial-signed-feed.json")));
        VerifySignedClient();
        Require(ReadSignedFeed(File.ReadAllText(Owned("APF/.client-update-staging/connect/feed.json"))).ToJson() == feed.ToJson() &&
            ReadSignedFeed(File.ReadAllText(Owned("APF/.client-update-staging/connect/github-feed.json"))).ToJson() == feed.ToJson(),
            "Existing signed origin/GitHub feeds differ from original frozen release.");
        var server = ApfPackManifest.FromJson(File.ReadAllText(Owned("APF/.client-update-staging/connect/server-pack.json")));
        Installer.ValidateIdentity(server, catalog.Full, Version);
        Installer.ValidateIdentity(server, catalog.Potato, Version);

        var potatoIds = new HashSet<string>(catalog.Potato.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var fullIds = new HashSet<string>(catalog.Full.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var fullDelta = new HashSet<string>(fullIds.Except(potatoIds), StringComparer.OrdinalIgnoreCase);
        var potatoDelta = new HashSet<string>(potatoIds.Except(fullIds), StringComparer.OrdinalIgnoreCase);
        Require(potatoIds.Count == 176 && fullIds.Count == 210 && UnionPackages().Count == 217 &&
            fullDelta.Count == 41 && potatoDelta.Count == 7, "Frozen RC22 package/delta counts changed.");
        ValidateResumeMetadata();
        ValidateOriginalRoutes();
        string stub = Path.Combine(work, "GameStub/bin/Release/net472/GameStub.exe");
        ApfPaths.EnsureNoReparsePoints(work, stub);
        Require(File.Exists(stub), "Existing GameStub required; no game/runtime build or replacement is permitted.");
        stubHash = HashFile(stub);
        Require(Equal(HashFile(Owned("KSP_x64.exe")), stubHash) && new FileInfo(Owned("KSP_x64.exe")).Length == 4608,
            "Fixture relaunch target is not original GameStub.");
        Require(Directory.Exists(Owned("GameData/Squad")) && Directory.GetFileSystemEntries(Owned("GameData/Squad")).Length == 0,
            "Only the empty Squad placeholder is allowed.");
        Require(File.ReadAllText(Owned(DataPath + "KesasInstallSelection.txt")) == "potato\n", "Saved dropdown must remain potato.");
        Require(File.ReadAllText(Owned(DataPath + "KESASGraphicsBeforePotato.cfg")).Replace("\r", "") == InitialSettings,
            "Original known-constant graphics backup changed.");
        string identity = File.ReadAllText(Owned(DataPath + "privatekey.txt"));
        string save = File.ReadAllText(Owned("saves/LiveProfileTest/persistent.sfs"));
        Guid placeholderId;
        Require(identity.StartsWith("headless-test-identity-", StringComparison.Ordinal) && identity.EndsWith("\n", StringComparison.Ordinal) &&
            Guid.TryParseExact(identity.Substring(23).TrimEnd('\n'), "N", out placeholderId), "Original placeholder identity shape required.");
        const string savePrefix = "// HEADLESS TEST PLACEHOLDER, not a playable KSP save\n";
        Require(save.StartsWith(savePrefix, StringComparison.Ordinal) && Guid.TryParseExact(save.Substring(savePrefix.Length), "N", out placeholderId),
            "Original headless save placeholder required.");

        Remember("LiveProfileTests.log");
        foreach (string path in SafeFiles(Owned("evidence")))
            OriginalEvidence.Add(Relative(path), HashFile(path));
        foreach (string name in new[] { "APFServer.txt", "APFTlsCertificatePins.txt", "APFClientUpdatePublicKey.xml",
            "privatekey.txt", "KesasInstallSelection.txt", "APFHelper.key" })
            Remember(DataPath + name);
        Remember("saves/LiveProfileTest/persistent.sfs");
        evidence = "evidence/fast-continuation-" + Guid.NewGuid().ToString("N") + "/";
        Require(!Directory.Exists(Owned(evidence.TrimEnd('/'))) && !File.Exists(Owned(evidence.TrimEnd('/'))), "New evidence directory collision.");
        Directory.CreateDirectory(Owned(evidence.TrimEnd('/')));
        log = new StreamWriter(NewFile(evidence + "FastProfileTests.log")) { AutoFlush = true };
        Say("Continuation fixture: " + root);
        Say("HEADLESS ONLY: existing GameStub/empty Squad; no real KSP is installed or launched.");
        Say("Original completed fresh-Potato preservation/cache checkpoint matched before re-establishing current identity/save/trust baselines. These new baselines prove only subsequent preservation, not earlier preservation.");
        Say("Fresh cancellation, 32848512-byte range reuse, and fresh-Potato GitHub/Potato proof remain in original logs; not re-run or relabeled.");
        Say("Independent Windows SHA256Cng; 1048576-byte sequential file/ZIP reads. Actual shipped Installer.Run and native helper invocation/environment are unchanged.");
        WriteNew(evidence + "original-evidence-hashes.json", ApfJson.Serialize(OriginalEvidence));
        WriteNew(evidence + "continuation-baselines.json", ApfJson.Serialize(Preserved));
        WriteNew(evidence + "baseline-scope.txt", "Current-file baselines were re-established only after matching the original completed fresh-Potato preservation/cache checkpoint. They do not independently prove preservation before this continuation. Original cancellation/range/fresh transport proof remains in original evidence.");
        using (var heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)))
        {
            foreach (var package in catalog.Potato.ManagedPackages) VerifyArchive(package, ArchivePath(package));
            Require(Inventories.Values.Sum(i => i.Count) == 28674 && Inventories.Values.Sum(i => i.Values.Sum(f => f.Size)) == 11211717503L,
                "Original fresh-Potato independent ZIP inventory totals differ.");
            ValidateInstalled("potato", "resume-baseline");
            Require(CacheSnapshot().Count == 176, "Initial verified cache must contain exactly 176 original Potato ZIPs.");
            AssertOriginalEvidence();
            EnsureIdle();
            Require(ServicePointManager.ServerCertificateValidationCallback == null, "Unexpected global TLS override; refusing execution.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            transport = new TransportAudit(catalog.GitHub);
            Require(WebRequest.RegisterPrefix("https://", transport), "Cannot observe actual HTTPS request creation.");
            Require(WebRequest.RegisterPrefix("http://", new RefuseHttp()), "Cannot reject plaintext transport.");
            Preflight("continuation-initial");
            Reconnect("potato", "potato-reconnect");
            string sharedBefore = SharedSnapshot();
            Transition("full", fullDelta, "potato-to-full");
            Require(SharedSnapshot() == sharedBefore, "Shared package content/state changed during the full switch.");
            Reconnect("full", "full-reconnect");
            Transition("potato", potatoDelta, "full-to-potato");
            Require(SharedSnapshot() == sharedBefore, "Shared package content/state changed on return to potato.");
            Reconnect("potato", "final-potato-reconnect");
            Preflight("final");
            CheckPreserved();
            AssertOriginalEvidence();
            transport.WriteSummary("final");
        }
    }

    private static void EnsureIdle()
    {
        using (var current = Process.GetCurrentProcess())
        {
            foreach (string name in new[] { "LiveProfileTests", "FastProfileTests" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) Require(process.Id == current.Id, "Another live/fast harness is active; never stop it here.");
        }
        using (var search = new ManagementObjectSearcher("SELECT Name, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe' OR Name='KSP_x64.exe'"))
        using (var processes = search.Get())
            foreach (ManagementObject process in processes)
                using (process)
                {
                    string command = process["CommandLine"] as string ?? "";
                    string executable = process["ExecutablePath"] as string ?? "";
                    string name = process["Name"] as string ?? "";
                    Require(name != "KSP_x64.exe" || !SamePath(executable, Owned("KSP_x64.exe")), "Fixture placeholder/game process is active.");
                    Require(command.IndexOf("APFApplyUpdate", StringComparison.OrdinalIgnoreCase) < 0 &&
                        command.IndexOf(".helper-runtime", StringComparison.OrdinalIgnoreCase) < 0,
                        "Native helper is active or cannot be distinguished safely.");
                }
        foreach (string name in new[] { ".installer.lock", "download.lock" })
        {
            string path = Owned("APF/" + name);
            Require(File.Exists(path) && new FileInfo(path).Length == 0, "Expected original idle lock marker missing/changed.");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        Require(!File.Exists(Owned("APF/LastDownloadWarning.log")) && !File.Exists(Owned("APF/LastDownloadError.log")),
            "Warning/error log present; no fallback or ambiguous provenance is accepted.");
    }

    private static string[] OriginalLogLines()
    {
        string path = Owned("LiveProfileTests.log");
        Require(new FileInfo(path).Length <= 8 * 1024 * 1024, "Original log unexpectedly large.");
        return File.ReadAllLines(path);
    }

    private static DateTime Checkpoint(string[] lines, string message)
    {
        var found = lines.Where(line => line.EndsWith(" " + message, StringComparison.Ordinal)).ToArray();
        Require(found.Length == 1, "Missing/ambiguous original checkpoint: " + message);
        return DateTime.Parse(found[0].Substring(0, found[0].IndexOf(' ')), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static void ReadOriginalCheckpoint()
    {
        var lines = OriginalLogLines();
        Checkpoint(lines, "Fixture: " + root);
        DateTime initial = Checkpoint(lines, "PASS initial: live signed RC22 feeds agree; exact shipped client/catalog/helper verified; pinned production identity matches.");
        DateTime interrupted = Checkpoint(lines, "PASS real GitHub interruption: observed=34071746 bytes; retained=32848512 bytes; installed folders unchanged. New Installer must prove range reuse and final SHA-256.");
        DateTime resumed = Checkpoint(lines, "PASS GitHub range resume: kesas-000-kspburst-de64ef39; reused=32848512 bytes; actual Range headers exclude cached bytes; complete signed-catalog SHA-256/size verified.");
        DateTime inventory = Checkpoint(lines, "PASS fresh-potato: 176 packages; 28674 files; 11211717503 bytes; exact profile roots, no unexpected visual mods.");
        DateTime preserved = Checkpoint(lines, "PASS fresh-potato: actual signed-catalog delta=176; native helper; all installed file hashes/sizes/profile verified.");
        Require(preserved.ToUniversalTime() == DateTime.Parse("2026-10-17T02:52:40.2035664Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) &&
            initial < interrupted && interrupted < resumed && resumed < inventory && inventory < preserved, "Original checkpoint order/time changed.");
        Require(!lines.Any(line => Regex.IsMatch(line, @"\b(FAIL|WARNING|WARN|ERROR|ROLLED-BACK)\b") ||
            line.IndexOf("potato-to-full", StringComparison.Ordinal) >= 0 || line.IndexOf("full-to-potato", StringComparison.Ordinal) >= 0),
            "Original log has failure, warning/error, or a later mutation phase.");
        string cancellation = File.ReadAllText(Owned("evidence/interrupted-expected-cancellation.log"));
        Require(cancellation.Contains("OperationCanceledException"), "Original expected cancellation diagnostic missing.");
        foreach (string name in new[] { "fresh-potato-helper.stdout.log", "fresh-potato-helper.stderr.log" })
            Require(File.Exists(Owned("evidence/" + name)) && new FileInfo(Owned("evidence/" + name)).Length == 0, "Original native output not clean.");
        originalNativeLog = File.ReadAllText(Owned("APF/APFTransaction.log"));
        var native = originalNativeLog.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Require(native.Length == 3 && native[0].EndsWith(" START pack=kesas version=kesas-rc22", StringComparison.Ordinal) &&
            native[1].EndsWith(" COMPLETED packages=176 deletes=0", StringComparison.Ordinal) &&
            native[1].StartsWith("2026-10-17T02:37:57.3284640Z ", StringComparison.Ordinal) &&
            native[2].Contains(" RESTORE-AVAILABLE "), "Exactly one completed original fresh native transaction required.");
        DateTime started = DateTime.Parse(native[0].Split(' ')[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTime completed = DateTime.Parse(native[1].Split(' ')[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Require(resumed < started && started < completed && completed < inventory, "Original native/fresh checkpoint order invalid.");
    }

    private static void ValidateResumeMetadata()
    {
        Require(Directory.GetFileSystemEntries(Owned("APF/.staging")).Length == 1 &&
            Directory.Exists(Owned("APF/.staging/connect")), "Active/extra staging transaction detected.");
        var cache = Directory.GetFileSystemEntries(Owned("APF/.staging/connect"));
        var expected = new HashSet<string>(catalog.Potato.ManagedPackages.Select(ArchivePath), StringComparer.OrdinalIgnoreCase);
        expected.Add(Owned(Path.GetDirectoryName(CompletedTransaction).Replace('\\','/')));
        Require(cache.Length == 177 && expected.SetEquals(cache), "Only 176 original Potato ZIPs and the exact completed transaction directory are allowed.");
        string consumedPath=Owned(CompletedTransaction+".consumed"),readyPath=Owned(CompletedTransaction+".ready");
        Require(!File.Exists(Owned(CompletedTransaction)) && new FileInfo(consumedPath).Length<=16L*1024*1024 &&
            new HashSet<string>(Directory.GetFileSystemEntries(Path.GetDirectoryName(consumedPath)),StringComparer.OrdinalIgnoreCase)
                .SetEquals(new[]{consumedPath,readyPath}) && File.ReadAllBytes(readyPath).SequenceEqual(new byte[]{1}),
            "Completed transaction markers are missing, active, or ambiguous.");
        var consumed=ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(consumedPath));
        var authorizationKey=ApfTransactionSecurity.LoadKey(Owned(DataPath+"APFHelper.key"));
        Require(ApfTransactionSecurity.Verify(consumed,authorizationKey) && consumed.ProcessId==0 && consumed.KspArguments.Count==0 &&
            IsHash(consumed.Authorization),"Completed transaction authorization/placeholder process mismatch.");
        string receipt=Owned("APF/.used-authorizations/"+consumed.Authorization+".receipt");
        Require(new HashSet<string>(Directory.GetFileSystemEntries(Owned("APF/.used-authorizations")),StringComparer.OrdinalIgnoreCase)
            .SetEquals(new[]{receipt}) && File.ReadAllBytes(receipt).SequenceEqual(new byte[]{1}),
            "Original completed transaction has no unique consumed-authorization receipt.");
        foreach (var package in catalog.Potato.ManagedPackages)
            Require(new FileInfo(ArchivePath(package)).Length == package.SizeBytes, "Original cache size mismatch: " + package.Id);
        var currentState = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(DataPath + "APFPackState.json")));
        var checkpointState = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned("evidence/fresh-potato-state.json")));
        Require(ApfJson.Serialize(currentState) == ApfJson.Serialize(checkpointState), "Current state differs from original completed fresh-Potato checkpoint.");
        var profile = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Owned(DataPath + "KesasInstallProfile.json")));
        Require(profile.Count == 2 && profile["mode"] == "potato" && profile["catalogVersion"] == Version, "Only original completed Potato profile is accepted.");
        var transaction = ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(Owned("evidence/fresh-potato-original-transaction.json")));
        Require(ApfTransactionSecurity.Verify(transaction, ApfTransactionSecurity.LoadKey(Owned(DataPath + "APFHelper.key"))),
            "Original fresh transaction authorization mismatch.");
        ValidateTransaction(transaction, catalog.Potato);
        Require(transaction.Packages.Count == 176 && transaction.Deletes.Count == 0 &&
            new HashSet<string>(transaction.Packages.Select(p => p.Package.Id), StringComparer.OrdinalIgnoreCase).SetEquals(catalog.Potato.ManagedPackages.Select(p => p.Id)) &&
            transaction.Packages.All(p => SamePath(p.ArchivePath, ArchivePath(p.Package))), "Original fresh transaction inventory changed.");
        transaction.ProcessId=0;transaction.KspArguments=new List<string>();ApfTransactionSecurity.Sign(transaction,authorizationKey);
        Require(ApfJson.Serialize(transaction)==ApfJson.Serialize(consumed),"Consumed transaction differs from the authenticated original fresh transaction.");
        var journals = SafeFiles(Owned("APF/.transaction-backup")).Where(p => Path.GetFileName(p) == "recovery.json").ToArray();
        Require(journals.Length == 1, "Exactly one original recovery journal required; no later/unfinished transaction.");
        var journal = ApfJson.Deserialize<ApfRecoveryJournal>(File.ReadAllText(journals[0]));
        Require(SamePath(journal.GameRoot, root) && journal.Kind == "pack" && journal.Status == "complete" &&
            journal.Records.Count == 177 && journal.Records.All(r => !r.Existed && !r.Restored && string.IsNullOrEmpty(r.Backup)),
            "Original fresh recovery journal is active, rolled back, or ambiguous.");
        var targets = new HashSet<string>(catalog.Potato.ManagedPackages.Select(p => Owned("GameData/" + ApfPaths.PackageRoot(p))), StringComparer.OrdinalIgnoreCase);
        targets.Add(Owned(DataPath + "APFPackState.json"));
        Require(targets.SetEquals(journal.Records.Select(r => r.Target)), "Original recovery targets changed.");
        Require(originalNativeLog.Contains(" RESTORE-AVAILABLE " + journals[0]), "Native completion/recovery journal mismatch.");
        foreach (string path in SafeFiles(Owned("APF")))
            Require(SamePath(path,readyPath) || SamePath(path,consumedPath) || !Regex.IsMatch(Path.GetFileName(path), @"(?i)(transaction.*\.(json|ready)|\.ready$|\.failed$)"),
                "Active/leftover transaction marker detected: " + Relative(path));
    }

    private static void ValidateOriginalRoutes()
    {
        var lines = OriginalLogLines();
        string transportLog = File.ReadAllText(Owned("evidence/fresh-potato-transport.log"));
        Require(!Regex.IsMatch(transportLog, @"uri=https://217\.216\.49\.250:18866/[^\s?]*\.zip"), "Original origin archive fallback detected.");
        Require(transportLog.Contains("uri=https://release-assets.githubusercontent.com/") && transportLog.Contains("response=True"),
            "Original GitHub CDN response evidence missing.");
        foreach (var package in catalog.Potato.ManagedPackages)
        {
            string asset = Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath);
            string suffix = "ROUTE archive=" + asset + " source=GitHub response=true originArchiveRequests=0 githubReady=true fallbackWarnings=0 sha256=" + package.Sha256 + " bytes=" + package.SizeBytes;
            Require(lines.Count(line => line.EndsWith(" " + suffix, StringComparison.Ordinal)) == 1, "Original per-archive GitHub proof missing: " + package.Id);
            Require(transportLog.Split('\n').Any(line => line.Contains("uri=" + catalog.GitHub.Media(asset).AbsoluteUri + " ") &&
                line.Contains("response=True")), "Original actual GitHub response missing: " + package.Id);
        }
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count != 0)
        {
            string current = pending.Pop();
            Owned(Relative(current));
            foreach (string path in Directory.GetFileSystemEntries(current))
            {
                Owned(Relative(path));
                if (Directory.Exists(path)) pending.Push(path);
                else yield return path;
            }
        }
    }

    private static void AssertOriginalEvidence()
    {
        foreach (var item in OriginalEvidence)
            Require(File.Exists(Owned(item.Key)) && Equal(HashFile(Owned(item.Key)), item.Value), "Original evidence changed: " + item.Key);
        Require(File.ReadAllText(Owned("APF/APFTransaction.log")).StartsWith(originalNativeLog, StringComparison.Ordinal), "Original native log prefix changed.");
        CheckPreserved();
    }

    private static void ValidateCatalog()
    {
        Require(catalog != null && catalog.Version == Version && catalog.GitHub != null, "Starter is not RC22.");
        catalog.GitHub.Validate();
        Require(catalog.GitHub.Enabled && catalog.GitHub.Repository == Repository && catalog.GitHub.MediaTag == "pack-media", "Public configured production GitHub repository/media tag required; no configuration is rewritten.");
        foreach (var manifest in new[] { catalog.Potato, catalog.Full })
        {
            Require(manifest != null && manifest.PackVersion == Version && manifest.RequiredClientVersion == Version && manifest.KspVersion == "1.12.5", "Unexpected profile version.");
            var validation = ApfManifestValidator.Validate(manifest);
            Require(validation.IsValid, string.Join("; ", validation.Errors));
            Require(manifest.ManagedPackages.Count > 0, "Empty profile.");
        }
        Installer.ValidateIdentity(catalog.Full, catalog.Potato, Version);
        Require(catalog.Sizes != null && catalog.Sizes.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Sizes.Count, "Missing/duplicate expanded sizes.");
        foreach (var p in UnionPackages())
        {
            Require(catalog.Sizes.Count(s => Equal(s.Id, p.Id) && s.ExpandedBytes > 0) == 1, "Missing expanded size: " + p.Id);
            var uri = new Uri(p.ArchiveUrl);
            Require(uri.Scheme == "https" && uri.Host == Origin.Host && uri.Port == Origin.Port && uri.Query.Length == 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/" + Path.GetFileName(uri.AbsolutePath), "Non-production package origin: " + p.Id);
            catalog.GitHub.Media(Path.GetFileName(uri.AbsolutePath));
        }
    }

    private static List<ApfPackage> UnionPackages()
    {
        var groups = catalog.Potato.ManagedPackages.Concat(catalog.Full.ManagedPackages).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups) Require(group.Select(PackageIdentity).Distinct().Count() == 1, "Shared package differs between profiles: " + group.Key);
        return groups.Select(g => g.First()).ToList();
    }

    private static string PackageIdentity(ApfPackage p) => p.Id + "|" + p.Version + "|" + p.Sha256.ToLowerInvariant() + "|" + p.SizeBytes + "|" + p.Destination + "|" + p.StripPrefix + "|" + new Uri(p.ArchiveUrl).AbsolutePath;
    private static ApfPackManifest Manifest(string mode) => mode == "potato" ? catalog.Potato : catalog.Full;

    private static void Preflight(string label)
    {
        SetPhase(label + ": verifying live signed production and GitHub RC22 feeds");
        var originFeed = ReadSignedFeed(Fetch(new Uri(Origin, "updates/APFClientUpdate.json")));
        var githubFeed = ReadSignedFeed(Fetch(catalog.GitHub.LatestFeed));
        Require(originFeed.ToJson() == githubFeed.ToJson(), "Live signed GitHub and production feeds disagree.");
        Require(feed == null || feed.ToJson() == originFeed.ToJson(), "Production release changed during this run.");
        feed = originFeed;
        VerifySignedClient();
        foreach (string name in new[] { CatalogPath, "GameData/LunaMultiplayer/APFApplyUpdate.ps1", "GameData/LunaMultiplayer/Plugins/KesasInstall.dll", "GameData/LunaMultiplayer/Plugins/ApfCore.dll", "GameData/LunaMultiplayer/Plugins/JsonFx.dll" })
            Require(feed.Files.Count(f => Equal(f.Path, name)) == 1, "Critical shipped file is not covered by live signature: " + name);
        var server = ApfPackManifest.FromJson(Fetch(new Uri(Origin, "APFPack.json")));
        Installer.ValidateIdentity(server, catalog.Full, Version);
        Installer.ValidateIdentity(server, catalog.Potato, Version);
        WriteNew(evidence + label + "-signed-feed.json", feed.ToJson());
        Say("PASS " + label + ": live signed RC22 feeds agree; exact shipped client/catalog/helper verified; pinned production identity matches.");
    }

    private static ApfClientUpdateManifest ReadSignedFeed(string json)
    {
        var manifest = ApfClientUpdateManifest.FromJson(json);
        var validation = ApfClientUpdateValidator.Validate(manifest);
        Require(validation.IsValid, string.Join("; ", validation.Errors));
        Require(manifest.Version == Version && manifest.KspVersion == "1.12.5", "Live signed feed is not activated RC22; deploy before running.");
        Require(manifest.Files.Count == 462 && Equal(manifest.Sha256, ClientSha256) && manifest.SizeBytes == 1837994,
            "Frozen RC22 signed client identity/count changed.");
        using (var key = ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(Owned(DataPath + "APFClientUpdatePublicKey.xml"))))
            Require(ApfClientUpdateSecurity.Verify(manifest, key), "Live feed signature invalid.");
        var package = new Uri(manifest.PackageUrl);
        Require(package.Scheme == "https" && package.Host == Origin.Host && package.Port == Origin.Port && package.UserInfo.Length == 0 && package.Query.Length == 0 && package.Fragment.Length == 0, "Signed client package is not pinned production.");
        return manifest;
    }

    private static string Fetch(Uri uri)
    {
        Func<Uri, HttpWebRequest> create = target =>
        {
            bool origin = target.Scheme == "https" && target.Host == Origin.Host && target.Port == Origin.Port && target.UserInfo.Length == 0 && target.Fragment.Length == 0;
            Require(origin || catalog.GitHub.AllowsTransport(target), "Unapproved preflight transport.");
            var request = (HttpWebRequest)WebRequest.Create(target);
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 30000;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => certificate != null &&
                (origin ? pins.Contains(Hash(certificate.GetRawCertData())) : catalog.GitHub.AcceptsCertificate(target, errors));
            return request;
        };
        using (var lease = HttpRedirects.Open(create(uri), create, _ => { }, () => { }, catalog.GitHub.ValidateRedirect))
        {
            Require(lease.Response.StatusCode == HttpStatusCode.OK, "Preflight HTTP response was not 200.");
            using (var input = lease.Response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[BufferBytes]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    Require(output.Length + count <= 4L * 1024 * 1024, "Preflight response exceeds 4 MiB.");
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }

    private static InstallResult RunInstaller(string mode, string label, out Installer installer, out int requestStart)
    {
        EnsureIdle();
        AssertOriginalEvidence();
        SetPhase(label + ": actual Installer.Run(\"" + mode + "\") with live updates enabled");
        CheckPreserved(); VerifySignedClient();
        installer = new Installer(root, new IPEndPoint(IPAddress.Parse("217.216.49.250"), 18865), Version);
        installer.Confirm = message => { throw new InvalidDataException("Unexpected extra mods/confirmation in owned clean fixture: " + message); };
        installer.Progress = status => { lock (ProgressGate) phase = label + ": " + status; };
        installer.Report = value => { lock (ProgressGate) progress = value; };
        var clock = Stopwatch.StartNew();
        installer.IsCanceled = () => clock.Elapsed > TimeSpan.FromHours(12);
        requestStart = transport.Count;
        var result = installer.Run(mode);
        Require(!result.ClientUpdate, "Exact shipped RC22 starter unexpectedly needs repair/update; refusing an altered test baseline.");
        Require((bool)GitHubReady.GetValue(installer), "Shipped installer did not validate the GitHub signed channel (githubReady=false).");
        foreach (string name in new[] { "LastDownloadWarning.log", "LastDownloadError.log" })
        {
            string path = Owned("APF/" + name);
            Require(!File.Exists(path), "Shipped transport warning/error; fallback cannot pass: " + (File.Exists(path) ? File.ReadAllText(path) : name));
        }
        string stage = "APF/.client-update-staging/connect/";
        Require(ReadSignedFeed(File.ReadAllText(Owned(stage + "feed.json"))).ToJson() == feed.ToJson(), "Installer origin feed changed.");
        Require(ReadSignedFeed(File.ReadAllText(Owned(stage + "github-feed.json"))).ToJson() == feed.ToJson(), "Installer GitHub feed changed.");
        transport.WriteSummary(label);
        return result;
    }



    private static void Transition(string mode, HashSet<string> expectedIds, string label)
    {
        var selected = Manifest(mode);
        var beforeCache = CacheSnapshot();
        Require((mode == "full" && label == "potato-to-full" && expectedIds.Count == 41) ||
            (mode == "potato" && label == "full-to-potato" && expectedIds.Count == 7), "Only the two exact continuation transitions are allowed.");
        foreach (var package in selected.ManagedPackages.Where(p => expectedIds.Contains(p.Id)))
            Require(beforeCache.ContainsKey(package.Sha256 + ".zip") == (mode == "potato"),
                "Full delta must download fresh; all seven Potato policy archives must reuse original verified cache.");
        var needed = selected.ManagedPackages.Where(p => expectedIds.Contains(p.Id)).ToList();
        long expanded = needed.Sum(p => catalog.Sizes.Single(s => Equal(s.Id, p.Id)).ExpandedBytes);
        long missing = needed.Where(p => !File.Exists(ArchivePath(p))).Sum(p => p.SizeBytes);
        SpaceGuard(checked(missing + 2 * expanded + (needed.Count == 0 ? 0 : needed.Max(p => p.SizeBytes)) + GiB), label);
        var result = RunInstaller(mode, label, out var installer, out int start);
        Require(result.Changed && !string.IsNullOrEmpty(result.TransactionPath), "Expected a profile transaction: " + label);
        var transaction = ReadTransaction(result.TransactionPath);
        Require(new HashSet<string>(transaction.Packages.Select(s => s.Package.Id), StringComparer.OrdinalIgnoreCase).SetEquals(expectedIds) && transaction.Packages.Count == expectedIds.Count, "Installer staged more/less than the actual profile delta: " + label);
        ValidateTransaction(transaction, selected);
        var archiveRequests = transport.Since(start).Where(r => r.Uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(!archiveRequests.Any(r => r.Uri.Host == Origin.Host), "Origin archive request attempted during continuation.");
        if (mode == "potato")
            Require(archiveRequests.Length == 0, "Cached seven-package Potato return attempted extra archive HTTP.");
        else
        {
            var expectedMirrors = new HashSet<string>(selected.ManagedPackages.Where(p => expectedIds.Contains(p.Id))
                .Select(p => catalog.GitHub.Media(Path.GetFileName(new Uri(p.ArchiveUrl).AbsolutePath)).AbsoluteUri));
            Require(expectedMirrors.SetEquals(archiveRequests.Where(r => r.Uri.Host == "github.com").Select(r => r.Uri.AbsoluteUri)),
                "Actual Full archive requests differ from the exact 41-package delta.");
        }
        foreach (var staged in transaction.Packages)
        {
            var package = selected.ManagedPackages.Single(p => Equal(p.Id, staged.Package.Id));
            Require(SamePath(staged.ArchivePath, ArchivePath(package)), "Unexpected cache path.");
            VerifyArchive(package, staged.ArchivePath);
            bool cached = beforeCache.ContainsKey(package.Sha256 + ".zip");
            transport.ProveArchive(package, start, cached);
        }
        AssertOldCache(beforeCache);
        WriteNew(evidence + label + "-original-transaction.json", File.ReadAllText(result.TransactionPath));
        Apply(result.TransactionPath, transaction, label);
        ValidateInstalled(mode, label);
        AssertOldCache(beforeCache);
        CheckPreserved();
        Say("PASS " + label + ": actual signed-catalog delta=" + transaction.Packages.Count + "; native helper; all installed file hashes/sizes/profile verified.");
    }

    private static void Reconnect(string mode, string label)
    {
        SetPhase(label + ": snapshotting clean profile");
        var cache = CacheSnapshot();
        string state = File.ReadAllText(Owned(DataPath + "APFPackState.json"));
        string profile = File.ReadAllText(Owned(DataPath + "KesasInstallProfile.json"));
        string settings = File.ReadAllText(Owned("settings.cfg"));
        var result = RunInstaller(mode, label, out var installer, out int start);
        Require(!result.Changed && !result.ClientUpdate && string.IsNullOrEmpty(result.TransactionPath), "Clean reconnect is not idempotent: " + label);
        Require(!transport.Since(start).Any(r => r.Uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "Idempotent reconnect attempted an archive request.");
        Require(state == File.ReadAllText(Owned(DataPath + "APFPackState.json")) && profile == File.ReadAllText(Owned(DataPath + "KesasInstallProfile.json")) && settings == File.ReadAllText(Owned("settings.cfg")), "Idempotent reconnect changed state/profile/settings.");
        Require(CacheSnapshot().Count == cache.Count, "Idempotent reconnect changed archive count.");
        AssertOldCache(cache);
        ValidateInstalled(mode, label);
        CheckPreserved();
        Say("PASS " + label + ": no download/restart; state, settings, dropdown, identity, save, cache preserved.");
    }

    private static ApfSyncTransaction ReadTransaction(string path)
    {
        Require(ApfPaths.IsUnder(Path.GetFullPath(path), Owned("APF/.staging")), "Transaction is outside the owned staging tree.");
        ApfPaths.EnsureNoReparsePoints(root, path);
        Require(new FileInfo(path).Length <= 16L * 1024 * 1024, "Oversized transaction.");
        var transaction = ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(path));
        Require(transaction != null && ApfTransactionSecurity.Verify(transaction, ApfTransactionSecurity.LoadKey(Owned(DataPath + "APFHelper.key"))), "Original engine transaction authorization failed.");
        return transaction;
    }

    private static void ValidateTransaction(ApfSyncTransaction transaction, ApfPackManifest selected)
    {
        Require(SamePath(transaction.GameRoot, root) && SamePath(transaction.GameDataPath, Owned("GameData")) && SamePath(transaction.StatePath, Owned(DataPath + "APFPackState.json")) && SamePath(transaction.KspExecutable, Owned("KSP_x64.exe")), "Transaction targets a path outside the fixture.");
        Require(transaction.Manifest.ToJson() == selected.ToJson(), "Transaction manifest differs from the live-signed catalog.");
        var knownRoots = new HashSet<string>(UnionPackages().Select(p => "GameData/" + ApfPaths.PackageRoot(p).Split('/')[0]), StringComparer.OrdinalIgnoreCase);
        var desiredRoots = new HashSet<string>(selected.ManagedPackages.Select(p => "GameData/" + ApfPaths.PackageRoot(p).Split('/')[0]), StringComparer.OrdinalIgnoreCase);
        foreach (var deletion in transaction.Deletes)
            Require(knownRoots.Contains(deletion.RelativePath) && !desiredRoots.Contains(deletion.RelativePath), "Unexpected removal target: " + deletion.RelativePath);
        foreach (var staged in transaction.Packages)
            Require(selected.ManagedPackages.Count(p => PackageIdentity(p) == PackageIdentity(staged.Package)) == 1, "Staged package is not signed-catalog content.");
    }

    private static void Apply(string path, ApfSyncTransaction transaction, string label)
    {
        SetPhase(label + ": native post-exit helper (45 minute bound; placeholder relaunch only)");
        CheckPreserved(); VerifySignedClient();
        Require(Equal(HashFile(Owned("KSP_x64.exe")), stubHash), "Relaunch target is not the test placeholder.");
        transaction.ProcessId = 0;
        transaction.KspArguments = new List<string>();
        var key = ApfTransactionSecurity.LoadKey(Owned(DataPath + "APFHelper.key"));
        ApfTransactionSecurity.Sign(transaction, key);
        File.WriteAllText(path, ApfJson.Serialize(transaction));
        Require(ApfTransactionSecurity.Verify(ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(path)), key), "Re-signed helper transaction failed verification.");
        string nativeLogPath = Owned("APF/APFTransaction.log");
        long previousLogLength = File.Exists(nativeLogPath) ? new FileInfo(nativeLogPath).Length : 0;
        var start = ApfPostExitLauncher.CreateStartInfo(root, path, false);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.WorkingDirectory = root;
        using (var process = Process.Start(start))
        {
            Require(process != null, "Native helper did not start.");
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            var stdout = Task.Run(() => CopyOutput(process.StandardOutput, evidence + label + "-helper.stdout.log"));
            var stderr = Task.Run(() => CopyOutput(process.StandardError, evidence + label + "-helper.stderr.log"));
            var clock = Stopwatch.StartNew();
            while (!process.WaitForExit(1000))
            {
                if (clock.Elapsed <= TimeSpan.FromMinutes(45)) continue;
                process.Kill();
                process.WaitForExit(10000);
                throw new TimeoutException("Owned native helper exceeded 45 minutes; fixture/evidence retained. No retry or cleanup performed.");
            }
            Require(Task.WaitAll(new[] { stdout, stderr }, 30000), "Helper output did not drain within 30 seconds.");
            Require(process.ExitCode == 0, "Native helper failed with exit code " + process.ExitCode + "; inspect retained helper output/APF logs.");
        }
        Require(File.Exists(nativeLogPath) && new FileInfo(nativeLogPath).Length > previousLogLength, "Native helper did not append its transaction log.");
        string transactionLog;
        using (var input = OpenSequential(nativeLogPath))
        {
            input.Position = previousLogLength;
            using (var reader = new StreamReader(input)) transactionLog = reader.ReadToEnd();
        }
        Require(transactionLog.Contains(" START pack=kesas version=kesas-rc22") &&
            transactionLog.Contains("COMPLETED packages=" + transaction.Packages.Count + " deletes=" + transaction.Deletes.Count) &&
            !Regex.IsMatch(transactionLog, @"\b(ROLLED-BACK|FAIL|ERROR|WARNING)\b"), "Missing this native transaction's start/completion or observed rollback/error.");
    }

    private static void CopyOutput(StreamReader input, string relative)
    {
        using (var output = new StreamWriter(NewFile(relative))) { char[] buffer = new char[4096]; int count; while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { output.Write(buffer, 0, count); output.Flush(); } }
    }

    private static void VerifyArchive(ApfPackage package, string path)
    {
        SetPhase("verifying archive and every entry: " + package.Id);
        var files = new Dictionary<string, FileProof>(StringComparer.OrdinalIgnoreCase);
        using (var inputFile = OpenSequential(path))
        {
            Require(inputFile.Length == package.SizeBytes && Equal(Hash(inputFile, package.SizeBytes), package.Sha256),
                "Downloaded archive SHA-256/size mismatch: " + package.Id);
            inputFile.Position = 0;
            using (var zip = new ZipArchive(inputFile, ZipArchiveMode.Read, true))
            {
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    string raw = entry.FullName.Replace('\\', '/');
                    int unixKind = (entry.ExternalAttributes >> 16) & 0xf000;
                    Require(!Path.IsPathRooted(raw) && ApfPaths.IsSafeRelative(raw.TrimEnd('/')) &&
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) == 0 &&
                        (unixKind == 0 || unixKind == 0x8000 || unixKind == 0x4000), "Unsafe ZIP path/link/type: " + entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    Require(files.Count < 100000 && entry.Length >= 0 && entry.Length <= 32 * GiB - expanded,
                        "ZIP entry count/expanded size exceeds exact shipped limits.");
                    expanded = checked(expanded + entry.Length);
                    string output = (string)ResolveZipEntry.Invoke(null, new object[] { entry.FullName, package, Owned("GameData") });
                    Require(ApfPaths.IsUnder(output, Owned("GameData")), "ZIP output escapes GameData.");
                    string relative = Relative(output);
                    Owned(relative);
                    Require(!files.Keys.Any(existing => (bool)ZipPathsCollide.Invoke(null, new object[] { existing, relative })),
                        "ZIP duplicate/overlapping output path: " + entry.FullName);
                    using (var input = entry.Open())
                        files.Add(relative, new FileProof { Size = entry.Length, Sha256 = Hash(input, entry.Length) });
                }
            }
        }
        Require(files.Count > 0 && files.Values.Sum(f => f.Size) == catalog.Sizes.Single(s => Equal(s.Id, package.Id)).ExpandedBytes,
            "Expanded catalog size/entry inventory mismatch: " + package.Id);
        Inventories[package.Id] = files;
        ArchiveHashes[package.Sha256 + ".zip"] = package.Sha256;
        WriteNew(evidence + "a-" + Guid.NewGuid().ToString("N") + ".json", ApfJson.Serialize(new { packageId=package.Id,files }));
    }

    private static void ValidateInstalled(string mode, string label)
    {
        SetPhase(label + ": checking every installed file SHA-256 and size");
        var selected = Manifest(mode);
        var state = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(DataPath + "APFPackState.json")));
        Require(state != null && state.PackId == selected.PackId && state.PackVersion == selected.PackVersion && state.Packages != null, "Wrong installed pack state identity.");
        Require(state.Packages.Count == selected.ManagedPackages.Count && new HashSet<string>(state.Packages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase).SetEquals(selected.ManagedPackages.Select(p => p.Id)), "Installed package state set differs from selected profile.");
        long fileCount = 0, bytes = 0;
        foreach (var package in selected.ManagedPackages)
        {
            var installed = state.Packages.Single(p => Equal(p.Id, package.Id));
            Require(installed.Version == package.Version && Equal(installed.ArchiveSha256, package.Sha256) && Equal(installed.Root, ApfPaths.PackageRoot(package)), "Installed package state identity mismatch: " + package.Id);
            Require(Inventories.TryGetValue(package.Id, out var expected), "No independently verified archive inventory: " + package.Id);
            Require(installed.Files != null && installed.Files.Count == expected.Count && new HashSet<string>(installed.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys), "State file inventory mismatch: " + package.Id);
            foreach (var file in installed.Files)
            {
                var proof = expected[file.Path];
                string target = Owned(file.Path);
                Require(File.Exists(target) && new FileInfo(target).Length == proof.Size && Equal(file.Sha256, proof.Sha256) && Equal(HashFile(target), proof.Sha256), "Installed file SHA-256/size differs from state and verified archive: " + file.Path);
                fileCount++; bytes = checked(bytes + proof.Size);
            }
            string packageRoot = Owned("GameData/" + ApfPaths.PackageRoot(package));
            var actual = File.Exists(packageRoot) ? new[] { packageRoot } : SafeFiles(packageRoot).ToArray();
            Require(new HashSet<string>(actual.Select(Relative), StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys), "Untracked files inside installed package: " + package.Id);
        }
        var allowed = new HashSet<string>(starterRoots, StringComparer.OrdinalIgnoreCase) { "Squad" };
        foreach (var p in selected.ManagedPackages) allowed.Add(ApfPaths.PackageRoot(p).Split('/')[0]);
        Require(new HashSet<string>(Directory.GetFileSystemEntries(Owned("GameData")).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase).SetEquals(allowed), "Unexpected/missing GameData roots (including visual mods).");
        var selectedRoots = new HashSet<string>(selected.ManagedPackages.Select(p => ApfPaths.PackageRoot(p)), StringComparer.OrdinalIgnoreCase);
        foreach (var excluded in UnionPackages().Where(p => !selectedRoots.Contains(ApfPaths.PackageRoot(p))))
            Require(!File.Exists(Owned("GameData/" + ApfPaths.PackageRoot(excluded))) && !Directory.Exists(Owned("GameData/" + ApfPaths.PackageRoot(excluded))), "Excluded profile/visual mod remains installed: " + excluded.Id);
        Require(Directory.GetFileSystemEntries(Owned("GameData/Squad")).Length == 0, "Empty Squad test sentinel was modified.");
        var profile = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Owned(DataPath + "KesasInstallProfile.json")));
        Require(profile["mode"] == mode && profile["catalogVersion"] == catalog.Version, "Completed profile metadata mismatch.");
        var settings = File.ReadAllText(Owned("settings.cfg"));
        Require(settings.Contains("KEY_BINDING = live-profile-test-preserve") && settings.Contains("SOUND_VOLUME = 0.37"), "Non-graphics settings changed.");
        var graphics = Installer.GraphicsValues(settings);
        var expectedGraphics = mode == "full" ? Installer.GraphicsValues(InitialSettings) : new Dictionary<string, string>
        {
            ["QUALITY_PRESET"] = "0", ["ANTI_ALIASING"] = "0", ["TEXTURE_QUALITY"] = "2", ["SYNC_VBL"] = "0",
            ["LIGHT_QUALITY"] = "0", ["SHADOWS_QUALITY"] = "0", ["FRAMERATE_LIMIT"] = "60",
            ["CELESTIAL_BODIES_CAST_SHADOWS"] = "False", ["PLANET_SCATTER"] = "False", ["PLANET_SCATTER_FACTOR"] = "0",
            ["AERO_FX_QUALITY"] = "0", ["SURFACE_FX"] = "False", ["REFLECTION_PROBE_REFRESH_MODE"] = "0",
            ["REFLECTION_PROBE_TEXTURE_RESOLUTION"] = "0", ["TERRAIN_SHADER_QUALITY"] = "0"
        };
        Require(graphics.Count == expectedGraphics.Count && expectedGraphics.All(item => graphics.TryGetValue(item.Key, out string value) && value == item.Value), "Graphics policy/restoration differs from shipped " + mode + " behavior.");
        Func<string, string> nonGraphics = text => string.Join("\n", text.Replace("\r", "").Split('\n').Where(line => !expectedGraphics.Keys.Any(key => line.StartsWith(key + " =", StringComparison.Ordinal))).Where(line => line.Length > 0));
        Require(nonGraphics(settings) == nonGraphics(InitialSettings), "Settings outside the explicit profile graphics keys were not preserved.");
        VerifySignedClient();
        WriteNew(evidence + label + "-state.json", ApfJson.Serialize(state));
        Say("PASS " + label + ": " + state.Packages.Count + " packages; " + fileCount + " files; " + bytes + " bytes; exact profile roots, no unexpected visual mods.");
    }

    private static string SharedSnapshot()
    {
        var full = new HashSet<string>(catalog.Full.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var shared = new HashSet<string>(catalog.Potato.ManagedPackages.Where(p => full.Contains(p.Id)).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        Require(shared.Count > 0, "No shared content to verify.");
        var state = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(DataPath + "APFPackState.json")));
        return ApfJson.Serialize(state.Packages.Where(p => shared.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void VerifySignedClient()
    {
        foreach (var file in feed.Files)
        {
            string path = Owned(file.Path);
            Require(File.Exists(path) && new FileInfo(path).Length == file.SizeBytes && Equal(HashFile(path), file.Sha256), "Shipped client file differs from live signed RC22 inventory: " + file.Path);
        }
    }

    private static Dictionary<string, string> CacheSnapshot()
    {
        string directory = Owned("APF/.staging/connect");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        foreach (string path in Directory.GetFiles(directory, "*.zip"))
        {
            string name = Path.GetFileName(path), hash = HashFile(path);
            Require(ArchiveHashes.TryGetValue(name, out string expected) && Equal(hash, expected), "Unverified/changed archive in owned cache: " + name);
            result.Add(name, hash + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks);
        }
        return result;
    }

    private static void AssertOldCache(Dictionary<string, string> before)
    {
        var after = CacheSnapshot();
        foreach (var item in before) Require(after.TryGetValue(item.Key, out string current) && current == item.Value, "Existing verified cache archive changed/disappeared: " + item.Key);
    }

    private static string ArchivePath(ApfPackage package) => Owned("APF/.staging/connect/" + package.Sha256 + ".zip");
    private static void Remember(string relative) => Preserved.Add(relative, HashFile(Owned(relative)));
    private static void CheckPreserved()
    {
        foreach (var item in Preserved) Require(File.Exists(Owned(item.Key)) && Equal(HashFile(Owned(item.Key)), item.Value), "Identity/save/dropdown/trust configuration changed: " + item.Key);
        if (stubHash != null) Require(Equal(HashFile(Owned("KSP_x64.exe")), stubHash), "GameStub placeholder was replaced.");
    }

    private static void SpaceGuard(long required, string purpose)
    {
        long available = new DriveInfo(Path.GetPathRoot(work)).AvailableFreeSpace;
        Say("Space guard " + purpose + ": required=" + required + " bytes; available=" + available + " bytes.");
        Require(available >= required, "Insufficient free space for " + purpose + ". No existing paths will be deleted.");
    }

    private static string Owned(string relative)
    {
        Require(!Path.IsPathRooted(relative) && ApfPaths.IsSafeRelative(relative), "Unsafe fixture-relative path: " + relative);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(ApfPaths.IsUnder(path, root), "Path escapes owned fixture.");
        ApfPaths.EnsureNoReparsePoints(root, path);
        return path;
    }
    private static string Relative(string path) => path.Substring(root.Length + 1).Replace('\\', '/');
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool IsHash(string value) => value != null && value.Length == 64 && value.All(c => "0123456789abcdefABCDEF".Contains(c));
    private static FileStream NewFile(string relative)
    {
        Require(evidence != null && relative.StartsWith(evidence, StringComparison.Ordinal), "Writes must target the unique new evidence directory.");
        string path = Owned(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    private static void WriteNew(string relative, string text) { using (var output = new StreamWriter(NewFile(relative), new UTF8Encoding(false))) output.Write(text); }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name) { var entry = zip.GetEntry(name); Require(entry != null, "Starter entry missing: " + name); return entry; }
    private static string ReadEntry(ZipArchive zip, string name) { using (var input = new StreamReader(Entry(zip, name).Open())) return input.ReadToEnd(); }
    private static FileStream OpenSequential(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
    private static ZipArchive OpenZip(string path) => new ZipArchive(OpenSequential(path), ZipArchiveMode.Read, false);
    private static string HashFile(string path)
    {
        using (var input = OpenSequential(path)) return Hash(input, input.Length);
    }
    private static string Hash(Stream input, long expected = -1)
    {
        byte[] buffer = hashBuffer ?? (hashBuffer = new byte[BufferBytes]);
        long total = 0;
        using (var sha = new SHA256Cng())
        {
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                total = checked(total + count);
                Require(expected < 0 || total <= expected, "Hash stream exceeds expected length.");
                sha.TransformBlock(buffer, 0, count, buffer, 0);
            }
            Require(expected < 0 || total == expected, "Hash stream length mismatch.");
            sha.TransformFinalBlock(new byte[0], 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }
    private static string Hash(byte[] bytes) { using (var input = new MemoryStream(bytes)) return Hash(input); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void SetPhase(string value) { lock (ProgressGate) { phase = value; progress = null; } Say(value); }
    private static void Heartbeat()
    {
        try
        {
            string message;
            lock (ProgressGate) message = phase + (progress == null ? "" : "; package=" + progress.Package + "; bytes=" + progress.Downloaded + "/" + progress.Total + "; speed=" + progress.BytesPerSecond.ToString("F0", CultureInfo.InvariantCulture) + " B/s");
            Say("PROGRESS " + message);
        }
        catch (Exception error) { Console.Error.WriteLine("Heartbeat failure: " + error.Message); }
    }
    private static void Say(string text)
    {
        lock (LogGate) { string line = DateTime.UtcNow.ToString("o") + " " + text; Console.WriteLine(line); log?.WriteLine(line); }
    }

    private sealed class FileProof { public long Size { get; set; } public string Sha256 { get; set; } }
    private sealed class RequestProof { public Uri Uri; public HttpWebRequest Request; public string Phase; }
    private sealed class RefuseHttp : IWebRequestCreate { public WebRequest Create(Uri uri) { throw new InvalidDataException("Plaintext network transport is forbidden."); } }

    private sealed class TransportAudit : IWebRequestCreate
    {
        private readonly GitHubSettings settings;
        private readonly object gate = new object();
        private readonly List<RequestProof> requests = new List<RequestProof>();
        public TransportAudit(GitHubSettings settings) { this.settings = settings; }
        public int Count { get { lock (gate) return requests.Count; } }
        public RequestProof[] Since(int index) { lock (gate) return requests.Skip(index).ToArray(); }
        public WebRequest Create(Uri uri)
        {
            bool origin = uri.Scheme == "https" && uri.Host == Origin.Host && uri.Port == Origin.Port && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
            Require(origin || settings.AllowsTransport(uri), "Unapproved request denied before contact: " + uri.Host);
            var request = WebRequest.CreateDefault(uri) as HttpWebRequest;
            Require(request != null && request.RequestUri == uri, "Diagnostic prefix did not return the normal HttpWebRequest.");
            string current; lock (ProgressGate) current = phase;
            lock (gate) requests.Add(new RequestProof { Uri = uri, Request = request, Phase = current });
            Say("TRANSPORT create route=" + (origin ? "pinned-origin" : "GitHub") + " uri=" + SafeUri(uri));
            return request;
        }
        public void ProveArchive(ApfPackage package, int start, bool cached)
        {
            var seen = Since(start);
            string asset = Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath);
            var originRequests = seen.Where(r => r.Uri.Host == Origin.Host && r.Uri.AbsolutePath.EndsWith("/" + asset, StringComparison.Ordinal)).ToArray();
            Require(originRequests.Length == 0 && !seen.Any(r => r.Uri.Host == Origin.Host && r.Uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
                "Origin archive fallback attempted: " + asset);
            var mirror = settings.Media(asset);
            var github = seen.Where(r => r.Uri == mirror).ToArray();
            if (cached)
            {
                Require(github.Length == 0, "Cached archive was unnecessarily re-downloaded: " + asset);
                Say("ROUTE archive=" + asset + " source=verified-same-fixture-cache original=GitHub sha256=" + package.Sha256 + " bytes=" + package.SizeBytes);
                return;
            }
            Require(github.Any(r => r.Request.HaveResponse), "No actual GitHub response observed for uncached archive: " + asset);
            Require(seen.Any(r => r.Uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) && r.Request.HaveResponse), "Missing actual GitHub CDN response evidence: " + asset);
            Say("ROUTE archive=" + asset + " source=GitHub response=true originArchiveRequests=0 githubReady=true fallbackWarnings=0 sha256=" + package.Sha256 + " bytes=" + package.SizeBytes);
        }
        public void WriteSummary(string label)
        {
            var lines = Since(0).Select(r => DateTime.UtcNow.ToString("o") + " phase=" + r.Phase + " uri=" + SafeUri(r.Uri) + " response=" + r.Request.HaveResponse + " range=" + (r.Request.Headers[HttpRequestHeader.Range] ?? "none")).ToArray();
            WriteNew(evidence + label + "-transport.log", string.Join(Environment.NewLine, lines));
            Say("Transport evidence: " + Owned(evidence + label + "-transport.log"));
        }
        private static string SafeUri(Uri uri) => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length == 0 ? "" : "?[redacted]");
    }
}
