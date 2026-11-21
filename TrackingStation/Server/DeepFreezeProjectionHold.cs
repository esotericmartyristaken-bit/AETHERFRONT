using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using ApfCore;
using LmpCommon;
using LunaConfigNode.CfgNode;
using Server.Aetherfront;
using Server.Log;
using Server.System;

namespace Aetherfront.Tracking
{
    // Hold the original committed scenario in memory and on disk. A hold
    // neither synthesizes a vessel nor archives, thaws, or frees its crew.
    public static class DeepFreezeProjectionHold
    {
        private static readonly ConditionalWeakTable<object, HashSet<Guid>> Held = new();
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public static bool Contains(object coordinator, Guid id) =>
            Held.TryGetValue(coordinator, out var ids) && ids.Contains(id);

        public static IEnumerable<Guid> ValidateMissing(object coordinator, string root,
            DeepFreezeScenarioCodec.Snapshot committed, Dictionary<Guid, byte[]> projections,
            Dictionary<Guid, AetherfrontDeepFreezeCheckpointValidator.VesselAssignments> assignments)
        {
            var missing = new HashSet<Guid>(committed.Vessels.Keys);
            missing.ExceptWith(projections.Keys);
            if (missing.Count == 0) return committed.Vessels.Keys;
            var awake = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var occupiedParts = new HashSet<uint>();
            foreach (var vessel in assignments.Values)
            foreach (var part in vessel.Parts)
            {
                occupiedParts.Add(part.Key);
                awake.UnionWith(part.Value);
            }

            foreach (var id in missing)
            {
                string path = Path.Combine(root, "Vessels", id + ".txt");
                ApfPaths.EnsureNoReparsePoints(root, path);
                try
                {
                    File.GetAttributes(path);
                    throw InvalidHold(id, "projection exists but was not parsed");
                }
                catch (FileNotFoundException) { }

                var crew = committed.Kerbals.Values.Where(k => k.VesselGuid == id).ToArray();
                var parts = committed.Parts.Values.Where(p => p.VesselGuid == id).ToArray();
                if (committed.Vessels[id].NumFrozenCrew != crew.Length ||
                    committed.Alarms.Values.Any(a => a.VesselGuid == id) ||
                    parts.Any(p => occupiedParts.Contains(p.FlightId) ||
                        p.NumFrozenCrew != crew.Count(k => k.PartId == p.FlightId)))
                    throw InvalidHold(id, "inconsistent vessel, freezer, or alarm metadata");

                var seats = new HashSet<(uint, int)>();
                foreach (var kerbal in crew)
                {
                    if (awake.Contains(kerbal.KerbalName) || kerbal.Type != "Unowned" ||
                        (kerbal.Status != "Dead" && kerbal.Status != "Missing") ||
                        !committed.Parts.TryGetValue(kerbal.PartId, out var freezer) ||
                        freezer.VesselGuid != id || kerbal.SeatIndex < 0 || kerbal.SeatIndex >= freezer.NumSeats ||
                        !seats.Add((kerbal.PartId, kerbal.SeatIndex)))
                        throw InvalidHold(id, "conflicting crew or freezer seat");
                    ValidateRoster(root, kerbal, id);
                }
            }

            Held.Add(coordinator, missing);
            LunaLog.Normal($"DeepFreeze: preserving {missing.Count} committed vessels whose projections are unavailable; all other vessels retain full startup validation.");
            return committed.Vessels.Keys.Where(id => !missing.Contains(id));
        }

        private static void ValidateRoster(string root, DeepFreezeScenarioCodec.KerbalRecord kerbal, Guid id)
        {
            string name = kerbal.KerbalName;
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw InvalidHold(id, "invalid roster filename");
            string path = Path.Combine(root, "Kerbals", name + ".txt");
            ApfPaths.EnsureNoReparsePoints(root, path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length <= 0 || input.Length > 64 * 1024) throw InvalidHold(id, "invalid roster size");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (!KerbalSystem.TryValidateKerbalPayload(name, bytes, bytes.Length))
                throw InvalidHold(id, "mismatched roster identity");
            var node = new ConfigNode(Utf8.GetString(bytes));
            if (!node.GetValues("type").Select(v => v.Value).SequenceEqual(new[] { kerbal.Type }) ||
                !node.GetValues("state").Select(v => v.Value).SequenceEqual(new[] { kerbal.Status }))
                throw InvalidHold(id, "mismatched frozen roster state");
        }

        private static InvalidDataException InvalidHold(Guid id, string reason) =>
            new($"Cannot hold missing committed DeepFreeze projection {id}: {reason}.");
    }
}
