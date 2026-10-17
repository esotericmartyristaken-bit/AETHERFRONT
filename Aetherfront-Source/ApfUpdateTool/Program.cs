using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using ApfCore;

namespace ApfUpdateTool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var values = Parse(args);
                if (values.ContainsKey("help")) { PrintUsage(); return 0; }
                if (values.ContainsKey("generate-key"))
                {
                    GenerateKey(Required(values, "private-output"), Required(values, "public-output"));
                    return 0;
                }
                if (values.ContainsKey("verify"))
                {
                    VerifyRelease(
                        Required(values, "manifest"),
                        Required(values, "archive"),
                        Required(values, "public-key-xml"));
                    return 0;
                }

                var sourceRoot = Path.GetFullPath(Required(values, "source-root"));
                var output = Path.GetFullPath(Required(values, "output"));
                var version = Required(values, "version");
                var packageBaseUrl = Required(values, "package-base-url");
                if (!Uri.TryCreate(packageBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("--package-base-url must be an HTTPS URL");
                if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
                    throw new ArgumentException("--package-base-url must not contain a query or fragment");
                if (!baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
                    baseUri = new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);

                var files = new[]
                {
                    "GameData/LunaMultiPlayer/Plugins/LmpClient.dll",
                    "GameData/LunaMultiPlayer/Plugins/ApfCore.dll",
                    "GameData/LunaMultiPlayer/Plugins/AetherfrontCore.dll",
                    "GameData/LunaMultiPlayer/Plugins/Lidgren.Network.dll",
                    "GameData/LunaMultiPlayer/Plugins/CachedQuickLz.dll",
                    "GameData/LunaMultiPlayer/Plugins/JsonFx.dll",
                    "GameData/LunaMultiPlayer/Plugins/System.Buffers.dll",
                    "GameData/LunaMultiPlayer/Plugins/System.Memory.dll",
                    "GameData/LunaMultiPlayer/Plugins/System.Numerics.Vectors.dll",
                    "GameData/LunaMultiPlayer/Plugins/System.Runtime.CompilerServices.Unsafe.dll",
                    "GameData/LunaMultiPlayer/Data/APFTlsCertificatePins.txt"
                };
                var sourceFiles = files.Select(path => new { Relative = path, Full = Path.Combine(sourceRoot, path.Replace('/', Path.DirectorySeparatorChar)) }).ToArray();
                foreach (var sourceFile in sourceFiles)
                    if (!File.Exists(sourceFile.Full)) throw new FileNotFoundException("Required client update file missing", sourceFile.Full);

                Directory.CreateDirectory(output);
                var archiveName = "APFClient-" + SafeName(version) + ".zip";
                var archivePath = Path.Combine(output, archiveName);
                if (File.Exists(archivePath)) File.Delete(archivePath);
                using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    foreach (var sourceFile in sourceFiles)
                        zip.CreateEntryFromFile(sourceFile.Full, sourceFile.Relative, CompressionLevel.Optimal);
                }

                var manifest = new ApfClientUpdateManifest
                {
                    Version = version,
                    KspVersion = Optional(values, "ksp-version"),
                    PackageUrl = new Uri(baseUri, archiveName).ToString(),
                    Sha256 = ApfJson.Sha256File(archivePath),
                    SizeBytes = new FileInfo(archivePath).Length,
                    ReleaseNotes = Optional(values, "release-notes")
                };
                foreach (var sourceFile in sourceFiles)
                {
                    var full = sourceFile.Full;
                    manifest.Files.Add(new ApfClientUpdateFile
                    {
                        Path = sourceFile.Relative,
                        Sha256 = ApfJson.Sha256File(full),
                        SizeBytes = new FileInfo(full).Length
                    });
                }
                var validation = ApfClientUpdateValidator.Validate(manifest, false);
                if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));
                using (var key = ApfClientUpdateSecurity.LoadXmlKey(File.ReadAllText(Required(values, "private-key-xml"))))
                    ApfClientUpdateSecurity.Sign(manifest, key);
                File.WriteAllText(Path.Combine(output, "APFClientUpdate.json"), manifest.ToJson());
                Console.WriteLine("APF client update built");
                Console.WriteLine("Manifest: " + Path.Combine(output, "APFClientUpdate.json"));
                Console.WriteLine("Archive: " + archivePath);
                Console.WriteLine("Version: " + manifest.Version);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("APF client update build failed: " + exception.Message);
                Console.Error.WriteLine("Run with --help for usage.");
                return 1;
            }
        }

        private static void GenerateKey(string privateOutput, string publicOutput)
        {
            var privatePath = Path.GetFullPath(privateOutput);
            var publicPath = Path.GetFullPath(publicOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(privatePath));
            Directory.CreateDirectory(Path.GetDirectoryName(publicPath));
            using (var rsa = RSA.Create(3072))
            {
                File.WriteAllText(privatePath, ApfClientUpdateSecurity.ExportXml(rsa, true));
                File.WriteAllText(publicPath, ApfClientUpdateSecurity.ExportXml(rsa, false));
            }
            Console.WriteLine("APF client update keypair generated");
            Console.WriteLine("Private key: " + privatePath);
            Console.WriteLine("Public key: " + publicPath);
        }

        private static void VerifyRelease(string manifestPath, string archivePath, string publicKeyPath)
        {
            manifestPath = Path.GetFullPath(manifestPath);
            archivePath = Path.GetFullPath(archivePath);
            publicKeyPath = Path.GetFullPath(publicKeyPath);
            var manifest = ApfClientUpdateManifest.FromJson(File.ReadAllText(manifestPath));
            var validation = ApfClientUpdateValidator.Validate(manifest, true);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));
            using (var publicKey = ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKeyPath)))
                if (!ApfClientUpdateSecurity.Verify(manifest, publicKey))
                    throw new CryptographicException("Client update signature verification failed");
            var archive = new FileInfo(archivePath);
            if (!archive.Exists) throw new FileNotFoundException("Client update archive missing", archivePath);
            if (archive.Length != manifest.SizeBytes) throw new InvalidDataException("Client update archive size does not match manifest");
            if (!string.Equals(ApfJson.Sha256File(archivePath), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Client update archive hash does not match manifest");

            var expected = manifest.Files.ToDictionary(file => ApfPaths.Normalize(file.Path), StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var normalized = ApfPaths.Normalize(entry.FullName);
                    if (!ApfPaths.IsSafeRelative(entry.FullName) || !expected.TryGetValue(normalized, out var file))
                        throw new InvalidDataException("Unexpected client update archive entry: " + entry.FullName);
                    if (!seen.Add(normalized)) throw new InvalidDataException("Duplicate client update archive entry: " + entry.FullName);
                    if (entry.Length != file.SizeBytes) throw new InvalidDataException("Client update file size mismatch: " + entry.FullName);
                    using (var sha = SHA256.Create())
                    using (var stream = entry.Open())
                    {
                        var hash = ApfJson.ToHex(sha.ComputeHash(stream));
                        if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Client update file hash mismatch: " + entry.FullName);
                    }
                }
                var missing = expected.Keys.Where(path => !seen.Contains(path)).ToArray();
                if (missing.Length != 0) throw new InvalidDataException("Client update archive is missing: " + string.Join(", ", missing));
            }
            Console.WriteLine("APF client update verified");
            Console.WriteLine("Version: " + manifest.Version);
            Console.WriteLine("Files: " + manifest.Files.Count);
            Console.WriteLine("Archive SHA-256: " + manifest.Sha256);
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
                    key.Equals("generate-key", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("verify", StringComparison.OrdinalIgnoreCase))
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

        private static string Optional(Dictionary<string, List<string>> values, string key)
        {
            return values.TryGetValue(key, out var list) && list.Count > 0 ? list[list.Count - 1] : string.Empty;
        }

        private static string SafeName(string value)
        {
            return new string((value ?? "update").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
        }

        private static void PrintUsage()
        {
            Console.WriteLine("APF client update builder");
            Console.WriteLine("  --generate-key --private-output <path> --public-output <path>");
            Console.WriteLine("  --verify --manifest <path> --archive <path> --public-key-xml <path>");
            Console.WriteLine("  --source-root <staged root>      Contains the complete GameData/LunaMultiPlayer/Plugins client set");
            Console.WriteLine("  --output <directory>             APFClientUpdate.json + archive");
            Console.WriteLine("  --version <apf-client-N>");
            Console.WriteLine("  --package-base-url <https://.../>");
            Console.WriteLine("  --private-key-xml <path>");
            Console.WriteLine("  --ksp-version <version>");
            Console.WriteLine("  --release-notes <text>");
        }
    }
}
