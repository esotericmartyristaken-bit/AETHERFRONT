using System;
using System.Collections.Generic;
using System.Linq;

namespace Aetherfront.Core
{
    // FactionProgression remains in historical state for recovery only. Personal
    // balances must never be inferred by copying a faction wallet to its members.
    public sealed class PersonalProgressionState
    {
        public int Version { get; set; } = 1;
        public decimal TotalEarnedScience { get; set; }
        public bool StartingBalancesConfigured { get; set; }
        public long StartingFundsMicro { get; set; }
        public decimal StartingScience { get; set; }
        public long StartingReputation { get; set; }
        public List<PlayerProgression> Players { get; set; } = new List<PlayerProgression>();
        public List<PersonalTradeSponsor> TradeSponsors { get; set; } = new List<PersonalTradeSponsor>();
        private Dictionary<string, PlayerProgression> index;
        internal PlayerProgression Find(string accountId)
        {
            if (index == null || index.Count != Players.Count) index = Players.ToDictionary(p => p.AccountId, StringComparer.Ordinal);
            PlayerProgression result;
            return index.TryGetValue(accountId, out result) ? result : null;
        }
        internal void Add(PlayerProgression player) { Players.Add(player); if (index != null) index.Add(player.AccountId, player); }
    }

    public sealed class PlayerProgression
    {
        public string AccountId { get; set; } = string.Empty;
        public decimal Science { get; set; }
        public decimal TradableScience { get; set; }
        public decimal EarnedScience { get; set; }
        public decimal SharedScienceCredited { get; set; }
        public long FundsMicro { get; set; }
        public long Reputation { get; set; }
        public List<string> TechnologyUnlocks { get; set; } = new List<string>();
        // Cumulative subject high-water marks survive reconnects, server restarts
        // and command-receipt pruning. A repeated experiment cannot mint science.
        public List<ScienceSourceCredit> ScienceSources { get; set; } = new List<ScienceSourceCredit>();
        public long NextCommandSequence { get; set; } = 1;
        private Dictionary<string, ScienceSourceCredit> sources;
        internal decimal PreviousScience(string sourceId)
        {
            if (sources == null || sources.Count != ScienceSources.Count) sources = ScienceSources.ToDictionary(s => s.Id, StringComparer.Ordinal);
            ScienceSourceCredit credit;
            return sources.TryGetValue(sourceId, out credit) ? credit.CumulativeScience : 0;
        }
        internal void CreditSource(string sourceId, decimal value, decimal award)
        {
            PreviousScience(sourceId);
            ScienceSourceCredit credit;
            if (!sources.TryGetValue(sourceId, out credit)) { credit = new ScienceSourceCredit { Id = sourceId }; ScienceSources.Add(credit); sources.Add(sourceId, credit); }
            credit.CumulativeScience = value;
            credit.CreditedScience += award;
        }
    }

    public sealed class ScienceSourceCredit
    {
        public string Id { get; set; } = string.Empty;
        public decimal CumulativeScience { get; set; }
        public decimal CreditedScience { get; set; }
    }

    public sealed class PersonalTradeSponsor
    {
        public string TradeId { get; set; } = string.Empty;
        public string ProposerAccountId { get; set; } = string.Empty;
        public string RecipientAccountId { get; set; } = string.Empty;
    }

    public static class PersonalProgression
    {
        public const decimal MaxScience = 1000000000000m;
        public const decimal MaxAward = 1000000m;
        public const int MaxSources = 100000;
        // Positive reward reasons from KSP's TransactionReasons. Research,
        // purchase, refund and cheat operations are deliberately excluded.
        public const long ScienceRewardReasons = 0x2 | 0x4 | 0x20 | 0x100 | 0x400 | 0x80000 | 0x100000;

        // KSP labs and some mods award science without a ScienceSubject. These
        // client-simulated rewards use immutable, durable receipts, not balance
        // deltas (which would recursively share grants and incoming trades).
        // As with vessel simulation, this is not cryptographic anti-cheat.
        public static AetherfrontResult RecordGameplayScienceReceipt(AetherfrontState state, string accountId, string receiptId, decimal amount, long reason)
        {
            Guid parsed;
            if (receiptId == null || !receiptId.StartsWith("award:", StringComparison.Ordinal) ||
                !Guid.TryParseExact(receiptId.Substring(6), "N", out parsed) || parsed == Guid.Empty ||
                reason < 0 || (reason & ~ScienceRewardReasons) != 0 || amount <= 0 || amount > MaxAward || decimal.Round(amount, 6) != amount)
                return AetherfrontResult.Reject(state.Revision, "InvalidScienceRewardReceipt");
            var account = state.FindAccount(accountId);
            if (account == null || account.Suspended) return AetherfrontResult.Reject(state.Revision, "AuthenticatedAccountRequired");
            receiptId = "award:" + parsed.ToString("N");
            var previous = Find(state, account.AccountId)?.PreviousScience(receiptId) ?? 0;
            if (previous > 0 && previous != amount) return AetherfrontResult.Reject(state.Revision, "ScienceRewardReceiptConflict");
            return RecordEarnedScience(state, account.AccountId, receiptId, amount, MaxAward);
        }

        public static PlayerProgression Find(AetherfrontState state, string accountId)
        {
            if (state == null || string.IsNullOrWhiteSpace(accountId) || state.PersonalProgression == null) return null;
            return state.PersonalProgression.Find(accountId);
        }

        // Called under the server store's transaction lock after authentication.
        // Lazy settlement is O(1), including for players who were offline.
        public static PlayerProgression Ensure(AetherfrontState state, string accountId, long startingFundsMicro = -1)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var account = state.FindAccount(accountId);
            if (account == null || account.Suspended) throw new InvalidOperationException("AuthenticatedAccountRequired");
            accountId = account.AccountId; // Canonical authenticated identifier.
            var ledger = state.PersonalProgression;
            if (ledger == null || ledger.Version != 1 || ledger.Players == null || ledger.TotalEarnedScience < 0 || ledger.TotalEarnedScience > MaxScience)
                throw new InvalidOperationException("InvalidPersonalProgressionState");
            if (startingFundsMicro == -1) startingFundsMicro = ledger.StartingFundsMicro;
            if (startingFundsMicro < 0 || ledger.StartingScience < 0 || ledger.StartingScience > MaxScience) throw new ArgumentOutOfRangeException(nameof(startingFundsMicro));
            PlayerProgression player = ledger.Find(accountId);
            if (player == null)
            {
                player = new PlayerProgression { AccountId = accountId, FundsMicro = startingFundsMicro, Reputation = ledger.StartingReputation };
                player.TechnologyUnlocks = state.TechnologyDefinitions.Where(t => t != null && t.Starter).Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var initialShared = ledger.TotalEarnedScience / 2m;
                if (initialShared + ledger.StartingScience > MaxScience) throw new InvalidOperationException("PersonalScienceLimit");
                player.Science = initialShared + ledger.StartingScience;
                player.SharedScienceCredited = initialShared;
                ledger.Add(player);
                return player;
            }
            var entitled = (ledger.TotalEarnedScience - player.EarnedScience) / 2m;
            var outstanding = entitled - player.SharedScienceCredited;
            if (player.EarnedScience < 0 || player.EarnedScience > ledger.TotalEarnedScience || player.SharedScienceCredited < 0 ||
                outstanding < 0 || player.Science < 0 || player.TradableScience < 0 || player.TradableScience > player.Science ||
                player.Science + outstanding > MaxScience || player.FundsMicro < 0 || player.NextCommandSequence < 1 ||
                player.TechnologyUnlocks == null || player.ScienceSources == null || player.ScienceSources.Count > MaxSources)
                throw new InvalidOperationException("InvalidPersonalScienceLedger");
            player.Science += outstanding;
            player.SharedScienceCredited = entitled;
            return player;
        }

        // This is a SERVER-VALIDATED cumulative award, not an RPC accepting an
        // arbitrary amount. The transport must obtain the cap from server data,
        // reject Sandbox, and bind the actor to the authenticated connection.
        // Transfers, refunds, starting science and shared grants never call it.
        public static AetherfrontResult RecordEarnedScience(AetherfrontState state, string accountId,
            string sourceId, decimal cumulativeScience, decimal serverCap, decimal rewardMultiplier = 1m, bool repeatable = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var account = state.FindAccount(accountId);
            if (account == null || account.Suspended) return AetherfrontResult.Reject(state.Revision, "AuthenticatedAccountRequired");
            accountId = account.AccountId;
            if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 256 || sourceId.Any(char.IsControl))
                return AetherfrontResult.Reject(state.Revision, "InvalidScienceSource");
            if (serverCap <= 0 || serverCap > MaxAward || cumulativeScience < 0 || cumulativeScience > (repeatable ? MaxScience : serverCap) || decimal.Round(cumulativeScience, 6) != cumulativeScience ||
                rewardMultiplier <= 0 || rewardMultiplier > 100 || decimal.Round(rewardMultiplier, 6) != rewardMultiplier)
                return AetherfrontResult.Reject(state.Revision, "InvalidScienceEvidence");
            var existing = Find(state, accountId);
            decimal previous = existing?.PreviousScience(sourceId) ?? 0;
            if (cumulativeScience <= previous) return AetherfrontResult.Success(state.Revision);
            if (previous == 0 && existing != null && existing.ScienceSources.Count >= MaxSources)
                return AetherfrontResult.Reject(state.Revision, "ScienceSourceLimit");
            var delta = (cumulativeScience - previous) * rewardMultiplier;
            var ledger = state.PersonalProgression;
            var outstanding = existing == null ? ledger.TotalEarnedScience / 2m :
                (ledger.TotalEarnedScience - existing.EarnedScience) / 2m - existing.SharedScienceCredited;
            if (delta > MaxAward || ledger.TotalEarnedScience + delta > MaxScience ||
                (existing?.Science ?? ledger.StartingScience) + outstanding + delta > MaxScience)
                return AetherfrontResult.Reject(state.Revision, "ScienceLimit");
            var player = Ensure(state, accountId);
            player.Science += delta;
            player.TradableScience += delta;
            player.EarnedScience += delta;
            player.CreditSource(sourceId, cumulativeScience, delta);
            ledger.TotalEarnedScience += delta;
            state.Revision++;
            return AetherfrontResult.Success(state.Revision);
        }

        public static AetherfrontResult UnlockTechnology(AetherfrontState state, string accountId, long sequence, string technologyId)
        {
            var definition = state.FindTechnology(technologyId ?? string.Empty);
            if (definition == null || definition.ScienceCost < 0) return AetherfrontResult.Reject(state.Revision, "UnknownTechnology");
            var player = Ensure(state, accountId);
            if (sequence != player.NextCommandSequence) return AetherfrontResult.Reject(state.Revision, "PersonalSequenceMismatch");
            if (player.TechnologyUnlocks.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))
                return AetherfrontResult.Reject(state.Revision, "TechnologyAlreadyUnlocked");
            bool missingPrerequisite = definition.PrerequisiteIds.Count > 0 && (definition.AnyPrerequisite
                ? !definition.PrerequisiteIds.Any(id => player.TechnologyUnlocks.Contains(id, StringComparer.OrdinalIgnoreCase))
                : definition.PrerequisiteIds.Any(id => !player.TechnologyUnlocks.Contains(id, StringComparer.OrdinalIgnoreCase)));
            if (missingPrerequisite)
                return AetherfrontResult.Reject(state.Revision, "TechnologyPrerequisiteMissing");
            if (player.Science < definition.ScienceCost) return AetherfrontResult.Reject(state.Revision, "InsufficientPersonalScience");
            if (sequence == long.MaxValue) return AetherfrontResult.Reject(state.Revision, "PersonalSequenceExhausted");
            player.Science -= definition.ScienceCost;
            // Use research-only shared/starting credit first. Earned science is
            // still fully usable and tradable, with no cap on playing more.
            player.TradableScience = Math.Min(player.TradableScience, player.Science);
            player.TechnologyUnlocks.Add(definition.Id);
            player.NextCommandSequence++;
            state.Revision++;
            return AetherfrontResult.Success(state.Revision);
        }

        public static AetherfrontResult Transfer(AetherfrontState state, string accountId, long sequence,
            string recipientAccountId, decimal science, long fundsMicro)
        {
            var senderAccount = state.FindAccount(accountId);
            var recipientAccount = state.FindAccount(recipientAccountId);
            if (senderAccount == null || recipientAccount == null || senderAccount.Suspended || recipientAccount.Suspended)
                return AetherfrontResult.Reject(state.Revision, "AccountUnavailable");
            if (string.Equals(senderAccount.AccountId, recipientAccount.AccountId, StringComparison.Ordinal))
                return AetherfrontResult.Reject(state.Revision, "SameAccountTransfer");
            if (science < 0 || science > MaxAward || decimal.Round(science, 6) != science || fundsMicro < 0 ||
                fundsMicro > AetherfrontConstants.MaxFundsMicroTransfer || (science == 0 && fundsMicro == 0))
                return AetherfrontResult.Reject(state.Revision, "InvalidPersonalTransfer");
            var sender = Ensure(state, senderAccount.AccountId);
            if (sequence != sender.NextCommandSequence) return AetherfrontResult.Reject(state.Revision, "PersonalSequenceMismatch");
            var recipient = Ensure(state, recipientAccount.AccountId);
            if (sender.Science < science || sender.FundsMicro < fundsMicro)
                return AetherfrontResult.Reject(state.Revision, "InsufficientPersonalBalance");
            if (sender.TradableScience < science)
                return AetherfrontResult.Reject(state.Revision, "SharedScienceCannotBeTransferred");
            if (recipient.Science + science > MaxScience || recipient.FundsMicro > long.MaxValue - fundsMicro || sequence == long.MaxValue)
                return AetherfrontResult.Reject(state.Revision, "PersonalBalanceLimit");
            sender.Science -= science;
            sender.TradableScience -= science;
            recipient.Science += science;
            recipient.TradableScience += science;
            sender.FundsMicro -= fundsMicro;
            recipient.FundsMicro += fundsMicro;
            sender.NextCommandSequence++;
            state.Revision++;
            return AetherfrontResult.Success(state.Revision);
        }
    }
}
