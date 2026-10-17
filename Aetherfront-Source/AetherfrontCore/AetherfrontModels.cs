using System;
using System.Collections.Generic;

namespace Aetherfront.Core
{
    public static class AetherfrontConstants
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaxFactionNameLength = 40;
        public const int MaxFactionAbbreviationLength = 8;
        public const int MaxFactionDescriptionLength = 1000;
        public const int MaxStoryTitleLength = 120;
        public const int MaxStoryBodyLength = 4000;
        public const int MaxMembersPerFaction = 64;
        public const long MaxScienceTransfer = 1000000000;
        public const long MaxFundsMicroTransfer = 1000000000000;
        public const long MaxResearchContribution = 1000000000;
        public const long TechnologyUnlockCost = 100;
        public const long FundsMicroPerKspFund = 1000000;
    }

    public static class FactionRoles
    {
        public const string Member = "Member";
        public const string Founder = "Founder";
        public const string Leader = "Leader";
        public const string Officer = "Officer";
        public const string Engineer = "Engineer";
        public const string Scientist = "Scientist";
        public const string Pilot = "Pilot";
        public const string Diplomat = "Diplomat";
        public const string Historian = "Historian";
    }

    public static class FactionStates
    {
        public const string Active = "Active";
        public const string Suspended = "Suspended";
        public const string Archived = "Archived";
    }

    public static class TradeStates
    {
        public const string Proposed = "Proposed";
        public const string PartiallyConfirmed = "PartiallyConfirmed";
        public const string Committed = "Committed";
        public const string Cancelled = "Cancelled";
        public const string Expired = "Expired";
    }

    public static class LoanStates
    {
        public const string Pending = "Pending";
        public const string Active = "Active";
        public const string Repaid = "Repaid";
        public const string Defaulted = "Defaulted";
        public const string Rejected = "Rejected";
    }

    public static class AgreementStates
    {
        public const string Proposed = "Proposed";
        public const string Active = "Active";
        public const string Declined = "Declined";
        public const string Revoked = "Revoked";
        public const string Expired = "Expired";
    }

    public static class RecruitmentPolicies
    {
        public const string Open = "Open";
        public const string Application = "Application";
        public const string InviteOnly = "InviteOnly";
        public const string Closed = "Closed";
    }

    public static class FactionApplicationStates
    {
        public const string Pending = "Pending";
        public const string Accepted = "Accepted";
        public const string Rejected = "Rejected";
        public const string Cancelled = "Cancelled";
    }

    public static class StoryModerationStates
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
    }

    public static class TradeAssetTypes
    {
        public const string FundsMicro = "FundsMicro";
        public const string Science = "Science";
        public const string Craft = "Craft";
    }

    public static class AgreementTypes
    {
        public const string Alliance = "Alliance";
        public const string CraftLicense = "CraftLicense";
        public const string Loan = "Loan";
    }

    public static class CraftAccess
    {
        public const string ViewMetadata = "ViewMetadata";
        public const string ViewFull = "ViewFull";
        public const string Clone = "Clone";
        public const string Use = "Use";
        public const string Modify = "Modify";
        public const string Launch = "Launch";
        public const string Recover = "Recover";
        public const string Transfer = "Transfer";
    }
    public sealed class AetherfrontState
    {
        public int SchemaVersion { get; set; } = AetherfrontConstants.CurrentSchemaVersion;
        public string ServerId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public List<FactionRecord> Factions { get; set; } = new List<FactionRecord>();
        public List<CraftRecord> Crafts { get; set; } = new List<CraftRecord>();
        public List<TradeOffer> Trades { get; set; } = new List<TradeOffer>();
        public List<ObjectiveDefinition> Objectives { get; set; } = new List<ObjectiveDefinition>();
        public List<ObjectiveProgress> ObjectiveProgress { get; set; } = new List<ObjectiveProgress>();
        public List<AetherfrontEvent> Events { get; set; } = new List<AetherfrontEvent>();
        public List<CommandReceipt> CommandReceipts { get; set; } = new List<CommandReceipt>();
        public List<FactionInvitation> Invitations { get; set; } = new List<FactionInvitation>();
        public List<DiplomaticAgreement> Agreements { get; set; } = new List<DiplomaticAgreement>();
        public List<StoryPublication> Stories { get; set; } = new List<StoryPublication>();
        public List<FactionApplication> Applications { get; set; } = new List<FactionApplication>();
        public List<string> ModeratorAccountIds { get; set; } = new List<string>();
        public CentralBankPolicy BankPolicy { get; set; } = new CentralBankPolicy();
        public List<LoanRecord> Loans { get; set; } = new List<LoanRecord>();
        public List<ResearchProjectDefinition> ResearchProjects { get; set; } = new List<ResearchProjectDefinition>();
        public List<ResearchProjectProgress> ResearchProgress { get; set; } = new List<ResearchProjectProgress>();
        public List<TechnologyDefinition> TechnologyDefinitions { get; set; } = new List<TechnologyDefinition>();
        public List<AetherfrontAccountRecord> Accounts { get; set; } = new List<AetherfrontAccountRecord>();
        public AetherfrontEventMode EventMode { get; set; } = new AetherfrontEventMode();
        public List<StoreItem> StoreItems { get; set; } = new List<StoreItem>();

        public FactionRecord FindFaction(string factionId)
        {
            return Factions.Find(f => string.Equals(f.Id, factionId, StringComparison.OrdinalIgnoreCase));
        }

        public FactionRecord FindFactionForAccount(string accountId)
        {
            return Factions.Find(f => f.Members.Exists(m => m.Active && string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) && f.State == FactionStates.Active);
        }

        public CraftRecord FindCraft(string craftId)
        {
            return Crafts.Find(c => string.Equals(c.Id, craftId, StringComparison.OrdinalIgnoreCase));
        }

        public TechnologyDefinition FindTechnology(string technologyId)
        {
            return TechnologyDefinitions.Find(t => string.Equals(t.Id, technologyId, StringComparison.OrdinalIgnoreCase));
        }

        public AetherfrontAccountRecord FindAccount(string accountId)
        {
            return Accounts.Find(a => string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
        }

        public TradeOffer FindTrade(string tradeId)
        {
            return Trades.Find(t => string.Equals(t.Id, tradeId, StringComparison.OrdinalIgnoreCase));
        }

        public FactionInvitation FindInvitation(string invitationId)
        {
            return Invitations.Find(i => string.Equals(i.Id, invitationId, StringComparison.OrdinalIgnoreCase));
        }

        public DiplomaticAgreement FindAgreement(string agreementId)
        {
            return Agreements.Find(a => string.Equals(a.Id, agreementId, StringComparison.OrdinalIgnoreCase));
        }

        public FactionApplication FindApplication(string applicationId)
        {
            return Applications.Find(a => string.Equals(a.Id, applicationId, StringComparison.OrdinalIgnoreCase));
        }
    }
    public sealed class AetherfrontEventMode
    {
        public string Id { get; set; } = "live-event";
        public string Name { get; set; } = string.Empty;
        public string BodyName { get; set; } = string.Empty;
        public int TargetSubspaceId { get; set; } = int.MinValue;
        public bool AllowAllCrafts { get; set; }
        public bool Active { get; set; }
        public DateTime? StartsAtUtc { get; set; }
        public DateTime? EndsAtUtc { get; set; }
        public string ModeratorAccountId { get; set; } = string.Empty;
    }
    public sealed class StoreItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long PriceFundsMicro { get; set; }
        public long PriceScience { get; set; }
        public long Stock { get; set; } = -1;
        public string GrantTechnologyId { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool Public { get; set; } = true;
        public bool ConfigManaged { get; set; } = true;
    }
    public sealed class AetherfrontAccountRecord
    {
        public string AccountId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
        public bool Suspended { get; set; }
    }
    public sealed class FactionRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Abbreviation { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#FFFFFF";
        public string FlagAssetId { get; set; } = string.Empty;
        public string FlagSha256 { get; set; } = string.Empty;
        public bool PublicProfile { get; set; } = true;
        public string RecruitmentPolicy { get; set; } = "Application";
        public string State { get; set; } = FactionStates.Active;
        public string FounderAccountId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public long Revision { get; set; }
        public List<FactionMember> Members { get; set; } = new List<FactionMember>();
        public FactionProgression Progression { get; set; } = new FactionProgression();
    }
    public sealed class CentralBankPolicy
    {
        public bool Enabled { get; set; } = true;
        public long BalanceMicro { get; set; } = 1000000000000000;
        public int BaseAprBasisPoints { get; set; } = 800;
        public int MaxAprBasisPoints { get; set; } = 3000;
        public long MaxLoanMicro { get; set; } = 100000000000;
        public int MaxTermDays { get; set; } = 180;
        public int MaxOpenLoansPerFaction { get; set; } = 3;
    }
    public sealed class LoanRecord
    {
        public string Id { get; set; } = string.Empty;
        public string BorrowerFactionId { get; set; } = string.Empty;
        public string LenderFactionId { get; set; } = string.Empty;
        public long PrincipalMicro { get; set; }
        public long OutstandingMicro { get; set; }
        public int AprBasisPoints { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime DueAtUtc { get; set; } = DateTime.UtcNow.AddDays(30);
        public DateTime LastAccrualAtUtc { get; set; } = DateTime.UtcNow;
        public string State { get; set; } = LoanStates.Pending;
    }
    public sealed class ResearchProjectDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string SeasonId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long TargetScience { get; set; } = 100;
        public string RewardTechnologyId { get; set; } = string.Empty;
        public long FundsRewardMicro { get; set; }
        public long ReputationReward { get; set; }
        public bool PublicProgress { get; set; } = true;
        public DateTime? StartsAtUtc { get; set; }
        public DateTime? EndsAtUtc { get; set; }
        public int RepeatPeriodDays { get; set; }
        public int CatchUpWindowDays { get; set; }
    }
    public sealed class ResearchProjectProgress
    {
        public string ProjectId { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public long ContributedScience { get; set; }
        public bool Completed { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string PeriodKey { get; set; } = "permanent";
    }
    public sealed class TechnologyDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public long ScienceCost { get; set; } = AetherfrontConstants.TechnologyUnlockCost;
        public List<string> PrerequisiteIds { get; set; } = new List<string>();
        public bool Starter { get; set; }
    }
    public sealed class FactionMember
    {
        public string AccountId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Role { get; set; } = FactionRoles.Member;
        public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
        public bool Active { get; set; } = true;
    }
    public sealed class FactionProgression
    {
        public long Science { get; set; }
        public long FundsMicro { get; set; }
        public long Reputation { get; set; }
        public List<string> TechnologyUnlocks { get; set; } = new List<string>();
        public long TotalScienceEarned { get; set; }
        public long TotalFundsEarnedMicro { get; set; }
        public long ContractsCompleted { get; set; }
        public long AchievementsCompleted { get; set; }
        public List<string> CompletedContractIds { get; set; } = new List<string>();
        public List<string> AchievementIds { get; set; } = new List<string>();
        public long SuccessfulLaunches { get; set; }
        public long BodiesReached { get; set; }
        public long RescueOperations { get; set; }
        public List<string> LaunchedVesselIds { get; set; } = new List<string>();
        public List<string> ReachedBodyKeys { get; set; } = new List<string>();
        public List<string> RescueOperationIds { get; set; } = new List<string>();
        public List<string> RecoveredVesselIds { get; set; } = new List<string>();
        public long ResearchProjectsCompleted { get; set; }
        public List<string> ResearchProjectIds { get; set; } = new List<string>();
        public List<string> StoreItemIds { get; set; } = new List<string>();
    }
    public sealed class CraftRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string OwnerFactionId { get; set; } = string.Empty;
        public string CreatorAccountId { get; set; } = string.Empty;
        public string ContentSha256 { get; set; } = string.Empty;
        public long Revision { get; set; }
        public bool Archived { get; set; }
        public List<CraftAccessGrant> AccessGrants { get; set; } = new List<CraftAccessGrant>();
        public string OwnerAccountId { get; set; } = string.Empty;
    }
    public sealed class CraftAccessGrant
    {
        public string GranteeFactionId { get; set; } = string.Empty;
        public string GranteeAccountId { get; set; } = string.Empty;
        public List<string> Permissions { get; set; } = new List<string>();
        public DateTime? ExpiresAtUtc { get; set; }
        public string AgreementId { get; set; } = string.Empty;
    }
    public sealed class TradeOffer
    {
        public string Id { get; set; } = string.Empty;
        public string ProposerFactionId { get; set; } = string.Empty;
        public string RecipientFactionId { get; set; } = string.Empty;
        public string State { get; set; } = TradeStates.Proposed;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
        public bool ProposerConfirmed { get; set; }
        public bool RecipientConfirmed { get; set; }
        public List<TradeAsset> ProposerAssets { get; set; } = new List<TradeAsset>();
        public List<TradeAsset> RecipientAssets { get; set; } = new List<TradeAsset>();
        public string CommitCommandId { get; set; } = string.Empty;
    }
    public sealed class TradeAsset
    {
        public string Type { get; set; } = string.Empty;
        public long Amount { get; set; }
        public string EntityId { get; set; } = string.Empty;
        public string ContentSha256 { get; set; } = string.Empty;
    }
    public sealed class FactionInvitation
    {
        public string Id { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string InvitedByAccountId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(14);
        public bool Accepted { get; set; }
        public bool Cancelled { get; set; }
    }
    public sealed class FactionApplication
    {
        public string Id { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string State { get; set; } = FactionApplicationStates.Pending;
        public string ReviewedByAccountId { get; set; } = string.Empty;
    }
    public sealed class DiplomaticAgreement
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = AgreementTypes.Alliance;
        public string ProposerFactionId { get; set; } = string.Empty;
        public string RecipientFactionId { get; set; } = string.Empty;
        public string State { get; set; } = TradeStates.Proposed;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAtUtc { get; set; }
        public string TermsJson { get; set; } = string.Empty;
    }
    public sealed class StoryPublication
    {
        public string Id { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string AuthorAccountId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
        public bool Public { get; set; } = true;
        public bool RequestedPublic { get; set; }
        public string ModerationState { get; set; } = StoryModerationStates.Pending;
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> RelatedEventIds { get; set; } = new List<string>();
        public string ApprovedByAccountId { get; set; } = string.Empty;
        public DateTime? ReviewedAtUtc { get; set; }
    }
    public sealed class ObjectiveDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string SeasonId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public long Target { get; set; } = 1;
        public long ScienceReward { get; set; }
        public long FundsRewardMicro { get; set; }
        public long ReputationReward { get; set; }
        public bool PublicProgress { get; set; } = true;
        public DateTime? StartsAtUtc { get; set; }
        public DateTime? EndsAtUtc { get; set; }
        public int RepeatPeriodDays { get; set; }
        public int CatchUpWindowDays { get; set; }
        public bool Archived { get; set; }
        public bool ConfigManaged { get; set; } = true;
    }
    public sealed class ObjectiveProgress
    {
        public string ObjectiveId { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public long Progress { get; set; }
        public bool Completed { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string PeriodKey { get; set; } = "permanent";
    }
    public sealed class AetherfrontEvent
    {
        public string Id { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string Type { get; set; } = string.Empty;
        public string ActorAccountId { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string SecondaryFactionId { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
        public List<EventField> Fields { get; set; } = new List<EventField>();
        public bool Public { get; set; } = true;
    }
    public sealed class EventField
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
    public sealed class CommandReceipt
    {
        public string CommandId { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public long Revision { get; set; }
        public List<string> EventIds { get; set; } = new List<string>();
    }

    public static class AetherfrontEventTypes
    {
        public const string FactionCreated = "FactionCreated";
        public const string FactionFounded = "FactionFounded";
        public const string FactionJoined = "FactionJoined";
        public const string FactionLeft = "FactionLeft";
        public const string FactionProfileChanged = "FactionProfileChanged";
        public const string FactionInvitationCreated = "FactionInvitationCreated";
        public const string FactionMemberRoleChanged = "FactionMemberRoleChanged";
        public const string FactionMemberRemoved = "FactionMemberRemoved";
        public const string FactionLeadershipTransferred = "FactionLeadershipTransferred";
        public const string ScienceTransferred = "ScienceTransferred";
        public const string FundsTransferred = "FundsTransferred";
        public const string CraftRegistered = "CraftRegistered";
        public const string CraftAccessGranted = "CraftAccessGranted";
        public const string TradeProposed = "TradeProposed";
        public const string TradeCommitted = "TradeCommitted";
        public const string TradeCancelled = "TradeCancelled";
        public const string AgreementProposed = "AgreementProposed";
        public const string AgreementAccepted = "AgreementAccepted";
        public const string AgreementRevoked = "AgreementRevoked";
        public const string ObjectiveProgressed = "ObjectiveProgressed";
        public const string ObjectiveCompleted = "ObjectiveCompleted";
        public const string StoryPublished = "StoryPublished";
        public const string VesselLaunched = "VesselLaunched";
        public const string VesselRecovered = "VesselRecovered";
        public const string BodyReached = "BodyReached";
        public const string RescueCompleted = "RescueCompleted";
        public const string MissionCompleted = "MissionCompleted";
        public const string ScienceEarned = "ScienceEarned";
        public const string FundsEarned = "FundsEarned";
        public const string ReputationEarned = "ReputationEarned";
        public const string AchievementCompleted = "AchievementCompleted";
        public const string LoanRequested = "LoanRequested";
        public const string LoanApproved = "LoanApproved";
        public const string LoanInterestAccrued = "LoanInterestAccrued";
        public const string LoanRepaid = "LoanRepaid";
        public const string LoanDefaulted = "LoanDefaulted";
        public const string TechnologyUnlocked = "TechnologyUnlocked";
        public const string EventModeStarted = "EventModeStarted";
        public const string EventModeEnded = "EventModeEnded";
        public const string StorePurchase = "StorePurchase";
        public const string ResearchContributed = "ResearchContributed";
        public const string ResearchCompleted = "ResearchCompleted";
        public const string ObjectiveDefined = "ObjectiveDefined";
        public const string ObjectiveUpdated = "ObjectiveUpdated";
        public const string ObjectiveArchived = "ObjectiveArchived";
    }
}
