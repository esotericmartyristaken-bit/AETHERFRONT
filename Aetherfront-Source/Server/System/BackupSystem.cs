using Server.Context;
using Server.Events;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Server.System
{
    public class BackupSystem
    {
        //Subscribe to the exit event so a backup is performed when closing the server
        static BackupSystem() => ExitEvent.ServerClosing += RunBackup;

        private static readonly object LockObj = new object();

        public static async void PerformBackups(CancellationToken token)
        {
            while (ServerContext.ServerRunning)
            {
                if (ServerContext.PlayerCount > 0)
                {
                    RunBackup();
                }

                try
                {
                    await Task.Delay(IntervalSettings.SettingsStore.BackupIntervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        public static void RunBackup()
        {
            lock (LockObj)
            {
                LunaLog.Debug("Performing backups...");
                var successful = true;
                successful &= TryRunBackupStep("vessels", VesselStoreSystem.BackupVessels);
                successful &= TryRunBackupStep("subspaces", WarpSystem.BackupSubspaces);
                successful &= TryRunBackupStep("start time", TimeSystem.BackupStartTime);
                successful &= TryRunBackupStep("scenarios", ScenarioStoreSystem.BackupScenarios);
                LunaLog.Debug(successful ? "Backups done" : "Backups completed with errors");
            }
        }

        /// <summary>
        /// Keep an individual persistence failure inside the backup subsystem. The
        /// async backup loop must not terminate Server.exe; independent state still
        /// gets saved and the complete exception remains available in the log.
        /// </summary>
        private static bool TryRunBackupStep(string name, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                LunaLog.Error($"Failed to back up {name}: {e}");
                return false;
            }
        }
    }
}
