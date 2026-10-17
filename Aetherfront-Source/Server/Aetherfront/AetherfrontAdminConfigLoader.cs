using Aetherfront.Core;
using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Aetherfront
{
    internal static class AetherfrontAdminConfigLoader
    {
        private const string ModeratorsPath = "Config\\AetherfrontModerators.json";
        private const string BankPath = "Config\\AetherfrontBank.json";

        public static void Load(AetherfrontServerStore store, string baseDirectory)
        {
            var moderatorsPath = Path.Combine(baseDirectory, ModeratorsPath);
            if (File.Exists(moderatorsPath))
            {
                try
                {
                    var config = AetherfrontJson.Deserialize<ModeratorConfig>(File.ReadAllText(moderatorsPath));
                    store.ReplaceModerators((config?.AccountIds ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                    LunaLog.Normal($"Aetherfront moderators loaded: {config?.AccountIds?.Count ?? 0}");
                }
                catch (Exception e)
                {
                    throw new InvalidDataException("Aetherfront moderator config could not be parsed: " + e.Message, e);
                }
            }

            var bankPath = Path.Combine(baseDirectory, BankPath);
            if (File.Exists(bankPath))
            {
                try
                {
                    var policy = AetherfrontJson.Deserialize<CentralBankPolicy>(File.ReadAllText(bankPath));
                    if (policy == null) throw new InvalidDataException("Bank policy is empty");
                    store.ReplaceBankPolicy(policy);
                    LunaLog.Normal("Aetherfront central-bank policy loaded");
                }
                catch (Exception e)
                {
                    throw new InvalidDataException("Aetherfront bank config could not be parsed: " + e.Message, e);
                }
            }
        }

        [global::System.Runtime.Serialization.DataContract]
        private sealed class ModeratorConfig
        {
            [global::System.Runtime.Serialization.DataMember(Order = 0)] public List<string> AccountIds { get; set; } = new List<string>();
        }
    }
}
