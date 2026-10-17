using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace ApfCore
{
    public static class KesasInstallPolicy
    {
        // Used only by the external installer while holding the normal installer lock.
        public static bool SuppressRelaunch;
        private static bool _installerOpened;
        private static bool _pendingChecked;

        public static bool TryLaunchInstaller(string gameRoot, int waitForPid)
        {
            string root=Path.GetFullPath(gameRoot);
            string script=Path.Combine(root,"GameData","LunaMultiplayer","KESASLauncher.ps1");
            if(!File.Exists(script)) return false;
            ApfPaths.EnsureNoReparsePoints(root,script);
            var start=new ProcessStartInfo {
                FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),
                Arguments="-NoProfile -STA -ExecutionPolicy Bypass -File \""+script+"\" -GameRoot \""+root.TrimEnd('\\')+"\" -WaitForPid "+waitForPid,
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=root
            };
            Process.Start(start);
            return true;
        }

        public static bool LaunchPending(string gameRoot)
        {
            if(_installerOpened || _pendingChecked || string.IsNullOrWhiteSpace(gameRoot)) return false;
            _pendingChecked=true;
            string catalogFile=Path.Combine(gameRoot,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
            if(!File.Exists(catalogFile)) return false;
            // The catalog version is part of the signed client update. No mutable
            // marker is included in the signed inventory or deleted after install.
            string text=File.ReadAllText(catalogFile);
            var match=System.Text.RegularExpressions.Regex.Match(text,"\\\"version\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
            if(!match.Success) return false;
            string profileFile=Path.Combine(gameRoot,"GameData","LunaMultiplayer","Data","KesasInstallProfile.json");
            if(File.Exists(profileFile)) {
                var profile=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(profileFile));
                if(profile.ContainsKey("catalogVersion") && profile["catalogVersion"]==match.Groups[1].Value) return false;
            }
            _installerOpened=true;
            return TryLaunchInstaller(gameRoot,Process.GetCurrentProcess().Id);
        }

        public static ApfPackManifest PrepareScan(string gameData, ApfPackManifest manifest)
        {
            if (manifest == null || manifest.ClientOnly == null) return manifest;
            string game = new DirectoryInfo(gameData).Parent.FullName;
            string profile = Path.Combine(game, "GameData", "LunaMultiplayer", "Data", "KesasInstallProfile.json");
            if (!File.Exists(profile)) return manifest;
            var selected = ApfJson.Deserialize<Dictionary<string, string>>(File.ReadAllText(profile));
            if (!selected.ContainsKey("mode") || selected["mode"] != "potato") return manifest;
            string rootsFile = Path.Combine(game, "GameData", "LunaMultiplayer", "Launcher", "visual-roots.txt");
            if (!File.Exists(rootsFile)) throw new InvalidDataException("Visual profile information is missing. Run the KESAS launcher to repair it.");
            var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(rootsFile))
            {
                string root = line.Trim();
                if (root.Length == 0) continue;
                if (root.Contains("/") || root.Contains("\\") || root.Contains(":") || root == "." || root == "..")
                    throw new InvalidDataException("Invalid optional visual root");
                optional.Add(root);
            }
            var copy = ApfPackManifest.FromJson(manifest.ToJson());
            foreach (var rule in copy.ClientOnly)
            {
                if (rule == null || rule.Dlls == null) continue;
                var kept = new List<ApfClientOnlyDll>();
                foreach (var dll in rule.Dlls)
                {
                    string path = ApfPaths.NormalizeGameDataRelative(dll.Path);
                    string root = path.Split('/')[0];
                    // Missing optional renderers are valid in Potato. Present DLLs
                    // still undergo the original hash check; gameplay rules stay intact.
                    if (!optional.Contains(root) || Directory.Exists(Path.Combine(gameData, root)) || File.Exists(Path.Combine(gameData, root))) kept.Add(dll);
                }
                rule.Dlls = kept;
            }
            return copy;
        }
    }
}
