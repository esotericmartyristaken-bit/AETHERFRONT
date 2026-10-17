using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using ApfCore;

var work = Path.GetFullPath("KESAS-RC14-Work");
var server = Path.GetFullPath("KESAS-VPS-Server");
var manifestPath = Path.Combine(work, "Candidate/Published/APFClientUpdate.json");
var manifest = ApfClientUpdateManifest.FromJson(File.ReadAllText(manifestPath));
foreach (bool missingFiles in new[] { false, true })
{
    string label = missingFiles ? "damaged-baseline" : "rc13-to-rc14";
    var root = Path.Combine(work, "UpgradeFixture", label);
    if (Directory.Exists(root)) throw new IOException("Fixture already exists: " + root);
    Directory.CreateDirectory(root);
    ZipFile.ExtractToDirectory(Path.Combine(server, "KESAS-Client-Bootstrap.zip"), root);
    var data = Path.Combine(root, "GameData/LunaMultiplayer/Data");
    var ksp = Path.Combine(root, "KSP.exe");
    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ksp);
    var identity = Path.Combine(data, "privatekey.txt");
    File.WriteAllText(identity, "fixture identity must survive");
    var save = Path.Combine(root, "saves/player/persistent.sfs");
    Directory.CreateDirectory(Path.GetDirectoryName(save)!);
    File.WriteAllText(save, "fixture save must survive");
    var selectedServer = File.ReadAllBytes(Path.Combine(data, "APFServer.txt"));
    if (missingFiles)
    {
        // A complete overlay must repair files without relying on an intermediate update.
        foreach (var item in manifest.Files.Where(item => item.Path.Contains("Localization/English/")).Take(5))
            File.Delete(Path.Combine(root, item.Path));
    }
    var stage = Path.Combine(root, "APF/.client-update-staging/" + manifest.Version);
    Directory.CreateDirectory(stage);
    var archive = Path.Combine(stage, "APFClientUpdate.zip");
    File.Copy(Path.Combine(work, "Candidate/Published/APFClient-kesas-rc14.zip"), archive);
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
        !File.ReadAllBytes(Path.Combine(data, "APFServer.txt")).SequenceEqual(selectedServer))
        throw new Exception("Fixture user data changed.");
    Console.WriteLine($"PASS {label}: production post-exit helper applied {manifest.Files.Count} signed files; saves, identity, server selection preserved.");
}

static string HashFile(string path)
{
    using var sha = SHA256.Create();
    return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
}
