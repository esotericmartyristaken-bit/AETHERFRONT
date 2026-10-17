using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using ApfCore;

var work = Path.GetFullPath("Aetherfront-RC22-Work");
var server = Path.GetFullPath("KESAS-VPS-Server");
var manifestPath = Path.Combine(work, "Candidate/Published/APFClientUpdate.json");
var manifest = ApfClientUpdateManifest.FromJson(File.ReadAllText(manifestPath));
string runId=Guid.NewGuid().ToString("N").Substring(0,10);
try {
foreach (string baseline in args.Length>0 ? args : new[] { "rc13", "rc14", "rc15", "rc16", "rc17", "rc18", "rc19", "rc20", "rc21", "damaged-rc13", "potato-rc16" })
{
    bool missingFiles = baseline == "damaged-rc13";
    string label = baseline + "-to-rc22";
    var root = Path.Combine(work, "UpgradeFixture", runId, label);
    if (Directory.Exists(root)) throw new IOException("Fixture already exists: " + root);
    Directory.CreateDirectory(root);
    string bootstrap = baseline=="rc21" ? Path.Combine(server,"Aetherfront-RC21-Export.zip") : baseline=="rc20" ? Path.Combine(server,"Aetherfront-RC20-Export.zip") : baseline=="rc19" ? Path.Combine(server,"Aetherfront-RC19-Export.zip") : baseline=="rc18" ? Path.Combine(server,"Aetherfront-RC18-Export.zip") : baseline=="rc17" ? Path.Combine(server,"KESAS-RC17-Export.zip") : baseline.Contains("rc16") ? Path.Combine(server,"KESAS-RC16-Export.zip") : baseline.Contains("rc15") ? Path.GetFullPath("KESAS-RC16-Work/Inputs/Bootstrap-rc15.zip") : baseline == "rc14" ?
        Path.Combine(server,"Backups/updates/20261016-052405-499-kesas-rc15-5b63f5accbc140a68265112d8ad8fc1a/files/KESAS-Client-Bootstrap.zip") :
        Path.Combine(server, "Backups/updates/20261016-030510-626-kesas-rc14-cb3cf079319e46cbb51ce90ea84edb08/files/KESAS-Client-Bootstrap.zip");
    ZipFile.ExtractToDirectory(bootstrap, root);
    var data = Path.Combine(root, "GameData/LunaMultiplayer/Data");
    var ksp = Path.Combine(root, "KSP_x64.exe");
    File.Copy(Path.Combine(work,"GameStub/bin/Release/net472/GameStub.exe"), ksp);
    var identity = Path.Combine(data, "privatekey.txt");
    File.WriteAllText(identity, "fixture identity must survive");
    var save = Path.Combine(root, "saves/player/persistent.sfs");
    Directory.CreateDirectory(Path.GetDirectoryName(save)!);
    File.WriteAllText(save, "fixture save must survive");
    var selectedServer = File.ReadAllBytes(Path.Combine(data, "APFServer.txt"));
    string profile=Path.Combine(data,"KesasInstallProfile.json");
    File.WriteAllText(profile,"{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc15\"}");
    var selection=Path.Combine(data,"KesasInstallSelection.txt");File.WriteAllText(selection,"potato\n");
    if(baseline=="potato-rc16") foreach(string visual in File.ReadAllLines(Path.GetFullPath("KESAS-RC16-Work/visual-roots.txt"))) {
        if(string.IsNullOrWhiteSpace(visual))continue;
        string target=Path.GetFullPath(Path.Combine(root,"GameData",visual));
        if(!target.StartsWith(Path.Combine(root,"GameData")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe fixture path");
        if(Directory.Exists(target))Directory.Delete(target,true);else if(File.Exists(target))File.Delete(target);
    }
    if (missingFiles)
    {
        // A complete overlay must repair files without relying on an intermediate update.
        foreach (var item in manifest.Files.Where(item => item.Path.Contains("Localization/English/")).Take(5))
            File.Delete(Path.Combine(root, item.Path));
    }
    var stage = Path.Combine(root, "APF/.client-update-staging/" + manifest.Version);
    Directory.CreateDirectory(stage);
    var archive = Path.Combine(stage, "APFClientUpdate.zip");
    File.Copy(Path.Combine(work, "Candidate/Published/APFClient-kesas-rc22.zip"), archive);
    var transaction = new ApfClientUpdateTransaction
    {
        GameRoot = root,
        GameDataPath = Path.Combine(root, "GameData"),
        ProcessId = 0,
        KspExecutable = ksp,
        KspArguments = new List<string>(),
        ArchivePath = archive,
        Manifest = manifest,
        Approval = ApfClientUpdateApproval.Capture(root)
    };
    ApfTransactionSecurity.Sign(transaction, ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data, "APFHelper.key")));
    var path = Path.Combine(stage, "transaction.json");
    File.WriteAllText(path, ApfJson.Serialize(transaction));
    var start = ApfPostExitLauncher.CreateStartInfo(root, path, true);
    start.RedirectStandardError = true;
    start.RedirectStandardOutput = true;
    using var process = Process.Start(start) ?? throw new IOException("Helper did not start");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    var helperClock = Stopwatch.StartNew();
    while (!process.WaitForExit(15000))
    {
        Console.WriteLine($"PROGRESS {label}: owned post-exit helper running for {helperClock.Elapsed.TotalSeconds:0} seconds.");
        if (helperClock.Elapsed < TimeSpan.FromMinutes(5)) continue;
        process.Kill();
        process.WaitForExit(10000);
        throw new TimeoutException("Owned fixture helper exceeded five minutes: " + label);
    }
    if (process.ExitCode != 0) throw new Exception("Helper failed: " + stderr.Result + stdout.Result);
    foreach (var file in manifest.Files)
    {
        var installed = Path.Combine(root, file.Path);
        if (!File.Exists(installed) || new FileInfo(installed).Length != file.SizeBytes ||
            !string.Equals(HashFile(installed), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Installed hash mismatch: " + file.Path);
    }
    if (File.ReadAllText(identity) != "fixture identity must survive" ||
        File.ReadAllText(save) != "fixture save must survive" ||
        File.ReadAllText(profile)!="{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc15\"}" || File.ReadAllText(selection)!="potato\n" ||
        !File.ReadAllBytes(Path.Combine(data, "APFServer.txt")).SequenceEqual(selectedServer))
        throw new Exception("Fixture user data changed.");
    if(baseline=="potato-rc16") foreach(string visual in File.ReadAllLines(Path.GetFullPath("KESAS-RC16-Work/visual-roots.txt")))
        if(!string.IsNullOrWhiteSpace(visual) && (Directory.Exists(Path.Combine(root,"GameData",visual)) || File.Exists(Path.Combine(root,"GameData",visual)))) throw new Exception("Client update reintroduced a visual root: "+visual);
    Console.WriteLine($"PASS {label}: production post-exit helper applied {manifest.Files.Count} signed files; saves, identity, server selection preserved.");
}
}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}

static string HashFile(string path)
{
    using var sha = SHA256.Create();
    return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
}
