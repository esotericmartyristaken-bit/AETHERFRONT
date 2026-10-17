using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static int checks;
    private static readonly string runId=Guid.NewGuid().ToString("N").Substring(0,10);
    private static readonly IPEndPoint endpoint=new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865);
    private static readonly List<string> published=new List<string>();
    private static string work,root,data,launcher;
    private static void Check(bool result,string message){if(!result)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    private static string Make(string relative){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));return path;}
    private static Installer New(string version="kesas-rc20"){return new Installer(root,endpoint,version){Confirm=message=>true};}
    private static void Main()
    {
        work=Path.GetFullPath("Aetherfront-RC21-Work");root=Path.Combine(work,"EngineFixture-"+runId);
        try{Run();Console.WriteLine("All "+checks+" in-game staging and helper checks passed.");}
        catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
        finally{
            string config=Path.GetFullPath("KESAS-VPS-Server/Server/Config")+Path.DirectorySeparatorChar;
            foreach(string file in published){string full=Path.GetFullPath(file);if(!full.StartsWith(config,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("Aetherfront-RC21-Fixture-",StringComparison.Ordinal)||new FileInfo(full).Length>4096)throw new IOException("Unsafe fixture cleanup target.");File.Delete(full);}
            Console.WriteLine("Removed "+published.Count+" tiny public test archives; source fixtures and recovery backups retained.");
        }
    }
    private static void Run()
    {
        ZipFile.ExtractToDirectory(Path.GetFullPath("client-artifacts/KESAS-RC16/KESAS-Starter-rc16.zip"),root);
        data=Path.Combine(root,"GameData/LunaMultiplayer/Data");launcher=Path.Combine(root,"GameData/LunaMultiplayer/Launcher");
        Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
        File.Copy(Path.Combine(work,"GameStub/bin/Release/net472/GameStub.exe"),Path.Combine(root,"KSP_x64.exe"));
        File.Copy(Path.Combine(work,"Patched/ApfCore.dll"),Path.Combine(root,"GameData/LunaMultiplayer/Plugins/ApfCore.dll"),true);
        File.Copy(Path.Combine(work,"Engine/bin/Release/net472/KesasInstall.dll"),Path.Combine(root,"GameData/LunaMultiplayer/Plugins/KesasInstall.dll"));
        File.Copy(Path.Combine(work,"APFApplyUpdate.ps1"),Path.Combine(root,"GameData/LunaMultiplayer/APFApplyUpdate.ps1"),true);
        File.WriteAllText(Path.Combine(data,"privatekey.txt"),"keep identity");File.WriteAllText(Make("saves/player/persistent.sfs"),"keep save");
        File.WriteAllText(Path.Combine(data,"KesasInstallSelection.txt"),"potato\n");
        File.WriteAllText(Make("GameData/PersonalAddon/custom.txt"),"recover me");
        File.WriteAllText(Path.Combine(root,"settings.cfg"),"ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = keep-me\n");
        var core=Package("TestCore");var visual=Package("TUFX");var policy=Package("ZZZ_KESASPotato");
        var full=Manifest(core,visual);var potato=Manifest(core,policy);
        var catalog=new InstallCatalog{Version="kesas-rc20",Full=full,Potato=potato,Sizes=new List<PackageSize>{Size(core),Size(visual),Size(policy)}};
        Save(catalog);
        var progress=new List<ProgressInfo>();int confirmed=0;
        var installer=New();installer.Report=p=>progress.Add(p);installer.Confirm=message=>{confirmed++;return true;};
        var pending=installer.Run("full",false);
        Check(pending.Changed&&!pending.ClientUpdate&&File.Exists(pending.TransactionPath),"Fresh Regular profile stages a signed pack transaction over pinned public HTTPS");
        Check(!Directory.Exists(Path.Combine(root,"GameData/TestCore"))&&File.Exists(Path.Combine(root,"GameData/PersonalAddon/custom.txt"))&&!File.Exists(Path.Combine(data,"KesasInstallProfile.json")),"Staging never mutates loaded mods or marks an uninstalled profile complete");
        Check(confirmed==1,"Unrelated extra mods require explicit approval");
        Check(progress.Any(p=>p.Total>0&&p.Downloaded==p.Total)&&progress.All(p=>p.Downloaded<=p.Total||p.Total==0),"Detailed progress includes bounded aggregate bytes, packages and completion");
        Apply(pending);
        Check(File.Exists(Path.Combine(root,"GameData/TestCore/example.cfg"))&&File.Exists(Path.Combine(root,"GameData/TUFX/example.cfg")),"Native PowerShell helper installs staged Regular packages after exit");
        Check(Directory.GetFiles(Path.Combine(root,"APF/.transaction-backup"),"custom.txt",SearchOption.AllDirectories).Any(),"Removed extra mods remain recoverable");
        Check(!New().Run("full",false).Changed,"Clean Regular reconnect needs no download or restart");
        pending=New().Run("potato",false);
        Check(File.Exists(Path.Combine(root,"GameData/TUFX/example.cfg")),"Profile change leaves loaded graphics intact until KSP exits");
        Apply(pending);
        Check(!Directory.Exists(Path.Combine(root,"GameData/TUFX"))&&File.Exists(Path.Combine(root,"GameData/TestCore/example.cfg"))&&File.Exists(Path.Combine(root,"GameData/ZZZ_KESASPotato/example.cfg")),"Regular to Potato removes only profile assets and keeps shared gameplay");
        Check(File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("TEXTURE_QUALITY = 2")&&File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("KEY_BINDING = keep-me"),"Post-exit Potato graphics preserve controls");
        Check(File.ReadAllText(Path.Combine(data,"KesasInstallProfile.json")).Contains("potato")&&!New().Run("potato",false).Changed,"Completed Potato preference survives and reconnect is idempotent");
        pending=New().Run("full",false);Apply(pending);
        Check(File.Exists(Path.Combine(root,"GameData/TUFX/example.cfg"))&&!Directory.Exists(Path.Combine(root,"GameData/ZZZ_KESASPotato"))&&File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("ANTI_ALIASING = 8"),"Potato to Regular restores graphics/assets and removes the Potato-only policy");
        Check(File.ReadAllText(Path.Combine(data,"privatekey.txt"))=="keep identity"&&File.ReadAllText(Path.Combine(root,"saves/player/persistent.sfs"))=="keep save"&&File.ReadAllText(Path.Combine(data,"KesasInstallSelection.txt"))=="potato\n","Player identity, saves and separately saved dropdown selection survive helper installs");
        Check(!KesasInstallPolicy.SuppressRelaunch&&!KesasInstallPolicy.LaunchPending(root)&&!KesasInstallPolicy.TryLaunchInstaller(root,0),"External launcher hooks stay disabled and suppression is restored");
        File.WriteAllText(Path.Combine(root,"GameData/TestCore/example.cfg"),"damaged");
        pending=New().Run("full",false);Apply(pending);
        Check(File.ReadAllText(Path.Combine(root,"GameData/TestCore/example.cfg"))=="fixture-TestCore","Connect detects and repairs modified gameplay configuration");
        var bad=Package("BadHash");bad.Sha256=new string('b',64);catalog.Full=Manifest(bad);catalog.Sizes.Add(Size(bad));Save(catalog);
        bool rejected=false;try{New().Run("full",false);}catch(InvalidDataException){rejected=true;}
        Check(rejected&&File.Exists(Path.Combine(root,"GameData/TestCore/example.cfg")),"Bad package hashes fail before any installed file changes");
        var range=Package("RangeCore");catalog.Full=Manifest(range);catalog.Sizes.Add(Size(range));Save(catalog);
        string partial=Make("APF/.staging/connect/"+range.Sha256+".zip.part");
        File.WriteAllBytes(partial,File.ReadAllBytes(published.Last()).Take(24).ToArray());
        pending=New().Run("full",false);Check(!File.Exists(partial)&&File.Exists(Path.Combine(root,"APF/.staging/connect",range.Sha256+".zip")),"Interrupted package downloads resume to a verified archive");Apply(pending);
        catalog.Full=full;Save(catalog);
        bool canceled=false;installer=New();installer.IsCanceled=()=>true;try{installer.Run("full",false);}catch(OperationCanceledException){canceled=true;}
        Check(canceled&&File.Exists(Path.Combine(root,"GameData/RangeCore/example.cfg")),"Canceled connection leaves installed profile untouched");
        var wrong=Manifest();wrong.ServerId="other-server";rejected=false;try{Installer.ValidateIdentity(wrong,full,"kesas-rc20");}catch(InvalidDataException){rejected=true;}
        Check(rejected,"A typed address cannot silently switch to a different server identity");
        var mismatch=Manifest();mismatch.RequiredClientVersion="kesas-rc1";
        rejected=false;try{Installer.ValidateIdentity(mismatch,full,"kesas-rc20");}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Catalog/server/client revision mismatches fail closed");
        var other=new Installer(root,new IPEndPoint(IPAddress.Parse("192.0.2.123"),2400),"kesas-rc20");
        var url=(Uri)typeof(Installer).GetField("updateUrl",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(other);
        Check(url.Host=="192.0.2.123"&&url.Port==2401&&url.AbsolutePath=="/updates/APFClientUpdate.json","Downloads follow selected IP and paired port, not the bootstrap's old IP");
        var download=typeof(Installer).GetMethod("Download",BindingFlags.Instance|BindingFlags.NonPublic);
        rejected=false;try{download.Invoke(New(),new object[]{new Uri("https://example.invalid/a.zip"),Make("APF/refused.zip"),100L,null});}catch(TargetInvocationException error){rejected=error.InnerException is InvalidDataException;}
        Check(rejected&&!File.Exists(Path.Combine(root,"APF/refused.zip")),"Off-origin downloads are refused before network or mutation");
        string publicKey=Path.Combine(data,"APFClientUpdatePublicKey.xml"),originalKey=File.ReadAllText(publicKey);
        using(var rsa=new RSACryptoServiceProvider(2048))File.WriteAllText(publicKey,rsa.ToXmlString(false));
        rejected=false;try{New("kesas-rc1").Run("full");}catch(InvalidDataException error){rejected=error.Message.Contains("signature");}
        Check(rejected,"Client update signatures remain mandatory");File.WriteAllText(publicKey,originalKey);
        pending=New("kesas-rc1").Run("potato");
        var update=ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(pending.TransactionPath));
        Check(pending.ClientUpdate&&update.Manifest.Version=="kesas-rc20"&&ApfTransactionSecurity.Verify(update,ApfTransactionSecurity.LoadKey(Path.Combine(data,"APFHelper.key"))),"Outdated clients automatically stage the signed complete client overlay before pack work");
        Check(File.ReadAllText(Path.Combine(data,"KesasInstallSelection.txt"))=="potato\n","Client update staging preserves the dropdown selection");
        var present=typeof(Installer).GetMethod("PackagePresent",BindingFlags.Instance|BindingFlags.NonPublic);
        var state=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(data,"APFPackState.json"))).Packages.Single(p=>p.Id==range.Id);
        string target=Make("LinkTarget/example.cfg");File.WriteAllText(target,"outside package");
        string link=Path.Combine(root,"GameData/RangeCore/linked");
        var linkStart=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe")){Arguments="-NoProfile -Command \"New-Item -ItemType Junction -Path '"+link+"' -Target '"+Path.GetDirectoryName(target)+"' | Out-Null\"",UseShellExecute=false,CreateNoWindow=true};
        using(var process=Process.Start(linkStart)){if(!process.WaitForExit(15000)||process.ExitCode!=0)throw new Exception("Junction fixture failed.");}
        state.Files[0].Path="GameData/RangeCore/linked/example.cfg";rejected=false;
        try{present.Invoke(New(),new object[]{range,state});}catch(TargetInvocationException error){rejected=error.InnerException is InvalidDataException;}
        Check(rejected&&File.ReadAllText(target)=="outside package","Real NTFS junctions are rejected without reading or changing target content");
        state.Files[0].Path="../not-owned.cfg";rejected=false;
        try{present.Invoke(New(),new object[]{range,state});}catch(TargetInvocationException error){rejected=error.InnerException is InvalidDataException;}
        Check(rejected,"Installed-state paths cannot escape the game root");
        string clientRoot=Path.Combine(work,"ClientIntegrityFixture-"+runId);Directory.CreateDirectory(clientRoot);
        ZipFile.ExtractToDirectory(Path.Combine(work,"Candidate/Published/APFClient-kesas-rc21.zip"),clientRoot);
        string clientData=Path.Combine(clientRoot,"GameData/LunaMultiplayer/Data");
        foreach(string name in new[]{"APFServer.txt","APFTlsCertificatePins.txt","APFClientUpdatePublicKey.xml"})File.Copy(Path.Combine(data,name),Path.Combine(clientData,name));
        Directory.CreateDirectory(Path.Combine(clientRoot,"GameData/Squad"));File.Copy(Path.Combine(root,"KSP_x64.exe"),Path.Combine(clientRoot,"KSP_x64.exe"));
        var complete=ApfClientUpdateManifest.FromJson(File.ReadAllText(Path.Combine(work,"Candidate/Published/APFClientUpdate.json")));
        var clientInstaller=new Installer(clientRoot,endpoint,"kesas-rc20");
        var filesMatch=typeof(Installer).GetMethod("ClientFilesMatch",BindingFlags.Instance|BindingFlags.NonPublic);
        Check((bool)filesMatch.Invoke(clientInstaller,new object[]{complete}),"Complete signed client overlay matches without an unnecessary same-version reinstall");
        File.AppendAllText(Path.Combine(clientRoot,complete.Files.First(f=>f.Path.Contains("Localization/")&&f.Path.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)).Path),"\nmodified\n");
        Check(!(bool)filesMatch.Invoke(clientInstaller,new object[]{complete}),"Same-version client damage is detected for repair on Connect");
    }
    private static void Apply(InstallResult pending)
    {
        var transaction=ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(pending.TransactionPath));
        transaction.ProcessId=0;transaction.KspArguments=new List<string>();
        ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadKey(Path.Combine(data,"APFHelper.key")));
        File.WriteAllText(pending.TransactionPath,ApfJson.Serialize(transaction));
        var start=ApfPostExitLauncher.CreateStartInfo(root,pending.TransactionPath,false);start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        using(var process=Process.Start(start)){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(60000))throw new TimeoutException("Owned fixture helper timed out.");if(process.ExitCode!=0)throw new Exception(stderr.Result+stdout.Result);}
    }
    private static PackageSize Size(ApfPackage package){return new PackageSize{Id=package.Id,ExpandedBytes=500};}
    private static void Save(InstallCatalog catalog){File.WriteAllText(Path.Combine(launcher,"install-catalog.json"),ApfJson.Serialize(catalog));}
    private static ApfPackManifest Manifest(params ApfPackage[] packages){return new ApfPackManifest{ServerId="kesas-b6286df413b9426f",PackId="kesas",PackVersion="kesas-rc20",KspVersion="1.12.5",RequiredClientVersion="kesas-rc20",ProtectedRoots=new List<string>{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony"},ManagedPackages=packages.ToList(),ClientOnly=new List<ApfClientOnlyRule>()};}
    private static ApfPackage Package(string name)
    {
        string filename="Aetherfront-RC21-Fixture-"+name+"-"+runId+".zip",path=Path.GetFullPath(Path.Combine("KESAS-VPS-Server/Server/Config",filename));
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry(name+"/example.cfg").Open()))writer.Write("fixture-"+name);
        published.Add(path);
        return new ApfPackage{Id=name,Version="1",ArchiveUrl="https://217.216.49.250:18866/"+filename,Sha256=ApfJson.Sha256File(path),SizeBytes=new FileInfo(path).Length,Destination="GameData",StripPrefix=name};
    }
}
