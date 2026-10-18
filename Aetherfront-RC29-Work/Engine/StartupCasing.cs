using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ApfCore;

namespace KesasInstall
{
    public static partial class PackageCasing
    {
        private sealed class RenameRecord
        {
            public RenameRecord(){}
            public string Original {get;set;}
            public string Canonical {get;set;}
            public string Temporary {get;set;}
            public string Status {get;set;}
            public bool Directory {get;set;}
        }
        private sealed class RenameEnvelope
        {
            public RenameEnvelope(){}
            public string Payload {get;set;}
            public string Mac {get;set;}
        }

        // Called synchronously by the canonical Instantly addon. KSP rebuilds
        // its asset UrlDir tree after Instantly addons, before models/textures.
        // This also repairs updates applied by an already-loaded older helper.
        public static void RestoreAtStartup(string gameRoot,string expectedClientVersion)
        {
            string root=Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string gameData=Path.Combine(root,"GameData");
            string data=Path.Combine(gameData,"LunaMultiplayer","Data");
            string receiptPath=Path.Combine(root,"APF","LastInstallResult.json");
            ApfPaths.EnsureNoReparsePoints(root,receiptPath);
            if(!File.Exists(receiptPath))return; // A fresh starter has no installed pack yet.
            var receipt=ApfJson.Deserialize<Dictionary<string,string>>(ReadBounded(root,receiptPath,65536));
            if(!Value(receipt,"status","complete") || !Value(receipt,"clientVersion",expectedClientVersion))return;
            receipt=Installer.ReadCompletedReceipt(root);
            if(receipt==null)throw new InvalidDataException("Startup package receipt authorization failed.");
            string feedPath=Path.Combine(root,"APF",".client-update-staging","connect","feed.json");
            if(!File.Exists(feedPath))return;
            var feed=ApfClientUpdateManifest.FromJson(ReadBounded(root,feedPath,4*1024*1024));
            if(feed.Version!=expectedClientVersion)return;
            string publicKey=Path.Combine(data,"APFClientUpdatePublicKey.xml");
            using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(ReadBounded(root,publicKey,65536)))
                if(!ApfClientUpdateSecurity.Verify(feed,key))throw new InvalidDataException("Startup package catalog signature is invalid.");
            const string catalogRelative="GameData/LunaMultiplayer/Launcher/install-catalog.json";
            foreach(string relative in new[]{catalogRelative,"GameData/LunaMultiplayer/Plugins/LmpClient.dll","GameData/LunaMultiplayer/Plugins/ApfCore.dll","GameData/LunaMultiplayer/Plugins/KesasInstall.dll"})
                VerifyDeclaredFile(root,relative,feed);
            var catalog=ApfJson.Deserialize<InstallCatalog>(ReadBounded(root,Path.Combine(root,catalogRelative.Replace('/',Path.DirectorySeparatorChar)),4*1024*1024));
            var profile=ApfJson.Deserialize<Dictionary<string,string>>(ReadBounded(root,Path.Combine(data,"KesasInstallProfile.json"),65536));
            if(catalog==null || catalog.Version!=feed.Version || !Value(receipt,"catalogVersion",feed.Version) ||
                !Value(receipt,"packVersion",feed.Version) || !Value(profile,"catalogVersion",feed.Version))
                throw new InvalidDataException("Startup package receipt does not match the signed catalog.");
            string mode;
            if(profile==null || !profile.TryGetValue("mode",out mode) || (mode!="full" && mode!="potato") || !Value(receipt,"mode",mode))
                throw new InvalidDataException("Startup profile receipt is invalid.");
            var manifest=mode=="potato"?catalog.Potato:catalog.Full;
            if(manifest==null || manifest.PackVersion!=feed.Version || manifest.RequiredClientVersion!=feed.Version || manifest.ManagedPackages==null)
                throw new InvalidDataException("Startup profile does not match the signed release.");
            LegacySettingsRecovery.Apply(root,manifest,receipt);
            string apf=Path.Combine(root,"APF");
            ApfPaths.EnsureNoReparsePoints(root,apf);
            string journal=Path.Combine(apf,"RootCasingRepair.json");
            ApfPaths.EnsureNoReparsePoints(root,journal);
            string lockPath=Path.Combine(apf,".case-repair.lock");
            ApfPaths.EnsureNoReparsePoints(root,lockPath);
            using(var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                byte[] secret=null;
                if(File.Exists(journal)) {
                    secret=ApfTransactionSecurity.LoadKey(Path.Combine(data,"APFHelper.key"));
                    Recover(root,gameData,journal,secret);
                }
                var roots=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var package in manifest.ManagedPackages) {
                    string canonical=CanonicalRoot(package);
                    string identity=ApfPaths.Normalize(canonical);
                    if(ApfPaths.IsBuiltInProtectedTopLevel(identity.Split('/')[0]))continue;
                    string previous;
                    if(roots.TryGetValue(identity,out previous) && previous!=canonical)
                        throw new InvalidDataException("Signed packages disagree about folder casing.");
                    roots[identity]=canonical;
                }
                foreach(string canonical in roots.Values) {
                    string parent=gameData;
                    foreach(string segment in canonical.Split('/')) {
                        string actual=FindSibling(root,parent,segment);
                        if(actual==null)break; // Partial/missing packages remain the installer's responsibility.
                        string target=Path.Combine(parent,segment);
                        if(!string.Equals(Path.GetFileName(actual),segment,StringComparison.Ordinal)) {
                            if(secret==null)secret=ApfTransactionSecurity.LoadKey(Path.Combine(data,"APFHelper.key"));
                            Repair(root,gameData,actual,target,journal,secret);
                        }
                        parent=target;
                    }
                }
                // The packaged addon writes its generated tree here but does
                // not create this empty runtime directory itself.
                if(roots.ContainsKey("hideemptytechtreenodes")) {
                    string addon=Path.Combine(gameData,roots["hideemptytechtreenodes"].Replace('/',Path.DirectorySeparatorChar));
                    if(Directory.Exists(addon)) {
                        string resources=Path.Combine(addon,"Resources");
                        ApfPaths.EnsureNoReparsePoints(root,resources);
                        if(File.Exists(resources))throw new InvalidDataException("Tech-tree runtime directory is a file.");
                        Directory.CreateDirectory(resources);
                    }
                }
            }
        }

        private static bool Value(Dictionary<string,string> values,string name,string expected)
        {string actual;return values!=null && values.TryGetValue(name,out actual) && actual==expected;}
        private static string ReadBounded(string root,string path,int maximum)
        {
            ApfPaths.EnsureNoReparsePoints(root,path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length>maximum)throw new InvalidDataException("Startup metadata is too large.");
                using(var reader=new StreamReader(stream))return reader.ReadToEnd();
            }
        }
        private static void VerifyDeclaredFile(string root,string relative,ApfClientUpdateManifest manifest)
        {
            var record=manifest.Files.SingleOrDefault(file=>file.Path==relative);
            string path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
            ApfPaths.EnsureNoReparsePoints(root,path);
            if(record==null || !File.Exists(path) || new FileInfo(path).Length!=record.SizeBytes ||
                !string.Equals(ApfJson.Sha256File(path),record.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Startup catalog/code does not match the signed release: "+relative);
        }
        private static string FindSibling(string root,string parent,string expected)
        {
            ApfPaths.EnsureNoReparsePoints(root,parent);
            if(!Directory.Exists(parent))return null;
            var matches=Directory.GetFileSystemEntries(parent).Where(path=>string.Equals(Path.GetFileName(path),expected,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length>1)throw new InvalidDataException("Ambiguous package path casing.");
            if(matches.Length==0)return null;
            ApfPaths.EnsureNoReparsePoints(root,matches[0]);
            return matches[0];
        }
        private static void ValidateTree(string root,string entry)
        {
            var pending=new Stack<string>();pending.Push(entry);
            while(pending.Count>0) {
                string current=pending.Pop();ApfPaths.EnsureNoReparsePoints(root,current);
                if(Directory.Exists(current))foreach(string child in Directory.GetFileSystemEntries(current))pending.Push(child);
            }
        }
        private static string Relative(string gameData,string path)
        {
            string prefix=Path.GetFullPath(gameData).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path);
            if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Casing repair escapes GameData.");
            return full.Substring(prefix.Length).Replace('\\','/');
        }
        private static void Repair(string root,string gameData,string original,string target,string journal,byte[] secret)
        {
            ValidateTree(root,original);
            string temp=Path.Combine(Path.GetDirectoryName(original),".aetherfront-case-"+Guid.NewGuid().ToString("N"));
            ApfPaths.EnsureNoReparsePoints(root,target);ApfPaths.EnsureNoReparsePoints(root,temp);
            var record=new RenameRecord{Original=Relative(gameData,original),Canonical=Relative(gameData,target),Temporary=Relative(gameData,temp),Status="applying",Directory=Directory.Exists(original)};
            ValidateRecord(record);
            SaveJournal(root,journal,record,secret);
            try {
                Move(root,original,temp,record.Directory);
                Move(root,temp,target,record.Directory);
                record.Status="complete";SaveJournal(root,journal,record,secret);
                // A log reader must not turn an already completed repair into
                // a failed client startup. The authenticated journal is durable.
                try {
                    string log=Path.Combine(root,"APF","APFUpdater.log");ApfPaths.EnsureNoReparsePoints(root,log);
                    File.AppendAllText(log,DateTime.UtcNow.ToString("o")+" Restored package folder casing from "+record.Original+" to "+record.Canonical+Environment.NewLine);
                }catch(IOException){}catch(UnauthorizedAccessException){}
            }catch {
                // Never leave an avoidable temporary root after a failed second
                // rename. If restoration is blocked, the signed journal stays.
                if((Directory.Exists(temp)||File.Exists(temp)) && !Directory.Exists(original) && !File.Exists(original))
                    try{Move(root,temp,original,record.Directory);}catch{}
                throw;
            }
        }
        private static void Move(string root,string source,string target,bool directory)
        {
            // Windows reports a parent-directory rename blocked by a scanner
            // or recently closed file handle as access denied or sharing busy.
            // Retry briefly, never alter permissions or overwrite a collision.
            for(int attempt=0;;attempt++) {
                ApfPaths.EnsureNoReparsePoints(root,source);ApfPaths.EnsureNoReparsePoints(root,target);
                if(File.Exists(target)||Directory.Exists(target))throw new InvalidDataException("Casing repair target already exists.");
                try {
                    if(directory)Directory.Move(source,target);else File.Move(source,target);
                    return;
                }catch(Exception error) when((error is IOException || error is UnauthorizedAccessException) && attempt<19 &&
                    ((error.HResult&65535)==5 || (error.HResult&65535)==32 || (error.HResult&65535)==33)) {
                    Thread.Sleep(100);
                }
            }
        }
        private static void ValidateRecord(RenameRecord record)
        {
            if(record==null || (record.Status!="applying" && record.Status!="complete") ||
                !ApfPaths.IsSafeRelative(record.Original) || !ApfPaths.IsSafeRelative(record.Canonical) || !ApfPaths.IsSafeRelative(record.Temporary) ||
                !string.Equals(ApfPaths.Normalize(record.Original),ApfPaths.Normalize(record.Canonical),StringComparison.Ordinal) ||
                ApfPaths.IsBuiltInProtectedTopLevel(ApfPaths.Normalize(record.Canonical).Split('/')[0]) ||
                !string.Equals(Path.GetDirectoryName(record.Original.Replace('/',Path.DirectorySeparatorChar)),Path.GetDirectoryName(record.Temporary.Replace('/',Path.DirectorySeparatorChar)),StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(Path.GetFileName(record.Temporary),"^\\.aetherfront-case-[0-9a-f]{32}$"))
                throw new InvalidDataException("Invalid casing recovery journal.");
        }
        private static string Mac(string payload,byte[] secret)
        {using(var hmac=new HMACSHA256(secret))return ApfJson.ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));}
        private static void SaveJournal(string root,string path,RenameRecord record,byte[] secret)
        {
            string payload=ApfJson.Serialize(record);
            string text=ApfJson.Serialize(new RenameEnvelope{Payload=payload,Mac=Mac(payload,secret)});
            string temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
            ApfPaths.EnsureNoReparsePoints(root,path);ApfPaths.EnsureNoReparsePoints(root,temporary);
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                byte[] bytes=new UTF8Encoding(false).GetBytes(text);stream.Write(bytes,0,bytes.Length);stream.Flush(true);
            }
            if(File.Exists(path))AtomicFiles.Replace(temporary,path,null);else File.Move(temporary,path);
        }
        private static void Recover(string root,string gameData,string path,byte[] secret)
        {
            var envelope=ApfJson.Deserialize<RenameEnvelope>(ReadBounded(root,path,65536));
            if(envelope==null || envelope.Payload==null || envelope.Mac==null || !Regex.IsMatch(envelope.Mac,"^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Casing recovery journal authentication failed.");
            string expected=Mac(envelope.Payload,secret);int different=0;
            for(int i=0;i<expected.Length;i++)different|=char.ToLowerInvariant(expected[i])^char.ToLowerInvariant(envelope.Mac[i]);
            if(different!=0)throw new InvalidDataException("Casing recovery journal authentication failed.");
            var record=ApfJson.Deserialize<RenameRecord>(envelope.Payload);ValidateRecord(record);
            if(record.Status=="complete")return;
            string temporary=Path.Combine(gameData,record.Temporary.Replace('/',Path.DirectorySeparatorChar));
            string target=Path.Combine(gameData,record.Canonical.Replace('/',Path.DirectorySeparatorChar));
            if(Directory.Exists(temporary)||File.Exists(temporary)) {
                ValidateTree(root,temporary);Move(root,temporary,target,record.Directory);
            }else {
                string actual=FindSibling(root,Path.GetDirectoryName(target),Path.GetFileName(target));
                if(actual==null)throw new InvalidDataException("Interrupted casing repair has no recoverable source.");
                if(Path.GetFileName(actual)!=Path.GetFileName(target)) {
                    ValidateTree(root,actual);
                    Move(root,actual,temporary,record.Directory);Move(root,temporary,target,record.Directory);
                }
            }
            record.Status="complete";SaveJournal(root,path,record,secret);
        }
    }
}
