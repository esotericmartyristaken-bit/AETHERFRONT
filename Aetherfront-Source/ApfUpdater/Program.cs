using System;
using System.IO;
using ApfCore;

namespace ApfUpdater
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var transactionPath = GetArgument(args, "--transaction");
                var clientUpdatePath = GetArgument(args, "--client-update");
                if ((!string.IsNullOrWhiteSpace(transactionPath) && !string.IsNullOrWhiteSpace(clientUpdatePath)) ||
                    (string.IsNullOrWhiteSpace(transactionPath) && string.IsNullOrWhiteSpace(clientUpdatePath)))
                    throw new InvalidDataException("Specify exactly one of --transaction or --client-update");
                var selectedPath = !string.IsNullOrWhiteSpace(clientUpdatePath) ? clientUpdatePath : transactionPath;
                if (!File.Exists(selectedPath)) throw new InvalidDataException("Missing APF update transaction file");

                selectedPath = Path.GetFullPath(selectedPath);
                var gameRoot = Path.GetFullPath(ReadGameRoot(selectedPath, !string.IsNullOrWhiteSpace(clientUpdatePath)));
                var helperRoot = Path.GetFullPath(Path.Combine(gameRoot, "GameData", "LunaMultiplayer"));
                if (!string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    helperRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("APFUpdater must run from the installed GameData/LunaMultiplayer directory");
                if (!ApfPaths.IsUnder(selectedPath, gameRoot))
                    throw new InvalidDataException("Transaction file escapes the game root");
                var stagingRoot = Path.Combine(gameRoot, "APF", !string.IsNullOrWhiteSpace(clientUpdatePath) ? ".client-update-staging" : ".staging");
                if (!ApfPaths.IsUnder(selectedPath, stagingRoot))
                    throw new InvalidDataException("Transaction file is outside the APF staging directory");
                ApfPaths.EnsureNoReparsePoints(gameRoot, stagingRoot);
                ApfPaths.EnsureNoReparsePoints(gameRoot, selectedPath);
                var keyPath = Path.Combine(helperRoot, "Data", "APFHelper.key");
                var key = ApfTransactionSecurity.LoadKey(keyPath);
                if (!string.IsNullOrWhiteSpace(clientUpdatePath))
                {
                    var transaction = ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(selectedPath));
                    if (!ApfTransactionSecurity.Verify(transaction, key))
                        throw new InvalidDataException("APF client update authorization failed");
                    ApfArchiveInstaller.ApplyClientUpdate(transaction);
                }
                else
                {
                    var transaction = ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(selectedPath));
                    if (!ApfTransactionSecurity.Verify(transaction, key))
                        throw new InvalidDataException("APF transaction authorization failed");
                    ApfArchiveInstaller.ApplyTransaction(transaction);
                }
                return 0;
            }
            catch (Exception exception)
            {
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "APFUpdater.log"), DateTime.UtcNow.ToString("O") + " " + exception + Environment.NewLine); }
                catch { }
                return 1;
            }
        }

        private static string GetArgument(string[] args, string name)
        {
            for (var index = 0; index + 1 < args.Length; index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }

        private static string ReadGameRoot(string transactionPath, bool clientUpdate)
        {
            var json = File.ReadAllText(transactionPath);
            return clientUpdate
                ? ApfJson.Deserialize<ApfClientUpdateTransaction>(json).GameRoot
                : ApfJson.Deserialize<ApfSyncTransaction>(json).GameRoot;
        }
    }
}
