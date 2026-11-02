using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ApfCore;

namespace KesasInstall
{
    // The signed client ships the payload under a non-DLL name. Never load it
    // from GameData or change an assembly which the game has already loaded.
    public static class RuntimeAssemblyRepair
    {
        public const string Target = "KSP_x64_Data/Managed/Assembly-CSharp.dll";
        public const string Payload = "GameData/LunaMultiplayer/PluginData/Runtime/Assembly-CSharp.apfbin";
        public const string Expected = "d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4";
        public const long ExpectedBytes = 10790912;
        public const string ExpectedModule = "4b449f28-41f8-4227-adfa-ad3149c8fdba";
        private const string Recovery = "APF/RuntimeRecovery/Assembly-CSharp.previous.bin";
        private const string Journal = "APF/RuntimeRecovery/Assembly-CSharp.repair";
        internal static Action<string> TestCheckpoint = null;

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        private static string Under(string root, string relative)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Require(!String.IsNullOrEmpty(relative) && relative.IndexOf(':') < 0 && relative.IndexOf('\\') < 0 && !relative.StartsWith("/", StringComparison.Ordinal), "Unsafe runtime repair path.");
            foreach (string part in relative.Split('/')) Require(part.Length > 0 && part != "." && part != "..", "Unsafe runtime repair path.");
            string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Require(result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Runtime repair escaped the game.");
            for (string cursor = result; cursor != null; cursor = Path.GetDirectoryName(cursor))
                if (File.Exists(cursor) || Directory.Exists(cursor))
                    Require((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, "Runtime repair refuses linked paths.");
            return result;
        }
        private static string Hash(Stream stream)
        {
            using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static string HashFile(string path)
        {
            if (!File.Exists(path)) return "missing";
            Require(new FileInfo(path).Length <= 64L * 1024 * 1024, "Oversized game assembly; automatic repair refused.");
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return Hash(input);
        }
        public static bool LoadedGameCompatible()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "Assembly-CSharp")
                    return assembly.ManifestModule.ModuleVersionId == new Guid(ExpectedModule);
            return true; // Headless updater/tool: no game implementation is loaded.
        }
        public static bool NeedsRepair(string root)
        {
            return HashFile(Under(root, Target)) != Expected || !LoadedGameCompatible();
        }
        public static bool NeedsClientPayload(string root)
        {
            return NeedsRepair(root) && HashFile(Under(root, Payload)) != Expected;
        }
        public static void BeforeConnection(string gameData)
        {
            gameData = Path.GetFullPath(gameData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Directory.GetParent(gameData).FullName;
            Require(String.Equals(gameData, Path.Combine(root, "GameData"), StringComparison.OrdinalIgnoreCase), "Invalid game-data path.");
            if (NeedsRepair(root)) throw new InvalidOperationException("KSP game assembly needs repair. Connect to prepare the signed repair, then choose Restart & apply before joining.");
        }
        private static void ValidatePlatform(string root)
        {
            Require(Environment.OSVersion.Platform == PlatformID.Win32NT, "Automatic game-assembly repair supports Windows KSP 1.12.5 only.");
            Require(File.Exists(Under(root, "KSP_x64.exe")), "Windows KSP executable is missing.");
            string build = Under(root, "buildID64.txt");
            Require(File.Exists(build) && new FileInfo(build).Length <= 4096, "KSP build identity is missing.");
            bool matching = false;
            foreach (string row in File.ReadAllLines(build))
                if (row.Trim().Equals("build id = 03190", StringComparison.OrdinalIgnoreCase)) matching = true;
            Require(matching, "Automatic game-assembly repair requires KSP 1.12.5 build 3190.");
            Require(HashFile(Under(root, "KSP_x64_Data/Managed/UnityEngine.CoreModule.dll")) == "36d48c2068f85117781e380375d027ef0942f3b0c98654282603649106d76a72" &&
                    HashFile(Under(root, "KSP_x64_Data/Managed/Assembly-CSharp-firstpass.dll")) == "058da112ebb5c6c1ef7abc512bb44ffc415951d0c5f0a36f8dfc6c6445db4a34",
                    "Other KSP runtime files differ; automatic single-assembly repair is not safe for this installation.");
        }
        private static void RequireGameExited(string root)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                Require(assembly.GetName().Name != "Assembly-CSharp", "Cannot replace a game assembly from inside the running game.");
            foreach (string name in new[] { "KSP_x64", "KSP" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        string executable;
                        try { executable = process.MainModule.FileName; }
                        catch (InvalidOperationException) { continue; }
                        catch (Exception e) { throw new IOException("Cannot verify whether KSP is still running; repair is deferred.", e); }
                        Require(!String.Equals(Path.GetDirectoryName(Path.GetFullPath(executable)), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "KSP is still running; game-assembly repair is deferred.");
                    }
        }
        private static void AtomicJournal(string path, string oldHash, string phase)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes("APF-RUNTIME-REPAIR-1\n" + Expected + "\n" + oldHash + "\n" + phase + "\n");
            string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string[] ReadJournal(string path)
        {
            if (!File.Exists(path)) return null;
            Require(new FileInfo(path).Length <= 1024, "Oversized runtime repair journal.");
            string[] rows = File.ReadAllLines(path);
            Require(rows.Length == 4 && rows[0] == "APF-RUNTIME-REPAIR-1" && rows[1] == Expected &&
                    (rows[2] == "missing" || IsHash(rows[2])) && (rows[3] == "prepared" || rows[3] == "complete"), "Invalid runtime repair journal; evidence preserved.");
            return rows;
        }
        private static bool IsHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char c in hash) if ("0123456789abcdef".IndexOf(c) < 0) return false;
            return true;
        }
        private static void Checkpoint(string name) { if (TestCheckpoint != null) TestCheckpoint(name); }
        internal static void ApplyAfterExit(string root)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string target = Under(root, Target), payload = Under(root, Payload);
            string recovery = Under(root, Recovery), journal = Under(root, Journal);
            RequireGameExited(root);
            Directory.CreateDirectory(Path.GetDirectoryName(recovery));
            using (var lease = new FileStream(Under(root, "APF/RuntimeRecovery/repair.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string[] pending = ReadJournal(journal);
                string previous = HashFile(target);
                if (pending != null && pending[3] == "prepared")
                {
                    if (previous == Expected)
                    {
                        Require(pending[2] == "missing" || HashFile(recovery) == pending[2], "Runtime repair recovery preimage is missing; evidence preserved.");
                        AtomicJournal(journal, pending[2], "complete");
                        return;
                    }
                    Require(previous == pending[2], "Runtime changed during interrupted repair; all copies preserved.");
                }
                if (previous == Expected) return;
                ValidatePlatform(root);
                Require(File.Exists(payload) && new FileInfo(payload).Length == ExpectedBytes, "Signed game-assembly repair payload is missing. Reconnect to download the current client.");
                if (File.Exists(target)) Require((File.GetAttributes(target) & FileAttributes.ReadOnly) == 0, "Game assembly is read-only; repair was not applied.");
                using (var input = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Require(Hash(input) == Expected, "Game-assembly repair payload failed its pinned SHA256 check.");
                    input.Position = 0;
                    string temporary = Under(root, "KSP_x64_Data/Managed/.apf-runtime-" + Guid.NewGuid().ToString("N") + ".bin");
                    try
                    {
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
                        Require(HashFile(temporary) == Expected && HashFile(target) == previous, "Runtime repair inputs changed during preparation.");
                        AtomicJournal(journal, previous, "prepared");
                        Checkpoint("before-replace");
                        RequireGameExited(root);
                        if (previous == "missing") File.Move(temporary, target); else File.Replace(temporary, target, recovery);
                        Checkpoint("after-replace");
                        Require(HashFile(target) == Expected, "Replaced game assembly failed verification; recovery evidence retained.");
                        AtomicJournal(journal, previous, "complete");
                        File.AppendAllText(Under(root, "APF/APFUpdater.log"), DateTime.UtcNow.ToString("o") + " GAME-ASSEMBLY-REPAIR previous=" + previous + " current=" + Expected + Environment.NewLine);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
        }
        private static void VerifyRelease(string root, string kind, object transaction)
        {
            Require(transaction != null, "Runtime repair requires an authenticated transaction.");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            byte[] key = ApfTransactionSecurity.LoadKey(Under(root, "GameData/LunaMultiplayer/Data/APFHelper.key"));
            ApfClientUpdateManifest manifest;
            string expectedVersion;
            if (kind == "client")
            {
                var request = transaction as ApfClientUpdateTransaction;
                Require(request != null && ApfTransactionSecurity.Verify(request, key) && String.Equals(Path.GetFullPath(request.GameRoot), fullRoot, StringComparison.OrdinalIgnoreCase), "Runtime repair client authorization failed.");
                manifest = request.Manifest; expectedVersion = manifest == null ? null : manifest.Version;
            }
            else
            {
                var request = transaction as ApfSyncTransaction;
                Require(request != null && request.Manifest != null && ApfTransactionSecurity.Verify(request, key) && String.Equals(Path.GetFullPath(request.GameRoot), fullRoot, StringComparison.OrdinalIgnoreCase), "Runtime repair pack authorization failed.");
                expectedVersion = request.Manifest.RequiredClientVersion;
                string feed = Under(root, "APF/.client-update-staging/connect/feed.json");
                Require(File.Exists(feed) && new FileInfo(feed).Length <= 4L * 1024 * 1024, "Verified release feed is missing for runtime repair.");
                manifest = ApfClientUpdateManifest.FromJson(File.ReadAllText(feed));
            }
            Require(manifest != null && manifest.KspVersion == "1.12.5" && manifest.Version == expectedVersion && ApfClientUpdateValidator.Validate(manifest).IsValid, "Invalid release manifest for runtime repair.");
            bool approvedPayload = false;
            foreach (var file in manifest.Files)
                if (String.Equals(file.Path, Payload, StringComparison.OrdinalIgnoreCase))
                    approvedPayload = file.SizeBytes == ExpectedBytes && String.Equals(file.Sha256, Expected, StringComparison.OrdinalIgnoreCase);
            Require(approvedPayload, "Signed release does not authorize the pinned game-assembly payload.");
            string publicKey = Under(root, "GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml");
            Require(new FileInfo(publicKey).Length <= 65536, "Oversized runtime repair signing key.");
            using (var signing = ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))
                Require(ApfClientUpdateSecurity.Verify(manifest, signing), "Runtime repair release signature is invalid.");
        }
        public static void BeforeHelperComplete(string root, string kind, object transaction)
        {
            // Called inside authenticated NativeInstall.Run, after pack + bundled
            // client apply and before its success marker, completion or relaunch.
            Require(kind == "pack" || kind == "client" || kind == "rollback", "Invalid runtime repair transaction kind.");
            if (kind != "rollback")
            {
                // Never treat the local HMAC as publisher authorization for a
                // stock-game replacement: verify the signed release again.
                VerifyRelease(root, kind, transaction);
                ApplyAfterExit(root);
            }
        }
        public static bool ShouldSetGraphics(string mode, ApfSyncTransaction request)
        {
            // An unchanged-profile runtime-only repair must not reset the user's
            // graphics preferences through the generic pack completion routine.
            if (request == null || request.Packages == null || request.Deletes == null || request.Packages.Count != 0 || request.Deletes.Count != 0) return true;
            string profile = Under(request.GameRoot, "GameData/LunaMultiplayer/Data/KesasInstallProfile.json");
            if (!File.Exists(profile) || new FileInfo(profile).Length > 65536) return true;
            var values = ApfJson.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(profile));
            string selected;
            return values == null || !values.TryGetValue("mode", out selected) || selected != mode;
        }
    }
}
