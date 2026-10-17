using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Forms;
using ApfCore;
using KesasLauncher;

internal static class Program
{
    static int checks;
    static readonly string runId=Guid.NewGuid().ToString("N").Substring(0,10);
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    [STAThread] static void Main()
    {
        string work=Path.GetFullPath("KESAS-RC16-Work");
        string root=Path.Combine(work,"TestsFixture-"+runId);if(Directory.Exists(root))throw new IOException("Fixture already exists");
        ZipFile.ExtractToDirectory(Path.GetFullPath("client-artifacts/KESAS-RC16/KESAS-Starter-Full-rc16.zip"),root);
        string data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(data);Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
        string launcher=Path.Combine(root,"GameData/LunaMultiplayer/Launcher");Directory.CreateDirectory(launcher);
        File.Copy(Path.Combine(work,"visual-roots.txt"),Path.Combine(launcher,"visual-roots.txt"),true);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),Path.Combine(root,"KSP_x64.exe"));
        File.Copy(Path.GetFullPath("KESAS-RC15-Work/Inputs/JsonFx.dll"),Make(root,"GameData/LunaMultiplayer/Plugins/JsonFx.dll"),true);
        File.Copy(Path.Combine(work,"ApfCore.dll"),Make(root,"GameData/LunaMultiplayer/Plugins/ApfCore.dll"),true);
        File.WriteAllText(Path.Combine(data,"APFTlsCertificatePins.txt"),"34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09");
        File.WriteAllText(Path.Combine(data,"APFClientUpdateManifestUrl.txt"),"https://217.216.49.250:18866/updates/APFClientUpdate.json");
        File.Copy(Path.GetFullPath("KESAS-VPS-Server/secrets/update-signing-public.xml"),Path.Combine(data,"APFClientUpdatePublicKey.xml"),true);
        File.WriteAllText(Path.Combine(data,"APFClient.json"),"{\"enabled\":true,\"clientVersion\":\"kesas-rc16\"}");
        File.WriteAllText(Path.Combine(data,"privatekey.txt"),"player-identity-must-stay");File.WriteAllText(Make(root,"saves/persistent.sfs"),"player-save-must-stay");
        File.WriteAllText(Path.Combine(root,"settings.cfg"),"ANTI_ALIASING = 8\nTEXTURE_QUALITY = 0\nKEY_BINDING = keep-me\n");
        File.WriteAllText(Make(root,"GameData/PersonalAddon/custom.txt"),"recover-me");
        var core=Package(work,"TestCore");var visual=Package(work,"TUFX");
        var full=Manifest(core,visual);var potato=Manifest(core);
        var catalog=new InstallCatalog {Version="kesas-rc16",Full=full,Potato=potato,Sizes=new List<PackageSize>{new PackageSize{Id=core.Id,ExpandedBytes=200},new PackageSize{Id=visual.Id,ExpandedBytes=200}}};
        File.WriteAllText(Path.Combine(launcher,"install-catalog.json"),ApfJson.Serialize(catalog));
        int confirmed=0;var installer=new Installer(root,false){Confirm=message=>{confirmed++;return true;},Progress=message=>Console.WriteLine("  "+message)};
        installer.Run("full");
        Check(File.Exists(Path.Combine(root,"GameData/TestCore/example.dll")) && File.Exists(Path.Combine(root,"GameData/TUFX/example.dll")),"Fresh Full install downloads both packages over pinned production HTTPS");
        Check(confirmed==1 && Directory.GetFiles(Path.Combine(root,"APF/.transaction-backup"),"custom.txt",SearchOption.AllDirectories).Any(),"Extra mod removal is confirmed and recoverable");
        Check(!KesasInstallPolicy.SuppressRelaunch,"External installer restores normal game-relaunch behavior");
        Check(!installer.Run("full").Changed,"Repeated same-profile launch reuses installed packages");
        installer.Run("potato",false);
        Check(!Directory.Exists(Path.Combine(root,"GameData/TUFX")) && File.Exists(Path.Combine(root,"GameData/TestCore/example.dll")),"Potato removes only optional visual package; shared content remains");
        Check(File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("TEXTURE_QUALITY = 2") && File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("KEY_BINDING = keep-me"),"Potato applies low graphics without changing controls");
        Check(File.ReadAllText(Path.Combine(data,"privatekey.txt"))=="player-identity-must-stay" && File.ReadAllText(Path.Combine(root,"saves/persistent.sfs"))=="player-save-must-stay","Save and player identity survive both profile installs");
        var server=Manifest();server.ClientOnly.Add(new ApfClientOnlyRule {Id="core",PathRoots=new List<string>{"GameData/TestCore"},Dlls=new List<ApfClientOnlyDll>{new ApfClientOnlyDll{Path="GameData/TestCore/example.dll",Sha256=ApfJson.Sha256File(Path.Combine(root,"GameData/TestCore/example.dll"))}}});
        server.ClientOnly.Add(new ApfClientOnlyRule {Id="visual",PathRoots=new List<string>{"GameData/TUFX"},Dlls=new List<ApfClientOnlyDll>{new ApfClientOnlyDll{Path="GameData/TUFX/example.dll",Sha256=new string('a',64)}}});
        var scan=ApfPackScanner.Scan(Path.Combine(root,"GameData")+Path.DirectorySeparatorChar,server,new ApfPackState());
        Check(scan.IsClean,"Potato passes server DLL checks with absent optional visuals, including trailing-slash path");
        Check(server.ClientOnly[1].Dlls.Count==1,"Profile scan does not mutate the server manifest or handshake digest");
        File.WriteAllText(Path.Combine(root,"GameData/TestCore/example.dll"),"tampered");
        Check(ApfPackScanner.Scan(Path.Combine(root,"GameData"),server,new ApfPackState()).Errors.Any(),"Potato does not bypass gameplay DLL integrity");
        installer.Run("full",false);
        Check(File.Exists(Path.Combine(root,"GameData/TUFX/example.dll")) && File.ReadAllText(Path.Combine(root,"settings.cfg")).Contains("ANTI_ALIASING = 8"),"Switching to Full restores visuals and previous graphics settings");
        var changed=ApfJson.Deserialize<ApfPackManifest>(full.ToJson());changed.ManagedPackages[0].SizeBytes=3L*1024*1024*1024;
        Check(ApfManifestValidator.Validate(changed).IsValid,"Bounded pack metadata accepts a 3 GiB archive");
        changed.ManagedPackages[0].SizeBytes=33L*1024*1024*1024;
        Check(!ApfManifestValidator.Validate(changed).IsValid,"Archives above 32 GiB are rejected");
        var bad=Package(work,"BadHash");bad.Sha256=new string('b',64);bad.Id="bad-hash";
        catalog.Full=Manifest(bad);catalog.Sizes.Add(new PackageSize{Id=bad.Id,ExpandedBytes=200});File.WriteAllText(Path.Combine(launcher,"install-catalog.json"),ApfJson.Serialize(catalog));
        bool rejected=false;try{installer.Run("full",false);}catch(InvalidDataException){rejected=true;}
        Check(rejected && File.Exists(Path.Combine(root,"GameData/TestCore/example.dll")),"Bad download hash is rejected before installed files change");
        catalog.Full=full;File.WriteAllText(Path.Combine(launcher,"install-catalog.json"),ApfJson.Serialize(catalog));
        using(var form=new InstallerWindow(root))using(var bitmap=new Bitmap(form.Width,form.Height)){form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(work,"launcher-full.png"));var box=form.Controls.OfType<CheckBox>().Single();box.Checked=true;Application.DoEvents();form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(work,"launcher-potato.png"));Check(box.Checked && form.Controls.OfType<Button>().Count()==2 && form.Controls.OfType<Label>().All(l=>l.Bottom<form.ClientSize.Height),"Native launcher renders Full/Potato selector and install controls");form.Close();}
        var range=Package(work,"RangeCore");
        string partial=Make(root,"APF/.staging/launcher/"+range.Sha256+".zip.part");
        File.WriteAllBytes(partial,File.ReadAllBytes(Path.Combine("KESAS-VPS-Server/Server/Config",Path.GetFileName(new Uri(range.ArchiveUrl).AbsolutePath))).Take(24).ToArray());
        catalog.Full=Manifest(range);catalog.Sizes.Add(new PackageSize{Id=range.Id,ExpandedBytes=200});File.WriteAllText(Path.Combine(launcher,"install-catalog.json"),ApfJson.Serialize(catalog));
        installer.Run("full",false);
        Check(File.Exists(Path.Combine(root,"GameData/RangeCore/example.dll")) && !File.Exists(partial),"Interrupted archive resumes through the production HTTPS installer");
        var real=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(work,"install-catalog.json"))).Full.ManagedPackages.OrderByDescending(p=>p.SizeBytes).First();
        var request=(HttpWebRequest)WebRequest.Create(real.ArchiveUrl);request.AllowAutoRedirect=false;request.Timeout=15000;request.ReadWriteTimeout=15000;
        request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>{using(var sha=SHA256.Create())return certificate!=null && BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-","").ToLowerInvariant()=="34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09";};
        request.AddRange(real.SizeBytes-128,real.SizeBytes-1);
        using(var response=(HttpWebResponse)request.GetResponse())using(var input=response.GetResponseStream())using(var memory=new MemoryStream()) {
            input.CopyTo(memory);
            byte[] expected=new byte[128];using(var local=File.OpenRead(Path.Combine(work,"HostedPackages",Path.GetFileName(new Uri(real.ArchiveUrl).AbsolutePath)))){local.Position=real.SizeBytes-128;local.Read(expected,0,128);}
            Check(response.StatusCode==HttpStatusCode.PartialContent && response.ContentLength==128 && memory.ToArray().SequenceEqual(expected),"Largest published media archive serves exact tail ranges over pinned public HTTPS");
        }
        File.WriteAllText(Path.Combine(data,"APFClient.json"),"{\"enabled\":true,\"clientVersion\":\"kesas-rc1\"}");
        string previousProfile=File.ReadAllText(Path.Combine(data,"KesasInstallProfile.json"));
        string previousCatalog=ApfJson.Deserialize<Dictionary<string,string>>(previousProfile)["catalogVersion"];
        var updateResult=installer.Run("potato",true);
        var selected=ApfJson.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(data,"KesasInstallProfile.json")));
        Check(updateResult.RestartLauncher && selected["mode"]=="potato" && selected["catalogVersion"]==previousCatalog,"Signed client update preserves current checkbox choice without falsely marking new media installed");
        string target=Path.Combine(root,"LinkTarget");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"example.dll"),"outside-package-sentinel");
        string link=Path.Combine(root,"GameData/RangeCore/linked");Directory.CreateDirectory(Path.GetDirectoryName(link));
        var linkStart=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe")) {
            Arguments="-NoProfile -ExecutionPolicy Bypass -Command \"New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null\"",UseShellExecute=false,CreateNoWindow=true
        };
        using(var process=Process.Start(linkStart)){if(!process.WaitForExit(15000)||process.ExitCode!=0)throw new Exception("Owned junction fixture creation failed");}
        var installedState=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Path.Combine(data,"APFPackState.json"))).Packages.Single(p=>p.Id==range.Id);
        var probe=new Installer(root,false);
        var present=typeof(Installer).GetMethod("PackagePresent",BindingFlags.NonPublic|BindingFlags.Instance);
        installedState.Files[0].Path="GameData/RangeCore/linked/example.dll";
        bool linkRejected=false;try{present.Invoke(probe,new object[]{range,installedState});}catch(TargetInvocationException error){linkRejected=error.InnerException is InvalidDataException;}
        Check(linkRejected && File.ReadAllText(Path.Combine(target,"example.dll"))=="outside-package-sentinel","Directory-check cache rejects real NTFS junctions without reading the target payload");
        installedState.Files[0].Path="../not-owned.dll";
        bool escapeRejected=false;try{present.Invoke(probe,new object[]{range,installedState});}catch(TargetInvocationException error){escapeRejected=error.InnerException is InvalidDataException;}
        Check(escapeRejected,"Installed-state paths cannot escape the game folder");
        Console.WriteLine("All "+checks+" launcher/policy checks passed.");
    }
    static string Make(string root,string relative){string p=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(p));return p;}
    static ApfPackManifest Manifest(params ApfPackage[] packages){return new ApfPackManifest{ServerId="fixture",PackId="kesas",PackVersion="kesas-rc16",KspVersion="1.12.5",RequiredClientVersion="kesas-rc16",ProtectedRoots=new List<string>{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony"},ManagedPackages=packages.ToList(),ClientOnly=new List<ApfClientOnlyRule>()};}
    static ApfPackage Package(string work,string name)
    {
        string filename="KESAS-RC16-Fixture-"+name+"-"+runId+".zip";string path=Path.GetFullPath(Path.Combine("KESAS-VPS-Server/Server/Config",filename));
        if(File.Exists(path))throw new IOException("Fixture artifact already exists");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry(name+"/example.dll").Open()))writer.Write("fixture-"+name);
        return new ApfPackage{Id=name,Version="1",ArchiveUrl="https://217.216.49.250:18866/"+filename,Sha256=ApfJson.Sha256File(path),SizeBytes=new FileInfo(path).Length,Destination="GameData",StripPrefix=name};
    }
}
