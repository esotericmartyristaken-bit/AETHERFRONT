using System;
using System.Collections.Generic;
using System.IO;
using ApfCore;

namespace ApfPackTool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var values = Parse(args);
                if (values.ContainsKey("help"))
                {
                    PrintUsage();
                    return 0;
                }
                if (values.ContainsKey("verify"))
                {
                    VerifyPack(Required(values, "manifest"), Required(values, "archive-root"));
                    return 0;
                }
                if (values.ContainsKey("create-sync-transaction"))
                {
                    CreateSyncTransaction(
                        Required(values, "manifest"),
                        Required(values, "game-root"),
                        Required(values, "relaunch-executable"));
                    return 0;
                }
                if (values.ContainsKey("verify-installed"))
                {
                    VerifyInstalled(Required(values, "manifest"), Required(values, "game-root"));
                    return 0;
                }

                var options = new ApfPackBuildOptions
                {
                    SourceGameData = Required(values, "source-gamedata"),
                    OutputDirectory = Required(values, "output"),
                    ServerId = Required(values, "server-id"),
                    PackId = Required(values, "pack-id"),
                    PackVersion = Required(values, "pack-version"),
                    KspVersion = Optional(values, "ksp-version"),
                    RequiredClientVersion = Optional(values, "client-version", "apf-client-1"),
                    ArchiveBaseUrl = Required(values, "archive-base-url"),
                    ReuseExistingArchives = values.ContainsKey("reuse-existing")
                };
                options.RequiredDlc.AddRange(values.GetMany("required-dlc"));
                options.ProtectedRoots.AddRange(values.GetMany("protected-root"));
                options.ClientOnlyRoots.AddRange(values.GetMany("client-only-root"));

                var result = ApfPackBuilder.Build(options);
                Console.WriteLine("APF pack built");
                Console.WriteLine("Manifest: " + result.ManifestPath);
                Console.WriteLine("Packages: " + result.Manifest.ManagedPackages.Count);
                Console.WriteLine("Client-only roots: " + result.Manifest.ClientOnly.Count);
                Console.WriteLine("Archive base URL: " + options.ArchiveBaseUrl);
                foreach (var package in result.Manifest.ManagedPackages)
                    Console.WriteLine(package.Id + " " + package.Version + " " + package.SizeBytes + " bytes " + package.Sha256);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("APF pack build failed: " + exception.Message);
                Console.Error.WriteLine("Run with --help for usage.");
                return 1;
            }
        }

        private static void VerifyPack(string manifestPath, string archiveRoot)
        {
            manifestPath = Path.GetFullPath(manifestPath);
            archiveRoot = Path.GetFullPath(archiveRoot);
            var manifest = ApfPackManifest.FromJson(File.ReadAllText(manifestPath));
            var validation = ApfManifestValidator.Validate(manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));
            var gameDataRoot = Path.Combine(archiveRoot, ".validation", "GameData");
            foreach (var package in manifest.ManagedPackages)
            {
                var uri = new Uri(package.ArchiveUrl, UriKind.Absolute);
                var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
                if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException("Package URL has no archive name: " + package.Id);
                ApfArchiveInstaller.ValidateArchive(Path.Combine(archiveRoot, fileName), package, gameDataRoot);
            }
            Console.WriteLine("APF pack verified");
            Console.WriteLine("Pack: " + manifest.PackId + " " + manifest.PackVersion);
            Console.WriteLine("Packages: " + manifest.ManagedPackages.Count);
        }

        private static void CreateSyncTransaction(string manifestPath, string gameRoot, string relaunchExecutable)
        {
            manifestPath = Path.GetFullPath(manifestPath);
            gameRoot = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            relaunchExecutable = Path.GetFullPath(relaunchExecutable);
            var gameData = Path.Combine(gameRoot, "GameData");
            var dataRoot = Path.Combine(gameData, "LunaMultiplayer", "Data");
            var stagingRoot = Path.Combine(gameRoot, "APF", ".staging", "full-pack");
            if (!Directory.Exists(gameData)) throw new DirectoryNotFoundException("GameData missing: " + gameData);
            if (!File.Exists(relaunchExecutable)) throw new FileNotFoundException("Relaunch executable missing", relaunchExecutable);
            var manifest = ApfPackManifest.FromJson(File.ReadAllText(manifestPath));
            var validation = ApfManifestValidator.Validate(manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));

            var transaction = new ApfSyncTransaction
            {
                GameRoot = gameRoot,
                GameDataPath = gameData,
                ProcessId = 0,
                KspExecutable = relaunchExecutable,
                StatePath = Path.Combine(dataRoot, "APFPackState.json"),
                Manifest = manifest,
                ExistingState = new ApfPackState()
            };
            foreach (var package in manifest.ManagedPackages)
            {
                var uri = new Uri(package.ArchiveUrl, UriKind.Absolute);
                var archivePath = Path.Combine(stagingRoot, Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)));
                ApfArchiveInstaller.ValidateArchive(archivePath, package, gameData);
                transaction.Packages.Add(new ApfStagedPackage { Package = package, ArchivePath = archivePath });
            }

            var key = ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(dataRoot, "APFHelper.key"));
            ApfTransactionSecurity.Sign(transaction, key);
            var transactionPath = Path.Combine(stagingRoot, "transaction.json");
            File.WriteAllText(transactionPath, ApfJson.Serialize(transaction));
            Console.WriteLine("APF sync transaction created");
            Console.WriteLine("Transaction: " + transactionPath);
            Console.WriteLine("Packages: " + transaction.Packages.Count);
        }

        private static void VerifyInstalled(string manifestPath, string gameRoot)
        {
            gameRoot = Path.GetFullPath(gameRoot);
            var gameData = Path.Combine(gameRoot, "GameData");
            var statePath = Path.Combine(gameData, "LunaMultiplayer", "Data", "APFPackState.json");
            var manifest = ApfPackManifest.FromJson(File.ReadAllText(Path.GetFullPath(manifestPath)));
            var state = ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath));
            var plan = ApfPackScanner.Scan(gameData, manifest, state);
            if (plan.Errors.Count != 0) throw new InvalidDataException("Installed pack errors: " + string.Join("; ", plan.Errors));
            if (plan.Operations.Count != 0)
                throw new InvalidDataException("Installed pack is not clean: " + string.Join("; ", plan.Operations.ConvertAll(operation => operation.RelativePath + " (" + operation.Reason + ")")));
            Console.WriteLine("APF installed pack verified clean");
            Console.WriteLine("Pack: " + manifest.PackId + " " + manifest.PackVersion);
            Console.WriteLine("Packages: " + state.Packages.Count);
        }

        private static Dictionary<string, List<string>> Parse(string[] args)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < (args?.Length ?? 0); index++)
            {
                var raw = args[index];
                if (!raw.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown argument: " + raw);
                var key = raw.Substring(2);
                if (key.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("verify", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("create-sync-transaction", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("verify-installed", StringComparison.OrdinalIgnoreCase))
                {
                    result[key] = new List<string>();
                    continue;
                }
                if (key.Equals("reuse-existing", StringComparison.OrdinalIgnoreCase))
                {
                    result[key] = new List<string>();
                    continue;
                }
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Missing value for --" + key);
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<string>();
                list.Add(args[++index]);
            }
            return result;
        }

        private static string Required(Dictionary<string, List<string>> values, string key)
        {
            if (!values.TryGetValue(key, out var list) || list.Count == 0 || string.IsNullOrWhiteSpace(list[list.Count - 1]))
                throw new ArgumentException("Missing --" + key);
            return list[list.Count - 1];
        }

        private static string Optional(Dictionary<string, List<string>> values, string key, string fallback = "")
        {
            return values.TryGetValue(key, out var list) && list.Count > 0 ? list[list.Count - 1] : fallback;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("APF pack builder");
            Console.WriteLine("  --verify --manifest <path> --archive-root <path>");
            Console.WriteLine("  --create-sync-transaction --manifest <path> --game-root <path> --relaunch-executable <path>");
            Console.WriteLine("  --verify-installed --manifest <path> --game-root <path>");
            Console.WriteLine("  --source-gamedata <path>       Input cloned KSP GameData");
            Console.WriteLine("  --output <path>                Output directory for APFPack.json and archives");
            Console.WriteLine("  --server-id <id>               APF server identifier");
            Console.WriteLine("  --pack-id <id>                 Pack identifier");
            Console.WriteLine("  --pack-version <version>       Pack version");
            Console.WriteLine("  --ksp-version <version>        Optional KSP version, e.g. 1.12.5");
            Console.WriteLine("  --client-version <version>     Optional required APF client version");
            Console.WriteLine("  --archive-base-url <https://>  HTTPS URL directory serving the archives");
            Console.WriteLine("  --client-only-root <root>      Repeatable top-level root, e.g. GraphicsMod");
            Console.WriteLine("  --required-dlc <path>          Repeatable GameData-relative DLC path");
            Console.WriteLine("  --protected-root <root>        Repeatable extra protected root");
            Console.WriteLine("  --reuse-existing               Reuse completed archives already in output");
            Console.WriteLine();
            Console.WriteLine("Archives are created per top-level mod root. CKAN is not required.");
        }
    }

    internal static class ArgumentExtensions
    {
        public static IEnumerable<string> GetMany(this Dictionary<string, List<string>> values, string key)
        {
            return values.TryGetValue(key, out var list) ? list : Array.Empty<string>();
        }
    }
}
