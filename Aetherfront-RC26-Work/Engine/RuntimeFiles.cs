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
            // Observed game-created files, not mod content. Keep these exact:
            // ContractConfigurator uses singular "log" and Engineer logs at root.
            if(path.StartsWith("contractconfigurator/log/",StringComparison.Ordinal)&&extension==".log")return true;
            if(path=="kerbalengineer/kerbalengineer.log")return true;
            if(path=="kspcommunityfixes/plugindata/pngtexturecache.cfg")return true;
            const string textureCache="kspcommunityfixes/plugindata/texturecache/";
            if(path.StartsWith(textureCache,StringComparison.Ordinal)) {
                string name=path.Substring(textureCache.Length);
                if(name=="texturedata.json"||name=="progressmarker")return true;
                // KSPCF writes raw texture bytes under an unsigned 32-bit ID.
                // No extension, nested paths, code or arbitrary cache filenames.
                if(name.Length>0&&name.Length<=10&&name.All(c=>c>='0'&&c<='9')&&uint.TryParse(name,out _))return true;
            }
            if(path=="zzz_kesasperformancefixes/plugindata/assetbundledefinitions.v1.cache"||
               path=="zzz_kesasperformancefixes/plugindata/assetbundledefinitions.v1.cache.tmp")return true;
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
        // Only the scanner's call site uses this. Archive/preparation inventory
        // must continue hashing every extracted file for its authenticated receipt.
        public static List<ApfFileState> ScanFiles(string gameDataPath,string rootPath)
        {
            string gameData=Path.GetFullPath(gameDataPath).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string packagePath=Path.GetFullPath(rootPath);
            if(!ApfPaths.IsUnder(packagePath,gameData))throw new InvalidDataException("Package scan escapes GameData.");
            var prepared=PreparedPack.Files(gameData,packagePath);
            if(prepared!=null)return prepared;
            var files=new List<ApfFileState>();
            if(!File.Exists(packagePath)&&!Directory.Exists(packagePath))return files;
            ApfPaths.EnsureNoReparsePoints(gameData,packagePath);
            var pending=new Stack<string>();pending.Push(packagePath);
            while(pending.Count>0) {
                string path=pending.Pop();
                ApfPaths.EnsureNoReparsePoints(gameData,path);
                var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Package scan refuses reparse points.");
                if((attributes&FileAttributes.Directory)!=0) {
                    // Never let recursive enumeration traverse a child before
                    // checking it, including a newly introduced empty junction.
                    foreach(string child in Directory.GetFileSystemEntries(path)) {
                        if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Package scan refuses reparse points.");
                        pending.Push(child);
                    }
                    continue;
                }
                string relative="GameData/"+path.Substring(gameData.Length+1).Replace('\\','/').ToLowerInvariant();
                if(!IsMutable(relative))files.Add(new ApfFileState{Path=relative,Sha256=ApfJson.Sha256File(path)});
            }
            return files.OrderBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static ApfSyncPlan ScanFinal(string gameData,ApfPackManifest manifest,ApfPackState state)
        {
            try{return ApfPackScanner.Scan(gameData,manifest,state);}
            finally{PreparedPack.ReleaseDirectories();}
        }
    }
}
