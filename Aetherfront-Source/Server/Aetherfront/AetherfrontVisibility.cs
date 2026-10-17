using Aetherfront.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Aetherfront
{
    internal static class AetherfrontVisibility
    {
        public static AetherfrontState ForAccount(AetherfrontState source, string accountId)
        {
            if (source == null) return new AetherfrontState();
            var projected = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(source));
            var own = source.FindFactionForAccount(accountId ?? string.Empty);
            var ownFactionId = own?.Id ?? string.Empty;
            var isModerator = source.ModeratorAccountIds.Any(id => string.Equals(id, accountId, StringComparison.OrdinalIgnoreCase));

            projected.Factions = projected.Factions.Where(f => isModerator || f.PublicProfile ||
                string.Equals(f.Id, ownFactionId, StringComparison.OrdinalIgnoreCase)).ToList();
            projected.Accounts = isModerator
                ? projected.Accounts
                : projected.Accounts.Where(a => string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var faction in projected.Factions)
            {
                if (!faction.PublicProfile && !string.Equals(faction.Id, ownFactionId, StringComparison.OrdinalIgnoreCase))
                    faction.Members.Clear();
            }

            projected.Crafts = projected.Crafts.Where(c =>
                string.Equals(c.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.OwnerFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                AetherfrontDomain.CanControlCraft(source, accountId ?? string.Empty, ownFactionId, c.Id,
                    CraftAccess.ViewMetadata, DateTime.UtcNow))
                .Select(c => ProjectCraft(source, c, accountId, ownFactionId))
                .ToList();
            projected.Trades = projected.Trades.Where(t =>
                string.Equals(t.ProposerFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.RecipientFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase)).ToList();
            projected.Agreements = projected.Agreements.Where(a =>
                string.Equals(a.ProposerFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.RecipientFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase)).ToList();
            projected.Loans = projected.Loans.Where(l => isModerator ||
                string.Equals(l.BorrowerFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.LenderFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!isModerator && projected.BankPolicy != null)
                projected.BankPolicy.BalanceMicro = 0;
            projected.Applications = projected.Applications.Where(a =>
                string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.FactionId, ownFactionId, StringComparison.OrdinalIgnoreCase)).ToList();
            projected.Stories = projected.Stories.Where(s => isModerator || s.Public ||
                string.Equals(s.FactionId, ownFactionId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!isModerator)
                projected.ModeratorAccountIds.Clear();
            projected.ObjectiveProgress = projected.ObjectiveProgress.Where(p =>
                string.Equals(p.FactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                projected.Objectives.Any(o => string.Equals(o.Id, p.ObjectiveId, StringComparison.OrdinalIgnoreCase) &&
                    !o.Archived && o.PublicProgress)).ToList();
            projected.ResearchProgress = projected.ResearchProgress.Where(p =>
                string.Equals(p.FactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                projected.ResearchProjects.Any(r => string.Equals(r.Id, p.ProjectId, StringComparison.OrdinalIgnoreCase) && r.PublicProgress)).ToList();
            projected.Events = projected.Events.Where(e => e.Public ||
                string.Equals(e.FactionId, ownFactionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.ActorAccountId, accountId, StringComparison.OrdinalIgnoreCase)).ToList();
            projected.CommandReceipts.Clear();
            return projected;
        }

        private static CraftRecord ProjectCraft(AetherfrontState source, CraftRecord craft, string accountId, string ownFactionId)
        {
            var own = string.Equals(craft.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(craft.OwnerFactionId, ownFactionId, StringComparison.OrdinalIgnoreCase);
            var full = own || AetherfrontDomain.CanControlCraft(source, accountId ?? string.Empty, ownFactionId,
                craft.Id, CraftAccess.ViewFull, DateTime.UtcNow);
            if (full) return craft;

            return new CraftRecord
            {
                Id = craft.Id,
                Name = craft.Name,
                OwnerFactionId = craft.OwnerFactionId,
                Revision = craft.Revision,
                Archived = craft.Archived,
                AccessGrants = new List<CraftAccessGrant>()
            };
        }

        public static AetherfrontWireReply ProjectReply(AetherfrontWireReply reply, string accountId)
        {
            if (reply == null || string.IsNullOrWhiteSpace(reply.StateJson)) return reply;
            var state = AetherfrontJson.Deserialize<AetherfrontState>(reply.StateJson);
            reply.StateJson = AetherfrontJson.Serialize(ForAccount(state, accountId));
            return reply;
        }
    }
}
