using Aetherfront.Core;
using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Aetherfront
{
    internal static class AetherfrontTechnologyLoader
    {
        private const string RelativePath = "Config\\AetherfrontTechnology.json";

        public static void Load(AetherfrontServerStore store, string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, RelativePath);
            if (!File.Exists(path))
            {
                LunaLog.Normal("Aetherfront technology catalog: no config found; legacy technology IDs remain accepted");
                return;
            }

            List<TechnologyDefinition> definitions;
            try
            {
                definitions = AetherfrontJson.Deserialize<List<TechnologyDefinition>>(File.ReadAllText(path)) ?? new List<TechnologyDefinition>();
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Aetherfront technology catalog could not be parsed: " + e.Message, e);
            }

            Validate(definitions, path);
            store.ReplaceTechnologyDefinitions(definitions);
            LunaLog.Normal($"Aetherfront technology catalog loaded: {definitions.Count} from {path}");
        }

        private static void Validate(List<TechnologyDefinition> definitions, string path)
        {
            var duplicate = definitions.GroupBy(t => t?.Id?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() > 1);
            if (duplicate != null)
                throw new InvalidDataException($"Aetherfront technology IDs must be unique and non-empty: '{duplicate.Key}' in {path}");

            var ids = new HashSet<string>(definitions.Select(t => t.Id.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var technology in definitions)
            {
                technology.Id = technology.Id.Trim();
                technology.Title = (technology.Title ?? string.Empty).Trim();
                technology.Description = (technology.Description ?? string.Empty).Trim();
                technology.PrerequisiteIds = (technology.PrerequisiteIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (technology.Id.Length > 96 || technology.Title.Length == 0 || technology.Title.Length > 160)
                    throw new InvalidDataException($"Aetherfront technology '{technology.Id}' has invalid identity/title");
                if (technology.Description.Length > 1000)
                    throw new InvalidDataException($"Aetherfront technology '{technology.Id}' has an invalid description");
                if (technology.ScienceCost < 0 || technology.ScienceCost > AetherfrontConstants.MaxScienceTransfer)
                    throw new InvalidDataException($"Aetherfront technology '{technology.Id}' has an invalid science cost");
                if (technology.PrerequisiteIds.Any(id => !ids.Contains(id) || string.Equals(id, technology.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Aetherfront technology '{technology.Id}' has an invalid prerequisite");
            }

            foreach (var technology in definitions)
                Visit(technology.Id, definitions, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), path);
        }

        private static void Visit(string id, List<TechnologyDefinition> definitions, HashSet<string> visiting,
            HashSet<string> visited, string path)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException($"Aetherfront technology prerequisite cycle includes '{id}' in {path}");
            var technology = definitions.First(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            foreach (var prerequisite in technology.PrerequisiteIds)
                Visit(prerequisite, definitions, visiting, visited, path);
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
