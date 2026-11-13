using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ApfCore;
using KesasInstall;

int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
void Reject(Action action,string message){bool caught=false;try{action();}catch(Exception){caught=true;}Check(caught,message);}
if(args.Length!=4)throw new ArgumentException("Expected verified RC60 baseline directory, feed, ZIP and public signing key.");
string baseline=Path.GetFullPath(args[0]);
string root=Path.Combine(Path.GetTempPath(),"APF-incremental-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Write(string relative,string text){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text,new UTF8Encoding(false));return path;}
string Copy(string relative){string destination=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(Path.Combine(baseline,relative),destination);return destination;}
string data=Path.Combine(root,"GameData","LunaMultiplayer","Data");
try {
    Write("KSP_x64.exe","fixture-only; never execute");Directory.CreateDirectory(Path.Combine(root,"GameData","Squad"));
    string save=Write("saves/Fixture/persistent.sfs","progress sentinel"),identity=Write("GameData/LunaMultiplayer/Data/privatekey.txt","identity sentinel"),controls=Write("settings.cfg","controls sentinel");
    Copy("GameData/LunaMultiplayer/Plugins/LmpClient.dll");
    Copy("GameData/LunaMultiplayer/Plugins/ApfCore.dll");
    Copy("GameData/LunaMultiplayer/Data/APFClient.json");
    Write("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml",File.ReadAllText(args[3]));
    Write("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt",new string('a',64));
    Write("GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt","https://217.216.49.250:18866/updates/APFClientUpdate.json");
    string catalogPath=Copy("GameData/LunaMultiplayer/Launcher/install-catalog.json");
    var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath));
    var manifest=ApfPackManifest.FromJson(catalog.Full.ToJson());var package=manifest.ManagedPackages[0];
    string packageRoot=ApfPaths.PackageRoot(package);
    string file=Write("GameData/"+packageRoot+"/fixture.cfg","large unchanged managed payload");
    var packageState=new ApfPackageState{Id=package.Id,Version=package.Version,ArchiveSha256=package.Sha256,Files=new List<ApfFileState>{new ApfFileState{Path="GameData/"+packageRoot+"/fixture.cfg",Sha256=ApfJson.Sha256File(file)}}};
    var state=new ApfPackState{PackId=manifest.PackId,PackVersion=manifest.PackVersion,Packages=new List<ApfPackageState>{packageState}};
    string statePath=Write("GameData/LunaMultiplayer/Data/APFPackState.json",ApfJson.Serialize(state));
    Write("GameData/LunaMultiplayer/Data/KesasInstallProfile.json",ApfJson.Serialize(new Dictionary<string,string>{{"mode","full"},{"catalogVersion",manifest.PackVersion}}));
    var feed=ApfClientUpdateManifest.FromJson(File.ReadAllText(args[1]));
    Write("APF/.client-update-staging/connect/feed.json",feed.ToJson());
    byte[] key=ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data,"APFHelper.key"));
    var transaction=new ApfSyncTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),KspExecutable=Path.Combine(root,"KSP_x64.exe"),StatePath=statePath,Manifest=manifest,ExistingState=state,ProcessId=0};
    ApfTransactionSecurity.Sign(transaction,key);
    Installer.WriteCompletedReceipt(transaction,"full");
    string receiptPath=Path.Combine(root,"APF","LastInstallResult.json");
    string receipt=File.ReadAllText(receiptPath),stateJson=File.ReadAllText(statePath);
    var previous=IncrementalInstall.Load(root,"full");
    Check(previous!=null,"authenticated completed inventory loads");
    Check(IncrementalInstall.Load(root,"potato")==null,"different install profile does not reuse baseline");
    Check(previous.CanReuse(package,packageState),"identical package identity and authenticated inventory reused");
    var changed=ApfJson.Deserialize<ApfPackage>(ApfJson.Serialize(package));changed.Sha256=new string('b',64);
    Check(!previous.CanReuse(changed,packageState),"changed package archive never reused");
    var changedState=ApfJson.Deserialize<ApfPackageState>(ApfJson.Serialize(packageState));changedState.Files[0].Sha256=new string('c',64);
    Check(!previous.CanReuse(package,changedState),"changed state inventory never reused");
    File.AppendAllText(statePath," ");Check(IncrementalInstall.Load(root,"full")==null,"valid JSON with tampered inventory bytes invalidates fast path");File.WriteAllText(statePath,stateJson,new UTF8Encoding(false));
    var invalid=ApfJson.Deserialize<Dictionary<string,string>>(receipt);invalid["mode"]="potato";File.WriteAllText(receiptPath,ApfJson.Serialize(invalid));
    Check(IncrementalInstall.Load(root,"potato")==null,"invalid receipt HMAC cannot authorize reuse");File.WriteAllText(receiptPath,receipt,new UTF8Encoding(false));
    string saved=Path.Combine(root,"APF","InstalledClient.json"),savedText=File.ReadAllText(saved);File.AppendAllText(saved,"broken");
    Check(IncrementalInstall.Load(root,"full")==null,"invalid saved signed manifest falls back");File.WriteAllText(saved,savedText,new UTF8Encoding(false));
    var installer=new Installer(root,new IPEndPoint(IPAddress.Loopback,18865),feed.Version);
    typeof(Installer).GetField("previousInstall",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(installer,previous);
    var present=typeof(Installer).GetMethod("PackagePresent",BindingFlags.Instance|BindingFlags.NonPublic)!;
    using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
        Check((bool)present.Invoke(installer,new object[]{package,packageState})!,"unchanged package not rehashed even while payload cannot be read");
        installer.ForceVerification=true;Reject(()=>present.Invoke(installer,new object[]{package,packageState}),"explicit Repair really reads installed package files");installer.ForceVerification=false;
        var retained=new List<PreparedPackage>{new PreparedPackage{Id=package.Id,ArchiveSha256=package.Sha256,Root=packageRoot,Files=packageState.Files}};
        string request=Write("APF/.staging/test/transaction.json",ApfJson.Serialize(transaction));
        PreparedPack.Prepare(transaction,request,_=>{},()=>false,retained,previous.ClientManifest);
        PreparedPack.Open(transaction,request);
        Check(PreparedPack.Files(transaction.GameDataPath,Path.Combine(transaction.GameDataPath,packageRoot))?.Count==1,"helper final scan reuses authenticated retained inventory without reading payload");
        PreparedPack.Close();
        string envelope=File.ReadAllText(request+".prepared");var corrupt=ApfJson.Deserialize<PreparedEnvelope>(envelope);corrupt.Mac="invalid";File.WriteAllText(request+".prepared",ApfJson.Serialize(corrupt));
        Reject(()=>PreparedPack.Open(transaction,request),"tampered retained receipt fails before installation");
        File.WriteAllText(request+".prepared",envelope);
        var authorized=ApfJson.Deserialize<PreparedEnvelope>(envelope);var payload=ApfJson.Deserialize<PreparedReceipt>(authorized.Payload);payload.RetainedPackages[0].Root="../outside";
        authorized.Payload=ApfJson.Serialize(payload);using(var hmac=new HMACSHA256(key))authorized.Mac=Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(authorized.Payload)));
        File.WriteAllText(request+".prepared",ApfJson.Serialize(authorized));Reject(()=>PreparedPack.Open(transaction,request),"even authorized retained path cannot escape package");PreparedPack.Close();
    }
    string stable=Write("GameData/LunaMultiplayer/Plugins/stable-test.dll","same"),different=Write("GameData/LunaMultiplayer/Plugins/changed-test.dll","old");
    var stableRecord=new ApfClientUpdateFile{Path="GameData/LunaMultiplayer/Plugins/stable-test.dll",SizeBytes=4,Sha256=ApfJson.Sha256File(stable)};
    string expected=Write("APF/new-bytes","new");
    var changedRecord=new ApfClientUpdateFile{Path="GameData/LunaMultiplayer/Plugins/changed-test.dll",SizeBytes=3,Sha256=ApfJson.Sha256File(expected)};
    var missingRecord=new ApfClientUpdateFile{Path="GameData/LunaMultiplayer/Plugins/missing-test.dll",SizeBytes=3,Sha256=changedRecord.Sha256};
    var selection=new ApfClientUpdateManifest{Files=new List<ApfClientUpdateFile>{stableRecord,changedRecord,missingRecord}};
    DeltaClientFiles.Begin(new ApfClientUpdateTransaction{GameRoot=root});var selected=DeltaClientFiles.Files(selection);
    Check(selected.Count==2 && !selected.Contains(stableRecord) && selected.Contains(changedRecord) && selected.Contains(missingRecord),"client replacement list contains only changed and missing files");
    Check(ReferenceEquals(selected,DeltaClientFiles.Files(selection)),"backup and replacement loops share one delta decision");
    string extra=Write("GameData/FixtureExtraMod/settings.txt","recoverable settings");
    string extraRoot=Path.GetDirectoryName(extra)!;
    var native=typeof(NativeInstall);var flags=BindingFlags.Static|BindingFlags.NonPublic;
    native.GetField("root",flags)!.SetValue(null,root);native.GetField("enabled",flags)!.SetValue(null,true);
    var removals=(HashSet<string>)native.GetField("removals",flags)!.GetValue(null)!;removals.Add(extraRoot);
    try {
        string canceledPath=Write("APF/.staging/canceled/transaction.json.cancel","cancel");
        native.GetField("cancellationPath",flags)!.SetValue(null,canceledPath);
        Reject(()=>NativeInstall.AcquireExecutionLocks(Path.Combine(root,"KSP_x64.exe")),"scene-change cancellation refuses helper mutation before execution locks");
        native.GetField("cancellationPath",flags)!.SetValue(null,null);
        var journal=NativeInstall.CreateRecovery(root,Path.Combine(root,"APF",".transaction-backup","fixture-recovery"),new[]{extraRoot},"pack");
        Check(journal.Records.Any(r=>r.Target==extraRoot),"automatically disabled extra mod has durable recovery record");
        Check(NativeInstall.RemoveApproved(extraRoot,transaction.GameDataPath) && !Directory.Exists(extraRoot),"extra mod removed from active GameData without deletion");
        Check(Directory.GetFiles(Path.Combine(root,"APF",".disabled-mods"),"settings.txt",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p)=="recoverable settings"),"extra mod bytes preserved in quarantine");
    } finally {removals.Clear();native.GetField("enabled",flags)!.SetValue(null,false);native.GetField("cancellationPath",flags)!.SetValue(null,null);}

    // Execute the real patched core against an authenticated shipping archive.
    // No KSP process is launched, no network is used, all writes stay in root.
    string archive=Path.Combine(root,"APF",".client-update-staging","shipping.zip");File.Copy(args[2],archive);
    var clientTransaction=new ApfClientUpdateTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),KspExecutable=Path.Combine(root,"KSP_x64.exe"),ProcessId=0,ArchivePath=archive,Manifest=feed,Approval=ApfClientUpdateApproval.Capture(root)};
    bool suppression=KesasInstallPolicy.SuppressRelaunch;KesasInstallPolicy.SuppressRelaunch=true;
    try {
        ApfArchiveInstaller.ApplyClientUpdate(clientTransaction);
        string shipped=Path.Combine(root,"GameData","LunaMultiplayer","Plugins","KesasInstall.dll");
        Check(File.Exists(shipped),"real core applies authenticated shipping files in fixture");
        DateTime time=File.GetLastWriteTimeUtc(shipped);string hash=ApfJson.Sha256File(shipped);
        clientTransaction.Approval=ApfClientUpdateApproval.Capture(root);
        var clock=Stopwatch.StartNew();ApfArchiveInstaller.ApplyClientUpdate(clientTransaction);clock.Stop();
        Check(File.GetLastWriteTimeUtc(shipped)==time && ApfJson.Sha256File(shipped)==hash,"unchanged real shipping DLL not rewritten on repeat");
        Console.WriteLine("MEASURE unchanged signed client apply milliseconds="+clock.Elapsed.TotalMilliseconds);
        string altered=Path.Combine(root,"GameData","LunaMultiplayer","Plugins","AetherfrontStabilityClient.dll");File.AppendAllText(altered,"damage");
        clientTransaction.Approval=ApfClientUpdateApproval.Capture(root);ApfArchiveInstaller.ApplyClientUpdate(clientTransaction);
        var declaration=feed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Plugins/AetherfrontStabilityClient.dll");
        Check(ApfJson.Sha256File(altered)==declaration.Sha256 && File.GetLastWriteTimeUtc(shipped)==time,"damaged client DLL repaired without rewriting unchanged DLL");
        File.AppendAllText(archive,"tamper");clientTransaction.Approval=ApfClientUpdateApproval.Capture(root);
        Reject(()=>ApfArchiveInstaller.ApplyClientUpdate(clientTransaction),"changed archive rejected before delta application");
        Check(File.GetLastWriteTimeUtc(shipped)==time && File.ReadAllText(save)=="progress sentinel" && File.ReadAllText(identity)=="identity sentinel" && File.ReadAllText(controls)=="controls sentinel","failed update and successful delta preserve progress, identity and controls");
    }finally{KesasInstallPolicy.SuppressRelaunch=suppression;}
    Console.WriteLine("PASS "+checks+" incremental installation assertions; no production writes, network, KSP launch or deployment.");
} finally {
    PreparedPack.Close();
    // Only the exact temporary directory generated above is eligible.
    if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar+"APF-incremental-tests-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup");
    Directory.Delete(root,true);
}
