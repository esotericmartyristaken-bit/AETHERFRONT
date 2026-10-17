using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static int Main()
    {
        string root=Path.GetFullPath("Aetherfront-RC24-Work/NetworkFixture-"+Guid.NewGuid().ToString("N"));
        try {
            var feed=ApfClientUpdateManifest.FromJson(File.ReadAllText("KESAS-VPS-Server/Updates/APFClientUpdate.json"));
            if(feed.Version!="kesas-rc24")throw new InvalidDataException("Run after RC24 activation only.");
            ZipFile.ExtractToDirectory(Path.GetFullPath("Aetherfront-RC23-Work/Candidate/Aetherfront-Starter-kesas-rc23.zip"),root);
            if(Hash(typeof(Installer).Assembly.Location)!=Hash(Path.Combine(root,"GameData/LunaMultiplayer/Plugins/KesasInstall.dll")))throw new InvalidDataException("Probe must execute the exact old RC23 engine.");
            Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
            File.Copy(Path.GetFullPath("Aetherfront-RC22-Work/GameStub/bin/Release/net472/GameStub.exe"),Path.Combine(root,"KSP_x64.exe"));
            var keep=new Dictionary<string,string>{{"saves/Tester/persistent.sfs","preserve save"},{"GameData/LunaMultiplayer/Data/privatekey.txt","preserve identity"},{"GameData/LunaMultiplayer/Data/KesasInstallSelection.txt","potato\n"},{"APF/.staging/connect/retained.zip.part.range-0-99","preserve cache"}};
            foreach(var item in keep){string path=Path.Combine(root,item.Key);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,item.Value);}
            var installer=new Installer(root,new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865),"kesas-rc23");
            installer.Progress=Console.WriteLine;
            var result=installer.Run("potato");
            if(!result.ClientUpdate||!(bool)typeof(Installer).GetField("githubReady",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(installer))throw new InvalidDataException("Expected a GitHub-backed signed client update.");
            var request=ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(result.TransactionPath));
            if(request.Manifest.Version!="kesas-rc24")throw new InvalidDataException("Unexpected downloaded release.");
            request.ProcessId=0;request.KspArguments=new List<string>{"--network-headless-"+Guid.NewGuid().ToString("N")};
            ApfTransactionSecurity.Sign(request,ApfTransactionSecurity.LoadKey(Path.Combine(root,"GameData/LunaMultiplayer/Data/APFHelper.key")));
            File.WriteAllText(result.TransactionPath,ApfJson.Serialize(request));
            var start=ApfPostExitLauncher.CreateStartInfo(root,result.TransactionPath,true);start.RedirectStandardError=start.RedirectStandardOutput=true;start.UseShellExecute=false;start.CreateNoWindow=true;
            using(var helper=Process.Start(start)) {
                var stdout=helper.StandardOutput.ReadToEndAsync();var stderr=helper.StandardError.ReadToEndAsync();var clock=Stopwatch.StartNew();
                while(!helper.WaitForExit(15000)){Console.WriteLine("Owned old-client helper: "+clock.Elapsed.TotalSeconds.ToString("0")+" seconds");if(clock.Elapsed>TimeSpan.FromMinutes(5))throw new TimeoutException("Helper retained without process termination. PID="+helper.Id);}
                File.WriteAllText(Path.Combine(root,"helper.log"),stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult());
                if(helper.ExitCode!=0)throw new InvalidDataException("Native client update failed; fixture retained.");
            }
            foreach(var file in feed.Files)if(Hash(Path.Combine(root,file.Path))!=file.Sha256)throw new InvalidDataException("Installed signed file differs: "+file.Path);
            foreach(var item in keep)if(File.ReadAllText(Path.Combine(root,item.Key))!=item.Value)throw new InvalidDataException("Preservation failed: "+item.Key);
            File.WriteAllText(Path.Combine(root,"result.json"),ApfJson.Serialize(new{passed=true,baseline="kesas-rc23",version="kesas-rc24",githubReady=true,signedFiles=feed.Files.Count,playerDataAndCachePreserved=true,headlessOnly=true,packInstallTested=false,completedUtc=DateTime.UtcNow.ToString("o")}));
            Console.WriteLine("PASS actual RC23 Connect -> GitHub signed RC24 -> native helper: "+feed.Files.Count+" hashes verified; save, identity, selection and partial cache preserved. "+root);return 0;
        } catch(Exception error){if(Directory.Exists(root))File.WriteAllText(Path.Combine(root,"failure.log"),error.ToString());Console.Error.WriteLine(error);return 1;}
    }
    private static string Hash(string path){using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
}
