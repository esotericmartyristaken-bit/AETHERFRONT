using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;

namespace Aetherfront.Synchronization
{
    public sealed class StateSection
    {
        public string Name { get; set; }
        public string Json { get; set; }
    }
    public sealed class StateDelta
    {
        public const string Prefix = "APF-STATE-DELTA-1:";
        public long BaseRevision { get; set; }
        public long Revision { get; set; }
        public string ServerId { get; set; }
        public List<StateSection> Sections { get; set; } = new List<StateSection>();
        public List<CraftRecord> CraftUpserts { get; set; }
        public List<string> CraftOrder { get; set; }
        static readonly PropertyInfo[] Properties = typeof(AetherfrontState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
        static readonly MethodInfo Deserialize = typeof(AetherfrontJson).GetMethods().Single(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition);

        public static StateDelta Between(AetherfrontState previous, AetherfrontState current)
            => Between(previous, current, AetherfrontJson.Serialize(previous), AetherfrontJson.Serialize(current));

        public static StateDelta Between(AetherfrontState previous, AetherfrontState current, string previousJson, string currentJson)
        {
            if (previous == null || current == null || previous.ServerId != current.ServerId || current.Revision < previous.Revision)
                throw new InvalidDataException("A delta requires the same server and a nondecreasing revision.");
            var result = new StateDelta { BaseRevision = previous.Revision, Revision = current.Revision, ServerId = current.ServerId };
            var beforeSections = JsonSlices.Object(previousJson); var afterSections = JsonSlices.Object(currentJson);
            foreach (var property in Properties)
            {
                if (property.Name == "Crafts") continue;
                string before = beforeSections[property.Name], after = afterSections[property.Name];
                if (!string.Equals(before, after, StringComparison.Ordinal)) result.Sections.Add(new StateSection { Name = property.Name, Json = after });
            }
            var oldCraft = previous.Crafts ?? new List<CraftRecord>();
            var afterCraft = current.Crafts ?? new List<CraftRecord>();
            var oldJson = JsonSlices.Array(beforeSections["Crafts"]); var newJson = JsonSlices.Array(afterSections["Crafts"]);
            if (oldJson.Count != oldCraft.Count || newJson.Count != afterCraft.Count) throw new InvalidDataException("Canonical craft count differs.");
            var beforeCraft = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < oldCraft.Count; i++) beforeCraft.Add(oldCraft[i].Id, oldJson[i]);
            var upserts = new List<CraftRecord>();
            for (int i = 0; i < afterCraft.Count; i++)
                if (!beforeCraft.TryGetValue(afterCraft[i].Id, out var old) || old != newJson[i]) upserts.Add(afterCraft[i]);
            var order = afterCraft.Select(c => c.Id).ToList();
            if (upserts.Count > 0 || !order.SequenceEqual((previous.Crafts ?? new List<CraftRecord>()).Select(c => c.Id)))
            { result.CraftUpserts = upserts; result.CraftOrder = order; }
            return result;
        }
        public AetherfrontState Apply(AetherfrontState previous)
        {
            if (previous == null || previous.ServerId != ServerId || previous.Revision != BaseRevision || Revision < BaseRevision ||
                Sections == null || Sections.Count > Properties.Length || Sections.Any(s => s == null) ||
                Sections.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != Sections.Count)
                throw new InvalidDataException("State delta base or sections do not match.");
            var next = new AetherfrontState();
            foreach (var property in Properties) property.SetValue(next, property.GetValue(previous, null), null);
            foreach (var section in Sections)
            {
                var property = Properties.SingleOrDefault(p => p.Name == section.Name && p.Name != "Crafts");
                if (property == null || section.Json == null || section.Json.Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Invalid state delta section.");
                property.SetValue(next, Deserialize.MakeGenericMethod(property.PropertyType).Invoke(null, new object[] { section.Json }), null);
            }
            if (CraftOrder != null)
            {
                if (CraftUpserts == null || CraftOrder.Count > 100000 || CraftOrder.Any(string.IsNullOrEmpty) ||
                    CraftOrder.Distinct(StringComparer.Ordinal).Count() != CraftOrder.Count || CraftUpserts.Any(c => c == null || string.IsNullOrEmpty(c.Id)) ||
                    CraftUpserts.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != CraftUpserts.Count)
                    throw new InvalidDataException("Invalid craft delta.");
                var crafts = (previous.Crafts ?? new List<CraftRecord>()).ToDictionary(c => c.Id, StringComparer.Ordinal);
                foreach (var craft in CraftUpserts) crafts[craft.Id] = craft;
                var retained = new HashSet<string>(CraftOrder, StringComparer.Ordinal);
                if (CraftOrder.Any(id => !crafts.ContainsKey(id)) || CraftUpserts.Any(c => !retained.Contains(c.Id)))
                    throw new InvalidDataException("Craft delta references a missing identity.");
                next.Crafts = CraftOrder.Select(id => crafts[id]).ToList();
            }
            else if (CraftUpserts != null) throw new InvalidDataException("Craft delta order is missing.");
            if (next.ServerId != ServerId || next.Revision != Revision) throw new InvalidDataException("State delta result identity does not match.");
            return next;
        }
    }
}
