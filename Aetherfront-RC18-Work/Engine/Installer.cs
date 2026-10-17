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

namespace KesasInstall
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
        public bool Changed { get; set; }
        public bool ClientUpdate { get; set; }
        public string TransactionPath { get; set; }
    }
    public sealed class ProgressInfo
    {
        public string Status { get; set; }
        public string Package { get; set; }
        public int PackageIndex { get; set; }
        public int PackageCount { get; set; }
        public long Downloaded { get; set; }
        public long Total { get; set; }
        public long PackageDownloaded { get; set; }
        public long PackageTotal { get; set; }
        public double BytesPerSecond { get; set; }
        public double EstimatedSeconds { get; set; } = -1;
        public bool Transferring { get; set; }
    }
    public sealed class Installer
    {
        public readonly string Root;
        public readonly string Data;
        public Action<string> Progress = delegate { };
        public Func<string, bool> Confirm = delegate { return false; };
        public Func<bool> IsCanceled = delegate { return false; };
        public Action<ProgressInfo> Report = delegate { };
        private readonly HashSet<string> pins;
        private readonly Uri updateUrl;
        private readonly string installedVersion;
        private string packageName="";
        private int packageIndex,packageCount;
        private long completed,totalBytes,currentDownloaded,currentTotal;
        private readonly TransferMeter meter=new TransferMeter();
        private readonly object progressGate=new object();
        private readonly Stopwatch reportClock=Stopwatch.StartNew();
        private volatile ResumableTransfer transfer;
        private volatile HttpWebRequest activeRequest;
        private readonly HashSet<string> checkedDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Installer(string root, IPEndPoint endpoint, string version)
        {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            Data = Path.Combine(Root, "GameData", "LunaMultiplayer", "Data");
            installedVersion=version;
            ApfPaths.EnsureNoReparsePoints(Root, Root);
            ApfPaths.EnsureNoReparsePoints(Root, Path.Combine(Root, "GameData"));
            ApfPaths.EnsureNoReparsePoints(Root,Data);
            if (!File.Exists(Path.Combine(Root,"KSP_x64.exe")) || !Directory.Exists(Path.Combine(Root,"GameData","Squad")))
                throw new InvalidDataException("Extract the starter ZIP beside KSP_x64.exe in a clean Windows KSP 1.12.5 copy.");
            pins=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ApfPaths.EnsureNoReparsePoints(Root,Path.Combine(Data,"APFTlsCertificatePins.txt"));
            foreach(string line in File.ReadAllLines(Path.Combine(Data,"APFTlsCertificatePins.txt"))) { string pin=line.Trim();if(pin.Length==0)continue;if(!Regex.IsMatch(pin,"^[0-9a-fA-F]{64}$"))throw new InvalidDataException("Invalid server certificate pin.");pins.Add(pin); }
            if(pins.Count==0 || pins.Count>8) throw new InvalidDataException("Invalid server certificate pin list.");
            if(endpoint==null || endpoint.Port<1 || endpoint.Port>=65535)throw new InvalidDataException("Enter a valid server IP and game port from 1 to 65534.");
            // The paired pack host listens one port above the game server. The
            // address changes, not the trusted certificate or signing key.
            updateUrl = new UriBuilder("https",endpoint.Address.ToString(),endpoint.Port+1,"updates/APFClientUpdate.json").Uri;
            if (updateUrl.Scheme != "https" || updateUrl.UserInfo.Length != 0 || updateUrl.Fragment.Length != 0) throw new InvalidDataException("Invalid update URL.");
        }
        public InstallResult Run(string mode,bool checkUpdates=true)
        {
            if (mode != "full" && mode != "potato") throw new ArgumentException("Unknown install profile");
            checkedDirectories.Clear();
            CheckCanceled();
            ApfPaths.EnsureNoReparsePoints(Root,Root);
            checkedDirectories.Add(Root);
            if(checkUpdates){var client=UpdateClient();if(client!=null)return client;}
            string catalogPath=Path.Combine(Root,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
            ApfPaths.EnsureNoReparsePoints(Root,catalogPath);
            var catalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath));
            var manifest = ApfPackManifest.FromJson((mode == "potato" ? catalog.Potato : catalog.Full).ToJson());
            string serverFile=Path.Combine(Root,"APF",".client-update-staging","connect","server-pack.json");
            SetProgress("Checking server requirements...");
            Download(new Uri(updateUrl,"/APFPack.json"),serverFile,4L*1024*1024,null);
            var server=ApfPackManifest.FromJson(File.ReadAllText(serverFile));
            ValidateIdentity(server,manifest,installedVersion);
            foreach(var package in manifest.ManagedPackages) {
                var original=new Uri(package.ArchiveUrl);
                if(original.Scheme!="https" || original.UserInfo.Length!=0 || original.Fragment.Length!=0 || original.Query.Length!=0 || original.AbsolutePath!= "/"+Path.GetFileName(original.AbsolutePath))throw new InvalidDataException("Invalid catalog package location.");
                package.ArchiveUrl=new Uri(updateUrl,original.AbsolutePath).AbsoluteUri;
            }
            var validation = ApfManifestValidator.Validate(manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ",validation.Errors));
            var sizes = catalog.Sizes.ToDictionary(s=>s.Id,s=>s.ExpandedBytes,StringComparer.OrdinalIgnoreCase);
            string statePath = Path.Combine(Data,"APFPackState.json");
            ApfPaths.EnsureNoReparsePoints(Root,statePath);
            var state = File.Exists(statePath) ? ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath)) : new ApfPackState();
            var old = (state.Packages ?? new List<ApfPackageState>()).ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
            var needed = new List<ApfPackage>();
            SetProgress("Checking installed files...");
            int checking=0;
            foreach(var package in manifest.ManagedPackages)
            {
                CheckCanceled();
                SetProgress("Checking package "+(++checking)+" / "+manifest.ManagedPackages.Count+": "+ApfPaths.PackageRoot(package));
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
            var known=new HashSet<string>(catalog.Full.ManagedPackages.Concat(catalog.Potato.ManagedPackages).Select(p=>ApfPaths.PackageRoot(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
            var extra=deletes.Where(d=>!known.Contains(d.RelativePath.Substring("GameData/".Length))).ToList();
            if(extra.Count>0 && !Confirm("These extra mods are not in the selected profile and will move into a recovery backup:\n\n"+string.Join("\n",extra.Select(d=>d.RelativePath))+"\n\nContinue?"))
                throw new OperationCanceledException("Profile change canceled. No installed files changed.");
            long required=512L*1024*1024;
            foreach(var package in needed) required=checked(required+package.SizeBytes+sizes[package.Id]);
            // Parallel chunks coexist with one assembled archive until its hash
            // is verified. Reserve that additional temporary archive space.
            if(needed.Count>0)required=checked(required+needed.Max(p=>p.SizeBytes));
            if(new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace<required) throw new IOException("Not enough free disk space. Need "+Format(required)+" for downloads and installation.");
            string profilePath=Path.Combine(Data,"KesasInstallProfile.json");
            ApfPaths.EnsureNoReparsePoints(Root,profilePath);
            var profile=File.Exists(profilePath)?ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(profilePath)):new Dictionary<string,string>();
            bool profileChanged=!profile.TryGetValue("mode",out var currentMode)||currentMode!=mode||!profile.TryGetValue("catalogVersion",out var currentCatalog)||currentCatalog!=catalog.Version;
            if(needed.Count==0 && deletes.Count==0 && !profileChanged)return new InstallResult();
            var transaction = new ApfSyncTransaction { GameRoot=Root, GameDataPath=Path.Combine(Root,"GameData"), KspExecutable=Path.Combine(Root,"KSP_x64.exe"), ProcessId=Process.GetCurrentProcess().Id, KspArguments=Environment.GetCommandLineArgs().Skip(1).ToList(), StatePath=statePath, Manifest=manifest, ExistingState=state, Deletes=deletes };
            string stage=Path.Combine(Root,"APF",".staging","connect");
            ApfPaths.EnsureNoReparsePoints(Root,stage);
            Directory.CreateDirectory(stage);
            completed=0;totalBytes=needed.Sum(p=>p.SizeBytes);packageCount=needed.Count;meter.Reset();
            for(int i=0;i<needed.Count;i++)
            {
                var package=needed[i];
                packageName=ApfPaths.PackageRoot(package);packageIndex=i+1;currentDownloaded=currentTotal=0;
                SetProgress("Downloading package "+packageIndex+" / "+packageCount+": "+packageName);
                var uri=new Uri(package.ArchiveUrl);
                if(!SameOrigin(uri,updateUrl)) throw new InvalidDataException("Package is not hosted by this server.");
                string archive=Path.Combine(stage,package.Sha256+".zip");
                Download(uri,archive,package.SizeBytes,package.Sha256);
                SetProgress("Verifying "+packageName+"...");
                ApfArchiveInstaller.ValidateArchive(archive,package,Path.Combine(Root,"GameData"));
                transaction.Packages.Add(new ApfStagedPackage { Package=package, ArchivePath=archive });
                completed+=package.SizeBytes;
                currentDownloaded=currentTotal=0;
            }
            CheckCanceled();
            {
                SetProgress("Rechecking server requirements before restart...");
                Download(new Uri(updateUrl,"/APFPack.json"),serverFile,4L*1024*1024,null);
                ValidateIdentity(ApfPackManifest.FromJson(File.ReadAllText(serverFile)),manifest,installedVersion);
                currentDownloaded=currentTotal=0;
                SetProgress("Preparing installation; KSP will restart...");
                string helperKey=Path.Combine(Data,"APFHelper.key");ApfPaths.EnsureNoReparsePoints(Root,helperKey);
                var key=ApfTransactionSecurity.LoadOrCreateKey(helperKey);
                ApfTransactionSecurity.Sign(transaction,key);
                string request=Path.Combine(stage,Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(request));
                File.WriteAllText(request,ApfJson.Serialize(transaction));
                return new InstallResult { Changed=true,TransactionPath=request };
            }
        }
        private InstallResult UpdateClient()
        {
            SetProgress("Checking client version...");
            string stage=Path.Combine(Root,"APF",".client-update-staging","connect"); ApfPaths.EnsureNoReparsePoints(Root,stage);Directory.CreateDirectory(stage);
            string manifestFile=Path.Combine(stage,"feed.json");
            Download(updateUrl,manifestFile,4L*1024*1024,null);
            var manifest=ApfClientUpdateManifest.FromJson(File.ReadAllText(manifestFile));
            var validation=ApfClientUpdateValidator.Validate(manifest);
            if(!validation.IsValid) throw new InvalidDataException(string.Join("; ",validation.Errors));
            string publicKey=Path.Combine(Data,"APFClientUpdatePublicKey.xml");ApfPaths.EnsureNoReparsePoints(Root,publicKey);
            using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))
                if(!ApfClientUpdateSecurity.Verify(manifest,key)) throw new InvalidDataException("Client update signature is invalid.");
            int compared=ApfClientUpdateVersion.Compare(manifest.Version,installedVersion);
            if(compared<0)return null;
            if(compared==0 && ClientFilesMatch(manifest))return null;
            if(manifest.KspVersion!="1.12.5")throw new InvalidDataException("This update requires a different KSP version.");
            var uri=new Uri(manifest.PackageUrl);
            if(!SameOrigin(uri,updateUrl)) throw new InvalidDataException("Client update is not hosted by this server.");
            var approval=ApfClientUpdateApproval.Capture(Root);
            packageName="Aetherfront client "+manifest.Version;packageIndex=packageCount=1;completed=0;totalBytes=manifest.SizeBytes;meter.Reset();
            string archive=Path.Combine(stage,manifest.Sha256+".zip"); Download(uri,archive,manifest.SizeBytes,manifest.Sha256);
            approval.EnsureCurrent(Root);
            var transaction=new ApfClientUpdateTransaction { GameRoot=Root,GameDataPath=Path.Combine(Root,"GameData"),KspExecutable=Path.Combine(Root,"KSP_x64.exe"),ProcessId=Process.GetCurrentProcess().Id,KspArguments=Environment.GetCommandLineArgs().Skip(1).ToList(),ArchivePath=archive,Manifest=manifest,Approval=approval };
            string helperKey=Path.Combine(Data,"APFHelper.key");ApfPaths.EnsureNoReparsePoints(Root,helperKey);
            ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadOrCreateKey(helperKey));
            string request=Path.Combine(stage,Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(request));File.WriteAllText(request,ApfJson.Serialize(transaction));
            SetProgress("Client update downloaded; preparing restart...");
            return new InstallResult {Changed=true,ClientUpdate=true,TransactionPath=request};
        }
        public static void ValidateIdentity(ApfPackManifest server,ApfPackManifest catalog,string version)
        {
            var validation=ApfManifestValidator.Validate(server);
            if(!validation.IsValid)throw new InvalidDataException(string.Join("; ",validation.Errors));
            if(server.ServerId!=catalog.ServerId || server.PackId!=catalog.PackId || server.PackVersion!=catalog.PackVersion || server.RequiredClientVersion!=version || catalog.RequiredClientVersion!=version || server.KspVersion!="1.12.5")
                throw new InvalidDataException("Server requirements changed or this starter belongs to another server. Reconnect for the current client; do not install untrusted content.");
        }
        private bool ClientFilesMatch(ApfClientUpdateManifest manifest)
        {
            foreach(var file in manifest.Files) {
                CheckCanceled();
                string target=Path.GetFullPath(Path.Combine(Root,file.Path));
                if(!ApfPaths.IsUnder(target,Root))throw new InvalidDataException("Client file escapes the game folder.");
                ApfPaths.EnsureNoReparsePoints(Root,target);
                if(!File.Exists(target)||new FileInfo(target).Length!=file.SizeBytes||!string.Equals(ApfJson.Sha256File(target),file.Sha256,StringComparison.OrdinalIgnoreCase))return false;
            }
            return true;
        }
        private bool PackagePresent(ApfPackage package,ApfPackageState state)
        {
            if(state.Version!=package.Version || !string.Equals(state.ArchiveSha256,package.Sha256,StringComparison.OrdinalIgnoreCase) || state.Files==null || state.Files.Count==0) return false;
            foreach(var file in state.Files)
            {
                CheckCanceled();
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
        public void Cancel(){transfer?.Cancel();try{activeRequest?.Abort();}catch(WebException){}}
        private void Download(Uri uri,string destination,long maximum,string expectedHash)
        {
            CheckCanceled();
            if(uri.Scheme!="https" || !SameOrigin(uri,updateUrl)) throw new InvalidDataException("Only this server's HTTPS downloads are allowed.");
            ApfPaths.EnsureNoReparsePoints(Root,destination);
            if(expectedHash!=null && File.Exists(destination) && new FileInfo(destination).Length==maximum && string.Equals(ApfJson.Sha256File(destination),expectedHash,StringComparison.OrdinalIgnoreCase)){ReportBytes(maximum,maximum);return;}
            if(expectedHash!=null) { DownloadArchive(uri,destination,maximum,expectedHash);return; }
            string part=destination+".part";
            long offset=expectedHash!=null && File.Exists(part) ? new FileInfo(part).Length : 0;
            if(offset>=maximum) offset=0;
            var request=(HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect=false;request.Timeout=30000;request.ReadWriteTimeout=30000;
            activeRequest=request;
            request.CachePolicy=new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>certificate!=null && pins.Contains(Hash(certificate.GetRawCertData()));
            if(offset>0) request.AddRange(offset);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try { using(var response=(HttpWebResponse)request.GetResponse())
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
                        if(expectedHash!=null && timer.ElapsedMilliseconds>=200){ReportBytes(total,maximum);timer.Restart();}
                    }
                    if(expectedHash!=null && total!=maximum) throw new IOException("Download ended early; retry to resume.");
                }
            }
            }finally{activeRequest=null;}
            if(expectedHash!=null && !string.Equals(ApfJson.Sha256File(part),expectedHash,StringComparison.OrdinalIgnoreCase)) { File.Delete(part);throw new InvalidDataException("Downloaded file hash differs from the signed catalog."); }
            if(File.Exists(destination)) File.Replace(part,destination,null); else File.Move(part,destination);
            if(expectedHash!=null)ReportBytes(maximum,maximum);
        }
        private void DownloadArchive(Uri uri,string destination,long size,string hash)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string part=destination+".part";
            transfer=new ResumableTransfer {
                CreateRequest=CreateArchiveRequest,IsCanceled=IsCanceled,
                ValidatePath=path=>ApfPaths.EnsureNoReparsePoints(Root,path),
                Received=meter.Add,Progress=count=>ReportTransfer(count,size)
            };
            meter.Start();
            try {
                string downloaded=transfer.Download(uri,part,size,true);
                meter.Pause();SetProgress("Verifying download: "+packageName+"...");CheckCanceled();
                if(!string.Equals(ApfJson.Sha256File(downloaded),hash,StringComparison.OrdinalIgnoreCase)) {
                    transfer.Cleanup(part,size);
                    throw new InvalidDataException("Downloaded file hash differs from the signed catalog. Retry to download a clean copy.");
                }
                CheckCanceled();
                if(File.Exists(destination))File.Replace(downloaded,destination,null);else File.Move(downloaded,destination);
                transfer.Cleanup(part,size);ReportBytes(size,size);
            } finally {meter.Pause();transfer=null;}
        }
        private HttpWebRequest CreateArchiveRequest(Uri uri)
        {
            var request=(HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect=false;request.Timeout=30000;request.ReadWriteTimeout=30000;
            request.AutomaticDecompression=DecompressionMethods.None;
            request.CachePolicy=new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.ServicePoint.ConnectionLimit=ResumableTransfer.Connections;
            request.ServicePoint.ReceiveBufferSize=1024*1024;
            request.ServicePoint.UseNagleAlgorithm=false;
            request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>certificate!=null && pins.Contains(Hash(certificate.GetRawCertData()));
            return request;
        }
        private void ReportTransfer(long count,long size)
        {
            lock(progressGate) {
                currentDownloaded=Math.Max(currentDownloaded,count);currentTotal=size;
                if(reportClock.ElapsedMilliseconds<250 && count<size)return;
                reportClock.Restart();PublishProgress("Downloading "+packageName,true);
            }
        }
        public void SetGraphics(string mode)
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
        private void SetProgress(string text){lock(progressGate){Progress(text);PublishProgress(text,false);}}
        private void ReportBytes(long current,long size){lock(progressGate){currentDownloaded=current;currentTotal=size;PublishProgress("Downloaded "+packageName,false);}}
        private void PublishProgress(string status,bool transferring){double speed,seconds;meter.Read(Math.Max(0,totalBytes-completed-currentDownloaded),out speed,out seconds);Report(new ProgressInfo{Status=status,Package=packageName,PackageIndex=packageIndex,PackageCount=packageCount,Downloaded=completed+currentDownloaded,Total=totalBytes,PackageDownloaded=currentDownloaded,PackageTotal=currentTotal,BytesPerSecond=speed,EstimatedSeconds=seconds,Transferring=transferring});}
        private static bool SameOrigin(Uri a,Uri b){return a.Scheme==b.Scheme && a.Host==b.Host && a.Port==b.Port && a.UserInfo.Length==0 && a.Fragment.Length==0;}
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        public static string Format(long bytes){return (bytes/1048576d).ToString("0.0")+" MiB";}
        public static void AtomicText(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp-"+Guid.NewGuid().ToString("N");File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
    }
}
