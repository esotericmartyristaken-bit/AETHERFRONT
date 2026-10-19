using System;
using System.IO;
using System.Linq;
using ApfCore;

namespace KesasInstall
{
    // Exact mod-owned user settings, never a general PluginData/file-type rule.
    public static class UserSettings
    {
        private static readonly string[] paths={
            "BonVoyage/Plugins/PluginData/BonVoyage/config.xml",
            "ConnectedLivingSpace/Plugins/PluginData/CLS_Settings.dat",
            "ExtraplanetaryLaunchpads/Plugins/PluginData/Launchpad/Settings.cfg",
            "KerbalKonstructs/PluginDataWindowPositions.cfg",
            "kOS/Plugins/PluginData/kOS/config.xml",
            "ShipManifest/Plugins/PluginData/SMSettings.dat",
            "TriggerTech/KerbalAlarmClock/Plugins/PluginData/settings.cfg",
            "WaypointManager/PluginData/WaypointManager.cfg",
            "Astrogator/AstrogatorSettings.cfg",
            "EditorExtensionsRedux/PluginData/RememEditor.cfg",
            "EditorExtensionsRedux/Plugins/config.xml",
            "Hangar/Plugins/PluginData/Hangar/config.xml",
            "TweakScale/plugins/PluginData/Scale/config.xml",
            "REPOSoftTech/DeepFreeze/PluginData/Config.cfg",
            "[x]_Science!/PluginData/settings.cfg",
            "PlanetShine/Plugins/PluginData/Settings.cfg"
        };
        public static string[] Paths {get{return (string[])paths.Clone();}}
        public static bool IsSetting(string relative)
        {
            string path=ApfPaths.NormalizeGameDataRelative(relative);
            return paths.Contains(path,StringComparer.OrdinalIgnoreCase);
        }
        internal static void PreserveExisting(string root,string preparedData,ApfPackage package)
        {
            string identity=ApfPaths.PackageRoot(package);
            foreach(string relative in paths.Where(p=>p.StartsWith(identity+"/",StringComparison.OrdinalIgnoreCase))) {
                string source=Path.Combine(root,"GameData",relative.Replace('/',Path.DirectorySeparatorChar));
                string target=Path.Combine(preparedData,relative.Replace('/',Path.DirectorySeparatorChar));
                ApfPaths.EnsureNoReparsePoints(root,source);ApfPaths.EnsureNoReparsePoints(root,target);
                if(Directory.Exists(source))throw new InvalidDataException("User settings path is a directory.");
                if(!File.Exists(source))continue;
                PreparedPack.PlainDirectory(root,Path.GetDirectoryName(source));
                PreparedPack.SettingsDirectory(root,Path.GetDirectoryName(target));
                using(var input=PreparedPack.SettingsFile(root,source,FileMode.Open,FileAccess.Read)) {
                    if(input.Length>4L*1024*1024)throw new InvalidDataException("User settings exceed the preservation size limit.");
                    // Open without truncation, validate its actual handle, then
                    // modify only this owned staging file. CreateNew cannot
                    // follow an attacker-supplied link for a missing target.
                    using(var output=PreparedPack.SettingsFile(root,target,File.Exists(target)?FileMode.Open:FileMode.CreateNew,FileAccess.ReadWrite)) {
                        output.SetLength(0);input.CopyTo(output);output.Flush(true);
                    }
                }
            }
        }
    }
}
