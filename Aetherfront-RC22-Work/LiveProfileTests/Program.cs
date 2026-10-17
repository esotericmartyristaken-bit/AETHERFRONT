using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ApfCore;
using KesasInstall;

// Deliberately opt-in. Build does not execute this program or make network requests.
// This exercises the HEADLESS install/update chain, not Unity, rendering, or gameplay.
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
    private static readonly List<FragmentProof> InterruptedFragments = new List<FragmentProof>();
    private static readonly FieldInfo GitHubReady = typeof(Installer).GetField("githubReady", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly string InitialSettings = "QUALITY_PRESET = 3\nANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nSYNC_VBL = 1\nLIGHT_QUALITY = 8\nSHADOWS_QUALITY = 3\nFRAMERATE_LIMIT = 120\nCELESTIAL_BODIES_CAST_SHADOWS = True\nPLANET_SCATTER = True\nPLANET_SCATTER_FACTOR = 1\nAERO_FX_QUALITY = 2\nSURFACE_FX = True\nREFLECTION_PROBE_REFRESH_MODE = 1\nREFLECTION_PROBE_TEXTURE_RESOLUTION = 1\nTERRAIN_SHADER_QUALITY = 2\nKEY_BINDING = live-profile-test-preserve\nSOUND_VOLUME = 0.37\n";
    private static string work, root, phase = "startup", stubHash;
    private static StreamWriter log;
    private static InstallCatalog catalog;
    private static ApfClientUpdateManifest feed;
    private static HashSet<string> pins, starterRoots;
    private static ProgressInfo progress;
    private static TransportAudit transport;

    private static int Main(string[] args)
    {
        if (args.Length != 2 || !args.Contains("--run-production") || !args.Contains("--ack-headless"))
        {
            Console.Error.WriteLine("NOT RUN. After review and production RC22 deployment only, from the APF workspace:\n" +
                "Aetherfront-RC22-Work\\LiveProfileTests\\bin\\Release\\net472\\LiveProfileTests.exe --run-production --ack-headless\n" +
                "Downloads real production media into one new retained GUID fixture. GameStub/empty Squad are test placeholders, not KSP/gameplay.");
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
                Run();
                Say("PASS HEADLESS production install/update chain only. Unity scenes/rendering/gameplay NOT verified.");
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

    private static void Run()
    {
        work = Path.GetFullPath("Aetherfront-RC22-Work");
        Require(Directory.Exists(Path.Combine(work, "LiveProfileTests")), "Run from the APF workspace root.");
        Require(new DriveInfo(Path.GetPathRoot(work)).DriveType == DriveType.Fixed, "Fixture must use a local fixed disk, never a redirected/UNC drive.");
        ApfPaths.EnsureNoReparsePoints(work, work);
        string starter = Path.GetFullPath("client-artifacts/Aetherfront-RC22/Aetherfront-Starter-rc22.zip");
        ApfPaths.EnsureNoReparsePoints(Path.GetDirectoryName(starter), starter);
        string expectedStarterHash = File.ReadAllText(starter + ".sha256").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0];
        Require(Equal(ApfJson.Sha256File(starter), expectedStarterHash), "Public starter checksum mismatch.");
        using (var zip = ZipFile.OpenRead(starter))
        {
            catalog = ApfJson.Deserialize<InstallCatalog>(ReadEntry(zip, CatalogPath));
            ValidateCatalog();
            Require(ReadEntry(zip, DataPath + "APFServer.txt").Trim() == Endpoint, "Starter is not configured for the production endpoint.");
            foreach (var assembly in new[] { typeof(Installer).Assembly, typeof(ApfJson).Assembly, typeof(JsonFx.Json.JsonReader).Assembly })
            {
                string name = Path.GetFileName(assembly.Location);
                using (var input = Entry(zip, "GameData/LunaMultiplayer/Plugins/" + name).Open())
                    Require(Equal(Hash(input), ApfJson.Sha256File(assembly.Location)), "Loaded assembly is not the exact shipped starter binary: " + name);
            }
            long expanded = zip.Entries.Sum(e => e.Length);
            var all = UnionPackages();
            long compressed = all.Sum(p => p.SizeBytes);
            long content = all.Sum(p => catalog.Sizes.Single(s => Equal(s.Id, p.Id)).ExpandedBytes);
            // Cache + two expanded generations (installation/recovery) + concurrent chunk assembly + 2 GiB headroom.
            SpaceGuard(checked(compressed + 2 * content + all.Max(p => p.SizeBytes) + expanded + 2 * GiB), "complete retained fixture budget");
            starterRoots = new HashSet<string>(zip.Entries.Where(e => e.FullName.StartsWith("GameData/", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName.Substring(9).Split('/')[0]).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        }
        Require(GitHubReady != null, "Shipped githubReady diagnostic field missing; route verification cannot be performed.");
        string stub = Path.Combine(work, "GameStub/bin/Release/net472/GameStub.exe");
        ApfPaths.EnsureNoReparsePoints(work, stub);
        Require(File.Exists(stub), "Build the existing GameStub first; this harness never builds source runtimes.");
        root = Path.Combine(work, "LiveProfileFixture-" + Guid.NewGuid().ToString("N"));
        Require(!Directory.Exists(root) && !File.Exists(root), "Fresh fixture path already exists.");
        Directory.CreateDirectory(root);
        using (new FileStream(Owned(".live-profile-owned"), FileMode.CreateNew, FileAccess.Write)) { }
        log = new StreamWriter(new FileStream(Owned("LiveProfileTests.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Say("Fixture: " + root);
        Say("Harness priority: BelowNormal; native helpers also run BelowNormal. No TLS/network configuration is changed.");
        Say("HEADLESS ONLY: GameStub.exe copied as KSP_x64.exe and empty GameData/Squad are test placeholders. No real KSP is installed or launched.");
        using (var heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)))
        {
            SetPhase("extracting unmodified public configured starter");
            ZipFile.ExtractToDirectory(starter, root);
            Require(!File.Exists(Owned("KSP_x64.exe")) && !Directory.Exists(Owned("GameData/Squad")), "Starter unexpectedly contains a game installation.");
            Directory.CreateDirectory(Owned("GameData/Squad"));
            File.Copy(stub, Owned("KSP_x64.exe"), false);
            stubHash = ApfJson.Sha256File(stub);
            pins = new HashSet<string>(File.ReadAllLines(Owned(DataPath + "APFTlsCertificatePins.txt")).Select(s => s.Trim()).Where(s => s.Length != 0), StringComparer.OrdinalIgnoreCase);
            Require(pins.Count > 0 && pins.Count <= 8 && pins.All(IsHash), "Invalid shipped production TLS pins.");
            Require(ServicePointManager.ServerCertificateValidationCallback == null, "Unexpected global TLS override; refusing execution.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            transport = new TransportAudit(catalog.GitHub);
            Require(WebRequest.RegisterPrefix("https://", transport), "Cannot observe actual HTTPS request creation; refusing unproven routes.");
            Require(WebRequest.RegisterPrefix("http://", new RefuseHttp()), "Cannot reject plaintext transport.");
            Preflight("initial");
            foreach (string name in new[] { "APFServer.txt", "APFTlsCertificatePins.txt", "APFClientUpdatePublicKey.xml" })
                Remember(DataPath + name);
            WriteNew(DataPath + "privatekey.txt", "headless-test-identity-" + Guid.NewGuid().ToString("N") + "\n");
            WriteNew("saves/LiveProfileTest/persistent.sfs", "// HEADLESS TEST PLACEHOLDER, not a playable KSP save\n" + Guid.NewGuid().ToString("N"));
            WriteNew("settings.cfg", InitialSettings);
            WriteNew(DataPath + "KesasInstallSelection.txt", "potato\n");
            foreach (string relative in new[] { DataPath + "privatekey.txt", "saves/LiveProfileTest/persistent.sfs", DataPath + "KesasInstallSelection.txt" }) Remember(relative);
            Require(!Directory.Exists(Owned("APF/.staging")), "New fixture does not have an empty package cache.");
            Say("Route proof: fresh cache; request creation/response diagnostics; githubReady=true; no fallback warnings; archive SHA-256/size from signed catalog. Missing evidence fails.");

            InterruptFreshDownload();
            Transition("potato", new HashSet<string>(catalog.Potato.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase), "fresh-potato");
            Reconnect("potato", "potato-reconnect");
            var sharedBefore = SharedSnapshot();
            var potatoIds = new HashSet<string>(catalog.Potato.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            var fullDelta = new HashSet<string>(catalog.Full.ManagedPackages.Where(p => !potatoIds.Contains(p.Id)).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            Require(fullDelta.Count > 0, "Catalog has no real full-profile delta.");
            Transition("full", fullDelta, "potato-to-full");
            Require(SharedSnapshot() == sharedBefore, "Shared package content/state changed during the full switch.");
            Reconnect("full", "full-reconnect");
            var fullIds = new HashSet<string>(catalog.Full.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            var potatoDelta = new HashSet<string>(catalog.Potato.ManagedPackages.Where(p => !fullIds.Contains(p.Id)).Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            Transition("potato", potatoDelta, "full-to-potato");
            Require(SharedSnapshot() == sharedBefore, "Shared package content/state changed on return to potato.");
            Reconnect("potato", "final-potato-reconnect");
            Preflight("final");
            CheckPreserved();
            transport.WriteSummary("final");
        }
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
        WriteNew("evidence/" + label + "-signed-feed.json", feed.ToJson());
        Say("PASS " + label + ": live signed RC22 feeds agree; exact shipped client/catalog/helper verified; pinned production identity matches.");
    }

    private static ApfClientUpdateManifest ReadSignedFeed(string json)
    {
        var manifest = ApfClientUpdateManifest.FromJson(json);
        var validation = ApfClientUpdateValidator.Validate(manifest);
        Require(validation.IsValid, string.Join("; ", validation.Errors));
        Require(manifest.Version == Version && manifest.KspVersion == "1.12.5", "Live signed feed is not activated RC22; deploy before running.");
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
            // Identical trust policy to shipped Installer: exact production certificate pin,
            // or ordinary error-free GitHub TLS. No global TLS callback or trust bypass.
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
                var buffer = new byte[65536]; int count;
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
        SetPhase(label + ": actual Installer.Run(\"" + mode + "\") with live updates enabled");
        CheckPreserved(); VerifySignedClient();
        installer = new Installer(root, new IPEndPoint(IPAddress.Parse("217.216.49.250"), 18865), Version);
        installer.Confirm = message => { throw new InvalidDataException("Unexpected extra mods/confirmation in owned clean fixture: " + message); };
        installer.Progress = status => { lock (ProgressGate) phase = label + ": " + status; };
        installer.Report = value => { lock (ProgressGate) progress = value; };
        var clock = Stopwatch.StartNew();
        installer.IsCanceled = () => clock.Elapsed > TimeSpan.FromHours(12);
        requestStart = transport.Count;
        var result = installer.Run(mode); // Never pass checkUpdates=false.
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

    private static void InterruptFreshDownload()
    {
        const long threshold = 32L * 1024 * 1024;
        SetPhase("fresh GitHub interruption: cancel after 32 MiB aggregate; no native helper");
        string before = InstalledSnapshot();
        int cancelRequested = 0, start = transport.Count;
        long observed = 0;
        var clock = Stopwatch.StartNew();
        var installer = new Installer(root, new IPEndPoint(IPAddress.Parse("217.216.49.250"), 18865), Version);
        installer.Confirm = message => { throw new InvalidDataException("Unexpected interruption confirmation: " + message); };
        installer.Progress = status => { lock (ProgressGate) phase = "interrupt: " + status; };
        installer.Report = value =>
        {
            lock (ProgressGate) progress = value;
            // Avoid canceling after an entire small archive already completed: the
            // test must retain a genuinely incomplete package, not only cached ZIPs.
            if (value.Transferring && value.Downloaded >= threshold && value.PackageDownloaded < value.PackageTotal)
            {
                Interlocked.Exchange(ref observed, value.Downloaded);
                Interlocked.Exchange(ref cancelRequested, 1);
            }
        };
        installer.IsCanceled = () => Volatile.Read(ref cancelRequested) != 0 || clock.Elapsed > TimeSpan.FromHours(2);
        bool canceled = false;
        try { installer.Run("potato"); }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled && cancelRequested != 0 && observed >= threshold, "Did not perform the intentional real GitHub interruption after 32 MiB.");
        Require((bool)GitHubReady.GetValue(installer), "Interrupted download did not use a validated GitHub channel.");
        Require(!File.Exists(Owned("APF/LastDownloadWarning.log")), "Fallback/transport warning during interruption; route proof rejected.");
        Require(InstalledSnapshot() == before, "Canceled real download changed installed folders/settings/player data.");
        Require(!File.Exists(Owned(DataPath + "APFPackState.json")) && !File.Exists(Owned(DataPath + "KesasInstallProfile.json")), "Canceled staging marked an installation complete.");
        var requests = transport.Since(start);
        Require(!requests.Any(r => r.Uri.Host == Origin.Host && r.Uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "Interrupted download attempted origin archive fallback.");
        foreach (var package in catalog.Potato.ManagedPackages)
        {
            string archive = ArchivePath(package), part = archive + ".part";
            if (File.Exists(archive))
            {
                VerifyArchive(package, archive);
                transport.ProveArchive(package, start, false);
            }
            var fragments = new List<FragmentProof>();
            if (File.Exists(part) && new FileInfo(part).Length > 0)
                fragments.Add(new FragmentProof { Package = package, Path = part, Start = 0, Length = new FileInfo(part).Length });
            foreach (string path in Directory.GetFiles(Path.GetDirectoryName(part), Path.GetFileName(part) + ".range-*"))
            {
                long length = new FileInfo(path).Length;
                if (length == 0) continue;
                string[] limits = path.Substring((part + ".range-").Length).Split('-');
                Require(limits.Length == 2, "Unexpected retained chunk path.");
                long first = long.Parse(limits[0], CultureInfo.InvariantCulture), last = long.Parse(limits[1], CultureInfo.InvariantCulture);
                Require(first >= 0 && last >= first && last < package.SizeBytes && length <= last - first + 1, "Invalid retained chunk bounds.");
                fragments.Add(new FragmentProof { Package = package, Path = path, Start = first, Length = length });
            }
            if (fragments.Count == 0) continue;
            long retained = fragments.Sum(f => f.Length);
            Require(retained > 0 && retained < package.SizeBytes, "Interruption did not leave an incomplete package.");
            var mirror = catalog.GitHub.Media(Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath));
            Require(requests.Any(r => r.Uri == mirror && r.Request.HaveResponse), "Partial cache has no actual GitHub request/response evidence.");
            Require(requests.Any(r => r.Uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) && r.Request.HaveResponse), "Partial cache has no GitHub CDN response evidence.");
            InterruptedFragments.AddRange(fragments);
        }
        Require(InterruptedFragments.Count > 0, "No nonempty partial archive/chunks survived intentional interruption.");
        // Installer.Run intentionally records cancellation as LastDownloadError.log.
        // Retain that expected error separately; subsequent stages must have no errors.
        string errorPath = Owned("APF/LastDownloadError.log");
        Require(File.Exists(errorPath) && File.ReadAllText(errorPath).Contains("OperationCanceledException"), "Missing expected cancellation diagnostic.");
        File.Move(errorPath, Owned("evidence/interrupted-expected-cancellation.log"));
        WriteNew("evidence/interrupted-fragments.json", ApfJson.Serialize(InterruptedFragments.Select(f => new { package = f.Package.Id, path = Relative(f.Path), start = f.Start, length = f.Length }).ToList()));
        transport.WriteSummary("interrupted");
        CheckPreserved();
        Say("PASS real GitHub interruption: observed=" + observed + " bytes; retained=" + InterruptedFragments.Sum(f => f.Length) + " bytes; installed folders unchanged. New Installer must prove range reuse and final SHA-256.");
    }

    private static string InstalledSnapshot()
    {
        var files = Directory.GetFiles(Owned("GameData"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Owned("saves"), "*", SearchOption.AllDirectories)).Concat(new[] { Owned("settings.cfg"), Owned("KSP_x64.exe") });
        return string.Join("\n", files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Relative(p) + "|" + new FileInfo(p).Length + "|" + ApfJson.Sha256File(p)));
    }

    private static void ProveResume(int start)
    {
        foreach (var group in InterruptedFragments.GroupBy(f => f.Package.Id))
        {
            var package = group.First().Package;
            var mirror = catalog.GitHub.Media(Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath));
            var requests = transport.Since(start).Where(r => r.Uri == mirror).ToArray();
            Require(requests.Any(r => r.Request.HaveResponse), "Resumed archive has no actual GitHub response.");
            foreach (var request in requests)
            {
                string range = request.Request.Headers[HttpRequestHeader.Range];
                Require(range != null && range.StartsWith("bytes=", StringComparison.Ordinal), "Resume restarted without a byte range.");
                string[] limits = range.Substring(6).Split('-');
                Require(limits.Length == 2, "Unexpected resume Range header.");
                long first = long.Parse(limits[0], CultureInfo.InvariantCulture);
                long last = limits[1].Length == 0 ? package.SizeBytes - 1 : long.Parse(limits[1], CultureInfo.InvariantCulture);
                foreach (var fragment in group)
                    Require(last < fragment.Start || first >= fragment.Start + fragment.Length, "Resume re-requested already retained GitHub bytes.");
            }
            Require(group.All(f => !File.Exists(f.Path)), "Verified resumed archive left uncleared retained fragments.");
            Require(Equal(ApfJson.Sha256File(ArchivePath(package)), package.Sha256) && new FileInfo(ArchivePath(package)).Length == package.SizeBytes, "Resumed archive failed full SHA-256/size.");
            Say("PASS GitHub range resume: " + package.Id + "; reused=" + group.Sum(f => f.Length) + " bytes; actual Range headers exclude cached bytes; complete signed-catalog SHA-256/size verified.");
        }
    }

    private static void Transition(string mode, HashSet<string> expectedIds, string label)
    {
        var selected = Manifest(mode);
        var beforeCache = CacheSnapshot();
        var needed = selected.ManagedPackages.Where(p => expectedIds.Contains(p.Id)).ToList();
        long expanded = needed.Sum(p => catalog.Sizes.Single(s => Equal(s.Id, p.Id)).ExpandedBytes);
        long missing = needed.Where(p => !File.Exists(ArchivePath(p))).Sum(p => p.SizeBytes);
        SpaceGuard(checked(missing + 2 * expanded + (needed.Count == 0 ? 0 : needed.Max(p => p.SizeBytes)) + GiB), label);
        var result = RunInstaller(mode, label, out var installer, out int start);
        Require(result.Changed && !string.IsNullOrEmpty(result.TransactionPath), "Expected a profile transaction: " + label);
        var transaction = ReadTransaction(result.TransactionPath);
        Require(new HashSet<string>(transaction.Packages.Select(s => s.Package.Id), StringComparer.OrdinalIgnoreCase).SetEquals(expectedIds) && transaction.Packages.Count == expectedIds.Count, "Installer staged more/less than the actual profile delta: " + label);
        ValidateTransaction(transaction, selected);
        foreach (var staged in transaction.Packages)
        {
            var package = selected.ManagedPackages.Single(p => Equal(p.Id, staged.Package.Id));
            Require(SamePath(staged.ArchivePath, ArchivePath(package)), "Unexpected cache path.");
            VerifyArchive(package, staged.ArchivePath);
            bool cached = beforeCache.ContainsKey(package.Sha256 + ".zip");
            transport.ProveArchive(package, start, cached);
        }
        if (label == "fresh-potato") ProveResume(start);
        AssertOldCache(beforeCache);
        // Keep the original engine authorization as evidence before the explicit local helper edit.
        WriteNew("evidence/" + label + "-original-transaction.json", File.ReadAllText(result.TransactionPath));
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
        Require(Equal(ApfJson.Sha256File(Owned("KSP_x64.exe")), stubHash), "Relaunch target is not the test placeholder.");
        transaction.ProcessId = 0;
        transaction.KspArguments = new List<string>();
        var key = ApfTransactionSecurity.LoadKey(Owned(DataPath + "APFHelper.key"));
        ApfTransactionSecurity.Sign(transaction, key);
        File.WriteAllText(path, ApfJson.Serialize(transaction)); // Only this new fixture's transaction is changed.
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
            // Drain both streams concurrently, including long full-profile extraction.
            var stdout = Task.Run(() => CopyOutput(process.StandardOutput, "evidence/" + label + "-helper.stdout.log"));
            var stderr = Task.Run(() => CopyOutput(process.StandardError, "evidence/" + label + "-helper.stderr.log"));
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
        using (var input = File.OpenRead(nativeLogPath))
        {
            input.Position = previousLogLength;
            using (var reader = new StreamReader(input)) transactionLog = reader.ReadToEnd();
        }
        Require(transactionLog.Contains("COMPLETED packages=" + transaction.Packages.Count + " deletes=" + transaction.Deletes.Count) && !transactionLog.Contains("ROLLED-BACK"), "Missing this native transaction's completion or observed rollback.");
    }

    private static void CopyOutput(StreamReader input, string relative)
    {
        using (var output = new StreamWriter(NewFile(relative))) { char[] buffer = new char[4096]; int count; while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { output.Write(buffer, 0, count); output.Flush(); } }
    }

    private static void VerifyArchive(ApfPackage package, string path)
    {
        SetPhase("verifying archive and every entry: " + package.Id);
        Require(new FileInfo(path).Length == package.SizeBytes && Equal(ApfJson.Sha256File(path), package.Sha256), "Downloaded archive SHA-256/size mismatch: " + package.Id);
        ApfArchiveInstaller.ValidateArchive(path, package, Owned("GameData"));
        var files = new Dictionary<string, FileProof>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
            {
                // Match shipped archive mapping; public ValidateArchive above enforces safety.
                string normalized = entry.FullName.Replace('\\', '/').Trim('/');
                string prefix = ApfPaths.Normalize(package.StripPrefix);
                string destination = ApfPaths.NormalizeGameDataRelative(package.Destination);
                string relative = destination.Length == 0 ? normalized : normalized.Substring(prefix.Length + 1);
                string output = "GameData/" + (destination.Length == 0 ? "" : destination + "/") + relative;
                Owned(output);
                using (var input = entry.Open()) files.Add(output, new FileProof { Size = entry.Length, Sha256 = Hash(input) });
            }
        }
        Require(files.Count > 0 && files.Values.Sum(f => f.Size) == catalog.Sizes.Single(s => Equal(s.Id, package.Id)).ExpandedBytes, "Expanded catalog size/entry inventory mismatch: " + package.Id);
        Inventories[package.Id] = files;
        ArchiveHashes[package.Sha256 + ".zip"] = package.Sha256;
        WriteNew("evidence/archive-" + package.Id + "-" + Guid.NewGuid().ToString("N") + ".json", ApfJson.Serialize(files));
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
                Require(File.Exists(target) && new FileInfo(target).Length == proof.Size && Equal(file.Sha256, proof.Sha256) && Equal(ApfJson.Sha256File(target), proof.Sha256), "Installed file SHA-256/size differs from state and verified archive: " + file.Path);
                fileCount++; bytes = checked(bytes + proof.Size);
            }
            string packageRoot = Owned("GameData/" + ApfPaths.PackageRoot(package));
            var actual = File.Exists(packageRoot) ? new[] { packageRoot } : Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories);
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
        WriteNew("evidence/" + label + "-state.json", ApfJson.Serialize(state));
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
            Require(File.Exists(path) && new FileInfo(path).Length == file.SizeBytes && Equal(ApfJson.Sha256File(path), file.Sha256), "Shipped client file differs from live signed RC22 inventory: " + file.Path);
        }
    }

    private static Dictionary<string, string> CacheSnapshot()
    {
        string directory = Owned("APF/.staging/connect");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        foreach (string path in Directory.GetFiles(directory, "*.zip"))
        {
            string name = Path.GetFileName(path), hash = ApfJson.Sha256File(path);
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
    private static void Remember(string relative) => Preserved.Add(relative, ApfJson.Sha256File(Owned(relative)));
    private static void CheckPreserved()
    {
        foreach (var item in Preserved) Require(File.Exists(Owned(item.Key)) && Equal(ApfJson.Sha256File(Owned(item.Key)), item.Value), "Identity/save/dropdown/trust configuration changed: " + item.Key);
        if (stubHash != null) Require(Equal(ApfJson.Sha256File(Owned("KSP_x64.exe")), stubHash), "GameStub placeholder was replaced.");
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
    private static FileStream NewFile(string relative) { string path = Owned(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
    private static void WriteNew(string relative, string text) { using (var output = new StreamWriter(NewFile(relative), new UTF8Encoding(false))) output.Write(text); }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name) { var entry = zip.GetEntry(name); Require(entry != null, "Starter entry missing: " + name); return entry; }
    private static string ReadEntry(ZipArchive zip, string name) { using (var input = new StreamReader(Entry(zip, name).Open())) return input.ReadToEnd(); }
    private static string Hash(Stream input) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
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
    private sealed class FragmentProof { public ApfPackage Package; public string Path; public long Start, Length; }
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
            // No HttpWebRequest subclass/wrapper. CreateDefault resolves the built-in
            // https: scheme registration, not this longer https:// diagnostic prefix.
            // Return the original request unchanged; engine controls TLS, ranges, redirects.
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
            Require(originRequests.Length == 0, "Origin archive fallback attempted: " + asset);
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
            WriteNew("evidence/" + label + "-transport.log", string.Join(Environment.NewLine, lines));
            Say("Transport evidence: " + Owned("evidence/" + label + "-transport.log"));
        }
        private static string SafeUri(Uri uri) => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length == 0 ? "" : "?[redacted]");
    }
}
