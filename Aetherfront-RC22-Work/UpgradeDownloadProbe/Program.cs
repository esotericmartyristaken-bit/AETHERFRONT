using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using ApfCore;
using KesasInstall;

try {
    string server=Path.GetFullPath("KESAS-VPS-Server");
    var live=ApfClientUpdateManifest.FromJson(File.ReadAllText(Path.Combine(server,"Updates/APFClientUpdate.json")));
    if(live.Version!="kesas-rc22")throw new InvalidOperationException("Run only after RC22 publication");
    string work=Path.GetFullPath("Aetherfront-RC22-Work"),root=Path.Combine(work,"UpgradeDownloadFixture-"+Guid.NewGuid().ToString("N").Substring(0,10));
    ZipFile.ExtractToDirectory(Path.GetFullPath("client-artifacts/Aetherfront-RC21/Aetherfront-Starter-rc21.zip"),root);
    string data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
    File.Copy(Path.Combine(work,"GameStub/bin/Release/net472/GameStub.exe"),Path.Combine(root,"KSP_x64.exe"));
    File.WriteAllText(Path.Combine(data,"privatekey.txt"),"preserve identity");File.WriteAllText(Path.Combine(data,"KesasInstallSelection.txt"),"potato\n");
    string save=Path.Combine(root,"saves/player/persistent.sfs");Directory.CreateDirectory(Path.GetDirectoryName(save));File.WriteAllText(save,"preserve save");
    string cached=Path.Combine(root,"APF/.staging/connect/existing.zip.part.range-205197312-213585919");Directory.CreateDirectory(Path.GetDirectoryName(cached));File.WriteAllText(cached,"preserve downloaded chunk");
    // This executable intentionally links the OLD RC21 engine, proving that
    // players do not need a manual DLL replacement to receive RC22.
    var result=new Installer(root,new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865),"kesas-rc21").Run("potato");
    if(!result.ClientUpdate)throw new Exception("RC21 did not stage the client update");
    var transaction=ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(result.TransactionPath));
    if(transaction.Manifest.Version!="kesas-rc22")throw new Exception("Wrong update version");
    transaction.ProcessId=0;transaction.KspArguments=new List<string>();ApfTransactionSecurity.Sign(transaction,ApfTransactionSecurity.LoadKey(Path.Combine(data,"APFHelper.key")));File.WriteAllText(result.TransactionPath,ApfJson.Serialize(transaction));
    var start=ApfPostExitLauncher.CreateStartInfo(root,result.TransactionPath,true);start.RedirectStandardOutput=true;start.RedirectStandardError=true;
    using(var process=Process.Start(start)){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(60000)||process.ExitCode!=0)throw new Exception("Upgrade helper failed: "+stderr.Result+stdout.Result);}
    foreach(var file in live.Files)if(!File.Exists(Path.Combine(root,file.Path))||ApfJson.Sha256File(Path.Combine(root,file.Path))!=file.Sha256)throw new Exception("Updated file mismatch: "+file.Path);
    if(File.ReadAllText(cached)!="preserve downloaded chunk"||File.ReadAllText(save)!="preserve save"||File.ReadAllText(Path.Combine(data,"privatekey.txt"))!="preserve identity"||File.ReadAllText(Path.Combine(data,"KesasInstallSelection.txt"))!="potato\n")throw new Exception("Cached/player data changed");
    Console.WriteLine("PASS unmodified RC21 engine fetched signed RC22 and native helper installed all "+live.Files.Count+" files; partial chunk, identity, save and Potato preference preserved; no manual repair.");
}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
