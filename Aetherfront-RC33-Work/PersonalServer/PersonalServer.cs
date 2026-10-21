using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Aetherfront.Core;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Server;
using Newtonsoft.Json;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Message;
using Server.Server;
using Server.Settings.Structures;

namespace Aetherfront.Personal.Server
{
    public static class PersonalServer
    {
        public const int ScienceClaimCommand = 40;
        public const int TransferCommand = 41;
        public const int GameplayScienceCommand = 42;
        private static readonly object CatalogGate = new object();
        private static Dictionary<string, decimal> caps;
        private static Dictionary<string, ScienceExperimentRule> experiments;

        // Runs inside the existing reader's authenticated, rate-limited and
        // suspended-account admission path and its fleet transaction gate.
        public static bool TryHandle(ClientStructure client, AetherfrontWireRequest request)
        {
            if (!AetherfrontServerSystem.Enabled || request == null || client == null || !client.Authenticated) return false;
            string actor = AetherfrontServerSystem.GetBoundAccountId(client);
            if (string.IsNullOrWhiteSpace(actor) || !AetherfrontServerSystem.Read(s => s.FindAccount(actor)?.Suspended == false)) return false;
            try
            {
                if (request.Operation == AetherfrontWireOperation.SnapshotRequest)
                {
                    EnsurePlayer(actor);
                    return false;
                }
                if (request.Operation != AetherfrontWireOperation.CommandRequest) return false;
                int command = (int)request.CommandType;
                if (command == ScienceClaimCommand || command == GameplayScienceCommand || command == TransferCommand || request.CommandType == AetherfrontCommandType.UnlockTechnology)
                {
                    EnsurePlayer(actor);
                    long beforeRevision = AetherfrontServerSystem.Read(s => s.Revision);
                    AetherfrontResult result;
                    if (GeneralSettings.SettingsStore.GameMode.ToString() == "Sandbox")
                        result = AetherfrontResult.Reject(AetherfrontServerSystem.Snapshot().Revision, "ProgressionUnavailableInSandbox");
                    else if (command == ScienceClaimCommand)
                        result = ScienceClaim(actor, request);
                    else if (command == GameplayScienceCommand)
                    {
                        decimal amount;
                        result = decimal.TryParse(request.Body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
                            ? AetherfrontServerSystem.Execute(s => PersonalProgression.RecordGameplayScienceReceipt(s, actor, request.EntityId, amount, request.Amount2))
                            : AetherfrontResult.Reject(beforeRevision, "InvalidScienceRewardReceipt");
                    }
                    else if (command == TransferCommand)
                        result = AetherfrontServerSystem.Execute(s => PersonalProgression.Transfer(s, actor, request.Amount2, request.TargetAccountId, request.Amount, 0));
                    else
                        result = AetherfrontServerSystem.Execute(s => PersonalProgression.UnlockTechnology(s, actor, request.Amount2, request.TechnologyId));
                    Reply(client, actor, result, command == ScienceClaimCommand || command == GameplayScienceCommand);
                    if (result.Accepted && result.Revision > beforeRevision) Notify();
                    return true;
                }
                if (request.CommandType == AetherfrontCommandType.TransferFunds || request.CommandType == AetherfrontCommandType.TransferScience ||
                    request.CommandType == AetherfrontCommandType.RequestLoan || request.CommandType == AetherfrontCommandType.ApproveLoan ||
                    request.CommandType == AetherfrontCommandType.RepayLoan || request.CommandType == AetherfrontCommandType.ContributeResearch ||
                    request.CommandType == AetherfrontCommandType.PurchaseStoreItem)
                {
                    Reply(client, actor, AetherfrontResult.Reject(AetherfrontServerSystem.Snapshot().Revision, "FactionEconomyRetired"));
                    return true;
                }
                if (request.CommandType == AetherfrontCommandType.CreateTradeOffer || request.CommandType == AetherfrontCommandType.ConfirmTrade || request.CommandType == AetherfrontCommandType.CancelTrade)
                {
                    EnsurePlayer(actor);
                    // Science awards advance the global revision frequently.
                    // Trades already pin craft revisions/hashes and revalidate
                    // consent, rights and personal balances under the store and
                    // fleet locks. Unrelated awards must not invalidate them.
                    request.ExpectedRevision = -1;
                }
                return false;
            }
            catch (Exception ex)
            {
                LunaLog.Error("Personal progression request failed: " + ex.GetType().Name);
                Reply(client, actor, AetherfrontResult.Reject(AetherfrontServerSystem.Snapshot().Revision, "PersonalProgressionUnavailable"));
                return true;
            }
        }

        private static List<TradeAsset> ReadAssets(string json) => string.IsNullOrWhiteSpace(json) ? new List<TradeAsset>() : AetherfrontJson.Deserialize<List<TradeAsset>>(json);
        private static bool IsCurrency(TradeAsset asset) => asset == null || asset.Type == TradeAssetTypes.Science || asset.Type == TradeAssetTypes.FundsMicro;

        // Called inside the original reader's Execute transaction. The original
        // fleet/revision/lease checks and domain trade settlement stay in use.
        public static bool TryApplyTrade(AetherfrontState state, AetherfrontWireRequest request, string actor, out AetherfrontResult result)
        {
            result = null;
            if (request.CommandType != AetherfrontCommandType.CreateTradeOffer && request.CommandType != AetherfrontCommandType.ConfirmTrade) return false;
            var trade = request.CommandType == AetherfrontCommandType.ConfirmTrade ? state.FindTrade(request.TradeId) : null;
            var give = trade?.ProposerAssets ?? ReadAssets(request.AssetJson);
            var receive = trade?.RecipientAssets ?? ReadAssets(request.CounterAssetJson);
            if (!give.Concat(receive).Any(IsCurrency)) return false;
            string denied = AetherfrontFleetGuard.CheckCommand(state, request);
            if (denied != null) { result = AetherfrontResult.Reject(state.Revision, denied); return true; }
            if (state.CommandReceipts.Any(r => r.CommandId == request.CommandId))
            {
                result = trade == null ? AetherfrontDomain.CreateTradeOffer(state, request.CommandId, actor, request.FactionId, request.TargetFactionId, give, receive, DateTime.UtcNow)
                    : AetherfrontDomain.ConfirmTrade(state, request.CommandId, actor, request.TradeId, DateTime.UtcNow);
                return true;
            }
            var proposer = state.FindFaction(trade?.ProposerFactionId ?? request.FactionId);
            var recipient = state.FindFaction(trade?.RecipientFactionId ?? request.TargetFactionId);
            if (proposer == null || recipient == null || proposer.Id == recipient.Id)
            { result = AetherfrontResult.Reject(state.Revision, "FactionUnavailable"); return true; }
            var sponsor = trade == null ? new PersonalTradeSponsor { ProposerAccountId = actor } : state.PersonalProgression.TradeSponsors.SingleOrDefault(s => s.TradeId == trade.Id);
            if (sponsor == null)
            { result = AetherfrontResult.Reject(state.Revision, "LegacyTradeNeedsRecreation"); return true; }
            if (state.FindFactionForAccount(sponsor.ProposerAccountId)?.Id != proposer.Id)
            { result = AetherfrontResult.Reject(state.Revision, "PersonalTradeSponsorUnavailable"); return true; }
            string recipientActor = sponsor.RecipientAccountId;
            if (trade != null && state.FindFactionForAccount(actor)?.Id == proposer.Id && actor != sponsor.ProposerAccountId)
            { result = AetherfrontResult.Reject(state.Revision, "OnlyPersonalSponsorCanConfirm"); return true; }
            if (trade != null && state.FindFactionForAccount(actor)?.Id == recipient.Id)
            {
                if (!string.IsNullOrEmpty(recipientActor) && recipientActor != actor)
                { result = AetherfrontResult.Reject(state.Revision, "OnlyPersonalSponsorCanConfirm"); return true; }
                recipientActor = actor;
            }
            if (!string.IsNullOrEmpty(recipientActor) && state.FindFactionForAccount(recipientActor)?.Id != recipient.Id)
            { result = AetherfrontResult.Reject(state.Revision, "PersonalTradeSponsorUnavailable"); return true; }
            var payer = PersonalProgression.Ensure(state, sponsor.ProposerAccountId);
            var counterparty = string.IsNullOrEmpty(recipientActor) ? null : PersonalProgression.Ensure(state, recipientActor);
            long oldProposerFunds = proposer.Progression.FundsMicro, oldRecipientFunds = recipient.Progression.FundsMicro;
            long oldProposerScience = proposer.Progression.Science, oldRecipientScience = recipient.Progression.Science;
            long proposerFunds = payer.FundsMicro, proposerScience = checked((long)decimal.Floor(payer.TradableScience));
            // The recipient has not consented at proposal time. Validate the
            // assets then, and check their own balance when they confirm.
            long recipientFunds = counterparty?.FundsMicro ?? AetherfrontConstants.MaxFundsMicroTransfer;
            long recipientScience = counterparty == null ? AetherfrontConstants.MaxScienceTransfer : checked((long)decimal.Floor(counterparty.TradableScience));
            long nextProposerFunds = proposerFunds, nextRecipientFunds = recipientFunds;
            decimal nextProposerScience = payer.Science, nextRecipientScience = counterparty?.Science ?? 0;
            decimal nextProposerTradable = payer.TradableScience, nextRecipientTradable = counterparty?.TradableScience ?? 0;
            bool committed = false;
            try
            {
                // A compatibility view exists only under the server store lock
                // for the existing domain validator. It is restored before any
                // persistence or reply; faction currency never becomes active.
                proposer.Progression.FundsMicro = proposerFunds; proposer.Progression.Science = proposerScience;
                recipient.Progression.FundsMicro = recipientFunds; recipient.Progression.Science = recipientScience;
                result = trade == null ? AetherfrontDomain.CreateTradeOffer(state, request.CommandId, actor, proposer.Id, recipient.Id, give, receive, DateTime.UtcNow)
                    : AetherfrontDomain.ConfirmTrade(state, request.CommandId, actor, trade.Id, DateTime.UtcNow);
                committed = result.Accepted && trade != null && trade.State == TradeStates.Committed;
                if (committed)
                {
                    if (counterparty == null) throw new InvalidOperationException("Personal trade requires two consenting sponsors");
                    nextProposerFunds = proposer.Progression.FundsMicro; nextRecipientFunds = recipient.Progression.FundsMicro;
                    nextProposerScience = payer.Science + proposer.Progression.Science - proposerScience;
                    nextRecipientScience = counterparty.Science + recipient.Progression.Science - recipientScience;
                    nextProposerTradable = payer.TradableScience + proposer.Progression.Science - proposerScience;
                    nextRecipientTradable = counterparty.TradableScience + recipient.Progression.Science - recipientScience;
                    if (nextProposerFunds < 0 || nextRecipientFunds < 0 || nextProposerScience < 0 || nextRecipientScience < 0 || nextProposerScience > PersonalProgression.MaxScience || nextRecipientScience > PersonalProgression.MaxScience)
                        throw new InvalidOperationException("Personal trade balance limit; transaction must roll back");
                }
            }
            finally
            {
                proposer.Progression.FundsMicro = oldProposerFunds; proposer.Progression.Science = oldProposerScience;
                recipient.Progression.FundsMicro = oldRecipientFunds; recipient.Progression.Science = oldRecipientScience;
            }
            if (result.Accepted)
            {
                if (trade == null)
                {
                    sponsor.TradeId = result.TradeId; state.PersonalProgression.TradeSponsors.Add(sponsor);
                }
                else if (!string.IsNullOrEmpty(recipientActor)) sponsor.RecipientAccountId = recipientActor;
                if (committed)
                {
                    payer.FundsMicro = nextProposerFunds; counterparty.FundsMicro = nextRecipientFunds;
                    payer.Science = nextProposerScience; counterparty.Science = nextRecipientScience;
                    payer.TradableScience = nextProposerTradable; counterparty.TradableScience = nextRecipientTradable;
                }
            }
            return true;
        }

        private static void EnsurePlayer(string actor)
        {
            bool needsSettlement = AetherfrontServerSystem.Read(s =>
            {
                var player = PersonalProgression.Find(s, actor);
                return !s.PersonalProgression.StartingBalancesConfigured || player == null;
            });
            if (!needsSettlement) return;
            AetherfrontServerSystem.Execute(s =>
            {
                if (!s.PersonalProgression.StartingBalancesConfigured)
                {
                    var policy = GameplaySettings.SettingsStore;
                    s.PersonalProgression.StartingFundsMicro = checked((long)((decimal)policy.StartingFunds * 1000000m));
                    s.PersonalProgression.StartingScience = (decimal)policy.StartingScience;
                    s.PersonalProgression.StartingReputation = checked((long)policy.StartingReputation);
                    s.PersonalProgression.StartingBalancesConfigured = true;
                }
                PersonalProgression.Ensure(s, actor);
                return AetherfrontResult.Success(s.Revision);
            });
        }

        private static AetherfrontResult ScienceClaim(string actor, AetherfrontWireRequest request)
        {
            decimal cumulative;
            if (!decimal.TryParse(request.Body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out cumulative) ||
                string.IsNullOrWhiteSpace(request.EntityId) || request.EntityId.Length > 256)
                return AetherfrontResult.Reject(AetherfrontServerSystem.Snapshot().Revision, "InvalidScienceEvidence");
            var catalog = LoadCaps();
            decimal cap;
            int separator = request.EntityId.IndexOf('@');
            ScienceExperimentRule experiment = null;
            if (separator > 0) experiments.TryGetValue(request.EntityId.Substring(0, separator), out experiment);
            if (!catalog.TryGetValue(request.EntityId, out cap))
            {
                // KSP's asteroid/comet overload appends '_' plus a source UID
                // to an otherwise registered experiment/body/situation/biome.
                // Its cap is the experiment's base cap, not the body multiplier.
                bool registered = false;
                if (experiment != null)
                    for (int i = request.EntityId.IndexOf('_', Math.Max(0, separator)); i >= 0; i = request.EntityId.IndexOf('_', i + 1))
                        if (i + 1 < request.EntityId.Length && catalog.ContainsKey(request.EntityId.Substring(0, i)))
                        { registered = true; break; }
                if (!registered) return AetherfrontResult.Reject(AetherfrontServerSystem.Snapshot().Revision, "ScienceSubjectNotRegistered");
                cap = experiment.Cap;
            }
            bool repeatable = experiment != null && !experiment.ApplyScienceScale;
            decimal multiplier = decimal.Round((decimal)GameplaySettings.SettingsStore.ScienceGainMultiplier, 6);
            if (multiplier <= 0 || multiplier > 100) throw new InvalidDataException("Unsupported science multiplier");
            // Tolerate the last float ULP at a completed stock subject, while
            // never crediting more than the server's cap.
            if (!repeatable && cumulative > cap + Math.Max(0.000001m, cap / 10000000m))
                return AetherfrontResult.Reject(AetherfrontServerSystem.Snapshot().Revision, "InvalidScienceEvidence");
            cumulative = decimal.Round(repeatable ? cumulative : Math.Min(cumulative, cap), 6, MidpointRounding.ToEven);
            cap = decimal.Round(cap, 6, MidpointRounding.ToEven);
            return AetherfrontServerSystem.Execute(s => PersonalProgression.RecordEarnedScience(s, actor, request.EntityId, cumulative, cap, multiplier, repeatable));
        }

        private static Dictionary<string, decimal> LoadCaps()
        {
            lock (CatalogGate)
            {
                if (caps != null) return caps;
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AetherfrontScienceSubjects.json");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 16 * 1024 * 1024) throw new InvalidDataException("Science catalog missing or oversized");
                string json = File.ReadAllText(path);
                List<ScienceSubjectCap> records;
                var rules = new Dictionary<string, ScienceExperimentRule>(StringComparer.Ordinal);
                if (json.TrimStart().StartsWith("[", StringComparison.Ordinal)) records = JsonConvert.DeserializeObject<List<ScienceSubjectCap>>(json);
                else
                {
                    var document = JsonConvert.DeserializeObject<ScienceCatalog>(json);
                    if (document == null || document.Version != 2 || document.Experiments == null || document.Experiments.Count > 4096)
                        throw new InvalidDataException("Invalid science experiment rules");
                    records = document.Subjects;
                    foreach (var rule in document.Experiments)
                    {
                        if (rule == null || string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 || rule.Id.Any(char.IsControl) || rule.Id.Contains("@") ||
                            rule.Cap <= 0 || rule.Cap > PersonalProgression.MaxAward || rules.ContainsKey(rule.Id)) throw new InvalidDataException("Invalid science experiment rule");
                        rules.Add(rule.Id, rule);
                    }
                }
                if (records == null || records.Count == 0 || records.Count > 100000) throw new InvalidDataException("Invalid science catalog");
                var validated = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var record in records)
                {
                    if (record == null || string.IsNullOrWhiteSpace(record.Id) || record.Id.Length > 256 || record.Cap <= 0 || record.Cap > PersonalProgression.MaxAward)
                        throw new InvalidDataException("Invalid science subject cap");
                    if (validated.TryGetValue(record.Id, out var old) && old != record.Cap) throw new InvalidDataException("Conflicting science subject cap");
                    validated[record.Id] = record.Cap;
                }
                experiments = rules; caps = validated;
                return caps;
            }
        }

        public static AetherfrontState Filter(AetherfrontState projected, string actor)
        {
            if (projected == null) return null;
            if (projected.PersonalProgression == null) throw new InvalidDataException("Personal progression missing");
            // Read-only snapshots derive outstanding shared credit. It is
            // persisted only with the next real mutation, avoiding one full
            // state write per online player for every science award.
            if (projected.FindAccount(actor)?.Suspended == false) PersonalProgression.Ensure(projected, actor);
            projected.PersonalProgression.Players = projected.PersonalProgression.Players.Where(p => string.Equals(p.AccountId, actor, StringComparison.OrdinalIgnoreCase)).ToList();
            projected.PersonalProgression.TradeSponsors = projected.PersonalProgression.TradeSponsors.Where(s => projected.Trades.Any(t => t.Id == s.TradeId)).ToList();
            foreach (var faction in projected.Factions)
            {
                // Historical data stays in the private authoritative snapshot.
                // It is no longer an active balance or a public progression UI.
                faction.Progression = new FactionProgression();
            }
            return projected;
        }

        private static void Reply(ClientStructure client, string actor, AetherfrontResult result, bool quiet = false)
        {
            var state = AetherfrontServerSystem.Snapshot();
            var reply = new AetherfrontWireReply
            {
                Operation = quiet ? AetherfrontWireOperation.SnapshotReply : AetherfrontWireOperation.CommandReply, Accepted = result.Accepted, ReasonCode = result.ReasonCode,
                Revision = state.Revision, ServerId = state.ServerId,
                StateJson = AetherfrontJson.Serialize(AetherfrontVisibility.ForAccount(state, actor)), EventsJson = "[]",
                InterestJson = AetherfrontVesselInterest.ForClient(state, actor, client.Subspace)
            };
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(reply);
            MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
        }

        private static void Notify()
        {
            var current = AetherfrontServerSystem.Read(s => new { s.Revision, s.ServerId });
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontEventNoticeMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireReply
            {
                Operation = AetherfrontWireOperation.EventNotice, Accepted = true, EventsJson = "[]",
                Revision = current.Revision, ServerId = current.ServerId
            });
            MessageQueuer.SendToAllClients<AetherfrontSrvMsg>(data);
        }

        private sealed class ScienceSubjectCap { public string Id { get; set; } public decimal Cap { get; set; } }
        private sealed class ScienceExperimentRule { public string Id { get; set; } public decimal Cap { get; set; } public bool ApplyScienceScale { get; set; } = true; }
        private sealed class ScienceCatalog { public int Version { get; set; } public List<ScienceSubjectCap> Subjects { get; set; } public List<ScienceExperimentRule> Experiments { get; set; } }
    }
}
