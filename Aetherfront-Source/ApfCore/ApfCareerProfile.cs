using System;
using System.Collections.Generic;

namespace ApfCore
{
    public sealed class ApfCareerProfile
    {
        public int SchemaVersion { get; set; } = 1;
        public string ProfileId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Mode { get; set; } = "Career";
        public string SaveNamespace { get; set; } = string.Empty;
        public int ProgressionSchemaVersion { get; set; } = 1;
        public bool SharedProgression { get; set; }
        public bool ResetOnPackChange { get; set; }
        public double StartingFunds { get; set; }
        public double StartingScience { get; set; }
        public float StartingReputation { get; set; }

        public string ToJson() => ApfJson.Serialize(this);
        public static ApfCareerProfile FromJson(string json) => ApfJson.Deserialize<ApfCareerProfile>(json);
    }

    public static class ApfCareerProfileValidator
    {
        public static ApfValidationResult Validate(ApfCareerProfile profile)
        {
            var result = new ApfValidationResult();
            if (profile == null)
            {
                result.Add("Career profile is null");
                return result;
            }
            if (profile.SchemaVersion != 1) result.Add("Unsupported career profile schemaVersion");
            RequireToken(profile.ProfileId, "career profileId", result);
            Require(profile.DisplayName, "career displayName", result);
            if (!string.Equals(profile.Mode, "Career", StringComparison.OrdinalIgnoreCase)) result.Add("Career profile mode must be Career");
            if (!ApfPaths.IsSafeRelative(profile.SaveNamespace)) result.Add("Career saveNamespace is unsafe");
            if (string.IsNullOrWhiteSpace(profile.SaveNamespace)) result.Add("Career saveNamespace is required");
            if (profile.ProgressionSchemaVersion <= 0) result.Add("Career progressionSchemaVersion must be positive");
            if (double.IsNaN(profile.StartingFunds) || double.IsInfinity(profile.StartingFunds) || profile.StartingFunds < 0)
                result.Add("Career startingFunds must be finite and non-negative");
            if (double.IsNaN(profile.StartingScience) || double.IsInfinity(profile.StartingScience) || profile.StartingScience < 0)
                result.Add("Career startingScience must be finite and non-negative");
            if (float.IsNaN(profile.StartingReputation) || float.IsInfinity(profile.StartingReputation))
                result.Add("Career startingReputation must be finite");
            return result;
        }

        private static void Require(string value, string name, ApfValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(value)) result.Add(name + " is required");
        }

        private static void RequireToken(string value, string name, ApfValidationResult result)
        {
            Require(value, name, result);
            if (!string.IsNullOrWhiteSpace(value))
                foreach (var character in value)
                    if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'))
                    {
                        result.Add(name + " contains an unsafe character");
                        break;
                    }
        }
    }
}
