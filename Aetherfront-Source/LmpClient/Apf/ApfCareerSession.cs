using ApfCore;
using System;
using System.IO;
using System.Linq;

namespace LmpClient.Apf
{
    /// <summary>
    /// Selects a local save namespace for the currently authenticated APF
    /// career profile. LMP's existing server-authoritative career sync remains
    /// unchanged; this prevents local persistent.sfs files from crossing APF
    /// servers while leaving vanilla/public LMP on its original folder.
    /// </summary>
    public static class ApfCareerSession
    {
        public const string DefaultSaveFolder = "LunaMultiplayer";

        public static string SaveFolder { get; private set; } = DefaultSaveFolder;
        public static string ProfileId { get; private set; } = string.Empty;
        public static string PackId { get; private set; } = string.Empty;
        public static string PackVersion { get; private set; } = string.Empty;
        public static ApfCareerProfile Profile { get; private set; }

        public static bool Apply(string profileId, string profileJson, string packId, string packVersion, out string error)
        {
            error = string.Empty;
            Profile = null;
            ProfileId = string.Empty;
            PackId = string.Empty;
            PackVersion = string.Empty;
            SaveFolder = DefaultSaveFolder;

            if (string.IsNullOrWhiteSpace(profileId))
                return true;

            var token = SafeToken(profileId);
            if (string.IsNullOrWhiteSpace(token))
            {
                error = "APF career profile ID is unsafe";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(profileJson))
            {
                try
                {
                    var profile = ApfCareerProfile.FromJson(profileJson);
                    var validation = ApfCareerProfileValidator.Validate(profile);
                    if (!validation.IsValid)
                    {
                        error = string.Join("; ", validation.Errors);
                        return false;
                    }
                    if (!string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal))
                    {
                        error = "APF career profile ID does not match handshake metadata";
                        return false;
                    }
                    Profile = profile;
                }
                catch (Exception exception)
                {
                    error = "APF career profile could not be parsed: " + exception.Message;
                    return false;
                }
            }

            ProfileId = profileId;
            PackId = SafeToken(packId);
            PackVersion = SafeToken(packVersion);
            if (Profile != null && Profile.ResetOnPackChange)
            {
                if (string.IsNullOrWhiteSpace(PackId) || string.IsNullOrWhiteSpace(PackVersion))
                {
                    error = "APF career profile requires a safe pack ID and version for pack-change isolation";
                    return false;
                }
                SaveFolder = "LunaMultiplayer-APF-" + token + "-" + PackId + "-" + PackVersion;
                if (SaveFolder.Length > 96)
                    SaveFolder = SaveFolder.Substring(0, 96).TrimEnd('-', '_', '.');
            }
            else
            {
                SaveFolder = "LunaMultiplayer-APF-" + token;
            }
            return true;
        }

        public static void Reset()
        {
            Profile = null;
            ProfileId = string.Empty;
            PackId = string.Empty;
            PackVersion = string.Empty;
            SaveFolder = DefaultSaveFolder;
        }

        public static void EnsureSaveDirectory(string kspPath)
        {
            if (string.IsNullOrWhiteSpace(kspPath)) throw new InvalidDataException("KSP path is empty");
            var saveRoot = Path.Combine(kspPath, "saves", SaveFolder);
            Directory.CreateDirectory(saveRoot);
            // KSP/LMP craft workflows expect these folders to exist. The
            // default public folder is created during startup, but an APF
            // profile is selected later during handshake and therefore needs
            // the same layout provisioned on demand.
            Directory.CreateDirectory(Path.Combine(saveRoot, "Ships", "VAB"));
            Directory.CreateDirectory(Path.Combine(saveRoot, "Ships", "SPH"));
            Directory.CreateDirectory(Path.Combine(saveRoot, "Subassemblies"));
        }

        private static string SafeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')))
                return string.Empty;
            var token = value;
            return token.Length > 64 ? token.Substring(0, 64) : token;
        }
    }
}
