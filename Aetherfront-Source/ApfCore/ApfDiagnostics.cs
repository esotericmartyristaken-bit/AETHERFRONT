using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ApfCore
{
    public sealed class ApfDiagnosticContext
    {
        public int SchemaVersion { get; set; } = 1;
        public string ReportId { get; set; } = string.Empty;
        public string CreatedUtc { get; set; } = string.Empty;
        public string ClientVersion { get; set; } = string.Empty;
        public string KspVersion { get; set; } = string.Empty;
        public string Scene { get; set; } = string.Empty;
        public string NetworkState { get; set; } = string.Empty;
        public string ServerAddress { get; set; } = string.Empty;
        public int ServerPort { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public bool Is64BitProcess { get; set; }
        public long PrivateMemoryBytes { get; set; }
    }
    public sealed class ApfDiagnosticEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public long SourceBytes { get; set; }
        public long IncludedBytes { get; set; }
        public bool Truncated { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
    public sealed class ApfDiagnosticManifest
    {
        public ApfDiagnosticContext Context { get; set; } = new ApfDiagnosticContext();
        public List<ApfDiagnosticEntry> Entries { get; set; } = new List<ApfDiagnosticEntry>();
        public string Privacy { get; set; } = "Text logs are path/credential redacted. No saves, craft files, screenshots, or arbitrary files are included.";
    }

    public sealed class ApfDiagnosticBundleResult
    {
        public string ReportId { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public List<ApfDiagnosticEntry> Entries { get; set; } = new List<ApfDiagnosticEntry>();
    }
    public sealed class ApfDiagnosticModInventory
    {
        public List<ApfDiagnosticModRoot> Roots { get; set; } = new List<ApfDiagnosticModRoot>();
        public List<ApfDiagnosticDll> Dlls { get; set; } = new List<ApfDiagnosticDll>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
    public sealed class ApfDiagnosticModRoot
    {
        public string Name { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long SizeBytes { get; set; }
        public bool Truncated { get; set; }
    }
    public sealed class ApfDiagnosticDll
    {
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
    public sealed class ApfDiagnosticStoredMetadata
    {
        public string CaseId { get; set; } = string.Empty;
        public string ReportId { get; set; } = string.Empty;
        public string ReceivedUtc { get; set; } = string.Empty;
        public long ReceivedBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string ClientVersion { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    public static class ApfDiagnosticBundleBuilder
    {
        public const long MaximumArchiveBytes = 32L * 1024 * 1024;
        private const int MaximumLogBytes = 8 * 1024 * 1024;
        private const int MaximumSmallLogBytes = 2 * 1024 * 1024;
        private const int MaximumInventoryFiles = 5000;

        private static readonly Regex XmlSecret = new Regex(
            @"(?is)<(password|token|secret|authorization|webhook|apikey|api_key)(?:\s[^>]*)?>.*?</\1>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex AssignmentSecret = new Regex(
            @"(?im)\b(password|token|secret|authorization|webhook|api[_-]?key)\b\s*[:=]\s*[^\r\n,;}]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Email = new Regex(
            @"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static ApfDiagnosticBundleResult Build(string gameRoot, string outputDirectory, ApfDiagnosticContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            gameRoot = Path.GetFullPath(gameRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            outputDirectory = Path.GetFullPath(outputDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException("KSP root does not exist: " + gameRoot);
            if (!ApfPaths.IsUnder(outputDirectory, gameRoot)) throw new InvalidDataException("Diagnostic output directory must be beneath the KSP root");
            ApfPaths.EnsureNoReparsePoints(gameRoot, outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            context.ReportId = string.IsNullOrWhiteSpace(context.ReportId)
                ? "AF-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()
                : SafeIdentifier(context.ReportId);
            context.CreatedUtc = DateTime.UtcNow.ToString("O");
            context.OperatingSystem = Environment.OSVersion.VersionString;
            context.Is64BitProcess = Environment.Is64BitProcess;
            try { context.PrivateMemoryBytes = Process.GetCurrentProcess().PrivateMemorySize64; }
            catch { context.PrivateMemoryBytes = 0; }

            var archivePath = Path.Combine(outputDirectory, context.ReportId + ".zip");
            ApfPaths.EnsureNoReparsePoints(gameRoot, archivePath);
            if (File.Exists(archivePath)) throw new IOException("Diagnostic archive already exists: " + archivePath);

            var manifest = new ApfDiagnosticManifest { Context = context };
            try
            {
                using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    AddKnownLog(archive, manifest, gameRoot, "KSP.log", "logs/KSP.log", MaximumLogBytes);
                    AddKnownLog(archive, manifest, gameRoot, Path.Combine("Logs", "LMP", "VesselSyncLog.txt"), "logs/VesselSyncLog.txt", MaximumLogBytes);
                    AddKnownLog(archive, manifest, gameRoot, Path.Combine("Logs", "LMP", "NetworkLog.txt"), "logs/NetworkLog.txt", MaximumSmallLogBytes);
                    AddKnownLog(archive, manifest, gameRoot, Path.Combine("GameData", "LunaMultiplayer", "APFUpdater.log"), "logs/APFUpdater.log", MaximumSmallLogBytes);
                    AddKnownLog(archive, manifest, gameRoot, Path.Combine("APF", "APFTransaction.log"), "logs/APFTransaction.log", MaximumSmallLogBytes);
                    AddKnownLog(archive, manifest, gameRoot, Path.Combine("GameData", "LunaMultiplayer", "Data", "settings.xml"), "config/settings.redacted.xml", MaximumSmallLogBytes);

                    var playerLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "..", "LocalLow", "Squad", "Kerbal Space Program", "Player.log");
                    AddExternalLog(archive, manifest, Path.GetFullPath(playerLog), "logs/Player.log", MaximumLogBytes, gameRoot);

                    AddGeneratedText(archive, manifest, "inventory/mod-inventory.json", "generated",
                        BuildModInventory(Path.Combine(gameRoot, "GameData"), gameRoot));
                    AddGeneratedText(archive, manifest, "diagnostic.json", "generated", ApfJson.Serialize(manifest));
                }

                var size = new FileInfo(archivePath).Length;
                if (size > MaximumArchiveBytes)
                    throw new InvalidDataException("Diagnostic archive exceeds the 32 MiB upload limit");
                return new ApfDiagnosticBundleResult
                {
                    ReportId = context.ReportId,
                    ArchivePath = archivePath,
                    SizeBytes = size,
                    Entries = manifest.Entries
                };
            }
            catch
            {
                try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
                throw;
            }
        }

        public static string Redact(string value, string gameRoot)
        {
            var result = value ?? string.Empty;
            result = ReplacePath(result, gameRoot, "%KSP_ROOT%");
            result = ReplacePath(result, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            result = XmlSecret.Replace(result, match => "<" + match.Groups[1].Value + ">[REDACTED]</" + match.Groups[1].Value + ">");
            result = AssignmentSecret.Replace(result, match => match.Groups[1].Value + "=[REDACTED]");
            result = Email.Replace(result, "[REDACTED_EMAIL]");
            return result;
        }

        private static void AddKnownLog(ZipArchive archive, ApfDiagnosticManifest manifest, string gameRoot,
            string relativePath, string entryName, int maximumBytes)
        {
            var fullPath = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
            if (!ApfPaths.IsUnder(fullPath, gameRoot)) throw new InvalidDataException("Diagnostic source escapes KSP root");
            AddExternalLog(archive, manifest, fullPath, entryName, maximumBytes, gameRoot);
        }

        private static void AddExternalLog(ZipArchive archive, ApfDiagnosticManifest manifest, string fullPath,
            string entryName, int maximumBytes, string gameRoot)
        {
            if (!File.Exists(fullPath)) return;
            var info = new FileInfo(fullPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var bytes = ReadTail(fullPath, maximumBytes, out var truncated);
            var text = DecodeText(bytes);
            if (truncated) text = "[APF: beginning omitted; latest " + maximumBytes + " bytes retained]" + Environment.NewLine + text;
            AddGeneratedText(archive, manifest, entryName, SafeSource(fullPath, gameRoot), Redact(text, gameRoot), info.Length, truncated);
        }

        private static void AddGeneratedText(ZipArchive archive, ApfDiagnosticManifest manifest, string entryName,
            string source, string text, long sourceBytes = -1, bool truncated = false)
        {
            if (!ApfPaths.IsSafeRelative(entryName)) throw new InvalidDataException("Unsafe diagnostic entry path: " + entryName);
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            using (var output = entry.Open()) output.Write(bytes, 0, bytes.Length);
            manifest.Entries.Add(new ApfDiagnosticEntry
            {
                Name = entryName.Replace('\\', '/'),
                Source = source,
                SourceBytes = sourceBytes < 0 ? bytes.Length : sourceBytes,
                IncludedBytes = bytes.Length,
                Truncated = truncated,
                Sha256 = Hash(bytes)
            });
        }

        private static string BuildModInventory(string gameData, string gameRoot)
        {
            var inventory = new ApfDiagnosticModInventory();
            if (!Directory.Exists(gameData))
            {
                inventory.Warnings.Add("GameData is missing");
                return ApfJson.Serialize(inventory);
            }
            ApfPaths.EnsureNoReparsePoints(gameRoot, gameData);

            foreach (var directory in Directory.GetDirectories(gameData).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    inventory.Warnings.Add("Skipped reparse point: GameData/" + info.Name);
                    continue;
                }
                long bytes = 0;
                var files = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(MaximumInventoryFiles + 1))
                    {
                        files++;
                        if (files > MaximumInventoryFiles) break;
                        try { bytes += new FileInfo(file).Length; } catch { }
                    }
                }
                catch (Exception exception) { inventory.Warnings.Add(info.Name + ": " + exception.GetType().Name); }
                inventory.Roots.Add(new ApfDiagnosticModRoot
                {
                    Name = info.Name,
                    FileCount = Math.Min(files, MaximumInventoryFiles),
                    SizeBytes = bytes,
                    Truncated = files > MaximumInventoryFiles
                });
            }

            var seen = 0;
            foreach (var file in SafeEnumerateFiles(gameData))
            {
                if (++seen > MaximumInventoryFiles)
                {
                    inventory.Warnings.Add("DLL inventory truncated at " + MaximumInventoryFiles + " files");
                    break;
                }
                if (!string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var relative = file.Substring(gameData.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                    inventory.Dlls.Add(new ApfDiagnosticDll
                    {
                        Path = "GameData/" + relative,
                        SizeBytes = new FileInfo(file).Length,
                        Sha256 = ApfJson.Sha256File(file)
                    });
                }
                catch (Exception exception) { inventory.Warnings.Add(Path.GetFileName(file) + ": " + exception.GetType().Name); }
            }
            return ApfJson.Serialize(inventory);
        }

        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string[] files;
                string[] directories;
                try
                {
                    files = Directory.GetFiles(current);
                    directories = Directory.GetDirectories(current);
                }
                catch { continue; }
                foreach (var file in files) yield return file;
                foreach (var directory in directories)
                {
                    try
                    {
                        if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
                    }
                    catch { }
                }
            }
        }

        private static byte[] ReadTail(string path, int maximumBytes, out bool truncated)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                truncated = input.Length > maximumBytes;
                if (truncated) input.Seek(-maximumBytes, SeekOrigin.End);
                var length = (int)Math.Min(input.Length, maximumBytes);
                var output = new byte[length];
                var offset = 0;
                while (offset < output.Length)
                {
                    var read = input.Read(output, offset, output.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset == output.Length) return output;
                Array.Resize(ref output, offset);
                return output;
            }
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }

        private static string SafeSource(string fullPath, string gameRoot)
        {
            var redacted = Redact(fullPath, gameRoot).Replace('\\', '/');
            return redacted;
        }

        private static string ReplacePath(string text, string path, string replacement)
        {
            if (string.IsNullOrWhiteSpace(path)) return text;
            var variants = new[] { path.TrimEnd('\\', '/'), path.TrimEnd('\\', '/').Replace('\\', '/') };
            foreach (var variant in variants.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
                text = Regex.Replace(text, Regex.Escape(variant), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return text;
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return ApfJson.ToHex(sha.ComputeHash(bytes));
        }

        private static string SafeIdentifier(string value)
        {
            var safe = new string((value ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            if (string.IsNullOrWhiteSpace(safe) || safe.Length > 80) throw new InvalidDataException("Invalid diagnostic report ID");
            return safe;
        }
    }
    public sealed class ApfDiagnosticUploadReply
    {
        public string CaseId { get; set; } = string.Empty;
        public long ReceivedBytes { get; set; }
    }
}
