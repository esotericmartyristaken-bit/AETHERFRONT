using Aetherfront.Core;
using Server.Context;
using Server.Client;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.IO;

namespace Server.Aetherfront
{
    public static class AetherfrontServerSystem
    {
        private static readonly object Gate = new object();
        private static AetherfrontServerStore _store;

        public static bool Enabled => _store != null;
        public static AetherfrontServerStore Store => _store;

        public static void Initialize()
        {
            lock (Gate)
            {
                if (_store != null) return;
                var serverId = BuildServerId();
                _store = new AetherfrontServerStore(AppDomain.CurrentDomain.BaseDirectory, serverId);
                AetherfrontTechnologyLoader.Load(_store, AppDomain.CurrentDomain.BaseDirectory);
                AetherfrontObjectiveLoader.Load(_store, AppDomain.CurrentDomain.BaseDirectory);
                AetherfrontResearchLoader.Load(_store, AppDomain.CurrentDomain.BaseDirectory);
                AetherfrontStoreLoader.Load(_store, AppDomain.CurrentDomain.BaseDirectory);
                AetherfrontAdminConfigLoader.Load(_store, AppDomain.CurrentDomain.BaseDirectory);
                LunaLog.Normal($"Aetherfront state ready: {_store.RootPath} (serverId={serverId}, revision={_store.ReadSnapshot().Revision})");
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_store == null) return;
                _store.Persist();
                _store = null;
            }
        }

        public static AetherfrontState Snapshot()
        {
            var store = _store ?? throw new InvalidOperationException("Aetherfront state is not initialized");
            return store.ReadSnapshot();
        }

        public static AetherfrontResult Execute(Func<AetherfrontState, AetherfrontResult> command)
        {
            var store = _store ?? throw new InvalidOperationException("Aetherfront state is not initialized");
            return store.Execute(command);
        }

        public static bool BindSession(ClientStructure client)
        {
            if (!Enabled || client == null) return false;
            var accountId = AetherfrontDomain.NormalizeAccountId(client.UniqueIdentifier);
            if (string.IsNullOrWhiteSpace(accountId)) return false;
            if (!string.IsNullOrWhiteSpace(client.AetherfrontAccountId) &&
                !string.Equals(client.AetherfrontAccountId, accountId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Store.TouchAccount(accountId, client.PlayerName, DateTime.UtcNow)) return false;
            client.AetherfrontAccountId = accountId;
            return true;
        }

        public static string GetBoundAccountId(ClientStructure client)
        {
            if (!Enabled || client == null || string.IsNullOrWhiteSpace(client.AetherfrontAccountId)) return string.Empty;
            return string.Equals(client.AetherfrontAccountId, AetherfrontDomain.NormalizeAccountId(client.UniqueIdentifier), StringComparison.OrdinalIgnoreCase)
                ? client.AetherfrontAccountId : string.Empty;
        }

        private static string BuildServerId()
        {
            var configured = GeneralSettings.SettingsStore?.ServerName;
            if (!string.IsNullOrWhiteSpace(configured))
                return NormalizeId(configured);
            return NormalizeId(new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory).Name);
        }

        private static string NormalizeId(string value)
        {
            var chars = value.Trim().ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_')) chars[i] = '-';
            return new string(chars).Trim('-');
        }
    }
}
