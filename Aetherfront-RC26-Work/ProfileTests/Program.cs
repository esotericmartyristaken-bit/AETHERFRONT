using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private const string Engine26="be8a27a5f9a568fa938d98fa4ba4ad9b5e6f1f5a56772e58510a6773828741b0";
    private const string Core26="0a4b6bd7c25ee4e18aba88ec8d41cb66b8c0034da3ba02d2d090ddb798bdc6c0";
    private const string Engine25="d319bdbe38288f57bfec5e4ddc0eba4f8086e4ebab35c773868498658f621919";
    private const string SourceState25="dd9c1c7290537840f08722e6115eb60b8af35c26effa3f429e423ac4c971473f";
    private const string Data="GameData/LunaMultiplayer/Data/";
    private static readonly string Workspace=Path.GetFullPath(".");
    private static readonly string OwnedBase=Path.Combine(Workspace,"Aetherfront-RC26-Work","ProfileTests");
    private static readonly string Baseline=Path.Combine(Workspace,"Aetherfront-RC25-Work","P3e9e2e69");
    private static readonly string Client25=Path.Combine(Workspace,"Aetherfront-RC25-Work","Candidate","Client");
    private static readonly string[] RepairRoots={"ContractConfigurator","KerbalEngineer","KSPCommunityFixes","ZZZ_KESASPerformanceFixes"};
    private static readonly string[] AssetIds={"kesas-modularlaunchpads-8feabdb7","kesas-umbraspaceindustries-2ed9115b","kesas-tundraspacecenter-7018b381","kesas-interstellartechnologies-173d8c64","kesas-rc25-modfixes"};
    private static readonly string[] RuntimePaths={
        "ContractConfigurator/log/APC/FirstPlanetFlyby.log","KerbalEngineer/KerbalEngineer.log",
        "KerbalEngineer/Settings/GeneralSettings.xml","KSPCommunityFixes/PluginData/PNGTextureCache.cfg",
        "KSPCommunityFixes/PluginData/TextureCache/1225581161","KSPCommunityFixes/PluginData/TextureCache/textureData.json",
        "KSPCommunityFixes/PluginData/TextureCache/progressMarker",
        "ZZZ_KESASPerformanceFixes/PluginData/AssetBundleDefinitions.v1.cache",
        "ZZZ_KESASPerformanceFixes/PluginData/AssetBundleDefinitions.v1.cache.tmp"};
    private static string root,candidate;private static int checks,linked,copied;private static long linkedBytes;
    private static InstallCatalog target,sourceCatalog;private static ApfClientUpdateManifest feed26;
    private static AssetPlan assets;private static AssetTest assetTest;
    private static string assetPlanPath,assetPlanSha256,assetTestPath,assetTestSha256;
    private static readonly Dictionary<string,AssetMedia> media=new Dictionary<string,AssetMedia>(StringComparer.Ordinal);
    private static readonly HashSet<string> migrationIds=new HashSet<string>(StringComparer.Ordinal);
    public sealed class AssetPlan
    {
        public int SchemaVersion{get;set;}public string Version{get;set;}public string SourceCatalogSha256{get;set;}
        public bool Signed{get;set;}public bool Published{get;set;}public bool Installed{get;set;}public bool RequiresReleaseOwnerSignature{get;set;}
        public List<AssetMedia> Media{get;set;}public List<object> Bindings{get;set;}
    }
    public sealed class AssetMedia
    {
        public string Filename{get;set;}public ApfPackage Declaration{get;set;}public ApfPackage OldDeclaration{get;set;}
        public string OldSha256{get;set;}public int SourceEntryCount{get;set;}public List<string> Profiles{get;set;}public List<AssetEntry> AddedEntries{get;set;}
    }
    public sealed class AssetEntry
    {
        public string Kind{get;set;}public string SourceEntry{get;set;}public string AddedEntry{get;set;}
        public string Sha256{get;set;}public long SizeBytes{get;set;}
    }
    public sealed class AssetTest
    {
        public int SchemaVersion{get;set;}public string Version{get;set;}public bool Passed{get;set;}
        public string PlanSha256{get;set;}public string SourceCatalogSha256{get;set;}
        public int PackagesVerified{get;set;}public int AliasesVerified{get;set;}public int PreciseBindingsVerified{get;set;}
        public int OriginalEntriesVerified{get;set;}public int NegativeTestsPassed{get;set;}
        public bool OriginalZipPrefixesPreserved{get;set;}public bool OriginalCentralDirectoryRecordsPreserved{get;set;}
        public bool BothProfilesUseIdenticalSharedVariants{get;set;}public bool UntouchedPackagesPreserved{get;set;}
        public List<AssetTestPackage> Packages{get;set;}
    }
    public sealed class AssetTestPackage
    {
        public string Id{get;set;}public string OldSha256{get;set;}public string NewSha256{get;set;}public string Filename{get;set;}
        public long SizeBytes{get;set;}public int OriginalEntriesVerified{get;set;}public int AddedEntriesVerified{get;set;}
        public bool AllOriginalBytesAndMetadataUnchanged{get;set;}public List<string> Profiles{get;set;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string link,string existing,IntPtr reserved);
    private static void Check(bool value,string message){if(!value)throw new InvalidDataException(message);checks++;Console.WriteLine(DateTime.UtcNow.ToString("o")+" PASS "+message);}
    private static string Owned(string relative)
    {
        string result=Path.GetFullPath(Path.Combine(root,relative));
        if(!ApfPaths.IsUnder(result,root))throw new InvalidDataException("Test output escapes its owned fixture.");
        ApfPaths.EnsureNoReparsePoints(root,result);return result;
    }
    // Unlink before any test write, even when the existing inode is shared.
    private static string WritePrivate(string relative,string text)
    {
        string path=Owned(relative);Directory.CreateDirectory(Path.GetDirectoryName(path));
        if(File.Exists(path))File.Delete(path);File.WriteAllText(path,text);return path;
    }
    private static void CopyPrivate(string source,string relative)
    {
        string path=Owned(relative);Directory.CreateDirectory(Path.GetDirectoryName(path));
        if(File.Exists(path))File.Delete(path);File.Copy(source,path);copied++;
    }
    private static void HashIs(string path,string expected,string message)
    {if(!File.Exists(path)||!string.Equals(ApfJson.Sha256File(path),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(message);}
    private static IEnumerable<string> PlainFiles(string directory)
    {
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Fixture input has a linked directory.");
        foreach(string file in Directory.GetFiles(directory)){
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Fixture input has a linked file.");yield return file;
        }
        foreach(string child in Directory.GetDirectories(directory))foreach(string file in PlainFiles(child))yield return file;
    }
    private static void RequireOwnedRoot(string value)
    {
        root=Path.GetFullPath(value);
        if(!string.Equals(Path.GetDirectoryName(root),OwnedBase,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Path.GetFileName(root),"^P[0-9a-f]{8}$"))throw new InvalidDataException("Only new, short ProfileTests/Pxxxxxxxx fixtures are accepted.");
        ApfPaths.EnsureNoReparsePoints(OwnedBase,root);
        if(File.ReadAllText(Owned("owned-fixture.txt"))!=root)throw new InvalidDataException("Fixture ownership sentinel mismatch.");
    }
    private static ApfPackState State(){return ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(Data+"APFPackState.json")));}
    private static ApfPackManifest Mode(string mode){return mode=="potato"?target.Potato:target.Full;}
    private static bool Repair(ApfPackage package){return RepairRoots.Contains(ApfPaths.PackageRoot(package),StringComparer.OrdinalIgnoreCase);}
    private static ApfClientUpdateManifest SignedFeed(string file,string publicKey,string version)
    {
        var feed=ApfClientUpdateManifest.FromJson(File.ReadAllText(file));
        if(feed.Version!=version||!ApfClientUpdateValidator.Validate(feed).IsValid)throw new InvalidDataException("Unexpected signed client revision.");
        using(var key=ApfClientUpdateSecurity.LoadXmlPublicKey(File.ReadAllText(publicKey)))
            if(!ApfClientUpdateSecurity.Verify(feed,key))throw new InvalidDataException("Client manifest signature is invalid.");
        return feed;
    }
    private static bool BothProfiles(IEnumerable<string> profiles)
    {return profiles!=null&&profiles.Count()==2&&new HashSet<string>(profiles,StringComparer.Ordinal).SetEquals(new[]{"full","potato"});}
    private static void ReadAssets(string planPath,string planHash,string testPath,string testHash,string baselineCatalogHash)
    {
        assetPlanPath=Path.GetFullPath(planPath);assetTestPath=Path.GetFullPath(testPath);assetPlanSha256=planHash;assetTestSha256=testHash;
        string assetRoot=Path.Combine(Workspace,"Aetherfront-RC26-Work","Assets");
        foreach(string path in new[]{assetPlanPath,assetTestPath}){
            if(!ApfPaths.IsUnder(path,assetRoot))throw new InvalidDataException("Asset evidence is outside the owned RC26 Assets directory.");
            ApfPaths.EnsureNoReparsePoints(assetRoot,path);
        }
        HashIs(assetPlanPath,planHash,"Asset plan differs from its explicitly reviewed hash.");HashIs(assetTestPath,testHash,"TestAssets evidence differs from its explicitly reviewed hash.");
        assets=ApfJson.Deserialize<AssetPlan>(File.ReadAllText(assetPlanPath));assetTest=ApfJson.Deserialize<AssetTest>(File.ReadAllText(assetTestPath));
        if(assets==null||assetTest==null||assets.SchemaVersion!=1||assets.Version!="kesas-rc26"||assets.Signed||assets.Published||assets.Installed||!assets.RequiresReleaseOwnerSignature||assets.Media==null||assets.Media.Count!=5||assets.Bindings==null||assets.Bindings.Count!=8||assets.SourceCatalogSha256!=baselineCatalogHash)throw new InvalidDataException("Asset plan is not the reviewed five-package additive RC26 plan.");
        if(assetTest.SchemaVersion!=1||assetTest.Version!="kesas-rc26"||!assetTest.Passed||assetTest.PlanSha256!=planHash||assetTest.SourceCatalogSha256!=baselineCatalogHash||assetTest.PackagesVerified!=5||assetTest.AliasesVerified!=7||assetTest.PreciseBindingsVerified!=8||assetTest.OriginalEntriesVerified<=0||assetTest.NegativeTestsPassed<6||!assetTest.OriginalZipPrefixesPreserved||!assetTest.OriginalCentralDirectoryRecordsPreserved||!assetTest.BothProfilesUseIdenticalSharedVariants||!assetTest.UntouchedPackagesPreserved||assetTest.Packages==null||assetTest.Packages.Count!=5)throw new InvalidDataException("Complete, hash-bound passing TestAssets preservation evidence is required.");
        media.Clear();int aliases=0,bindings=0;
        foreach(var item in assets.Media){
            var package=item.Declaration;
            if(package==null||!AssetIds.Contains(package.Id,StringComparer.Ordinal)||media.ContainsKey(package.Id)||!BothProfiles(item.Profiles)||item.AddedEntries==null||item.AddedEntries.Count==0)throw new InvalidDataException("Unexpected, duplicate or unbound media declaration.");
            var old=sourceCatalog.Potato.ManagedPackages.Single(p=>p.Id==package.Id);
            if(ApfJson.Serialize(old)!=ApfJson.Serialize(item.OldDeclaration)||old.Sha256!=item.OldSha256||ApfJson.Serialize(old)!=ApfJson.Serialize(sourceCatalog.Full.ManagedPackages.Single(p=>p.Id==package.Id))||old.Sha256==package.Sha256)throw new InvalidDataException("Asset old declaration differs from the signed RC25 catalogs.");
            var expected=ApfJson.Deserialize<ApfPackage>(ApfJson.Serialize(old));expected.Version=package.Version;expected.ArchiveUrl=package.ArchiveUrl;expected.Sha256=package.Sha256;expected.SizeBytes=package.SizeBytes;
            if(ApfJson.Serialize(expected)!=ApfJson.Serialize(package))throw new InvalidDataException("Asset changed package identity, destination, prefix or policy.");
            var uri=new Uri(package.ArchiveUrl);var origin=new Uri(old.ArchiveUrl);
            string filename=Path.GetFileName(uri.AbsolutePath);
            if(uri.Scheme!="https"||uri.GetLeftPart(UriPartial.Authority)!=origin.GetLeftPart(UriPartial.Authority)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment)||!string.IsNullOrEmpty(uri.UserInfo)||filename!=item.Filename||!Regex.IsMatch(filename,"^kesas-rc26-[a-z0-9-]+-"+Regex.Escape(package.Sha256)+"\\.zip$")||!Regex.IsMatch(package.Sha256,"^[0-9a-f]{64}$")||package.SizeBytes<=0)throw new InvalidDataException("Asset URL/hash/size is not the exact reviewed content-addressed tuple.");
            string archive=Path.Combine(Path.GetDirectoryName(assetPlanPath),"Media",filename);ApfPaths.EnsureNoReparsePoints(assetRoot,archive);
            HashIs(archive,package.Sha256,"Reviewed asset archive bytes changed.");if(new FileInfo(archive).Length!=package.SizeBytes)throw new InvalidDataException("Reviewed asset archive size changed.");
            var proof=assetTest.Packages.Single(p=>p.Id==package.Id);
            if(proof.OldSha256!=old.Sha256||proof.NewSha256!=package.Sha256||proof.Filename!=filename||proof.SizeBytes!=package.SizeBytes||!proof.AllOriginalBytesAndMetadataUnchanged||proof.OriginalEntriesVerified<=0||proof.OriginalEntriesVerified!=item.SourceEntryCount||proof.AddedEntriesVerified!=item.AddedEntries.Count||!BothProfiles(proof.Profiles))throw new InvalidDataException("Per-package TestAssets preservation evidence does not bind this archive.");
            var added=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string rootPrefix=ApfPaths.PackageRoot(package)+"/";
            foreach(var entry in item.AddedEntries){
                if(!ApfPaths.IsSafeRelative(entry.AddedEntry)||!entry.AddedEntry.StartsWith(rootPrefix,StringComparison.Ordinal)||!added.Add(entry.AddedEntry)||!Regex.IsMatch(entry.Sha256,"^[0-9a-f]{64}$")||entry.SizeBytes<=0)throw new InvalidDataException("Unsafe or duplicate reviewed asset addition.");
                if(package.Id=="kesas-rc25-modfixes"){
                    if(entry.Kind!="owned-config"||entry.AddedEntry!="ZZZ_KESASModFixes/StaticTextureBindings.cfg"||entry.SizeBytes>65536)throw new InvalidDataException("Only the precise reviewed Static texture binding config is permitted.");bindings++;
                }else{
                    if(entry.Kind!="byte-identical-alias"||!ApfPaths.IsSafeRelative(entry.SourceEntry)||!entry.SourceEntry.StartsWith(rootPrefix,StringComparison.Ordinal))throw new InvalidDataException("Only reviewed same-package byte-identical texture aliases are permitted.");aliases++;
                }
            }
            media.Add(package.Id,item);
        }
        if(!new HashSet<string>(media.Keys,StringComparer.Ordinal).SetEquals(AssetIds)||aliases!=7||bindings!=1||assetTest.Packages.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=5||assetTest.Packages.Sum(p=>p.OriginalEntriesVerified)!=assetTest.OriginalEntriesVerified)throw new InvalidDataException("Asset evidence must cover exactly five packages, seven aliases and one eight-binding config.");
        Check(true,"Explicit AssetStage binds five exact declarations and passing TestAssets proof for unchanged original ZIP entries, seven aliases and eight bindings");
    }
    private static void CatalogRepairContract(InstallCatalog previous)
    {
        Check(previous.Version=="kesas-rc25"&&target.Version=="kesas-rc26","Catalog revisions are RC25 and RC26");
        foreach(string mode in new[]{"potato","full"}){
            var old=mode=="potato"?previous.Potato:previous.Full;var next=Mode(mode);
            Check(old.ManagedPackages.Count==next.ManagedPackages.Count&&next.ManagedPackages.Count==(mode=="potato"?177:211),mode+" retains the actual full package inventory");
            var expected=ApfPackManifest.FromJson(old.ToJson());expected.PackVersion="kesas-rc26";expected.RequiredClientVersion="kesas-rc26";
            var bumped=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var changed=new HashSet<string>(StringComparer.Ordinal);
            foreach(var package in expected.ManagedPackages){
                if(Repair(package)){
                    bumped.Add(ApfPaths.PackageRoot(package));package.Version+="-repair26";changed.Add(package.Id);
                }
                if(media.TryGetValue(package.Id,out var item)){
                    package.Version=item.Declaration.Version;package.ArchiveUrl=item.Declaration.ArchiveUrl;
                    package.Sha256=item.Declaration.Sha256;package.SizeBytes=item.Declaration.SizeBytes;changed.Add(package.Id);
                }
            }
            var declared=new HashSet<string>(old.ManagedPackages.Where(Repair).Select(p=>p.Id),StringComparer.Ordinal);declared.UnionWith(media.Keys);
            Check(bumped.SetEquals(RepairRoots)&&changed.SetEquals(declared)&&expected.ToJson()==next.ToJson(),mode+" changes only routing, four runtime repair versions and the five exact AssetPlan declarations");
            if(mode=="potato"){migrationIds.Clear();migrationIds.UnionWith(declared);}
        }
        Check(ApfJson.Serialize(previous.GitHub)==ApfJson.Serialize(target.GitHub)&&ApfJson.Serialize(previous.Sizes)==ApfJson.Serialize(target.Sizes),"Catalog source settings and declared expanded sizes unchanged");
    }
    private static Dictionary<string,string> SourceSnapshot()
    {
        var files=new List<string>{Path.Combine(Baseline,Data,"APFPackState.json"),Path.Combine(Baseline,Data,"KesasInstallProfile.json"),Path.Combine(Baseline,"settings.cfg"),Path.Combine(Baseline,"GameData/LunaMultiplayer/Launcher/install-catalog.json")};
        foreach(string name in RepairRoots.Concat(media.Values.Select(item=>ApfPaths.PackageRoot(item.OldDeclaration))).Distinct(StringComparer.OrdinalIgnoreCase))files.AddRange(PlainFiles(Path.Combine(Baseline,"GameData",name)));
        return files.ToDictionary(p=>p,ApfJson.Sha256File,StringComparer.OrdinalIgnoreCase);
    }
    private static void CloneBaseline()
    {
        HashIs(Path.Combine(Baseline,Data,"APFPackState.json"),SourceState25,"Original completed RC25 baseline state changed before cloning.");
        var state=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(Baseline,Data,"APFPackState.json")));
        var profile=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Baseline,Data,"KesasInstallProfile.json")));
        Check(state.PackVersion=="kesas-rc25"&&state.Packages.Count==177&&profile["mode"]=="potato"&&profile["catalogVersion"]=="kesas-rc25","Existing baseline is the completed RC25 Potato177 fixture, not a fresh-stock installation");
        string gameData=Path.Combine(Baseline,"GameData");Directory.CreateDirectory(Owned("GameData/Squad"));
        foreach(string directory in Directory.GetDirectories(gameData)){
            string name=Path.GetFileName(directory);if(name=="LunaMultiplayer"||name=="000_Harmony")continue;
            foreach(string source in PlainFiles(directory)){
                string relative="GameData/"+source.Substring(gameData.Length+1);string destination=Owned(relative);Directory.CreateDirectory(Path.GetDirectoryName(destination));
                string extension=Path.GetExtension(source).ToLowerInvariant();
                bool privateFile=RuntimeFiles.IsMutable(relative)||new[]{".cfg",".xml",".json",".log",".txt",".user",".glob",".cache",".tmp"}.Contains(extension);
                if(privateFile||!CreateHardLink(destination,source,IntPtr.Zero)){File.Copy(source,destination);copied++;}
                else{linked++;linkedBytes+=new FileInfo(source).Length;}
            }
        }
        foreach(string source in PlainFiles(Client25))CopyPrivate(source,source.Substring(Client25.Length+1));
        foreach(string name in new[]{"APFPackState.json","KesasInstallProfile.json","APFServer.txt","APFTlsCertificatePins.txt","APFClientUpdatePublicKey.xml"})CopyPrivate(Path.Combine(Baseline,Data,name),Data+name);
        CopyPrivate(Path.Combine(Baseline,"settings.cfg"),"settings.cfg");
        CopyPrivate(Path.Combine(Workspace,"Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe"),"KSP_x64.exe");
        HashIs(Owned("KSP_x64.exe"),"dd4826db1753b1dcea52873117dae5baa6e84e25d304491c50abb747df68e2fd","Owned executable is not the approved GameStub.");
        WritePrivate(Data+"privatekey.txt","RC26 profile identity sentinel");WritePrivate("saves/Profile/persistent.sfs","RC26 profile save sentinel");
        CopyPrivate(Path.Combine(Workspace,"Aetherfront-RC25-Work/Candidate/Published/APFClientUpdate.json"),"APF/.client-update-staging/connect/feed.json");
        ApfTransactionSecurity.LoadOrCreateKey(Owned(Data+"APFHelper.key"));
        Check(State().Packages.All(p=>sourceCatalog.Potato.ManagedPackages.Any(n=>n.Id==p.Id&&n.Version==p.Version&&n.Sha256==p.ArchiveSha256)),"Reused state retains the original RC25 package versions and archive hashes before the reviewed migration");
        WritePrivate("clone-proof.json",ApfJson.Serialize(new{source=Baseline,linked,copied,linkedBytes,freshStock=false,fullPackReextracted=false}));
    }
    private static void Dirty(string phase)
    {
        foreach(string path in RuntimePaths)WritePrivate("GameData/"+path,"generated RC26 profile runtime "+phase+" "+path);
        Check(RuntimePaths.All(path=>File.Exists(Owned("GameData/"+path))),phase+": all observed generated runtime files exist privately");
    }
    private static string LocalArchive(ApfPackage package)
    {
        string name=Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath);
        if(media.TryGetValue(package.Id,out var item)){
            if(ApfJson.Serialize(package)!=ApfJson.Serialize(item.Declaration))throw new InvalidDataException("Catalog asset tuple differs from explicit AssetPlan.");
            string reviewed=Path.Combine(Path.GetDirectoryName(assetPlanPath),"Media",item.Filename);
            HashIs(reviewed,package.Sha256,"Explicit AssetStage archive changed before preparation.");
            if(new FileInfo(reviewed).Length!=package.SizeBytes)throw new InvalidDataException("Explicit AssetStage archive size changed before preparation.");return reviewed;
        }
        var sources=new[]{Path.Combine(Workspace,"Aetherfront-RC25-Work/ModFixes/Staged-RC25/Media",name),Path.Combine(Workspace,"Aetherfront-RC26-Work/ModFixes/Staged-RC26/Media",name),Path.Combine(Workspace,"KESAS-VPS-Server/Server/Config",name),Path.Combine(Baseline,"APF/.staging/6125b20fdf4646dca33aa029f64e9588",name)};
        string source=sources.FirstOrDefault(File.Exists);if(source==null)throw new FileNotFoundException("Required real catalog archive unavailable locally; no network fallback.",name);
        if(new FileInfo(source).Length!=package.SizeBytes)throw new InvalidDataException("Real archive size mismatch: "+name);
        HashIs(source,package.Sha256,"Real catalog archive hash mismatch: "+name);return source;
    }
    private static string Prepare(string mode,bool clientBundle)
    {
        var previous=State();var manifest=ApfPackManifest.FromJson(Mode(mode).ToJson());
        var old=previous.Packages.ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);
        var transaction=new ApfSyncTransaction{GameRoot=root,GameDataPath=Owned("GameData"),KspExecutable=Owned("KSP_x64.exe"),ProcessId=0,KspArguments=new List<string>(),StatePath=Owned(Data+"APFPackState.json"),Manifest=manifest,ExistingState=previous};
        string stage="APF/.staging/"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(Owned(stage));
        foreach(var package in manifest.ManagedPackages){
            if(old.TryGetValue(package.Id,out var installed)&&installed.Version==package.Version&&installed.ArchiveSha256==package.Sha256)continue;
            string source=LocalArchive(package),archive=Owned(stage+"/"+Path.GetFileName(source));
            if(!CreateHardLink(archive,source,IntPtr.Zero))File.Copy(source,archive);
            transaction.Packages.Add(new ApfStagedPackage{Package=package,ArchivePath=archive});
        }
        var desired=new HashSet<string>(manifest.ManagedPackages.Select(p=>ApfPaths.PackageRoot(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
        var allowed=new HashSet<string>((manifest.ProtectedRoots??new List<string>()).Select(p=>ApfPaths.NormalizeGameDataRelative(p).Split('/')[0]),StringComparer.OrdinalIgnoreCase);
        foreach(var rule in manifest.ClientOnly)foreach(string value in rule.PathRoots)allowed.Add(ApfPaths.NormalizeGameDataRelative(value).Split('/')[0]);
        foreach(string entry in Directory.GetFileSystemEntries(transaction.GameDataPath)){
            string name=Path.GetFileName(entry);if(!desired.Contains(name)&&!allowed.Contains(name)&&!ApfPaths.IsBuiltInProtectedTopLevel(name.ToLowerInvariant()))transaction.Deletes.Add(new ApfDeleteChange{RelativePath="GameData/"+name,Reason="Explicit isolated profile-fixture transition"});
        }
        if(clientBundle)Check(transaction.Packages.Count==migrationIds.Count&&new HashSet<string>(transaction.Packages.Select(p=>p.Package.Id),StringComparer.Ordinal).SetEquals(migrationIds)&&transaction.Deletes.Count==0,"Dirty RC25 migration stages exactly the declared union: four runtime repairs plus five reviewed media archives ("+migrationIds.Count+" packages)");
        byte[] key=ApfTransactionSecurity.LoadKey(Owned(Data+"APFHelper.key"));ApfTransactionSecurity.Sign(transaction,key);
        string request=WritePrivate(stage+"/transaction.json",ApfJson.Serialize(transaction));
        if(clientBundle){
            string archive="APF/.client-update-staging/migration/client.zip";CopyPrivate(Path.Combine(candidate,"Published/APFClient-kesas-rc26.zip"),archive);
            var client=new ApfClientUpdateTransaction{GameRoot=root,GameDataPath=transaction.GameDataPath,ProcessId=0,KspExecutable=transaction.KspExecutable,KspArguments=new List<string>(),ArchivePath=Owned(archive),Manifest=feed26,Approval=ApfClientUpdateApproval.Capture(root)};
            ApfTransactionSecurity.Sign(client,key);File.WriteAllText(request+".client",ApfJson.Serialize(client));
            CopyPrivate(Path.Combine(candidate,"Published/APFClientUpdate.json"),"APF/.client-update-staging/connect/feed.json");
        }
        Console.WriteLine("PROFILE "+mode+" packages="+manifest.ManagedPackages.Count+" replacements="+transaction.Packages.Count+" deletes="+transaction.Deletes.Count+" helper="+ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location));
        PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        if(clientBundle)Check(State().PackVersion=="kesas-rc25"&&ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Owned("GameData/LunaMultiplayer/Launcher/install-catalog.json"))).Version=="kesas-rc25"&&RuntimePaths.All(p=>File.Exists(Owned("GameData/"+p))),"Preparation leaves dirty installed RC25 client/catalog/profile intact");
        return request;
    }
    private static void Commit(string request,bool explicitRestart)
    {
        using(var completion=NativeInstall.Run(Owned("GameData/LunaMultiplayer"),request,"pack")){
            Check(completion.RestartedProcessId==0,"No automatic game restart before explicit approval");
            if(explicitRestart){
                completion.Restart();Check(completion.RestartedProcessId>0,"Exactly one approved GameStub restart follows combined client/pack commit");
                bool rejected=false;try{completion.Restart();}catch(InvalidOperationException){rejected=true;}
                Check(rejected,"A second restart from the same completion is rejected");
                try{using(var game=Process.GetProcessById(completion.RestartedProcessId))if(!game.WaitForExit(10000))throw new IOException("Owned GameStub did not exit.");}catch(ArgumentException){}
            }
        }
    }
    private static void VerifyRuntimeScanner(string phase,string mode)
    {
        var state=State();var manifest=ApfPackManifest.FromJson(Mode(mode).ToJson());
        manifest.ManagedPackages=manifest.ManagedPackages.Where(Repair).ToList();
        var subset=new ApfPackState{PackId=state.PackId,PackVersion=state.PackVersion,Packages=state.Packages.Where(p=>manifest.ManagedPackages.Any(n=>n.Id==p.Id)).ToList()};
        var held=new List<FileStream>();
        try{
            foreach(string path in RuntimePaths)held.Add(new FileStream(Owned("GameData/"+path),FileMode.Open,FileAccess.Read,FileShare.None));
            var plan=ApfPackScanner.Scan(Owned("GameData"),manifest,subset);
            Check(plan.Install.Count==0,phase+": real core scanner accepts all four actual roots while runtime files are exclusively locked (no runtime hashing)");
        }finally{foreach(var stream in held)stream.Dispose();}
        string immutable=WritePrivate("GameData/KSPCommunityFixes/PluginData/not-runtime.dll","immutable negative-control sentinel");
        try{Check(ApfPackScanner.Scan(Owned("GameData"),manifest,subset).Install.Count==1,phase+": PluginData DLL negative control is not hidden by runtime exemptions");}
        finally{File.Delete(immutable);}
    }
    private static void VerifyReceipt(string mode)
    {
        var state=State();var manifest=Mode(mode);var receipt=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Owned("APF/LastInstallResult.json")));
        Check(receipt["status"]=="complete"&&receipt["mode"]==mode&&receipt["clientVersion"]=="kesas-rc26"&&receipt["packVersion"]=="kesas-rc26"&&state.PackVersion=="kesas-rc26"&&state.Packages.Count==manifest.ManagedPackages.Count,mode+": actual state and rich client/pack/profile completion receipt are RC26");
        WritePrivate(Data+"server-pack.json",manifest.ToJson());
        string manual=WritePrivate("GameData/ProfileManualSentinel/keep.txt","Fast path must not request manual-mod removal.");
        try{
            var plan=Installer.ScanConnection(Owned("GameData"),manifest,state);
            Check(plan.IsClean&&plan.Delete.Count==0&&File.Exists(manual),mode+": signed completed reconnect ignores manual addon and generated runtime files");
            var installer=new Installer(root,new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865),"kesas-rc26");
            typeof(Installer).GetField("verifiedClientManifest",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(installer,feed26);
            var matches=typeof(Installer).GetMethod("CompletedPackMatches",BindingFlags.Instance|BindingFlags.NonPublic);
            string other=mode=="potato"?"full":"potato";
            Check(!(bool)matches.Invoke(installer,new object[]{other,target,Mode(other)}),mode+": opposite profile cannot reuse this receipt");
        }finally{File.Delete(manual);Directory.Delete(Path.GetDirectoryName(manual));}
    }
    private static void VerifyInstalledAssets(string phase)
    {
        int additions=0;
        foreach(var item in media.Values){
            var installed=State().Packages.Single(p=>p.Id==item.Declaration.Id);
            if(installed.Version!=item.Declaration.Version||installed.ArchiveSha256!=item.Declaration.Sha256)throw new InvalidDataException("Installed asset state differs from reviewed declaration.");
            foreach(var entry in item.AddedEntries){
                string path=Owned("GameData/"+entry.AddedEntry);HashIs(path,entry.Sha256,"Installed reviewed asset addition differs from AssetPlan.");
                if(new FileInfo(path).Length!=entry.SizeBytes)throw new InvalidDataException("Installed reviewed asset addition size differs.");additions++;
            }
        }
        Check(additions==8,phase+": all seven reviewed texture aliases and the exact eight-binding config are installed byte-identically");
    }
    private static int LegacyWorker(string fixture)
    {
        RequireOwnedRoot(fixture);var context=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Owned("context.json")));candidate=context["candidate"];
        HashIs(typeof(NativeInstall).Assembly.Location,Engine25,"Migration worker is not the actually loaded RC25 helper.");
        HashIs(typeof(ApfJson).Assembly.Location,context["legacyCoreSha256"],"Migration worker core differs from signed RC25.");
        string originalCatalog=Path.Combine(Client25,"GameData/LunaMultiplayer/Launcher/install-catalog.json");
        HashIs(originalCatalog,context["baselineCatalogSha256"],"Signed RC25 source catalog changed before migration worker preparation.");
        sourceCatalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(originalCatalog));
        target=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(candidate,"Client/GameData/LunaMultiplayer/Launcher/install-catalog.json")));
        feed26=SignedFeed(Path.Combine(candidate,"Published/APFClientUpdate.json"),Owned(Data+"APFClientUpdatePublicKey.xml"),"kesas-rc26");
        HashIs(Path.Combine(candidate,"Client/GameData/LunaMultiplayer/Launcher/install-catalog.json"),feed26.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Launcher/install-catalog.json").Sha256,"Worker target catalog differs from signed RC26.");
        ReadAssets(context["assetPlanPath"],context["assetPlanSha256"],context["assetTestPath"],context["assetTestSha256"],context["baselineCatalogSha256"]);
        CatalogRepairContract(sourceCatalog);
        string request=Prepare("potato",true);Commit(request,true);
        Check(State().PackVersion=="kesas-rc26","Old loaded RC25 helper commits the real RC26 manifest");
        Check(!File.ReadAllText(Owned("APF/APFTransaction.log")).Contains("ROLLED-BACK"),"Four forced repair versions prevent the old-helper runtime-inventory rollback");
        WritePrivate("legacy-result.json",ApfJson.Serialize(new{passed=true,checks,helperSha256=ApfJson.Sha256File(typeof(NativeInstall).Assembly.Location),coreSha256=ApfJson.Sha256File(typeof(ApfJson).Assembly.Location),preparedByOldHelper=true,runtimeRepairPackages=4,reviewedMediaPackages=media.Count,actualArchivePackages=migrationIds.Count,assetPlanSha256,assetTestSha256,explicitRestarts=1,request}));return 0;
    }
    private static void RunLegacyWorker()
    {
        string bin="worker25";CopyPrivate(Assembly.GetExecutingAssembly().Location,bin+"/ProfileTests.exe");
        string config=Assembly.GetExecutingAssembly().Location+".config";if(File.Exists(config))CopyPrivate(config,bin+"/ProfileTests.exe.config");
        foreach(string name in new[]{"KesasInstall.dll","ApfCore.dll","JsonFx.dll"})CopyPrivate(Path.Combine(Client25,"GameData/LunaMultiplayer/Plugins",name),bin+"/"+name);
        foreach(string name in new[]{"System.IO.Compression.dll","System.IO.Compression.FileSystem.dll","System.Numerics.dll"})CopyPrivate(Path.Combine(Client25,"KSP_x64_Data/Managed",name),bin+"/"+name);
        string executable=Owned(bin+"/ProfileTests.exe"),arguments="--legacy-worker \""+root+"\"";
        if(Type.GetType("Mono.Runtime")!=null){arguments="\""+executable+"\" "+arguments;executable=Path.Combine(Workspace,"Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe");}
        var info=new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Workspace,RedirectStandardOutput=true,RedirectStandardError=true};
        using(var process=new Process{StartInfo=info})using(var stdout=new StreamWriter(Owned("legacy.stdout.log")))using(var stderr=new StreamWriter(Owned("legacy.stderr.log"))){
            process.OutputDataReceived+=(sender,eventArgs)=>{if(eventArgs.Data!=null){lock(stdout){stdout.WriteLine(eventArgs.Data);stdout.Flush();}Console.WriteLine(eventArgs.Data);}};
            process.ErrorDataReceived+=(sender,eventArgs)=>{if(eventArgs.Data!=null){lock(stderr){stderr.WriteLine(eventArgs.Data);stderr.Flush();}Console.Error.WriteLine(eventArgs.Data);}};
            process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();Check(process.ExitCode==0,"Separate old-helper25 combined migration worker completed without a second installer/restart");
        }
        Check(File.Exists(Owned("legacy-result.json")),"Old-helper migration proof retained separately");
    }
    private static int Main(string[] args)
    {
        try{
            if(args.Length==2&&args[0]=="--legacy-worker")return LegacyWorker(args[1]);
            if(args.Length<6||args.Length>7||(args.Length==7&&args[6]!="--chain"))throw new ArgumentException("Expected absolute Candidate directory, CODE-RESULT.json, ASSET-PLAN.json, plan SHA256, TestAssets result and result SHA256, optionally --chain. All inputs must be reviewed and frozen.");
            if(!Regex.IsMatch(Engine26,"^[0-9a-f]{64}$")||Engine26=="bfafcbbb46e3ef92fe88c8a488fe618295d38981597e118397e9fff1ce4cbc26")throw new InvalidDataException("Await the reviewed post-junction RC26 Engine pin; the old bfaf candidate is forbidden.");
            candidate=Path.GetFullPath(args[0]);string codeResult=Path.GetFullPath(args[1]);
            if(!ApfPaths.IsUnder(candidate,Path.Combine(Workspace,"Aetherfront-RC26-Work")))throw new InvalidDataException("Candidate is outside RC26 work.");
            HashIs(typeof(NativeInstall).Assembly.Location,Engine26,"Loaded engine is not the frozen RC26 output.");HashIs(typeof(ApfJson).Assembly.Location,Core26,"Loaded core is not the frozen scanner-grafted RC26 output.");
            string publicKey=Path.Combine(Baseline,Data,"APFClientUpdatePublicKey.xml");
            var oldFeed=SignedFeed(Path.Combine(Workspace,"Aetherfront-RC25-Work/Candidate/Published/APFClientUpdate.json"),publicKey,"kesas-rc25");
            feed26=SignedFeed(Path.Combine(candidate,"Published/APFClientUpdate.json"),publicKey,"kesas-rc26");
            HashIs(Path.Combine(candidate,"Published/APFClient-kesas-rc26.zip"),feed26.Sha256,"Candidate client archive differs from its signed manifest.");
            foreach(var input in new[]{new{Relative="GameData/LunaMultiplayer/Plugins/KesasInstall.dll",Hash=Engine26},new{Relative="GameData/LunaMultiplayer/Plugins/ApfCore.dll",Hash=Core26}}){
                Check(feed26.Files.Single(f=>f.Path==input.Relative).Sha256==input.Hash,"Signed candidate pins frozen "+input.Relative);
                HashIs(Path.Combine(candidate,"Client",input.Relative),input.Hash,"Candidate overlay differs from frozen code.");
            }
            foreach(string name in new[]{"KesasInstall.dll","ApfCore.dll","JsonFx.dll"})HashIs(Path.Combine(Client25,"GameData/LunaMultiplayer/Plugins",name),oldFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Plugins/"+name).Sha256,"Old-helper input differs from signed RC25.");
            string targetCatalog=Path.Combine(candidate,"Client/GameData/LunaMultiplayer/Launcher/install-catalog.json");
            HashIs(targetCatalog,feed26.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Launcher/install-catalog.json").Sha256,"Candidate catalog is not the signed catalog.");
            target=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(targetCatalog));
            string originalCatalog=Path.Combine(Client25,"GameData/LunaMultiplayer/Launcher/install-catalog.json");
            string baselineCatalogSha256=oldFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Launcher/install-catalog.json").Sha256;
            HashIs(originalCatalog,baselineCatalogSha256,"Baseline catalog differs from signed RC25.");
            sourceCatalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(originalCatalog));
            ReadAssets(args[2],args[3],args[4],args[5],baselineCatalogSha256);
            CatalogRepairContract(sourceCatalog);
            var original=SourceSnapshot();
            root=Path.Combine(OwnedBase,"P"+Guid.NewGuid().ToString("N").Substring(0,8));if(Directory.Exists(root))throw new IOException("Owned fixture already exists.");Directory.CreateDirectory(root);WritePrivate("owned-fixture.txt",root);
            Console.WriteLine("FIXTURE "+root);CloneBaseline();
            WritePrivate("context.json",ApfJson.Serialize(new Dictionary<string,string>{{"candidate",candidate},{"codeResult",codeResult},{"legacyCoreSha256",oldFeed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Plugins/ApfCore.dll").Sha256},{"baselineCatalogSha256",baselineCatalogSha256},{"assetPlanPath",assetPlanPath},{"assetPlanSha256",assetPlanSha256},{"assetTestPath",assetTestPath},{"assetTestSha256",assetTestSha256}}));
            Dirty("dirty-existing25");RunLegacyWorker();
            Dirty("regenerated-after-migration26");VerifyRuntimeScanner("RC26 migration","potato");VerifyInstalledAssets("RC26 migration");VerifyReceipt("potato");
            bool chain=args.Length==7;
            if(chain)foreach(string mode in new[]{"full","potato"}){
                Dirty("before-"+mode);string request=Prepare(mode,false);Commit(request,false);
                Dirty("after-"+mode);VerifyRuntimeScanner(mode,mode);VerifyInstalledAssets(mode);VerifyReceipt(mode);
            }
            Check(File.ReadAllText(Owned(Data+"privatekey.txt"))=="RC26 profile identity sentinel"&&File.ReadAllText(Owned("saves/Profile/persistent.sfs"))=="RC26 profile save sentinel"&&File.ReadAllText(Owned("settings.cfg")).Contains("KEY_BINDING = retained"),"Migration and selected transitions preserve private identity, saves and controls");
            Check(original.All(pair=>File.Exists(pair.Key)&&ApfJson.Sha256File(pair.Key)==pair.Value),"Original RC25 baseline metadata and every runtime/media source root remain unchanged");
            WritePrivate("result.json",ApfJson.Serialize(new{passed=true,checks,headlessOnly=true,unityGameplayTested=false,reusedExistingProfile=true,freshStockTest=false,fullBaselineReextracted=false,dirtyExistingProfileMigration=true,oldHelperVersion="kesas-rc25",newVersion="kesas-rc26",engine26Sha256=Engine26,core26Sha256=Core26,runtimeRepairArchives=4,reviewedAssetArchives=media.Count,actualMigrationPackages=migrationIds.Count,migrationPackageIds=migrationIds.OrderBy(id=>id,StringComparer.Ordinal).ToArray(),assetPlanPath,assetPlanSha256,assetTestPath,assetTestSha256,originalAssetEntriesPreservedByTestAssets=assetTest.OriginalEntriesVerified,installedAliasCount=7,installedBindingCount=8,bothCatalogsValidated=true,fullProfileCycleExecuted=chain,runtimeFileBoundary="Synthetic private payloads at the observed game-generated paths; no mod or Unity execution.",scannerBoundary="Four real repair roots scanned directly with exclusive runtime-file locks; NativeRun independently verifies the complete signed manifest at each commit.",assetBoundary="Exact AssetPlan/TestAssets hashes, five unchanged-root declarations and installed additions checked; original ZIP entry preservation is attributed to the separately pinned TestAssets evidence, not re-executed here.",source=Baseline,sourceHashes=original,candidate,codeResult,linked,copied,linkedBytes,completedUtc=DateTime.UtcNow.ToString("o")}));
            Console.WriteLine("PASS dirty existing profile checks="+checks+" fullCycle="+chain+" RESULT "+Owned("result.json"));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Retained owned fixture="+root);return 1;}
    }
}
