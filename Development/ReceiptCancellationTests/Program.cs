using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ApfCore;
using KesasInstall;

internal static class Program
{
    const string Version = "kesas-rc28";
    static string root, web;
    static int checks;
    static int Main(string[] args)
    {
        if(args.Length==0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APF_RECEIPT_ARGUMENTS")))
            args=Environment.GetEnvironmentVariable("APF_RECEIPT_ARGUMENTS").Split('\n');
        string plugins = Environment.GetEnvironmentVariable("APF_TEST_PLUGINS");
        string engine = Environment.GetEnvironmentVariable("APF_TEST_ENGINE");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            string file = name == "KesasInstall.dll" ? engine : Path.Combine(plugins, name);
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); checks++; Console.WriteLine("PASS " + message); }
    static string Put(string relative, string text)
    {
        string file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, text); return file;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        if (args.Length != 5 || (args[0] != "create" && args[0] != "probe" && args[0] != "atomic"))
            throw new ArgumentException("create|probe <new-owned-fixture> <web-root> <test-private-key> <test-certificate-sha256>");
        root = Path.GetFullPath(args[1]); web = Path.GetFullPath(args[2]);
        string parent = Path.GetFullPath(Environment.GetEnvironmentVariable("APF_TEST_FIXTURE_PARENT")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !web.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Fixture and web roots must be inside their explicit disposable parent.");
        if (args[0] == "create") { Create(args[3], args[4]); return 0; }
        AtomicReplacement(); if(args[0]!="atomic")Probe(args[3]); return 0;
    }
    static void AtomicReplacement()
    {
        var type = typeof(Installer).Assembly.GetType("KesasInstall.AtomicFiles", false);
        if (type == null) return; // Original RC28 negative control lacks this fix.
        var replace = type.GetMethod("Replace", BindingFlags.NonPublic | BindingFlags.Static);
        string source = Path.Combine(root, "atomic-source.txt"), destination = Path.Combine(root, "atomic-destination.txt");
        File.WriteAllText(source, "new"); File.WriteAllText(destination, "old");
        var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(120); held.Dispose(); });
        release.Start();
        try { replace.Invoke(null, new object[] { source, destination, null }); } finally { release.Join(); }
        Check(File.ReadAllText(destination) == "new" && !File.Exists(source), "Transient sharing lock completes through atomic replacement after reader exits");
        File.WriteAllText(source, "next");
        bool rejected = false; var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var permanent = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { replace.Invoke(null, new object[] { source, destination, null }); }
            catch (TargetInvocationException error) { if (!(error.InnerException is IOException)) throw; rejected = true; }
        Check(rejected && watch.ElapsedMilliseconds < 2000, "Persistent sharing lock fails within a bounded interval");
        Check(File.ReadAllText(source) == "next" && File.ReadAllText(destination) == "new", "Failed atomic replacement preserves both files without delete/copy fallback");
    }
    static ApfPackage Package(string name)
    {
        string file = Put("GameData/" + name + "/part.cfg", "PART { name = " + name + " }\n");
        string archive = Path.Combine(web, name + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntryFromFile(file, name + "/part.cfg");
        return new ApfPackage { Id = name.ToLowerInvariant(), Version = "v1", ArchiveUrl = "https://127.0.0.1:28876/" + name + ".zip",
            Sha256 = ApfJson.Sha256File(archive), SizeBytes = new FileInfo(archive).Length, StripPrefix = name };
    }
    static ApfPackManifest Manifest(params ApfPackage[] packages)
    {
        return new ApfPackManifest { ServerId = "controller-receipt-fixture", PackId = "receipt-fixture", PackVersion = Version,
            RequiredClientVersion = Version, KspVersion = "1.12.5", ProtectedRoots = new List<string> { "Squad", "LunaMultiplayer" },
            ManagedPackages = packages.ToList() };
    }
    static void Create(string privateKey, string pin)
    {
        Check(!Directory.Exists(root) && !Directory.Exists(web), "Fixture paths are new; existing installations cannot be overwritten");
        Directory.CreateDirectory(root); Directory.CreateDirectory(web); Directory.CreateDirectory(Path.Combine(root, "GameData/Squad"));
        string installedPlugins=Path.Combine(root,"GameData/LunaMultiplayer/Plugins");
        Directory.CreateDirectory(installedPlugins);
        foreach(string name in new[]{"LmpClient.dll","ApfCore.dll","KesasInstall.dll"})
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("APF_TEST_PLUGINS"),name),Path.Combine(installedPlugins,name));
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(root, "KSP_x64.exe"));
        var shared = Package("FixtureShared"); var visual = Package("FixtureVisual");
        var full = Manifest(shared, visual); var potato = Manifest(shared);
        var catalog = new InstallCatalog { Version = Version, Full = full, Potato = potato, GitHub = new GitHubSettings { Enabled = false },
            Sizes = new List<PackageSize> { new PackageSize { Id = shared.Id, ExpandedBytes = 4096 }, new PackageSize { Id = visual.Id, ExpandedBytes = 4096 } } };
        string catalogFile = Put("GameData/LunaMultiplayer/Launcher/install-catalog.json", ApfJson.Serialize(catalog));
        Put("GameData/LunaMultiplayer/Data/APFClient.json", "{\"enabled\":true}");
        Put("GameData/LunaMultiplayer/Data/APFServer.txt", "127.0.0.1:28875\n");
        Put("GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt", "https://127.0.0.1:28876/updates/APFClientUpdate.json\n");
        Put("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt", pin + "\n");
        using (var key = ApfClientUpdateSecurity.LoadXmlKey(File.ReadAllText(privateKey)))
            Put("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml", ApfClientUpdateSecurity.ExportXml(key, false));
        ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(root, "GameData/LunaMultiplayer/Data/APFHelper.key"));
        var state = new ApfPackState { PackId = full.PackId, PackVersion = Version };
        foreach (var package in full.ManagedPackages) state.Packages.Add(new ApfPackageState { Id = package.Id, Version = package.Version,
            ArchiveSha256 = package.Sha256, Root = package.StripPrefix, Files = new List<ApfFileState> {
                new ApfFileState { Path = "GameData/" + package.StripPrefix + "/part.cfg", Sha256 = ApfJson.Sha256File(Path.Combine(root, "GameData", package.StripPrefix, "part.cfg")) } } });
        Put("GameData/LunaMultiplayer/Data/APFPackState.json", ApfJson.Serialize(state));
        Put("GameData/LunaMultiplayer/Data/KesasInstallProfile.json", "{\"mode\":\"full\",\"catalogVersion\":\"" + Version + "\"}");
        Put("settings.cfg", "ANTI_ALIASING = 4\nKEY_BINDING = preserved\n");
        string updates = Path.Combine(web, "updates"); Directory.CreateDirectory(updates);
        string archive = Path.Combine(updates, "APFClient-" + Version + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntryFromFile(catalogFile, "GameData/LunaMultiplayer/Launcher/install-catalog.json");
        var feed = new ApfClientUpdateManifest { SchemaVersion = 2, Version = Version, KspVersion = "1.12.5", PackageUrl = "https://127.0.0.1:28876/updates/" + Path.GetFileName(archive),
            Sha256 = ApfJson.Sha256File(archive), SizeBytes = new FileInfo(archive).Length, Files = new List<ApfClientUpdateFile> {
                new ApfClientUpdateFile { Path = "GameData/LunaMultiplayer/Launcher/install-catalog.json", Sha256 = ApfJson.Sha256File(catalogFile), SizeBytes = new FileInfo(catalogFile).Length } } };
        using (var key = ApfClientUpdateSecurity.LoadXmlKey(File.ReadAllText(privateKey))) ApfClientUpdateSecurity.Sign(feed, key);
        var validation = ApfClientUpdateValidator.Validate(feed);
        Check(validation.IsValid, "Fixture signed feed validates: " + string.Join("; ", validation.Errors));
        File.WriteAllText(Path.Combine(updates, "APFClientUpdate.json"), feed.ToJson());
        Put("APF/.client-update-staging/connect/feed.json", feed.ToJson());
        File.WriteAllText(Path.Combine(web, "APFPack.json"), full.ToJson());
        Installer.WriteCompletedReceipt(new ApfSyncTransaction { GameRoot = root, Manifest = full }, "full");
        Check(ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json")))["status"] == "complete", "Normal authenticated writer creates baseline completion proof");
        Console.WriteLine("Fixture created; no KSP/server process launched and no production state used.");
    }
    static Installer InstallerFor(List<string> progress)
    {
        return new Installer(root, new IPEndPoint(IPAddress.Loopback, 28875), Version) { Confirm = text => false, Report = value => progress.Add(value.Status ?? "") };
    }
    static void Probe(string privateKey)
    {
        var progress = new List<string>(); var first = InstallerFor(progress).Run("full");
        Check(!first.Changed && !progress.Any(x => x.StartsWith("Checking package ")), "Completed same-profile baseline uses authenticated shortcut");
        string before = File.ReadAllText(Path.Combine(root, "APF/LastInstallResult.json"));
        string installed = File.ReadAllText(Path.Combine(root, "GameData/FixtureVisual/part.cfg"));
        progress.Clear(); var changing = InstallerFor(progress); var prepared = changing.Run("potato");
        Check(prepared.Changed && File.Exists(prepared.TransactionPath), "Normal other-profile preparation completes before any apply");
        changing.Cancel();
        Check(File.ReadAllText(Path.Combine(root,"GameData/FixtureVisual/part.cfg")) == installed, "Cancel before apply leaves installed optional content unchanged");
        string after = File.ReadAllText(Path.Combine(root, "APF/LastInstallResult.json"));
        bool proofPreserved = before == after;
        progress.Clear(); var retry = InstallerFor(progress).Run("full");
        bool firstRetryScanned = progress.Any(x => x.StartsWith("Checking package "));
        progress.Clear(); var secondRetry = InstallerFor(progress).Run("full");
        bool secondRetryScanned = progress.Any(x => x.StartsWith("Checking package "));
        File.WriteAllText(Path.Combine(root, "cancellation-result.json"), ApfJson.Serialize(new { proofPreserved, firstRetryScanned, secondRetryScanned,
            installedContentPreserved = File.ReadAllText(Path.Combine(root,"GameData/FixtureVisual/part.cfg")) == installed,
            firstRetryChanged = retry.Changed, secondRetryChanged = secondRetry.Changed, checks, unityGameplayTested = false }));
        Check(proofPreserved, "Cancel before apply preserves the still-valid installed completion proof");
        Check(!retry.Changed && !secondRetry.Changed && !firstRetryScanned && !secondRetryScanned, "Return to the unchanged installed profile avoids repeated full scans");
        string feedPath = Path.Combine(web,"updates/APFClientUpdate.json"), packPath = Path.Combine(web,"APFPack.json");
        string oldFeed = File.ReadAllText(feedPath), oldPack = File.ReadAllText(packPath);
        var nextCatalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(root,"GameData/LunaMultiplayer/Launcher/install-catalog.json")));
        nextCatalog.Version = "kesas-rc29";
        foreach(var profile in new[]{nextCatalog.Full,nextCatalog.Potato}) { profile.PackVersion="kesas-rc29"; profile.RequiredClientVersion="kesas-rc29"; }
        string nextArchive = Path.Combine(web,"updates/APFClient-kesas-rc29.zip");
        using(var zip=ZipFile.Open(nextArchive,ZipArchiveMode.Create))
            using(var writer=new StreamWriter(zip.CreateEntry("GameData/LunaMultiplayer/Launcher/install-catalog.json").Open())) writer.Write(ApfJson.Serialize(nextCatalog));
        byte[] catalogBytes;
        using(var zip=ZipFile.OpenRead(nextArchive)) using(var stream=zip.Entries[0].Open()) using(var buffer=new MemoryStream()) { stream.CopyTo(buffer); catalogBytes=buffer.ToArray(); }
        string catalogHash;
        using(var hash=SHA256.Create()) catalogHash=BitConverter.ToString(hash.ComputeHash(catalogBytes)).Replace("-","").ToLowerInvariant();
        var nextFeed=new ApfClientUpdateManifest {SchemaVersion=2,Version="kesas-rc29",KspVersion="1.12.5",PackageUrl="https://127.0.0.1:28876/updates/APFClient-kesas-rc29.zip",Sha256=ApfJson.Sha256File(nextArchive),SizeBytes=new FileInfo(nextArchive).Length,Files=new List<ApfClientUpdateFile>{new ApfClientUpdateFile{Path="GameData/LunaMultiplayer/Launcher/install-catalog.json",Sha256=catalogHash,SizeBytes=catalogBytes.Length}}};
        using(var key=ApfClientUpdateSecurity.LoadXmlKey(File.ReadAllText(privateKey))) ApfClientUpdateSecurity.Sign(nextFeed,key);
        try {
            File.WriteAllText(feedPath,nextFeed.ToJson()); File.WriteAllText(packPath,nextCatalog.Full.ToJson());
            var updating=InstallerFor(new List<string>()); var preparedClient=updating.Run("full");
            Check(preparedClient.Changed && File.Exists(preparedClient.TransactionPath),"New signed client version prepares normally and cannot reuse the installed-version shortcut");
            updating.Cancel();
            Check(File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json"))==before,"Cancelling a prepared client self-update preserves the installed completion proof");
        } finally {File.WriteAllText(feedPath,oldFeed);File.WriteAllText(packPath,oldPack);}
        progress.Clear(); var original=InstallerFor(progress).Run("full");
        Check(!original.Changed && !progress.Any(x=>x.StartsWith("Checking package ")),"Old installed target reconnects without a full scan after client-update cancellation");
        var rejected = InstallerFor(new List<string>()).Run("potato");
        var transaction = ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(rejected.TransactionPath));
        transaction.Authorization = "invalid-test-authorization";
        File.WriteAllText(rejected.TransactionPath, ApfJson.Serialize(transaction));
        bool refused = false;
        try { using (NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"), rejected.TransactionPath, "pack")) { } }
        catch (InvalidDataException) { refused = true; }
        Check(refused, "Native helper refuses an unauthorized transaction before installed mutation");
        var failed = ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json")));
        Check(failed["status"] == "failed", "Actual helper entry/failure invalidates the old completion proof");
        progress.Clear(); var revalidated = InstallerFor(progress).Run("full");
        Check(!revalidated.Changed && progress.Any(x => x.StartsWith("Checking package ")), "Failed proof requires full installed-content validation");
        progress.Clear(); var final = InstallerFor(progress).Run("full");
        Check(!final.Changed && progress.Any(x => x.StartsWith("Checking package ")), "A failed apply cannot gain completion proof from content validation alone");
        Check(File.ReadAllText(Path.Combine(root,"GameData/FixtureVisual/part.cfg")) == installed, "Rejected apply/revalidation never changes installed content");
    }
}
