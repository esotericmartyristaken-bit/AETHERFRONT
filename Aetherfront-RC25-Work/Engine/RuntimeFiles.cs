using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ApfCore;

namespace KesasInstall
{
    public static class RuntimeFiles
    {
        // These locations contain settings written by the installed mods. Part
        // definitions, assemblies, models and textures are never exempted.
        public static bool IsMutable(string value)
        {
            string path=ApfPaths.NormalizeGameDataRelative(value).ToLowerInvariant();
            string extension=Path.GetExtension(path);
            if(path=="kopernicus/cache/mesh.hash")return true;
            if(extension==".bin"&&(path.StartsWith("kcalbelohsystem/cache/",StringComparison.Ordinal)||path.StartsWith("opm/cache/",StringComparison.Ordinal)||path.StartsWith("kopernicus/cache/",StringComparison.Ordinal)))return true;
            if(extension==".glob"&&path.StartsWith("000_at_utils/plugins/plugindata/",StringComparison.Ordinal))return true;
            if(!new[]{".cfg",".xml",".json",".txt",".dat",".user",".cache",".log"}.Contains(extension))return false;
            if(path.StartsWith("000_at_utils/",StringComparison.Ordinal)&&extension==".user")return true;
            if(path=="kcalbelohsystem/kcalbeloh system settings.cfg"||path=="contractconfigurator/settings.cfg"||path=="contractconfigurator/biomedata.cfg"||path=="kopernicus/config/kopernicus_config.cfg")return true;
            string[] roots={"000_at_utils/","001_toolbarcontrol/","bettertimewarp/","contractconfigurator/","fmrs/","kcalbelohsystem/","kerbalengineer/","kopernicus/","opm/","scansat/"};
            return roots.Any(root=>path.StartsWith(root,StringComparison.Ordinal))&&(path.Contains("/plugindata/")||path.Contains("/settings/")||path.Contains("/logs/"));
        }
        public static bool SameFiles(IEnumerable<ApfFileState> expected,IEnumerable<ApfFileState> actual)
        {
            var left=(expected??Enumerable.Empty<ApfFileState>()).Where(f=>!IsMutable(f.Path)).OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            var right=(actual??Enumerable.Empty<ApfFileState>()).Where(f=>!IsMutable(f.Path)).OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            return left.Length==right.Length&&left.Zip(right,(a,b)=>string.Equals(a.Path,b.Path,StringComparison.OrdinalIgnoreCase)&&string.Equals(a.Sha256,b.Sha256,StringComparison.OrdinalIgnoreCase)).All(value=>value);
        }
        public static ApfSyncPlan ScanFinal(string gameData,ApfPackManifest manifest,ApfPackState state)
        {
            return ApfPackScanner.Scan(gameData,manifest,state);
        }
    }
}
