using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace ApfCore
{
    public static class ApfArchiveInstaller
    {
        private const int MaximumArchiveEntries = 10000;
        private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;

        public static void ApplyTransaction(ApfSyncTransaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            var manifestResult = ApfManifestValidator.Validate(transaction.Manifest);
            if (!manifestResult.IsValid) throw new InvalidDataException(string.Join("; ", manifestResult.Errors));

            var gameRoot = Path.GetFullPath(transaction.GameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var gameData = Path.GetFullPath(transaction.GameDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expectedGameData = Path.GetFullPath(Path.Combine(gameRoot, "GameData"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(gameRoot) || !Directory.Exists(gameData)
                || !string.Equals(gameData, expectedGameData, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid APF transaction roots");
            ApfPaths.EnsureNoReparsePoints(gameRoot, gameRoot);
            ApfPaths.EnsureNoReparsePoints(gameRoot, gameData);
            EnsureRelaunchTarget(transaction.KspExecutable, gameRoot);

            var protectedRoots = new HashSet<string>((transaction.Manifest.ProtectedRoots ?? new List<string>())
                .Select(ApfPaths.NormalizeGameDataRelative), StringComparer.OrdinalIgnoreCase);
            var mutationTargets = new List<string>();

            foreach (var staged in transaction.Packages ?? new List<ApfStagedPackage>())
            {
                if (staged == null || staged.Package == null)
                    throw new InvalidDataException("Transaction contains a null package");
                var package = transaction.Manifest.ManagedPackages.FirstOrDefault(p => string.Equals(p.Id, staged.Package.Id, StringComparison.OrdinalIgnoreCase));
                if (package == null ||
                    !string.Equals(package.Version, staged.Package.Version, StringComparison.Ordinal) ||
                    !string.Equals(package.ArchiveUrl, staged.Package.ArchiveUrl, StringComparison.Ordinal) ||
                    !string.Equals(package.Sha256, staged.Package.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    package.SizeBytes != staged.Package.SizeBytes ||
                    !string.Equals(ApfPaths.Normalize(package.Destination), ApfPaths.Normalize(staged.Package.Destination), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ApfPaths.Normalize(package.StripPrefix), ApfPaths.Normalize(staged.Package.StripPrefix), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Transaction package is not the manifest package: " + staged.Package?.Id);
                if (!ApfPaths.IsUnder(staged.ArchivePath, Path.Combine(gameRoot, "APF", ".staging")))
                    throw new InvalidDataException("Staged archive escapes APF staging");
                ApfPaths.EnsureNoReparsePoints(gameRoot, staged.ArchivePath);
                ValidateArchive(staged.ArchivePath, package, gameData);

                var root = ApfPaths.PackageRoot(staged.Package);
                if (string.IsNullOrEmpty(root) || protectedRoots.Any(p => PathsCollide(p, root)) ||
                    ApfPaths.IsBuiltInProtectedTopLevel(root.Split('/')[0]))
                    throw new InvalidDataException("Package attempts to replace a protected root: " + staged.Package.Id);
                mutationTargets.Add(Path.Combine(gameData, root.Replace('/', Path.DirectorySeparatorChar)));
            }

            foreach (var deletion in transaction.Deletes ?? new List<ApfDeleteChange>())
            {
                if (deletion == null || !ApfPaths.IsSafeRelative(deletion.RelativePath))
                    throw new InvalidDataException("Unsafe APF deletion path: " + deletion?.RelativePath);
                var relative = ApfPaths.NormalizeGameDataRelative(deletion.RelativePath);
                if (string.IsNullOrEmpty(relative)) throw new InvalidDataException("Refusing to delete GameData root");
                var top = relative.Split('/')[0];
                if (protectedRoots.Any(p => PathsCollide(p, relative)) || ApfPaths.IsBuiltInProtectedTopLevel(top))
                    throw new InvalidDataException("Refusing to delete protected path: " + deletion.RelativePath);
                mutationTargets.Add(Path.Combine(gameData, relative.Replace('/', Path.DirectorySeparatorChar)));
            }

            EnsureMutationTargetsDoNotOverlap(mutationTargets);
            var statePath = string.IsNullOrWhiteSpace(transaction.StatePath) ? string.Empty : Path.GetFullPath(transaction.StatePath);
            if (!string.IsNullOrEmpty(statePath))
            {
                if (!ApfPaths.IsUnder(statePath, gameRoot) || statePath.Equals(gameRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("State path escapes or equals game root");
                EnsureNoReparsePoints(gameRoot, statePath);
            }
            WaitForProcessExit(transaction.ProcessId, transaction.KspExecutable);
            EnsureGameDataWritable(gameData, transaction);

            var transactionLog = Path.Combine(gameRoot, "APF", "APFTransaction.log");
            var backupDirectory = Path.Combine(gameRoot, "APF", ".transaction-backup", Guid.NewGuid().ToString("N"));
            ApfPaths.EnsureNoReparsePoints(gameRoot, transactionLog);
            ApfPaths.EnsureNoReparsePoints(gameRoot, backupDirectory);
            var backups = new List<PathBackup>();
            var committed = false;
            try
            {
                Directory.CreateDirectory(backupDirectory);
                AppendLog(transactionLog, "START pack=" + transaction.Manifest.PackId + " version=" + transaction.Manifest.PackVersion);

                foreach (var target in mutationTargets)
                    MovePathToBackup(target, gameData, backupDirectory, backups);

                if (!string.IsNullOrEmpty(statePath))
                {
                    if (PathExists(statePath))
                        MovePathToBackup(statePath, gameRoot, backupDirectory, backups);
                }

                var state = transaction.ExistingState ?? new ApfPackState();
                if (state.Packages == null) state.Packages = new List<ApfPackageState>();
                state.PackId = transaction.Manifest.PackId;
                state.PackVersion = transaction.Manifest.PackVersion;
                var manifestIds = new HashSet<string>(transaction.Manifest.ManagedPackages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
                state.Packages.RemoveAll(p => p == null || !manifestIds.Contains(p.Id));
                foreach (var staged in transaction.Packages ?? new List<ApfStagedPackage>())
                {
                    ExtractPackage(staged.ArchivePath, staged.Package, gameData);
                    var root = ApfPaths.PackageRoot(staged.Package);
                    state.Packages.RemoveAll(p => string.Equals(p.Id, staged.Package.Id, StringComparison.OrdinalIgnoreCase));
                    state.Packages.Add(new ApfPackageState
                    {
                        Id = staged.Package.Id,
                        Version = staged.Package.Version,
                        ArchiveSha256 = staged.Package.Sha256,
                        Root = root,
                        Files = ApfPackScanner.BuildFileState(gameData, Path.Combine(gameData, root.Replace('/', Path.DirectorySeparatorChar)))
                    });
                }

                if (!string.IsNullOrEmpty(statePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                    var temp = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temp, ApfJson.Serialize(state));
                    if (File.Exists(statePath)) File.Replace(temp, statePath, null);
                    else File.Move(temp, statePath);
                }

                AppendLog(transactionLog, "COMPLETED packages=" + (transaction.Packages?.Count ?? 0) + " deletes=" + (transaction.Deletes?.Count ?? 0));
                committed = true;
            }
            catch
            {
                RollbackPackTransaction(mutationTargets, gameData, statePath, backups);
                AppendLog(transactionLog, "ROLLED-BACK pack=" + transaction.Manifest.PackId + " version=" + transaction.Manifest.PackVersion);
                throw;
            }
            finally
            {
                if (committed)
                {
                    try { if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, true); }
                    catch (Exception exception) { AppendLog(transactionLog, "BACKUP-RETAINED " + exception.Message); }
                }
                else
                {
                    // Keep failed-transaction backups. They are the recovery path
                    // if a rollback itself encounters a locked file or filesystem
                    // failure; the next APF run can report/clean them explicitly.
                    AppendLog(transactionLog, "BACKUP-RETAINED " + backupDirectory);
                }
            }

            Relaunch(transaction);
        }

        public static void ApplyClientUpdate(ApfClientUpdateTransaction transaction)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            var manifestResult = ApfClientUpdateValidator.Validate(transaction.Manifest, false);
            if (!manifestResult.IsValid) throw new InvalidDataException(string.Join("; ", manifestResult.Errors));

            var gameRoot = Path.GetFullPath(transaction.GameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var gameData = Path.GetFullPath(transaction.GameDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expectedGameData = Path.GetFullPath(Path.Combine(gameRoot, "GameData"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(gameRoot) || !Directory.Exists(gameData)
                || !string.Equals(gameData, expectedGameData, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid APF client update roots");
            ApfPaths.EnsureNoReparsePoints(gameRoot, gameRoot);
            ApfPaths.EnsureNoReparsePoints(gameRoot, gameData);
            EnsureRelaunchTarget(transaction.KspExecutable, gameRoot);

            var stagingRoot = Path.Combine(gameRoot, "APF", ".client-update-staging");
            if (!ApfPaths.IsUnder(transaction.ArchivePath, stagingRoot))
                throw new InvalidDataException("Client update archive escapes APF staging");
            ApfPaths.EnsureNoReparsePoints(gameRoot, transaction.ArchivePath);
            ValidateClientUpdateArchive(transaction.ArchivePath, transaction.Manifest, gameRoot);

            WaitForProcessExit(transaction.ProcessId, transaction.KspExecutable);
            var replaced = new List<ClientUpdateReplacement>();
            try
            {
                using (var zip = ZipFile.OpenRead(transaction.ArchivePath))
                {
                    foreach (var file in transaction.Manifest.Files)
                    {
                        var relative = ApfPaths.Normalize(file.Path);
                        var target = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                        if (!ApfPaths.IsUnder(target, Path.Combine(gameRoot, "GameData", "LunaMultiPlayer", "Plugins")) ||
                            !ApfClientUpdateValidator.IsAllowedFile(relative))
                            throw new InvalidDataException("Client update target is not an allowed plugin path: " + file.Path);
                        EnsureNoReparsePoints(gameData, target);
                        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                            throw new IOException("Client update target is read-only: " + target);

                        var entry = zip.Entries.FirstOrDefault(candidate =>
                            !string.IsNullOrEmpty(candidate.Name) &&
                            string.Equals(ApfPaths.Normalize(candidate.FullName), relative, StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new InvalidDataException("Client update archive entry is missing: " + file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        var temporary = target + ".apf-new-" + Guid.NewGuid().ToString("N");
                        using (var input = entry.Open())
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            input.CopyTo(output);

                        var replacement = new ClientUpdateReplacement { Target = target, Temporary = temporary };
                        if (File.Exists(target))
                        {
                            replacement.Backup = target + ".apf-backup-" + Guid.NewGuid().ToString("N");
                            File.Replace(temporary, target, replacement.Backup);
                        }
                        else
                        {
                            File.Move(temporary, target);
                            replacement.Created = true;
                        }
                        replaced.Add(replacement);
                    }
                }
            }
            catch
            {
                RollbackClientUpdate(replaced);
                throw;
            }

            foreach (var replacement in replaced)
            {
                try { if (!string.IsNullOrEmpty(replacement.Backup) && File.Exists(replacement.Backup)) File.Delete(replacement.Backup); }
                catch { }
            }
            AppendLog(Path.Combine(gameRoot, "APF", "APFTransaction.log"),
                "CLIENT-UPDATE version=" + transaction.Manifest.Version + " files=" + transaction.Manifest.Files.Count);
            Relaunch(transaction);
        }

        public static void ValidateClientUpdateArchive(string archivePath, ApfClientUpdateManifest manifest, string gameRoot)
        {
            var manifestResult = ApfClientUpdateValidator.Validate(manifest, false);
            if (!manifestResult.IsValid) throw new InvalidDataException(string.Join("; ", manifestResult.Errors));
            if (!File.Exists(archivePath)) throw new FileNotFoundException("Staged APF client update archive missing", archivePath);
            if (!string.Equals(ApfJson.Sha256File(archivePath), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Client update archive SHA-256 does not match manifest");
            if (new FileInfo(archivePath).Length != manifest.SizeBytes)
                throw new InvalidDataException("Client update archive size does not match manifest");

            var expected = manifest.Files.ToDictionary(file => ApfPaths.Normalize(file.Path), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            var fileCount = 0;
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (++fileCount > MaximumArchiveEntries) throw new InvalidDataException("Client update archive has too many files");
                    if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - expandedBytes)
                        throw new InvalidDataException("Client update archive expands beyond the safety limit");
                    expandedBytes += entry.Length;
                    var normalized = ApfPaths.Normalize(entry.FullName);
                    if (!ApfPaths.IsSafeRelative(entry.FullName) || !expected.TryGetValue(normalized, out var expectedFile))
                        throw new InvalidDataException("Unexpected client update archive entry: " + entry.FullName);
                    if (!seen.Add(normalized)) throw new InvalidDataException("Duplicate client update archive entry: " + entry.FullName);
                    if (entry.Length != expectedFile.SizeBytes) throw new InvalidDataException("Client update file size does not match manifest: " + entry.FullName);
                    using (var stream = entry.Open())
                    using (var sha = SHA256.Create())
                    {
                        var actual = ApfJson.ToHex(sha.ComputeHash(stream));
                        if (!string.Equals(actual, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Client update file SHA-256 does not match manifest: " + entry.FullName);
                    }
                }
            }
            if (seen.Count != expected.Count) throw new InvalidDataException("Client update archive is missing one or more manifest files");
        }

        private static void EnsureGameDataWritable(string gameDataPath, ApfSyncTransaction transaction)
        {
            var mutations = (transaction.Packages?.Count ?? 0) > 0 || (transaction.Deletes?.Count ?? 0) > 0;
            if (!mutations) return;
            var probe = Path.Combine(gameDataPath, ".apf-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (File.Create(probe)) { }
                File.Delete(probe);
            }
            catch (Exception exception)
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                throw new IOException("KSP GameData is not writable; no APF changes applied", exception);
            }

            foreach (var deletion in transaction.Deletes ?? new List<ApfDeleteChange>())
                EnsureNoReadOnlyFiles(Path.Combine(gameDataPath, ApfPaths.NormalizeGameDataRelative(deletion.RelativePath).Replace('/', Path.DirectorySeparatorChar)));
            foreach (var staged in transaction.Packages ?? new List<ApfStagedPackage>())
                EnsureNoReadOnlyFiles(Path.Combine(gameDataPath, ApfPaths.PackageRoot(staged.Package).Replace('/', Path.DirectorySeparatorChar)));

            if (!string.IsNullOrWhiteSpace(transaction.StatePath))
                EnsureNoReadOnlyFiles(Path.GetFullPath(transaction.StatePath));
        }

        private static void EnsureNoReadOnlyFiles(string path)
        {
            if (PathExists(path))
                EnsureNoReparsePoints(Path.GetDirectoryName(path) ?? path, path);
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("APF target is read-only: " + path);
                return;
            }
            if (!Directory.Exists(path)) return;
            EnsureNoReparsePointsRecursively(path, Path.GetFullPath(path));
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) throw new IOException("APF target contains a read-only file: " + file);
        }

        private static void EnsureMutationTargetsDoNotOverlap(IEnumerable<string> targets)
        {
            var unique = targets
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (var left = 0; left < unique.Length; left++)
                for (var right = left + 1; right < unique.Length; right++)
                    if (ApfPaths.IsUnder(unique[left], unique[right]) || ApfPaths.IsUnder(unique[right], unique[left]))
                        throw new InvalidDataException("APF transaction mutation targets overlap: " + unique[left] + " and " + unique[right]);
        }

        private static void MovePathToBackup(string target, string root, string backupDirectory, List<PathBackup> backups)
        {
            target = Path.GetFullPath(target);
            if (!ApfPaths.IsUnder(target, root)) throw new InvalidDataException("APF backup target escapes root: " + target);
            if (!PathExists(target)) return;
            EnsureNoReparsePoints(root, target);
            EnsureNoReparsePointsRecursively(target, root);

            var backupPath = Path.Combine(backupDirectory, "item-" + backups.Count.ToString("D6"));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
            var isDirectory = Directory.Exists(target);
            if (isDirectory) Directory.Move(target, backupPath);
            else File.Move(target, backupPath);
            backups.Add(new PathBackup { Target = target, Backup = backupPath, IsDirectory = isDirectory });
        }

        private static void RollbackPackTransaction(IEnumerable<string> mutationTargets, string gameDataPath, string statePath, List<PathBackup> backups)
        {
            // Remove anything created by the failed extraction before restoring
            // the original roots. Refuse to recurse through a newly-created link.
            foreach (var target in (mutationTargets ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!PathExists(target)) continue;
                    EnsureNoReparsePoints(gameDataPath, target);
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    else File.Delete(target);
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(statePath))
            {
                try
                {
                    if (PathExists(statePath))
                    {
                        EnsureNoReparsePoints(Path.GetDirectoryName(statePath), statePath);
                        if (Directory.Exists(statePath)) Directory.Delete(statePath, true);
                        else File.Delete(statePath);
                    }
                }
                catch { }
            }

            foreach (var backup in (backups ?? new List<PathBackup>()).AsEnumerable().Reverse())
            {
                try
                {
                    if (!PathExists(backup.Backup)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(backup.Target));
                    if (backup.IsDirectory) Directory.Move(backup.Backup, backup.Target);
                    else File.Move(backup.Backup, backup.Target);
                }
                catch { }
            }
        }

        private static bool PathExists(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private static void AppendLog(string path, string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine);
            }
            catch { }
        }

        public static void ValidateArchive(string archivePath, ApfPackage package, string gameDataPath)
        {
            if (!File.Exists(archivePath)) throw new FileNotFoundException("Staged APF archive missing", archivePath);
            if (!string.Equals(ApfJson.Sha256File(archivePath), package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive SHA-256 does not match manifest: " + package.Id);
            if (package.SizeBytes > 0 && new FileInfo(archivePath).Length != package.SizeBytes)
                throw new InvalidDataException("Archive size does not match manifest: " + package.Id);

            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var destinations = new List<string>();
                long expandedBytes = 0;
                var entryCount = 0;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (++entryCount > MaximumArchiveEntries) throw new InvalidDataException("APF archive has too many files");
                    if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - expandedBytes)
                        throw new InvalidDataException("APF archive expands beyond the safety limit");
                    expandedBytes += entry.Length;
                    var output = ResolveEntryPath(entry.FullName, package, gameDataPath);
                    var relativeOutput = output.Substring(Path.GetFullPath(gameDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                    if (destinations.Any(existing => PathsCollide(existing, relativeOutput)))
                        throw new InvalidDataException("APF archive contains duplicate or overlapping output paths: " + entry.FullName);
                    destinations.Add(relativeOutput);
                }
                if (entryCount == 0) throw new InvalidDataException("APF archive contains no files");
            }
        }

        public static List<string> ExtractPackage(string archivePath, ApfPackage package, string gameDataPath)
        {
            ValidateArchive(archivePath, package, gameDataPath);
            var installed = new List<string>();
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destination = ResolveEntryPath(entry.FullName, package, gameDataPath);
                    EnsureNoReparsePoints(gameDataPath, destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    installed.Add(destination);
                }
            }
            return installed;
        }

        private static string ResolveEntryPath(string entryName, ApfPackage package, string gameDataPath)
        {
            var normalized = entryName.Replace('\\', '/').Trim('/');
            if (!ApfPaths.IsSafeRelative(normalized) || ApfManifestValidator.IsBlockedArchiveEntry(normalized))
                throw new InvalidDataException("Unsafe APF archive entry: " + entryName);

            var prefix = ApfPaths.Normalize(package.StripPrefix);
            if (!string.IsNullOrEmpty(prefix) && !normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive entry is outside stripPrefix: " + entryName);

            var preserveArchivePrefix = string.IsNullOrEmpty(ApfPaths.NormalizeGameDataRelative(package.Destination));
            var relative = preserveArchivePrefix ? normalized : normalized.Length == prefix.Length ? string.Empty : normalized.Substring(prefix.Length + 1);
            if (string.IsNullOrEmpty(relative)) throw new InvalidDataException("Archive contains only its stripPrefix directory");

            var destinationRoot = ApfPaths.NormalizeGameDataRelative(package.Destination);
            var relativeOutput = string.IsNullOrEmpty(destinationRoot) ? relative : destinationRoot + "/" + relative;
            var output = Path.Combine(gameDataPath, relativeOutput.Replace('/', Path.DirectorySeparatorChar));
            if (!ApfPaths.IsUnder(output, gameDataPath)) throw new InvalidDataException("Archive entry escapes GameData: " + entryName);
            EnsureNoReparsePoints(gameDataPath, output);
            return output;
        }

        private static void EnsureNoReparsePoints(string gameDataPath, string candidate)
        {
            var root = Path.GetFullPath(gameDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullCandidate = Path.GetFullPath(candidate);
            if (!ApfPaths.IsUnder(fullCandidate, root)) throw new InvalidDataException("Path escapes GameData: " + candidate);

            var current = fullCandidate;
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refusing to traverse a reparse point: " + current);
            }

            var parent = Directory.GetParent(current);
            while (parent != null)
            {
                var parentPath = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!ApfPaths.IsUnder(parentPath, root)) break;
                if (Directory.Exists(parentPath) && (File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refusing to traverse a reparse point: " + parentPath);
                if (parentPath.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                parent = parent.Parent;
            }
        }

        private static void EnsureNoReparsePointsRecursively(string candidate, string root)
        {
            if (!PathExists(candidate)) return;
            EnsureNoReparsePoints(root, candidate);
            if (!Directory.Exists(candidate)) return;
            foreach (var child in Directory.GetFileSystemEntries(candidate))
                EnsureNoReparsePointsRecursively(child, root);
        }

        private static void WaitForProcessExit(int processId, string expectedExecutable)
        {
            if (processId <= 0) return;
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    if (process.HasExited) return;
                    string actualExecutable;
                    try { actualExecutable = process.MainModule?.FileName; }
                    catch (Exception exception) { throw new InvalidDataException("Could not verify the original KSP process before mutation", exception); }
                    if (string.IsNullOrWhiteSpace(actualExecutable) ||
                        !string.Equals(Path.GetFullPath(actualExecutable), Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("APF transaction process ID does not belong to the original KSP executable");
                    process.WaitForExit();
                }
            }
            catch (ArgumentException) { }
        }

        private static bool PathsCollide(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
                || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static void Relaunch(ApfSyncTransaction transaction)
        {
            EnsureRelaunchTarget(transaction.KspExecutable, transaction.GameRoot);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = transaction.KspExecutable,
                WorkingDirectory = transaction.GameRoot,
                UseShellExecute = false,
                Arguments = ApfProcessArguments.Join(transaction.KspArguments ?? new List<string>())
            };
            System.Diagnostics.Process.Start(startInfo);
        }

        private static void Relaunch(ApfClientUpdateTransaction transaction)
        {
            EnsureRelaunchTarget(transaction.KspExecutable, transaction.GameRoot);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = transaction.KspExecutable,
                WorkingDirectory = transaction.GameRoot,
                UseShellExecute = false,
                Arguments = ApfProcessArguments.Join(transaction.KspArguments ?? new List<string>())
            };
            System.Diagnostics.Process.Start(startInfo);
        }

        private static void EnsureRelaunchTarget(string executable, string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(executable))
                throw new FileNotFoundException("KSP executable missing", executable);

            var fullExecutable = Path.GetFullPath(executable);
            var fullRoot = Path.GetFullPath(gameRoot);
            if (!ApfPaths.IsUnder(fullExecutable, fullRoot) ||
                ApfPaths.IsUnder(fullExecutable, Path.Combine(fullRoot, "GameData")) ||
                !string.Equals(Path.GetExtension(fullExecutable), ".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullExecutable))
                throw new InvalidDataException("KSP executable must be an existing .exe outside GameData beneath the cloned KSP root: " + executable);
            ApfPaths.EnsureNoReparsePoints(fullRoot, fullExecutable);
        }

        private static void RollbackClientUpdate(List<ClientUpdateReplacement> replaced)
        {
            for (var index = replaced.Count - 1; index >= 0; index--)
            {
                var replacement = replaced[index];
                try
                {
                    if (!string.IsNullOrEmpty(replacement.Backup) && File.Exists(replacement.Backup))
                    {
                        if (File.Exists(replacement.Target)) File.Delete(replacement.Target);
                        File.Move(replacement.Backup, replacement.Target);
                    }
                    else if (replacement.Created && File.Exists(replacement.Target)) File.Delete(replacement.Target);
                }
                catch { }
                try { if (File.Exists(replacement.Temporary)) File.Delete(replacement.Temporary); } catch { }
            }
        }

        private sealed class ClientUpdateReplacement
        {
            public string Target { get; set; }
            public string Temporary { get; set; }
            public string Backup { get; set; }
            public bool Created { get; set; }
        }

        private sealed class PathBackup
        {
            public string Target { get; set; }
            public string Backup { get; set; }
            public bool IsDirectory { get; set; }
        }
    }
}
