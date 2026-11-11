using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LmpCommon;
using LunaConfigNode.CfgNode;
using Server.Aetherfront;
using Server.Context;
using Server.Log;
using Server.System;

namespace Aetherfront.Synchronization.Server
{
    // Startup only: after canonical roster migration, before loading vessel memory
    // or accepting players. Never invents crew, timers, supplies or progression.
    public static class SurvivalRosterRepair
    {
        public sealed class Result { public int Files, Removed, Unresolved; }
        sealed class Document { internal string Path, Text; internal byte[] Image; internal ConfigNode Node; internal HashSet<string> Crew; }
        sealed class Assignment { internal string Crew; internal Document Document; }
        public static void Run()
        {
            var type = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontDeepFreezeSystem", true);
            var snapshot = (DeepFreezeScenarioCodec.Snapshot)type.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);
            var result = Repair(ServerContext.UniverseDirectory, new HashSet<string>(snapshot.Kerbals.Keys, StringComparer.Ordinal));
            if (result.Removed != 0 || result.Unresolved != 0)
                LunaLog.Warning("Survival roster reconciliation: files=" + result.Files + "; stale records archived=" + result.Removed +
                    "; unresolved=" + result.Unresolved + ". Crew status, mission epochs, supplies and progression unchanged.");
        }
        public static Result Repair(string universe, ISet<string> frozen)
        {
            var result = new Result(); string directory = Path.Combine(universe, "Vessels"), kerbals = Path.Combine(universe, "Kerbals");
            if (!Directory.Exists(directory) || !Directory.Exists(kerbals)) return result;
            var documents = Directory.GetFiles(directory, "*.txt").Select(path =>
            {
                byte[] image = File.ReadAllBytes(path); string text = new UTF8Encoding(false, true).GetString(image).TrimStart('\uFEFF'); var node = new ConfigNode(text);
                return new Document { Path = path, Text = text, Image = image, Node = node, Crew = new HashSet<string>(node.GetNodes("PART")
                    .SelectMany(p => p.Value.GetValues("crew").Select(v => v.Value)), StringComparer.Ordinal) };
            }).ToArray();
            var locations = documents.SelectMany(d => d.Crew.Select(c => new Assignment { Crew = c, Document = d }))
                .GroupBy(x => x.Crew, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Document).Distinct().ToArray(), StringComparer.Ordinal);
            foreach (var document in documents)
            {
                var removed = new List<string>();
                foreach (var survival in Find(document.Node, "APF_SURVIVAL"))
                {
                    var rows = survival.GetNodes("CREW").ToArray();
                    var names = rows.Select(r => r.Value.GetValues("KerbalName").Select(v => v.Value).ToArray()).ToArray();
                    if (names.Any(n => n.Length != 1 || string.IsNullOrWhiteSpace(n[0])) || names.Select(n => n[0]).Distinct(StringComparer.Ordinal).Count() != names.Length)
                    { result.Unresolved++; continue; }
                    for (int i = 0; i < rows.Length; i++)
                    {
                        string name = names[i][0]; if (document.Crew.Contains(name)) continue;
                        if (frozen.Contains(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        { result.Unresolved++; continue; }
                        string path = Path.Combine(kerbals, name + ".txt");
                        if (!File.Exists(path)) { result.Unresolved++; continue; }
                        var kerbal = new ConfigNode(File.ReadAllText(path));
                        string state = kerbal.GetValue("state")?.Value;
                        locations.TryGetValue(name, out var assigned);
                        // A unique persisted seat is stronger evidence than an
                        // Available flag (the native roster migration normalizes
                        // that flag at startup). Dead/unknown conflicts stay strict.
                        bool elsewhere = assigned != null && assigned.Length == 1 && !ReferenceEquals(assigned[0], document) &&
                            (state == "Assigned" || state == "Available");
                        bool unassigned = assigned == null && (state == "Available" || state == "Dead");
                        if (kerbal.GetValue("name")?.Value != name || (!elsewhere && !unassigned)) { result.Unresolved++; continue; }
                        survival.Nodes.Remove(rows[i]); removed.Add(name);
                    }
                }
                if (removed.Count == 0) continue;
                string recovery = Path.Combine(universe, "Survival-Recovery"); Directory.CreateDirectory(recovery);
                byte[] original = document.Image; string hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
                string archive = Path.Combine(recovery, Path.GetFileNameWithoutExtension(document.Path) + "-" + hash + ".txt");
                if (File.Exists(archive))
                {
                    if (!SHA256.HashData(File.ReadAllBytes(archive)).SequenceEqual(SHA256.HashData(original))) throw new InvalidDataException("Survival recovery archive differs.");
                }
                else
                {
                    using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    { SyncTimings.Write(stream, original, 0, original.Length); SyncTimings.Flush(stream, true); }
                }
                // Validate the exact original immediately before replacement. Even
                // this startup operation must not overwrite a concurrent writer.
                if (!File.ReadAllBytes(document.Path).SequenceEqual(document.Image)) throw new IOException("Vessel changed during survival reconciliation.");
                byte[] updated = Encoding.UTF8.GetBytes(document.Node.ToString());
                FileHandler.WriteToFileAtomic(document.Path, updated, updated.Length);
                result.Files++; result.Removed += removed.Count;
            }
            return result;
        }
        static IEnumerable<ConfigNode> Find(ConfigNode node, string name)
        {
            if (node.Name == name) yield return node;
            foreach (var child in node.GetAllNodes()) foreach (var match in Find(child, name)) yield return match;
        }
    }
}
