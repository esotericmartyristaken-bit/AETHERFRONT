using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Scenario;
using Server.Aetherfront;
using Aetherfront.Core;
using LunaConfigNode.CfgNode;
using System;
using global::System.Text;

namespace Server.System
{
    public static class ShareAchievementsSystem
    {
        public static void AchievementsReceived(ClientStructure client, ShareProgressAchievementsMsgData data)
        {
            LunaLog.Debug($"Achievements data received: {data.Id}");

            // Stock ProgressTracking is client-authored and shared by vanilla
            // LMP. APF achievements are recorded by server-generated events.
            if (AetherfrontServerSystem.Enabled)
            {
                LunaLog.Debug("Ignored legacy achievement update in APF authoritative mode.");
                return;
            }

            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            if (AetherfrontServerSystem.Enabled && !string.IsNullOrWhiteSpace(accountId) && IsCompleted(data))
                AetherfrontGameplayBridge.RecordAchievementCompleted(accountId, data.Id);

            //send the achievements update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteAchievementDataToFile(data);
        }

        private static bool IsCompleted(ShareProgressAchievementsMsgData data)
        {
            if (data == null || data.Data == null || data.NumBytes <= 0 || data.NumBytes > data.Data.Length)
                return false;
            try
            {
                var node = new ConfigNode(Encoding.UTF8.GetString(data.Data, 0, data.NumBytes));
                return ContainsTrueValue(node, "isComplete") || ContainsTrueValue(node, "complete") ||
                    string.Equals(node.GetValue("state")?.Value, "Complete", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                LunaLog.Warning($"Aetherfront achievement parse failed for {data.Id}: {e.Message}");
                return false;
            }
        }

        private static bool ContainsTrueValue(ConfigNode node, string key)
        {
            if (node == null) return false;
            foreach (var value in node.GetAllValues())
                if (string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    bool.TryParse(value.Value, out var parsed) && parsed)
                    return true;
            foreach (var child in node.GetAllNodes())
                if (ContainsTrueValue(child, key)) return true;
            return false;
        }
    }
}
