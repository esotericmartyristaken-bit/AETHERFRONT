using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        public GitHubSettings GitHub { get; set; } = new GitHubSettings();
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
        private GitHubSettings github=new GitHubSettings();
        private bool githubReady;
        private readonly string installedVersion;
        private ApfClientUpdateManifest verifiedClientManifest;
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
            try {using(DownloadFiles.Acquire(Root))return RunCore(mode,checkUpdates);}
            catch(Exception error){Log("LastDownloadError.log",error.ToString());throw;}
        }
        private InstallResult RunCore(string mode,bool checkUpdates)
        {
            if (mode != "full" && mode != "potato") throw new ArgumentException("Unknown install profile");
            checkedDirectories.Clear();
            verifiedClientManifest=null;
            CheckCanceled();
            ApfPaths.EnsureNoReparsePoints(Root,Root);
            checkedDirectories.Add(Root);
            LoadGitHubSettings();
            bool duplicateClient=ConfirmDuplicateClient();
            bool obsoleteInstaller=File.Exists(Path.Combine(Root,"GameData","LunaMultiplayer","Launcher","AetherfrontInstallUI.bin"));
            InstallResult pendingClient=checkUpdates?UpdateClient(mode):null;
            string targetVersion=pendingClient==null?installedVersion:verifiedClientManifest.Version;
            string catalogPath=Path.Combine(Root,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
            if(pendingClient!=null)catalogPath=StageClientCatalog(pendingClient);
            ApfPaths.EnsureNoReparsePoints(Root,catalogPath);
            var catalog = ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath));
            var manifest = ApfPackManifest.FromJson((mode == "potato" ? catalog.Potato : catalog.Full).ToJson());
            string serverFile=Path.Combine(Root,"APF",".client-update-staging","connect","server-pack.json");
            SetProgress("Checking server requirements...");
            Download(new Uri(updateUrl,"/APFPack.json"),serverFile,4L*1024*1024,null);
            var server=ApfPackManifest.FromJson(File.ReadAllText(serverFile));
            ValidateIdentity(server,manifest,targetVersion);
            foreach(var package in manifest.ManagedPackages) {
                var original=new Uri(package.ArchiveUrl);
                if(original.Scheme!="https" || original.UserInfo.Length!=0 || original.Fragment.Length!=0 || original.Query.Length!=0 || original.AbsolutePath!= "/"+Path.GetFileName(original.AbsolutePath))throw new InvalidDataException("Invalid catalog package location.");
                package.ArchiveUrl=new Uri(updateUrl,original.AbsolutePath).AbsoluteUri;
            }
            var validation = ApfManifestValidator.Validate(manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ",validation.Errors));
            // A successful helper receipt is the reconnect proof. Do not rescan
            // player-modified runtime data or every shipped client file here.
            if(!duplicateClient && DuplicateClientExists())duplicateClient=ConfirmDuplicateClient();
            if(!duplicateClient && !obsoleteInstaller && pendingClient==null && CompletedPackMatches(mode,catalog,manifest))return new InstallResult();
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
            if(extra.Count>0 && !Confirm("These extra mods are not in the selected profile and will be permanently removed. Removed mods will not be backed up:\n\n"+string.Join("\n",extra.Select(d=>d.RelativePath))+"\n\nRemove these mods and continue?"))
                throw new OperationCanceledException("Profile change canceled. No installed files changed.");
            long required=512L*1024*1024;
            if(pendingClient!=null)foreach(var file in verifiedClientManifest.Files)required=checked(required+2*file.SizeBytes);
            foreach(var package in needed) required=checked(required+package.SizeBytes+sizes[package.Id]);
            // Parallel chunks coexist with one assembled archive until its hash
            // is verified. Reserve that additional temporary archive space.
            if(needed.Count>0)required=checked(required+needed.Max(p=>p.SizeBytes));
            if(new DriveInfo(Path.GetPathRoot(Root)).AvailableFreeSpace<required) throw new IOException("Not enough free disk space. Need "+Format(required)+" for downloads and installation.");
            string profilePath=Path.Combine(Data,"KesasInstallProfile.json");
            ApfPaths.EnsureNoReparsePoints(Root,profilePath);
            var profile=File.Exists(profilePath)?ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(profilePath)):new Dictionary<string,string>();
            bool profileChanged=!profile.TryGetValue("mode",out var currentMode)||currentMode!=mode||!profile.TryGetValue("catalogVersion",out var currentCatalog)||currentCatalog!=catalog.Version;
            if(!duplicateClient && !obsoleteInstaller && pendingClient==null && needed.Count==0 && deletes.Count==0 && !profileChanged)return new InstallResult();
            InvalidateCompletedReceipt("pack");
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
                ValidateIdentity(ApfPackManifest.FromJson(File.ReadAllText(serverFile)),manifest,targetVersion);
                currentDownloaded=currentTotal=0;
                SetProgress("Preparing installation in game. Restart KSP when ready.");
                string helperKey=Path.Combine(Data,"APFHelper.key");ApfPaths.EnsureNoReparsePoints(Root,helperKey);
                var key=ApfTransactionSecurity.LoadOrCreateKey(helperKey);
                ApfTransactionSecurity.Sign(transaction,key);
                string request=Path.Combine(stage,Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(request));
                File.WriteAllText(request,ApfJson.Serialize(transaction));
                if(pendingClient!=null)WriteBundledClient(pendingClient,request);
                if(duplicateClient)File.WriteAllText(request+".duplicate","true",new UTF8Encoding(false));
                PreparedPack.Prepare(transaction,request,SetProgress,IsCanceled);
                CheckCanceled();
                return new InstallResult { Changed=true,TransactionPath=request };
            }
        }
        private InstallResult UpdateClient(string mode)
        {
            SetProgress("Checking client version...");
            string stage=Path.Combine(Root,"APF",".client-update-staging","connect"); ApfPaths.EnsureNoReparsePoints(Root,stage);Directory.CreateDirectory(stage);
            ApfClientUpdateManifest latest=null;
            if(github.Enabled) {
                SetProgress("Checking GitHub for client updates...");
                string channel=Path.Combine(stage,"github-feed.json");
                try{Download(github.LatestFeed,channel,4L*1024*1024,null);latest=ReadClientManifest(channel);githubReady=true;}
                catch(WebException error) when(GitHubSettings.CanFallBack(error)) {
                    CheckCanceled();githubReady=false;Warn("GitHub unavailable; using the pinned server for this connection.");
                }
            }
            string manifestFile=Path.Combine(stage,"feed.json");
            Download(updateUrl,manifestFile,4L*1024*1024,null);
            var manifest=ReadClientManifest(manifestFile);
            verifiedClientManifest=manifest;
            // GitHub's channel must never install a future client before this
            // selected server has activated that release. Both feeds are signed.
            if(latest!=null && latest.Version==manifest.Version && latest.ToJson()!=manifest.ToJson())throw new InvalidDataException("GitHub and server release manifests disagree.");
            int compared=ApfClientUpdateVersion.Compare(manifest.Version,installedVersion);
            if(compared<0)return null;
            if(compared==0 && (CompletedClientMatches(mode,manifest) || ClientFilesMatch(manifest)))return null;
            if(manifest.KspVersion!="1.12.5")throw new InvalidDataException("This update requires a different KSP version.");
            var uri=new Uri(manifest.PackageUrl);
            if(!SameOrigin(uri,updateUrl)) throw new InvalidDataException("Client update is not hosted by this server.");
            InvalidateCompletedReceipt("client");
            var approval=ApfClientUpdateApproval.Capture(Root);
            packageName="Aetherfront client "+manifest.Version;packageIndex=packageCount=1;completed=0;totalBytes=manifest.SizeBytes;meter.Reset();
            string archive=Path.Combine(stage,manifest.Sha256+".zip"); Download(uri,archive,manifest.SizeBytes,manifest.Sha256);
            approval.EnsureCurrent(Root);
            var transaction=new ApfClientUpdateTransaction { GameRoot=Root,GameDataPath=Path.Combine(Root,"GameData"),KspExecutable=Path.Combine(Root,"KSP_x64.exe"),ProcessId=Process.GetCurrentProcess().Id,KspArguments=Environment.GetCommandLineArgs().Skip(1).ToList(),ArchivePath=archive,Manifest=manifest,Approval=approval };
            string helperKey=Path.Combine(Data,"APFHelper.key");ApfPaths.EnsureNoReparsePoints(Root,helperKey);
            ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadOrCreateKey(helperKey));
            string request=Path.Combine(stage,Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(request));File.WriteAllText(request,ApfJson.Serialize(transaction));
            SetProgress("Client update downloaded. Restart KSP when ready to finish installation.");
            return new InstallResult {Changed=true,ClientUpdate=true,TransactionPath=request};
        }
        private bool DuplicateClientExists()
        {
            return File.Exists(Path.Combine(Root,"Resources","GameData","LunaMultiplayer","Plugins","LmpClient.dll"));
        }
        private bool ConfirmDuplicateClient()
        {
            if(!DuplicateClientExists())return false;
            string folder=Path.Combine(Root,"Resources","GameData","LunaMultiplayer");
            ApfPaths.EnsureNoReparsePoints(Root,Path.Combine(folder,"Plugins","LmpClient.dll"));
            if(!Confirm("An extra multiplayer client was found at Resources/GameData/LunaMultiplayer. Disable this duplicate installation and continue? Saves/identity kept."))
                throw new OperationCanceledException("Duplicate client repair canceled. No installed files changed.");
            return true;
        }
        private ApfClientUpdateTransaction ReadPendingClient(InstallResult pending,out string json)
        {
            string path=Path.GetFullPath(pending.TransactionPath);
            ApfPaths.EnsureNoReparsePoints(Root,path);
            if(!pending.ClientUpdate || !ApfPaths.IsUnder(path,Path.Combine(Root,"APF",".client-update-staging")) ||
                Path.GetFileName(path)!="transaction.json")throw new InvalidDataException("Invalid bundled client request location.");
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(input.Length>16L*1024*1024)throw new InvalidDataException("Bundled client request is too large.");
                using(var reader=new StreamReader(input))json=reader.ReadToEnd();
            }
            var client=ApfJson.Deserialize<ApfClientUpdateTransaction>(json);
            string keyPath=Path.Combine(Data,"APFHelper.key");ApfPaths.EnsureNoReparsePoints(Root,keyPath);
            if(client==null || client.Manifest==null || client.Approval==null || verifiedClientManifest==null ||
                !ApfTransactionSecurity.Verify(client,ApfTransactionSecurity.LoadKey(keyPath)) ||
                !string.Equals(Path.GetFullPath(client.GameRoot).TrimEnd(Path.DirectorySeparatorChar),Root,StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(client.GameDataPath),Path.Combine(Root,"GameData"),StringComparison.OrdinalIgnoreCase) ||
                client.Manifest.ToJson()!=verifiedClientManifest.ToJson())throw new InvalidDataException("Bundled client authorization differs from the active signed release.");
            return client;
        }
        private string StageClientCatalog(InstallResult pending)
        {
            string requestJson;
            var client=ReadPendingClient(pending,out requestJson);
            const string relative="GameData/LunaMultiplayer/Launcher/install-catalog.json";
            var declared=client.Manifest.Files.SingleOrDefault(f=>string.Equals(f.Path,relative,StringComparison.OrdinalIgnoreCase));
            if(declared==null || declared.SizeBytes<=0 || declared.SizeBytes>4L*1024*1024)throw new InvalidDataException("Signed client catalog is missing or too large.");
            ApfPaths.EnsureNoReparsePoints(Root,client.ArchivePath);
            if(!ApfPaths.IsUnder(client.ArchivePath,Path.Combine(Root,"APF",".client-update-staging")))throw new InvalidDataException("Bundled client archive escapes staging.");
            string staged=Path.Combine(Path.GetDirectoryName(pending.TransactionPath),"target-catalog.json");
            ApfPaths.EnsureNoReparsePoints(Root,staged);
            SetProgress("Validating the client update and staging its install catalog...");
            CheckCanceled();
            using(var held=new FileStream(client.ArchivePath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                ApfArchiveInstaller.ValidateClientUpdateArchive(client.ArchivePath,client.Manifest,Root);
                CheckCanceled();
                using(var archive=new ZipArchive(held,ZipArchiveMode.Read,true)) {
                    var entries=archive.Entries.Where(e=>string.Equals(e.FullName.Replace('\\','/'),relative,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(entries.Length!=1 || entries[0].Length!=declared.SizeBytes)throw new InvalidDataException("Signed client catalog archive entry is missing or inconsistent.");
                    using(var input=entries[0].Open())
                    using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                        var buffer=new byte[65536];long copied=0;int count;
                        while((count=input.Read(buffer,0,buffer.Length))>0) {
                            CheckCanceled();copied=checked(copied+count);
                            if(copied>declared.SizeBytes)throw new InvalidDataException("Client catalog exceeded its signed size.");
                            output.Write(buffer,0,count);
                        }
                        if(copied!=declared.SizeBytes)throw new InvalidDataException("Client catalog ended before its signed size.");
                    }
                }
            }
            if(!string.Equals(ApfJson.Sha256File(staged),declared.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Staged client catalog differs from the signed release.");
            var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(staged));
            if(catalog==null || catalog.Full==null || catalog.Potato==null || catalog.Version!=client.Manifest.Version ||
                catalog.Full.PackVersion!=catalog.Version || catalog.Potato.PackVersion!=catalog.Version)throw new InvalidDataException("Staged catalog does not match the client revision.");
            ValidateIdentity(catalog.Full,catalog.Potato,client.Manifest.Version);
            ValidateIdentity(catalog.Potato,catalog.Full,client.Manifest.Version);
            return staged;
        }
        private void WriteBundledClient(InstallResult pending,string request)
        {
            string json;ReadPendingClient(pending,out json);
            string path=request+".client";ApfPaths.EnsureNoReparsePoints(Root,path);
            if(!ApfPaths.IsUnder(path,Path.Combine(Root,"APF",".staging")))throw new InvalidDataException("Bundled client sidecar escapes pack staging.");
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(output,new UTF8Encoding(false)))writer.Write(json);
        }
        private ApfClientUpdateManifest ReadClientManifest(string path)
        {
            var manifest=ApfClientUpdateManifest.FromJson(File.ReadAllText(path));
            var validation=ApfClientUpdateValidator.Validate(manifest);
            if(!validation.IsValid)throw new InvalidDataException(string.Join("; ",validation.Errors));
            string publicKey=Path.Combine(Data,"APFClientUpdatePublicKey.xml");ApfPaths.EnsureNoReparsePoints(Root,publicKey);
            using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))
                if(!ApfClientUpdateSecurity.Verify(manifest,key))throw new InvalidDataException("Client update signature is invalid.");
            return manifest;
        }
        private void LoadGitHubSettings()
        {
            github=new GitHubSettings();githubReady=false;
            string path=Path.Combine(Root,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
            ApfPaths.EnsureNoReparsePoints(Root,path);
            if(!File.Exists(path) || new FileInfo(path).Length>4L*1024*1024)return;
            try{var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(path));var settings=catalog==null?null:catalog.GitHub;if(settings!=null){settings.Validate();github=settings;}}
            catch(Exception error) when(error is InvalidDataException || error is FormatException || error.GetType().FullName=="JsonFx.Json.JsonDeserializationException") {
                // A missing/damaged routing catalog must not prevent the pinned
                // signed updater from repairing that very client file.
                Warn("Client download settings need repair; checking the pinned server.");
            }
        }
        public static void ValidateIdentity(ApfPackManifest server,ApfPackManifest catalog,string version)
        {
            var validation=ApfManifestValidator.Validate(server);
            if(!validation.IsValid)throw new InvalidDataException(string.Join("; ",validation.Errors));
            if(server.ServerId!=catalog.ServerId || server.PackId!=catalog.PackId || server.PackVersion!=catalog.PackVersion || server.RequiredClientVersion!=version || catalog.RequiredClientVersion!=version || server.KspVersion!="1.12.5")
                throw new InvalidDataException("Server requirements changed or this starter belongs to another server. Reconnect for the current client; do not install untrusted content.");
        }
        // Used only by the connection coordinator, never the installer's final
        // verification. Connect has just checked both small signed feeds.
        public static ApfSyncPlan ScanConnection(string gameData,ApfPackManifest server,ApfPackState state)
        {
            try {
                string root=Directory.GetParent(Path.GetFullPath(gameData)).FullName;
                if(!string.Equals(Path.GetFullPath(gameData),Path.Combine(root,"GameData"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid connection scan root.");
                string data=Path.Combine(root,"GameData","LunaMultiplayer","Data");
                string feedPath=Path.Combine(root,"APF",".client-update-staging","connect","feed.json");
                string serverPath=Path.Combine(root,"APF",".client-update-staging","connect","server-pack.json");
                string catalogPath=Path.Combine(root,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
                string profilePath=Path.Combine(data,"KesasInstallProfile.json");
                string publicPath=Path.Combine(data,"APFClientUpdatePublicKey.xml");
                foreach(string path in new[]{feedPath,serverPath,catalogPath,profilePath,publicPath})ApfPaths.EnsureNoReparsePoints(root,path);
                if(new FileInfo(feedPath).Length>4L*1024*1024||new FileInfo(serverPath).Length>4L*1024*1024||new FileInfo(catalogPath).Length>4L*1024*1024||new FileInfo(profilePath).Length>65536)throw new InvalidDataException("Connection metadata is too large.");
                var feed=ApfClientUpdateManifest.FromJson(File.ReadAllText(feedPath));
                if(!ApfClientUpdateValidator.Validate(feed).IsValid)throw new InvalidDataException("Invalid connection client manifest.");
                using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicPath)))if(!ApfClientUpdateSecurity.Verify(feed,key))throw new InvalidDataException("Connection client signature is invalid.");
                var profile=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(profilePath));
                if(profile==null||!profile.TryGetValue("mode",out var mode)||(mode!="full"&&mode!="potato"))throw new InvalidDataException("Connection profile is missing.");
                var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath));
                if(catalog==null||catalog.Full==null||catalog.Potato==null)throw new InvalidDataException("Connection catalog is incomplete.");
                var selected=mode=="potato"?catalog.Potato:catalog.Full;
                ValidateIdentity(server,selected,feed.Version);
                if(state==null||state.PackId!=server.PackId||state.PackVersion!=server.PackVersion||ApfPackManifest.FromJson(File.ReadAllText(serverPath)).ToJson()!=server.ToJson())throw new InvalidDataException("Connection requirements changed.");
                var installer=new Installer(root,new IPEndPoint(IPAddress.Loopback,18865),feed.Version){verifiedClientManifest=feed};
                if(installer.CompletedPackMatches(mode,catalog,selected))return new ApfSyncPlan{PackId=server.PackId,PackVersion=server.PackVersion};
            }catch(Exception error) when(error is IOException||error is InvalidDataException||error is UnauthorizedAccessException||error is ArgumentException||error is InvalidOperationException||error is FormatException||error is System.Security.Cryptography.CryptographicException||error.GetType().FullName=="JsonFx.Json.JsonDeserializationException"){}
            return ApfPackScanner.Scan(gameData,server,state);
        }
        private bool ClientFilesMatch(ApfClientUpdateManifest manifest)
        {
            foreach(var file in manifest.Files) {
                CheckCanceled();
                string target=Path.GetFullPath(Path.Combine(Root,file.Path));
                if(!ApfPaths.IsUnder(target,Root))throw new InvalidDataException("Client file escapes the game folder.");
                ApfPaths.EnsureNoReparsePoints(Root,target);
                if(RuntimeFiles.IsMutable(file.Path)){if(Directory.Exists(target))return false;continue;}
                if(!File.Exists(target))return false;
                if(new FileInfo(target).Length!=file.SizeBytes||!string.Equals(ApfJson.Sha256File(target),file.Sha256,StringComparison.OrdinalIgnoreCase))return false;
            }
            return true;
        }
        private bool PackagePresent(ApfPackage package,ApfPackageState state)
        {
            if(state.Version!=package.Version ||
               !string.Equals(state.ArchiveSha256,package.Sha256,StringComparison.OrdinalIgnoreCase) ||
               state.Files==null || state.Files.Count==0)return false;
            CheckCanceled();
            string path=Path.Combine(Root,"GameData",ApfPaths.PackageRoot(package).Replace('/',Path.DirectorySeparatorChar));
            ApfPaths.EnsureNoReparsePoints(Root,path);
            if(!File.Exists(path) && !Directory.Exists(path))return false;
            // Update/profile-change preflight and final verification must agree.
            // Unchanged completed reconnects still bypass this scan entirely.
            return RuntimeFiles.SameFiles(state.Files,RuntimeFiles.ScanFiles(Path.Combine(Root,"GameData"),path));
        }
        private bool CompletedClientMatches(string mode,ApfClientUpdateManifest manifest)
        {
            if(DuplicateClientExists())return false;
            var receipt=ReadCompletedReceipt(Root);
            if(receipt==null || !ReceiptValue(receipt,"mode",mode) || !ReceiptValue(receipt,"clientVersion",installedVersion) ||
                manifest.Version!=installedVersion || !ReceiptValue(receipt,"clientManifestSha256",TextHash(manifest.ToJson())))return false;
            string catalogPath=Path.Combine(Root,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
            string profilePath=Path.Combine(Data,"KesasInstallProfile.json");
            string statePath=Path.Combine(Data,"APFPackState.json");
            ApfPaths.EnsureNoReparsePoints(Root,catalogPath);ApfPaths.EnsureNoReparsePoints(Root,profilePath);ApfPaths.EnsureNoReparsePoints(Root,statePath);
            if(!File.Exists(catalogPath) || !File.Exists(profilePath) || !File.Exists(statePath) ||
                new FileInfo(catalogPath).Length>4L*1024*1024 || new FileInfo(profilePath).Length>65536)return false;
            var signedCatalog=manifest.Files.SingleOrDefault(f=>string.Equals(f.Path,"GameData/LunaMultiplayer/Launcher/install-catalog.json",StringComparison.OrdinalIgnoreCase));
            if(signedCatalog==null || !ReceiptValue(receipt,"catalogSha256",signedCatalog.Sha256) ||
                !string.Equals(ApfJson.Sha256File(catalogPath),signedCatalog.Sha256,StringComparison.OrdinalIgnoreCase))return false;
            try {
                var profile=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(profilePath));
                return profile!=null && ReceiptValue(profile,"mode",mode) &&
                    receipt.TryGetValue("catalogVersion",out var version) && ReceiptValue(profile,"catalogVersion",version);
            }catch(Exception error) when(ReceiptReadError(error)){return false;}
        }
        private bool CompletedPackMatches(string mode,InstallCatalog catalog,ApfPackManifest manifest)
        {
            if(verifiedClientManifest==null || !CompletedClientMatches(mode,verifiedClientManifest))return false;
            var receipt=ReadCompletedReceipt(Root);
            return receipt!=null && ReceiptValue(receipt,"catalogVersion",catalog.Version) &&
                ReceiptValue(receipt,"serverId",manifest.ServerId) && ReceiptValue(receipt,"packId",manifest.PackId) &&
                ReceiptValue(receipt,"packVersion",manifest.PackVersion) &&
                ReceiptValue(receipt,"packManifestSha256",TextHash(manifest.ToJson()));
        }
        // Call only after authenticated post-exit commit, final verification,
        // graphics and profile/state persistence. NativeInstall must not overwrite
        // this record afterwards with its old status-only completion record.
        public static void WriteCompletedReceipt(ApfSyncTransaction transaction,string mode)
        {
            if(transaction==null || transaction.Manifest==null || (mode!="full" && mode!="potato"))throw new InvalidDataException("Invalid completed installation.");
            string root=Path.GetFullPath(transaction.GameRoot).TrimEnd(Path.DirectorySeparatorChar);
            if(File.Exists(Path.Combine(root,"Resources","GameData","LunaMultiplayer","Plugins","LmpClient.dll")))throw new InvalidDataException("Duplicate multiplayer client remains enabled.");
            string data=Path.Combine(root,"GameData","LunaMultiplayer","Data");
            string catalogPath=Path.Combine(root,"GameData","LunaMultiplayer","Launcher","install-catalog.json");
            string feedPath=Path.Combine(root,"APF",".client-update-staging","connect","feed.json");
            string profilePath=Path.Combine(data,"KesasInstallProfile.json");
            string statePath=Path.Combine(data,"APFPackState.json");
            string keyPath=Path.Combine(data,"APFHelper.key");
            string publicKey=Path.Combine(data,"APFClientUpdatePublicKey.xml");
            foreach(string path in new[]{catalogPath,feedPath,profilePath,statePath,keyPath,publicKey})ApfPaths.EnsureNoReparsePoints(root,path);
            if(new FileInfo(catalogPath).Length>4L*1024*1024 || new FileInfo(feedPath).Length>4L*1024*1024 ||
                new FileInfo(profilePath).Length>65536)throw new InvalidDataException("Completion metadata exceeds its size limit.");
            var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath));
            var profile=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(profilePath));
            var feed=ApfClientUpdateManifest.FromJson(File.ReadAllText(feedPath));
            var validation=ApfClientUpdateValidator.Validate(feed);
            if(!validation.IsValid)throw new InvalidDataException("Completion client manifest is invalid.");
            using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))
                if(!ApfClientUpdateSecurity.Verify(feed,key))throw new InvalidDataException("Completion client manifest signature is invalid.");
            var selected=mode=="potato"?catalog.Potato:catalog.Full;
            ValidateIdentity(transaction.Manifest,selected,feed.Version);
            if(catalog.Version!=transaction.Manifest.PackVersion || profile==null || !ReceiptValue(profile,"mode",mode) ||
                !ReceiptValue(profile,"catalogVersion",catalog.Version) || !File.Exists(statePath))throw new InvalidDataException("Installation completion has not committed its profile and state.");
            string catalogHash=ApfJson.Sha256File(catalogPath);
            var declared=feed.Files.SingleOrDefault(f=>string.Equals(f.Path,"GameData/LunaMultiplayer/Launcher/install-catalog.json",StringComparison.OrdinalIgnoreCase));
            if(declared==null || !string.Equals(declared.Sha256,catalogHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Completed catalog differs from the signed client manifest.");
            var receipt=new Dictionary<string,string> {
                {"schemaVersion","1"},{"status","complete"},{"kind","pack"},{"mode",mode},
                {"settingsPreservation","prepared28"},
                {"clientVersion",feed.Version},{"serverId",transaction.Manifest.ServerId},{"packId",transaction.Manifest.PackId},
                {"packVersion",transaction.Manifest.PackVersion},{"catalogVersion",catalog.Version},
                {"catalogSha256",catalogHash},{"clientManifestSha256",TextHash(feed.ToJson())},
                {"packManifestSha256",TextHash(transaction.Manifest.ToJson())},{"updatedUtc",DateTime.UtcNow.ToString("o")}
            };
            receipt["authorization"]=ReceiptAuthorization(receipt,ApfTransactionSecurity.LoadKey(keyPath));
            string result=Path.Combine(root,"APF","LastInstallResult.json");ApfPaths.EnsureNoReparsePoints(root,result);
            AtomicText(result,ApfJson.Serialize(receipt));
        }
        private void InvalidateCompletedReceipt(string kind)
        {
            string result=Path.Combine(Root,"APF","LastInstallResult.json");ApfPaths.EnsureNoReparsePoints(Root,result);
            AtomicText(result,ApfJson.Serialize(new Dictionary<string,string>{{"status","staging"},{"kind",kind},{"updatedUtc",DateTime.UtcNow.ToString("o")}}));
        }
        internal static Dictionary<string,string> ReadCompletedReceipt(string root)
        {
            try {
                string path=Path.Combine(root,"APF","LastInstallResult.json");
                string keyPath=Path.Combine(root,"GameData","LunaMultiplayer","Data","APFHelper.key");
                ApfPaths.EnsureNoReparsePoints(root,path);ApfPaths.EnsureNoReparsePoints(root,keyPath);
                if(!File.Exists(path) || !File.Exists(keyPath) || new FileInfo(path).Length>65536)return null;
                var receipt=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(path));
                if(receipt==null || !ReceiptValue(receipt,"schemaVersion","1") || !ReceiptValue(receipt,"status","complete") ||
                    !ReceiptValue(receipt,"kind","pack") || !receipt.TryGetValue("authorization",out var authorization) ||
                    !string.Equals(authorization,ReceiptAuthorization(receipt,ApfTransactionSecurity.LoadKey(keyPath)),StringComparison.Ordinal))return null;
                return receipt;
            }catch(Exception error) when(ReceiptReadError(error)){return null;}
        }
        private static bool ReceiptReadError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
                error is FormatException || error is ArgumentException || error is CryptographicException ||
                error.GetType().FullName=="JsonFx.Json.JsonDeserializationException" ||
                error.GetType().FullName=="JsonFx.Serialization.DeserializationException";
        }
        private static bool ReceiptValue(Dictionary<string,string> values,string key,string expected)
        {
            return values.TryGetValue(key,out var actual) && string.Equals(actual,expected,StringComparison.Ordinal);
        }
        private static string ReceiptAuthorization(Dictionary<string,string> receipt,byte[] key)
        {
            var signed=receipt.Where(item=>item.Key!="authorization").OrderBy(item=>item.Key,StringComparer.Ordinal).ToDictionary(item=>item.Key,item=>item.Value,StringComparer.Ordinal);
            using(var hmac=new HMACSHA256(key))return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(ApfJson.Serialize(signed))));
        }
        private static string TextHash(string value){return Hash(Encoding.UTF8.GetBytes(value));}
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
            if(uri.Scheme!="https" || (!SameOrigin(uri,updateUrl) && !github.AllowsTransport(uri))) throw new InvalidDataException("Only the server and approved GitHub downloads are allowed.");
            ApfPaths.EnsureNoReparsePoints(Root,destination);
            if(expectedHash!=null && File.Exists(destination) && new FileInfo(destination).Length==maximum && string.Equals(ApfJson.Sha256File(destination),expectedHash,StringComparison.OrdinalIgnoreCase)){
                new ResumableTransfer{ValidatePath=path=>ApfPaths.EnsureNoReparsePoints(Root,path),Warning=Warn}.Cleanup(destination+".part",maximum,true);
                ReportBytes(maximum,maximum);return;
            }
            if(expectedHash!=null) { DownloadArchive(uri,destination,maximum,expectedHash);return; }
            string part=destination+".part";
            long offset=expectedHash!=null && File.Exists(part) ? new FileInfo(part).Length : 0;
            if(offset>=maximum) offset=0;
            var request=CreateArchiveRequest(uri);
            activeRequest=request;
            if(offset>0) request.AddRange(offset);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try { using(var lease=HttpRedirects.Open(request,target=>{var next=CreateArchiveRequest(target);activeRequest=next;return next;},finished=>{if(ReferenceEquals(activeRequest,finished))activeRequest=null;},CheckCanceled,github.ValidateRedirect))
            {
                var response=lease.Response;
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
            if(github.Enabled && githubReady) {
                Uri mirror;
                if(uri.AbsolutePath.StartsWith("/updates/",StringComparison.Ordinal)) {
                    string[] segments=uri.AbsolutePath.Split('/');
                    if(segments.Length!=4)throw new InvalidDataException("Invalid client archive location.");
                    mirror=github.Client(segments[2],Path.GetFileName(uri.AbsolutePath));
                }else mirror=github.Media(Path.GetFileName(uri.AbsolutePath));
                try{DownloadArchiveFrom(mirror,destination,size,hash);return;}
                catch(WebException error) when(GitHubSettings.CanFallBack(error)) {
                    CheckCanceled();githubReady=false;Warn("GitHub download interrupted; resuming from the pinned server.");
                }
            }
            DownloadArchiveFrom(uri,destination,size,hash);
        }
        private void DownloadArchiveFrom(Uri uri,string destination,long size,string hash)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string part=destination+".part";
            transfer=new ResumableTransfer {
                CreateRequest=CreateArchiveRequest,IsCanceled=IsCanceled,
                ValidatePath=path=>ApfPaths.EnsureNoReparsePoints(Root,path),
                Received=meter.Add,Progress=count=>ReportTransfer(count,size),Warning=Warn,ValidateRedirect=github.ValidateRedirect
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
                transfer.Cleanup(part,size,true);ReportBytes(size,size);
            } finally {meter.Pause();transfer=null;}
        }
        private void Warn(string message){Log("LastDownloadWarning.log",message);}
        private void Log(string name,string message)
        {
            // Fixed, bounded local diagnostics only; never upload automatically.
            try {
                string path=Path.Combine(Root,"APF",name);ApfPaths.EnsureNoReparsePoints(Root,path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if(message.Length>32768)message=message.Substring(0,32768);
                File.WriteAllText(path,DateTime.UtcNow.ToString("o")+Environment.NewLine+message);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        private HttpWebRequest CreateArchiveRequest(Uri uri)
        {
            if(!SameOrigin(uri,updateUrl) && !github.AllowsTransport(uri))throw new InvalidDataException("Unapproved download origin.");
            var request=(HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect=false;request.Timeout=30000;request.ReadWriteTimeout=30000;
            request.AutomaticDecompression=DecompressionMethods.None;
            request.CachePolicy=new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.ServicePoint.ConnectionLimit=ResumableTransfer.Connections;
            // KSP's Mono setter always throws NotImplementedException.
            // Keep the runtime's receive-window defaults.
            request.ServicePoint.UseNagleAlgorithm=false;
            request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>certificate!=null && (SameOrigin(uri,updateUrl)?pins.Contains(Hash(certificate.GetRawCertData())):github.AcceptsCertificate(uri,errors));
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
