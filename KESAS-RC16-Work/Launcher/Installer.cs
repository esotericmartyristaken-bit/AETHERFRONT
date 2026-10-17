using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApfCore;

namespace KesasLauncher
{
    public sealed class InstallCatalog
    {
        public string Version { get; set; }
        public ApfPackManifest Full { get; set; }
        public ApfPackManifest Potato { get; set; }
        public List<PackageSize> Sizes { get; set; } = new List<PackageSize>();
    }
    public sealed class PackageSize
    {
        public string Id { get; set; }
        public long ExpandedBytes { get; set; }
    }
    public sealed class InstallResult
    {
        public bool RestartLauncher { get; set; }
        public bool Changed { get; set; }
    }
    public sealed class Installer
    {
        public readonly string Root;
        public readonly string Data;
        public Action<string> Progress = delegate { };
        public Func<string, bool> Confirm = delegate { return false; };
        public Func<bool> IsCanceled = delegate { return false; };
        private readonly HashSet<string> pins;
        private readonly Uri updateUrl;
        private readonly bool launchGame;
        private readonly HashSet<string> checkedDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Installer(string root, bool launch = true)
        {
            // Windows PowerShell can inherit legacy TLS defaults. This process
            // is external to KSP; require TLS 1.2 without changing OS settings.
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            Data = Path.Combine(Root, "GameData", "LunaMultiplayer", "Data");
            launchGame = launch;
            ApfPaths.EnsureNoReparsePoints(Root, Root);
            ApfPaths.EnsureNoReparsePoints(Root, Path.Combine(Root, "GameData"));
            if (!File.Exists(Path.Combine(Root,"KSP_x64.exe")) || !Directory.Exists(Path.Combine(Root,"GameData","Squad")))
                throw new InvalidDataException("Extract the starter ZIP beside KSP_x64.exe in a clean Windows KSP 1.12.5 copy.");
            if(launch) foreach(string required in new[]{"UnityPlayer.dll","KSP_x64_Data/globalgamemanagers","KSP_x64_Data/Managed/Assembly-CSharp.dll","KSP_x64_Data/Managed/UnityEngine.CoreModule.dll"})
                if(!File.Exists(Path.Combine(Root,required))) throw new InvalidDataException("Incomplete KSP copy: missing "+required+". Repair the game before installing mods.");
            pins=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string line in File.ReadAllLines(Path.Combine(Data,"APFTlsCertificatePins.txt"))) { string pin=line.Trim();if(pin.Length==0)continue;if(!Regex.IsMatch(pin,"^[0-9a-fA-F]{64}$"))throw new InvalidDataException("Invalid server certificate pin.");pins.Add(pin); }
            if(pins.Count==0 || pins.Count>8) throw new InvalidDataException("Invalid server certificate pin list.");
            updateUrl = new Uri(File.ReadAllText(Path.Combine(Data,"APFClientUpdateManifestUrl.txt")).Trim());
            if (updateUrl.Scheme != "https" || updateUrl.UserInfo.Length != 0 || updateUrl.Fragment.Length != 0) throw new InvalidDataException("Invalid update URL.");
        }
        public InstallResult Run(string mode, bool checkUpdates = true)
        {
            if (mode != "full" && mode != "potato") throw new ArgumentException("Unknown install profile");
            EnsureGameClosed();
            checkedDirectories.Clear();
            ApfPaths.EnsureNoReparsePoints(Root,Root);
            checkedDirectories.Add(Root);
            if (checkUpdates && UpdateClient()) {
                string selectedFile=Path.Combine(Data,"KesasInstallProfile.json");
                var selected=File.Exists(selectedFile)?ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(selectedFile)):new Dictionary<string,string>();
                selected["mode"]=mode;
                if(!selected.ContainsKey("catalogVersion"))selected["catalogVersion"]="";
                AtomicText(selectedFile,ApfJson.Serialize(selected));
                return new InstallResult { RestartLauncher=true, Changed=true };
            }
            var catalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(Root,"GameData","LunaMultiplayer","Launcher","install-catalog.json")));
            var manifest = mode == "potato" ? catalog.Potato : catalog.Full;
            var validation = ApfManifestValidator.Validate(manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ",validation.Errors));
            var sizes = catalog.Sizes.ToDictionary(s=>s.Id,s=>s.ExpandedBytes,StringComparer.OrdinalIgnoreCase);
            string statePath = Path.Combine(Data,"APFPackState.json");
            var state = File.Exists(statePath) ? ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath)) : new ApfPackState();
            var old = (state.Packages ?? new List<ApfPackageState>()).ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
            var needed = new List<ApfPackage>();
            foreach(var package in manifest.ManagedPackages)
            {
                CheckCanceled();
                if (!old.TryGetValue(package.Id,out var installed) || !PackagePresent(package,installed)) needed.Add(package);
            }
            var desired = new HashSet<string>(manifest.ManagedPackages.Select(p=>ApfPaths.PackageRoot(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>((manifest.ProtectedRoots ?? new List<string>()).Select(p=>ApfPaths.NormalizeGameDataRelative(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
            foreach(var rule in manifest.ClientOnly) foreach(var root in rule.PathRoots) allowed.Add(ApfPaths.NormalizeGameDataRelative(root).Split('/')[0]);
            var deletes = new List<ApfDeleteChange>();
            foreach(string entry in Directory.GetFileSystemEntries(Path.Combine(Root,"GameData")))
            {
                string name=Path.GetFileName(entry);
                if (!desired.Contains(name) && !allowed.Contains(name) && !ApfPaths.IsBuiltInProtectedTopLevel(name.ToLowerInvariant()))
                    deletes.Add(new ApfDeleteChange { RelativePath="GameData/"+name, Reason="Not in the selected install profile" });
            }
            if(deletes.Count>0 && !Confirm("These folders/files will move into a recovery backup:\n\n"+string.Join("\n",deletes.Select(d=>d.RelativePath))+"\n\nContinue?"))
                throw new OperationCanceledException("Profile change canceled. No installed files changed.");
            long required=512L*1024*1024;
            foreach(var package in needed) required=checked(required+package.SizeBytes+sizes[package.Id]);
            if(new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace<required) throw new IOException("Not enough free disk space. Need "+Format(required)+" for downloads and installation.");
            var transaction = new ApfSyncTransaction { GameRoot=Root, GameDataPath=Path.Combine(Root,"GameData"), KspExecutable=Path.Combine(Root,"KSP_x64.exe"), ProcessId=0, StatePath=statePath, Manifest=manifest, ExistingState=state, Deletes=deletes };
            string stage=Path.Combine(Root,"APF",".staging","launcher");
            Directory.CreateDirectory(stage);
            for(int i=0;i<needed.Count;i++)
            {
                var package=needed[i];
                Progress("Package "+(i+1)+" / "+needed.Count+": "+ApfPaths.PackageRoot(package));
                var uri=new Uri(package.ArchiveUrl);
                if(!SameOrigin(uri,updateUrl)) throw new InvalidDataException("Package is not hosted by this server.");
                string archive=Path.Combine(stage,package.Sha256+".zip");
                Download(uri,archive,package.SizeBytes,package.Sha256);
                transaction.Packages.Add(new ApfStagedPackage { Package=package, ArchivePath=archive });
            }
            CheckCanceled(); EnsureGameClosed();
            if(needed.Count>0 || deletes.Count>0)
            {
                Progress("Installing. Keep the launcher open until this finishes.");
                var key=ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(Data,"APFHelper.key"));
                ApfTransactionSecurity.Sign(transaction,key);
                string request=Path.Combine(stage,Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(request));
                File.WriteAllText(request,ApfJson.Serialize(transaction));
                KesasInstallPolicy.SuppressRelaunch=true;
                try { ApfPostExitRunner.Apply(Path.Combine(Root,"GameData","LunaMultiplayer"),request,false); }
                finally { KesasInstallPolicy.SuppressRelaunch=false; }
            }
            SetGraphics(mode);
            AtomicText(Path.Combine(Data,"KesasInstallProfile.json"),ApfJson.Serialize(new Dictionary<string,string>{{"mode",mode},{"catalogVersion",catalog.Version}}));
            Progress(mode=="potato" ? "Potato installation ready." : "Full installation ready.");
            if(launchGame) Process.Start(new ProcessStartInfo(Path.Combine(Root,"KSP_x64.exe")){WorkingDirectory=Root,UseShellExecute=true});
            return new InstallResult { Changed=needed.Count>0 || deletes.Count>0 };
        }
        private bool UpdateClient()
        {
            Progress("Checking client updates...");
            string stage=Path.Combine(Root,"APF",".client-update-staging","launcher"); Directory.CreateDirectory(stage);
            string manifestFile=Path.Combine(stage,"feed.json");
            Download(updateUrl,manifestFile,4L*1024*1024,null);
            var manifest=ApfClientUpdateManifest.FromJson(File.ReadAllText(manifestFile));
            var validation=ApfClientUpdateValidator.Validate(manifest);
            if(!validation.IsValid) throw new InvalidDataException(string.Join("; ",validation.Errors));
            using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(Path.Combine(Data,"APFClientUpdatePublicKey.xml"))))
                if(!ApfClientUpdateSecurity.Verify(manifest,key)) throw new InvalidDataException("Client update signature is invalid.");
            var settings=ApfJson.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Data,"APFClient.json")));
            if(ApfClientUpdateVersion.Compare(manifest.Version,Convert.ToString(settings["clientVersion"]))<=0) return false;
            var uri=new Uri(manifest.PackageUrl);
            if(!SameOrigin(uri,updateUrl)) throw new InvalidDataException("Client update is not hosted by this server.");
            string archive=Path.Combine(stage,manifest.Sha256+".zip"); Download(uri,archive,manifest.SizeBytes,manifest.Sha256);
            var transaction=new ApfClientUpdateTransaction { GameRoot=Root,GameDataPath=Path.Combine(Root,"GameData"),KspExecutable=Path.Combine(Root,"KSP_x64.exe"),ProcessId=0,ArchivePath=archive,Manifest=manifest,Approval=ApfClientUpdateApproval.Capture(Root) };
            ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(Data,"APFHelper.key")));
            string request=Path.Combine(stage,Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(request));File.WriteAllText(request,ApfJson.Serialize(transaction));
            Progress("Installing client update...");
            KesasInstallPolicy.SuppressRelaunch=true;
            try { ApfPostExitRunner.Apply(Path.Combine(Root,"GameData","LunaMultiplayer"),request,true); }
            finally { KesasInstallPolicy.SuppressRelaunch=false; }
            return true;
        }
        private bool PackagePresent(ApfPackage package,ApfPackageState state)
        {
            if(state.Version!=package.Version || !string.Equals(state.ArchiveSha256,package.Sha256,StringComparison.OrdinalIgnoreCase) || state.Files==null || state.Files.Count==0) return false;
            foreach(var file in state.Files)
            {
                string path=Path.GetFullPath(Path.Combine(Root,file.Path.Replace('/',Path.DirectorySeparatorChar)));
                if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installed file is outside this game folder.");
                if(!PlainDirectory(Path.GetDirectoryName(path)))return false;
                FileAttributes attributes;
                try{attributes=File.GetAttributes(path);}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Installed files must not use junctions or symlinks.");
                if((attributes&FileAttributes.Directory)!=0)return false;
                string extension=Path.GetExtension(path).ToLowerInvariant();
                if((extension==".dll" || extension==".cfg" || extension==".xml" || extension==".json") && !string.Equals(ApfJson.Sha256File(path),file.Sha256,StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        private bool PlainDirectory(string path)
        {
            if(checkedDirectories.Contains(path))return true;
            if(string.Equals(path,Root,StringComparison.OrdinalIgnoreCase)) {
                ApfPaths.EnsureNoReparsePoints(Root,Root);checkedDirectories.Add(Root);return true;
            }
            if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installed directory is outside this game folder.");
            if(!PlainDirectory(Path.GetDirectoryName(path)))return false;
            FileAttributes attributes;
            try{attributes=File.GetAttributes(path);}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Installed directories must not use junctions or symlinks.");
            if((attributes&FileAttributes.Directory)==0)return false;
            checkedDirectories.Add(path);return true;
        }
        private void EnsureGameClosed()
        {
            foreach(var process in Process.GetProcessesByName("KSP_x64"))
            {
                using(process) {
                    try { if(string.Equals(process.MainModule.FileName,Path.Combine(Root,"KSP_x64.exe"),StringComparison.OrdinalIgnoreCase)) throw new IOException("Close this KSP copy before installing or switching profiles."); }
                    catch(System.ComponentModel.Win32Exception) { throw new IOException("Cannot inspect a running KSP process. Close KSP before installing."); }
                }
            }
        }
        private void Download(Uri uri,string destination,long maximum,string expectedHash)
        {
            if(uri.Scheme!="https" || !SameOrigin(uri,updateUrl)) throw new InvalidDataException("Only this server's HTTPS downloads are allowed.");
            ApfPaths.EnsureNoReparsePoints(Root,destination);
            if(expectedHash!=null && File.Exists(destination) && new FileInfo(destination).Length==maximum && string.Equals(ApfJson.Sha256File(destination),expectedHash,StringComparison.OrdinalIgnoreCase)) return;
            string part=destination+".part";
            long offset=expectedHash!=null && File.Exists(part) ? new FileInfo(part).Length : 0;
            if(offset>=maximum) offset=0;
            var request=(HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect=false;request.Timeout=30000;request.ReadWriteTimeout=30000;
            request.CachePolicy=new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>certificate!=null && pins.Contains(Hash(certificate.GetRawCertData()));
            if(offset>0) request.AddRange(offset);
            using(var response=(HttpWebResponse)request.GetResponse())
            {
                if(response.StatusCode==HttpStatusCode.PartialContent)
                {
                    string prefix="bytes "+offset+"-";
                    if(offset==0 || !(response.Headers["Content-Range"]??"").StartsWith(prefix,StringComparison.Ordinal)) throw new InvalidDataException("Invalid download range response.");
                }
                else if(response.StatusCode==HttpStatusCode.OK) offset=0;
                else throw new IOException("Download failed: "+response.StatusCode);
                if(response.ContentLength>maximum-offset) throw new InvalidDataException("Download is larger than declared.");
                using(var input=response.GetResponseStream())
                using(var output=new FileStream(part,offset>0 ? FileMode.Append : FileMode.Create,FileAccess.Write,FileShare.None))
                {
                    var buffer=new byte[65536]; long total=offset;var timer=Stopwatch.StartNew();int read;
                    while((read=input.Read(buffer,0,buffer.Length))>0)
                    {
                        CheckCanceled(); total=checked(total+read);if(total>maximum)throw new InvalidDataException("Download exceeded its size limit.");output.Write(buffer,0,read);
                        if(timer.ElapsedMilliseconds>=300){Progress("Downloading "+Format(total)+" / "+Format(maximum));timer.Restart();}
                    }
                    if(expectedHash!=null && total!=maximum) throw new IOException("Download ended early; retry to resume.");
                }
            }
            if(expectedHash!=null && !string.Equals(ApfJson.Sha256File(part),expectedHash,StringComparison.OrdinalIgnoreCase)) { File.Delete(part);throw new InvalidDataException("Downloaded file hash differs from the signed catalog."); }
            if(File.Exists(destination)) File.Replace(part,destination,null); else File.Move(part,destination);
        }
        private void SetGraphics(string mode)
        {
            string settings=Path.Combine(Root,"settings.cfg"); string backup=Path.Combine(Data,"KESASGraphicsBeforePotato.cfg");
            ApfPaths.EnsureNoReparsePoints(Root,settings);ApfPaths.EnsureNoReparsePoints(Root,backup);
            string current=File.Exists(settings)?File.ReadAllText(settings):"";
            if(mode=="full")
            {
                if(File.Exists(backup)) {
                    var values=GraphicsValues(File.ReadAllText(backup));
                    if(values.Count==0) values=GraphicsValues("QUALITY_PRESET = 3\nANTI_ALIASING = 2\nTEXTURE_QUALITY = 0\nSYNC_VBL = 1\nLIGHT_QUALITY = 8\nSHADOWS_QUALITY = 3\nFRAMERATE_LIMIT = 60\nCELESTIAL_BODIES_CAST_SHADOWS = True\nPLANET_SCATTER = True\nPLANET_SCATTER_FACTOR = 1\nAERO_FX_QUALITY = 2\nSURFACE_FX = True\nREFLECTION_PROBE_REFRESH_MODE = 1\nREFLECTION_PROBE_TEXTURE_RESOLUTION = 1\nTERRAIN_SHADER_QUALITY = 2");
                    AtomicText(settings,MergeGraphics(current,values));
                    File.Move(backup,backup+".restored-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                }
                return;
            }
            if(!File.Exists(backup)) AtomicText(backup,current);
            var low=new Dictionary<string,string> { ["QUALITY_PRESET"]="0",["ANTI_ALIASING"]="0",["TEXTURE_QUALITY"]="2",["SYNC_VBL"]="0",["LIGHT_QUALITY"]="0",["SHADOWS_QUALITY"]="0",["FRAMERATE_LIMIT"]="60",["CELESTIAL_BODIES_CAST_SHADOWS"]="False",["PLANET_SCATTER"]="False",["PLANET_SCATTER_FACTOR"]="0",["AERO_FX_QUALITY"]="0",["SURFACE_FX"]="False",["REFLECTION_PROBE_REFRESH_MODE"]="0",["REFLECTION_PROBE_TEXTURE_RESOLUTION"]="0",["TERRAIN_SHADER_QUALITY"]="0" };
            AtomicText(settings,MergeGraphics(current,low));
        }
        public static Dictionary<string,string> GraphicsValues(string text)
        {
            var result=new Dictionary<string,string>();
            var names=new HashSet<string>(new[]{"QUALITY_PRESET","ANTI_ALIASING","TEXTURE_QUALITY","SYNC_VBL","LIGHT_QUALITY","SHADOWS_QUALITY","FRAMERATE_LIMIT","CELESTIAL_BODIES_CAST_SHADOWS","PLANET_SCATTER","PLANET_SCATTER_FACTOR","AERO_FX_QUALITY","SURFACE_FX","REFLECTION_PROBE_REFRESH_MODE","REFLECTION_PROBE_TEXTURE_RESOLUTION","TERRAIN_SHADER_QUALITY"});
            foreach(string line in text.Split('\n')) { var match=Regex.Match(line,@"^([A-Z_]+)\s*=\s*([^\r\n]*)");if(match.Success && names.Contains(match.Groups[1].Value)) result[match.Groups[1].Value]=match.Groups[2].Value.Trim(); }
            return result;
        }
        public static string MergeGraphics(string text,Dictionary<string,string> values)
        {
            foreach(var item in values) { string pattern=@"(?m)^"+Regex.Escape(item.Key)+@"\s*=\s*[^\r\n]*";string replacement=item.Key+" = "+item.Value;text=Regex.IsMatch(text,pattern)?Regex.Replace(text,pattern,replacement):text.TrimEnd()+Environment.NewLine+replacement+Environment.NewLine; }return text;
        }
        private void CheckCanceled(){if(IsCanceled())throw new OperationCanceledException("Download canceled. Installed files were kept.");}
        private static bool SameOrigin(Uri a,Uri b){return a.Scheme==b.Scheme && a.Host==b.Host && a.Port==b.Port && a.UserInfo.Length==0 && a.Fragment.Length==0;}
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        public static string Format(long bytes){return (bytes/1048576d).ToString("0.0")+" MiB";}
        private static void AtomicText(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp-"+Guid.NewGuid().ToString("N");File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
    }
}
