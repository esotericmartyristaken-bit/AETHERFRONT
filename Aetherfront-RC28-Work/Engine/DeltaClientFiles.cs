using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ApfCore;

namespace KesasInstall
{
    public static class DeltaClientFiles
    {
        [ThreadStatic]private static string root;
        [ThreadStatic]private static ApfClientUpdateManifest source;
        [ThreadStatic]private static List<ApfClientUpdateFile> selected;
        public static void Begin(ApfClientUpdateTransaction transaction)
        {
            root=Path.GetFullPath(transaction.GameRoot);source=null;selected=null;
        }
        // Called only from ApplyClientUpdate AFTER the unchanged full archive,
        // RSA, approval and stopped-game checks. The signed manifest is never
        // edited; only the replacement/backup/final-write enumeration narrows.
        public static List<ApfClientUpdateFile> Files(ApfClientUpdateManifest manifest)
        {
            if(root==null)throw new InvalidOperationException("Client delta has no transaction root.");
            if(ReferenceEquals(source,manifest) && selected!=null)return selected;
            var files=new List<ApfClientUpdateFile>();
            var physical=typeof(ApfArchiveInstaller).GetMethod("PhysicalClientPath",BindingFlags.Static|BindingFlags.NonPublic);
            if(physical==null)throw new MissingMethodException("PhysicalClientPath");
            foreach(var file in manifest.Files) {
                string relative=(string)physical.Invoke(null,new object[]{file.Path});
                string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
                if(!ApfPaths.IsUnder(path,root))throw new InvalidDataException("Client delta path escapes the game root.");
                ApfPaths.EnsureNoReparsePoints(root,path);
                bool unchanged=false;
                if(File.Exists(path) && new FileInfo(path).Length==file.SizeBytes) {
                    unchanged=PreparedPack.ClientUnchanged(root,file) || string.Equals(ApfJson.Sha256File(path),file.Sha256,StringComparison.OrdinalIgnoreCase);
                }
                if(!unchanged)files.Add(file);
            }
            source=manifest;selected=files;
            return selected;
        }
    }
}
