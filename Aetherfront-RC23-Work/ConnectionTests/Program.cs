using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static string root, evidence;
    private static readonly string Data = "GameData/LunaMultiplayer/Data/";
    private static readonly Dictionary<string,string> preserved = new Dictionary<string,string>();
    private static int Main(string[] args)
    {
        if(args.Length!=1 || args[0]!="--upgrade-owned-headless-fixture") return 2;
        try {
            root=Path.GetFullPath("Aetherfront-RC22-Work/LiveProfileFixture-8ae6532fec3643778f37c0288970177b");
            ApfPaths.EnsureNoReparsePoints(Path.GetPathRoot(root),root);
            Require(File.Exists(Owned(".live-profile-owned")),"Original owned headless fixture required.");
            Require(Hash(Owned("KSP_x64.exe"))==Hash(Path.GetFullPath("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe")),"Only known GameStub may relaunch.");
            Require(File.ReadAllText(Owned(Data+"APFClient.json")).Contains("kesas-rc22"),"Expected unmodified installed RC22 baseline.");
            foreach(var process in Process.GetProcessesByName("KSP_x64")) using(process) {
                if(!process.HasExited && string.Equals(process.MainModule.FileName,Owned("KSP_x64.exe"),StringComparison.OrdinalIgnoreCase)) throw new IOException("Owned fixture already running.");
            }
            using(var guard=new FileStream(Owned("APF/.installer.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
            evidence=Owned("evidence/rc23-connection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(evidence);
            foreach(var relative in new[]{Data+"APFPackState.json",Data+"KesasInstallProfile.json",Data+"APFClient.json","APF/APFTransaction.log"})
                if(File.Exists(Owned(relative))) File.Copy(Owned(relative),Path.Combine(evidence,Path.GetFileName(relative)));
            foreach(var relative in new[]{Data+"settings.xml",Data+"settings_bkp.xml",Data+"KesasInstallSelection.txt",Data+"APFHelper.key",Data+"privatekey.txt",Data+"APFServer.txt",Data+"APFTlsCertificatePins.txt"})
                if(File.Exists(Owned(relative))) preserved.Add(relative,Hash(Owned(relative)));
            foreach(var directory in new[]{"saves","APF/Identity"}) if(Directory.Exists(Owned(directory)))
                foreach(var path in Directory.GetFiles(Owned(directory),"*",SearchOption.AllDirectories)) preserved.Add(path.Substring(root.Length+1).Replace('\\','/'),Hash(path));
            var before=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(Data+"APFPackState.json")));
            Require(before.PackVersion=="kesas-rc22" && before.Packages.Count==176,"Exact completed Potato RC22 baseline required.");
            var client=Run("kesas-rc22");
            Require(client.Changed && client.ClientUpdate,"Old Connect implementation did not produce RC23 client update.");
            Apply(client,true);
            var pack=Run("kesas-rc23");
            Require(pack.Changed && !pack.ClientUpdate,"New catalog did not require the 4K texture replacement.");
            var transaction=ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(pack.TransactionPath));
            Require(transaction.Packages.Count==1 && transaction.Packages[0].Package.Id=="kesas-kcalbelohtextures-4k-v1.1.8" && transaction.Deletes.Count==0,"Update attempted changes beyond the single texture package.");
            Apply(pack,false);
            var after=ApfJson.Deserialize<ApfPackState>(File.ReadAllText(Owned(Data+"APFPackState.json")));
            Require(after.PackVersion=="kesas-rc23" && after.Packages.Count==176,"Unexpected updated state identity/count.");
            var comparison=ApfJson.Deserialize<Comparison>(File.ReadAllText(Path.GetFullPath("Aetherfront-RC23-Work/Kcalbeloh-Comparison.json")));
            var installedTexture=after.Packages.Single(p=>p.Id=="kesas-kcalbelohtextures-4k-v1.1.8");
            var expected=comparison.Inventory4k.ToDictionary(p=>"GameData/"+p.Path,StringComparer.OrdinalIgnoreCase);
            Require(installedTexture.Files.Count==227,"Wrong texture inventory count.");
            foreach(var file in installedTexture.Files) Require(expected.ContainsKey(file.Path) && file.Sha256==expected[file.Path].Sha256 && Hash(Owned(file.Path))==file.Sha256,"4K installed content differs from official archive: "+file.Path);
            var oldUnchanged=before.Packages.Where(p=>p.Id!="kesas-kcalbelohtextures-44097afb").OrderBy(p=>p.Id).ToArray();
            var newUnchanged=after.Packages.Where(p=>p.Id!="kesas-kcalbelohtextures-4k-v1.1.8").OrderBy(p=>p.Id).ToArray();
            Require(ApfJson.Serialize(oldUnchanged)==ApfJson.Serialize(newUnchanged),"Unrelated 175 package states changed.");
            var clean=Run("kesas-rc23");
            Require(!clean.Changed && !clean.ClientUpdate && string.IsNullOrEmpty(clean.TransactionPath),"Unchanged reconnect requested another installation.");
            foreach(var item in preserved) Require(Hash(Owned(item.Key))==item.Value,"Preserved data changed: "+item.Key);
            File.WriteAllText(Path.Combine(evidence,"result.json"),ApfJson.Serialize(new{passed=true,headlessOnly=true,unityGameplayTested=false,baseline="kesas-rc22",version="kesas-rc23",clientUpdateThroughExistingEngine=true,downloadedTexturePackages=1,untouchedPackageStates=175,cleanReconnectUnchanged=true,preservedFiles=preserved.Count,githubReadyForAllConnections=true,completedUtc=DateTime.UtcNow.ToString("o")}));
            Console.WriteLine("PASS: actual RC22 Connect -> RC23 client -> one GitHub 4K package -> unchanged reconnect; 175 other package states and player data preserved. "+evidence);
            return 0;
        } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
    private static InstallResult Run(string version)
    {
        var installer=new Installer(root,new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865),version);
        installer.Confirm=message=>{throw new InvalidOperationException("Unexpected extra-mod confirmation: "+message);};
        var clock=Stopwatch.StartNew();long last=-20000;
        installer.Report=progress=>{if(clock.ElapsedMilliseconds-last>=15000){last=clock.ElapsedMilliseconds;Console.WriteLine(version+": "+progress.Status);}};
        var result=installer.Run("potato");
        Require((bool)typeof(Installer).GetField("githubReady",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(installer),"GitHub unavailable or origin fallback used; do not claim complete GitHub path.");
        return result;
    }
    private static void Apply(InstallResult result,bool client)
    {
        string path=Path.GetFullPath(result.TransactionPath);
        Require(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Transaction outside owned fixture.");
        var key=ApfTransactionSecurity.LoadKey(Owned(Data+"APFHelper.key"));
        if(client){var t=ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(path));Require(ApfTransactionSecurity.Verify(t,key),"Client transaction HMAC failed.");t.ProcessId=0;ApfTransactionSecurity.Sign(t,key);File.WriteAllText(path,ApfJson.Serialize(t));}
        else {var t=ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(path));Require(ApfTransactionSecurity.Verify(t,key),"Pack transaction HMAC failed.");t.ProcessId=0;ApfTransactionSecurity.Sign(t,key);File.WriteAllText(path,ApfJson.Serialize(t));}
        var start=ApfPostExitLauncher.CreateStartInfo(root,path,client);start.UseShellExecute=false;start.CreateNoWindow=true;start.WorkingDirectory=root;start.RedirectStandardError=start.RedirectStandardOutput=true;
        using(var process=Process.Start(start)) {
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();var clock=Stopwatch.StartNew();
            while(!process.WaitForExit(15000)){Console.WriteLine("Owned helper "+process.Id+": "+clock.Elapsed.TotalSeconds.ToString("0")+" seconds.");if(clock.Elapsed>TimeSpan.FromMinutes(10))throw new TimeoutException("Owned helper still running; no process killed. PID="+process.Id);}
            Require(Task.WaitAll(new Task[]{stdout,stderr},30000),"Helper output did not drain.");
            File.WriteAllText(Path.Combine(evidence,client?"client-helper.log":"pack-helper.log"),stdout.Result+stderr.Result);
            Require(process.ExitCode==0,"Native helper failed; evidence retained.");
        }
    }
    private static string Owned(string relative){string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));Require(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Path outside owned fixture.");ApfPaths.EnsureNoReparsePoints(root,path);return path;}
    private static string Hash(string path){using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
    private static void Require(bool valid,string error){if(!valid)throw new InvalidDataException(error);}
    private sealed class Comparison {public FileProof[] Inventory4k {get;set;}}
    private sealed class FileProof {public string Path {get;set;} public string Sha256 {get;set;}}
}
