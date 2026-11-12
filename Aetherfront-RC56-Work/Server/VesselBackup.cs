using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
            internal byte[] UnchangedIdentity;
        }
        static readonly ConcurrentDictionary<Guid, Version> Versions = new ConcurrentDictionary<Guid, Version>();
        static readonly object Fleet = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontFleetGuard", true)
            .GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static readonly object Backup = typeof(VesselStoreSystem).GetField("BackupLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static readonly MethodInfo Healthy = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontDeepFreezeSystem", true)
            .GetMethod("EnsureHealthy", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo PathLock = typeof(FileHandler).GetMethod("GetLockSemaphore", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        delegate bool FileInformation(IntPtr file, IntPtr information);
        static readonly FileInformation Information = Marshal.GetDelegateForFunctionPointer<FileInformation>(NativeLibrary.GetExport(
            NativeLibrary.Load("kernel32.dll", typeof(VesselBackup).Assembly, DllImportSearchPath.System32), "GetFileInformationByHandle"));
        public static long UnchangedSkipped, DurableStages;
        static object Semaphore(Plan plan) { return PathLock.Invoke(null, new object[] { plan.Destination }); }
        static byte[] Identity(FileStream stream)
        {
            // BY_HANDLE_FILE_INFORMATION: retain volume/file ID, creation/write
            // times, size and attributes; exclude access time and link count.
            IntPtr memory = Marshal.AllocHGlobal(52);
            try
            {
                if (!Information(stream.SafeFileHandle.DangerousGetHandle(), memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var raw = new byte[52]; Marshal.Copy(memory, raw, 0, raw.Length);
                return raw.Take(12).Concat(raw.Skip(20).Take(20)).Concat(raw.Skip(44).Take(8)).ToArray();
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        static bool Unchanged(Plan plan)
        {
            long compare = SyncTimings.Start();
            try
            {
                // Only the native per-path writer lock is held during reads.
                // No Fleet or Backup lock, and no canonical handle blocks replace.
                lock (Semaphore(plan))
                using (var stream = new FileStream(plan.Destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length != plan.Bytes.Length) return false;
                    byte[] identity = Identity(stream), buffer = new byte[81920]; int offset = 0, count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (offset + count > plan.Bytes.Length || !buffer.AsSpan(0, count).SequenceEqual(plan.Bytes.AsSpan(offset, count))) return false;
                        offset += count;
                    }
                    if (offset != plan.Bytes.Length || !identity.SequenceEqual(Identity(stream))) return false;
                    plan.UnchangedIdentity = identity; return true;
                }
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            finally { SyncTimings.Complete("backup.compare", compare); }
        }
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
                // Restore native no-op semantics: an identical canonical record
                // needs no new temporary write or expensive durable disk flush.
                if (Unchanged(plan)) return;
                // The large write and durable flush never own Fleet or BackupLock.
                using (var stream = new FileStream(plan.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { SyncTimings.Write(stream, plan.Bytes, 0, plan.Bytes.Length); SyncTimings.Flush(stream, true); }
                Interlocked.Increment(ref DurableStages);
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
                    object pathLock = Semaphore(plan);
                    // Never wait for another path reader/writer while gameplay
                    // locks are held. A later periodic capture retries contention.
                    if (!Monitor.TryEnter(pathLock)) return false;
                    try
                    {
                        if (File.Exists(plan.Destination) && (File.GetAttributes(plan.Destination) & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                            throw new InvalidDataException("Vessel backup target must be a writable regular file.");
                        if (plan.UnchangedIdentity != null)
                        {
                            try
                            {
                                using var canonical = new FileStream(plan.Destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                if (!plan.UnchangedIdentity.SequenceEqual(Identity(canonical))) return false;
                            }
                            catch (FileNotFoundException) { return false; }
                            Interlocked.Increment(ref UnchangedSkipped);
                        }
                        else File.Move(plan.Temporary, plan.Destination, true);
                    }
                    finally { Monitor.Exit(pathLock); }
                }
                SyncTimings.Complete("backup.publish", publish); return true;
        }
        internal static void Discard(Plan plan) { if (File.Exists(plan.Temporary)) File.Delete(plan.Temporary); }
    }
}
