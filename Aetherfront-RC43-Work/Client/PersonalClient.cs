using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;
using LmpClient;
using LmpClient.Aetherfront;
using LmpClient.Apf;
using LmpClient.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Aetherfront;
using UnityEngine;

namespace Aetherfront.Personal.Client
{

    public static class PersonalClient
    {
        private static readonly Type LegacyMirror = typeof(AetherfrontClientSystem).Assembly.GetType("LmpClient.Aetherfront.ProgressionMirror", true);
        private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly MethodInfo SetScience = LegacyMirror.GetMethod("SetScience", Private);
        private static readonly MethodInfo SetFunds = LegacyMirror.GetMethod("SetFunds", Private);
        private static readonly MethodInfo SetReputation = LegacyMirror.GetMethod("SetReputation", Private);
        private static readonly MethodInfo ApplyTechnology = LegacyMirror.GetMethod("ApplyTechnology", Private);
        private static Game game;
        private static ResearchAndDevelopment research;
        private static string server, account;
        private static HashSet<string> unlocks;
        private static GameScenes scene;
        private static bool applying;
        private static PlayerProgression indexedPlayer;
        private static Dictionary<string, decimal> subjectCredits;
        private static string technologySearch = string.Empty;
        public static bool Active => ApfClientSettings.Enabled && AetherfrontClientSystem.Ready && MainSystem.NetworkState >= ClientState.Running;
        public static PlayerProgression Current => PersonalProgression.Find(AetherfrontClientSystem.State, MainSystem.UniqueIdentifier ?? string.Empty);

        internal static decimal CreditedScience(string sourceId)
        {
            var player = Current;
            if (player == null) return 0;
            if (!ReferenceEquals(indexedPlayer, player))
            {
                subjectCredits = player.ScienceSources.ToDictionary(c => c.Id, c => c.CumulativeScience, StringComparer.Ordinal);
                indexedPlayer = player;
            }
            decimal credited;
            return subjectCredits.TryGetValue(sourceId, out credited) ? credited : 0;
        }

        public static void RestoreSubject(ScienceSubject __result)
        {
            SubjectHistory.Restore(__result);
        }

        public static void QueueScienceClaim(AetherfrontWireRequest request)
        {
            if (!Active) return;
            decimal amount;
            if (!decimal.TryParse(request.Body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) || amount <= 0) return;
            ScienceOutbox.Enqueue(new PendingScienceClaim { ServerId = AetherfrontClientSystem.State.ServerId, AccountId = MainSystem.UniqueIdentifier,
                Command = (int)request.CommandType, SourceId = request.EntityId, Amount = amount, Reason = request.Amount2 });
        }

        internal static void SendScienceClaim(AetherfrontWireRequest request)
        {
            if (!Active) return;
            request.ProtocolVersion = 1; request.Operation = AetherfrontWireOperation.CommandRequest;
            if (string.IsNullOrEmpty(request.CommandId)) request.CommandId = "science:" + Guid.NewGuid().ToString("N");
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AetherfrontCommandMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(request);
            NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AetherfrontCliMsg>(data));
        }

        public static bool CanRequest(string technologyId)
        {
            var player = Current;
            return !applying && Active && player != null && !string.IsNullOrWhiteSpace(technologyId) &&
                !player.TechnologyUnlocks.Contains(technologyId, StringComparer.OrdinalIgnoreCase);
        }

        public static void RequestTechnology(string technologyId)
        {
            if (!CanRequest(technologyId)) return;
            AetherfrontClientSystem.SendCommand(new AetherfrontWireRequest
            {
                CommandType = AetherfrontCommandType.UnlockTechnology, TechnologyId = technologyId,
                Amount2 = Current.NextCommandSequence
            });
        }

        public static void Apply()
        {
            if (applying) return;
            if (!Active || HighLogic.CurrentGame == null)
            { game = null; research = null; unlocks = null; server = null; account = null; indexedPlayer = null; subjectCredits = null; return; }
            var player = Current;
            if (player == null) return; // No fallback to a faction wallet.
            // Also bootstrap through the already-running mirror: a missing
            // addon registration must never silently disable durable science.
            ScienceEvents.EnsureStarted();
            FlightCheckpointClient.EnsureStarted();
            bool identityChanged = server != AetherfrontClientSystem.State.ServerId || account != player.AccountId;
            bool treeChanged = identityChanged || game != HighLogic.CurrentGame || research != ResearchAndDevelopment.Instance || scene != HighLogic.LoadedScene;
            decimal visibleScience;
            var desired = ResearchSync.Project(AetherfrontClientSystem.State, player, out visibleScience);
            applying = true;
            try
            {
                if (HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.Instance != null)
                {
                    if (treeChanged || !ReferenceEquals(indexedPlayer, player))
                        foreach (var subject in ResearchAndDevelopment.GetSubjects()) RestoreSubject(subject);
                    SetScience.Invoke(null, new object[] { ResearchAndDevelopment.Instance, (float)visibleScience });
                    if ((treeChanged || unlocks == null || !unlocks.SetEquals(desired)) &&
                        (bool)ApplyTechnology.Invoke(null, new object[] { ResearchAndDevelopment.Instance, desired, identityChanged }))
                    {
                        server = AetherfrontClientSystem.State.ServerId; account = player.AccountId;
                        game = HighLogic.CurrentGame; research = ResearchAndDevelopment.Instance; scene = HighLogic.LoadedScene; unlocks = desired;
                    }
                }
                if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
                {
                    SetFunds.Invoke(null, new object[] { player.FundsMicro / 1000000d });
                    SetReputation.Invoke(null, new object[] { (float)player.Reputation });
                }
            }
            catch (Exception error) { unlocks = null; Debug.LogError("[Aetherfront] Personal progression mirror failed: " + (error.InnerException ?? error)); }
            finally { applying = false; }
        }

        public static string Summary()
        {
            var player = Current;
            if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return "Sandbox: all technology is available.";
            if (player == null) return "Loading personal progression...";
            return player.Science.ToString("0.##", CultureInfo.InvariantCulture) + " personal science | " + (player.FundsMicro / 1000000m).ToString("0.##", CultureInfo.InvariantCulture) + " personal funds";
        }

        public static long PersonalFunds(FactionProgression ignored) => Current?.FundsMicro ?? 0;
        public static long WholeScience(FactionProgression ignored) => checked((long)decimal.Floor(Current?.TradableScience ?? 0));
        private static void DrawSyncStatus()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return;
            if (!ScienceEvents.CaptureReady) GUILayout.Label("Science capture is not ready. Avoid recovery/transmission; see KSP.log.");
            if (!string.IsNullOrEmpty(ScienceOutbox.Warning)) GUILayout.Label(ScienceOutbox.Warning);
            else if (ScienceOutbox.PendingCount > 0) GUILayout.Label("Science sync: " + ScienceOutbox.PendingCount + " reward(s) awaiting server confirmation.");
            if (ScienceOutbox.HeldCount > 0)
            {
                GUILayout.Label(ScienceOutbox.HeldCount + " reward(s) paused after server rejection. Other science continues; evidence was saved.");
                if (GUILayout.Button("Retry paused rewards")) ScienceOutbox.RetryHeld();
            }
        }
        public static void DrawSummary(string ignoredText) { GUILayout.Label(Summary()); DrawSyncStatus(); }
        public static void DrawTradingSummary(string ignoredText)
        {
            GUILayout.Label(Summary());
            DrawSyncStatus();
            GUILayout.Label("Tradable science: " + (Current?.TradableScience ?? 0).ToString("0.##") + ". Shared and starting science can be spent on research but cannot be traded.");
        }
        public static void DrawGroupExplanation(string ignoredText) => GUILayout.Label("Factions share vessels and permissions. Your science, funds and technology are personal.");
        public static string ModeDescription() => HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX
            ? "Sandbox mode. All KSP technology is unlocked."
            : "Personal progression. You keep all science you earn; every other player receives an additional 50%.";
        public static int ProgressToolbar(int selected, string[] tabs, GUILayoutOption[] options)
        {
            if (tabs.Length == 3 && tabs[0] == "Research" && tabs[1] == "Standings" && tabs[2] == "Store")
                return 0;
            return GUILayout.Toolbar(selected, tabs, options);
        }
        public static void DrawResearchNoFaction(string ignoredText) => DrawResearch(AetherfrontClientSystem.State, null);
        public static void DrawResearch(AetherfrontState state, FactionRecord ignoredFaction)
        {
            GUILayout.Label(ModeDescription());
            var player = Current;
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return;
            if (player == null) { GUILayout.Label("Loading your personal progression..."); return; }
            GUILayout.Label(Summary());
            DrawSyncStatus();
            GUILayout.Label("Science earned: " + player.EarnedScience.ToString("0.##") + " | Shared science received: " + player.SharedScienceCredited.ToString("0.##"));
            GUILayout.Label("Offline players receive their shares when they return. New players receive the full historical shared baseline.");
            GUILayout.Label("Shared and starting science are for research. Earned science can also be traded.");
            GUILayout.Label("Find technology");
            technologySearch = GUILayout.TextField(technologySearch, 96);
            foreach (var definition in state.TechnologyDefinitions.Where(t => t != null && ((t.Title ?? "").IndexOf(technologySearch, StringComparison.OrdinalIgnoreCase) >= 0 || t.Id.IndexOf(technologySearch, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                bool unlocked = player.TechnologyUnlocks.Contains(definition.Id, StringComparer.OrdinalIgnoreCase);
                bool prerequisites = definition.PrerequisiteIds.Count == 0 || (definition.AnyPrerequisite
                    ? definition.PrerequisiteIds.Any(p => player.TechnologyUnlocks.Contains(p, StringComparer.OrdinalIgnoreCase))
                    : definition.PrerequisiteIds.All(p => player.TechnologyUnlocks.Contains(p, StringComparer.OrdinalIgnoreCase)));
                GUILayout.BeginHorizontal();
                GUILayout.Label((string.IsNullOrEmpty(definition.Title) ? definition.Id : definition.Title) + " | " + definition.ScienceCost + " science");
                if (unlocked) GUILayout.Label("Unlocked", GUILayout.Width(90));
                else
                {
                    bool wasEnabled = GUI.enabled;
                    try
                    {
                        GUI.enabled = wasEnabled && prerequisites && player.Science >= definition.ScienceCost;
                        if (GUILayout.Button("Unlock", GUILayout.Width(90))) RequestTechnology(definition.Id);
                    }
                    finally { GUI.enabled = wasEnabled; }
                }
                GUILayout.EndHorizontal();
            }
        }
        public static string ConfirmPersonalPayment(string text, AetherfrontWireRequest request)
        {
            if (request == null) return text;
            if (request.CommandType == AetherfrontCommandType.CreateTradeOffer || request.CommandType == AetherfrontCommandType.ConfirmTrade)
                return text + "\n\nAny science or funds on your side come from your personal balance. Confirming makes you the payment sponsor for this trade.";
            return text;
        }
        public static bool CaptureScienceReceipt(string payload) => FlightCheckpointClient.Capture(payload) || ScienceOutbox.Capture(payload);
    }
}

