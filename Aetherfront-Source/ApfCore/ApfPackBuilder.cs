using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ApfCore
{
    public sealed class ApfPackBuildOptions
    {
        public string SourceGameData { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string PackId { get; set; } = string.Empty;
        public string PackVersion { get; set; } = string.Empty;
        public string KspVersion { get; set; } = string.Empty;
        public string RequiredClientVersion { get; set; } = string.Empty;
        public string ArchiveBaseUrl { get; set; } = string.Empty;
        public bool ReuseExistingArchives { get; set; }
        public List<string> RequiredDlc { get; } = new List<string>();
        public List<string> ProtectedRoots { get; } = new List<string>();
        public List<string> ClientOnlyRoots { get; } = new List<string>();
    }

    public sealed class ApfPackBuildResult
    {
        public ApfPackManifest Manifest { get; set; }
        public string ManifestPath { get; set; } = string.Empty;
        public List<string> ArchivePaths { get; } = new List<string>();
    }

    /// <summary>
    /// Creates one archive per top-level mod root and emits a validated APF manifest.
    /// This intentionally does not infer client-only status or resolve CKAN metadata.
    /// Both are administrator decisions and must be explicit.
    /// </summary>
    public static class ApfPackBuilder
    {
        private static readonly string[] DefaultProtectedRoots =
        {
            "Squad",
            "SquadExpansion",
            "LunaMultiPlayer"
        };

        private static readonly HashSet<string> KnownGeneratedLooseFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "modulemanager.configcache",
            "modulemanager.configsha",
            "modulemanager.configsha.dmp",
            "modulemanager.physics",
            "modulemanager.techtree",
            "toolbar-settings.dat",
            // LMP's distributable archive is client-owned, not a server-pack payload.
            "lunamultiplayer.zip"
        };

        public static ApfPackBuildResult Build(ApfPackBuildOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var source = RequireDirectory(options.SourceGameData, "source GameData");
            var output = Path.GetFullPath(options.OutputDirectory ?? string.Empty);
            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
                throw new ArgumentException("OutputDirectory is required", nameof(options));
            if (ApfPaths.IsUnder(output, source))
                throw new InvalidDataException("Output directory must be outside the source GameData directory");
            Directory.CreateDirectory(output);

            var archiveBase = RequireHttpsBase(options.ArchiveBaseUrl);
            RequireValue(options.ServerId, "ServerId");
            RequireValue(options.PackId, "PackId");
            RequireValue(options.PackVersion, "PackVersion");

            var manifest = new ApfPackManifest
            {
                ServerId = options.ServerId.Trim(),
                PackId = options.PackId.Trim(),
                PackVersion = options.PackVersion.Trim(),
                KspVersion = options.KspVersion?.Trim() ?? string.Empty,
                RequiredClientVersion = options.RequiredClientVersion?.Trim() ?? string.Empty
            };

            AddProtectedRoots(manifest, DefaultProtectedRoots);
            AddProtectedRoots(manifest, options.ProtectedRoots);
            AddRequiredDlc(manifest, options.RequiredDlc);

            var clientOnlyRoots = NormalizeClientOnlyRoots(options.ClientOnlyRoots);
            var topLevelDirectories = Directory.GetDirectories(source)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var buildResult = new ApfPackBuildResult();
            foreach (var directory in topLevelDirectories)
            {
                var rootName = Path.GetFileName(directory);
                var normalizedRoot = ApfPaths.Normalize(rootName);
                if (ApfPaths.IsBuiltInProtectedTopLevel(normalizedRoot) || IsProtected(manifest, normalizedRoot))
                    continue;
                if (clientOnlyRoots.Contains(normalizedRoot))
                    continue;

                var package = BuildManagedPackage(directory, rootName, output, archiveBase, manifest.PackVersion, options.ReuseExistingArchives);
                manifest.ManagedPackages.Add(package.Package);
                buildResult.ArchivePaths.Add(package.ArchivePath);
            }

            foreach (var clientRoot in clientOnlyRoots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                var sourceRoot = Path.Combine(source, clientRoot.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(sourceRoot))
                    throw new DirectoryNotFoundException("Client-only root does not exist: GameData/" + clientRoot);

                var rule = new ApfClientOnlyRule
                {
                    Id = "client-only-" + clientRoot.Replace('/', '-')
                };
                rule.PathRoots.Add("GameData/" + clientRoot);
                foreach (var dll in Directory.GetFiles(sourceRoot, "*.dll", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    rule.Dlls.Add(new ApfClientOnlyDll
                    {
                        Path = RelativeGameData(source, dll),
                        Sha256 = ApfJson.Sha256File(dll)
                    });
                }
                manifest.ClientOnly.Add(rule);
            }

            var looseFiles = Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var unknownLooseFiles = looseFiles.Where(file => !KnownGeneratedLooseFiles.Contains(ApfPaths.Normalize(file))).ToArray();
            AddProtectedRoots(manifest, looseFiles.Where(file => KnownGeneratedLooseFiles.Contains(ApfPaths.Normalize(file))));
            foreach (var fileName in unknownLooseFiles)
            {
                var package = BuildLooseFilePackage(Path.Combine(source, fileName), output, archiveBase, manifest.PackVersion, options.ReuseExistingArchives);
                manifest.ManagedPackages.Add(package.Package);
                buildResult.ArchivePaths.Add(package.ArchivePath);
            }

            var validation = ApfManifestValidator.Validate(manifest);
            if (!validation.IsValid)
                throw new InvalidDataException("Generated APF manifest is invalid: " + string.Join("; ", validation.Errors));

            var manifestPath = Path.Combine(output, "APFPack.json");
            File.WriteAllText(manifestPath, manifest.ToJson());
            buildResult.Manifest = manifest;
            buildResult.ManifestPath = manifestPath;
            return buildResult;
        }

        private static (ApfPackage Package, string ArchivePath) BuildManagedPackage(
            string sourceDirectory, string rootName, string outputDirectory, Uri archiveBase, string packVersion, bool reuseExisting)
        {
            if (!Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("Mod root contains no files: " + rootName);

            var archiveName = SafeFileName(rootName) + "-" + SafeFileName(packVersion) + ".zip";
            var archivePath = Path.Combine(outputDirectory, archiveName);
            if (File.Exists(archivePath) && !reuseExisting)
                throw new IOException("Refusing to overwrite existing archive: " + archivePath);

            // CreateFromDirectory(sourceDirectory, ...) makes archive entries
            // relative to rootName's contents (for example, "Plugin/foo.dll").
            // The manifest deliberately uses stripPrefix=rootName, so emit the
            // top-level root into each archive entry ("rootName/Plugin/foo.dll")
            // to keep builder output consumable by ApfArchiveInstaller.
            if (!File.Exists(archivePath))
            {
                var sourceRoot = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        var relative = file.Substring(sourceRoot.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        var entry = archive.CreateEntry(rootName + "/" + relative, CompressionLevel.Optimal);
                        using (var input = File.OpenRead(file))
                        using (var output = entry.Open())
                            input.CopyTo(output);
                    }
                }
            }
            var package = new ApfPackage
            {
                Id = rootName,
                Version = packVersion,
                ArchiveUrl = new Uri(archiveBase, Uri.EscapeDataString(archiveName)).AbsoluteUri,
                Sha256 = ApfJson.Sha256File(archivePath),
                SizeBytes = new FileInfo(archivePath).Length,
                Destination = "GameData",
                StripPrefix = rootName
            };
            return (package, archivePath);
        }

        private static (ApfPackage Package, string ArchivePath) BuildLooseFilePackage(
            string sourceFile, string outputDirectory, Uri archiveBase, string packVersion, bool reuseExisting)
        {
            var fileName = Path.GetFileName(sourceFile);
            if (!ApfPaths.IsSafeRelative(fileName))
                throw new InvalidDataException("Unsafe loose GameData file: " + fileName);

            var archiveName = "__APF_Loose__-" + SafeFileName(fileName) + "-" + SafeFileName(packVersion) + ".zip";
            var archivePath = Path.Combine(outputDirectory, archiveName);
            if (File.Exists(archivePath) && !reuseExisting)
                throw new IOException("Refusing to overwrite existing archive: " + archivePath);

            if (!File.Exists(archivePath))
            {
                using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
                    using (var input = File.OpenRead(sourceFile))
                    using (var output = entry.Open())
                        input.CopyTo(output);
                }
            }

            var package = new ApfPackage
            {
                Id = "loose-" + fileName,
                Version = packVersion,
                ArchiveUrl = new Uri(archiveBase, Uri.EscapeDataString(archiveName)).AbsoluteUri,
                Sha256 = ApfJson.Sha256File(archivePath),
                SizeBytes = new FileInfo(archivePath).Length,
                Destination = "GameData",
                StripPrefix = fileName
            };
            return (package, archivePath);
        }

        private static HashSet<string> NormalizeClientOnlyRoots(IEnumerable<string> roots)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                var normalized = ApfPaths.NormalizeGameDataRelative(root);
                if (!ApfPaths.IsSafeRelative(root) || string.IsNullOrEmpty(normalized) || normalized.Contains("/"))
                    throw new InvalidDataException("Client-only roots must be top-level safe GameData directories: " + root);
                if (!result.Add(normalized))
                    throw new InvalidDataException("Duplicate client-only root: " + root);
            }
            return result;
        }

        private static void AddProtectedRoots(ApfPackManifest manifest, IEnumerable<string> roots)
        {
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                var normalized = ApfPaths.NormalizeGameDataRelative(root);
                if (!ApfPaths.IsSafeRelative(root) || string.IsNullOrEmpty(normalized))
                    throw new InvalidDataException("Invalid protected root: " + root);
                if (!manifest.ProtectedRoots.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                    manifest.ProtectedRoots.Add(normalized);
            }
        }

        private static void AddRequiredDlc(ApfPackManifest manifest, IEnumerable<string> roots)
        {
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                var normalized = ApfPaths.NormalizeGameDataRelative(root);
                if (!ApfPaths.IsSafeRelative(root) || string.IsNullOrEmpty(normalized))
                    throw new InvalidDataException("Invalid required DLC path: " + root);
                if (!manifest.RequiredDlc.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                    manifest.RequiredDlc.Add(normalized);
            }
        }

        private static bool IsProtected(ApfPackManifest manifest, string root)
        {
            return (manifest.ProtectedRoots ?? new List<string>()).Any(value => PathsCollide(ApfPaths.NormalizeGameDataRelative(value), root));
        }

        private static bool PathsCollide(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
                || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static string RelativeGameData(string gameData, string path)
        {
            var root = Path.GetFullPath(gameData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            if (!ApfPaths.IsUnder(full, root)) throw new InvalidDataException("Path escapes GameData: " + path);
            return "GameData/" + full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string RequireDirectory(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                throw new DirectoryNotFoundException(name + " directory does not exist: " + path);
            return Path.GetFullPath(path);
        }

        private static void RequireValue(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " is required", name);
        }

        private static Uri RequireHttpsBase(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("ArchiveBaseUrl must be an HTTPS URL", nameof(value));
            var text = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
            return new Uri(text, UriKind.Absolute);
        }

        private static string SafeFileName(string value)
        {
            var chars = (value ?? "package").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray();
            return new string(chars.Length == 0 ? new[] { 'p' } : chars);
        }
    }
}
