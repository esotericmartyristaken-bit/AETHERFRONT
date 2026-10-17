using ApfCore;
using LmpCommon;
using LmpClient.Utilities;
using System;
using System.IO;

namespace LmpClient.Apf
{
    public static class ApfClientSettings
    {
        private static string DataPath => string.IsNullOrEmpty(LmpClient.MainSystem.KspPath)
            ? string.Empty
            : CommonUtil.CombinePaths(LmpClient.MainSystem.KspPath, "GameData", "LunaMultiplayer", "Data");

        public static bool Enabled => !string.IsNullOrEmpty(DataPath) && File.Exists(Path.Combine(DataPath, "APFClient.json"));
        public static string ClientVersion => ApfClientBuildVersion.Value;
        public static string PrivateMasterServer => ReadText("APFMasterServer.txt");
        public static bool PrivateMasterConfigured => Enabled && LunaNetUtils.CreateEndpointFromString(PrivateMasterServer) != null;
        public static string ClientUpdateManifestUrl => ReadText("APFClientUpdateManifestUrl.txt");
        public static string ClientUpdatePublicKeyXml => ReadText("APFClientUpdatePublicKey.xml");
        public static string DiagnosticsEndpoint => ReadText("APFDiagnosticsEndpoint.txt");
        public static string DiagnosticsUploadToken => ReadText("APFDiagnosticsUploadToken.txt");
        public static string TlsCertificatePinsPath => Path.Combine(DataPath, "APFTlsCertificatePins.txt");
        public static string DiagnosticsOutboxPath => Path.Combine(LmpClient.MainSystem.KspPath, "APF", "Diagnostics", "Outbox");
        public static string StatePath => Path.Combine(DataPath, "APFPackState.json");
        public static string ReconnectPath => Path.Combine(DataPath, "APFReconnect.json");
        public static string HelperKeyPath => Path.Combine(DataPath, "APFHelper.key");

        public static byte[] LoadOrCreateHelperKey() => ApfTransactionSecurity.LoadOrCreateKey(HelperKeyPath);

        public static ApfPackState LoadState()
        {
            try { return File.Exists(StatePath) ? ApfJson.Deserialize<ApfPackState>(File.ReadAllText(StatePath)) : new ApfPackState(); }
            catch (Exception) { return new ApfPackState(); }
        }

        private static string ReadText(string fileName)
        {
            try
            {
                var path = Path.Combine(DataPath, fileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
