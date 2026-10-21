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
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class ScienceEvents : MonoBehaviour
    {
        private float nextHookAttempt;
        private bool subjectHookInstalled;
        private static ScienceEvents live;
        private sealed class ObservedAward
        {
            internal int Frame; internal float Input; internal decimal Amount; internal long Reason;
            internal string Server, Account;
            internal bool HasSubject;
        }
        private readonly List<ObservedAward> observed = new List<ObservedAward>();
        private void Awake() { live = this; DontDestroyOnLoad(this); GameEvents.OnScienceRecieved.Add(Received); }
        private void OnDestroy() { GameEvents.OnScienceRecieved.Remove(Received); if (live == this) live = null; }
        private void OnApplicationQuit() { FlushAwards(true); ScienceOutbox.Save(true); }
        private void Update()
        {
            FlushAwards(false);
            ScienceOutbox.Tick();
            // Harmony is installed by the pack. The small vanilla starter must
            // still load before the pack exists, so do not add an assembly
            // dependency on Harmony to this bootstrap-loaded plugin.
            if (subjectHookInstalled || Time.realtimeSinceStartup < nextHookAttempt) return;
            nextHookAttempt = Time.realtimeSinceStartup + 10;
            var harmony = Type.GetType("HarmonyLib.Harmony, 0Harmony", false);
            var harmonyMethod = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", false);
            if (harmony == null || harmonyMethod == null) return;
            try
            {
                var target = typeof(ResearchAndDevelopment).GetMethod("getScienceSubject", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ScienceSubject) }, null);
                if (target == null) throw new MissingMethodException("KSP science subject history hook");
                var patch = harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length >= 3 && m.GetParameters()[0].ParameterType == typeof(MethodBase));
                var parameters = new object[patch.GetParameters().Length];
                parameters[0] = target;
                parameters[2] = Activator.CreateInstance(harmonyMethod, typeof(PersonalClient).GetMethod(nameof(PersonalClient.RestoreSubject)));
                var owner = Activator.CreateInstance(harmony, "aetherfront.personal.subject-history");
                patch.Invoke(owner, parameters);
                var addition = typeof(ResearchAndDevelopment).GetMethod("AddScience", new[] { typeof(float), typeof(TransactionReasons) });
                if (addition == null) throw new MissingMethodException("KSP science reward hook");
                parameters = new object[patch.GetParameters().Length]; parameters[0] = addition;
                parameters[1] = Activator.CreateInstance(harmonyMethod, typeof(ScienceEvents).GetMethod(nameof(BeforeScience)));
                parameters[2] = Activator.CreateInstance(harmonyMethod, typeof(ScienceEvents).GetMethod(nameof(AfterScience)));
                patch.Invoke(owner, parameters);
                subjectHookInstalled = true;
                Debug.Log("[Aetherfront] Personal science subject history hook ready.");
            }
            catch (Exception error)
            {
                subjectHookInstalled = true; // Log once; never spam every frame.
                Debug.LogError("[Aetherfront] Personal science subject history hook failed: " + (error.InnerException ?? error));
            }
        }
        public static void BeforeScience(ResearchAndDevelopment __instance, out float __state) { __state = __instance.Science; }
        public static void AfterScience(ResearchAndDevelopment __instance, float value, TransactionReasons reason, float __state)
        {
            if (live == null || !PersonalClient.Active || HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX ||
                value <= 0 || value > (float)PersonalProgression.MaxScience || float.IsNaN(value) || float.IsInfinity(value) || (long)reason < 0 || ((long)reason & ~PersonalProgression.ScienceRewardReasons) != 0) return;
            // Use the reward amount, not a rounded float wallet delta. The
            // wallet is mirrored from the decimal server ledger separately.
            live.observed.Add(new ObservedAward { Frame = Time.frameCount, Input = value, Amount = decimal.Round((decimal)value, 6), Reason = (long)reason,
                Server = AetherfrontClientSystem.State.ServerId, Account = MainSystem.UniqueIdentifier });
        }
        private void FlushAwards(bool all)
        {
            for (int i = observed.Count - 1; i >= 0; i--)
            {
                var award = observed[i];
                if (!all && award.Frame == Time.frameCount) continue;
                if (!award.HasSubject)
                    ScienceOutbox.Enqueue(new PendingScienceClaim { ServerId = award.Server, AccountId = award.Account, Command = 42,
                        SourceId = "award:" + Guid.NewGuid().ToString("N"), Amount = award.Amount, Reason = award.Reason });
                observed.RemoveAt(i);
            }
        }
        private void Received(float amount, ScienceSubject subject, ProtoVessel vessel, bool reversed)
        {
            if (!PersonalClient.Active || subject == null || HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX ||
                float.IsInfinity(subject.science)) return;
            if (float.IsNaN(subject.science) || subject.science < 0 || subject.science > (float)PersonalProgression.MaxScience) return;
            var paired = observed.LastOrDefault(a => !a.HasSubject && a.Frame == Time.frameCount && a.Input == amount);
            if (paired != null) paired.HasSubject = true; // One award, never both transport paths.
            // KSP sends cumulative subject science after recording the award.
            // The server derives the new delta from a durable per-player high
            // water mark and a server-owned cap. Client amounts are not grants.
            decimal cumulative = decimal.Round((decimal)subject.science, 6, MidpointRounding.ToEven);
            PersonalClient.QueueScienceClaim(new AetherfrontWireRequest
            {
                CommandType = (AetherfrontCommandType)40, EntityId = subject.id,
                Body = cumulative.ToString(CultureInfo.InvariantCulture)
            });
        }
    }

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
            if (!Active || __result == null || HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return;
            var player = Current;
            if (player == null) return;
            decimal credited = CreditedScience(__result.id);
            if (__result.science < (float)credited)
            {
                __result.science = (float)credited;
                if (__result.applyScienceScale) __result.scientificValue = ResearchAndDevelopment.GetSubjectValue(__result.science, __result);
            }
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
            request.CommandId = "science:" + Guid.NewGuid().ToString("N");
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
            bool identityChanged = server != AetherfrontClientSystem.State.ServerId || account != player.AccountId;
            bool treeChanged = identityChanged || game != HighLogic.CurrentGame || research != ResearchAndDevelopment.Instance || scene != HighLogic.LoadedScene;
            var desired = new HashSet<string>(player.TechnologyUnlocks, StringComparer.OrdinalIgnoreCase);
            applying = true;
            try
            {
                if (HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.Instance != null)
                {
                    if (treeChanged || !ReferenceEquals(indexedPlayer, player))
                        foreach (var subject in ResearchAndDevelopment.GetSubjects()) RestoreSubject(subject);
                    SetScience.Invoke(null, new object[] { ResearchAndDevelopment.Instance, (float)player.Science });
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
            if (!string.IsNullOrEmpty(ScienceOutbox.Warning)) GUILayout.Label(ScienceOutbox.Warning);
            else if (ScienceOutbox.PendingCount > 0) GUILayout.Label("Science sync: " + ScienceOutbox.PendingCount + " reward(s) awaiting server confirmation.");
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
    }
}
