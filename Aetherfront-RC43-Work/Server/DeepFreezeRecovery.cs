using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ApfCore;
using LmpCommon;
using Server.Aetherfront;
using Server.Log;

namespace Aetherfront.Personal.Server
{
    // Called only by the native startup validator. Do not infer removal from a
    // missing file alone: frozen crew and unarchived vessels remain fail-closed.
    public static class DeepFreezeRecovery
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        public static void PrepareRecoveredWorld(object coordinator)
        {
            lock (FlightCheckpointRuntime.Gate)
            {
                Type type = coordinator.GetType();
                string root = (string)type.GetField("universeRoot", Fields).GetValue(coordinator);
                var current = (DeepFreezeScenarioCodec.Snapshot)type.GetField("current", Fields).GetValue(coordinator);
                var removed = new HashSet<Guid>();
                var state = AetherfrontServerSystem.Snapshot();
                foreach (var vessel in current.Vessels.Values)
                {
                    string path = Path.Combine(root, "Vessels", vessel.VesselGuid + ".txt");
                    ApfPaths.EnsureNoReparsePoints(root, path);
                    try { File.GetAttributes(path); continue; }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    var craft = state?.FindCraft(vessel.VesselGuid.ToString());
                    if (craft == null || !craft.Archived || !NoFrozenCrew(current, vessel)) return;
                    removed.Add(vessel.VesselGuid);
                }
                if (removed.Count == 0) return;
                var store = (AetherfrontDeepFreezeCheckpointStore)type.GetField("store", Fields).GetValue(coordinator);
                long revision = (long)type.GetField("revision", Fields).GetValue(coordinator);
                byte[] original = (byte[])type.GetField("scenario", Fields).GetValue(coordinator);
                var disk = store.Recover();
                if (disk.Revision != revision || !disk.ScenarioData.SequenceEqual(original))
                    throw new IOException("DeepFreeze state changed during startup recovery.");
                var clean = new DeepFreezeScenarioCodec.Snapshot(current.ScenarioName, current.Scenes, current.Enabled,
                    current.Kerbals.Values, current.Vessels.Values.Where(v => !removed.Contains(v.VesselGuid)),
                    current.Parts.Values.Where(p => !removed.Contains(p.VesselGuid)),
                    current.Alarms.Values.Where(a => !removed.Contains(a.VesselGuid)));
                byte[] bytes = DeepFreezeScenarioCodec.Serialize(clean);
                string recovery = Path.Combine(root, "DeepFreeze", "Recovery");
                ApfPaths.EnsureNoReparsePoints(root, recovery);
                Directory.CreateDirectory(recovery);
                string backup = Path.Combine(recovery, "archived-unfrozen-" + Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant() + ".txt");
                if (File.Exists(backup))
                {
                    if (!File.ReadAllBytes(backup).SequenceEqual(original)) throw new IOException("DeepFreeze recovery evidence differs.");
                }
                else
                {
                    using var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    file.Write(original, 0, original.Length); file.Flush(true);
                }
                // Native journal commits only the scenario: no vessel, roster,
                // progression, ownership, or personal-clock writes are made.
                var committed = store.Commit(revision, bytes, new Dictionary<Guid, byte[]>(),
                    new Dictionary<string, byte[]>(), Array.Empty<Guid>());
                type.GetField("revision", Fields).SetValue(coordinator, committed.Revision);
                type.GetField("scenario", Fields).SetValue(coordinator, committed.ScenarioData);
                type.GetField("current", Fields).SetValue(coordinator, clean);
                LunaLog.Normal("DeepFreeze startup recovery removed archived non-frozen vessel metadata: " + removed.Count + "; original scenario retained in DeepFreeze/Recovery; roster unchanged.");
            }
        }

        private static bool NoFrozenCrew(DeepFreezeScenarioCodec.Snapshot source, DeepFreezeScenarioCodec.VesselRecord vessel)
        {
            Guid id = vessel.VesselGuid;
            // Ordinary crew names/counts are cached UI metadata here. Their
            // authoritative roster is deliberately not altered by this cleanup.
            return vessel.NumFrozenCrew == 0 &&
                !source.Kerbals.Values.Any(k => k.VesselGuid == id) &&
                source.Parts.Values.Where(p => p.VesselGuid == id).All(p => p.NumFrozenCrew == 0) &&
                source.Alarms.Values.Where(a => a.VesselGuid == id).All(a =>
                    (a.FrozenKerbals == null || a.FrozenKerbals.Count == 0) && (a.ThawedKerbals == null || a.ThawedKerbals.Count == 0));
        }
    }
}
