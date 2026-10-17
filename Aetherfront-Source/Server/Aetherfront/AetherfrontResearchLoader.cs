using Aetherfront.Core;
using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Aetherfront
{
    internal static class AetherfrontResearchLoader
    {
        private const string RelativePath = "Config\\AetherfrontResearch.json";

        public static void Load(AetherfrontServerStore store, string baseDirectory)
        {
            var path = Path.Combine(baseDirectory, RelativePath);
            if (!File.Exists(path))
            {
                LunaLog.Normal("Aetherfront research: no server research config found; using persisted projects");
                return;
            }

            List<ResearchProjectDefinition> definitions;
            try
            {
                definitions = AetherfrontJson.Deserialize<List<ResearchProjectDefinition>>(File.ReadAllText(path)) ?? new List<ResearchProjectDefinition>();
                Validate(definitions, path);
            }
            catch (Exception e)
            {
                throw new InvalidDataException("Aetherfront research config could not be parsed: " + e.Message, e);
            }

            store.ReplaceResearchProjects(definitions);
            LunaLog.Normal($"Aetherfront research projects loaded: {definitions.Count} from {path}");
        }

        private static void Validate(List<ResearchProjectDefinition> definitions, string path)
        {
            var duplicates = definitions.GroupBy(p => p?.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Where(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() != 1).ToList();
            if (duplicates.Count > 0)
                throw new InvalidDataException($"Aetherfront research project ids must be unique and non-empty in {path}");

            foreach (var project in definitions)
            {
                if (project.Title == null || project.Title.Trim().Length == 0 || project.Title.Length > 160)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid title");
                if ((project.Description ?? string.Empty).Length > 1200)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid description");
                if (project.TargetScience <= 0 || project.TargetScience > AetherfrontConstants.MaxResearchContribution)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid target");
                if (project.FundsRewardMicro < 0 || project.ReputationReward < 0)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has a negative reward");
                if ((project.StartsAtUtc != null && project.EndsAtUtc != null) && project.EndsAtUtc <= project.StartsAtUtc)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid time window");
                if (project.RepeatPeriodDays < 0 || project.RepeatPeriodDays > 31)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid repeat period");
                if (project.CatchUpWindowDays < 0 || project.CatchUpWindowDays > 14)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid catch-up window");
                if (project.CatchUpWindowDays > 0 && project.EndsAtUtc == null)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' needs EndsAtUtc for catch-up");
                if (!string.IsNullOrWhiteSpace(project.RewardTechnologyId) && project.RewardTechnologyId.Trim().Length > 96)
                    throw new InvalidDataException($"Aetherfront research project '{project.Id}' has an invalid technology reward");
            }
        }
    }
}
