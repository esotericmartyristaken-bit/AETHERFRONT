using ApfCore;
using LmpCommon;
using LmpCommon.Enums;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.IO;
using System.Linq;

namespace Server.Apf
{
    public static class ApfServerSettings
    {
        public static ApfPackManifest Manifest { get; private set; }
        public static string ManifestUrl { get; private set; } = string.Empty;
        public static string Error { get; private set; } = string.Empty;
        public static bool HasPack { get; private set; }
        public static ApfCareerProfile CareerProfile { get; private set; }
        public static string CareerProfileId => CareerProfile?.ProfileId ?? string.Empty;
        public static bool Enabled => HasPack && Manifest != null && string.IsNullOrEmpty(Error);

        public static void Load()
        {
            var manifestPath = Path.Combine(ServerContext.ConfigDirectory, "APFPack.json");
            HasPack = File.Exists(manifestPath);
            Manifest = null;
            ManifestUrl = string.Empty;
            CareerProfile = null;
            Error = string.Empty;
            if (!HasPack)
            {
                LunaLog.Normal("[APF]: Pack mode disabled; running as a standard LMP server");
                return;
            }

            try
            {
                Manifest = ApfJson.Deserialize<ApfPackManifest>(File.ReadAllText(manifestPath));
                var validation = ApfManifestValidator.Validate(Manifest);
                if (!validation.IsValid) Error = string.Join("; ", validation.Errors);

                var urlPath = Path.Combine(ServerContext.ConfigDirectory, "APFManifestUrl.txt");
                ManifestUrl = File.Exists(urlPath) ? File.ReadAllText(urlPath).Trim() : string.Empty;
                if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    Error = string.IsNullOrEmpty(Error) ? "APFManifestUrl.txt must contain an HTTPS URL" : Error + "; APF manifest URL is not HTTPS";
                if (string.IsNullOrWhiteSpace(DebugSettings.SettingsStore.CustomMasterServer))
                    Error = string.IsNullOrEmpty(Error) ? "APF server requires DebugSettings.CustomMasterServer" : Error + "; APF private master is not configured";
                else if (LunaNetUtils.CreateEndpointFromString(DebugSettings.SettingsStore.CustomMasterServer) == null)
                    Error = string.IsNullOrEmpty(Error) ? "DebugSettings.CustomMasterServer is not a valid host:port endpoint" : Error + "; APF private master endpoint is invalid";

                var careerPath = Path.Combine(ServerContext.ConfigDirectory, "APFCareer.json");
                if (File.Exists(careerPath))
                {
                    CareerProfile = ApfCareerProfile.FromJson(File.ReadAllText(careerPath));
                    var careerValidation = ApfCareerProfileValidator.Validate(CareerProfile);
                    if (!careerValidation.IsValid)
                        Error = string.IsNullOrEmpty(Error) ? string.Join("; ", careerValidation.Errors) : Error + "; " + string.Join("; ", careerValidation.Errors);
                }
                else if (GeneralSettings.SettingsStore.GameMode == GameMode.Career)
                {
                    var profileToken = SafeToken(Manifest.ServerId);
                    CareerProfile = new ApfCareerProfile
                    {
                        ProfileId = profileToken + "-career",
                        DisplayName = Manifest.ServerId + " Career",
                        SaveNamespace = "APF/" + profileToken + "/Career",
                        ProgressionSchemaVersion = 1,
                        ResetOnPackChange = true
                    };
                }
            }
            catch (Exception exception)
            {
                Error = exception.Message;
            }

            if (Enabled)
                LunaLog.Normal($"[APF]: Pack {Manifest.PackId} {Manifest.PackVersion} loaded; manifest URL {ManifestUrl}" +
                    (CareerProfile == null ? string.Empty : "; career profile " + CareerProfile.ProfileId));
            else
                LunaLog.Error("[APF]: Pack configuration invalid: " + Error);
        }

        public static string ReadManifestJson()
        {
            return Manifest?.ToJson() ?? string.Empty;
        }

        private static string SafeToken(string value)
        {
            var token = new string((value ?? "apf").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            return string.IsNullOrWhiteSpace(token) ? "apf" : token;
        }
    }
}
