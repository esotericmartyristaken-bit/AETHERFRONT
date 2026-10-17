using System;
using System.Collections.Generic;
using Aetherfront.Core;
using LmpClient.Apf;
using LmpClient.Base;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareReputation;
using LmpClient.Systems.ShareScience;
using LmpClient.Systems.ShareTechnology;
using LmpCommon.Enums;
using KSP.UI.Screens;

namespace LmpClient.Aetherfront
{
    // Injected into the current client assembly; no extra runtime plugin is required.
    internal static class ProgressionMirror
    {
        private static string _serverId;
        private static string _factionId;
        private static Game _game;
        private static ResearchAndDevelopment _research;
        private static GameScenes _scene;
        private static HashSet<string> _unlocks;
        private static bool _forceTechnology;
        private static bool _applying;
        private static DateTime _lastErrorUtc;

        public static void Tick()
        {
            if (MainSystem.NetworkState < ClientState.Running) Reset();
        }

        public static bool CanRequest(string technologyId)
        {
            if (_applying || !ApfClientSettings.Enabled || !AetherfrontClientSystem.Ready ||
                string.IsNullOrWhiteSpace(technologyId)) return false;
            var state = AetherfrontClientSystem.State;
            var faction = state == null ? null : state.FindFactionForAccount(MainSystem.UniqueIdentifier ?? string.Empty);
            if (faction == null || faction.Progression == null) return false;
            // Stock initialization may announce a technology that is already authoritative.
            var unlocked = faction.Progression.TechnologyUnlocks;
            if (unlocked != null)
                foreach (var id in unlocked)
                    if (string.Equals(id, technologyId, StringComparison.OrdinalIgnoreCase)) return false;
            _forceTechnology = true;
            return true;
        }

        public static void Apply()
        {
            if (_applying) return;
            var state = AetherfrontClientSystem.State;
            var game = HighLogic.CurrentGame;
            if (!ApfClientSettings.Enabled || MainSystem.NetworkState < ClientState.Running ||
                state == null || string.IsNullOrEmpty(state.ServerId) || game == null)
            {
                Reset();
                return;
            }
            var faction = state.FindFactionForAccount(MainSystem.UniqueIdentifier ?? string.Empty);
            // Factionless clients retain stock progression. An empty faction must not relock the tree.
            if (faction == null || faction.Progression == null)
            {
                Reset();
                return;
            }

            var progression = faction.Progression;
            var research = ResearchAndDevelopment.Instance;
            bool factionChanged = !string.Equals(_serverId, state.ServerId, StringComparison.Ordinal) ||
                                  !string.Equals(_factionId, faction.Id, StringComparison.Ordinal);
            bool contextChanged = factionChanged || !ReferenceEquals(_game, game) ||
                                  !ReferenceEquals(_research, research) || _scene != HighLogic.LoadedScene;
            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (progression.TechnologyUnlocks != null)
                foreach (var id in progression.TechnologyUnlocks)
                    if (!string.IsNullOrWhiteSpace(id)) desired.Add(id);

            _applying = true;
            try
            {
                int mode = (int)game.Mode;
                if (mode != 0 && research != null)
                {
                    SetScience(research, (float)progression.Science);
                    if (contextChanged || _forceTechnology || _unlocks == null || !_unlocks.SetEquals(desired))
                    {
                        if (ApplyTechnology(research, desired, factionChanged))
                        {
                            _serverId = state.ServerId;
                            _factionId = faction.Id;
                            _game = game;
                            _research = research;
                            _scene = HighLogic.LoadedScene;
                            _unlocks = desired;
                            _forceTechnology = false;
                        }
                    }
                }
                if (mode == 1)
                {
                    SetFunds((double)progression.FundsMicro / 1000000.0);
                    SetReputation((float)progression.Reputation);
                }
            }
            catch (Exception error)
            {
                _unlocks = null;
                if ((DateTime.UtcNow - _lastErrorUtc).TotalSeconds >= 30)
                {
                    _lastErrorUtc = DateTime.UtcNow;
                    LunaLog.LogError("[Aetherfront] Progression apply failed: " + error);
                }
            }
            finally { _applying = false; }
        }

        private static bool ApplyTechnology(ResearchAndDevelopment research, HashSet<string> desired, bool factionChanged)
        {
            var tree = AssetBase.RnDTechTree;
            if (tree == null) return false;
            var nodes = tree.GetTreeTechs();
            if (nodes == null || nodes.Length == 0) return false;
            var system = System<ShareTechnologySystem>.Singleton;
            bool ignoring = system.IgnoreEvents;
            bool changed = false;
            if (!ignoring) system.StartIgnoringEvents();
            try
            {
                foreach (var node in nodes)
                {
                    if (node == null || string.IsNullOrEmpty(node.techID)) continue;
                    var current = research.GetTechState(node.techID);
                    bool available = desired.Contains(node.techID);
                    if (available)
                    {
                        if (factionChanged || current == null || (int)current.state != 1)
                        {
                            research.UnlockProtoTechNode(node);
                            changed = true;
                        }
                    }
                    else if (current == null || (int)current.state != 0 ||
                             (current.partsPurchased != null && current.partsPurchased.Count != 0))
                    {
                        research.SetTechState(node.techID, new ProtoTechNode
                        {
                            techID = node.techID,
                            scienceCost = node.scienceCost,
                            state = (RDTech.State)0,
                            partsPurchased = new List<AvailablePart>()
                        });
                        changed = true;
                    }
                }
                if (changed)
                {
                    // Keep sharing suppressed while tree-spawn callbacks initialize every node.
                    ResearchAndDevelopment.RefreshTechTreeUI();
                    if (EditorPartList.Instance != null) EditorPartList.Instance.Refresh();
                }
                return true;
            }
            finally { if (!ignoring) system.StopIgnoringEvents(); }
        }

        private static void SetScience(ResearchAndDevelopment research, float value)
        {
            if (research.Science == value) return;
            var system = System<ShareScienceSystem>.Singleton;
            bool ignoring = system.IgnoreEvents;
            if (!ignoring) system.StartIgnoringEvents();
            try { research.SetScience(value, TransactionReasons.None); }
            finally { if (!ignoring) system.StopIgnoringEvents(); }
        }

        private static void SetFunds(double value)
        {
            var funding = Funding.Instance;
            if (funding == null || funding.Funds == value) return;
            var system = System<ShareFundsSystem>.Singleton;
            bool ignoring = system.IgnoreEvents;
            if (!ignoring) system.StartIgnoringEvents();
            try { funding.SetFunds(value, TransactionReasons.None); }
            finally { if (!ignoring) system.StopIgnoringEvents(); }
        }

        private static void SetReputation(float value)
        {
            var reputation = Reputation.Instance;
            if (reputation == null || reputation.reputation == value) return;
            var system = System<ShareReputationSystem>.Singleton;
            bool ignoring = system.IgnoreEvents;
            if (!ignoring) system.StartIgnoringEvents();
            try { reputation.SetReputation(value, TransactionReasons.None); }
            finally { if (!ignoring) system.StopIgnoringEvents(); }
        }

        private static void Reset()
        {
            _serverId = null;
            _factionId = null;
            _game = null;
            _research = null;
            _unlocks = null;
            _forceTechnology = false;
        }
    }
}
