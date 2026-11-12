using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Aetherfront.Core;
using LmpCommon;
using Server.Context;
using Server.System;

namespace Aetherfront.Synchronization.Server
{
    public static class CheckpointFleet
    {
        static readonly ConcurrentDictionary<string, int> RegistryOnly = new ConcurrentDictionary<string, int>();
        public static List<CraftRecord> Owned(AetherfrontState state, string actor)
        {
            var registered = state.Crafts.Where(c => !c.Archived && c.OwnerAccountId == actor).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            var included = new List<CraftRecord>(); var history = new List<CraftRecord>();
            string scenario = Path.Combine(ServerContext.UniverseDirectory, "DeepFreeze", "Scenario.txt");
            DeepFreezeScenarioCodec.Snapshot frozen = null;
            if (File.Exists(scenario))
            {
                using (var input = new FileStream(scenario, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var image = new MemoryStream())
                {
                    if (input.Length > 524288) throw new InvalidDataException("Shared crew scenario exceeds checkpoint limit.");
                    input.CopyTo(image); var data = image.ToArray();
                    if (data.Length > 524288) throw new InvalidDataException("Shared crew scenario exceeds checkpoint limit.");
                    frozen = DeepFreezeScenarioCodec.Parse(data, data.Length);
                }
            }
            foreach (var craft in registered)
            {
                Guid id = Guid.Parse(craft.Id);
                // Docking/foreign-owner validation remains in the native adapter.
                // Child registrations are required to preserve docking identity.
                if (!string.IsNullOrEmpty(craft.DockedToCraftId)) { included.Add(craft); continue; }
                bool removed = VesselContext.RemovedVessels.ContainsKey(id);
                if (!removed && VesselStoreSystem.CurrentVessels.ContainsKey(id)) { included.Add(craft); continue; }
                bool disk = File.Exists(Path.Combine(VesselStoreSystem.VesselsPath, id + ".txt"));
                bool locked = global::Server.System.LockSystem.LockQuery.ControlLockExists(id);
                bool shared = frozen != null && frozen.Vessels.ContainsKey(id);
                bool quarantined = Directory.Exists(Path.Combine(ServerContext.UniverseDirectory, "Vessels-Quarantine")) &&
                    Directory.EnumerateFiles(Path.Combine(ServerContext.UniverseDirectory, "Vessels-Quarantine"), "*", SearchOption.AllDirectories).Any(p => p.IndexOf(id.ToString(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (locked || shared || quarantined || (!removed && disk))
                    throw new InvalidOperationException("An active fleet vessel is unavailable for checkpointing: " + id + ". Existing checkpoint and registry data are preserved.");
                // The native live universe, not the design/ownership registry,
                // defines the fleet. Keep non-live registrations and their exact
                // metadata; never archive/delete them to make a save succeed.
                history.Add(craft);
            }
            if (history.Count != 0) PreserveHistory(state, actor, history);
            RegistryOnly[actor] = history.Count;
            return included;
        }
        static void PreserveHistory(AetherfrontState state, string actor, List<CraftRecord> history)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(AetherfrontJson.Serialize(history));
            string name; using (var sha = SHA256.Create()) name = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(actor))).ToLowerInvariant() + "-" + Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
            string directory = Path.Combine(ServerContext.UniverseDirectory, "FlightCheckpoints", "RegistryHistory");
            Directory.CreateDirectory(directory); string target = Path.Combine(directory, name + ".json");
            if (File.Exists(target))
            { if (!File.ReadAllBytes(target).SequenceEqual(bytes)) throw new IOException("Checkpoint registration history differs."); return; }
            string staging = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                File.Move(staging, target);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        public static string Status(string original, string actor)
        {
            return RegistryOnly.TryGetValue(actor, out int count) && count > 0 ?
                (original ?? "") + " Checkpoints contain the live universe fleet; " + count + " registry-only craft records remain unchanged outside the restored fleet." : original;
        }
    }
}
