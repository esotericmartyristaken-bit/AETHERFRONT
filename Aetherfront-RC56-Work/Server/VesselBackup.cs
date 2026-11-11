using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Server.Aetherfront;
using Server.System;

namespace Aetherfront.Synchronization.Server
{
    public static class VesselBackup
    {
        internal sealed class Version { internal long Captured; }
        internal sealed class Plan
        {
            internal Guid Id;
            internal long Sequence;
            internal Version Version;
            internal object Identity;
            internal string Directory, Destination, Temporary;
            internal byte[] Bytes;
        }
        static readonly ConcurrentDictionary<Guid, Version> Versions = new ConcurrentDictionary<Guid, Version>();
        static readonly object Fleet = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontFleetGuard", true)
            .GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static readonly object Backup = typeof(VesselStoreSystem).GetField("BackupLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static readonly MethodInfo Healthy = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontDeepFreezeSystem", true)
            .GetMethod("EnsureHealthy", BindingFlags.Static | BindingFlags.NonPublic);
        public static void Run()
        {
            foreach (var id in VesselStoreSystem.CurrentVessels.Keys.ToArray()) Save(id);
        }
        public static bool Save(Guid id)
        {
            var plan = Capture(id); if (plan == null) return false;
            try { Stage(plan); return Publish(plan); }
            finally { Discard(plan); }
        }
        internal static Plan Capture(Guid id)
        {
            string text, directory; object identity; long sequence;
            var version = Versions.GetOrAdd(id, _ => new Version());
            long capture = SyncTimings.Start();
            using (SyncTimings.FleetScope())
            {
                Healthy.Invoke(null, null);
                if (VesselContext.RemovedVessels.ContainsKey(id) || !VesselStoreSystem.CurrentVessels.TryGetValue(id, out var vessel)) return null;
                directory = Path.GetFullPath(VesselStoreSystem.VesselsPath); identity = vessel;
                sequence = checked(++version.Captured);
                long serialize = SyncTimings.Start();
                try { text = vessel.ToString(); } finally { SyncTimings.Complete("backup.serialize", serialize); }
            }
            SyncTimings.Complete("backup.capture", capture);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Vessel backup directory must not be a link.");
            string destination = Path.Combine(directory, id.ToString() + ".txt");
            string temporary = Path.Combine(directory, id.ToString() + "." + Guid.NewGuid().ToString("N") + ".pending");
            return new Plan { Id = id, Sequence = sequence, Version = version, Identity = identity, Directory = directory,
                Destination = destination, Temporary = temporary, Bytes = Encoding.UTF8.GetBytes(text) };
        }
        internal static void Stage(Plan plan)
        {
                // The large write and durable flush never own Fleet or BackupLock.
                using (var stream = new FileStream(plan.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { SyncTimings.Write(stream, plan.Bytes, 0, plan.Bytes.Length); SyncTimings.Flush(stream, true); }
        }
        internal static bool Publish(Plan plan)
        {
                long publish = SyncTimings.Start();
                using (SyncTimings.FleetScope()) using (SyncTimings.BackupScope())
                {
                    Healthy.Invoke(null, null);
                    // A removed/replaced identity or a newer capture invalidates this staged file.
                    if (plan.Version.Captured != plan.Sequence || VesselContext.RemovedVessels.ContainsKey(plan.Id) ||
                        !VesselStoreSystem.CurrentVessels.TryGetValue(plan.Id, out var current) || !ReferenceEquals(current, plan.Identity)) return false;
                    if (File.Exists(plan.Destination) && (File.GetAttributes(plan.Destination) & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                        throw new InvalidDataException("Vessel backup target must be a writable regular file.");
                    File.Move(plan.Temporary, plan.Destination, true);
                }
                SyncTimings.Complete("backup.publish", publish); return true;
        }
        internal static void Discard(Plan plan) { if (File.Exists(plan.Temporary)) File.Delete(plan.Temporary); }
    }
}
