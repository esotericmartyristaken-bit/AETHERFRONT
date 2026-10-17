using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ApfCore;

namespace ApfUpdaterTests
{
    internal static class Program
    {
        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "apf-updater-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var workingDirectory = Directory.GetCurrentDirectory();
                var helperCandidates = new[]
                {
                    Path.Combine(workingDirectory, "Build", "APF", "APFUpdaterClient"),
                    Path.Combine(workingDirectory, "Build", "APF", "APFUpdater"),
                    Path.Combine(workingDirectory, "ApfUpdater", "bin", "Release", "net6.0", "win-x64"),
                    Path.Combine(workingDirectory, "ApfUpdater", "bin", "Release", "net6.0", "win-x64", "publish"),
                    Path.Combine(workingDirectory, "LunaMultiplayer-Source", "ApfUpdater", "bin", "Release", "net6.0", "win-x64"),
                    Path.Combine(workingDirectory, "LunaMultiplayer-Source", "ApfUpdater", "bin", "Release", "net6.0", "win-x64", "publish"),
                    Path.Combine(workingDirectory, "LunaMultiplayer-Source", "Build", "APF", "APFUpdater")
                };
                var helperSource = helperCandidates.FirstOrDefault(Directory.Exists);
                if (string.IsNullOrEmpty(helperSource))
                    throw new DirectoryNotFoundException("APFUpdater publish output was not found. Run Documentation/Publish-APFArtifacts.ps1 first.");
                var helperRoot = Path.Combine(root, "GameData", "LunaMultiplayer");
                var gameData = Path.Combine(root, "GameData");
                var plugins = Path.Combine(helperRoot, "Plugins");
                var data = Path.Combine(helperRoot, "Data");
                Directory.CreateDirectory(plugins);
                Directory.CreateDirectory(data);
                foreach (var source in Directory.GetFiles(helperSource, "*", SearchOption.TopDirectoryOnly))
                    File.Copy(source, Path.Combine(helperRoot, Path.GetFileName(source)), true);

                var updateFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GameData/LunaMultiPlayer/Plugins/LmpClient.dll"] = "updated-client",
                    ["GameData/LunaMultiPlayer/Plugins/ApfCore.dll"] = "updated-core"
                };
                foreach (var item in updateFiles)
                {
                    var target = Path.Combine(root, item.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllText(target, "old-" + item.Value);
                }

                var manifest = new ApfClientUpdateManifest
                {
                    Version = "apf-client-2",
                    PackageUrl = "https://updates.example.invalid/APFClient-2.zip"
                };
                var staging = Path.Combine(root, "APF", ".client-update-staging", "apf-client-2");
                Directory.CreateDirectory(staging);
                var archive = Path.Combine(staging, "APFClientUpdate.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    foreach (var item in updateFiles)
                    {
                        var entry = zip.CreateEntry(item.Key);
                        var bytes = Encoding.UTF8.GetBytes(item.Value);
                        using (var output = entry.Open()) output.Write(bytes, 0, bytes.Length);
                        manifest.Files.Add(new ApfClientUpdateFile
                        {
                            Path = item.Key,
                            Sha256 = ApfJson.ToHex(System.Security.Cryptography.SHA256.Create().ComputeHash(bytes)),
                            SizeBytes = bytes.Length
                        });
                    }
                }
                manifest.Sha256 = ApfJson.Sha256File(archive);
                manifest.SizeBytes = new FileInfo(archive).Length;

                var key = ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(data, "APFHelper.key"));
                var fakeKsp = Path.Combine(root, "KSP.exe");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), fakeKsp, true);
                var transactionPath = Path.Combine(staging, "transaction.json");
                var transaction = new ApfClientUpdateTransaction
                {
                    GameRoot = root,
                    GameDataPath = gameData,
                    ProcessId = 0,
                    KspExecutable = fakeKsp,
                    KspArguments = new List<string> { "/c", "exit" },
                    ArchivePath = archive,
                    Manifest = manifest
                };
                ApfTransactionSecurity.Sign(transaction, key);
                File.WriteAllText(transactionPath, ApfJson.Serialize(transaction));

                var helper = Path.Combine(helperRoot, "ApfUpdater.exe");
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = helper,
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    Arguments = ApfProcessArguments.Join(new[] { "--client-update", transactionPath })
                });
                if (process == null || !process.WaitForExit(60000)) throw new InvalidOperationException("APFUpdater did not exit");
                Assert(process.ExitCode == 0, "APFUpdater exit code");
                foreach (var item in updateFiles)
                    Assert(File.ReadAllText(Path.Combine(root, item.Key.Replace('/', Path.DirectorySeparatorChar))) == item.Value, "helper applied " + item.Key);
                Assert(File.Exists(Path.Combine(root, "APF", "APFTransaction.log")), "helper transaction log");

                var misplacedTransaction = Path.Combine(root, "misplaced-update.json");
                File.Copy(transactionPath, misplacedTransaction, true);
                var misplacedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = helper,
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    Arguments = ApfProcessArguments.Join(new[] { "--client-update", misplacedTransaction })
                });
                if (misplacedProcess == null || !misplacedProcess.WaitForExit(60000)) throw new InvalidOperationException("APFUpdater misplaced transaction did not exit");
                Assert(misplacedProcess.ExitCode != 0, "helper rejects transaction outside staging");
                Console.WriteLine("APF updater integration passed");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Failed: " + message);
        }
    }
}
