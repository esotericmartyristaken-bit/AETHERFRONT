using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ApfCore
{
    public sealed class ApfValidationResult
    {
        public List<string> Errors { get; } = new List<string>();
        public bool IsValid => Errors.Count == 0;

        public void Add(string error)
        {
            if (!string.IsNullOrWhiteSpace(error))
                Errors.Add(error);
        }
    }

    public static class ApfManifestValidator
    {
        private static readonly Regex Sha256 = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
        private static readonly HashSet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".js", ".scr"
        };

        public static ApfValidationResult Validate(ApfPackManifest manifest)
        {
            var result = new ApfValidationResult();
            if (manifest == null)
            {
                result.Add("Manifest is null");
                return result;
            }

            if (manifest.SchemaVersion != 1) result.Add("Unsupported schemaVersion");
            Require(manifest.ServerId, "serverId", result);
            Require(manifest.PackId, "packId", result);
            Require(manifest.PackVersion, "packVersion", result);
            if (manifest.ManagedPackages == null) result.Add("managedPackages is missing");
            if (manifest.ClientOnly == null) result.Add("clientOnly is missing");

            var protectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in manifest.ProtectedRoots ?? new List<string>())
            {
                var normalized = ApfPaths.NormalizeGameDataRelative(root);
                if (string.IsNullOrEmpty(normalized) || !ApfPaths.IsSafeRelative(root)) result.Add("Invalid protected root: " + root);
                else if (!protectedRoots.Add(normalized)) result.Add("Duplicate protected root: " + root);
            }

            var requiredDlc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dlc in manifest.RequiredDlc ?? new List<string>())
            {
                var normalized = ApfPaths.NormalizeGameDataRelative(dlc);
                if (string.IsNullOrEmpty(normalized) || !ApfPaths.IsSafeRelative(dlc))
                    result.Add("Invalid required DLC path: " + dlc);
                else if (!ApfPaths.IsBuiltInProtectedTopLevel(normalized.Split('/')[0]))
                    result.Add("Required DLC must be beneath a protected DLC root: " + dlc);
                else if (!requiredDlc.Add(normalized))
                    result.Add("Duplicate required DLC path: " + dlc);
            }

            var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packageRoots = new List<string>();
            foreach (var package in manifest.ManagedPackages ?? new List<ApfPackage>())
            {
                if (package == null) { result.Add("managedPackages contains null"); continue; }
                if (!packageIds.Add(package.Id ?? string.Empty)) result.Add("Duplicate package id: " + package.Id);
                Require(package.Id, "package id", result);
                Require(package.Version, "package version", result);
                if (!Uri.TryCreate(package.ArchiveUrl, UriKind.Absolute, out var archiveUri) || archiveUri.Scheme != Uri.UriSchemeHttps)
                    result.Add("Package archiveUrl must use HTTPS: " + package.Id);
                if (!Sha256.IsMatch(package.Sha256 ?? string.Empty)) result.Add("Package sha256 must be 64 hex characters: " + package.Id);
                if (package.SizeBytes < 0) result.Add("Package sizeBytes cannot be negative: " + package.Id);
                ValidatePath(package.Destination, "package destination", result, true);
                ValidatePath(package.StripPrefix, "package stripPrefix", result, false);
                if (string.IsNullOrEmpty(ApfPaths.Normalize(package.StripPrefix))) result.Add("Package stripPrefix is required: " + package.Id);

                var root = ApfPaths.PackageRoot(package);
                if (protectedRoots.Any(p => PathsCollide(p, root)) ||
                    ApfPaths.IsBuiltInProtectedTopLevel(root.Split('/')[0]))
                    result.Add("Package writes into protected root: " + package.Id);
                if (packageRoots.Any(p => PathsCollide(p, root)))
                    result.Add("Package destination collides with another package: " + package.Id);
                packageRoots.Add(root);
            }

            var clientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clientOnlyRoots = new List<string>();
            var clientOnlyDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in manifest.ClientOnly ?? new List<ApfClientOnlyRule>())
            {
                if (rule == null) { result.Add("clientOnly contains null"); continue; }
                if (!clientIds.Add(rule.Id ?? string.Empty)) result.Add("Duplicate client-only id: " + rule.Id);
                Require(rule.Id, "client-only id", result);
                if ((rule.PathRoots == null || rule.PathRoots.Count == 0) && (rule.Dlls == null || rule.Dlls.Count == 0))
                    result.Add("Client-only rule must declare pathRoots or dlls: " + rule.Id);
                foreach (var pathRoot in rule.PathRoots ?? new List<string>())
                {
                    ValidatePath(pathRoot, "client-only path root", result, true);
                    var normalized = ApfPaths.NormalizeGameDataRelative(pathRoot);
                    if (string.IsNullOrEmpty(normalized)) result.Add("Client-only path root cannot be GameData: " + rule.Id);
                    else if (normalized.Contains("/")) result.Add("Client-only path root must be a top-level GameData directory: " + pathRoot);
                    if (protectedRoots.Any(p => PathsCollide(p, normalized)) ||
                        (!string.IsNullOrEmpty(normalized) && ApfPaths.IsBuiltInProtectedTopLevel(normalized.Split('/')[0])))
                        result.Add("Client-only rule writes into protected root: " + rule.Id);
                    if (packageRoots.Any(p => PathsCollide(p, normalized)))
                        result.Add("Client-only rule collides with a managed package: " + rule.Id);
                    if (clientOnlyRoots.Any(p => PathsCollide(p, normalized)))
                        result.Add("Client-only path root collides with another rule: " + rule.Id);
                    clientOnlyRoots.Add(normalized);
                }

                foreach (var dll in rule.Dlls ?? new List<ApfClientOnlyDll>())
                {
                    if (dll == null) { result.Add("client-only dll list contains null"); continue; }
                    ValidatePath(dll.Path, "client-only dll path", result, true);
                    if (!string.IsNullOrEmpty(dll.Sha256) && !Sha256.IsMatch(dll.Sha256)) result.Add("Invalid client-only dll sha256: " + dll.Path);
                    var dllPath = ApfPaths.NormalizeGameDataRelative(dll.Path);
                    if (string.IsNullOrEmpty(dllPath) || !clientOnlyRoots.Any(root => dllPath.Equals(root, StringComparison.OrdinalIgnoreCase) || dllPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                        result.Add("Client-only dll must be beneath a declared path root: " + dll.Path);
                    else if (!clientOnlyDlls.Add(dllPath))
                        result.Add("Duplicate client-only dll path: " + dll.Path);
                }
            }

            return result;
        }

        public static bool IsBlockedArchiveEntry(string path)
        {
            return BlockedExtensions.Contains(System.IO.Path.GetExtension(path));
        }

        private static void Require(string value, string name, ApfValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(value)) result.Add(name + " is required");
        }

        private static bool PathsCollide(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
                || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidatePath(string path, string name, ApfValidationResult result, bool allowGameDataPrefix)
        {
            if (string.IsNullOrWhiteSpace(path)) { result.Add(name + " is required"); return; }
            if (!ApfPaths.IsSafeRelative(path)) { result.Add("Unsafe " + name + ": " + path); return; }
            var normalized = ApfPaths.Normalize(path);
            if (allowGameDataPrefix && normalized != "gamedata" && !normalized.StartsWith("gamedata/", StringComparison.Ordinal))
                result.Add(name + " must be beneath GameData: " + path);
        }
    }
}
