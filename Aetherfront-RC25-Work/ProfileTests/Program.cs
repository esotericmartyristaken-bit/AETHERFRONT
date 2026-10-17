using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static string root,data;
    private static int checks;
    private static InstallCatalog catalog;
    private static ApfClientUpdateManifest feed;
    private static void Check(bool value,string message){if(!value)throw new InvalidDataException(message);checks++;Console.WriteLine("PASS "+message);}
    private static void Write(string relative,string value){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,value);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string link,string existing,IntPtr reserved);
    private static int Main()
    {
        try {
            using(var process=Process.GetCurrentProcess())process.PriorityClass=ProcessPriorityClass.BelowNormal;
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
            Write("result.json",ApfJson.Serialize(new{passed=true,checks,unityGameplayTested=false,headlessOnly=true,completedUtc=DateTime.UtcNow.ToString("o")}));
            Console.WriteLine("PASS full profile chain fixture="+root+" checks="+checks);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Retained fixture="+root);return 1;}
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
