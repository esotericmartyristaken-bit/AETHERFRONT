using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ApfCore
{
    public static class ApfPaths
    {
        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var segments = path.Trim().Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("/", segments).ToLowerInvariant();
        }

        public static string NormalizeGameDataRelative(string path)
        {
            var value = Normalize(path);
            if (value == "gamedata")
                return string.Empty;
            if (value.StartsWith("gamedata/", StringComparison.Ordinal))
                value = value.Substring("gamedata/".Length);
            return value;
        }

        public static bool IsSafeRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(path))
                return false;
            if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
                return false;

            var segments = Normalize(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 && segments.All(IsSafeSegment);
        }

        private static bool IsSafeSegment(string segment)
        {
            if (segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(" ", StringComparison.Ordinal) || segment.Contains(":"))
                return false;
            if (segment.Any(character => Path.GetInvalidFileNameChars().Contains(character)))
                return false;
            var deviceName = segment.Split('.')[0];
            return !ReservedDeviceNames.Contains(deviceName);
        }

        public static bool IsUnder(string candidate, string root)
        {
            var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies the existing portion of a path does not traverse a junction,
        /// symlink, or other Windows reparse point. This is intentionally safe for
        /// paths that do not exist yet: every existing parent is still checked.
        /// </summary>
        public static void EnsureNoReparsePoints(string root, string candidate)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullCandidate = Path.GetFullPath(candidate);
            if (!IsUnder(fullCandidate, fullRoot))
                throw new InvalidDataException("Path escapes its allowed root: " + candidate);

            var current = fullCandidate;
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to traverse a reparse point: " + current);

            var parent = Directory.GetParent(current);
            while (parent != null)
            {
                var parentPath = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!IsUnder(parentPath, fullRoot)) break;
                if (Directory.Exists(parentPath) &&
                    (File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refusing to traverse a reparse point: " + parentPath);
                if (parentPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) break;
                parent = parent.Parent;
            }
        }

        public static string PackageRoot(ApfPackage package)
        {
            var destination = NormalizeGameDataRelative(package.Destination);
            return string.IsNullOrEmpty(destination) ? Normalize(package.StripPrefix) : destination;
        }

        public static IEnumerable<string> TopLevelRoots(string relativePath)
        {
            var normalized = NormalizeGameDataRelative(relativePath);
            if (string.IsNullOrEmpty(normalized))
                yield break;
            yield return normalized.Split('/')[0];
        }

        public static bool IsBuiltInProtectedTopLevel(string normalized)
        {
            var value = Normalize(normalized);
            return value.StartsWith("squad", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("lunamultiplayer", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("dlc", StringComparison.OrdinalIgnoreCase);
        }
    }
}
