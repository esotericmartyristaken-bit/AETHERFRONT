using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ApfCore
{
    public static class ApfPackScanner
    {
        public static ApfSyncPlan Scan(string gameDataPath, ApfPackManifest manifest, ApfPackState state)
        {
            var plan = new ApfSyncPlan { PackId = manifest?.PackId ?? string.Empty, PackVersion = manifest?.PackVersion ?? string.Empty };
            var validation = ApfManifestValidator.Validate(manifest);
            plan.Errors.AddRange(validation.Errors);
            if (!validation.IsValid) return plan;
            if (!Directory.Exists(gameDataPath)) { plan.Errors.Add("GameData directory does not exist"); return plan; }

            try
            {
                var reparsePoint = FindReparsePoint(gameDataPath);
                if (reparsePoint != null)
                {
                    plan.Errors.Add("GameData contains a reparse point; remove junctions/symlinks before APF synchronization: " +
                        Relative(gameDataPath, reparsePoint));
                    return plan;
                }
            }
            catch (Exception exception)
            {
                plan.Errors.Add("GameData could not be safely scanned: " + exception.Message);
                return plan;
            }

            var statePackages = (state?.Packages ?? new List<ApfPackageState>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var managedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var protectedRoots = new HashSet<string>((manifest.ProtectedRoots ?? new List<string>()).Select(ApfPaths.NormalizeGameDataRelative), StringComparer.OrdinalIgnoreCase);
            var protectedTopRoots = new HashSet<string>(protectedRoots
                .Where(root => !string.IsNullOrEmpty(root))
                .Select(root => root.Split('/')[0]), StringComparer.OrdinalIgnoreCase);

            foreach (var package in manifest.ManagedPackages)
            {
                var root = ApfPaths.PackageRoot(package);
                foreach (var top in ApfPaths.TopLevelRoots(root)) managedRoots.Add(top);

                if (!statePackages.TryGetValue(package.Id, out var installed) ||
                    !string.Equals(installed.Version, package.Version, StringComparison.Ordinal) ||
                    !string.Equals(installed.ArchiveSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    plan.AddInstall(package, "Package is missing or changed");
                    continue;
                }

                var rootPath = Path.Combine(gameDataPath, root.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(rootPath) && !File.Exists(rootPath))
                {
                    plan.AddInstall(package, "Package files are missing");
                    continue;
                }

                var actual = BuildFileState(gameDataPath, rootPath);
                if (!SameFiles(installed.Files, actual))
                    plan.AddInstall(package, "Package files were modified");
            }

            foreach (var rule in manifest.ClientOnly ?? new List<ApfClientOnlyRule>())
            {
                foreach (var pathRoot in rule.PathRoots ?? new List<string>())
                    foreach (var top in ApfPaths.TopLevelRoots(pathRoot)) managedRoots.Add(top);

                foreach (var dll in rule.Dlls ?? new List<ApfClientOnlyDll>())
                {
                    if (string.IsNullOrWhiteSpace(dll.Sha256)) continue;
                    var relative = ApfPaths.NormalizeGameDataRelative(dll.Path);
                    var path = Path.Combine(gameDataPath, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path))
                    {
                        plan.Errors.Add("Client-only DLL is missing: " + dll.Path);
                    }
                    else if (!string.Equals(ApfJson.Sha256File(path), dll.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.Errors.Add("Client-only DLL hash mismatch: " + dll.Path);
                    }
                }
            }

            foreach (var entry in Directory.GetFileSystemEntries(gameDataPath))
            {
                var name = Path.GetFileName(entry);
                var normalized = ApfPaths.Normalize(name);
                if (protectedTopRoots.Contains(normalized) || ApfPaths.IsBuiltInProtectedTopLevel(normalized) || managedRoots.Contains(normalized))
                    continue;

                plan.AddDelete("GameData/" + name, "Not in the server pack or client-only allowlist");
            }

            return plan;
        }

        public static List<ApfFileState> BuildFileState(string gameDataPath, string rootPath)
        {
            var files = new List<ApfFileState>();
            if (File.Exists(rootPath))
            {
                files.Add(new ApfFileState { Path = Relative(gameDataPath, rootPath), Sha256 = ApfJson.Sha256File(rootPath) });
                return files;
            }

            if (!Directory.Exists(rootPath)) return files;
            foreach (var file in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories))
                files.Add(new ApfFileState { Path = Relative(gameDataPath, file), Sha256 = ApfJson.Sha256File(file) });
            return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool SameFiles(IEnumerable<ApfFileState> expected, IEnumerable<ApfFileState> actual)
        {
            var left = (expected ?? Enumerable.Empty<ApfFileState>()).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var right = (actual ?? Enumerable.Empty<ApfFileState>()).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            return left.Length == right.Length && left.Zip(right, (a, b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase)).All(x => x);
        }

        private static string Relative(string gameDataPath, string path)
        {
            var relative = path.Substring(Path.GetFullPath(gameDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "GameData/" + relative.Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
        }

        private static string FindReparsePoint(string gameDataPath)
        {
            var root = Path.GetFullPath(gameDataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return current;

                if (!Directory.Exists(current)) continue;
                foreach (var child in Directory.GetFileSystemEntries(current))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        return child;
                    if (Directory.Exists(child)) pending.Push(child);
                }
            }
            return null;
        }
    }

    public sealed class ApfSyncPlan
    {
        public string PackId { get; set; } = string.Empty;
        public string PackVersion { get; set; } = string.Empty;
        public List<ApfPackageChange> Install { get; } = new List<ApfPackageChange>();
        public List<ApfDeleteChange> Delete { get; } = new List<ApfDeleteChange>();
        public List<ApfSyncOperation> Operations { get; } = new List<ApfSyncOperation>();
        public List<string> Errors { get; } = new List<string>();
        public bool IsClean => Errors.Count == 0 && Install.Count == 0 && Delete.Count == 0;
        public ApfPackStatus Status => Errors.Count > 0 ? ApfPackStatus.Invalid : IsClean ? ApfPackStatus.Clean : ApfPackStatus.SyncRequired;

        internal void AddInstall(ApfPackage package, string reason)
        {
            Install.Add(new ApfPackageChange { Package = package, Reason = reason });
            Operations.Add(new ApfSyncOperation
            {
                Kind = "install",
                PackageId = package?.Id ?? string.Empty,
                RelativePath = package?.Destination ?? string.Empty,
                Reason = reason,
                SizeBytes = package?.SizeBytes ?? 0
            });
        }

        internal void AddDelete(string relativePath, string reason)
        {
            Delete.Add(new ApfDeleteChange { RelativePath = relativePath, Reason = reason });
            Operations.Add(new ApfSyncOperation
            {
                Kind = "delete",
                RelativePath = relativePath,
                Reason = reason
            });
        }
    }

    public sealed class ApfPackageChange
    {
        public ApfPackage Package { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string StagedArchivePath { get; set; } = string.Empty;
    }

    public sealed class ApfDeleteChange
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
