using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Aetherfront.Core
{
    public sealed class AetherfrontResult
    {
        public bool Accepted { get; private set; }
        public string ReasonCode { get; private set; }
        public long Revision { get; private set; }
        public string FactionId { get; private set; }
        public string TradeId { get; private set; }
        public List<AetherfrontEvent> Events { get; private set; }

        public static AetherfrontResult Success(long revision, string factionId = "", string tradeId = "", List<AetherfrontEvent> events = null)
        {
            return new AetherfrontResult
            {
                Accepted = true,
                ReasonCode = "Accepted",
                Revision = revision,
                FactionId = factionId ?? string.Empty,
                TradeId = tradeId ?? string.Empty,
                Events = events ?? new List<AetherfrontEvent>()
            };
        }

        public static AetherfrontResult Reject(long revision, string reasonCode)
        {
            return new AetherfrontResult
            {
                Accepted = false,
                ReasonCode = reasonCode ?? "Rejected",
                Revision = revision,
                Events = new List<AetherfrontEvent>()
            };
        }
    }

    public static class AetherfrontDomain
    {
        private static readonly Regex HexColor = new Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

        public static string NormalizeAccountId(string accountId)
        {
            var value = (accountId ?? string.Empty).Trim();
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }

        public static string NormalizeDisplayName(string displayName)
        {
            var value = (displayName ?? string.Empty).Trim();
            return value.Length > 64 ? value.Substring(0, 64) : value;
        }

        public static AetherfrontResult CreateFaction(
            AetherfrontState state,
            string commandId,
            string actorAccountId,
            string displayName,
            string abbreviation,
            string description,
            string colorHex,
            string flagAssetId,
            string flagSha256,
            DateTime nowUtc,
            string recruitmentPolicy = RecruitmentPolicies.Application,
            bool publicProfile = true)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;

            if (state.FindFactionForAccount(actorAccountId) != null)
                return Reject(state, commandId, "AlreadyInFaction");
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > AetherfrontConstants.MaxFactionNameLength)
                return Reject(state, commandId, "InvalidFactionName");
            if (string.IsNullOrWhiteSpace(abbreviation) || abbreviation.Trim().Length > AetherfrontConstants.MaxFactionAbbreviationLength)
                return Reject(state, commandId, "InvalidFactionAbbreviation");
            if ((description ?? string.Empty).Length > AetherfrontConstants.MaxFactionDescriptionLength)
                return Reject(state, commandId, "DescriptionTooLong");
            if (!HexColor.IsMatch(colorHex ?? string.Empty))
                return Reject(state, commandId, "InvalidFactionColor");
            if (!IsRecruitmentPolicy(recruitmentPolicy))
                return Reject(state, commandId, "InvalidRecruitmentPolicy");
            if (state.Factions.Any(f => string.Equals(f.Name, displayName.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "FactionNameTaken");

            var faction = new FactionRecord
            {
                Id = NewId(),
                Name = displayName.Trim(),
                Abbreviation = abbreviation.Trim().ToUpperInvariant(),
                Description = (description ?? string.Empty).Trim(),
                ColorHex = colorHex,
                FlagAssetId = flagAssetId ?? string.Empty,
                FlagSha256 = flagSha256 ?? string.Empty,
                PublicProfile = publicProfile,
                RecruitmentPolicy = recruitmentPolicy,
                FounderAccountId = actorAccountId,
                CreatedAtUtc = nowUtc,
                Revision = 1,
                Members = new List<FactionMember>
                {
                    new FactionMember
                    {
                        AccountId = actorAccountId,
                        Role = FactionRoles.Founder,
                        JoinedAtUtc = nowUtc,
                        Active = true
                    }
                },
                Progression = new FactionProgression { FundsMicro = 100000000 }
            };

            foreach (var starter in state.TechnologyDefinitions.Where(t => t != null && t.Starter))
                if (!faction.Progression.TechnologyUnlocks.Any(id => string.Equals(id, starter.Id, StringComparison.OrdinalIgnoreCase)))
                    faction.Progression.TechnologyUnlocks.Add(starter.Id.Trim());

            state.Factions.Add(faction);
            var events = Append(state, commandId, AetherfrontEventTypes.FactionCreated, actorAccountId, faction.Id, "", faction.Id, nowUtc,
                Field("name", faction.Name), Field("abbreviation", faction.Abbreviation));
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult JoinFaction(AetherfrontState state, string commandId, string actorAccountId, string factionId, string displayName, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || faction.State != FactionStates.Active) return Reject(state, commandId, "FactionUnavailable");
            if (faction.RecruitmentPolicy == RecruitmentPolicies.Application)
                return Reject(state, commandId, "FactionApplicationRequired");
            if (faction.RecruitmentPolicy == RecruitmentPolicies.Closed || faction.RecruitmentPolicy == RecruitmentPolicies.InviteOnly)
                return Reject(state, commandId, "FactionRecruitmentRestricted");
            if (state.FindFactionForAccount(actorAccountId) != null) return Reject(state, commandId, "AlreadyInFaction");
            if (faction.Members.Count(m => m.Active) >= AetherfrontConstants.MaxMembersPerFaction) return Reject(state, commandId, "FactionFull");
            var inactiveMember = faction.Members.Find(m => string.Equals(m.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase) && !m.Active);
            if (inactiveMember != null) faction.Members.Remove(inactiveMember);
            if (faction.Members.Any(m => string.Equals(m.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "AlreadyFactionMember");

            faction.Members.Add(new FactionMember
            {
                AccountId = actorAccountId,
                DisplayName = displayName ?? string.Empty,
                Role = FactionRoles.Member,
                JoinedAtUtc = nowUtc,
                Active = true
            });
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionJoined, actorAccountId, faction.Id, "", actorAccountId, nowUtc);
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult ApplyToFaction(AetherfrontState state, string commandId, string actorAccountId,
            string factionId, string displayName, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || faction.State != FactionStates.Active) return Reject(state, commandId, "FactionUnavailable");
            if (faction.RecruitmentPolicy != RecruitmentPolicies.Application) return Reject(state, commandId, "FactionDoesNotAcceptApplications");
            if (state.FindFactionForAccount(actorAccountId) != null) return Reject(state, commandId, "AlreadyInFaction");
            if (state.Applications.Any(a => a.State == FactionApplicationStates.Pending &&
                string.Equals(a.FactionId, factionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ApplicationAlreadyPending");
            var application = new FactionApplication
            {
                Id = NewId(),
                FactionId = faction.Id,
                AccountId = actorAccountId,
                DisplayName = displayName ?? string.Empty,
                CreatedAtUtc = nowUtc,
                State = FactionApplicationStates.Pending
            };
            state.Applications.Add(application);
            var events = Append(state, commandId, "FactionApplicationCreated", actorAccountId, faction.Id, "", application.Id, nowUtc);
            events[0].Public = false;
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult ReviewFactionApplication(AetherfrontState state, string commandId, string actorAccountId,
            string applicationId, bool accept, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var application = state.FindApplication(applicationId);
            if (application == null || application.State != FactionApplicationStates.Pending) return Reject(state, commandId, "ApplicationUnavailable");
            var faction = state.FindFaction(application.FactionId);
            if (faction == null || !CanManageFaction(faction, actorAccountId)) return Reject(state, commandId, "FactionPermissionDenied");
            if (!accept)
            {
                application.State = FactionApplicationStates.Rejected;
                application.ReviewedByAccountId = actorAccountId;
                var rejectedEvents = Append(state, commandId, "FactionApplicationRejected", actorAccountId, faction.Id, "", application.Id, nowUtc);
                rejectedEvents[0].Public = false;
                return Commit(state, commandId, rejectedEvents, faction.Id, "");
            }
            if (state.FindFactionForAccount(application.AccountId) != null) return Reject(state, commandId, "ApplicantAlreadyInFaction");
            if (faction.Members.Count(m => m.Active) >= AetherfrontConstants.MaxMembersPerFaction) return Reject(state, commandId, "FactionFull");
            faction.Members.Add(new FactionMember
            {
                AccountId = application.AccountId,
                DisplayName = application.DisplayName,
                Role = FactionRoles.Member,
                JoinedAtUtc = nowUtc,
                Active = true
            });
            faction.Revision++;
            application.State = FactionApplicationStates.Accepted;
            application.ReviewedByAccountId = actorAccountId;
            var events = Append(state, commandId, "FactionApplicationAccepted", actorAccountId, faction.Id, "", application.Id, nowUtc);
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult LeaveFaction(AetherfrontState state, string commandId, string actorAccountId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFactionForAccount(actorAccountId);
            if (faction == null) return Reject(state, commandId, "NotInFaction");
            var member = faction.Members.Find(m => string.Equals(m.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase) && m.Active);
            if (member == null) return Reject(state, commandId, "NotActiveMember");
            if (member.Role == FactionRoles.Founder && faction.Members.Count(m => m.Active) > 1)
                return Reject(state, commandId, "FounderMustTransferLeadership");

            member.Active = false;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionLeft, actorAccountId, faction.Id, "", actorAccountId, nowUtc);
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult UpdateFactionProfile(
            AetherfrontState state,
            string commandId,
            string actorAccountId,
            string factionId,
            string name,
            string abbreviation,
            string description,
            string colorHex,
            string flagAssetId,
            string flagSha256,
            DateTime nowUtc,
            string recruitmentPolicy = null,
            bool? publicProfile = null)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null) return Reject(state, commandId, "FactionNotFound");
            if (!CanManageFaction(faction, actorAccountId)) return Reject(state, commandId, "FactionPermissionDenied");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > AetherfrontConstants.MaxFactionNameLength)
                return Reject(state, commandId, "InvalidFactionName");
            if (string.IsNullOrWhiteSpace(abbreviation) || abbreviation.Trim().Length > AetherfrontConstants.MaxFactionAbbreviationLength)
                return Reject(state, commandId, "InvalidFactionAbbreviation");
            if ((description ?? string.Empty).Length > AetherfrontConstants.MaxFactionDescriptionLength)
                return Reject(state, commandId, "DescriptionTooLong");
            if (!HexColor.IsMatch(colorHex ?? string.Empty)) return Reject(state, commandId, "InvalidFactionColor");
            if (recruitmentPolicy != null && !IsRecruitmentPolicy(recruitmentPolicy))
                return Reject(state, commandId, "InvalidRecruitmentPolicy");
            if (state.Factions.Any(f => f.Id != faction.Id && string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "FactionNameTaken");

            faction.Name = name.Trim();
            faction.Abbreviation = abbreviation.Trim().ToUpperInvariant();
            faction.Description = (description ?? string.Empty).Trim();
            faction.ColorHex = colorHex;
            faction.FlagAssetId = flagAssetId ?? string.Empty;
            faction.FlagSha256 = flagSha256 ?? string.Empty;
            if (recruitmentPolicy != null) faction.RecruitmentPolicy = recruitmentPolicy;
            if (publicProfile.HasValue) faction.PublicProfile = publicProfile.Value;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionProfileChanged, actorAccountId, faction.Id, "", faction.Id, nowUtc);
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult InviteMember(AetherfrontState state, string commandId, string actorAccountId, string factionId, string targetAccountId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || faction.State != FactionStates.Active) return Reject(state, commandId, "FactionUnavailable");
            if (!CanManageFaction(faction, actorAccountId)) return Reject(state, commandId, "FactionPermissionDenied");
            if (string.IsNullOrWhiteSpace(targetAccountId)) return Reject(state, commandId, "TargetAccountRequired");
            if (string.Equals(actorAccountId, targetAccountId, StringComparison.OrdinalIgnoreCase)) return Reject(state, commandId, "AlreadyInFaction");
            if (state.FindFactionForAccount(targetAccountId) != null) return Reject(state, commandId, "TargetAlreadyInFaction");
            if (faction.Members.Count(m => m.Active) >= AetherfrontConstants.MaxMembersPerFaction) return Reject(state, commandId, "FactionFull");

            state.Invitations.RemoveAll(i => !i.Accepted && !i.Cancelled &&
                string.Equals(i.FactionId, factionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.AccountId, targetAccountId, StringComparison.OrdinalIgnoreCase));
            var invitation = new FactionInvitation
            {
                Id = NewId(),
                FactionId = faction.Id,
                AccountId = targetAccountId,
                InvitedByAccountId = actorAccountId,
                CreatedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddDays(14)
            };
            state.Invitations.Add(invitation);
            var events = Append(state, commandId, AetherfrontEventTypes.FactionInvitationCreated, actorAccountId, faction.Id, "", invitation.Id, nowUtc,
                Field("targetAccountId", targetAccountId));
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult AcceptInvitation(AetherfrontState state, string commandId, string actorAccountId, string invitationId, string displayName, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (state.FindFactionForAccount(actorAccountId) != null) return Reject(state, commandId, "AlreadyInFaction");
            var invitation = state.FindInvitation(invitationId);
            if (invitation == null || invitation.Cancelled || invitation.Accepted || invitation.ExpiresAtUtc <= nowUtc ||
                !string.Equals(invitation.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase))
                return Reject(state, commandId, "InvitationUnavailable");
            var faction = state.FindFaction(invitation.FactionId);
            if (faction == null || faction.State != FactionStates.Active) return Reject(state, commandId, "FactionUnavailable");
            if (faction.Members.Count(m => m.Active) >= AetherfrontConstants.MaxMembersPerFaction) return Reject(state, commandId, "FactionFull");

            faction.Members.RemoveAll(m => string.Equals(m.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase) && !m.Active);
            faction.Members.Add(new FactionMember
            {
                AccountId = actorAccountId,
                DisplayName = displayName ?? string.Empty,
                Role = FactionRoles.Member,
                JoinedAtUtc = nowUtc,
                Active = true
            });
            invitation.Accepted = true;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionJoined, actorAccountId, faction.Id, "", actorAccountId, nowUtc,
                Field("invitationId", invitation.Id));
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult RemoveMember(AetherfrontState state, string commandId, string actorAccountId, string factionId, string targetAccountId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null) return Reject(state, commandId, "FactionNotFound");
            if (!CanManageFaction(faction, actorAccountId)) return Reject(state, commandId, "FactionPermissionDenied");
            var member = faction.Members.Find(m => m.Active && string.Equals(m.AccountId, targetAccountId, StringComparison.OrdinalIgnoreCase));
            if (member == null) return Reject(state, commandId, "MemberNotFound");
            if (member.Role == FactionRoles.Founder) return Reject(state, commandId, "FounderCannotBeRemoved");
            member.Active = false;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionMemberRemoved, actorAccountId, faction.Id, "", targetAccountId, nowUtc);
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult SetMemberRole(AetherfrontState state, string commandId, string actorAccountId, string factionId, string targetAccountId, string role, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null) return Reject(state, commandId, "FactionNotFound");
            if (!CanManageFaction(faction, actorAccountId)) return Reject(state, commandId, "FactionPermissionDenied");
            if (!IsAssignableRole(role)) return Reject(state, commandId, "InvalidFactionRole");
            var member = faction.Members.Find(m => m.Active && string.Equals(m.AccountId, targetAccountId, StringComparison.OrdinalIgnoreCase));
            if (member == null) return Reject(state, commandId, "MemberNotFound");
            if (member.Role == FactionRoles.Founder) return Reject(state, commandId, "FounderRoleImmutable");
            member.Role = role;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionMemberRoleChanged, actorAccountId, faction.Id, "", targetAccountId, nowUtc,
                Field("role", role));
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult TransferLeadership(AetherfrontState state, string commandId, string actorAccountId, string factionId, string targetAccountId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null) return Reject(state, commandId, "FactionNotFound");
            var founder = faction.Members.Find(m => m.Active && m.Role == FactionRoles.Founder &&
                string.Equals(m.AccountId, actorAccountId, StringComparison.OrdinalIgnoreCase));
            var target = faction.Members.Find(m => m.Active && string.Equals(m.AccountId, targetAccountId, StringComparison.OrdinalIgnoreCase));
            if (founder == null || target == null) return Reject(state, commandId, "FounderPermissionRequired");
            founder.Role = FactionRoles.Leader;
            target.Role = FactionRoles.Founder;
            faction.FounderAccountId = target.AccountId;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FactionLeadershipTransferred, actorAccountId, faction.Id, "", targetAccountId, nowUtc);
            return Commit(state, commandId, events, faction.Id, "");
        }

        public static AetherfrontResult TransferScience(AetherfrontState state, string commandId, string actorAccountId, string sourceFactionId, string targetFactionId, long amount, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (amount <= 0 || amount > AetherfrontConstants.MaxScienceTransfer) return Reject(state, commandId, "InvalidScienceAmount");
            if (string.Equals(sourceFactionId, targetFactionId, StringComparison.OrdinalIgnoreCase)) return Reject(state, commandId, "SameFactionTransfer");
            var source = state.FindFaction(sourceFactionId);
            var target = state.FindFaction(targetFactionId);
            if (source == null || target == null || source.State != FactionStates.Active || target.State != FactionStates.Active) return Reject(state, commandId, "FactionUnavailable");
            if (!CanManageEconomy(source, actorAccountId)) return Reject(state, commandId, "EconomyPermissionDenied");
            if (source.Progression.Science < amount) return Reject(state, commandId, "InsufficientScience");

            source.Progression.Science -= amount;
            target.Progression.Science += amount;
            source.Revision++;
            target.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.ScienceTransferred, actorAccountId, source.Id, target.Id, "", nowUtc,
                Field("amount", amount.ToString()));
            return Commit(state, commandId, events, source.Id, "");
        }

        public static AetherfrontResult TransferFunds(AetherfrontState state, string commandId, string actorAccountId, string sourceFactionId, string targetFactionId, long amountMicro, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (amountMicro <= 0 || amountMicro > AetherfrontConstants.MaxFundsMicroTransfer) return Reject(state, commandId, "InvalidFundsAmount");
            if (string.Equals(sourceFactionId, targetFactionId, StringComparison.OrdinalIgnoreCase)) return Reject(state, commandId, "SameFactionTransfer");
            var source = state.FindFaction(sourceFactionId);
            var target = state.FindFaction(targetFactionId);
            if (source == null || target == null || source.State != FactionStates.Active || target.State != FactionStates.Active) return Reject(state, commandId, "FactionUnavailable");
            if (!CanManageEconomy(source, actorAccountId)) return Reject(state, commandId, "EconomyPermissionDenied");
            if (source.Progression.FundsMicro < amountMicro) return Reject(state, commandId, "InsufficientFunds");

            source.Progression.FundsMicro -= amountMicro;
            target.Progression.FundsMicro += amountMicro;
            source.Revision++;
            target.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FundsTransferred, actorAccountId, source.Id, target.Id, "", nowUtc,
                Field("amountMicro", amountMicro.ToString()));
            return Commit(state, commandId, events, source.Id, "");
        }

        public static AetherfrontResult RegisterCraft(AetherfrontState state, string commandId, string actorAccountId, string factionId, string craftId, string name, string contentSha256, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (!string.IsNullOrWhiteSpace(factionId) && faction == null) return Reject(state, commandId, "FactionNotFound");
            if (faction != null && !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (string.IsNullOrWhiteSpace(craftId) || string.IsNullOrWhiteSpace(contentSha256)) return Reject(state, commandId, "InvalidCraft");
            if (state.FindCraft(craftId) != null) return Reject(state, commandId, "CraftAlreadyRegistered");

            state.Crafts.Add(new CraftRecord
            {
                Id = craftId,
                Name = name ?? string.Empty,
                OwnerFactionId = factionId,
                CreatorAccountId = actorAccountId,
                ContentSha256 = contentSha256,
                Revision = 1,
                OwnerAccountId = actorAccountId
            });
            var events = Append(state, commandId, AetherfrontEventTypes.CraftRegistered, actorAccountId, factionId, "", craftId, nowUtc);
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult GrantCraftAccess(AetherfrontState state, string commandId, string actorAccountId, string craftId, string granteeFactionId, IEnumerable<string> permissions, DateTime? expiresAtUtc, string agreementId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var craft = state.FindCraft(craftId);
            var grantee = state.FindFaction(granteeFactionId);
            var owner = craft == null ? null : state.FindFaction(craft.OwnerFactionId);
            if (craft == null || owner == null || grantee == null) return Reject(state, commandId, "CraftOrFactionNotFound");
            if (!CanManageCraft(owner, actorAccountId)) return Reject(state, commandId, "CraftPermissionDenied");
            var selected = (permissions ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (selected.Count == 0) return Reject(state, commandId, "NoCraftPermissions");

            craft.AccessGrants.RemoveAll(g => string.Equals(g.GranteeFactionId, granteeFactionId, StringComparison.OrdinalIgnoreCase));
            craft.AccessGrants.Add(new CraftAccessGrant
            {
                GranteeFactionId = granteeFactionId,
                Permissions = selected,
                ExpiresAtUtc = expiresAtUtc,
                AgreementId = agreementId ?? string.Empty
            });
            craft.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.CraftAccessGranted, actorAccountId, owner.Id, grantee.Id, craft.Id, nowUtc,
                Field("permissions", string.Join(",", selected)));
            return Commit(state, commandId, events, owner.Id, "");
        }

        public static bool CanControlCraft(AetherfrontState state, string accountId, string factionId, string craftId, string requiredPermission, DateTime nowUtc)
        {
            return CanControlCraft(state, accountId, factionId, craftId, requiredPermission, nowUtc, int.MinValue, int.MinValue);
        }

        public static bool CanControlCraft(AetherfrontState state, string accountId, string factionId, string craftId,
            string requiredPermission, DateTime nowUtc, int craftSubspaceId, int actorSubspaceId)
        {
            var craft = state.FindCraft(craftId);
            if (craft == null) return false;
            if (craft.Archived) return false;
            if (string.Equals(craft.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(craft.OwnerFactionId)) return true;
                var ownerAccountFaction = state.FindFaction(craft.OwnerFactionId);
                return ownerAccountFaction != null && IsActiveMember(ownerAccountFaction, accountId);
            }
            if (string.Equals(craft.OwnerFactionId, factionId, StringComparison.OrdinalIgnoreCase))
            {
                var owner = state.FindFaction(craft.OwnerFactionId);
                return owner != null && IsActiveMember(owner, accountId);
            }

            var ownerFaction = state.FindFaction(craft.OwnerFactionId);
            var viewerFaction = state.FindFaction(factionId);
            if (ownerFaction != null && viewerFaction != null &&
                (requiredPermission == CraftAccess.ViewMetadata || requiredPermission == CraftAccess.ViewFull ||
                 requiredPermission == CraftAccess.Clone || requiredPermission == CraftAccess.Use ||
                 requiredPermission == CraftAccess.Launch) &&
                HasActiveAlliance(state, ownerFaction.Id, viewerFaction.Id, nowUtc) &&
                IsSubspaceCompatible(craftSubspaceId, actorSubspaceId) &&
                IsActiveMember(viewerFaction, accountId))
                return true;

            return craft.AccessGrants.Any(g =>
                ((!string.IsNullOrWhiteSpace(g.GranteeFactionId) && string.Equals(g.GranteeFactionId, factionId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(g.GranteeAccountId) && string.Equals(g.GranteeAccountId, accountId, StringComparison.OrdinalIgnoreCase))) &&
                (g.ExpiresAtUtc == null || g.ExpiresAtUtc.Value > nowUtc) &&
                IsSubspaceCompatible(craftSubspaceId, actorSubspaceId) &&
                g.Permissions.Any(p => string.Equals(p, requiredPermission, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool IsSubspaceCompatible(int craftSubspaceId, int actorSubspaceId)
        {
            // Legacy/domain-only callers may not have runtime subspace data yet. The
            // vessel relay path supplies both values and gets the strict equality gate.
            return craftSubspaceId == int.MinValue || actorSubspaceId == int.MinValue || craftSubspaceId == actorSubspaceId;
        }

        public static bool HasActiveAlliance(AetherfrontState state, string firstFactionId, string secondFactionId, DateTime nowUtc)
        {
            return state.Agreements.Any(a => a.Type == AgreementTypes.Alliance && a.State == AgreementStates.Active &&
                ((string.Equals(a.ProposerFactionId, firstFactionId, StringComparison.OrdinalIgnoreCase) && string.Equals(a.RecipientFactionId, secondFactionId, StringComparison.OrdinalIgnoreCase)) ||
                 (string.Equals(a.ProposerFactionId, secondFactionId, StringComparison.OrdinalIgnoreCase) && string.Equals(a.RecipientFactionId, firstFactionId, StringComparison.OrdinalIgnoreCase))) &&
                (a.ExpiresAtUtc == null || a.ExpiresAtUtc.Value > nowUtc));
        }

        public static AetherfrontResult ProposeAlliance(AetherfrontState state, string commandId, string actorAccountId, string proposerFactionId, string recipientFactionId, DateTime? expiresAtUtc, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (string.Equals(proposerFactionId, recipientFactionId, StringComparison.OrdinalIgnoreCase)) return Reject(state, commandId, "SameFactionAgreement");
            var proposer = state.FindFaction(proposerFactionId);
            var recipient = state.FindFaction(recipientFactionId);
            if (proposer == null || recipient == null || proposer.State != FactionStates.Active || recipient.State != FactionStates.Active)
                return Reject(state, commandId, "FactionUnavailable");
            if (!CanManageDiplomacy(proposer, actorAccountId)) return Reject(state, commandId, "DiplomacyPermissionDenied");
            if (HasActiveAlliance(state, proposerFactionId, recipientFactionId, nowUtc)) return Reject(state, commandId, "AgreementAlreadyActive");

            var agreement = new DiplomaticAgreement
            {
                Id = NewId(),
                Type = AgreementTypes.Alliance,
                ProposerFactionId = proposerFactionId,
                RecipientFactionId = recipientFactionId,
                State = AgreementStates.Proposed,
                CreatedAtUtc = nowUtc,
                ExpiresAtUtc = expiresAtUtc
            };
            state.Agreements.Add(agreement);
            var events = Append(state, commandId, AetherfrontEventTypes.AgreementProposed, actorAccountId, proposerFactionId, recipientFactionId, agreement.Id, nowUtc);
            return Commit(state, commandId, events, proposerFactionId, "");
        }

        public static AetherfrontResult RespondToAgreement(AetherfrontState state, string commandId, string actorAccountId, string agreementId, bool accept, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var agreement = state.FindAgreement(agreementId);
            if (agreement == null || agreement.State != AgreementStates.Proposed ||
                (agreement.ExpiresAtUtc != null && agreement.ExpiresAtUtc.Value <= nowUtc))
                return Reject(state, commandId, "AgreementUnavailable");
            var recipient = state.FindFaction(agreement.RecipientFactionId);
            if (recipient == null || !CanManageDiplomacy(recipient, actorAccountId)) return Reject(state, commandId, "DiplomacyPermissionDenied");
            agreement.State = accept ? AgreementStates.Active : AgreementStates.Declined;
            var type = accept ? AetherfrontEventTypes.AgreementAccepted : AetherfrontEventTypes.AgreementRevoked;
            var events = Append(state, commandId, type, actorAccountId, agreement.ProposerFactionId, agreement.RecipientFactionId, agreement.Id, nowUtc,
                Field("accepted", accept.ToString()));
            return Commit(state, commandId, events, agreement.RecipientFactionId, "");
        }

        public static AetherfrontResult RevokeAgreement(AetherfrontState state, string commandId, string actorAccountId, string agreementId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var agreement = state.FindAgreement(agreementId);
            if (agreement == null || agreement.State != AgreementStates.Active) return Reject(state, commandId, "AgreementUnavailable");
            var proposer = state.FindFaction(agreement.ProposerFactionId);
            var recipient = state.FindFaction(agreement.RecipientFactionId);
            if ((proposer == null || !CanManageDiplomacy(proposer, actorAccountId)) &&
                (recipient == null || !CanManageDiplomacy(recipient, actorAccountId)))
                return Reject(state, commandId, "DiplomacyPermissionDenied");
            agreement.State = AgreementStates.Revoked;
            var events = Append(state, commandId, AetherfrontEventTypes.AgreementRevoked, actorAccountId, agreement.ProposerFactionId, agreement.RecipientFactionId, agreement.Id, nowUtc);
            return Commit(state, commandId, events, agreement.ProposerFactionId, "");
        }

        public static AetherfrontResult CreateTradeOffer(AetherfrontState state, string commandId, string actorAccountId, string proposerFactionId, string recipientFactionId, IEnumerable<TradeAsset> proposerAssets, IEnumerable<TradeAsset> recipientAssets, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (string.Equals(proposerFactionId, recipientFactionId, StringComparison.OrdinalIgnoreCase)) return Reject(state, commandId, "SameFactionTrade");
            var proposer = state.FindFaction(proposerFactionId);
            var recipient = state.FindFaction(recipientFactionId);
            if (proposer == null || recipient == null || proposer.State != FactionStates.Active || recipient.State != FactionStates.Active)
                return Reject(state, commandId, "FactionUnavailable");
            if (!CanManageEconomy(proposer, actorAccountId)) return Reject(state, commandId, "EconomyPermissionDenied");
            var give = (proposerAssets ?? new TradeAsset[0]).Select(CloneAsset).ToList();
            var receive = (recipientAssets ?? new TradeAsset[0]).Select(CloneAsset).ToList();
            if (give.Count == 0 && receive.Count == 0) return Reject(state, commandId, "NoTradeAssets");
            var assetError = ValidateTradeAssets(state, proposerFactionId, give);
            if (assetError != null) return Reject(state, commandId, "Proposer" + assetError);
            assetError = ValidateTradeAssets(state, recipientFactionId, receive);
            if (assetError != null) return Reject(state, commandId, "Recipient" + assetError);
            if (give.Concat(receive).Count() > 32) return Reject(state, commandId, "TooManyTradeAssets");
            if (give.Concat(receive).Where(a => a.Type == TradeAssetTypes.Craft).GroupBy(a => a.EntityId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                return Reject(state, commandId, "DuplicateCraftAsset");

            var trade = new TradeOffer
            {
                Id = NewId(),
                ProposerFactionId = proposerFactionId,
                RecipientFactionId = recipientFactionId,
                State = TradeStates.Proposed,
                CreatedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddDays(7),
                ProposerAssets = give,
                RecipientAssets = receive
            };
            state.Trades.Add(trade);
            var events = Append(state, commandId, AetherfrontEventTypes.TradeProposed, actorAccountId, proposerFactionId, recipientFactionId, trade.Id, nowUtc);
            return Commit(state, commandId, events, proposerFactionId, trade.Id);
        }

        public static AetherfrontResult ConfirmTrade(AetherfrontState state, string commandId, string actorAccountId, string tradeId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var trade = state.FindTrade(tradeId);
            if (trade == null || trade.State == TradeStates.Cancelled || trade.State == TradeStates.Expired || trade.State == TradeStates.Committed)
                return Reject(state, commandId, "TradeUnavailable");
            if (trade.ExpiresAtUtc <= nowUtc) { trade.State = TradeStates.Expired; return Reject(state, commandId, "TradeExpired"); }
            var proposer = state.FindFaction(trade.ProposerFactionId);
            var recipient = state.FindFaction(trade.RecipientFactionId);
            if (proposer == null || recipient == null) return Reject(state, commandId, "FactionUnavailable");
            if (IsActiveMember(proposer, actorAccountId) && CanManageEconomy(proposer, actorAccountId)) trade.ProposerConfirmed = true;
            else if (IsActiveMember(recipient, actorAccountId) && CanManageEconomy(recipient, actorAccountId)) trade.RecipientConfirmed = true;
            else return Reject(state, commandId, "TradePermissionDenied");

            if (!trade.ProposerConfirmed || !trade.RecipientConfirmed)
            {
                trade.State = TradeStates.PartiallyConfirmed;
                var pending = Append(state, commandId, AetherfrontEventTypes.TradeProposed, actorAccountId, trade.ProposerFactionId, trade.RecipientFactionId, trade.Id, nowUtc,
                    Field("confirmed", actorAccountId));
                return Commit(state, commandId, pending, trade.ProposerFactionId, trade.Id);
            }

            var settlementError = SettleTrade(state, trade, nowUtc);
            if (settlementError != null) return Reject(state, commandId, settlementError);
            trade.State = TradeStates.Committed;
            trade.CommitCommandId = commandId;
            var events = Append(state, commandId, AetherfrontEventTypes.TradeCommitted, actorAccountId, trade.ProposerFactionId, trade.RecipientFactionId, trade.Id, nowUtc);
            return Commit(state, commandId, events, trade.ProposerFactionId, trade.Id);
        }

        public static AetherfrontResult CancelTrade(AetherfrontState state, string commandId, string actorAccountId, string tradeId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var trade = state.FindTrade(tradeId);
            if (trade == null || trade.State == TradeStates.Committed || trade.State == TradeStates.Cancelled) return Reject(state, commandId, "TradeUnavailable");
            var proposer = state.FindFaction(trade.ProposerFactionId);
            var recipient = state.FindFaction(trade.RecipientFactionId);
            if ((proposer == null || !CanManageEconomy(proposer, actorAccountId)) && (recipient == null || !CanManageEconomy(recipient, actorAccountId)))
                return Reject(state, commandId, "TradePermissionDenied");
            trade.State = TradeStates.Cancelled;
            var events = Append(state, commandId, AetherfrontEventTypes.TradeCancelled, actorAccountId, trade.ProposerFactionId, trade.RecipientFactionId, trade.Id, nowUtc);
            return Commit(state, commandId, events, trade.ProposerFactionId, trade.Id);
        }

        public static AetherfrontResult PublishStory(AetherfrontState state, string commandId, string actorAccountId, string factionId, string title, string body, bool isPublic, IEnumerable<string> tags, IEnumerable<string> relatedEventIds, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > AetherfrontConstants.MaxStoryTitleLength) return Reject(state, commandId, "InvalidStoryTitle");
            if (string.IsNullOrWhiteSpace(body) || body.Trim().Length > AetherfrontConstants.MaxStoryBodyLength) return Reject(state, commandId, "InvalidStoryBody");
            var story = new StoryPublication
            {
                Id = NewId(),
                FactionId = factionId,
                AuthorAccountId = actorAccountId,
                Title = title.Trim(),
                Body = body.Trim(),
                PublishedAtUtc = nowUtc,
                Public = false,
                RequestedPublic = isPublic,
                ModerationState = StoryModerationStates.Pending,
                Tags = (tags ?? new string[0]).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
                RelatedEventIds = (relatedEventIds ?? new string[0]).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList()
            };
            state.Stories.Add(story);
            var events = Append(state, commandId, AetherfrontEventTypes.StoryPublished, actorAccountId, factionId, "", story.Id, nowUtc,
                Field("title", story.Title));
            events[0].Public = false;
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult ReviewStory(AetherfrontState state, string commandId, string actorAccountId,
            string storyId, bool approve, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (!state.ModeratorAccountIds.Any(id => string.Equals(id, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ModeratorPermissionDenied");
            var story = state.Stories.Find(s => string.Equals(s.Id, storyId, StringComparison.OrdinalIgnoreCase));
            if (story == null || story.ModerationState != StoryModerationStates.Pending)
                return Reject(state, commandId, "StoryUnavailable");
            story.ModerationState = approve ? StoryModerationStates.Approved : StoryModerationStates.Rejected;
            story.Public = approve && story.RequestedPublic;
            story.ApprovedByAccountId = actorAccountId;
            story.ReviewedAtUtc = nowUtc;
            var events = Append(state, commandId, approve ? "StoryApproved" : "StoryRejected", actorAccountId,
                story.FactionId, "", story.Id, nowUtc);
            events[0].Public = story.Public;
            return Commit(state, commandId, events, story.FactionId, "");
        }

        public static AetherfrontResult RequestLoan(AetherfrontState state, string commandId, string actorAccountId,
            string borrowerFactionId, long principalMicro, int aprBasisPoints, int termDays, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var policy = state.BankPolicy ?? new CentralBankPolicy();
            state.BankPolicy = policy;
            var borrower = state.FindFaction(borrowerFactionId);
            if (!policy.Enabled) return Reject(state, commandId, "CentralBankDisabled");
            if (borrower == null || borrower.State != FactionStates.Active || !CanManageEconomy(borrower, actorAccountId))
                return Reject(state, commandId, "BorrowerPermissionDenied");
            if (principalMicro <= 0 || principalMicro > policy.MaxLoanMicro) return Reject(state, commandId, "InvalidLoanPrincipal");
            if (termDays <= 0 || termDays > policy.MaxTermDays) return Reject(state, commandId, "InvalidLoanTerm");
            if (aprBasisPoints == 0) aprBasisPoints = policy.BaseAprBasisPoints;
            if (aprBasisPoints < 0 || aprBasisPoints > policy.MaxAprBasisPoints) return Reject(state, commandId, "InvalidLoanApr");
            if (state.Loans.Count(l => string.Equals(l.BorrowerFactionId, borrowerFactionId, StringComparison.OrdinalIgnoreCase) &&
                (l.State == LoanStates.Pending || l.State == LoanStates.Active || l.State == LoanStates.Defaulted)) >= policy.MaxOpenLoansPerFaction)
                return Reject(state, commandId, "LoanLimitReached");

            var loan = new LoanRecord
            {
                Id = NewId(),
                BorrowerFactionId = borrowerFactionId,
                PrincipalMicro = principalMicro,
                OutstandingMicro = principalMicro,
                AprBasisPoints = aprBasisPoints,
                CreatedAtUtc = nowUtc,
                DueAtUtc = nowUtc.AddDays(termDays),
                LastAccrualAtUtc = nowUtc,
                State = LoanStates.Pending
            };
            state.Loans.Add(loan);
            var events = Append(state, commandId, AetherfrontEventTypes.LoanRequested, actorAccountId, borrowerFactionId, "", loan.Id, nowUtc,
                Field("principalMicro", principalMicro.ToString()), Field("aprBasisPoints", aprBasisPoints.ToString()), Field("termDays", termDays.ToString()));
            return Commit(state, commandId, events, borrowerFactionId, "");
        }

        public static AetherfrontResult ApproveLoan(AetherfrontState state, string commandId, string actorAccountId,
            string loanId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (!state.ModeratorAccountIds.Any(id => string.Equals(id, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ModeratorPermissionDenied");
            var loan = state.Loans.Find(l => string.Equals(l.Id, loanId, StringComparison.OrdinalIgnoreCase));
            var policy = state.BankPolicy ?? new CentralBankPolicy();
            state.BankPolicy = policy;
            if (!policy.Enabled || loan == null || loan.State != LoanStates.Pending) return Reject(state, commandId, "LoanUnavailable");
            if (policy.BalanceMicro < loan.PrincipalMicro) return Reject(state, commandId, "CentralBankInsufficientFunds");
            var borrower = state.FindFaction(loan.BorrowerFactionId);
            if (borrower == null || borrower.State != FactionStates.Active) return Reject(state, commandId, "BorrowerUnavailable");

            policy.BalanceMicro -= loan.PrincipalMicro;
            borrower.Progression.FundsMicro = checked(borrower.Progression.FundsMicro + loan.PrincipalMicro);
            loan.State = LoanStates.Active;
            loan.LastAccrualAtUtc = nowUtc;
            borrower.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.LoanApproved, actorAccountId, borrower.Id, "", loan.Id, nowUtc,
                Field("principalMicro", loan.PrincipalMicro.ToString()));
            return Commit(state, commandId, events, borrower.Id, "");
        }

        public static AetherfrontResult RepayLoan(AetherfrontState state, string commandId, string actorAccountId,
            string loanId, long amountMicro, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var loan = state.Loans.Find(l => string.Equals(l.Id, loanId, StringComparison.OrdinalIgnoreCase));
            if (state.BankPolicy == null) state.BankPolicy = new CentralBankPolicy();
            if (loan == null || (loan.State != LoanStates.Active && loan.State != LoanStates.Defaulted))
                return Reject(state, commandId, "LoanUnavailable");
            var borrower = state.FindFaction(loan.BorrowerFactionId);
            if (borrower == null || !CanManageEconomy(borrower, actorAccountId)) return Reject(state, commandId, "BorrowerPermissionDenied");
            if (amountMicro <= 0) return Reject(state, commandId, "InvalidRepayment");

            var originalOutstanding = loan.OutstandingMicro;
            var originalLastAccrual = loan.LastAccrualAtUtc;
            var originalState = loan.State;
            var events = new List<AetherfrontEvent>();
            var interest = AccrueLoan(loan, nowUtc);
            if (interest > 0)
                events.AddRange(Append(state, commandId, AetherfrontEventTypes.LoanInterestAccrued, actorAccountId, borrower.Id, "", loan.Id, nowUtc,
                    Field("interestMicro", interest.ToString())));
            if (nowUtc > loan.DueAtUtc && loan.State == LoanStates.Active)
            {
                loan.State = LoanStates.Defaulted;
                events.AddRange(Append(state, commandId, AetherfrontEventTypes.LoanDefaulted, actorAccountId, borrower.Id, "", loan.Id, nowUtc));
            }
            amountMicro = Math.Min(amountMicro, loan.OutstandingMicro);
            if (borrower.Progression.FundsMicro < amountMicro)
            {
                loan.OutstandingMicro = originalOutstanding;
                loan.LastAccrualAtUtc = originalLastAccrual;
                loan.State = originalState;
                return Reject(state, commandId, "InsufficientFunds");
            }
            borrower.Progression.FundsMicro -= amountMicro;
            state.BankPolicy.BalanceMicro = checked(state.BankPolicy.BalanceMicro + amountMicro);
            loan.OutstandingMicro -= amountMicro;
            if (loan.OutstandingMicro == 0) loan.State = LoanStates.Repaid;
            borrower.Revision++;
            events.AddRange(Append(state, commandId, AetherfrontEventTypes.LoanRepaid, actorAccountId, borrower.Id, "", loan.Id, nowUtc,
                Field("amountMicro", amountMicro.ToString()), Field("outstandingMicro", loan.OutstandingMicro.ToString())));
            return Commit(state, commandId, events, borrower.Id, "");
        }

        public static AetherfrontResult RecordObjectiveEvent(AetherfrontState state, string commandId, string actorAccountId, string factionId, string eventType, long amount, string entityId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (amount <= 0) return Reject(state, commandId, "InvalidObjectiveAmount");

            var events = new List<AetherfrontEvent>();
            foreach (var objective in state.Objectives.Where(o => o != null && !o.Archived && string.Equals(o.EventType, eventType, StringComparison.OrdinalIgnoreCase)))
            {
                var periodKey = GetObjectivePeriodKey(objective, nowUtc);
                if (string.IsNullOrWhiteSpace(periodKey)) continue;
                var progress = state.ObjectiveProgress.Find(p => p.ObjectiveId == objective.Id && p.FactionId == factionId &&
                    string.Equals(NormalizePeriodKey(p.PeriodKey), periodKey, StringComparison.OrdinalIgnoreCase));
                if (progress == null)
                {
                    progress = new ObjectiveProgress { ObjectiveId = objective.Id, FactionId = factionId, PeriodKey = periodKey };
                    state.ObjectiveProgress.Add(progress);
                }
                if (progress.Completed) continue;
                progress.Progress = Math.Min(objective.Target, progress.Progress + amount);
                events.AddRange(Append(state, commandId, AetherfrontEventTypes.ObjectiveProgressed, actorAccountId, factionId, "", entityId, nowUtc,
                    Field("objectiveId", objective.Id), Field("progress", progress.Progress.ToString())));
                if (progress.Progress >= objective.Target)
                {
                    progress.Completed = true;
                    progress.CompletedAtUtc = nowUtc;
                    faction.Progression.Science += objective.ScienceReward;
                    faction.Progression.FundsMicro += objective.FundsRewardMicro;
                    faction.Progression.Reputation += objective.ReputationReward;
                    faction.Progression.AchievementsCompleted++;
                    events.AddRange(Append(state, commandId, AetherfrontEventTypes.ObjectiveCompleted, actorAccountId, factionId, "", objective.Id, nowUtc));
                }
            }

            if (events.Count == 0) return Reject(state, commandId, "NoMatchingObjective");
            faction.Revision++;
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult UpsertObjective(AetherfrontState state, string commandId, string actorAccountId,
            string objectiveId, string seasonId, string title, string description, string eventType, long target,
            long scienceReward, long fundsRewardMicro, long reputationReward, bool publicProgress,
            DateTime? startsAtUtc, DateTime? endsAtUtc, int repeatPeriodDays, int catchUpWindowDays, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (!state.ModeratorAccountIds.Any(id => string.Equals(id, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ModeratorPermissionDenied");

            var id = (objectiveId ?? string.Empty).Trim();
            var validation = ValidateObjectiveDefinition(id, title, description, eventType, target, scienceReward,
                fundsRewardMicro, reputationReward, startsAtUtc, endsAtUtc, repeatPeriodDays, catchUpWindowDays);
            if (validation != null) return Reject(state, commandId, validation);

            var objective = state.Objectives.Find(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            var created = objective == null;
            if (created)
            {
                objective = new ObjectiveDefinition { Id = id };
                state.Objectives.Add(objective);
            }
            objective.SeasonId = (seasonId ?? string.Empty).Trim();
            objective.Title = title.Trim();
            objective.Description = (description ?? string.Empty).Trim();
            objective.EventType = eventType.Trim();
            objective.Target = target;
            objective.ScienceReward = scienceReward;
            objective.FundsRewardMicro = fundsRewardMicro;
            objective.ReputationReward = reputationReward;
            objective.PublicProgress = publicProgress;
            objective.StartsAtUtc = startsAtUtc;
            objective.EndsAtUtc = endsAtUtc;
            objective.RepeatPeriodDays = repeatPeriodDays;
            objective.CatchUpWindowDays = catchUpWindowDays;
            objective.Archived = false;
            objective.ConfigManaged = false;
            var events = Append(state, commandId, created ? AetherfrontEventTypes.ObjectiveDefined : AetherfrontEventTypes.ObjectiveUpdated,
                actorAccountId, "", "", objective.Id, nowUtc,
                Field("title", objective.Title), Field("eventType", objective.EventType), Field("target", objective.Target.ToString()));
            return Commit(state, commandId, events, "", "");
        }

        public static AetherfrontResult ArchiveObjective(AetherfrontState state, string commandId, string actorAccountId,
            string objectiveId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (!state.ModeratorAccountIds.Any(id => string.Equals(id, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ModeratorPermissionDenied");
            var objective = state.Objectives.Find(o => string.Equals(o.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
            if (objective == null) return Reject(state, commandId, "ObjectiveNotFound");
            if (objective.Archived) return Reject(state, commandId, "ObjectiveAlreadyArchived");
            objective.Archived = true;
            objective.ConfigManaged = false;
            var events = Append(state, commandId, AetherfrontEventTypes.ObjectiveArchived, actorAccountId, "", "", objective.Id, nowUtc);
            return Commit(state, commandId, events, "", "");
        }

        public static AetherfrontResult SetEventMode(AetherfrontState state, string commandId, string actorAccountId,
            string modeId, string name, string bodyName, int targetSubspaceId, bool allowAllCrafts,
            DateTime? startsAtUtc, DateTime? endsAtUtc, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (!state.ModeratorAccountIds.Any(id => string.Equals(id, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ModeratorPermissionDenied");
            var id = string.IsNullOrWhiteSpace(modeId) ? "live-event" : modeId.Trim();
            if (id.Length > 96 || !Regex.IsMatch(id, "^[A-Za-z0-9._-]+$")) return Reject(state, commandId, "InvalidEventModeId");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 96) return Reject(state, commandId, "InvalidEventModeName");
            if ((bodyName ?? string.Empty).Trim().Length > 96) return Reject(state, commandId, "InvalidEventModeBody");
            if (!allowAllCrafts) return Reject(state, commandId, "EventModeRequiresAllCrafts");
            var starts = startsAtUtc ?? nowUtc;
            var ends = endsAtUtc;
            if (ends != null && ends.Value <= starts) return Reject(state, commandId, "InvalidEventModeWindow");

            state.EventMode = new AetherfrontEventMode
            {
                Id = id,
                Name = name.Trim(),
                BodyName = (bodyName ?? string.Empty).Trim(),
                TargetSubspaceId = targetSubspaceId,
                AllowAllCrafts = true,
                Active = true,
                StartsAtUtc = starts,
                EndsAtUtc = ends,
                ModeratorAccountId = NormalizeAccountId(actorAccountId)
            };
            var events = Append(state, commandId, AetherfrontEventTypes.EventModeStarted, actorAccountId, "", "", id, nowUtc,
                Field("name", state.EventMode.Name), Field("bodyName", state.EventMode.BodyName),
                Field("subspaceId", targetSubspaceId.ToString()), Field("allowAllCrafts", "true"));
            return Commit(state, commandId, events, "", "");
        }

        public static AetherfrontResult ClearEventMode(AetherfrontState state, string commandId, string actorAccountId,
            DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            if (!state.ModeratorAccountIds.Any(id => string.Equals(id, actorAccountId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ModeratorPermissionDenied");
            if (state.EventMode == null || !state.EventMode.Active) return Reject(state, commandId, "EventModeNotActive");
            var modeId = state.EventMode.Id;
            state.EventMode.Active = false;
            state.EventMode.AllowAllCrafts = false;
            state.EventMode.EndsAtUtc = nowUtc;
            var events = Append(state, commandId, AetherfrontEventTypes.EventModeEnded, actorAccountId, "", "", modeId, nowUtc);
            return Commit(state, commandId, events, "", "");
        }

        public static AetherfrontResult PurchaseStoreItem(AetherfrontState state, string commandId, string actorAccountId,
            string storeItemId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFactionForAccount(actorAccountId);
            if (faction == null) return Reject(state, commandId, "FactionMembershipRequired");
            var item = (state.StoreItems ?? new List<StoreItem>()).Find(i => string.Equals(i.Id, storeItemId, StringComparison.OrdinalIgnoreCase));
            if (item == null || !item.Enabled) return Reject(state, commandId, "StoreItemUnavailable");
            var owned = faction.Progression.StoreItemIds ?? new List<string>();
            faction.Progression.StoreItemIds = owned;
            if (owned.Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "StoreItemAlreadyOwned");
            if (item.Stock == 0) return Reject(state, commandId, "StoreItemOutOfStock");
            if (item.PriceFundsMicro < 0 || item.PriceScience < 0) return Reject(state, commandId, "InvalidStoreItemPrice");
            if (faction.Progression.FundsMicro < item.PriceFundsMicro) return Reject(state, commandId, "InsufficientFactionFunds");
            if (faction.Progression.Science < item.PriceScience) return Reject(state, commandId, "InsufficientFactionScience");
            if (!string.IsNullOrWhiteSpace(item.GrantTechnologyId) && state.FindTechnology(item.GrantTechnologyId) == null)
                return Reject(state, commandId, "StoreTechnologyNotFound");

            faction.Progression.FundsMicro = checked(faction.Progression.FundsMicro - item.PriceFundsMicro);
            faction.Progression.Science = checked(faction.Progression.Science - item.PriceScience);
            owned.Add(item.Id);
            if (item.Stock > 0) item.Stock--;
            if (!string.IsNullOrWhiteSpace(item.GrantTechnologyId))
            {
                faction.Progression.TechnologyUnlocks = faction.Progression.TechnologyUnlocks ?? new List<string>();
                if (!faction.Progression.TechnologyUnlocks.Any(id => string.Equals(id, item.GrantTechnologyId, StringComparison.OrdinalIgnoreCase)))
                    faction.Progression.TechnologyUnlocks.Add(item.GrantTechnologyId);
            }
            var events = Append(state, commandId, AetherfrontEventTypes.StorePurchase, actorAccountId, faction.Id, "", item.Id, nowUtc,
                Field("fundsMicro", item.PriceFundsMicro.ToString()), Field("science", item.PriceScience.ToString()),
                Field("technologyId", item.GrantTechnologyId));
            return Commit(state, commandId, events, faction.Id, "");
        }

        private static string ValidateObjectiveDefinition(string objectiveId, string title, string description, string eventType,
            long target, long scienceReward, long fundsRewardMicro, long reputationReward, DateTime? startsAtUtc,
            DateTime? endsAtUtc, int repeatPeriodDays, int catchUpWindowDays)
        {
            if (string.IsNullOrWhiteSpace(objectiveId) || objectiveId.Length > 96 ||
                !Regex.IsMatch(objectiveId, "^[A-Za-z0-9._-]+$")) return "InvalidObjectiveId";
            if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > AetherfrontConstants.MaxStoryTitleLength) return "InvalidObjectiveTitle";
            if ((description ?? string.Empty).Length > AetherfrontConstants.MaxStoryBodyLength) return "ObjectiveDescriptionTooLong";
            if (string.IsNullOrWhiteSpace(eventType) || eventType.Trim().Length > 96) return "InvalidObjectiveEventType";
            if (target <= 0 || target > AetherfrontConstants.MaxResearchContribution) return "InvalidObjectiveTarget";
            if (scienceReward < 0 || scienceReward > AetherfrontConstants.MaxScienceTransfer) return "InvalidObjectiveScienceReward";
            if (fundsRewardMicro < 0 || fundsRewardMicro > AetherfrontConstants.MaxFundsMicroTransfer) return "InvalidObjectiveFundsReward";
            if (reputationReward < 0 || reputationReward > AetherfrontConstants.MaxScienceTransfer) return "InvalidObjectiveReputationReward";
            if (startsAtUtc != null && endsAtUtc != null && endsAtUtc.Value <= startsAtUtc.Value) return "InvalidObjectiveWindow";
            if (repeatPeriodDays < 0 || repeatPeriodDays > 3650) return "InvalidObjectiveRepeatPeriod";
            if (catchUpWindowDays < 0 || catchUpWindowDays > 365) return "InvalidObjectiveCatchUpWindow";
            return null;
        }

        private static string NormalizePeriodKey(string periodKey)
        {
            return string.IsNullOrWhiteSpace(periodKey) ? "permanent" : periodKey.Trim();
        }

        private static string GetObjectivePeriodKey(ObjectiveDefinition objective, DateTime nowUtc)
        {
            if (objective == null) return string.Empty;
            if (objective.StartsAtUtc != null && nowUtc < objective.StartsAtUtc.Value) return string.Empty;
            if (objective.EndsAtUtc != null && nowUtc > objective.EndsAtUtc.Value)
            {
                if (objective.CatchUpWindowDays <= 0 || nowUtc > objective.EndsAtUtc.Value.AddDays(objective.CatchUpWindowDays))
                    return string.Empty;
                return (objective.SeasonId ?? objective.Id) + ":catch-up";
            }
            if (objective.RepeatPeriodDays <= 0) return "permanent";
            var anchor = objective.StartsAtUtc ?? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var period = (long)Math.Floor((nowUtc - anchor).TotalDays / objective.RepeatPeriodDays);
            return (objective.SeasonId ?? objective.Id) + ":" + period;
        }

        private static string GetResearchProjectPeriodKey(ResearchProjectDefinition project, DateTime nowUtc)
        {
            if (project == null) return string.Empty;
            if (project.StartsAtUtc != null && nowUtc < project.StartsAtUtc.Value) return string.Empty;
            if (project.EndsAtUtc != null && nowUtc > project.EndsAtUtc.Value)
            {
                if (project.CatchUpWindowDays <= 0 || nowUtc > project.EndsAtUtc.Value.AddDays(project.CatchUpWindowDays))
                    return string.Empty;
                return (project.SeasonId ?? project.Id) + ":catch-up";
            }
            if (project.RepeatPeriodDays <= 0) return "permanent";
            var anchor = project.StartsAtUtc ?? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var period = (long)Math.Floor((nowUtc - anchor).TotalDays / project.RepeatPeriodDays);
            return (project.SeasonId ?? project.Id) + ":" + period;
        }

        public static AetherfrontResult RecordTrustedMilestone(AetherfrontState state, string commandId,
            string actorAccountId, string factionId, string eventType, string entityId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId))
                return Reject(state, commandId, "FactionMembershipRequired");
            if (string.IsNullOrWhiteSpace(entityId)) return Reject(state, commandId, "MilestoneEntityRequired");

            var key = entityId.Trim();
            if (key.Length > 128) return Reject(state, commandId, "MilestoneEntityTooLong");
            var progression = faction.Progression;
            switch (eventType ?? string.Empty)
            {
                case AetherfrontEventTypes.VesselLaunched:
                    if (progression.LaunchedVesselIds.Any(id => string.Equals(id, key, StringComparison.OrdinalIgnoreCase)))
                        return Reject(state, commandId, "MilestoneAlreadyRecorded");
                    progression.LaunchedVesselIds.Add(key);
                    progression.SuccessfulLaunches = checked(progression.SuccessfulLaunches + 1);
                    break;
                case AetherfrontEventTypes.VesselRecovered:
                    if (progression.RecoveredVesselIds.Any(id => string.Equals(id, key, StringComparison.OrdinalIgnoreCase)))
                        return Reject(state, commandId, "MilestoneAlreadyRecorded");
                    progression.RecoveredVesselIds.Add(key);
                    break;
                case AetherfrontEventTypes.BodyReached:
                    if (progression.ReachedBodyKeys.Any(id => string.Equals(id, key, StringComparison.OrdinalIgnoreCase)))
                        return Reject(state, commandId, "MilestoneAlreadyRecorded");
                    progression.ReachedBodyKeys.Add(key);
                    progression.BodiesReached = checked(progression.BodiesReached + 1);
                    break;
                case AetherfrontEventTypes.RescueCompleted:
                    if (progression.RescueOperationIds.Any(id => string.Equals(id, key, StringComparison.OrdinalIgnoreCase)))
                        return Reject(state, commandId, "MilestoneAlreadyRecorded");
                    progression.RescueOperationIds.Add(key);
                    progression.RescueOperations = checked(progression.RescueOperations + 1);
                    break;
                default:
                    return Reject(state, commandId, "UnknownTrustedMilestone");
            }

            faction.Revision++;
            var events = Append(state, commandId, eventType, actorAccountId, factionId, "", key, nowUtc);
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult AwardScience(AetherfrontState state, string commandId, string actorAccountId,
            string factionId, long amount, string reason, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (amount <= 0 || amount > AetherfrontConstants.MaxScienceTransfer) return Reject(state, commandId, "InvalidScienceAmount");

            faction.Progression.Science = checked(faction.Progression.Science + amount);
            faction.Progression.TotalScienceEarned = checked(faction.Progression.TotalScienceEarned + amount);
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.ScienceEarned, actorAccountId, factionId, "", "", nowUtc,
                Field("amount", amount.ToString()), Field("reason", reason ?? string.Empty));
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult AwardFunds(AetherfrontState state, string commandId, string actorAccountId,
            string factionId, long amountMicro, string reason, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (amountMicro <= 0 || amountMicro > AetherfrontConstants.MaxFundsMicroTransfer) return Reject(state, commandId, "InvalidFundsAmount");

            faction.Progression.FundsMicro = checked(faction.Progression.FundsMicro + amountMicro);
            faction.Progression.TotalFundsEarnedMicro = checked(faction.Progression.TotalFundsEarnedMicro + amountMicro);
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.FundsEarned, actorAccountId, factionId, "", "", nowUtc,
                Field("amountMicro", amountMicro.ToString()), Field("reason", reason ?? string.Empty));
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult AwardReputation(AetherfrontState state, string commandId, string actorAccountId,
            string factionId, long amount, string reason, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (amount <= 0 || amount > AetherfrontConstants.MaxScienceTransfer) return Reject(state, commandId, "InvalidReputationAmount");

            faction.Progression.Reputation = checked(faction.Progression.Reputation + amount);
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.ReputationEarned, actorAccountId, factionId, "", "", nowUtc,
                Field("amount", amount.ToString()), Field("reason", reason ?? string.Empty));
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult RecordMissionCompleted(AetherfrontState state, string commandId,
            string actorAccountId, string factionId, string contractId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (string.IsNullOrWhiteSpace(contractId)) return Reject(state, commandId, "ContractIdRequired");
            if (faction.Progression.CompletedContractIds.Any(id => string.Equals(id, contractId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "ContractAlreadyRecorded");

            faction.Progression.ContractsCompleted = checked(faction.Progression.ContractsCompleted + 1);
            faction.Progression.CompletedContractIds.Add(contractId);
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.MissionCompleted, actorAccountId, factionId, "", contractId, nowUtc);
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult RecordAchievementCompleted(AetherfrontState state, string commandId,
            string actorAccountId, string factionId, string achievementId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (string.IsNullOrWhiteSpace(achievementId)) return Reject(state, commandId, "AchievementIdRequired");
            if (faction.Progression.AchievementIds.Any(id => string.Equals(id, achievementId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "AchievementAlreadyRecorded");

            faction.Progression.AchievementIds.Add(achievementId);
            faction.Progression.AchievementsCompleted = checked(faction.Progression.AchievementsCompleted + 1);
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.AchievementCompleted, actorAccountId, factionId, "", achievementId, nowUtc);
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult UnlockTechnology(AetherfrontState state, string commandId, string actorAccountId,
            string factionId, string technologyId, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;
            var faction = state.FindFaction(factionId);
            if (faction == null || !IsActiveMember(faction, actorAccountId)) return Reject(state, commandId, "FactionMembershipRequired");
            if (string.IsNullOrWhiteSpace(technologyId) || technologyId.Length > 96) return Reject(state, commandId, "InvalidTechnologyId");
            var normalizedTechnologyId = technologyId.Trim();
            var definition = state.FindTechnology(normalizedTechnologyId);
            if (state.TechnologyDefinitions.Count > 0 && definition == null)
                return Reject(state, commandId, "UnknownTechnology");
            if (faction.Progression.TechnologyUnlocks.Any(t => string.Equals(t, normalizedTechnologyId, StringComparison.OrdinalIgnoreCase)))
                return Reject(state, commandId, "TechnologyAlreadyUnlocked");
            if (definition != null && definition.PrerequisiteIds.Any(prerequisite =>
                !faction.Progression.TechnologyUnlocks.Any(unlocked => string.Equals(unlocked, prerequisite, StringComparison.OrdinalIgnoreCase))))
                return Reject(state, commandId, "TechnologyPrerequisiteMissing");
            var cost = definition?.ScienceCost ?? AetherfrontConstants.TechnologyUnlockCost;
            if (faction.Progression.Science < cost)
                return Reject(state, commandId, "InsufficientFactionScience");

            faction.Progression.Science -= cost;
            faction.Progression.TechnologyUnlocks.Add(normalizedTechnologyId);
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.TechnologyUnlocked, actorAccountId, factionId, "", normalizedTechnologyId, nowUtc,
                Field("cost", cost.ToString()));
            return Commit(state, commandId, events, factionId, "");
        }

        public static AetherfrontResult ContributeResearch(AetherfrontState state, string commandId,
            string actorAccountId, string factionId, string projectId, long amountScience, DateTime nowUtc)
        {
            var duplicate = ExistingCommand(state, commandId);
            if (duplicate != null) return FromReceipt(state, duplicate);
            var invalid = ValidateCommand(state, commandId, actorAccountId);
            if (invalid != null) return invalid;

            var faction = state.FindFaction(factionId);
            if (faction == null || faction.State != FactionStates.Active || !CanContributeResearch(faction, actorAccountId))
                return Reject(state, commandId, "ResearchPermissionDenied");
            if (string.IsNullOrWhiteSpace(projectId)) return Reject(state, commandId, "ResearchProjectRequired");
            if (amountScience <= 0 || amountScience > AetherfrontConstants.MaxResearchContribution)
                return Reject(state, commandId, "InvalidResearchContribution");
            if (faction.Progression.Science < amountScience)
                return Reject(state, commandId, "InsufficientFactionScience");

            var project = state.ResearchProjects.Find(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            var periodKey = GetResearchProjectPeriodKey(project, nowUtc);
            if (project == null || project.TargetScience <= 0 || string.IsNullOrWhiteSpace(periodKey))
                return Reject(state, commandId, "ResearchProjectUnavailable");

            var progress = state.ResearchProgress.Find(p => string.Equals(p.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.FactionId, faction.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizePeriodKey(p.PeriodKey), periodKey, StringComparison.OrdinalIgnoreCase));
            if (progress == null)
            {
                progress = new ResearchProjectProgress { ProjectId = project.Id, FactionId = faction.Id, PeriodKey = periodKey };
                state.ResearchProgress.Add(progress);
            }
            if (progress.Completed) return Reject(state, commandId, "ResearchProjectCompleted");
            var remaining = project.TargetScience - progress.ContributedScience;
            if (remaining <= 0) return Reject(state, commandId, "ResearchProjectCompleted");
            if (amountScience > remaining) return Reject(state, commandId, "ResearchContributionExceedsRemaining");

            faction.Progression.Science -= amountScience;
            progress.ContributedScience += amountScience;
            faction.Revision++;
            var events = Append(state, commandId, AetherfrontEventTypes.ResearchContributed, actorAccountId, faction.Id, "", project.Id, nowUtc,
                Field("amountScience", amountScience.ToString()), Field("progress", progress.ContributedScience.ToString()),
                Field("periodKey", periodKey));
            if (progress.ContributedScience >= project.TargetScience)
            {
                progress.Completed = true;
                progress.CompletedAtUtc = nowUtc;
                faction.Progression.ResearchProjectsCompleted = checked(faction.Progression.ResearchProjectsCompleted + 1);
                faction.Progression.ResearchProjectIds.Add(project.Id + "@" + periodKey);
                if (!string.IsNullOrWhiteSpace(project.RewardTechnologyId) &&
                    !faction.Progression.TechnologyUnlocks.Any(id => string.Equals(id, project.RewardTechnologyId, StringComparison.OrdinalIgnoreCase)))
                    faction.Progression.TechnologyUnlocks.Add(project.RewardTechnologyId.Trim());
                if (project.FundsRewardMicro > 0)
                {
                    faction.Progression.FundsMicro = checked(faction.Progression.FundsMicro + project.FundsRewardMicro);
                    faction.Progression.TotalFundsEarnedMicro = checked(faction.Progression.TotalFundsEarnedMicro + project.FundsRewardMicro);
                }
                if (project.ReputationReward > 0)
                    faction.Progression.Reputation = checked(faction.Progression.Reputation + project.ReputationReward);
                events.AddRange(Append(state, commandId, AetherfrontEventTypes.ResearchCompleted, actorAccountId, faction.Id, "", project.Id, nowUtc,
                    Field("periodKey", periodKey), Field("technologyId", project.RewardTechnologyId ?? string.Empty),
                    Field("fundsRewardMicro", project.FundsRewardMicro.ToString()), Field("reputationReward", project.ReputationReward.ToString())));
            }
            return Commit(state, commandId, events, faction.Id, "");
        }

        private static string ValidateTradeAssets(AetherfrontState state, string factionId, List<TradeAsset> assets)
        {
            var funds = assets.Where(a => a.Type == TradeAssetTypes.FundsMicro).Sum(a => a.Amount);
            var science = assets.Where(a => a.Type == TradeAssetTypes.Science).Sum(a => a.Amount);
            var faction = state.FindFaction(factionId);
            if (faction == null) return "FactionNotFound";
            if (funds < 0 || funds > AetherfrontConstants.MaxFundsMicroTransfer || faction.Progression.FundsMicro < funds) return "InsufficientFunds";
            if (science < 0 || science > AetherfrontConstants.MaxScienceTransfer || faction.Progression.Science < science) return "InsufficientScience";
            foreach (var asset in assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Type)) return "InvalidTradeAsset";
                if ((asset.Type == TradeAssetTypes.FundsMicro || asset.Type == TradeAssetTypes.Science) && asset.Amount <= 0) return "InvalidTradeAmount";
                if (asset.Type == TradeAssetTypes.Craft)
                {
                    var craft = state.FindCraft(asset.EntityId);
                    if (craft == null || craft.Archived || !string.Equals(craft.OwnerFactionId, factionId, StringComparison.OrdinalIgnoreCase)) return "CraftNotOwned";
                    if (!string.IsNullOrWhiteSpace(asset.ContentSha256) && !string.Equals(asset.ContentSha256, craft.ContentSha256, StringComparison.OrdinalIgnoreCase)) return "CraftHashMismatch";
                }
                else if (asset.Type != TradeAssetTypes.FundsMicro && asset.Type != TradeAssetTypes.Science)
                    return "UnknownTradeAsset";
            }
            return null;
        }

        private static long AccrueLoan(LoanRecord loan, DateTime nowUtc)
        {
            if (loan == null || loan.OutstandingMicro <= 0 || nowUtc <= loan.LastAccrualAtUtc)
                return 0;
            var elapsedDays = (nowUtc - loan.LastAccrualAtUtc).TotalDays;
            if (elapsedDays <= 0) return 0;
            var interest = (long)Math.Floor(loan.OutstandingMicro * (loan.AprBasisPoints / 10000d) * (elapsedDays / 365d));
            loan.OutstandingMicro = checked(loan.OutstandingMicro + Math.Max(0, interest));
            loan.LastAccrualAtUtc = nowUtc;
            return Math.Max(0, interest);
        }

        private static string SettleTrade(AetherfrontState state, TradeOffer trade, DateTime nowUtc)
        {
            var proposer = state.FindFaction(trade.ProposerFactionId);
            var recipient = state.FindFaction(trade.RecipientFactionId);
            var proposerError = ValidateTradeAssets(state, proposer.Id, trade.ProposerAssets);
            if (proposerError != null) return "Proposer" + proposerError;
            var recipientError = ValidateTradeAssets(state, recipient.Id, trade.RecipientAssets);
            if (recipientError != null) return "Recipient" + recipientError;

            var proposerFunds = trade.ProposerAssets.Where(a => a.Type == TradeAssetTypes.FundsMicro).Sum(a => a.Amount);
            var recipientFunds = trade.RecipientAssets.Where(a => a.Type == TradeAssetTypes.FundsMicro).Sum(a => a.Amount);
            var proposerScience = trade.ProposerAssets.Where(a => a.Type == TradeAssetTypes.Science).Sum(a => a.Amount);
            var recipientScience = trade.RecipientAssets.Where(a => a.Type == TradeAssetTypes.Science).Sum(a => a.Amount);
            proposer.Progression.FundsMicro = checked(proposer.Progression.FundsMicro - proposerFunds + recipientFunds);
            recipient.Progression.FundsMicro = checked(recipient.Progression.FundsMicro - recipientFunds + proposerFunds);
            proposer.Progression.Science = checked(proposer.Progression.Science - proposerScience + recipientScience);
            recipient.Progression.Science = checked(recipient.Progression.Science - recipientScience + proposerScience);

            foreach (var asset in trade.ProposerAssets.Where(a => a.Type == TradeAssetTypes.Craft))
            {
                var craft = state.FindCraft(asset.EntityId);
                TransferCraftOwnership(craft, recipient);
                craft.Revision++;
            }
            foreach (var asset in trade.RecipientAssets.Where(a => a.Type == TradeAssetTypes.Craft))
            {
                var craft = state.FindCraft(asset.EntityId);
                TransferCraftOwnership(craft, proposer);
                craft.Revision++;
            }
            proposer.Revision++;
            recipient.Revision++;
            return null;
        }

        private static void TransferCraftOwnership(CraftRecord craft, FactionRecord destination)
        {
            if (craft == null || destination == null) return;
            craft.OwnerFactionId = destination.Id;
            craft.OwnerAccountId = destination.Members
                .Where(m => m.Active && (m.Role == FactionRoles.Founder || m.Role == FactionRoles.Leader))
                .Select(m => m.AccountId)
                .FirstOrDefault() ?? destination.Members.Where(m => m.Active).Select(m => m.AccountId).FirstOrDefault() ?? string.Empty;
        }

        private static AetherfrontResult ValidateCommand(AetherfrontState state, string commandId, string actorAccountId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrWhiteSpace(commandId)) return AetherfrontResult.Reject(state.Revision, "CommandIdRequired");
            if (string.IsNullOrWhiteSpace(actorAccountId)) return AetherfrontResult.Reject(state.Revision, "AccountIdRequired");
            return null;
        }

        private static AetherfrontResult Reject(AetherfrontState state, string commandId, string reason)
        {
            var result = AetherfrontResult.Reject(state.Revision, reason);
            state.CommandReceipts.Add(new CommandReceipt { CommandId = commandId, Accepted = false, ReasonCode = reason, Revision = state.Revision });
            return result;
        }

        private static AetherfrontResult Commit(AetherfrontState state, string commandId, List<AetherfrontEvent> events, string factionId, string tradeId)
        {
            var receipt = new CommandReceipt
            {
                CommandId = commandId,
                Accepted = true,
                ReasonCode = "Accepted",
                Revision = state.Revision,
                EventIds = events.Select(e => e.Id).ToList()
            };
            state.CommandReceipts.Add(receipt);
            return AetherfrontResult.Success(state.Revision, factionId, tradeId, events);
        }

        private static CommandReceipt ExistingCommand(AetherfrontState state, string commandId)
        {
            return string.IsNullOrWhiteSpace(commandId) ? null : state.CommandReceipts.Find(r => r.CommandId == commandId);
        }

        private static AetherfrontResult FromReceipt(AetherfrontState state, CommandReceipt receipt)
        {
            var events = state.Events.Where(e => receipt.EventIds.Contains(e.Id)).ToList();
            return receipt.Accepted
                ? AetherfrontResult.Success(receipt.Revision, events.FirstOrDefault()?.FactionId ?? "", "", events)
                : AetherfrontResult.Reject(receipt.Revision, receipt.ReasonCode);
        }

        private static List<AetherfrontEvent> Append(AetherfrontState state, string commandId, string type, string actorAccountId, string factionId, string secondaryFactionId, string entityId, DateTime nowUtc, params EventField[] fields)
        {
            state.Revision++;
            var item = new AetherfrontEvent
            {
                Id = NewId(),
                Revision = state.Revision,
                Type = type,
                ActorAccountId = actorAccountId ?? string.Empty,
                FactionId = factionId ?? string.Empty,
                SecondaryFactionId = secondaryFactionId ?? string.Empty,
                EntityId = entityId ?? string.Empty,
                OccurredAtUtc = nowUtc,
                Fields = fields == null ? new List<EventField>() : fields.ToList()
            };
            state.Events.Add(item);
            return new List<AetherfrontEvent> { item };
        }

        private static EventField Field(string key, string value)
        {
            return new EventField { Key = key, Value = value ?? string.Empty };
        }

        private static bool IsActiveMember(FactionRecord faction, string accountId)
        {
            return faction != null && faction.Members.Any(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool CanManageFaction(FactionRecord faction, string accountId)
        {
            return faction != null && faction.Members.Any(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                (m.Role == FactionRoles.Founder || m.Role == FactionRoles.Leader || m.Role == FactionRoles.Officer));
        }

        private static bool IsRecruitmentPolicy(string policy)
        {
            return string.Equals(policy, RecruitmentPolicies.Open, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(policy, RecruitmentPolicies.Application, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(policy, RecruitmentPolicies.InviteOnly, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(policy, RecruitmentPolicies.Closed, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanManageEconomy(FactionRecord faction, string accountId)
        {
            return faction != null && faction.Members.Any(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                (m.Role == FactionRoles.Founder || m.Role == FactionRoles.Leader || m.Role == FactionRoles.Officer || m.Role == FactionRoles.Diplomat));
        }

        private static bool CanContributeResearch(FactionRecord faction, string accountId)
        {
            return faction != null && faction.Members.Any(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                (m.Role == FactionRoles.Founder || m.Role == FactionRoles.Leader || m.Role == FactionRoles.Officer ||
                 m.Role == FactionRoles.Scientist));
        }

        private static bool CanManageCraft(FactionRecord faction, string accountId)
        {
            return faction != null && faction.Members.Any(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                (m.Role == FactionRoles.Founder || m.Role == FactionRoles.Leader || m.Role == FactionRoles.Officer || m.Role == FactionRoles.Engineer));
        }

        private static bool CanManageDiplomacy(FactionRecord faction, string accountId)
        {
            return faction != null && faction.Members.Any(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                (m.Role == FactionRoles.Founder || m.Role == FactionRoles.Leader || m.Role == FactionRoles.Officer || m.Role == FactionRoles.Diplomat));
        }

        private static bool IsAssignableRole(string role)
        {
            return role == FactionRoles.Member || role == FactionRoles.Leader || role == FactionRoles.Officer ||
                role == FactionRoles.Engineer || role == FactionRoles.Scientist || role == FactionRoles.Pilot ||
                role == FactionRoles.Diplomat || role == FactionRoles.Historian;
        }

        private static TradeAsset CloneAsset(TradeAsset asset)
        {
            return asset == null ? new TradeAsset() : new TradeAsset
            {
                Type = asset.Type ?? string.Empty,
                Amount = asset.Amount,
                EntityId = asset.EntityId ?? string.Empty,
                ContentSha256 = asset.ContentSha256 ?? string.Empty
            };
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
