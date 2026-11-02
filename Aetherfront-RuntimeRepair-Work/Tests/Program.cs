using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static string controller, game, fixture, code, plugins;
    private static int checks;
    private static readonly byte[] old = Encoding.UTF8.GetBytes("disposable incompatible assembly fixture; never executed");
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL " + label);
        checks++; Console.WriteLine("PASS " + label);
    }
    private static int Main(string[] args)
    {
        controller = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("APF_RUNTIME_CONTROLLER"));
        game = Path.GetFullPath(args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("APF_RUNTIME_GAME")); fixture = Path.GetFullPath(Path.Combine(controller, "tests/runtime-repair-fixture"));
        code = Path.Combine(controller, "build/runtime-repair-candidate/code"); plugins = Path.Combine(game, "GameData/LunaMultiplayer/Plugins");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            string name = new AssemblyName(request.Name).Name + ".dll";
            string path = Path.Combine(code, name);
            if (!File.Exists(path)) path = Path.Combine(plugins, name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Run(); Console.WriteLine("PASS " + checks + " runtime repair assertions; no KSP process launched"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static string At(string relative)
    {
        string value = Path.GetFullPath(Path.Combine(fixture, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!value.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Test path escaped its fixture");
        for (string cursor = value; cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Test path traverses a link");
        return value;
    }
    private static void Put(string relative, byte[] bytes)
    {
        string path = At(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes);
    }
    private static void Text(string relative, string value) { Put(relative, Encoding.UTF8.GetBytes(value)); }
    private static void Copy(string relative) { Put(relative, File.ReadAllBytes(Path.Combine(game, relative.Replace('/', Path.DirectorySeparatorChar)))); }
    private static void Delete(string relative)
    {
        string path = At(relative);
        if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
    }
    private static void Reset()
    {
        foreach (string relative in new[] { RuntimeAssemblyRepair.Target, "APF/RuntimeRecovery/Assembly-CSharp.previous.bin", "APF/RuntimeRecovery/Assembly-CSharp.repair", "APF/APFUpdater.log" }) Delete(relative);
        Put(RuntimeAssemblyRepair.Target, old);
        Put(RuntimeAssemblyRepair.Payload, File.ReadAllBytes(Path.Combine(game, RuntimeAssemblyRepair.Target.Replace('/', Path.DirectorySeparatorChar))));
        Text("buildID64.txt", "[config]\nbuild id = 03190\n");
        Copy("KSP_x64_Data/Managed/UnityEngine.CoreModule.dll"); Copy("KSP_x64_Data/Managed/Assembly-CSharp-firstpass.dll");
        typeof(RuntimeAssemblyRepair).GetField("TestCheckpoint", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
    }
    private static void Report()
    {
        try { typeof(RuntimeAssemblyRepair).GetMethod("ApplyAfterExit", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { fixture }); }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Refused(Action action, string label)
    {
        bool refused = false;
        try { action(); } catch (Exception error) when (error is IOException || error is InvalidDataException || error is InvalidOperationException || error is UnauthorizedAccessException) { refused = true; }
        Check(refused, label);
    }
    private static bool Original() { return Convert.ToBase64String(File.ReadAllBytes(At(RuntimeAssemblyRepair.Target))) == Convert.ToBase64String(old); }
    private static string NativeRequest()
    {
        string helper = At("GameData/LunaMultiplayer");
        foreach (string name in new[] { "ApfCore.dll", "JsonFx.dll", "LmpClient.dll" })
            Put("GameData/LunaMultiplayer/Plugins/" + name, File.ReadAllBytes(Path.Combine(plugins, name)));
        Put("GameData/LunaMultiplayer/Plugins/KesasInstall.dll", File.ReadAllBytes(Path.Combine(code, "KesasInstall.dll")));
        byte[] bytes = File.ReadAllBytes(Path.Combine(game, RuntimeAssemblyRepair.Target.Replace('/', Path.DirectorySeparatorChar)));
        byte[] catalog = Encoding.UTF8.GetBytes(ApfJson.Serialize(new InstallCatalog { Version = "kesas-rc38", Full = FixturePack(), Potato = FixturePack() }));
        string catalogHash; using (var algorithm = SHA256.Create()) catalogHash = ApfJson.ToHex(algorithm.ComputeHash(catalog));
        // Keep transaction evidence, but reuse this immutable payload instead
        // of retaining another full game-assembly ZIP on every assertion run.
        string archive=At("APF/.client-update-staging/payload-"+RuntimeAssemblyRepair.Expected.Substring(0,16)+"-"+catalogHash.Substring(0,16)+".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archive));
        if(!File.Exists(archive))using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry(RuntimeAssemblyRepair.Payload).Open()) entry.Write(bytes, 0, bytes.Length);
            using (var entry = zip.CreateEntry("GameData/LunaMultiplayer/Launcher/install-catalog.json").Open()) entry.Write(catalog, 0, catalog.Length);
        }
        using(var zip=ZipFile.OpenRead(archive))
        {
            if(zip.Entries.Count!=2)throw new InvalidDataException("Immutable test payload has unexpected files");
            foreach(var content in new[]{Tuple.Create(RuntimeAssemblyRepair.Payload,bytes),Tuple.Create("GameData/LunaMultiplayer/Launcher/install-catalog.json",catalog)})
            {
                var entry=zip.GetEntry(content.Item1);if(entry==null||entry.Length!=content.Item2.Length)throw new InvalidDataException("Immutable test payload size changed");
                using(var stream=entry.Open())using(var buffer=new MemoryStream())
                {stream.CopyTo(buffer);if(!buffer.ToArray().SequenceEqual(content.Item2))throw new InvalidDataException("Immutable test payload changed");}
            }
        }
        var manifest = new ApfClientUpdateManifest
        {
            SchemaVersion = 2, Version = "kesas-rc38", KspVersion = "1.12.5", PackageUrl = "https://127.0.0.1/no-network.zip",
            Sha256 = ApfJson.Sha256File(archive), SizeBytes = new FileInfo(archive).Length,
            Files = new List<ApfClientUpdateFile> { new ApfClientUpdateFile { Path = RuntimeAssemblyRepair.Payload, SizeBytes = bytes.Length, Sha256 = RuntimeAssemblyRepair.Expected },
                new ApfClientUpdateFile { Path = "GameData/LunaMultiplayer/Launcher/install-catalog.json", SizeBytes = catalog.Length, Sha256 = catalogHash } }
        };
        using (var signing = new RSACryptoServiceProvider(2048))
        {
            signing.PersistKeyInCsp = false;
            ApfClientUpdateSecurity.Sign(manifest, signing);
            Text("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml", ApfClientUpdateSecurity.ExportXml(signing, false));
        }
        var request = new ApfClientUpdateTransaction
        {
            GameRoot = fixture, GameDataPath = At("GameData"), ProcessId = 0, KspExecutable = At("KSP_x64.exe"),
            ArchivePath = archive, Manifest = manifest, Approval = ApfClientUpdateApproval.Capture(fixture)
        };
        ApfTransactionSecurity.Sign(request, ApfTransactionSecurity.LoadOrCreateKey(At("GameData/LunaMultiplayer/Data/APFHelper.key")));
        string path = At("APF/.client-update-staging/" + Guid.NewGuid().ToString("N") + "/transaction.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, ApfJson.Serialize(request));
        return path;
    }
    private static ApfPackManifest FixturePack()
    {
        return new ApfPackManifest { ServerId = "runtime-repair-fixture", PackId = "kesas", PackVersion = "kesas-rc38", RequiredClientVersion = "kesas-rc38", KspVersion = "1.12.5",
            ProtectedRoots = new List<string> { "Squad", "SquadExpansion", "LunaMultiplayer", "000_Harmony" }, ManagedPackages = new List<ApfPackage>(), ClientOnly = new List<ApfClientOnlyRule>() };
    }
    private static string NativePackRequest()
    {
        string clientPath = NativeRequest();
        var client = ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(clientPath));
        Text("APF/.client-update-staging/connect/feed.json", client.Manifest.ToJson());
        Text("GameData/LunaMultiplayer/Data/APFServer.txt", "127.0.0.1:18865");
        Text("GameData/LunaMultiplayer/Data/KesasInstallProfile.json", "{\"mode\":\"full\",\"catalogVersion\":\"kesas-rc38\"}");
        Text("GameData/LunaMultiplayer/Data/APFPackState.json", ApfJson.Serialize(new ApfPackState()));
        Text("GameData/LunaMultiplayer/Launcher/install-catalog.json", ApfJson.Serialize(new InstallCatalog { Version = "kesas-rc38", Full = FixturePack(), Potato = FixturePack() }));
        var pack = new ApfSyncTransaction { GameRoot = fixture, GameDataPath = At("GameData"), KspExecutable = At("KSP_x64.exe"), ProcessId = 0,
            KspArguments = new List<string> { "--unexecuted-fixture-" + Guid.NewGuid().ToString("N") },
            Manifest = FixturePack(), ExistingState = new ApfPackState(), StatePath = At("GameData/LunaMultiplayer/Data/APFPackState.json"), Packages = new List<ApfStagedPackage>(), Deletes = new List<ApfDeleteChange>() };
        Check(!RuntimeAssemblyRepair.ShouldSetGraphics("full", pack), "same-profile runtime-only repair preserves Regular graphics");
        Check(RuntimeAssemblyRepair.ShouldSetGraphics("potato", pack), "genuine profile change still applies graphics");
        Text("GameData/LunaMultiplayer/Data/KesasInstallProfile.json", "{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc38\"}");
        Check(!RuntimeAssemblyRepair.ShouldSetGraphics("potato", pack), "same-profile runtime-only repair preserves Potato graphics");
        Text("GameData/LunaMultiplayer/Data/KesasInstallProfile.json", "{\"mode\":\"full\",\"catalogVersion\":\"kesas-rc38\"}");
        ApfTransactionSecurity.Sign(pack, ApfTransactionSecurity.LoadKey(At("GameData/LunaMultiplayer/Data/APFHelper.key")));
        string path = At("APF/.staging/" + Guid.NewGuid().ToString("N") + "/transaction.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, ApfJson.Serialize(pack));
        return path;
    }
    private static void NativeApply(string request)
    {
        using (var completion = NativeInstall.Run(At("GameData/LunaMultiplayer"), request, "client"))
            Check(completion.RestartedProcessId == 0, "actual native transaction applies without launching a game");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Directory.CreateDirectory(fixture);
        Text("KSP_x64.exe", "UNEXECUTABLE TEST MARKER, NOT A GAME");
        Directory.CreateDirectory(At("GameData/Squad"));
        Text("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt", new String('a', 64));
        Text("GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt", "https://127.0.0.1:18866/unused-feed.json");
        Text("GameData/LunaMultiplayer/Data/APFClient.json", "{\"enabled\":true,\"clientVersion\":\"kesas-rc38\"}");
        Text("settings.cfg", "USER SETTINGS MUST SURVIVE"); Text("APF/Identity/identity.marker", "DISPOSABLE IDENTITY MARKER"); Text("saves/test/save.marker", "NOT A FULL SAVE");
        string settings = ApfJson.Sha256File(At("settings.cfg")), identity = ApfJson.Sha256File(At("APF/Identity/identity.marker")), save = ApfJson.Sha256File(At("saves/test/save.marker"));
        Reset();
        RuntimeAssemblyRepair.BeforeHelperComplete(fixture, "rollback", null); Check(Original(), "rollback transactions never reapply the replacement");
        Refused(() => RuntimeAssemblyRepair.BeforeHelperComplete(fixture, "unknown", null), "unknown transaction kind rejected");
        Check(RuntimeAssemblyRepair.NeedsRepair(fixture), "incompatible assembly invalidates completed reconnect");
        Check(!RuntimeAssemblyRepair.NeedsClientPayload(fixture), "verified cached payload avoids client redownload");
        Check(ApfClientUpdateValidator.IsAllowedFile(RuntimeAssemblyRepair.Payload, 2), "existing shipping validator accepts non-DLL repair payload");
        Check(!ApfClientUpdateValidator.IsAllowedFile(RuntimeAssemblyRepair.Target, 2), "ordinary client allowlist does not gain arbitrary managed-DLL writes");
        Refused(() => Installer.ScanConnection(At("GameData"), null, null), "actual connection scanner blocks an incompatible runtime");
        Delete(RuntimeAssemblyRepair.Payload);
        Check(RuntimeAssemblyRepair.NeedsClientPayload(fixture), "missing repair payload requires signed client refresh");
        var installer = new Installer(fixture, new IPEndPoint(IPAddress.Loopback, 18865), "kesas-rc38");
        var completed = typeof(Installer).GetMethod("CompletedClientMatches", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(!(bool)completed.Invoke(installer, new object[] { "potato", null }), "actual completed-client shortcut cannot hide missing repair payload");
        Reset(); Report();
        Check(ApfJson.Sha256File(At(RuntimeAssemblyRepair.Target)) == RuntimeAssemblyRepair.Expected, "atomic repair core installs exact approved assembly");
        Check(Convert.ToBase64String(File.ReadAllBytes(At("APF/RuntimeRecovery/Assembly-CSharp.previous.bin"))) == Convert.ToBase64String(old), "atomic replacement preserves original incompatible DLL");
        Check(File.ReadAllLines(At("APF/RuntimeRecovery/Assembly-CSharp.repair"))[3] == "complete", "repair journal completes only after target verification");
        Check(File.ReadAllText(At("APF/APFUpdater.log")).Contains("GAME-ASSEMBLY-REPAIR"), "helper records completed code hashes");
        long ticks = File.GetLastWriteTimeUtc(At(RuntimeAssemblyRepair.Target)).Ticks;
        Delete(RuntimeAssemblyRepair.Payload);
        for (int n = 0; n < 4; n++) { Check(!RuntimeAssemblyRepair.NeedsRepair(fixture), "healthy reconnect single-file check " + n); Report(); }
        Check(File.GetLastWriteTimeUtc(At(RuntimeAssemblyRepair.Target)).Ticks == ticks, "healthy checks never rewrite the game DLL");
        RuntimeAssemblyRepair.BeforeConnection(At("GameData") + Path.DirectorySeparatorChar); Check(true, "healthy connection supports trailing game-data separator");
        Reset(); var tampered = File.ReadAllBytes(At(RuntimeAssemblyRepair.Payload)); tampered[4096] ^= 1; Put(RuntimeAssemblyRepair.Payload, tampered);
        Refused(Report, "tampered payload rejected by pinned SHA256"); Check(Original(), "tampered payload leaves original assembly intact");
        Reset(); Text("buildID64.txt", "build id = 03191\n"); Refused(Report, "different KSP build refused"); Check(Original(), "different build not overwritten");
        Reset(); Text("KSP_x64_Data/Managed/UnityEngine.CoreModule.dll", "different Unity runtime"); Refused(Report, "different supporting runtime refused"); Check(Original(), "incompatible supporting runtime leaves base DLL intact");
        Reset(); File.SetAttributes(At(RuntimeAssemblyRepair.Target), FileAttributes.ReadOnly); Refused(Report, "read-only target refused"); Check(Original(), "read-only failure preserves target");
        Reset(); using (var held = new FileStream(At(RuntimeAssemblyRepair.Target), FileMode.Open, FileAccess.Read, FileShare.None)) Refused(Report, "locked game DLL refused"); Check(Original(), "locked failure preserves target");
        Reset();
        var checkpoint = typeof(RuntimeAssemblyRepair).GetField("TestCheckpoint", BindingFlags.Static | BindingFlags.NonPublic);
        checkpoint.SetValue(null, (Action<string>)(value => { if (value == "before-replace") throw new IOException("simulated interruption before replacement"); }));
        Refused(Report, "interruption before replacement remains retryable"); Check(Original(), "interrupted precommit preserves old DLL");
        checkpoint.SetValue(null, null); Report(); Check(!RuntimeAssemblyRepair.NeedsRepair(fixture), "retry resumes prepared repair normally");
        Reset(); checkpoint.SetValue(null, (Action<string>)(value => { if (value == "after-replace") throw new IOException("simulated interruption after replacement"); }));
        Refused(Report, "interruption after atomic replacement recorded"); Check(ApfJson.Sha256File(At(RuntimeAssemblyRepair.Target)) == RuntimeAssemblyRepair.Expected, "postcommit interruption leaves exact complete new DLL");
        ticks = File.GetLastWriteTimeUtc(At(RuntimeAssemblyRepair.Target)).Ticks; checkpoint.SetValue(null, null); Report();
        Check(File.ReadAllLines(At("APF/RuntimeRecovery/Assembly-CSharp.repair"))[3] == "complete", "retry completes interrupted journal");
        Check(File.GetLastWriteTimeUtc(At(RuntimeAssemblyRepair.Target)).Ticks == ticks, "postcommit retry does not replace DLL again");
        Reset(); checkpoint.SetValue(null, (Action<string>)(value => { if (value == "before-replace") throw new IOException("interruption"); })); Refused(Report, "prepare ambiguous-recovery fixture");
        checkpoint.SetValue(null, null); Text(RuntimeAssemblyRepair.Target, "external edit after interruption"); string external = ApfJson.Sha256File(At(RuntimeAssemblyRepair.Target));
        Refused(Report, "unexpected post-interruption mutation refuses automatic overwrite"); Check(ApfJson.Sha256File(At(RuntimeAssemblyRepair.Target)) == external, "ambiguous recovery preserves external edit and evidence");
        Reset(); Text("APF/RuntimeRecovery/Assembly-CSharp.repair", "untrusted journal"); Refused(Report, "malformed repair journal fails closed"); Check(Original(), "malformed journal preserves original DLL");
        Reset(); Delete(RuntimeAssemblyRepair.Target); Report(); Check(!RuntimeAssemblyRepair.NeedsRepair(fixture), "missing assembly restored from exact approved payload");
        Reset(); string forged = NativeRequest(); var forgedRequest = ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(forged));
        forgedRequest.Manifest.Signature = Convert.ToBase64String(new byte[256]);
        ApfTransactionSecurity.Sign(forgedRequest, ApfTransactionSecurity.LoadKey(At("GameData/LunaMultiplayer/Data/APFHelper.key")));
        File.WriteAllText(forged, ApfJson.Serialize(forgedRequest));
        bool signatureRejected = false;
        try { NativeApply(forged); } catch (InvalidDataException error) { signatureRejected = error.Message.Contains("release signature is invalid"); }
        Check(signatureRejected, "native helper rejects forged release signature despite valid local transaction authorization");
        Check(Original(), "invalid release signature cannot trigger runtime replacement");
        Reset(); NativeApply(NativeRequest());
        Check(!RuntimeAssemblyRepair.NeedsRepair(fixture), "signed native client transaction repairs runtime before completion");
        Check(ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(At("APF/LastInstallResult.json")))["status"] == "complete", "native helper marks successful repair complete");
        Reset(); string lockedRequest = NativeRequest();
        using (var held = new FileStream(At(RuntimeAssemblyRepair.Target), FileMode.Open, FileAccess.Read, FileShare.None)) Refused(() => NativeApply(lockedRequest), "real native helper reports a blocked runtime replacement as failure");
        Check(ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(At("APF/LastInstallResult.json")))["status"] == "failed", "native helper does not leave a false completed receipt");
        Check(Original(), "failed native transaction preserves old runtime");
        NativeApply(NativeRequest()); Check(!RuntimeAssemblyRepair.NeedsRepair(fixture), "new signed transaction retries after failure without stale busy state");
        Reset(); string packRequest = NativePackRequest();
        using (var completion = NativeInstall.Run(At("GameData/LunaMultiplayer"), packRequest, "pack")) Check(completion.RestartedProcessId == 0, "actual runtime-only pack transaction never launches KSP during apply");
        Check(!RuntimeAssemblyRepair.NeedsRepair(fixture), "actual pack path verifies signed cached feed and repairs runtime");
        Check(ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(At("APF/LastInstallResult.json")))["status"] == "complete", "runtime-only pack receipt completes normally");
        Check(ApfJson.Sha256File(At("settings.cfg")) == settings && ApfJson.Sha256File(At("APF/Identity/identity.marker")) == identity && ApfJson.Sha256File(At("saves/test/save.marker")) == save, "settings identity and save markers remain byte-identical");
        // Model a loaded incompatible implementation without starting Unity or KSP.
        AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("Assembly-CSharp"), AssemblyBuilderAccess.Run).DefineDynamicModule("Synthetic incompatible loaded module");
        Check(!RuntimeAssemblyRepair.LoadedGameCompatible(), "loaded incompatible module disables continuity startup");
        Check(RuntimeAssemblyRepair.NeedsRepair(fixture), "already-correct disk DLL still requires restart when old module is loaded");
        Refused(Report, "replacement from a process with game assembly loaded is refused");
        Check(ApfJson.Sha256File(At(RuntimeAssemblyRepair.Target)) == RuntimeAssemblyRepair.Expected, "loaded-game refusal never changes installed DLL");
    }
}
