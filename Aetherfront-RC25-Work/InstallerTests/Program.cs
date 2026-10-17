using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static int checks;
    private static string root,data,catalogPath,profilePath,resultPath;
    private static Installer installer;
    private static InstallCatalog catalog;
    private static ApfPackManifest pack;
    private static ApfClientUpdateManifest feed;
    private static ApfSyncTransaction transaction;
    private static byte[] helperKey;
    private static readonly BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void EnsureRuntimeLibraries()
    {
        string output=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string baseline=Path.GetFullPath("Aetherfront-RC24-Work/Candidate/Client/KSP_x64_Data/Managed");
        foreach(string name in new[]{"System.IO.Compression.dll","System.IO.Compression.FileSystem.dll","System.Numerics.dll"})
            if(!File.Exists(Path.Combine(output,name)))File.Copy(Path.Combine(baseline,name),Path.Combine(output,name));
    }
    private static object Call(string name,params object[] arguments)
    {
        try{return typeof(Installer).GetMethod(name,hidden).Invoke(installer,arguments);}
        catch(TargetInvocationException error){throw error.InnerException;}
    }
    private static void Check(bool value,string message)
    {
        if(!value)throw new Exception(message);
        checks++;Console.WriteLine("PASS "+message);
    }
    private static string Write(string relative,string content)
    {
        string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,content);return path;
    }
    private static void Complete(){Installer.WriteCompletedReceipt(transaction,"full");}
    private static bool Matches(string mode="full",ApfPackManifest manifest=null)
    {
        return (bool)Call("CompletedPackMatches",mode,catalog,manifest??pack);
    }
    private static void SetVerified(ApfClientUpdateManifest manifest)
    {
        typeof(Installer).GetField("verifiedClientManifest",hidden).SetValue(installer,manifest);
    }
    private static void SaveReceipt(Dictionary<string,string> receipt,bool authenticate)
    {
        if(authenticate)receipt["authorization"]=(string)typeof(Installer).GetMethod("ReceiptAuthorization",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{receipt,helperKey});
        File.WriteAllText(resultPath,ApfJson.Serialize(receipt));
    }
    private static void ConnectionChecks()
    {
        string gameData=Path.Combine(root,"GameData");
        string feedPath=Path.Combine(root,"APF/.client-update-staging/connect/feed.json");
        string serverPath=Write("APF/.client-update-staging/connect/server-pack.json",pack.ToJson());
        string statePath=Path.Combine(data,"APFPackState.json");
        var state=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(statePath));
        Complete();
        var failures=new List<string>();
        Action<string,Func<bool>> verify=(name,test)=>{
            try{Check(test(),name);}
            catch(Exception error){failures.Add(name+": "+error.GetType().FullName+": "+error.Message);Console.WriteLine("FAIL "+failures.Last());}
        };
        Func<ApfPackManifest,ApfPackState,bool> fallback=(server,installed)=>{
            ApfSyncPlan expected=null;Exception expectedFailure=null;
            try{expected=ApfPackScanner.Scan(gameData,server,installed);}catch(Exception error){expectedFailure=error;}
            try
            {
                var actual=Installer.ScanConnection(gameData,server,installed);
                return expectedFailure==null&&!actual.IsClean&&ApfJson.Serialize(actual)==ApfJson.Serialize(expected);
            }
            catch(Exception error)
            {
                if(expectedFailure!=null&&error.GetType()==expectedFailure.GetType()&&error.Message==expectedFailure.Message)return true;
                throw;
            }
        };
        verify("Connection fixture normal scanner detects modified package and manual addon",()=>{
            var normal=ApfPackScanner.Scan(gameData,pack,state);return !normal.IsClean&&normal.Install.Count>0&&normal.Delete.Count>0;
        });
        string originalFeed=File.ReadAllText(feedPath),originalServer=File.ReadAllText(serverPath),originalCatalog=File.ReadAllText(catalogPath),originalProfile=File.ReadAllText(profilePath),originalReceipt=File.ReadAllText(resultPath);
        try
        {
            string before=string.Join("|",new[]{feedPath,serverPath,catalogPath,profilePath,resultPath,statePath}.Select(ApfJson.Sha256File));
            var first=Installer.ScanConnection(gameData,pack,state);
            verify("Matching completed connection receipt returns clean plan without scanning changed files",()=>first.IsClean&&first.PackId==pack.PackId&&first.PackVersion==pack.PackVersion);
            first.Errors.Add("test-only mutation of previously returned plan");
            var second=Installer.ScanConnection(gameData,pack,state);
            verify("Connection fast path returns fresh independent plan each time",()=>second.IsClean&&!ReferenceEquals(first,second)&&second.Errors.Count==0);
            verify("Connection fast path never rewrites installed or receipt metadata",()=>before==string.Join("|",new[]{feedPath,serverPath,catalogPath,profilePath,resultPath,statePath}.Select(ApfJson.Sha256File)));
            foreach(string status in new[]{"failed","applying","staging"})
            {
                var receipt=ApfJson.Deserialize<Dictionary<string,string>>(originalReceipt);receipt["status"]=status;SaveReceipt(receipt,true);
                verify("Connection "+status+" receipt falls back to normal scanner",()=>fallback(pack,state));
            }
            File.WriteAllText(resultPath,originalReceipt);
            var tampered=ApfJson.Deserialize<Dictionary<string,string>>(originalReceipt);tampered["authorization"]="invalid";SaveReceipt(tampered,false);
            verify("Connection invalid receipt HMAC falls back",()=>fallback(pack,state));File.WriteAllText(resultPath,originalReceipt);
            File.WriteAllText(profilePath,"{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc25\"}");
            verify("Connection changed committed profile falls back",()=>fallback(pack,state));File.WriteAllText(profilePath,originalProfile);
            var otherState=ApfJson.Deserialize<ApfPackState>(ApfJson.Serialize(state));otherState.PackVersion="kesas-rc26";
            verify("Connection changed state revision falls back without throwing",()=>fallback(pack,otherState));
            verify("Connection missing state falls back without throwing",()=>fallback(pack,null));
            var otherServer=ApfPackManifest.FromJson(pack.ToJson());otherServer.PackVersion="kesas-rc26";
            verify("Connection changed server revision falls back without throwing",()=>fallback(otherServer,state));
            otherServer=ApfPackManifest.FromJson(pack.ToJson());otherServer.ServerId="another-server";
            verify("Connection changed server identity falls back without throwing",()=>fallback(otherServer,state));
            otherServer=ApfPackManifest.FromJson(pack.ToJson());otherServer.ManagedPackages[0].Sha256=new string('c',64);
            File.WriteAllText(serverPath,otherServer.ToJson());
            verify("Connection saved server-pack must match exact handshake manifest",()=>fallback(pack,state));File.WriteAllText(serverPath,originalServer);
            var invalidFeed=ApfClientUpdateManifest.FromJson(originalFeed);invalidFeed.ReleaseNotes+=" invalid signature";File.WriteAllText(feedPath,invalidFeed.ToJson());
            verify("Connection invalid client signature falls back without throwing",()=>fallback(pack,state));File.WriteAllText(feedPath,originalFeed);
            File.AppendAllText(catalogPath," ");verify("Connection altered signed catalog falls back",()=>fallback(pack,state));File.WriteAllText(catalogPath,originalCatalog);
            foreach(string path in new[]{feedPath,serverPath,catalogPath,profilePath})
            {
                string contents=File.ReadAllText(path);File.WriteAllText(path,"{");
                verify("Connection malformed "+Path.GetFileName(path)+" falls back without throwing",()=>fallback(pack,state));File.WriteAllText(path,contents);
            }
            string duplicate=Write("Resources/GameData/LunaMultiplayer/Plugins/LmpClient.dll","connection duplicate fixture; never loaded");
            verify("Connection exact nested duplicate rejects fast path",()=>fallback(pack,state));File.Move(duplicate,duplicate+".connection-disabled");
        }
        finally
        {
            File.WriteAllText(feedPath,originalFeed);File.WriteAllText(serverPath,originalServer);File.WriteAllText(catalogPath,originalCatalog);File.WriteAllText(profilePath,originalProfile);File.WriteAllText(resultPath,originalReceipt);
        }
        if(failures.Count>0)throw new InvalidDataException("ScanConnection regression failures: "+string.Join("; ",failures));
    }
    private static void DuplicateChecks()
    {
        Complete();
        string duplicate=Write("Resources/GameData/LunaMultiplayer/Plugins/LmpClient.dll","duplicate fixture; never loaded");
        Check(!Matches(),"Exact nested duplicate DLL rejects completed fast path");
        int prompts=0;installer.Confirm=message=>{prompts++;return false;};bool canceled=false;
        try{Call("ConfirmDuplicateClient");}catch(OperationCanceledException){canceled=true;}
        Check(canceled&&prompts==1&&File.Exists(duplicate),"Rejecting duplicate repair preserves nested installation");
        installer.Confirm=message=>{prompts++;return true;};
        Check((bool)Call("ConfirmDuplicateClient")&&prompts==2&&File.Exists(duplicate),"Approved duplicate repair only plans; never mutates loaded installation");
        bool rejected=false;try{Complete();}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Success receipt cannot be written while duplicate remains enabled");
        File.Move(duplicate,duplicate+".fixture-disabled");
        Check(Matches(),"Nested folder alone does not block fast path without exact duplicate DLL");
    }
    private static void BundleChecks()
    {
        string before=ApfJson.Sha256File(catalogPath);
        var next=ApfJson.Deserialize<InstallCatalog>(ApfJson.Serialize(catalog));next.Version="kesas-rc26";
        foreach(var selected in new[]{next.Full,next.Potato}){selected.PackVersion=next.Version;selected.RequiredClientVersion=next.Version;}
        byte[] catalogBytes=new UTF8Encoding(false).GetBytes(ApfJson.Serialize(next));
        const string relative="GameData/LunaMultiplayer/Launcher/install-catalog.json";
        string archive=Path.Combine(root,"APF/.client-update-staging/bundle-fixture/client.zip");Directory.CreateDirectory(Path.GetDirectoryName(archive));
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
        using(var stream=zip.CreateEntry(relative).Open())stream.Write(catalogBytes,0,catalogBytes.Length);
        var nextFeed=ApfClientUpdateManifest.FromJson(feed.ToJson());nextFeed.Version=next.Version;nextFeed.PackageUrl="https://127.0.0.1:18866/updates/kesas-rc26/client.zip";
        nextFeed.SizeBytes=new FileInfo(archive).Length;nextFeed.Sha256=ApfJson.Sha256File(archive);
        string targetCatalog=Write("APF/.client-update-staging/bundle-fixture/catalog-source.json",Encoding.UTF8.GetString(catalogBytes));
        nextFeed.Files=new List<ApfClientUpdateFile>{new ApfClientUpdateFile{Path=relative,SizeBytes=catalogBytes.Length,Sha256=ApfJson.Sha256File(targetCatalog)}};
        using(var signing=new RSACryptoServiceProvider(2048))
        {
            Write("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml",signing.ToXmlString(false));
            ApfClientUpdateSecurity.Sign(nextFeed,signing);
        }
        string signedFeed=Write("APF/.client-update-staging/bundle-fixture/feed.json",nextFeed.ToJson());
        SetVerified((ApfClientUpdateManifest)Call("ReadClientManifest",signedFeed));
        string plugins=Path.Combine(data,"..","Plugins");Directory.CreateDirectory(plugins);
        string baseline=Path.GetFullPath("Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins");
        foreach(string name in new[]{"LmpClient.dll","ApfCore.dll"})File.Copy(Path.Combine(baseline,name),Path.Combine(plugins,name));
        Write("GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt","https://127.0.0.1:18866/updates/APFClientUpdate.json");
        Write("GameData/LunaMultiplayer/Data/APFClient.json","{\"enabled\":true,\"clientVersion\":\"kesas-rc25\"}");
        var client=new ApfClientUpdateTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),ProcessId=Process.GetCurrentProcess().Id,KspExecutable=Path.Combine(root,"KSP_x64.exe"),KspArguments=new List<string>(),ArchivePath=archive,Manifest=nextFeed,Approval=ApfClientUpdateApproval.Capture(root)};
        ApfTransactionSecurity.Sign(client,helperKey);
        string sourceRequest=Write("APF/.client-update-staging/bundle-fixture/request/transaction.json",ApfJson.Serialize(client));
        var pending=new InstallResult{Changed=true,ClientUpdate=true,TransactionPath=sourceRequest};
        string staged=(string)Call("StageClientCatalog",pending);
        Check(ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(staged)).Version=="kesas-rc26","Future client signed catalog extracted into private staging");
        Check(ApfJson.Sha256File(catalogPath)==before,"Catalog staging never replaces installed catalog while loaded");
        Check(Directory.GetFiles(Path.GetDirectoryName(staged)).Select(Path.GetFileName).OrderBy(s=>s).SequenceEqual(new[]{"target-catalog.json","transaction.json"}),"Only signed catalog extracted beside pending client request");
        bool canceled=false;installer.IsCanceled=()=>true;
        try{Call("StageClientCatalog",pending);}catch(OperationCanceledException){canceled=true;}
        installer.IsCanceled=()=>false;Check(canceled&&ApfJson.Sha256File(catalogPath)==before,"Catalog staging honors cancellation without loaded-file mutation");
        var combined=new ApfSyncTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),ProcessId=client.ProcessId,KspExecutable=client.KspExecutable,KspArguments=new List<string>(),StatePath=Path.Combine(data,"APFPackState.json"),Manifest=next.Full,ExistingState=new ApfPackState(),Packages=new List<ApfStagedPackage>(),Deletes=new List<ApfDeleteChange>()};
        ApfTransactionSecurity.Sign(combined,helperKey);
        string request=Write("APF/.staging/bundle-fixture/transaction.json",ApfJson.Serialize(combined));
        Call("WriteBundledClient",pending,request);
        Check(File.ReadAllText(request+".client")==File.ReadAllText(sourceRequest),"Combined pack sidecar preserves original signed client request");
        var bundled=ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(request+".client"));
        Check(bundled.ProcessId==client.ProcessId&&ApfTransactionSecurity.Verify(bundled,helperKey),"Client authorization and original game PID retained before helper authentication");
        PreparedPack.Prepare(combined,request,message=>{},()=>false);
        var envelope=ApfJson.Deserialize<PreparedEnvelope>(File.ReadAllText(request+".prepared"));
        var prepared=ApfJson.Deserialize<PreparedReceipt>(envelope.Payload);
        Check(prepared.Packages.Count==0&&prepared.BundledClientSha256==ApfJson.Sha256File(request+".client"),"Zero-package combined update still prepares authenticated client bundle");
        PreparedPack.Open(combined,request);
        try
        {
            bool locked=false;
            try{using(new FileStream(archive,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){} }catch(IOException){locked=true;}
            Check(locked,"Helper preflight retains client archive write-denying lock");
            Check(ApfJson.Sha256File(catalogPath)==before,"Combined preflight never applies client or pack while loaded");
        }
        finally{PreparedPack.Close();}
        string sidecar=File.ReadAllText(request+".client");File.AppendAllText(request+".client"," ");bool rejected=false;
        try{PreparedPack.Open(combined,request);}catch(InvalidDataException){rejected=true;}
        finally{PreparedPack.Close();}
        Check(rejected,"Changed bundled client sidecar rejected before ready");File.WriteAllText(request+".client",sidecar);
        client.Authorization="invalid";File.WriteAllText(sourceRequest,ApfJson.Serialize(client));rejected=false;
        try{Call("StageClientCatalog",pending);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Invalid pending client authorization rejected before extraction");
    }
    private static void NativeCommitChecks()
    {
        string fixture=Path.GetFullPath("Aetherfront-RC25-Work/InstallerCommitFixture-"+Guid.NewGuid().ToString("N"));
        string worker=Path.Combine(fixture,"worker");Directory.CreateDirectory(worker);
        string executable=Assembly.GetExecutingAssembly().Location;
        string target=Path.Combine(worker,Path.GetFileName(executable));File.Copy(executable,target);
        if(File.Exists(executable+".config"))File.Copy(executable+".config",target+".config");
        File.Copy(Path.GetFullPath("Aetherfront-RC25-Work/Patched/ApfCore.dll"),Path.Combine(worker,"ApfCore.dll"));
        File.Copy(Path.GetFullPath("Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/JsonFx.dll"),Path.Combine(worker,"JsonFx.dll"));
        File.WriteAllText(Path.Combine(fixture,"owned-test-fixture.txt"),fixture);
        bool mono=Type.GetType("Mono.Runtime")!=null;
        var start=new ProcessStartInfo{FileName=mono?Path.GetFullPath("Aetherfront-RC22-Work/MonoHost/bin/Release/net472/MonoHost.exe"):target,Arguments=mono?ApfProcessArguments.Join(new[]{target}):"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Directory.GetCurrentDirectory()};
        start.EnvironmentVariables["KESAS_INSTALLER_COMMIT_FIXTURE"]=fixture;
        using(var process=Process.Start(start))
        {
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(60000)){process.Kill();throw new TimeoutException("Owned isolated commit worker exceeded 60 seconds.");}
            Console.Write(output.GetAwaiter().GetResult());Console.Error.Write(error.GetAwaiter().GetResult());
            Check(process.ExitCode==0&&File.Exists(Path.Combine(fixture,"commit-passed.json")),"Isolated patched RC25 NativeRun commits client, prepared pack, duplicate repair and explicit GameStub restart");
        }
    }
    private static int CommitWorker(string fixture)
    {
        string workspace=Path.GetFullPath("Aetherfront-RC25-Work");root=Path.GetFullPath(fixture);
        if(!ApfPaths.IsUnder(root,workspace)||!Path.GetFileName(root).StartsWith("InstallerCommitFixture-",StringComparison.Ordinal)||File.ReadAllText(Path.Combine(root,"owned-test-fixture.txt"))!=root)throw new InvalidDataException("Commit worker target is not its owned fixture.");
        ApfPaths.EnsureNoReparsePoints(workspace,root);
        data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(data);Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
        string stub=Path.GetFullPath("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe");
        File.Copy(stub,Path.Combine(root,"KSP_x64.exe"));File.Copy(stub,Path.Combine(root,"KSP.exe"));
        string plugins=Path.Combine(root,"GameData/LunaMultiplayer/Plugins");Directory.CreateDirectory(plugins);
        foreach(string name in new[]{"LmpClient.dll","ApfCore.dll"})File.Copy(Path.GetFullPath("Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins/"+name),Path.Combine(plugins,name));
        Write("GameData/LunaMultiplayer/Data/APFServer.txt","127.0.0.1:18865");
        Write("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt",new string('a',64));
        Write("GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt","https://127.0.0.1:18866/updates/APFClientUpdate.json");
        Write("GameData/LunaMultiplayer/Data/APFClient.json","{\"enabled\":true,\"clientVersion\":\"kesas-rc25\"}");
        Write("GameData/LunaMultiplayer/Data/privatekey.txt","canonical identity sentinel");Write("saves/Fixture/persistent.sfs","save sentinel");
        Write("settings.cfg","ANTI_ALIASING = 2\nKEY_BINDING = retained\n");
        Write("Resources/GameData/LunaMultiplayer/Plugins/LmpClient.dll","disabled duplicate sentinel");
        Write("Resources/GameData/LunaMultiplayer/Data/privatekey.txt","duplicate identity sentinel");
        Write("Resources/Unrelated/keep.txt","unrelated resources sentinel");
        string installed=Write("GameData/TestCore/example.cfg","old managed config");
        string archive=Path.Combine(root,"APF/.staging/commit/package.zip");Directory.CreateDirectory(Path.GetDirectoryName(archive));
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry("TestCore/example.cfg").Open()))writer.Write("new managed config");
        var package=new ApfPackage{Id="testcore",Version="1",ArchiveUrl="https://127.0.0.1:18866/package.zip",Sha256=ApfJson.Sha256File(archive),SizeBytes=new FileInfo(archive).Length,Destination="GameData",StripPrefix="TestCore"};
        pack=new ApfPackManifest{ServerId="kesas-b6286df413b9426f",PackId="kesas",PackVersion="kesas-rc26",RequiredClientVersion="kesas-rc26",KspVersion="1.12.5",ProtectedRoots=new List<string>{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony"},ManagedPackages=new List<ApfPackage>{package},ClientOnly=new List<ApfClientOnlyRule>()};
        var next=new InstallCatalog{Version="kesas-rc26",Full=pack,Potato=ApfPackManifest.FromJson(pack.ToJson())};
        catalog=ApfJson.Deserialize<InstallCatalog>(ApfJson.Serialize(next));catalog.Version="kesas-rc25";
        foreach(var selected in new[]{catalog.Full,catalog.Potato}){selected.PackVersion="kesas-rc25";selected.RequiredClientVersion="kesas-rc25";}
        catalogPath=Write("GameData/LunaMultiplayer/Launcher/install-catalog.json",ApfJson.Serialize(catalog));
        Write("GameData/LunaMultiplayer/Data/KesasInstallProfile.json","{\"mode\":\"full\",\"catalogVersion\":\"kesas-rc25\"}");
        var previous=new ApfPackState{PackId="kesas",PackVersion="kesas-rc25",Packages=new List<ApfPackageState>{new ApfPackageState{Id="testcore",Version="0",ArchiveSha256=new string('b',64),Files=new List<ApfFileState>{new ApfFileState{Path="GameData/TestCore/example.cfg",Sha256=ApfJson.Sha256File(installed)}}}}};
        string statePath=Write("GameData/LunaMultiplayer/Data/APFPackState.json",ApfJson.Serialize(previous));
        helperKey=ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data,"APFHelper.key"));
        string clientArchive=Path.Combine(root,"APF/.client-update-staging/commit/client.zip");Directory.CreateDirectory(Path.GetDirectoryName(clientArchive));
        byte[] bytes=new UTF8Encoding(false).GetBytes(ApfJson.Serialize(next));const string relative="GameData/LunaMultiplayer/Launcher/install-catalog.json";
        using(var zip=ZipFile.Open(clientArchive,ZipArchiveMode.Create))using(var stream=zip.CreateEntry(relative).Open())stream.Write(bytes,0,bytes.Length);
        string staged=Write("APF/.client-update-staging/commit/catalog.json",Encoding.UTF8.GetString(bytes));
        feed=ApfClientUpdateManifest.FromJson(File.ReadAllText("Aetherfront-RC24-Work/Candidate/Published/APFClientUpdate.json"));feed.Version="kesas-rc26";feed.PackageUrl="https://127.0.0.1:18866/updates/kesas-rc26/client.zip";feed.SizeBytes=new FileInfo(clientArchive).Length;feed.Sha256=ApfJson.Sha256File(clientArchive);
        feed.Files=new List<ApfClientUpdateFile>{new ApfClientUpdateFile{Path=relative,SizeBytes=bytes.Length,Sha256=ApfJson.Sha256File(staged)}};
        using(var signing=new RSACryptoServiceProvider(2048)){Write("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml",signing.ToXmlString(false));ApfClientUpdateSecurity.Sign(feed,signing);}
        Write("APF/.client-update-staging/connect/feed.json",feed.ToJson());
        var client=new ApfClientUpdateTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),ProcessId=0,KspExecutable=Path.Combine(root,"KSP_x64.exe"),KspArguments=new List<string>(),ArchivePath=clientArchive,Manifest=feed,Approval=ApfClientUpdateApproval.Capture(root)};
        ApfTransactionSecurity.Sign(client,helperKey);
        transaction=new ApfSyncTransaction{GameRoot=root,GameDataPath=client.GameDataPath,ProcessId=0,KspExecutable=client.KspExecutable,KspArguments=new List<string>(),StatePath=statePath,Manifest=pack,ExistingState=previous,Packages=new List<ApfStagedPackage>{new ApfStagedPackage{Package=package,ArchivePath=archive}},Deletes=new List<ApfDeleteChange>()};
        ApfTransactionSecurity.Sign(transaction,helperKey);
        string request=Write("APF/.staging/commit/request/transaction.json",ApfJson.Serialize(transaction));
        File.WriteAllText(request+".client",ApfJson.Serialize(client));File.WriteAllText(request+".duplicate","true");
        using(var game=Process.Start(new ProcessStartInfo(client.KspExecutable){UseShellExecute=false,WorkingDirectory=root}))Check(game.WaitForExit(10000)&&game.ExitCode==0,"Real GameStub exits before helper commit");
        PreparedPack.Prepare(transaction,request,Console.WriteLine,()=>false);
        Check(File.ReadAllText(installed)=="old managed config"&&ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath)).Version=="kesas-rc25","Combined preparation leaves installed client catalog and pack unchanged");
        using(var completion=NativeInstall.Run(Path.Combine(root,"GameData/LunaMultiplayer"),request,"pack"))
        {
            Check(File.ReadAllText(installed)=="new managed config","NativeRun commits prepared package bytes");
            Check(ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(catalogPath)).Version=="kesas-rc26","Same NativeRun commits bundled signed client catalog");
            var result=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"APF/LastInstallResult.json")));
            Check(result["status"]=="complete"&&result["clientVersion"]=="kesas-rc26"&&result["packVersion"]=="kesas-rc26","Combined helper writes rich target-revision success receipt last");
            Check(File.Exists(request+".consumed")&&File.Exists(request+".ready"),"Single pack runner authenticates and consumes combined transaction");
            Check(!Directory.Exists(Path.Combine(root,"Resources/GameData/LunaMultiplayer")),"Approved exact duplicate installation disabled after exit");
            string disabled=Directory.GetFiles(Path.Combine(root,"APF/.disabled-install-copies"),"privatekey.txt",SearchOption.AllDirectories).Single();
            Check(File.ReadAllText(disabled)=="duplicate identity sentinel","Disabled duplicate identity retained in recoverable folder");
            Check(File.ReadAllText(Path.Combine(root,"Resources/Unrelated/keep.txt"))=="unrelated resources sentinel","Unrelated Resources tree remains unchanged");
            Check(File.ReadAllText(Path.Combine(data,"privatekey.txt"))=="canonical identity sentinel"&&File.ReadAllText(Path.Combine(root,"saves/Fixture/persistent.sfs"))=="save sentinel","Combined commit preserves canonical identity and saves");
            Check(completion.RestartedProcessId==0,"Completion reports no restart before explicit request");
            completion.Restart();Check(completion.RestartedProcessId>0,"Explicit restart launches only owned real GameStub");
        }
        using(var game=Process.Start(new ProcessStartInfo(client.KspExecutable){UseShellExecute=false,WorkingDirectory=root}))Check(game.WaitForExit(10000)&&game.ExitCode==0,"Completion disposal releases executable locks");
        Write("commit-passed.json",ApfJson.Serialize(new{checks,runtime=Type.GetType("Mono.Runtime")==null?"CLR":"KSP-Mono",headlessOnly=true}));
        Console.WriteLine("PASS native combined commit checks="+checks+" fixture="+root);return 0;
    }
    private static int Main()
    {
        try
        {
            EnsureRuntimeLibraries();
            string commitFixture=Environment.GetEnvironmentVariable("KESAS_INSTALLER_COMMIT_FIXTURE");
            if(!string.IsNullOrEmpty(commitFixture))return CommitWorker(commitFixture);
            root=Path.GetFullPath("Aetherfront-RC25-Work/InstallerFixture-"+Guid.NewGuid().ToString("N"));
            data=Path.Combine(root,"GameData/LunaMultiplayer/Data");
            Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
            Write("KSP_x64.exe","Never executed: isolated receipt fixture.");
            Write("GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt",new string('a',64));
            string config=Write("GameData/TestCore/example.cfg","pristine");
            var package=new ApfPackage{Id="testcore",Version="1",ArchiveUrl="https://127.0.0.1:18866/test.zip",Sha256=new string('b',64),SizeBytes=100,Destination="GameData",StripPrefix="TestCore"};
            pack=new ApfPackManifest{ServerId="kesas-b6286df413b9426f",PackId="kesas",PackVersion="kesas-rc25",KspVersion="1.12.5",RequiredClientVersion="kesas-rc25",ProtectedRoots=new List<string>{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony"},ManagedPackages=new List<ApfPackage>{package},ClientOnly=new List<ApfClientOnlyRule>()};
            catalog=new InstallCatalog{Version="kesas-rc25",Full=pack,Potato=ApfPackManifest.FromJson(pack.ToJson())};
            catalogPath=Write("GameData/LunaMultiplayer/Launcher/install-catalog.json",ApfJson.Serialize(catalog));
            profilePath=Write("GameData/LunaMultiplayer/Data/KesasInstallProfile.json",ApfJson.Serialize(new Dictionary<string,string>{{"mode","full"},{"catalogVersion","kesas-rc25"}}));
            var state=new ApfPackageState{Id=package.Id,Version=package.Version,ArchiveSha256=package.Sha256,Files=new List<ApfFileState>{new ApfFileState{Path="GameData/TestCore/example.cfg",Sha256=ApfJson.Sha256File(config)}}};
            string statePath=Write("GameData/LunaMultiplayer/Data/APFPackState.json",ApfJson.Serialize(new ApfPackState{PackId=pack.PackId,PackVersion=pack.PackVersion,Packages=new List<ApfPackageState>{state}}));
            helperKey=ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data,"APFHelper.key"));
            feed=ApfClientUpdateManifest.FromJson(File.ReadAllText("Aetherfront-RC24-Work/Candidate/Published/APFClientUpdate.json"));
            feed.Version="kesas-rc25";feed.PackageUrl="https://127.0.0.1:18866/updates/kesas-rc25/APFClient-kesas-rc25.zip";
            var declared=feed.Files.Single(f=>f.Path=="GameData/LunaMultiplayer/Launcher/install-catalog.json");
            declared.Sha256=ApfJson.Sha256File(catalogPath);declared.SizeBytes=new FileInfo(catalogPath).Length;
            using(var signing=new RSACryptoServiceProvider(2048))
            {
                Write("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml",signing.ToXmlString(false));
                ApfClientUpdateSecurity.Sign(feed,signing);
            }
            Write("APF/.client-update-staging/connect/feed.json",feed.ToJson());
            transaction=new ApfSyncTransaction{GameRoot=root,GameDataPath=Path.Combine(root,"GameData"),Manifest=pack,ExistingState=new ApfPackState(),StatePath=statePath};
            installer=new Installer(root,new IPEndPoint(IPAddress.Loopback,18865),"kesas-rc25");SetVerified(feed);
            resultPath=Path.Combine(root,"APF/LastInstallResult.json");
            Check(!Matches(),"Missing receipt never fast-paths");
            Write("APF/LastInstallResult.json","{\"status\":\"complete\",\"kind\":\"pack\"}");
            Check(!Matches(),"RC24 status-only completion never fast-paths");
            Complete();Check(Matches(),"Completed authenticated revision/profile receipt accepts reconnect");
            Check(!(bool)Call("ClientFilesMatch",feed),"Fixture lacks client overlay files, proving ordinary file scan would fail");
            Write("GameData/PersonalAddon/example.cfg","No removal prompt or scan on successful reconnect.");
            File.WriteAllText(config,"changed after successful installation");
            Check(Matches(),"Successful receipt avoids client, package, and manual extra-mod rescans");
            Check(!Matches("potato"),"Profile selection change never fast-paths");
            var changed=ApfPackManifest.FromJson(pack.ToJson());changed.PackVersion="kesas-rc26";
            Check(!Matches("full",changed),"Changed pack revision never fast-paths");
            changed=ApfPackManifest.FromJson(pack.ToJson());changed.ServerId="other-server";
            Check(!Matches("full",changed),"Changed server identity never fast-paths");
            changed=ApfPackManifest.FromJson(pack.ToJson());changed.ManagedPackages[0].Sha256=new string('c',64);
            Check(!Matches("full",changed),"Same-version changed package tuple never fast-paths");
            var otherFeed=ApfClientUpdateManifest.FromJson(feed.ToJson());otherFeed.ReleaseNotes+=" changed";SetVerified(otherFeed);
            Check(!Matches(),"Same-version changed signed-feed contents never fast-paths");SetVerified(feed);
            otherFeed=ApfClientUpdateManifest.FromJson(feed.ToJson());otherFeed.Version="kesas-rc26";SetVerified(otherFeed);
            Check(!Matches(),"Changed active client revision never fast-paths");SetVerified(feed);
            string originalProfile=File.ReadAllText(profilePath);File.WriteAllText(profilePath,"{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc25\"}");
            Check(!Matches(),"Changed committed profile rejects old success");File.WriteAllText(profilePath,originalProfile);
            string originalCatalog=File.ReadAllText(catalogPath);File.AppendAllText(catalogPath," ");
            Check(!Matches(),"Catalog must still match exact signed catalog hash");File.WriteAllText(catalogPath,originalCatalog);
            string originalState=File.ReadAllText(statePath);File.Delete(statePath);
            Check(!Matches(),"Missing committed state rejects old success");File.WriteAllText(statePath,originalState);
            foreach(string status in new[]{"failed","applying","staging","pending","rolled-back"})
            {
                Complete();var receipt=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(resultPath));receipt["status"]=status;SaveReceipt(receipt,true);
                Check(!Matches(),"Authenticated "+status+" receipt never fast-paths");
            }
            Complete();var tampered=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(resultPath));tampered["mode"]="potato";SaveReceipt(tampered,false);
            Check(!Matches(),"Tampered receipt HMAC rejected");
            Complete();Call("InvalidateCompletedReceipt","pack");Check(!Matches(),"New staging invalidates previous success before downloads");
            File.WriteAllText(resultPath,"{");Check(!Matches(),"Malformed receipt safely falls back");
            Complete();SetVerified(null);Check(!Matches(),"No verified active signed client feed means no fast path");SetVerified(feed);
            File.WriteAllText(config,"pristine");Check((bool)Call("PackagePresent",package,state),"Unchanged immutable configuration matches");
            File.WriteAllText(config,"changed");Check(!(bool)Call("PackagePresent",package,state),"Arbitrary configuration changes still require repair");
            string mutable="GameData/SCANsat/PluginData/Settings.cfg";Write(mutable,"user preference");
            var mutablePackage=new ApfPackage{Id="scansat",Version="1",Sha256=new string('d',64),Destination="GameData",StripPrefix="SCANsat"};
            var mutableState=new ApfPackageState{Id="scansat",Version="1",ArchiveSha256=mutablePackage.Sha256,Files=new List<ApfFileState>{new ApfFileState{Path=mutable,Sha256=new string('e',64)}}};
            Check(RuntimeFiles.IsMutable(mutable),"Confirmed SCANsat runtime settings explicitly allowed");
            Check((bool)Call("PackagePresent",mutablePackage,mutableState),"Known runtime config mutation does not replan installation");
            File.Delete(Path.Combine(root,mutable));Check((bool)Call("PackagePresent",mutablePackage,mutableState),"Optional regenerated runtime config absence does not replan installation");
            Check(!RuntimeFiles.IsMutable("GameData/TestCore/example.cfg"),"No blanket configuration exemption");
            Check(!RuntimeFiles.IsMutable("GameData/SCANsat/PluginData/injected.dll"),"PluginData DLLs never become mutable data");
            Check(!RuntimeFiles.IsMutable("GameData/LunaMultiplayer/Launcher/install-catalog.json"),"Signed catalog never becomes mutable data");
            string dll=Write("GameData/TestCore/example.dll","pristine DLL sentinel");state.Files=new List<ApfFileState>{new ApfFileState{Path="GameData/TestCore/example.dll",Sha256=ApfJson.Sha256File(dll)}};
            File.WriteAllText(dll,"modified DLL sentinel");Check(!(bool)Call("PackagePresent",package,state),"Changed code still requires repair without a completed receipt");
            Check(File.ReadAllText(Path.Combine(root,"GameData/PersonalAddon/example.cfg")).StartsWith("No removal"),"Receipt tests never remove manual mods or launch helper/game/network");
            ConnectionChecks();
            DuplicateChecks();
            BundleChecks();
            NativeCommitChecks();
            Console.WriteLine("PASS installer checks="+checks+" fixture="+root);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Retained isolated fixture="+root);return 1;}
    }
}
