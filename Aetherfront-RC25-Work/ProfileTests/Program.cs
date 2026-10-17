using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static string root,data;
    private static int checks;
    private static InstallCatalog catalog;
    private static ApfClientUpdateManifest feed;
    private const string ResumeRoot="./Aetherfront-RC25-Work/P3e9e2e69";
    private const string ResumeRequest="APF/.staging/6125b20fdf4646dca33aa029f64e9588/transaction.json";
    private const string OriginalRequestSha256="44e1030fcf8ce1ac5b87bf5661b1a3e54c945b35261933939e928e87ee289259";
    private const string OriginalPreparedSha256="b9d72a005700327ff8837b46345cd934773896c729260ca2025650eb8e00ebab";
    private const string OriginalCatalogSha256="dc96d70b45804d5266f1b1c5f8575c55e7d448f1996721c509efc4b3c5b18f97";
    private const string OriginalEngineSha256="9cab0e49923bb4a2a150baaae93d990c99f1b8689d7d35173836431f9233e547";
    private static bool resumedPreparedFresh;
    private static string resumeEvidence,resumeResult,resumeCandidateEngineSha256,resumeCandidateFeedSha256;
    private static FileStream resumeLease;
    private static void Check(bool value,string message){if(!value)throw new InvalidDataException(message);checks++;Console.WriteLine("PASS "+message);}
    private static void Write(string relative,string value){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string link,string existing,IntPtr reserved);
    private static int Main(string[] args)
    {
        try {
            using(var process=Process.GetCurrentProcess())process.PriorityClass=ProcessPriorityClass.BelowNormal;
            if(args.Length!=0) {
                if(args.Length!=1||args[0]!="--resume-prepared")throw new ArgumentException("Use --resume-prepared without a root argument; only the explicitly approved P3e9e2e69 fixture is supported.");
                ResumePreparedFresh();
            } else {
            // Match ordinary desktop game-folder lengths, not a GUID-heavy
            // test path which itself exceeds Windows mod-path limits.
            root=Path.GetFullPath("Aetherfront-RC25-Work/P"+Guid.NewGuid().ToString("N").Substring(0,8));
            if(Directory.Exists(root))throw new IOException("Owned profile fixture already exists.");
            data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(data);
            string candidate=Path.GetFullPath("Aetherfront-RC25-Work/Candidate/Client");
            foreach(string source in Directory.GetFiles(candidate,"*",SearchOption.AllDirectories)) {
                string target=Path.Combine(root,source.Substring(candidate.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(source,target);
            }
            File.Copy("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe",Path.Combine(root,"KSP_x64.exe"));
            Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
            Write("GameData/LunaMultiplayer/Data/APFServer.txt","217.216.49.250:18865");
            Write("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt","34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09");
            Write("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml",File.ReadAllText("KESAS-VPS-Server/secrets/update-signing-public.xml"));
            Write("APF/.client-update-staging/connect/feed.json",File.ReadAllText("Aetherfront-RC25-Work/Candidate/Published/APFClientUpdate.json"));
            Write("saves/Tester/persistent.sfs","save sentinel");Write("GameData/LunaMultiplayer/Data/privatekey.txt","identity sentinel");
            Write("settings.cfg","ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = retained\n");
            catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(root,"GameData/LunaMultiplayer/Launcher/install-catalog.json")));
            feed=ApfClientUpdateManifest.FromJson(File.ReadAllText(Path.Combine(root,"APF/.client-update-staging/connect/feed.json")));
            Apply("potato");
            }
            Check(Directory.Exists(Path.Combine(root,"GameData/KcalbelohTextures")),"Fresh Potato includes official 4K texture package");
            Write("GameData/SCANsat/PluginData/Settings.cfg","runtime settings changed after installation");
            Write("GameData/000_AT_Utils/000_AT_Utils.user","runtime generated settings");
            Write("GameData/000_AT_Utils/Plugins/PluginData/000_AT_Utils/000_AT_Utils.glob","runtime global settings");
            Write("GameData/KcalbelohSystem/Cache/Ahtpan.bin","regenerated planet cache");
            Write("GameData/OPM/Cache/Nissee.bin","regenerated planet cache");
            Write("GameData/Kopernicus/Cache/mesh.hash","generated mesh hash");
            Write("GameData/Kopernicus/Cache/Kerbin.bin","generated default scaled-space cache");
            Write("GameData/PersonalAddon/remove.txt","approved removal sentinel");
            Apply("full");
            Check(!Directory.Exists(Path.Combine(root,"GameData/PersonalAddon")),"Approved unrelated mod removed in the same commit");
            Apply("potato");
            Check(File.ReadAllText(Path.Combine(root,"saves/Tester/persistent.sfs"))=="save sentinel"&&File.ReadAllText(Path.Combine(data,"privatekey.txt"))=="identity sentinel","Both profile switches preserve saves and identity");
            Check(File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("KEY_BINDING = retained"),"Both profile switches preserve controls");
            Write(resumedPreparedFresh?resumeResult:"result.json",ApfJson.Serialize(new{passed=true,checks,unityGameplayTested=false,headlessOnly=true,resumedPreparedFresh,freshPreparationRerun=!resumedPreparedFresh,originalPreparedRequest=resumedPreparedFresh?ResumeRequest:null,originalRequestSha256=resumedPreparedFresh?OriginalRequestSha256:null,originalPreparedSha256=resumedPreparedFresh?OriginalPreparedSha256:null,originalCatalogSha256=resumedPreparedFresh?OriginalCatalogSha256:null,freshPreparationEngineSha256=resumedPreparedFresh?OriginalEngineSha256:null,nativeCommitEngineSha256=resumeCandidateEngineSha256,candidateFeedSha256=resumeCandidateFeedSha256,resumeEvidence,preparationNote=resumedPreparedFresh?"Fresh 177-package Potato preparation was reused from the original authenticated request; this run did not repeat that extraction.":"Fresh preparation and the complete profile chain ran in this invocation.",completedUtc=DateTime.UtcNow.ToString("o")}));
            Console.WriteLine("PASS full profile chain fixture="+root+" checks="+checks);
            Console.WriteLine("RESULT "+Path.Combine(root,resumedPreparedFresh?resumeResult:"result.json"));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Retained fixture="+root);return 1;}
        finally{resumeLease?.Dispose();}
    }
    private static void RequireHash(string path,string expected,string message)
    {
        if(!File.Exists(path)||!string.Equals(ApfJson.Sha256File(path),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(message);
    }
    private static bool HasAcknowledgedRequest(string stage)
    {
        foreach(string entry in Directory.GetFileSystemEntries(stage)) {
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Resume staging must not contain links.");
            if((attributes&FileAttributes.Directory)!=0){if(HasAcknowledgedRequest(entry))return true;}
            else if(entry.EndsWith(".ready",StringComparison.OrdinalIgnoreCase)||entry.EndsWith(".consumed",StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }
    private static void RequireUncommittedResume()
    {
        if(!string.Equals(Path.GetFullPath(root),Path.GetFullPath(ResumeRoot),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Resume target is not the exact approved fixture.");
        foreach(string relative in new[]{"GameData/LunaMultiplayer/Data/APFPackState.json","GameData/LunaMultiplayer/Data/KesasInstallProfile.json","APF/APFTransaction.log"}) {
            string path=Path.Combine(root,relative);ApfPaths.EnsureNoReparsePoints(root,path);
            if(File.Exists(path)||Directory.Exists(path))throw new InvalidDataException("Resume refused after installation state or transaction evidence exists: "+relative);
        }
        foreach(string relative in new[]{"APF/.staging","APF/.client-update-staging"}) {
            string stage=Path.Combine(root,relative);ApfPaths.EnsureNoReparsePoints(root,stage);
            if(Directory.Exists(stage)&&HasAcknowledgedRequest(stage))throw new InvalidDataException("Resume refused: a helper request was already acknowledged or consumed.");
        }
        if(Directory.Exists(Path.Combine(root,"APF/.transaction-backup")))throw new InvalidDataException("Resume refused: recovery history already exists.");
    }
    private static void RequireResumeSentinels()
    {
        foreach(var sentinel in new Dictionary<string,string>{
            {"saves/Tester/persistent.sfs","save sentinel"},
            {"GameData/LunaMultiplayer/Data/privatekey.txt","identity sentinel"},
            {"settings.cfg","ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = retained\n"},
            {"GameData/LunaMultiplayer/Data/APFServer.txt","217.216.49.250:18865"},
            {"GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt","34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09"}}) {
            string path=Path.Combine(root,sentinel.Key);ApfPaths.EnsureNoReparsePoints(root,path);
            if(!File.Exists(path)||File.ReadAllText(path)!=sentinel.Value)throw new InvalidDataException("Approved fixture sentinel differs: "+sentinel.Key);
        }
        RequireHash(Path.Combine(root,"KSP_x64.exe"),"dd4826db1753b1dcea52873117dae5baa6e84e25d304491c50abb747df68e2fd","Resume requires the original owned GameStub, not a real game executable.");
        string[] entries=Directory.GetFileSystemEntries(Path.Combine(root,"GameData"));
        if(!new HashSet<string>(entries.Select(Path.GetFileName),StringComparer.OrdinalIgnoreCase).SetEquals(new[]{"Squad","000_Harmony","LunaMultiplayer"}))throw new InvalidDataException("GameData no longer has exactly the original bootstrap roots.");
        foreach(string entry in entries) {
            ApfPaths.EnsureNoReparsePoints(root,entry);
        }
    }
    private static void RequireFixtureNotRunning()
    {
        foreach(var process in Process.GetProcessesByName("KSP_x64").Concat(Process.GetProcessesByName("KSP")))using(process) {
            if(process.HasExited)continue;
            if(ApfPaths.IsUnder(process.MainModule.FileName,root))throw new IOException("Close the owned fixture GameStub before refreshing bootstrap files.");
        }
        foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if(!assembly.IsDynamic&&!string.IsNullOrEmpty(assembly.Location)&&ApfPaths.IsUnder(assembly.Location,root))throw new IOException("Resume cannot refresh a bootstrap assembly loaded from the fixture itself.");
    }
    private static ApfClientUpdateManifest ReadSignedFeed(string path)
    {
        if(new FileInfo(path).Length>4L*1024*1024)throw new InvalidDataException("Candidate client feed exceeds its size limit.");
        var manifest=ApfClientUpdateManifest.FromJson(File.ReadAllText(path));
        if(!ApfClientUpdateValidator.Validate(manifest).IsValid||manifest.Version!="kesas-rc25")throw new InvalidDataException("Resume requires the signed RC25 candidate.");
        string publicKey=Path.Combine(data,"APFClientUpdatePublicKey.xml");ApfPaths.EnsureNoReparsePoints(root,publicKey);
        using(var signing=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))
            if(!ApfClientUpdateSecurity.Verify(manifest,signing))throw new InvalidDataException("Candidate client feed signature is invalid.");
        return manifest;
    }
    private static void RefreshResumeBootstrap(string request,PreparedEnvelope envelope)
    {
        string work=Directory.GetParent(Path.GetFullPath(ResumeRoot)).FullName;
        string candidate=Path.Combine(work,"Candidate","Client");
        string candidateFeed=Path.Combine(work,"Candidate","Published","APFClientUpdate.json");
        ApfPaths.EnsureNoReparsePoints(work,candidate);ApfPaths.EnsureNoReparsePoints(work,candidateFeed);
        var nextFeed=ReadSignedFeed(candidateFeed);string candidateFeedHash=ApfJson.Sha256File(candidateFeed);
        var catalogFile=nextFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Launcher/install-catalog.json");
        if(!string.Equals(catalogFile.Sha256,OriginalCatalogSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The rebuilt candidate changed the original pack catalog; prepared data cannot be reused.");
        var engineFile=nextFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Plugins/KesasInstall.dll");
        if(string.Equals(engineFile.Sha256,OriginalEngineSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Rebuild the candidate after the NativeOpen optimization before resuming; the old 9cab engine is not accepted.");
        RequireHash(typeof(PreparedPack).Assembly.Location,engineFile.Sha256,"Rebuild ProfileTests against the same optimized engine as the signed candidate.");
        RequireHash(typeof(ApfJson).Assembly.Location,nextFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Plugins/ApfCore.dll").Sha256,"ProfileTests core differs from the rebuilt signed candidate.");
        var copies=new List<KeyValuePair<string,string>>();
        foreach(var file in nextFeed.Files) {
            string relative=file.Path.Replace('\\','/');
            if(!ApfPaths.IsSafeRelative(relative)||!(relative.StartsWith("GameData/LunaMultiplayer/",StringComparison.OrdinalIgnoreCase)||relative.StartsWith("GameData/000_Harmony/",StringComparison.OrdinalIgnoreCase)||relative.StartsWith("KSP_x64_Data/Managed/",StringComparison.OrdinalIgnoreCase)||relative.StartsWith("KESAS/",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Candidate file is not a bootstrap overlay path: "+relative);
            if(relative.StartsWith("GameData/LunaMultiplayer/Data/",StringComparison.OrdinalIgnoreCase)&&!string.Equals(relative,"GameData/LunaMultiplayer/Data/APFClient.json",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Candidate must not replace fixture trust, identity, helper authorization or state.");
            string source=Path.GetFullPath(Path.Combine(candidate,relative.Replace('/',Path.DirectorySeparatorChar)));
            string target=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!ApfPaths.IsUnder(source,candidate)||!ApfPaths.IsUnder(target,root))throw new InvalidDataException("Bootstrap overlay escapes its owned roots.");
            ApfPaths.EnsureNoReparsePoints(candidate,source);ApfPaths.EnsureNoReparsePoints(root,target);
            if(!File.Exists(source)||new FileInfo(source).Length!=file.SizeBytes)throw new InvalidDataException("Candidate bootstrap file is missing or has changed size: "+relative);
            RequireHash(source,file.Sha256,"Candidate bootstrap hash differs from signed inventory: "+relative);
            if(Directory.Exists(target))throw new InvalidDataException("Bootstrap file target is a directory.");
            copies.Add(new KeyValuePair<string,string>(source,target));
        }
        string cachedFeed=Path.Combine(root,"APF/.client-update-staging/connect/feed.json");ApfPaths.EnsureNoReparsePoints(root,cachedFeed);
        var oldFeed=ReadSignedFeed(cachedFeed);
        if(!string.Equals(oldFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Launcher/install-catalog.json").Sha256,OriginalCatalogSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Original cached feed does not authorize the original catalog.");
        string token=Guid.NewGuid().ToString("N");resumeEvidence="APF/profile-resume-evidence/"+token;resumeResult="result-resumed-prepared-"+token+".json";
        string evidence=Path.Combine(root,resumeEvidence);ApfPaths.EnsureNoReparsePoints(root,evidence);Directory.CreateDirectory(evidence);
        foreach(string path in new[]{request,request+".prepared",cachedFeed,Path.Combine(root,"APF/LastInstallResult.json"),Path.Combine(root,"result.json")})
            if(File.Exists(path))File.Copy(path,Path.Combine(evidence,Path.GetFileName(path)+".before"));
        File.Copy(Path.Combine(root,"GameData/LunaMultiplayer/Launcher/install-catalog.json"),Path.Combine(evidence,"install-catalog.json.before"));
        File.Copy(candidateFeed,Path.Combine(evidence,"candidate-feed.json"));
        string payloadSha256;using(var hash=SHA256.Create())payloadSha256=ApfJson.ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(envelope.Payload)));
        Installer.AtomicText(Path.Combine(evidence,"resume-inputs.json"),ApfJson.Serialize(new{root,request,OriginalRequestSha256,OriginalPreparedSha256,OriginalCatalogSha256,OriginalEngineSha256,candidateFeedHash,candidateEngineSha256=engineFile.Sha256,preparedPayloadSha256=payloadSha256,note="Original preparation and stopped preflight evidence retained. No new fresh 177-package extraction is claimed.",createdUtc=DateTime.UtcNow.ToString("o")}));
        RequireFixtureNotRunning();
        foreach(var copy in copies) {
            Directory.CreateDirectory(Path.GetDirectoryName(copy.Value));
            if(File.Exists(copy.Value)&&string.Equals(ApfJson.Sha256File(copy.Key),ApfJson.Sha256File(copy.Value),StringComparison.OrdinalIgnoreCase))continue;
            File.Copy(copy.Key,copy.Value,true);
        }
        RequireHash(candidateFeed,candidateFeedHash,"Candidate feed changed during bootstrap refresh.");
        Installer.AtomicText(cachedFeed,File.ReadAllText(candidateFeed));
        resumeCandidateEngineSha256=engineFile.Sha256;resumeCandidateFeedSha256=candidateFeedHash;feed=nextFeed;
    }
    private static void ResumePreparedFresh()
    {
        root=Path.GetFullPath(ResumeRoot);data=Path.Combine(root,"GameData/LunaMultiplayer/Data");
        ApfPaths.EnsureNoReparsePoints(root,root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("The exact approved prepared fixture is missing.");
        string lease=Path.Combine(root,"APF/.profile-resume.lock");ApfPaths.EnsureNoReparsePoints(root,lease);
        resumeLease=new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        RequireUncommittedResume();RequireResumeSentinels();
        string request=Path.Combine(root,ResumeRequest);
        ApfPaths.EnsureNoReparsePoints(root,request);ApfPaths.EnsureNoReparsePoints(root,request+".prepared");
        if(File.Exists(request+".client")||File.Exists(request+".duplicate"))throw new InvalidDataException("The original fresh preparation must not acquire additional operation sidecars.");
        RequireHash(request,OriginalRequestSha256,"Resume request is not the exact originally staged transaction.");
        RequireHash(request+".prepared",OriginalPreparedSha256,"Original prepared receipt changed.");
        string catalogPath=Path.Combine(root,"GameData/LunaMultiplayer/Launcher/install-catalog.json");ApfPaths.EnsureNoReparsePoints(root,catalogPath);
        RequireHash(catalogPath,OriginalCatalogSha256,"Original catalog changed; fresh staged packages cannot be reused.");
        catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath));
        var transaction=ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(request));
        string keyPath=Path.Combine(data,"APFHelper.key");ApfPaths.EnsureNoReparsePoints(root,keyPath);
        byte[] key=ApfTransactionSecurity.LoadKey(keyPath);
        if(transaction==null||!ApfTransactionSecurity.Verify(transaction,key)||!string.Equals(Path.GetFullPath(transaction.GameRoot),root,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFullPath(transaction.GameDataPath),Path.Combine(root,"GameData"),StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFullPath(transaction.StatePath),Path.GetFullPath(Path.Combine(data,"APFPackState.json")),StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFullPath(transaction.KspExecutable),Path.Combine(root,"KSP_x64.exe"),StringComparison.OrdinalIgnoreCase)||transaction.ProcessId!=0||transaction.KspArguments.Count!=0||transaction.Deletes.Count!=0||transaction.Packages.Count!=177||(transaction.ExistingState?.Packages?.Count??0)!=0)throw new InvalidDataException("Original request is not an untouched fresh 177-package transaction for this exact fixture.");
        if(catalog==null||catalog.Full==null||catalog.Potato==null||catalog.Version!="kesas-rc25"||catalog.Potato.ManagedPackages.Count!=177||catalog.Full.ManagedPackages.Count!=211||transaction.Manifest.ToJson()!=catalog.Potato.ToJson())throw new InvalidDataException("Original request does not match the original Potato catalog.");
        var envelope=ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(request+".prepared"));
        if(envelope==null||envelope.Payload==null||envelope.Mac==null)throw new InvalidDataException("Original prepared envelope is incomplete.");
        string expected;
        using(var hmac=new HMACSHA256(key))expected=Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(envelope.Payload)));
        if(!string.Equals(envelope.Mac,expected,StringComparison.Ordinal))throw new InvalidDataException("Original prepared receipt HMAC is invalid.");
        var prepared=ApfJson.Deserialize<PreparedReceipt>(envelope.Payload);
        if(prepared==null||prepared.Authorization!=transaction.Authorization||prepared.Packages.Count!=177||prepared.Packages.Sum(p=>p.Files.Count)!=28675||prepared.DataDirectory!="APF/p/5b32bf3935c9/GameData"||!Regex.IsMatch(prepared.DataDirectory,"^APF/p/[0-9a-f]{12}/GameData$"))throw new InvalidDataException("Original prepared files do not match this approved fixture.");
        string preparedRoot=Path.Combine(root,prepared.DataDirectory.Replace('/',Path.DirectorySeparatorChar));ApfPaths.EnsureNoReparsePoints(root,preparedRoot);
        if(!Directory.Exists(preparedRoot))throw new DirectoryNotFoundException("The original prepared payload directory is missing.");
        RequireFixtureNotRunning();
        RefreshResumeBootstrap(request,envelope);
        RequireUncommittedResume();RequireResumeSentinels();
        RequireHash(request,OriginalRequestSha256,"Bootstrap refresh changed the original request.");RequireHash(request+".prepared",OriginalPreparedSha256,"Bootstrap refresh changed the original preparation.");RequireHash(catalogPath,OriginalCatalogSha256,"Bootstrap refresh changed the original catalog.");
        resumedPreparedFresh=true;
        Console.WriteLine("RESUME prepared fresh Potato packages=177 files=28675 request="+request+"; no fresh extraction rerun; evidence="+Path.Combine(root,resumeEvidence));
        using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){}
        VerifyCompletedMode("potato",transaction.Manifest);
    }
    private static void Apply(string mode)
    {
        var manifest=ApfPackManifest.FromJson((mode=="potato"?catalog.Potato:catalog.Full).ToJson());
        string statePath=Path.Combine(data,"APFPackState.json");
        var state=File.Exists(statePath)?ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath)):new ApfPackState();
        var old=(state.Packages??new List<ApfPackageState>()).ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
        var transaction=new ApfSyncTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),KspExecutable=Path.Combine(root,"KSP_x64.exe"),ProcessId=0,KspArguments=new List<string>(),StatePath=statePath,Manifest=manifest,ExistingState=state};
        string stage=Path.Combine(root,"APF/.staging",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        foreach(var package in manifest.ManagedPackages) {
            if(old.TryGetValue(package.Id,out var installed)&&installed.Version==package.Version&&installed.ArchiveSha256==package.Sha256)continue;
            string name=Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath);
            string source=Path.GetFullPath("Aetherfront-RC25-Work/ModFixes/Staged-RC25/Media/"+name);
            if(!File.Exists(source))source=Path.GetFullPath("KESAS-VPS-Server/Server/Config/"+name);
            if(!File.Exists(source))throw new FileNotFoundException("Required immutable archive missing",name);
            string archive=Path.Combine(stage,name);
            if(!CreateHardLink(archive,source,IntPtr.Zero))File.Copy(source,archive);
            transaction.Packages.Add(new ApfStagedPackage{Package=package,ArchivePath=archive});
        }
        var desired=new HashSet<string>(manifest.ManagedPackages.Select(p=>ApfPaths.PackageRoot(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
        var allowed=new HashSet<string>((manifest.ProtectedRoots??new List<string>()).Select(p=>ApfPaths.NormalizeGameDataRelative(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
        foreach(var rule in manifest.ClientOnly)foreach(string value in rule.PathRoots)allowed.Add(ApfPaths.NormalizeGameDataRelative(value).Split('/')[0]);
        foreach(string entry in Directory.GetFileSystemEntries(transaction.GameDataPath)) {
            string name=Path.GetFileName(entry);
            if(!desired.Contains(name)&&!allowed.Contains(name)&&!ApfPaths.IsBuiltInProtectedTopLevel(name.ToLowerInvariant()))transaction.Deletes.Add(new ApfDeleteChange{RelativePath="GameData/"+name,Reason="Explicitly approved isolated profile-test removal"});
        }
        ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data,"APFHelper.key")));
        string request=Path.Combine(stage,"transaction.json");File.WriteAllText(request,ApfJson.Serialize(transaction));
        Console.WriteLine("PROFILE "+mode+" packages="+manifest.ManagedPackages.Count+" replacements="+transaction.Packages.Count+" deletes="+transaction.Deletes.Count);
        PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){}
        VerifyCompletedMode(mode,manifest);
    }
    private static void VerifyCompletedMode(string mode,ApfPackManifest manifest)
    {
        var result=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json")));
        Check(result["status"]=="complete"&&result["mode"]==mode&&result["catalogVersion"]=="kesas-rc25",mode+" completed authenticated receipt committed");
        var installer=new Installer(root,new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865),"kesas-rc25");
        typeof(Installer).GetField("verifiedClientManifest",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(installer,feed);
        bool fast=(bool)typeof(Installer).GetMethod("CompletedPackMatches",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(installer,new object[]{mode,catalog,manifest});
        Check(fast,mode+" current revision/profile reconnect uses completed receipt");
        string opposite=mode=="potato"?"full":"potato";
        Check(!(bool)typeof(Installer).GetMethod("CompletedPackMatches",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(installer,new object[]{opposite,catalog,opposite=="potato"?catalog.Potato:catalog.Full}),mode+" opposite profile cannot fast-path");
    }
}
