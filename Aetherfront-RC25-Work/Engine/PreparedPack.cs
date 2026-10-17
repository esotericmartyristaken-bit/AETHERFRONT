using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApfCore;

namespace KesasInstall
{
    // Extraction is performed in private staging while the game remains open.
    // A second process locks and verifies those bytes before authorizing exit.
    public sealed class PreparedPackage
    {
        public string Id { get; set; }
        public string ArchiveSha256 { get; set; }
        public string Root { get; set; }
        public List<ApfFileState> Files { get; set; }
    }
    public sealed class PreparedReceipt
    {
        public string Authorization { get; set; }
        public string DataDirectory { get; set; }
        public string BundledClientSha256 { get; set; }
        public string DuplicateClientSha256 { get; set; }
        public List<PreparedPackage> Packages { get; set; }
    }
    public sealed class PreparedEnvelope
    {
        public string Payload { get; set; }
        public string Mac { get; set; }
    }
    public static class PreparedPack
    {
        private static readonly List<FileStream> locks = new List<FileStream>();
        private static readonly Dictionary<string,FileStream> fileLocks = new Dictionary<string,FileStream>(StringComparer.OrdinalIgnoreCase);
        private static PreparedReceipt active;
        private static ApfSyncTransaction transaction;
        private static string preparedData;
        private static ApfClientUpdateTransaction bundledClient;
        private static string duplicateClient;
        public static void Prepare(ApfSyncTransaction value,string requestPath,Action<string> progress,Func<bool> canceled)
        {
            string root=Path.GetFullPath(value.GameRoot);
            // Keep extraction close to the game root. Nesting beneath the long
            // transaction path exceeds MAX_PATH for otherwise valid mod files.
            string relative="APF/p/"+Guid.NewGuid().ToString("N").Substring(0,12)+"/GameData";
            string data=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
            ApfPaths.EnsureNoReparsePoints(root,data);
            if(Directory.Exists(data))throw new InvalidDataException("Prepared installation directory already exists.");
            Directory.CreateDirectory(data);
            var receipt=new PreparedReceipt{Authorization=value.Authorization,DataDirectory=relative,Packages=new List<PreparedPackage>()};
            string clientPath=requestPath+".client";
            ApfPaths.EnsureNoReparsePoints(root,clientPath);
            if(File.Exists(clientPath)){if(new FileInfo(clientPath).Length>16L*1024*1024)throw new InvalidDataException("Bundled client request is too large.");receipt.BundledClientSha256=ApfJson.Sha256File(clientPath);}
            string duplicateMarker=requestPath+".duplicate";
            ApfPaths.EnsureNoReparsePoints(root,duplicateMarker);
            if(File.Exists(duplicateMarker)) {
                string duplicate=Path.Combine(root,"Resources","GameData","LunaMultiplayer","Plugins","LmpClient.dll");
                ApfPaths.EnsureNoReparsePoints(root,duplicate);
                if(!File.Exists(duplicate))throw new InvalidDataException("Approved duplicate multiplayer client is missing.");
                receipt.DuplicateClientSha256=ApfJson.Sha256File(duplicate);
            }
            var extract=typeof(ApfArchiveInstaller).GetMethod("ExtractPackage",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
            if(extract==null)throw new MissingMethodException("Package extraction entry point is missing.");
            int index=0;
            foreach(var staged in value.Packages) {
                if(canceled())throw new OperationCanceledException();
                progress("Preparing package "+(++index)+" / "+value.Packages.Count+": "+ApfPaths.PackageRoot(staged.Package));
                try{extract.Invoke(null,new object[]{staged.ArchivePath,staged.Package,data});}
                catch(TargetInvocationException error){throw error.InnerException??error;}
                string packageRelative=ApfPaths.PackageRoot(staged.Package);
                receipt.Packages.Add(new PreparedPackage{Id=staged.Package.Id,ArchiveSha256=staged.Package.Sha256,Root=packageRelative,Files=ApfPackScanner.BuildFileState(data,Path.Combine(data,packageRelative.Replace('/',Path.DirectorySeparatorChar)))});
            }
            if(canceled())throw new OperationCanceledException();
            string payload=ApfJson.Serialize(receipt);
            byte[] key=ApfTransactionSecurity.LoadKey(Path.Combine(root,"GameData","LunaMultiplayer","Data","APFHelper.key"));
            Installer.AtomicText(requestPath+".prepared",ApfJson.Serialize(new PreparedEnvelope{Payload=payload,Mac=Mac(payload,key)}));
        }
        public static void Open(ApfSyncTransaction value,string requestPath)
        {
            string path=requestPath+".prepared";
            if(!File.Exists(path))return; // Authenticated historical transactions remain supported.
            string root=Path.GetFullPath(value.GameRoot);
            ApfPaths.EnsureNoReparsePoints(root,path);
            if(new FileInfo(path).Length>32L*1024*1024)throw new InvalidDataException("Prepared receipt is too large.");
            var envelope=ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(path));
            byte[] key=ApfTransactionSecurity.LoadKey(Path.Combine(root,"GameData","LunaMultiplayer","Data","APFHelper.key"));
            if(envelope==null||envelope.Payload==null||!SameMac(envelope.Mac,Mac(envelope.Payload,key)))throw new InvalidDataException("Prepared receipt authorization failed.");
            var receipt=ApfJson.Deserialize<PreparedReceipt>(envelope.Payload);
            if(receipt==null||receipt.Authorization!=value.Authorization||receipt.Packages==null||receipt.Packages.Count!=value.Packages.Count)throw new InvalidDataException("Prepared receipt does not match this transaction.");
            if(receipt.DataDirectory==null||!Regex.IsMatch(receipt.DataDirectory,"^APF/p/[0-9a-f]{12}/GameData$"))throw new InvalidDataException("Prepared directory is not an owned short staging path.");
            string data=Path.Combine(root,receipt.DataDirectory.Replace('/',Path.DirectorySeparatorChar));
            ApfPaths.EnsureNoReparsePoints(root,data);
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                if(!string.IsNullOrEmpty(receipt.DuplicateClientSha256)) {
                    string duplicate=Path.Combine(root,"Resources","GameData","LunaMultiplayer");
                    ValidateTree(root,duplicate);
                    if(!string.Equals(ApfJson.Sha256File(Path.Combine(duplicate,"Plugins","LmpClient.dll")),receipt.DuplicateClientSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Approved duplicate multiplayer client changed; approve again.");
                    duplicateClient=duplicate;
                }
                string clientPath=requestPath+".client";
                ApfPaths.EnsureNoReparsePoints(root,clientPath);
                if(!string.IsNullOrEmpty(receipt.BundledClientSha256)) {
                    if(!File.Exists(clientPath)||new FileInfo(clientPath).Length>16L*1024*1024||!string.Equals(ApfJson.Sha256File(clientPath),receipt.BundledClientSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Bundled client request changed.");
                    var client=ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(clientPath));
                    if(client==null||!ApfTransactionSecurity.Verify(client,key)||!string.Equals(Path.GetFullPath(client.GameRoot),root,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFullPath(client.GameDataPath),Path.Combine(root,"GameData"),StringComparison.OrdinalIgnoreCase)||client.ProcessId!=value.ProcessId||client.Manifest.Version!=value.Manifest.RequiredClientVersion||client.Approval==null)throw new InvalidDataException("Bundled client authorization differs from the pack.");
                    string publicKey=Path.Combine(root,"GameData","LunaMultiplayer","Data","APFClientUpdatePublicKey.xml");
                    ApfPaths.EnsureNoReparsePoints(root,publicKey);
                    using(var signing=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))if(!ApfClientUpdateSecurity.Verify(client.Manifest,signing))throw new InvalidDataException("Bundled client signature is invalid.");
                    if(!ApfPaths.IsUnder(client.ArchivePath,Path.Combine(root,"APF",".client-update-staging")))throw new InvalidDataException("Bundled client archive escapes staging.");
                    ApfPaths.EnsureNoReparsePoints(root,client.ArchivePath);
                    locks.Add(new FileStream(client.ArchivePath,FileMode.Open,FileAccess.Read,FileShare.Read));
                    ApfArchiveInstaller.ValidateClientUpdateArchive(client.ArchivePath,client.Manifest,root);
                    client.Approval.EnsureCurrent(root);
                    bundledClient=client;
                }else if(File.Exists(clientPath))throw new InvalidDataException("Unsigned bundled client request.");
                foreach(var prepared in receipt.Packages) {
                    var package=value.Packages.SingleOrDefault(p=>p.Package.Id==prepared.Id)?.Package;
                    if(package==null||!ids.Add(prepared.Id)||prepared.ArchiveSha256!=package.Sha256||prepared.Root!=ApfPaths.PackageRoot(package)||prepared.Files==null)throw new InvalidDataException("Prepared package identity differs.");
                    string packageRoot=Path.Combine(data,prepared.Root.Replace('/',Path.DirectorySeparatorChar));
                    ApfPaths.EnsureNoReparsePoints(root,packageRoot);
                    var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var file in prepared.Files) {
                        string relative=ApfPaths.NormalizeGameDataRelative(file.Path);
                        if(!ApfPaths.IsSafeRelative(relative)||!expected.Add(relative))throw new InvalidDataException("Unsafe or duplicate prepared file.");
                        string target=Path.GetFullPath(Path.Combine(data,relative.Replace('/',Path.DirectorySeparatorChar)));
                        if(!string.Equals(target,packageRoot,StringComparison.OrdinalIgnoreCase)&&!ApfPaths.IsUnder(target,packageRoot))throw new InvalidDataException("Prepared file escapes its package.");
                        ApfPaths.EnsureNoReparsePoints(root,target);
                        var input=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read);
                        locks.Add(input);
                        fileLocks.Add(relative,input);
                        using(var hash=SHA256.Create())if(!string.Equals(ApfJson.ToHex(hash.ComputeHash(input)),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Prepared file changed: "+file.Path);
                    }
                    string[] actual=File.Exists(packageRoot)?new[]{packageRoot}:Directory.GetFiles(packageRoot,"*",SearchOption.AllDirectories);
                    foreach(string file in actual){ApfPaths.EnsureNoReparsePoints(root,file);if(!expected.Contains(file.Substring(data.Length+1).Replace('\\','/')))throw new InvalidDataException("Unexpected prepared file.");}
                    if(actual.Length!=expected.Count)throw new InvalidDataException("Prepared file count differs.");
                }
                active=receipt;transaction=value;preparedData=data;
            }catch{Close();throw;}
        }
        public static bool ArchiveReady(string archive,ApfPackage package,string gameData)
        {
            return active!=null&&transaction.Packages.Any(p=>p.Package.Id==package.Id&&p.Package.Sha256==package.Sha256&&string.Equals(Path.GetFullPath(p.ArchivePath),Path.GetFullPath(archive),StringComparison.OrdinalIgnoreCase))&&string.Equals(Path.GetFullPath(gameData),Path.GetFullPath(transaction.GameDataPath),StringComparison.OrdinalIgnoreCase);
        }
        public static void CommitClient()
        {
            if(bundledClient==null)return;
            // The enclosing pack runner has consumed its authorization, holds
            // the installer lock, and has already waited for the original KSP.
            var client=bundledClient;bundledClient=null;
            client.ProcessId=0;
            ApfArchiveInstaller.ApplyClientUpdate(client);
        }
        public static void DisableDuplicate()
        {
            if(duplicateClient==null)return;
            string root=Path.GetFullPath(transaction.GameRoot);
            ValidateTree(root,duplicateClient);
            if(!string.Equals(ApfJson.Sha256File(Path.Combine(duplicateClient,"Plugins","LmpClient.dll")),active.DuplicateClientSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Duplicate multiplayer client changed during preparation.");
            string destination=Path.Combine(root,"APF",".disabled-install-copies",Guid.NewGuid().ToString("N"),"LunaMultiplayer");
            ApfPaths.EnsureNoReparsePoints(root,destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(duplicateClient,destination);
            duplicateClient=null;
        }
        private static void ValidateTree(string root,string directory)
        {
            ApfPaths.EnsureNoReparsePoints(root,directory);
            foreach(string entry in Directory.GetFileSystemEntries(directory)) {
                ApfPaths.EnsureNoReparsePoints(root,entry);
                if(Directory.Exists(entry))ValidateTree(root,entry);
            }
        }
        public static List<string> Extract(string archive,ApfPackage package,string gameData)
        {
            if(!ArchiveReady(archive,package,gameData))return null;
            string relative=ApfPaths.PackageRoot(package);
            string source=Path.Combine(preparedData,relative.Replace('/',Path.DirectorySeparatorChar));
            string target=Path.Combine(gameData,relative.Replace('/',Path.DirectorySeparatorChar));
            ApfPaths.EnsureNoReparsePoints(transaction.GameRoot,source);ApfPaths.EnsureNoReparsePoints(transaction.GameRoot,target);
            if(Directory.Exists(target)||File.Exists(target))throw new IOException("Prepared package target was not vacated.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var prepared=active.Packages.Single(p=>p.Id==package.Id);
            // Windows refuses parent-directory rename while children are open.
            // Reopen and hash after the rename, then retain locks until commit.
            foreach(var file in prepared.Files){string key=ApfPaths.NormalizeGameDataRelative(file.Path);fileLocks[key].Dispose();fileLocks.Remove(key);}
            if(Directory.Exists(source))Directory.Move(source,target);else File.Move(source,target);
            foreach(var file in prepared.Files) {
                string relativeFile=ApfPaths.NormalizeGameDataRelative(file.Path);
                string destination=Path.Combine(gameData,relativeFile.Replace('/',Path.DirectorySeparatorChar));
                ApfPaths.EnsureNoReparsePoints(transaction.GameRoot,destination);
                var input=new FileStream(destination,FileMode.Open,FileAccess.Read,FileShare.Read);locks.Add(input);fileLocks.Add(relativeFile,input);
                using(var hash=SHA256.Create())if(!string.Equals(ApfJson.ToHex(hash.ComputeHash(input)),file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Committed prepared bytes differ: "+file.Path);
            }
            return prepared.Files.Select(f=>Path.Combine(gameData,ApfPaths.NormalizeGameDataRelative(f.Path).Replace('/',Path.DirectorySeparatorChar))).ToList();
        }
        public static List<ApfFileState> Files(string gameData,string packagePath)
        {
            if(active==null||!string.Equals(Path.GetFullPath(gameData),Path.GetFullPath(transaction.GameDataPath),StringComparison.OrdinalIgnoreCase))return null;
            var package=active.Packages.SingleOrDefault(p=>string.Equals(Path.GetFullPath(packagePath),Path.GetFullPath(Path.Combine(gameData,p.Root.Replace('/',Path.DirectorySeparatorChar))),StringComparison.OrdinalIgnoreCase));
            if(package==null)return null;
            // Reject removals/additions even while immutable file handles prevent writes.
            string[] actual=File.Exists(packagePath)?new[]{packagePath}:Directory.GetFiles(packagePath,"*",SearchOption.AllDirectories);
            var paths=new HashSet<string>(package.Files.Select(f=>ApfPaths.NormalizeGameDataRelative(f.Path)),StringComparer.OrdinalIgnoreCase);
            if(actual.Length!=paths.Count||actual.Any(f=>!paths.Contains(f.Substring(gameData.Length+1).Replace('\\','/'))))throw new InvalidDataException("Prepared installed package paths changed.");
            foreach(string file in actual) {
                ApfPaths.EnsureNoReparsePoints(transaction.GameRoot,file);
                // The installed path must still name the exact locked file.
                using(var current=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
                    if(!GetFileInformationByHandle(fileLocks[file.Substring(gameData.Length+1).Replace('\\','/')].SafeFileHandle.DangerousGetHandle(),out var held)||!GetFileInformationByHandle(current.SafeFileHandle.DangerousGetHandle(),out var now)||held.Volume!=now.Volume||held.IndexHigh!=now.IndexHigh||held.IndexLow!=now.IndexLow)throw new InvalidDataException("Prepared installed file identity changed.");
                }
            }
            return package.Files;
        }
        public static void Close(){active=null;transaction=null;preparedData=null;bundledClient=null;duplicateClient=null;foreach(var input in locks)input.Dispose();locks.Clear();fileLocks.Clear();}
        [StructLayout(LayoutKind.Sequential)]private struct FileIdentity { public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(IntPtr handle,out FileIdentity information);
        private static string Mac(string payload,byte[] key){using(var hash=new HMACSHA256(key))return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(payload)));}
        private static bool SameMac(string left,string right){if(left==null||left.Length!=right.Length)return false;int difference=0;for(int i=0;i<left.Length;i++)difference|=left[i]^right[i];return difference==0;}
    }
}
