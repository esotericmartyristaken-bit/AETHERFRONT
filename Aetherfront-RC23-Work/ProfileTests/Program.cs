using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private const string Release = "kesas-rc23";
    private const string FourHash = "57beb1c4342582da0ef44a9f838f24103583028f4c5eb2fa6ba33f5ce541c2b1";
    private const string EightHash = "36be531daf64fe9aecd02a796cf729bfed8b9398afc6115a073b96bd326a4e79";
    private const string FourId = "kesas-kcalbelohtextures-4k-v1.1.8";
    private const string EightId = "kesas-kcalbelohtextures-44097afb";
    private const string Data = "GameData/LunaMultiplayer/Data/";
    private const string CatalogFile = "GameData/LunaMultiplayer/Launcher/install-catalog.json";
    private const string InitialSettings = "QUALITY_PRESET = 3\nANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nSYNC_VBL = 1\nLIGHT_QUALITY = 8\nSHADOWS_QUALITY = 3\nFRAMERATE_LIMIT = 120\nCELESTIAL_BODIES_CAST_SHADOWS = True\nPLANET_SCATTER = True\nPLANET_SCATTER_FACTOR = 1\nAERO_FX_QUALITY = 2\nSURFACE_FX = True\nREFLECTION_PROBE_REFRESH_MODE = 1\nREFLECTION_PROBE_TEXTURE_RESOLUTION = 1\nTERRAIN_SHADER_QUALITY = 2\nKEY_BINDING = preserve-fixture-binding\nSOUND_VOLUME = 0.37\n";
    private static readonly MethodInfo PackagePresent = typeof(Installer).GetMethod("PackagePresent", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ApfPackage), typeof(ApfPackageState) }, null);
    private static readonly Dictionary<string, string> Preserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PhaseResult> Phases = new List<PhaseResult>();
    private static string work, root, evidence, phase = "inputs", stubHash;
    private static InstallCatalog catalog;
    private static Comparison comparison;
    private static ApfClientUpdateManifest signedClient;
    private static ApfPackage four, eight;
    private static StreamWriter log;
    private static int? activeHelper;
    private static bool resumeFinal;

    private static int Main(string[] args)
    {
        resumeFinal = args.Length == 2 && args[0] == "--resume-reviewed-final" && args[1] == "--ack-headless";
        if (!resumeFinal && (args.Length != 2 || args[0] != "--run-production" || args[1] != "--ack-headless"))
        {
            Console.Error.WriteLine("Not run. From the APF workspace: Aetherfront-RC23-Work\\ProfileTests\\bin\\Release\\net472\\ProfileTests.exe --run-production --ack-headless\nCreates a new local GUID fixture. Production native helper; GameStub only. No Unity, network downloads, home-PC access, server mutation, or cleanup.");
            return 2;
        }
        try
        {
            Run();
            SaveReport(true, null);
            Say("PASS HEADLESS: isolated native 8K seed -> 4K -> 8K -> 4K; exact 227-file inventories, package state and preservation. Full-profile graphics, Unity/gameplay/network NOT tested here.");
            return 0;
        }
        catch (Exception error)
        {
            Say("FAIL " + phase + ": " + error);
            if (evidence != null) SaveReport(false, error.ToString());
            return 1;
        }
        finally
        {
            if (root != null) Say("Fixture retained: " + root);
            log?.Dispose();
        }
    }

    private static void Run()
    {
        work = Path.GetFullPath("Aetherfront-RC23-Work");
        Require(!work.StartsWith(@"\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(work)).DriveType == DriveType.Fixed, "Fixed local workspace required.");
        ApfPaths.EnsureNoReparsePoints(Path.GetPathRoot(work), work);
        string starter = Path.Combine(work, "Candidate/Aetherfront-Starter-kesas-rc23.zip");
        string feedPath = Path.Combine(work, "Candidate/Published/APFClientUpdate.json");
        string candidate = Path.Combine(work, "Candidate/Client");
        string official = Path.Combine(work, "Kcalbeloh.Textures-4k.zip");
        string original = Path.GetFullPath("KESAS-VPS-Server/Server/Config/kesas-kcalbelohtextures-44097afb-36be531daf64fe9a.zip");
        string stub = Path.GetFullPath("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe");
        foreach (var input in new[] { starter, feedPath, official, original, stub, Path.Combine(work, "Kcalbeloh-Comparison.json") })
        {
            ApfPaths.EnsureNoReparsePoints(Path.GetPathRoot(work), input);
            Require(File.Exists(input), "Missing input: " + input);
        }
        Require(PackagePresent != null, "Exact shipped Installer.PackagePresent signature is required.");
        signedClient = ApfClientUpdateManifest.FromJson(File.ReadAllText(feedPath));
        Require(signedClient.Version == Release && ApfClientUpdateValidator.Validate(signedClient).IsValid, "RC23 signed client manifest required.");
        // Export adds public trust files to the starter, not the signed overlay.
        // Verify against the established operator-side public key independently.
        using (var key = ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(Path.GetFullPath("KESAS-VPS-Server/secrets/update-signing-public.xml"))))
            Require(ApfClientUpdateSecurity.Verify(signedClient, key), "Candidate signature failed.");
        foreach (var assembly in new[] { typeof(Installer).Assembly, typeof(ApfJson).Assembly, typeof(JsonFx.Json.JsonReader).Assembly })
        {
            string relative = "GameData/LunaMultiplayer/Plugins/" + Path.GetFileName(assembly.Location);
            var proof = signedClient.Files.Single(f => f.Path == relative);
            Require(Equal(Hash(assembly.Location), proof.Sha256) && Equal(Hash(Path.Combine(candidate, relative)), proof.Sha256), "Loaded reference is not the exact signed candidate: " + relative);
        }
        comparison = ApfJson.Deserialize<Comparison>(File.ReadAllText(Path.Combine(work, "Kcalbeloh-Comparison.json")));
        Require(comparison.Files4k == 227 && comparison.Files8k == 227 && comparison.ChangedCount == 48 &&
            comparison.GameplayChanges.Length == 0 && comparison.MissingPaths.Length == 0 && comparison.AddedPaths.Length == 0 &&
            Equal(comparison.Archive4kSha256, FourHash) && Equal(comparison.Archive8kSha256, EightHash), "Reviewed official comparison changed.");
        ValidateInventory(comparison.Inventory4k); ValidateInventory(comparison.Inventory8k);
        var oldMap = comparison.Inventory8k.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Require(comparison.Inventory4k.All(f => oldMap.ContainsKey(f.Path)), "Texture variant path sets differ.");
        var changed = comparison.Inventory4k.Where(f => !Equal(f.Sha256, oldMap[f.Path].Sha256)).ToArray();
        Require(changed.Length == 48 && !changed.Any(f => Regex.IsMatch(f.Path, @"(?i)(height|biome|\.cfg$|\.dll$|\.bin$)")), "Gameplay-data comparison failed.");
        Require(new FileInfo(official).Length == 696453837 && Equal(Hash(official), FourHash) && Equal(Hash(original), EightHash), "Texture archive checksum mismatch.");
        Require(new FileInfo(stub).Length == 4608, "Existing minimal GameStub required.");
        stubHash = Hash(stub);
        Require(new DriveInfo(Path.GetPathRoot(work)).AvailableFreeSpace >= 20L * 1024 * 1024 * 1024, "Reserve 20 GiB for owned fixture, archives and retained recovery data.");

        if (resumeFinal) { ResumeReviewedFinal(); return; }

        root = Path.Combine(work, "ProfileFixture-" + Guid.NewGuid().ToString("N"));
        Require(!Directory.Exists(root) && !File.Exists(root), "New GUID fixture required.");
        Directory.CreateDirectory(root);
        evidence = Owned("evidence"); Directory.CreateDirectory(evidence);
        log = new StreamWriter(new FileStream(Owned("evidence/ProfileTests.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        WriteNew(".profile-fixture-owned", "Headless RC23 texture regression; no game content.\n");
        using (var zip = ZipFile.OpenRead(starter))
        {
            foreach (var entry in zip.Entries)
            {
                Require(!entry.FullName.Contains("\\") && ApfPaths.IsSafeRelative(entry.FullName.TrimEnd('/')), "Unsafe starter entry.");
                Owned(entry.FullName.TrimEnd('/'));
            }
            zip.ExtractToDirectory(root);
        }
        VerifySignedFiles();
        catalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Owned(CatalogFile)));
        Require(catalog.Version == Release && catalog.Full.PackVersion == Release && catalog.Potato.PackVersion == Release && catalog.Full.RequiredClientVersion == Release && catalog.Potato.RequiredClientVersion == Release, "Catalog version mismatch.");
        foreach (var manifest in new[] { catalog.Full, catalog.Potato })
        {
            var validation = ApfManifestValidator.Validate(manifest);
            Require(validation.IsValid, string.Join("; ", validation.Errors));
        }
        four = catalog.Potato.ManagedPackages.Single(p => p.Id == FourId);
        eight = catalog.Full.ManagedPackages.Single(p => p.Id == EightId);
        Require(Equal(four.Sha256, FourHash) && Equal(eight.Sha256, EightHash) && four.Version == "sha-57beb1c4342582da" &&
            four.Destination == "GameData/KcalbelohTextures" && four.StripPrefix == "GameData/KcalbelohTextures" && four.Id != eight.Id &&
            !catalog.Potato.ManagedPackages.Any(p => p.Id == eight.Id) && !catalog.Full.ManagedPackages.Any(p => p.Id == four.Id), "Distinct profile texture identities required.");
        Require(catalog.Potato.ManagedPackages.Any(p => Equal(ApfPaths.PackageRoot(p), "ZZZ_KESASPotato")), "Actual Potato policy must select post-exit Potato mode.");
        Require(catalog.Sizes.Single(s => s.Id == four.Id).ExpandedBytes == comparison.Inventory4k.Sum(f => f.SizeBytes) &&
            catalog.Sizes.Single(s => s.Id == eight.Id).ExpandedBytes == comparison.Inventory8k.Sum(f => f.SizeBytes), "Expanded-size metadata mismatch.");
        File.Copy(stub, Owned("KSP_x64.exe"));
        Directory.CreateDirectory(Owned("GameData/Squad"));
        foreach (var item in new Dictionary<string, string> {
            ["GameData/Squad/fixture-sentinel.txt"] = "Owned Squad placeholder; preserve.",
            ["GameData/UnrelatedFixture/sentinel.cfg"] = "Unrelated mod; preserve.",
            [Data + "settings.xml"] = "<fixture>preserve player settings</fixture>",
            [Data + "settings_bkp.xml"] = "<fixture>preserve player settings backup</fixture>",
            [Data + "KesasInstallSelection.txt"] = "potato\n",
            [Data + "privatekey.txt"] = "headless fixture identity only",
            ["APF/Identity/fixture.txt"] = "preserve identity directory",
            ["saves/fixture/persistent.sfs"] = "preserve fixture save",
            ["APF/preexisting-backup/sentinel.txt"] = "preserve prior recovery"
        }) { WriteNew(item.Key, item.Value); Preserved.Add(item.Key, Hash(Owned(item.Key))); }
        Preserved.Add("KSP_x64.exe", stubHash);
        WriteNew("settings.cfg", InitialSettings);
        foreach (string trust in new[] { "APFServer.txt", "APFTlsCertificatePins.txt", "APFClientUpdatePublicKey.xml" })
            Preserved.Add(Data + trust, Hash(Owned(Data + trust)));
        ApfTransactionSecurity.LoadOrCreateKey(Owned(Data + "APFHelper.key"));
        Preserved.Add(Data + "APFHelper.key", Hash(Owned(Data + "APFHelper.key")));
        CopyArchive(original, eight); CopyArchive(official, four);
        Install("seed-8k", catalog.Full, eight, comparison.Inventory8k, "full");
        WriteNew("GameData/KcalbelohTextures/obsolete-8k-fixture.txt", "Old-root sentinel must not survive whole-root replacement.");
        Install("8k-to-4k", catalog.Potato, four, comparison.Inventory4k, "potato");
        Install("4k-to-8k", catalog.Full, eight, comparison.Inventory8k, "full");
        Install("8k-to-4k-again", catalog.Potato, four, comparison.Inventory4k, "potato");
        VerifySignedFiles(); CheckPreserved();
    }

    private static void Install(string label, ApfPackManifest selected, ApfPackage package, FileProof[] files, string mode)
    {
        phase = label;
        Say("START " + label + ": actual catalog texture declaration in isolated fixture manifest; no explicit deletions.");
        CheckPreserved();
        var statePath = Owned(Data + "APFPackState.json");
        var old = File.Exists(statePath) ? ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath)) : new ApfPackState();
        if (old.Packages.Count > 0) Require(!Present(package, old.Packages.Single()), "Wrong-profile texture state was incorrectly accepted.");
        // The native helper verifies the entire declared pack after applying it.
        // Isolate this texture regression rather than falsely declaring 210 mods
        // installed in a one-package fixture. Full production manifests are
        // validated above and exercised by the separate live connection test.
        var isolated = ApfPackManifest.FromJson(selected.ToJson());
        isolated.ManagedPackages = new List<ApfPackage> { package };
        isolated.ClientOnly = new List<ApfClientOnlyRule> { new ApfClientOnlyRule {
            Id = "owned-unrelated-fixture", PathRoots = new List<string> { "GameData/UnrelatedFixture" }, Dlls = new List<ApfClientOnlyDll>() } };
        var transaction = new ApfSyncTransaction {
            GameRoot = root, GameDataPath = Owned("GameData"), KspExecutable = Owned("KSP_x64.exe"), ProcessId = 0,
            KspArguments = new List<string> { "--headless-fixture-operation=" + Guid.NewGuid().ToString("N") }, StatePath = statePath, Manifest = isolated,
            ExistingState = old, Packages = new List<ApfStagedPackage> { new ApfStagedPackage { Package = package, ArchivePath = Archive(package) } },
            Deletes = new List<ApfDeleteChange>()
        };
        byte[] key = ApfTransactionSecurity.LoadKey(Owned(Data + "APFHelper.key"));
        ApfTransactionSecurity.Sign(transaction, key);
        string request = "APF/.staging/requests/" + Guid.NewGuid().ToString("N") + "/transaction.json";
        WriteNew(request, ApfJson.Serialize(transaction));
        Require(ApfTransactionSecurity.Verify(ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(Owned(request))), key), "Transaction signature failed.");
        string nativeLog = Owned("APF/APFTransaction.log");
        long logOffset = File.Exists(nativeLog) ? new FileInfo(nativeLog).Length : 0;
        Require(Equal(Hash(Owned("KSP_x64.exe")), stubHash), "Refusing to launch anything except owned GameStub.");
        var start = ApfPostExitLauncher.CreateStartInfo(root, Owned(request), false);
        start.UseShellExecute = false; start.CreateNoWindow = true; start.WorkingDirectory = root;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        var clock = Stopwatch.StartNew();
        using (var process = Process.Start(start))
        {
            Require(process != null, "Native helper did not start.");
            activeHelper = process.Id;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            while (!process.WaitForExit(15000))
            {
                Say("PROGRESS " + label + ": owned native helper PID " + process.Id + "; " + clock.Elapsed.TotalSeconds.ToString("0") + " seconds.");
                if (clock.Elapsed < TimeSpan.FromMinutes(10)) continue;
                throw new TimeoutException("Owned helper exceeded ten minutes. No process killed, no retry/cleanup. PID=" + process.Id);
            }
            activeHelper = null;
            Require(Task.WaitAll(new Task[] { stdout, stderr }, 30000), "Helper output did not drain.");
            WriteNew("evidence/" + label + ".stdout.log", stdout.Result); WriteNew("evidence/" + label + ".stderr.log", stderr.Result);
            Require(process.ExitCode == 0, "Native helper failed; retained stdout/stderr. Exit=" + process.ExitCode);
        }
        string appended;
        using (var stream = new FileStream(nativeLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        { stream.Position = logOffset; using (var reader = new StreamReader(stream)) appended = reader.ReadToEnd(); }
        Require(appended.Contains("START pack=" + selected.PackId + " version=" + Release) && appended.Contains("COMPLETED packages=1 deletes=0") &&
            !Regex.IsMatch(appended, @"\b(ROLLED-BACK|FAIL|ERROR|WARNING)\b"), "Native completion log missing or reported error.");
        var state = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath));
        Require(state.PackId == selected.PackId && state.PackVersion == Release && state.Packages.Count == 1, "Wrong state identity/count; obsolete package ID not pruned.");
        var installed = state.Packages.Single();
        Require(installed.Id == package.Id && installed.Version == package.Version && Equal(installed.ArchiveSha256, package.Sha256) &&
            Equal(installed.Root, ApfPaths.PackageRoot(package)), "Installed package identity/hash mismatch.");
        var expected = files.ToDictionary(f => "GameData/" + f.Path, StringComparer.OrdinalIgnoreCase);
        Require(installed.Files.Count == 227 && new HashSet<string>(installed.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys), "State's 227-file inventory differs from comparison.");
        foreach (var entry in installed.Files)
        {
            var proof = expected[entry.Path]; string path = Owned(entry.Path);
            Require(File.Exists(path) && new FileInfo(path).Length == proof.SizeBytes && Equal(entry.Sha256, proof.Sha256) && Equal(Hash(path), proof.Sha256), "Installed content differs from official inventory: " + entry.Path);
        }
        var actual = Directory.GetFiles(Owned("GameData/KcalbelohTextures"), "*", SearchOption.AllDirectories).Select(Relative);
        Require(new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys), "Extra/missing files remain in replaced texture root.");
        Require(Present(package, installed), "Correct installed package was not recognized.");
        var other = package.Id == four.Id ? eight : four;
        Require(!Present(other, installed), "Other profile's package was incorrectly recognized.");
        // PackagePresent checks existence for texture assets, not their complete byte hashes.
        // Assert missing-file detection without claiming same-size DDS corruption detection.
        string victim = installed.Files.First().Path;
        string held = "evidence/" + label + "-missing-file-held.bin";
        File.Move(Owned(victim), Owned(held));
        try { Require(!Present(package, installed), "Missing texture file was incorrectly accepted."); }
        finally { File.Move(Owned(held), Owned(victim)); }
        Require(Present(package, installed), "Restored texture package was not recognized.");
        var profile = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(Owned(Data + "KesasInstallProfile.json")));
        Require(profile["mode"] == "full" && profile["catalogVersion"] == Release, "Isolated texture-only post-exit metadata mismatch.");
        string settings = File.ReadAllText(Owned("settings.cfg"));
        Require(NonGraphics(settings) == NonGraphics(InitialSettings), "Non-graphics player settings changed.");
        var graphics = Installer.GraphicsValues(settings);
        Require(ApfJson.Serialize(graphics) == ApfJson.Serialize(Installer.GraphicsValues(InitialSettings)), "Isolated texture fixture changed graphics settings.");
        CheckPreserved(); VerifySignedFiles();
        var result = new PhaseResult { Label = label, Mode = mode, PackageId = installed.Id, ArchiveSha256 = installed.ArchiveSha256,
            FileCount = installed.Files.Count, ExpandedBytes = files.Sum(f => f.SizeBytes), HelperSeconds = clock.Elapsed.TotalSeconds,
            AllContentHashesVerified = true, ObsoleteStatePruned = true, NoExtraTextureFiles = true,
            PackagePresentCorrect = true, WrongProfileRejected = true, MissingFileRejected = true, Preserved = true };
        Phases.Add(result); WriteNew("evidence/" + label + "-state.json", ApfJson.Serialize(state));
        SaveReport(false, null);
        Say("PASS " + label + ": 227 exact files; old package state removed; missing-file detection and unrelated data preserved.");
    }

    private static void CopyArchive(string source, ApfPackage package)
    {
        string destination = Archive(package); Directory.CreateDirectory(Path.GetDirectoryName(destination));
        Say("Staging local texture archive: " + package.Id);
        File.Copy(source, destination, false);
        Require(new FileInfo(destination).Length == package.SizeBytes && Equal(Hash(destination), package.Sha256), "Staged archive differs from signed catalog.");
    }
    private static bool Present(ApfPackage package, ApfPackageState state)
    {
        var installer = new Installer(root, new IPEndPoint(IPAddress.Parse("217.216.49.250"), 18865), Release);
        return (bool)PackagePresent.Invoke(installer, new object[] { package, state });
    }
    private static void VerifySignedFiles()
    {
        foreach (var proof in signedClient.Files)
        {
            string path = Owned(proof.Path);
            Require(File.Exists(path) && new FileInfo(path).Length == proof.SizeBytes && Equal(Hash(path), proof.Sha256), "Signed starter file differs: " + proof.Path);
        }
    }
    private static void ValidateInventory(FileProof[] files)
    {
        Require(files != null && files.Length == 227 && files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 227 &&
            files.All(f => f.Path.StartsWith("KcalbelohTextures/", StringComparison.Ordinal) && !f.Path.Contains("\\") && ApfPaths.IsSafeRelative(f.Path) && f.SizeBytes >= 0 && Regex.IsMatch(f.Sha256 ?? "", "^[0-9a-fA-F]{64}$")), "Invalid comparison inventory.");
    }
    private static void CheckPreserved()
    { foreach (var item in Preserved) Require(File.Exists(Owned(item.Key)) && Equal(Hash(Owned(item.Key)), item.Value), "Preserved fixture data changed: " + item.Key); }
    private static string NonGraphics(string text)
    {
        var keys = Installer.GraphicsValues(InitialSettings).Keys;
        return string.Join("\n", text.Replace("\r", "").Split('\n').Where(s => s.Length > 0 && !keys.Any(key => s.StartsWith(key + " =", StringComparison.Ordinal))));
    }
    private static string Archive(ApfPackage package) => Owned("APF/.staging/connect/" + package.Sha256 + ".zip");
    private static string Owned(string relative)
    {
        Require(root != null && !Path.IsPathRooted(relative), "Relative fixture path required.");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Path escapes fixture.");
        ApfPaths.EnsureNoReparsePoints(root, path); return path;
    }
    private static string Relative(string path) => path.Substring(root.Length + 1).Replace('\\', '/');
    private static void WriteNew(string relative, string content)
    {
        string path = Owned(relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))) output.Write(content);
    }
    private static string Hash(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Say(string message)
    { string line = DateTime.UtcNow.ToString("o") + " " + message; Console.WriteLine(line); log?.WriteLine(line); }
    private static void SaveReport(bool passed, string error)
    {
        var report = new { SchemaVersion = 1, Passed = passed, Status = passed ? "passed" : error == null ? "in-progress" : "failed",
            CreatedAtUtc = DateTime.UtcNow.ToString("o"), Fixture = root,
            Release, HeadlessOnly = true, ProductionPostExitHelper = true, ProcessId = 0, RelaunchTarget = "RC22 GameStub",
            NetworkDownloadsTested = false, UnityOrGameplayTested = false, FullPackInstalled = false,
            ActualCatalogTextureDeclarations = true, IsolatedTextureManifests = true, FullProfileGraphicsTested = false, StagedPackagesPerPhase = 1, ExplicitDeletions = 0,
            SameSizeDdsCorruptionDetectionClaimed = false, ActiveOwnedHelperPid = activeHelper,
            Archive4kSha256 = FourHash, Archive8kSha256 = EightHash, Phase = phase, Phases, Error = error };
        string output = (passed || error != null) && !resumeFinal ? "evidence/ProfileTests-result.json" :
            "evidence/report-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json";
        WriteNew(output, ApfJson.Serialize(report));
    }

    private static void ResumeReviewedFinal()
    {
        root=Path.Combine(work,"ProfileFixture-b4edee33df2d4d1d842a17eb6eb89c87");
        Require(File.Exists(Owned(".profile-fixture-owned")),"Exact original owned fixture required.");
        using(var guard=new FileStream(Owned("APF/.installer.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        var previous=ApfJson.Deserialize<PriorResult>(File.ReadAllText(Owned("evidence/ProfileTests-result.json")));
        Require(previous.Phase=="8k-to-4k-again" && previous.Phases.Length==3 && previous.Phases.All(p=>p.AllContentHashesVerified && p.Preserved),"Reviewed three-phase checkpoint required.");
        Phases.AddRange(previous.Phases);
        evidence=Owned("evidence");
        log=new StreamWriter(new FileStream(Owned("evidence/continued-final-"+Guid.NewGuid().ToString("N")+".log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
        VerifySignedFiles();
        catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Owned(CatalogFile)));
        four=catalog.Potato.ManagedPackages.Single(p=>p.Id==FourId);
        eight=catalog.Full.ManagedPackages.Single(p=>p.Id==EightId);
        Require(Hash(Owned("KSP_x64.exe"))==stubHash,"Only known GameStub may relaunch.");
        var state=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(Data+"APFPackState.json")));
        Require(state.Packages.Count==1 && state.Packages[0].Id==EightId,"Original eight-K checkpoint changed.");
        foreach(var file in comparison.Inventory8k)Require(Hash(Owned("GameData/"+file.Path))==file.Sha256,"Eight-K checkpoint content changed.");
        foreach(var relative in new[]{Data+"settings.xml",Data+"settings_bkp.xml",Data+"KesasInstallSelection.txt",Data+"privatekey.txt",Data+"APFHelper.key",Data+"APFServer.txt",Data+"APFTlsCertificatePins.txt",Data+"APFClientUpdatePublicKey.xml","KSP_x64.exe","saves/fixture/persistent.sfs","APF/Identity/fixture.txt","APF/preexisting-backup/sentinel.txt","GameData/UnrelatedFixture/sentinel.cfg","GameData/Squad/fixture-sentinel.txt"})Preserved.Add(relative,Hash(Owned(relative)));
        Require(File.ReadAllText(Owned("settings.cfg"))==InitialSettings,"Expected unchanged full graphics checkpoint.");
        Say("Continuing only final texture transaction. Prior three positive proofs retained. Original fake PID 0 repeated identical authorization; a fresh signed fixture argument now separates operations. No receipts removed or bypassed. Final-phase preservation baseline is explicitly re-established here.");
        Install("repeat-4k-fresh-authorization",catalog.Potato,four,comparison.Inventory4k,"potato");
    }

    private sealed class PriorResult { public string Phase {get;set;} public PhaseResult[] Phases {get;set;} }

    private sealed class Comparison
    {
        public string Archive4kSha256 { get; set; }
        public string Archive8kSha256 { get; set; }
        public int Files4k { get; set; }
        public int Files8k { get; set; }
        public int ChangedCount { get; set; }
        public string[] MissingPaths { get; set; }
        public string[] AddedPaths { get; set; }
        public object[] GameplayChanges { get; set; }
        public FileProof[] Inventory4k { get; set; }
        public FileProof[] Inventory8k { get; set; }
    }
    private sealed class FileProof
    { public string Path { get; set; } public long SizeBytes { get; set; } public string Sha256 { get; set; } }
    private sealed class PhaseResult
    {
        public string Label { get; set; } public string Mode { get; set; } public string PackageId { get; set; }
        public string ArchiveSha256 { get; set; } public int FileCount { get; set; } public long ExpandedBytes { get; set; }
        public double HelperSeconds { get; set; } public bool AllContentHashesVerified { get; set; } public bool ObsoleteStatePruned { get; set; }
        public bool NoExtraTextureFiles { get; set; } public bool PackagePresentCorrect { get; set; } public bool WrongProfileRejected { get; set; }
        public bool MissingFileRejected { get; set; } public bool Preserved { get; set; }
    }
}
