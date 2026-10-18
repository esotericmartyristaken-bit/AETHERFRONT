using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static string root,data;private static int checks;
    private static readonly List<string> regressionFixtures=new List<string>();
    private static void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks++;Console.WriteLine("PASS "+name);}
    private static string Write(string relative,string value){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value);return path;}
    private static bool legacyRuntime,legacyMigration;
    private static int Main(string[] args)
    {
        try {
            if(args.Length>0) {
                if(args[0]!="--runtime-repro"||args.Length>2||(args.Length==2&&args[1]!="--legacy"&&args[1]!="--legacy-migration"))throw new ArgumentException("Expected --runtime-repro [--legacy|--legacy-migration].");
                legacyRuntime=args.Length==2&&args[1]=="--legacy";
                legacyMigration=args.Length==2&&args[1]=="--legacy-migration";
                InFixture("runtime-generated",TestGeneratedRuntimeFiles);
                Console.WriteLine("PASS runtime reproduction checks="+checks+" legacy="+legacyRuntime);return 0;
            }
            root=Path.GetFullPath("Aetherfront-RC27-Work/NativeFixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(data);Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
            File.Copy(Path.GetFullPath("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe"),Path.Combine(root,"KSP_x64.exe"));
            File.Copy(Path.Combine(root,"KSP_x64.exe"),Path.Combine(root,"KSP.exe"));
            Write("GameData/LunaMultiplayer/Data/APFServer.txt","217.216.49.250:18865");
            Write("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt","34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09");
            Write("settings.cfg","ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = retained\n");
            Write("saves/Tester/persistent.sfs","save sentinel");Write("GameData/LunaMultiplayer/Data/privatekey.txt","identity sentinel");
            Write("GameData/PersonalAddon/remove.txt","not recoverable by request");
            Write("GameData/TestCore/example.cfg","previous managed configuration");
            using(var process=Process.Start(new ProcessStartInfo(Path.Combine(root,"KSP_x64.exe")){UseShellExecute=false}))Check(process.WaitForExit(10000)&&process.ExitCode==0,"Known headless game executable runs before installation");
            var transaction=MakeTransaction(true);
            string request=Save(transaction);
            PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
            Check(File.ReadAllText(Path.Combine(root,"GameData/TestCore/example.cfg"))=="previous managed configuration","Preparation never changes installed files");
            using(var completion=NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")) {
                Check(File.ReadAllText(Path.Combine(root,"GameData/TestCore/example.cfg"))=="new managed configuration","Signed pack installs and verifies expected content");
                Check(Directory.GetDirectories(Path.Combine(root,"GameData")).Select(Path.GetFileName).Contains("TestCore",StringComparer.Ordinal),"Prepared package keeps canonical folder casing");
                Check(!Directory.Exists(Path.Combine(root,"GameData/PersonalAddon")),"Approved extra mod removed");
                var journal=Directory.GetFiles(Path.Combine(root,"APF/.transaction-backup"),"recovery.json",SearchOption.AllDirectories).Single();
                var proof=ApfJson.Deserialize<ApfRecoveryJournal>(File.ReadAllText(journal));
                Check(proof.Status=="complete"&&proof.Records.All(r=>!r.Target.EndsWith("PersonalAddon",StringComparison.OrdinalIgnoreCase)),"Removed mod has no backup or recovery record");
                Check(!Directory.GetFiles(Path.Combine(root,"APF/.transaction-backup"),"remove.txt",SearchOption.AllDirectories).Any(),"No removed-mod content retained");
                Check(Directory.GetFiles(Path.Combine(root,"APF/.transaction-backup"),"example.cfg",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p)=="previous managed configuration"),"Replaced managed files retain failure recovery");
                Check(LaunchBlocked("KSP_x64.exe")&&LaunchBlocked("KSP.exe"),"Both executable names blocked until completion is acknowledged");
                Check(NativeInstall.Status.Contains("verified"),"Completion reported only after installation verification");
                completion.Restart();
                Check(completion.RestartedProcessId>0,"Explicit restart starts the validated KSP executable directly");
            }
            Thread.Sleep(100);
            using(var process=Process.Start(new ProcessStartInfo(Path.Combine(root,"KSP_x64.exe")){UseShellExecute=false}))Check(process.WaitForExit(10000),"Explicit restart releases executable locks");
            Check(File.ReadAllText(Path.Combine(root,"saves/Tester/persistent.sfs"))=="save sentinel"&&File.ReadAllText(Path.Combine(data,"privatekey.txt"))=="identity sentinel","Saves and identity preserved");
            Check(File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("KEY_BINDING = retained"),"Controls preserved");
            var rejected=MakeTransaction(false);rejected.Authorization="invalid";request=Save(rejected,false);
            bool failed=false;try{using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){} }catch(InvalidDataException){failed=true;}
            Check(failed,"Invalid authorization rejected without mutation");
            Check(File.Exists(Path.Combine(root,"APF/APFUpdater.log")),"Failure retained in updater log");
            using(var process=Process.Start(new ProcessStartInfo(Path.Combine(root,"KSP_x64.exe")){UseShellExecute=false}))Check(process.WaitForExit(10000),"Failure releases execution locks");
            InFixture("collision",TestCollision);
            InFixture("bare-deletion",TestBareDeletion);
            InFixture("overlap",TestOverlappingRun);
            InFixture("final-scan-rollback",TestFinalScanRollback);
            InFixture("prepared-corruption",TestPreparedCorruption);
            InFixture("approved-duplicate",TestDuplicateRepair);
            InFixture("directory-leases",TestDirectoryLeases);
            InFixture("runtime-generated",TestGeneratedRuntimeFiles);
            InFixture("flat-file-packages",TestFlatFilePackages);
            Check(RuntimeFiles.IsMutable("GameData/SCANsat/PluginData/Settings.cfg"),"SCANsat runtime settings allowed");
            Check(RuntimeFiles.IsMutable("GameData/HideEmptyTechTreeNodes/Resources/HETTN.TechTree")&&RuntimeFiles.IsMutable("GameData/HideEmptyTechTreeNodes/Resources/HETTN.TechTree.tmp"),"Exact generated tech-tree outputs allowed");
            Check(!RuntimeFiles.IsMutable("GameData/HideEmptyTechTreeNodes/Resources/HETTN.TechTree.cfg")&&!RuntimeFiles.IsMutable("GameData/HideEmptyTechTreeNodes/Resources/plugin.dll")&&!RuntimeFiles.IsMutable("GameData/HideEmptyTechTreeNodes/Resources/HETTN.TechTree.bak"),"Tech-tree runtime exceptions do not exempt configuration, assemblies or backups");
            Check(!RuntimeFiles.IsMutable("GameData/FarFutureTechnologies/Parts/Engines/part.cfg"),"Part definitions remain immutable");
            Check(!RuntimeFiles.IsMutable("GameData/SCANsat/PluginData/plugin.dll"),"PluginData assemblies remain immutable");
            Check(RuntimeFiles.IsMutable("GameData/000_AT_Utils/Plugins/PluginData/000_AT_Utils/000_AT_Utils.glob"),"ATUtils generated global settings allowed");
            Check(RuntimeFiles.IsMutable("GameData/KcalbelohSystem/Cache/Ahtpan.bin")&&RuntimeFiles.IsMutable("GameData/OPM/Cache/Nissee.bin"),"Declared planet mesh cache locations allowed");
            Check(!RuntimeFiles.IsMutable("GameData/KcalbelohSystem/Parts/part.cfg")&&!RuntimeFiles.IsMutable("GameData/OPM/Cache/plugin.dll"),"Cache exceptions never exempt part definitions or assemblies");
            Check(RuntimeFiles.IsMutable("GameData/Kopernicus/Cache/mesh.hash")&&RuntimeFiles.IsMutable("GameData/Kopernicus/Cache/Kerbin.bin")&&!RuntimeFiles.IsMutable("GameData/Kopernicus/Cache/plugin.dll"),"Kopernicus generated mesh hashes and default cache allowed, not DLLs");
            File.WriteAllText(Path.Combine(root,"result.json"),ApfJson.Serialize(new{passed=true,checks,regressionFixtures,headlessOnly=true,unityGameplayTested=false,completedUtc=DateTime.UtcNow.ToString("o")}));
            Console.WriteLine("PASS native checks="+checks+" fixture="+root);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void InFixture(string label,Action test)
    {
        string previousRoot=root,previousData=data;
        try {
            root=Path.GetFullPath("Aetherfront-RC27-Work/NativeFixture-"+label+"-"+Guid.NewGuid().ToString("N"));
            ApfPaths.EnsureNoReparsePoints(Path.GetFullPath("Aetherfront-RC27-Work"),root);
            Directory.CreateDirectory(root);regressionFixtures.Add(root);
            data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(data);Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
            string stub=Path.GetFullPath("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe");
            File.Copy(stub,Path.Combine(root,"KSP_x64.exe"));File.Copy(stub,Path.Combine(root,"KSP.exe"));
            Write("GameData/LunaMultiplayer/Data/APFServer.txt","127.0.0.1:18865");
            Write("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt","34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09");
            Write("GameData/LunaMultiplayer/Data/APFPackState.json",ApfJson.Serialize(new ApfPackState{PackId="kesas",PackVersion="previous-native-fixture"}));
            Write("GameData/LunaMultiplayer/Data/KesasInstallProfile.json",ApfJson.Serialize(new Dictionary<string,string>{{"mode","full"},{"catalogVersion","previous-native-fixture"}}));
            Write("settings.cfg","ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = retained\n");
            Write("saves/Tester/persistent.sfs","save sentinel");Write("GameData/LunaMultiplayer/Data/privatekey.txt","identity sentinel");
            Write("GameData/PersonalAddon/remove.txt","not recoverable by request");
            Write("GameData/TestCore/example.cfg","previous managed configuration");
            Console.WriteLine("FIXTURE "+label+" "+root);
            test();
            Check(File.ReadAllText(Path.Combine(root,"saves/Tester/persistent.sfs"))=="save sentinel"&&File.ReadAllText(Path.Combine(data,"privatekey.txt"))=="identity sentinel",label+": saves and identity preserved");
        } finally {root=previousRoot;data=previousData;}
    }
    private static string[] Snapshot(string relative)
    {
        string directory=Path.Combine(root,relative);
        return Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Select(p=>p.Substring(directory.Length)+"="+ApfJson.Sha256File(p)).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
    }
    private static void TestPreparedCorruption()
    {
        var transaction=MakeTransaction(true);string request=Save(transaction);
        PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        var envelope=ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(request+".prepared"));
        var prepared=ApfJson.Deserialize<PreparedReceipt>(envelope.Payload);
        File.WriteAllText(Path.Combine(root,prepared.DataDirectory.Replace('/',Path.DirectorySeparatorChar),"TestCore/example.cfg"),"corrupt");
        string[] before=Snapshot("GameData");bool rejected=false;
        try{using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){} }catch(InvalidDataException){rejected=true;}
        Check(rejected&&before.SequenceEqual(Snapshot("GameData")),"Prepared corruption rejected before installed-file mutation");
    }
    private static void TestDuplicateRepair()
    {
        Write("Resources/GameData/LunaMultiplayer/Plugins/LmpClient.dll","duplicate client sentinel");
        Write("Resources/GameData/LunaMultiplayer/Data/privatekey.txt","duplicate identity preserved");
        Write("Resources/unrelated.txt","unrelated Resources retained");
        var transaction=MakeTransaction(true);string request=Save(transaction);File.WriteAllText(request+".duplicate","true");
        PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        Check(File.Exists(Path.Combine(root,"Resources/GameData/LunaMultiplayer/Plugins/LmpClient.dll")),"Duplicate approval and staging do not mutate the loaded copy");
        using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){}
        Check(!Directory.Exists(Path.Combine(root,"Resources/GameData/LunaMultiplayer")),"Approved duplicate no longer discoverable by KSP");
        Check(File.ReadAllText(Directory.GetFiles(Path.Combine(root,"APF/.disabled-install-copies"),"privatekey.txt",SearchOption.AllDirectories).Single())=="duplicate identity preserved","Disabled duplicate identity retained recoverably");
        Check(File.ReadAllText(Path.Combine(root,"Resources/unrelated.txt"))=="unrelated Resources retained","Duplicate repair preserves unrelated Resources files");
    }
    private static void TestDirectoryLeases()
    {
        string empty=Path.Combine(root,"empty-directory");Directory.CreateDirectory(empty);
        var method=typeof(PreparedPack).GetMethod("PlainDirectory",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
        try {
            method.Invoke(null,new object[]{root,empty});bool rejected=false;
            try{Directory.Move(empty,empty+"-moved");}catch(IOException){rejected=true;}
            Check(rejected,"Cached directory handle prevents replacement even without file locks");
            using(var write=OpenDirectoryForWrite(empty,0x40000000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero)) {
                int error=System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Check(write.IsInvalid&&error==32,"Directory lease blocks GENERIC_WRITE handles; attribute-only retagging is checked separately");
            }
        }finally{PreparedPack.Close();}
        Directory.Move(empty,empty+"-moved");Check(Directory.Exists(empty+"-moved"),"Directory lease release restores ordinary rename access");
        using(var write=OpenDirectoryForWrite(empty+"-moved",0x40000000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero))Check(!write.IsInvalid,"Directory lease release restores normal write-handle access");
        string link=Path.Combine(root,"directory-junction");
        using(var process=Process.Start(new ProcessStartInfo{FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),Arguments=ApfProcessArguments.Join(new[]{"/c","mklink","/J",link,empty+"-moved"}),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true})) {
            Check(process.WaitForExit(10000)&&process.ExitCode==0,"Owned junction safety fixture created without opening a window");
        }
        bool blocked=false;
        try{method.Invoke(null,new object[]{root,link});}
        catch(System.Reflection.TargetInvocationException error){blocked=error.InnerException is InvalidDataException;}
        finally{PreparedPack.Close();}
        Check(blocked,"Directory cache rejects a junction using opened-handle attributes");
        string scanLink=Path.Combine(root,"GameData","TestCore","empty-junction");
        using(var process=Process.Start(new ProcessStartInfo{FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),Arguments=ApfProcessArguments.Join(new[]{"/c","mklink","/J",scanLink,empty+"-moved"}),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true}))
            Check(process.WaitForExit(10000)&&process.ExitCode==0,"Owned empty scan-junction safety fixture created");
        bool scanBlocked=false;
        try{typeof(RuntimeFiles).GetMethod("ScanFiles").Invoke(null,new object[]{Path.Combine(root,"GameData"),Path.Combine(root,"GameData/TestCore")});}
        catch(System.Reflection.TargetInvocationException error){scanBlocked=error.InnerException is InvalidDataException;}
        Check(scanBlocked,"Scanner rejects an empty child junction before recursive traversal or hashing");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryForWrite(string path,uint access,uint sharing,IntPtr security,uint disposition,uint flags,IntPtr template);
    private static string BackupRoot {get{return Path.Combine(root,"APF/.transaction-backup");}}
    private static ApfRecoveryJournal ReadJournal()
    {
        return ApfJson.Deserialize<ApfRecoveryJournal>(File.ReadAllText(Directory.GetFiles(BackupRoot,"recovery.json",SearchOption.AllDirectories).Single()));
    }
    private static bool IsRemoval(ApfRecoveryRecord record)
    {
        return string.Equals(Path.GetFullPath(record.Target),Path.Combine(root,"GameData/PersonalAddon"),StringComparison.OrdinalIgnoreCase);
    }
    private static void CheckResult(string expected)
    {
        var result=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json")));
        Check(result["status"]==expected&&result["kind"]=="pack","Final installation result is "+expected);
    }
    private static bool LaunchRuns(string name)
    {
        using(var process=Process.Start(new ProcessStartInfo(Path.Combine(root,name)){UseShellExecute=false}))return process!=null&&process.WaitForExit(10000)&&process.ExitCode==0;
    }
    private static void TestCollision()
    {
        var transaction=MakeTransaction(true);
        transaction.Deletes.Add(new ApfDeleteChange{RelativePath="GameData/TestCore",Reason="Deliberate replacement/removal collision"});
        string request=Save(transaction);string[] before=Snapshot("GameData");
        bool rejected=false;
        try {using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){} }
        catch(InvalidDataException error){rejected=error.Message.Contains("cannot be replaced and permanently removed");}
        Check(rejected,"Replacement/removal collision rejected by the collision guard");
        Check(before.SequenceEqual(Snapshot("GameData")),"Collision rejected before managed files, permanent removal, or state mutation");
        Check(!Directory.Exists(BackupRoot),"Collision rejected before recovery journal or backup creation");
        CheckResult("failed");
        Check(LaunchRuns("KSP_x64.exe")&&LaunchRuns("KSP.exe"),"Collision failure releases both executable locks");
    }
    private static void TestBareDeletion()
    {
        var transaction=MakeTransaction(true);transaction.Deletes.Single().RelativePath="PersonalAddon";
        using(var completion=NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),Save(transaction),"pack")) {
            Check(!Directory.Exists(Path.Combine(root,"GameData/PersonalAddon"))&&File.ReadAllText(Path.Combine(root,"GameData/TestCore/example.cfg"))=="new managed configuration","Bare GameData-relative deletion installs successfully");
            var journal=ReadJournal();
            Check(journal.Status=="complete"&&!journal.Records.Any(IsRemoval)&&!Directory.GetFiles(BackupRoot,"remove.txt",SearchOption.AllDirectories).Any(),"Bare deletion has neither recovery record nor retained removed content");
            CheckResult("complete");
            Check(LaunchBlocked("KSP_x64.exe")&&LaunchBlocked("KSP.exe"),"Bare deletion completion retains both executable locks");
        }
        Check(LaunchRuns("KSP_x64.exe")&&LaunchRuns("KSP.exe"),"Closing bare deletion completion releases both executable locks");
    }
    private static void TestOverlappingRun()
    {
        string secondRequest;
        using(var completion=NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),Save(MakeTransaction(true)),"pack")) {
            secondRequest=Save(MakeTransaction(false));string[] before=Snapshot("GameData");
            string result=File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json"));
            bool rejected=false;
            try {using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),secondRequest,"pack")){} }
            catch(InvalidOperationException error){rejected=error.Message=="An installation is already active.";}
            Check(rejected,"Second Run rejected while the first completion remains undisposed");
            Check(File.Exists(secondRequest)&&!File.Exists(secondRequest+".consumed")&&!File.Exists(secondRequest+".ready"),"Rejected overlapping request remains unconsumed and unacknowledged");
            Check(before.SequenceEqual(Snapshot("GameData"))&&result==File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json")),"Rejected overlapping Run leaves installed content, state, and result unchanged");
            Check(LaunchBlocked("KSP_x64.exe")&&LaunchBlocked("KSP.exe"),"Rejected overlapping Run does not release the first completion's executable locks");
        }
        Check(LaunchRuns("KSP_x64.exe")&&LaunchRuns("KSP.exe"),"First completion disposal releases both executable locks");
        using(var completion=NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),secondRequest,"pack")) {
            Check(File.Exists(secondRequest+".consumed")&&LaunchBlocked("KSP_x64.exe")&&LaunchBlocked("KSP.exe"),"Previously rejected request runs after disposal and reacquires both locks");
            CheckResult("complete");
        }
    }
    private static void TestFinalScanRollback()
    {
        string[] managedBefore=Snapshot("GameData/TestCore");
        string stateBefore=File.ReadAllText(Path.Combine(data,"APFPackState.json"));
        string profileBefore=File.ReadAllText(Path.Combine(data,"KesasInstallProfile.json"));
        string settingsBefore=File.ReadAllText(Path.Combine(root,"settings.cfg"));
        var transaction=MakeTransaction(true);var staged=transaction.Packages.Single().Package;
        transaction.ExistingState=ApfJson.Deserialize<ApfPackState>(stateBefore);
        transaction.Manifest.ManagedPackages.Add(new ApfPackage{Id="missing-required",Version="1",ArchiveUrl=staged.ArchiveUrl,Sha256=staged.Sha256,SizeBytes=staged.SizeBytes,Destination="GameData",StripPrefix="MissingRequired"});
        bool rejected=false;
        string request=Save(transaction);PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        try {using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){} }
        catch(InvalidDataException error){rejected=error.Message.Contains("Pack verification failed after installation")&&error.Message.Contains("missing-required");}
        Check(rejected,"Declared but unstaged required package forces post-install final scan failure");
        Check(managedBefore.SequenceEqual(Snapshot("GameData/TestCore"))&&stateBefore==File.ReadAllText(Path.Combine(data,"APFPackState.json")),"Final scan failure restores managed content and prior state exactly");
        Check(!Directory.Exists(Path.Combine(root,"GameData/PersonalAddon")),"Final scan rollback does not restore the permanently removed extra mod");
        var journal=ReadJournal();
        Check(journal.Status=="restored"&&journal.Records.Count==2&&journal.Records.All(r=>r.Existed&&r.Restored)&&!journal.Records.Any(IsRemoval),"Restored journal covers only the replaced managed root and state");
        Check(Directory.GetFiles(BackupRoot,"example.cfg",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p)=="new managed configuration"),"Rollback retains undo evidence that replacement extraction completed");
        Check(!Directory.GetFiles(BackupRoot,"remove.txt",SearchOption.AllDirectories).Any(),"Failed installation retains no permanently removed mod content");
        Check(profileBefore==File.ReadAllText(Path.Combine(data,"KesasInstallProfile.json"))&&settingsBefore==File.ReadAllText(Path.Combine(root,"settings.cfg")),"Final scan failure does not commit graphics or profile changes");
        string log=File.ReadAllText(Path.Combine(root,"APF/APFTransaction.log"));
        Check(log.Contains("START pack=kesas")&&log.Contains("ROLLED-BACK pack=kesas")&&!log.Contains("COMPLETED packages="),"Final scan failure logs rollback without transaction completion");
        CheckResult("failed");
        Check(LaunchRuns("KSP_x64.exe")&&LaunchRuns("KSP.exe"),"Final scan rollback releases both executable locks");
    }
    private static bool LaunchBlocked(string name){try{using(var p=Process.Start(new ProcessStartInfo(Path.Combine(root,name)){UseShellExecute=false})){p.WaitForExit(10000);return false;}}catch(System.ComponentModel.Win32Exception e){return e.NativeErrorCode==32||e.NativeErrorCode==5;}}
    private static void TestGeneratedRuntimeFiles()
    {
        var transaction=MakeTransaction(true);
        string[] roots={"ContractConfigurator","KerbalEngineer","KSPCommunityFixes","ZZZ_KESASPerformanceFixes"};
        foreach(string name in roots) {
            string file=Write("GameData/"+name+"/required.cfg","required immutable content");
            var package=new ApfPackage{Id="runtime-"+name.ToLowerInvariant(),Version="1",ArchiveUrl="https://217.216.49.250:18866/unchanged.zip",Sha256=new string('a',64),SizeBytes=1,Destination="GameData",StripPrefix=name};
            transaction.Manifest.ManagedPackages.Add(package);
            transaction.ExistingState.Packages.Add(new ApfPackageState{Id=package.Id,Version=package.Version,ArchiveSha256=package.Sha256,Files=new List<ApfFileState>{new ApfFileState{Path="GameData/"+name.ToLowerInvariant()+"/required.cfg",Sha256=ApfJson.Sha256File(file)}}});
        }
        // These are created by the real game, not present in archive state.
        Write("GameData/ContractConfigurator/log/APC/FirstPlanetFlyby.log","generated contract log");
        Write("GameData/KerbalEngineer/KerbalEngineer.log","generated engineer log");
        Write("GameData/KerbalEngineer/Settings/GeneralSettings.xml","generated engineer preferences");
        Write("GameData/KSPCommunityFixes/PluginData/PNGTextureCache.cfg","textureCacheEnabled = true");
        Write("GameData/KSPCommunityFixes/PluginData/TextureCache/1225581161","generated raw texture cache");
        Write("GameData/KSPCommunityFixes/PluginData/TextureCache/textureData.json","{}");
        Write("GameData/KSPCommunityFixes/PluginData/TextureCache/progressMarker","");
        Write("GameData/ZZZ_KESASPerformanceFixes/PluginData/AssetBundleDefinitions.v1.cache","generated bundle cache");
        Write("GameData/ZZZ_KESASPerformanceFixes/PluginData/AssetBundleDefinitions.v1.cache.tmp","interrupted temporary cache");
        string stateBefore=ApfJson.Serialize(transaction.ExistingState);
        File.WriteAllText(Path.Combine(data,"APFPackState.json"),stateBefore);
        if(legacyMigration) {
            // Existing clients run their already-loaded RC25 helper. Force the
            // four repaired roots through prepared replacement in this commit.
            foreach(var package in transaction.Manifest.ManagedPackages.Where(p=>p.Id.StartsWith("runtime-",StringComparison.Ordinal))) {
                string archive=Path.Combine(root,"APF/.staging",package.Id+".zip");
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)) {
                    using(var writer=new StreamWriter(zip.CreateEntry(package.StripPrefix+"/required.cfg").Open()))writer.Write("required immutable content");
                    if(package.StripPrefix=="KSPCommunityFixes")using(var writer=new StreamWriter(zip.CreateEntry(package.StripPrefix+"/PluginData/PNGTextureCache.cfg").Open()))writer.Write("textureCacheEnabled = true");
                }
                package.Version="1-repair26";package.Sha256=ApfJson.Sha256File(archive);package.SizeBytes=new FileInfo(archive).Length;
                transaction.ExistingState.Packages.Single(p=>p.Id==package.Id).ArchiveSha256=package.Sha256;
                transaction.Packages.Add(new ApfStagedPackage{Package=package,ArchivePath=archive});
            }
            Check(transaction.Packages.Where(p=>p.Package.Id.StartsWith("runtime-",StringComparison.Ordinal)).All(p=>transaction.ExistingState.Packages.Any(s=>s.Id==p.Package.Id&&s.ArchiveSha256==p.Package.Sha256&&s.Version!=p.Package.Version)),"Migration forces replacements by version only; existing archive content hashes are unchanged");
            File.WriteAllText(Path.Combine(data,"APFPackState.json"),ApfJson.Serialize(transaction.ExistingState));
        }
        string request=Save(transaction);PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        if(legacyMigration) {
            var envelope=ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(request+".prepared"));
            var prepared=ApfJson.Deserialize<PreparedReceipt>(envelope.Payload);
            Check(prepared.Packages.Single(p=>p.Root=="kspcommunityfixes").Files.Any(f=>f.Path.EndsWith("/plugindata/pngtexturecache.cfg",StringComparison.OrdinalIgnoreCase)),"Archive/prepared inventory still authenticates runtime CFG bytes rather than excluding them globally");
        }
        if(legacyRuntime) {
            bool rejected=false;
            try{using(NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")){} }
            catch(InvalidDataException error){rejected=error.Message.Contains("Pack verification failed after installation")&&roots.All(name=>error.Message.Contains("runtime-"+name.ToLowerInvariant()));}
            Check(rejected,"RC25 reproduces all four observed runtime-file package mismatches");
            Check(File.ReadAllText(Path.Combine(root,"GameData/TestCore/example.cfg"))=="previous managed configuration","RC25 reproduction really rolls back the newly installed managed package");
            CheckResult("failed");return;
        }
        using(var completion=NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack")) {
            if(legacyMigration) {
                completion.Restart();
                Check(completion.RestartedProcessId>0,"Already-loaded RC25 helper completes repaired roots in one approved restart");
                bool twice=false;try{completion.Restart();}catch(InvalidOperationException){twice=true;}
                Check(twice,"Successful migration cannot start a second restart");
            }
        }
        Check(File.ReadAllText(Path.Combine(root,"GameData/TestCore/example.cfg"))=="new managed configuration","RC26 commits despite all four real mod-generated file inventories");
        CheckResult("complete");
        Check(!File.ReadAllText(Path.Combine(root,"APF/APFTransaction.log")).Contains("ROLLED-BACK"),"Generated runtime files do not cause a hidden rollback");
        if(legacyMigration) {
            Check(!File.Exists(Path.Combine(root,"GameData/KerbalEngineer/KerbalEngineer.log")),"Legacy migration replaces the offending runtime inventory before final verification");
            Check(Directory.GetFiles(BackupRoot,"KerbalEngineer.log",SearchOption.AllDirectories).Any(),"Replaced managed roots retain safe recovery, unlike permanently removed extra mods");
            return;
        }
        var state=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(data,"APFPackState.json")));
        Check(RuntimeFiles.ScanFinal(Path.Combine(root,"GameData"),transaction.Manifest,state).IsClean,"Reconnecting after generated game state remains clean");
        using(var cache=new FileStream(Path.Combine(root,"GameData/KSPCommunityFixes/PluginData/TextureCache/1225581161"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Check(RuntimeFiles.ScanFinal(Path.Combine(root,"GameData"),transaction.Manifest,state).IsClean,"Scanner never opens or hashes an exclusively locked generated texture cache");
        Write("GameData/KSPCommunityFixes/PluginData/TextureCache/plugin.dll","untrusted added assembly");
        Check(!RuntimeFiles.ScanFinal(Path.Combine(root,"GameData"),transaction.Manifest,state).IsClean,"Texture cache exception never exempts an added DLL");
        File.Delete(Path.Combine(root,"GameData/KSPCommunityFixes/PluginData/TextureCache/plugin.dll"));
        Write("GameData/ContractConfigurator/required.cfg","tampered required definition");
        Check(!RuntimeFiles.ScanFinal(Path.Combine(root,"GameData"),transaction.Manifest,state).IsClean,"Generated log exceptions do not exempt changed required configuration");
        foreach(string path in new[]{"GameData/ContractConfigurator/log/injected.cfg","GameData/KSPCommunityFixes/PluginData/TextureCache/4294967296","GameData/KSPCommunityFixes/PluginData/TextureCache/sub/123","GameData/KSPCommunityFixes/PluginData/TextureCache/123.dll","GameData/KSPCommunityFixes/PluginData/arbitrary.cfg","GameData/ZZZ_KESASPerformanceFixes/PluginData/plugin.dll","GameData/ZZZ_KESASPerformanceFixes/PluginData/arbitrary.cache"})
            Check(!RuntimeFiles.IsMutable(path),"Runtime rule remains narrow: "+path);
    }
    private static void TestFlatFilePackages()
    {
        string gameData=Path.Combine(root,"GameData"),path=Path.Combine(gameData,"ModuleManager.4.2.3.dll");
        File.WriteAllText(path,"flat-file package content");
        var files=ScanFlatPackage(gameData,path);
        Check(files.Count==1&&files[0].Path=="GameData/modulemanager.4.2.3.dll","Flat GameData file packages are scanned, not treated as missing directories");
        Check(files[0].Sha256==ApfJson.Sha256File(path),"Flat file package bytes remain hashed");
        File.AppendAllText(path," modified");
        Check(!RuntimeFiles.SameFiles(files,ScanFlatPackage(gameData,path)),"Flat root DLL modifications remain detectable");
        File.Delete(path);
        Check(ScanFlatPackage(gameData,path).Count==0,"A genuinely missing flat package is reported missing");
    }
    // Reflection preserves compilation against the unchanged old helper for
    // --runtime-repro controls; those modes do not execute this new scanner test.
    private static List<ApfFileState> ScanFlatPackage(string gameData,string path)
    {return (List<ApfFileState>)typeof(RuntimeFiles).GetMethod("ScanFiles").Invoke(null,new object[]{gameData,path});}
    private static ApfSyncTransaction MakeTransaction(bool remove)
    {
        string stage=Path.Combine(root,"APF/.staging",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);string archive=Path.Combine(stage,"test.zip");
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry("TestCore/example.cfg").Open()))writer.Write("new managed configuration");
        var package=new ApfPackage{Id="testcore",Version="1",ArchiveUrl="https://217.216.49.250:18866/test.zip",Sha256=ApfJson.Sha256File(archive),SizeBytes=new FileInfo(archive).Length,Destination="GameData",StripPrefix="TestCore"};
        var manifest=new ApfPackManifest{ServerId="kesas-b6286df413b9426f",PackId="kesas",PackVersion="kesas-rc24",RequiredClientVersion="kesas-rc24",KspVersion="1.12.5",ProtectedRoots=new List<string>{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony"},ManagedPackages=new List<ApfPackage>{package},ClientOnly=new List<ApfClientOnlyRule>()};
        return new ApfSyncTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),KspExecutable=Path.Combine(root,"KSP_x64.exe"),ProcessId=0,KspArguments=new List<string>{"--headless-fixture-"+Guid.NewGuid().ToString("N")},StatePath=Path.Combine(data,"APFPackState.json"),Manifest=manifest,ExistingState=new ApfPackState(),Packages=new List<ApfStagedPackage>{new ApfStagedPackage{Package=package,ArchivePath=archive}},Deletes=remove?new List<ApfDeleteChange>{new ApfDeleteChange{RelativePath="GameData/PersonalAddon",Reason="Explicitly approved test removal"}}:new List<ApfDeleteChange>()};
    }
    private static string Save(ApfSyncTransaction transaction,bool sign=true){string path=Path.Combine(root,"APF/.staging",Guid.NewGuid().ToString("N"),"transaction.json");Directory.CreateDirectory(Path.GetDirectoryName(path));if(sign)ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data,"APFHelper.key")));File.WriteAllText(path,ApfJson.Serialize(transaction));return path;}
}
