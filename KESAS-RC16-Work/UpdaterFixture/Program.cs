using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using ApfCore;

var work = Path.GetFullPath("KESAS-RC16-Work");
var server = Path.GetFullPath("KESAS-VPS-Server");
var manifestPath = Path.Combine(work, "Candidate/Published/APFClientUpdate.json");
var manifest = ApfClientUpdateManifest.FromJson(File.ReadAllText(manifestPath));
string runId=Guid.NewGuid().ToString("N").Substring(0,10);
foreach (string baseline in new[] { "rc13", "rc14", "rc15", "damaged-rc13", "potato-rc15" })
{
    bool missingFiles = baseline == "damaged-rc13";
    string label = baseline + "-to-rc16";
    var root = Path.Combine(work, "UpgradeFixture", runId, label);
    if (Directory.Exists(root)) throw new IOException("Fixture already exists: " + root);
    Directory.CreateDirectory(root);
    string bootstrap = baseline.Contains("rc15") ? Path.Combine(work,"Inputs/Bootstrap-rc15.zip") : baseline == "rc14" ?
        Path.Combine(server,"Backups/updates/20261016-052405-499-kesas-rc15-5b63f5accbc140a68265112d8ad8fc1a/files/KESAS-Client-Bootstrap.zip") :
        Path.Combine(server, "Backups/updates/20261016-030510-626-kesas-rc14-cb3cf079319e46cbb51ce90ea84edb08/files/KESAS-Client-Bootstrap.zip");
    ZipFile.ExtractToDirectory(bootstrap, root);
    var data = Path.Combine(root, "GameData/LunaMultiplayer/Data");
    var ksp = Path.Combine(root, "KSP.exe");
    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ksp);
    var identity = Path.Combine(data, "privatekey.txt");
    File.WriteAllText(identity, "fixture identity must survive");
    var save = Path.Combine(root, "saves/player/persistent.sfs");
    Directory.CreateDirectory(Path.GetDirectoryName(save)!);
    File.WriteAllText(save, "fixture save must survive");
    var selectedServer = File.ReadAllBytes(Path.Combine(data, "APFServer.txt"));
    string profile=Path.Combine(data,"KesasInstallProfile.json");
    File.WriteAllText(profile,"{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc15\"}");
    if(baseline=="potato-rc15") foreach(string visual in File.ReadAllLines(Path.Combine(work,"visual-roots.txt"))) {
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
    File.Copy(Path.Combine(work, "Candidate/Published/APFClient-kesas-rc16.zip"), archive);
    var transaction = new ApfClientUpdateTransaction
    {
        GameRoot = root,
        GameDataPath = Path.Combine(root, "GameData"),
        ProcessId = 0,
        KspExecutable = ksp,
        KspArguments = new List<string> { "/c", "exit" },
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
    if (!process.WaitForExit(60000)) throw new TimeoutException("Helper timed out: " + label);
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
        File.ReadAllText(profile)!="{\"mode\":\"potato\",\"catalogVersion\":\"kesas-rc15\"}" ||
        !File.ReadAllBytes(Path.Combine(data, "APFServer.txt")).SequenceEqual(selectedServer))
        throw new Exception("Fixture user data changed.");
    if(baseline=="potato-rc15") foreach(string visual in File.ReadAllLines(Path.Combine(work,"visual-roots.txt")))
        if(!string.IsNullOrWhiteSpace(visual) && (Directory.Exists(Path.Combine(root,"GameData",visual)) || File.Exists(Path.Combine(root,"GameData",visual)))) throw new Exception("Client update reintroduced a visual root: "+visual);
    Console.WriteLine($"PASS {label}: production post-exit helper applied {manifest.Files.Count} signed files; saves, identity, server selection preserved.");
}

static string HashFile(string path)
{
    using var sha = SHA256.Create();
    return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
}
