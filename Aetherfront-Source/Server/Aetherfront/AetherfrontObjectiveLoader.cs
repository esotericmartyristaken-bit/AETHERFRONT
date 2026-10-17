using Aetherfront.Core;
using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Aetherfront
{
    internal static class AetherfrontObjectiveLoader
    {
        private const string RelativePath = "Config\\AetherfrontObjectives.json";

        public static void Load(AetherfrontServerStore store, string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, RelativePath);
            if (!File.Exists(path))
            {
                LunaLog.Normal("Aetherfront objectives: no server objective config found; using persisted objectives");
                return;
            }

            List<ObjectiveDefinition> definitions;
            try
            {
                definitions = AetherfrontJson.Deserialize<List<ObjectiveDefinition>>(File.ReadAllText(path)) ?? new List<ObjectiveDefinition>();
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Aetherfront objective config could not be parsed: " + e.Message, e);
            }

            Validate(definitions, path);
            store.MergeConfiguredObjectives(definitions);
            LunaLog.Normal($"Aetherfront objectives loaded: {definitions.Count} from {path}");
        }

        private static void Validate(List<ObjectiveDefinition> definitions, string path)
        {
            var duplicate = definitions.GroupBy(o => o?.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() > 1);
            if (duplicate != null)
                throw new InvalidDataException($"Aetherfront objective ids must be unique and non-empty: '{duplicate.Key}' in {path}");

            foreach (var objective in definitions)
            {
                if (string.IsNullOrWhiteSpace(objective.Title) || objective.Title.Length > 160)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has an invalid title");
                if (string.IsNullOrWhiteSpace(objective.EventType) || objective.EventType.Length > 80)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has an invalid event type");
                if (objective.Target <= 0 || objective.Target > 1000000000)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has an invalid target");
                if (objective.ScienceReward < 0 || objective.FundsRewardMicro < 0 || objective.ReputationReward < 0)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has a negative reward");
                if (objective.StartsAtUtc != null && objective.EndsAtUtc != null && objective.EndsAtUtc <= objective.StartsAtUtc)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has an invalid time window");
                if (objective.RepeatPeriodDays < 0 || objective.RepeatPeriodDays > 31)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has an invalid repeat period");
                if (objective.CatchUpWindowDays < 0 || objective.CatchUpWindowDays > 14)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' has an invalid catch-up window");
                if (objective.CatchUpWindowDays > 0 && objective.EndsAtUtc == null)
                    throw new InvalidDataException($"Aetherfront objective '{objective.Id}' needs EndsAtUtc for catch-up");
            }
        }
    }
}
