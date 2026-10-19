using System;
using System.IO;
using System.Linq;
using ApfCore;

namespace KesasInstall
{
    internal static class ObsoleteScripts
    {
        // Called only after StartupCasing authenticates the completed release.
        // Delete exact retired distribution bytes, never modified user scripts.
        internal static void Remove(string root, ApfClientUpdateManifest manifest)
        {
            string[][] retired = {
                new[] { "GameData/LunaMultiplayer/KESASLauncher.ps1", "0cda5b90152fc9a0ce2d6feb39e663a9e9e56c8e07ace459edfa4b4f3be7c7e6" },
                new[] { "GameData/LunaMultiplayer/RepairClientCasing.ps1", "83d2dad02b42ddc03d39d81fd530a2eee61b291d276f6240d411184a7a3fbfc6" },
                new[] { "KESAS/Verify-Install.ps1", "e2be0679099aa9b606c8aa8bea572ce7645849a3b91ae300d1fd3fce829e0447" }
            };
            if (manifest == null || manifest.Files == null) throw new InvalidDataException("Missing authenticated client inventory.");
            foreach (var item in retired) {
                if (manifest.Files.Any(f => string.Equals(f.Path, item[0], StringComparison.OrdinalIgnoreCase))) continue;
                string path = Path.Combine(root, item[0].Replace('/', Path.DirectorySeparatorChar));
                ApfPaths.EnsureNoReparsePoints(root, path);
                try {
                    if (!File.Exists(path) || !string.Equals(ApfJson.Sha256File(path), item[1], StringComparison.OrdinalIgnoreCase)) continue;
                    ApfPaths.EnsureNoReparsePoints(root, path);
                    File.Delete(path);
                } catch (IOException) {
                    // Optional cleanup never prevents startup if a script is open.
                    // Retry the same hash-checked deletion on the next launch.
                } catch (UnauthorizedAccessException) {}
            }
        }
    }
}
