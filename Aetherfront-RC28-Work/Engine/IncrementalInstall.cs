using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ApfCore;

namespace KesasInstall
{
    // A completed helper authenticates the inventory, not file mtimes. Normal
    // updates trust that managed baseline; explicit Repair inspects live bytes.
    public sealed class IncrementalInstall
    {
        private readonly Dictionary<string,ApfPackageState> packages;
        public ApfClientUpdateManifest ClientManifest {get;private set;}
        private IncrementalInstall(ApfPackState state,ApfClientUpdateManifest manifest)
        {
            packages=state.Packages.ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
            ClientManifest=manifest;
        }
        public static IncrementalInstall Load(string root,string mode)
        {
            try {
                root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                var receipt=Installer.ReadCompletedReceipt(root);
                if(receipt==null || !Value(receipt,"incrementalSchema","1") || !Value(receipt,"mode",mode) ||
                    !receipt.TryGetValue("gameRoot",out var gameRoot) || !string.Equals(root,gameRoot,StringComparison.OrdinalIgnoreCase))return null;
                string statePath=Path.Combine(root,"GameData","LunaMultiplayer","Data","APFPackState.json");
                string stateText=ReadBound(root,statePath,32L*1024*1024,receipt,"stateSha256");
                string manifestText=ReadBound(root,Path.Combine(root,"APF","InstalledClient.json"),4L*1024*1024,receipt,"installedManifestSha256");
                var manifest=ApfClientUpdateManifest.FromJson(manifestText);
                if(!ApfClientUpdateValidator.Validate(manifest).IsValid || !Value(receipt,"clientManifestSha256",Hash(Encoding.UTF8.GetBytes(manifest.ToJson()))))return null;
                string publicPath=Path.Combine(root,"GameData","LunaMultiplayer","Data","APFClientUpdatePublicKey.xml");
                ApfPaths.EnsureNoReparsePoints(root,publicPath);
                using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicPath)))if(!ApfClientUpdateSecurity.Verify(manifest,key))return null;
                var state=ApfJson.Deserialize<ApfPackState>(stateText);
                if(state==null || state.Packages==null || !Value(receipt,"packId",state.PackId) || !Value(receipt,"packVersion",state.PackVersion))return null;
                return new IncrementalInstall(state,manifest);
            }catch(Exception error) when(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is InvalidOperationException || error is FormatException || error is CryptographicException ||
                error.GetType().FullName.StartsWith("JsonFx.",StringComparison.Ordinal)){return null;}
        }
        public bool CanReuse(ApfPackage package,ApfPackageState current)
        {
            return package!=null && current!=null && packages.TryGetValue(package.Id,out var previous) &&
                current.Version==package.Version && previous.Version==current.Version &&
                string.Equals(current.ArchiveSha256,package.Sha256,StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.ArchiveSha256,current.ArchiveSha256,StringComparison.OrdinalIgnoreCase) &&
                previous.Files!=null && previous.Files.Count>0 && current.Files!=null &&
                RuntimeFiles.SameFiles(previous.Files,current.Files);
        }
        public static void Commit(string root,Dictionary<string,string> receipt,ApfClientUpdateManifest manifest,string statePath)
        {
            string saved=Path.Combine(root,"APF","InstalledClient.json");
            ApfPaths.EnsureNoReparsePoints(root,saved);ApfPaths.EnsureNoReparsePoints(root,statePath);
            Installer.AtomicText(saved,manifest.ToJson());
            receipt["incrementalSchema"]="1";receipt["gameRoot"]=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            receipt["installedManifestSha256"]=ApfJson.Sha256File(saved);
            receipt["stateSha256"]=ApfJson.Sha256File(statePath);
        }
        private static bool Value(Dictionary<string,string> receipt,string key,string expected)
        {
            return receipt.TryGetValue(key,out var actual) && string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase);
        }
        private static string Hash(byte[] bytes){using(var hash=SHA256.Create())return ApfJson.ToHex(hash.ComputeHash(bytes));}
        private static string ReadBound(string root,string path,long maximum,Dictionary<string,string> receipt,string key)
        {
            ApfPaths.EnsureNoReparsePoints(root,path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length>maximum)throw new InvalidDataException("Incremental metadata is too large.");
                var bytes=new byte[(int)stream.Length];int offset=0,count;
                while(offset<bytes.Length && (count=stream.Read(bytes,offset,bytes.Length-offset))>0)offset+=count;
                if(offset!=bytes.Length || (receipt!=null && !Value(receipt,key,Hash(bytes))))throw new InvalidDataException("Incremental inventory changed.");
                return new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
            }
        }
    }
}
