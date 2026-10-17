using System;
using System.Collections.Generic;

namespace Aetherfront.Core
{
    public enum AetherfrontWireOperation
    {
        SnapshotRequest = 1,
        CommandRequest = 2,
        SnapshotReply = 3,
        CommandReply = 4,
        EventNotice = 5
    }

    public enum AetherfrontCommandType
    {
        CreateFaction = 1,
        JoinFaction = 2,
        LeaveFaction = 3,
        UpdateFactionProfile = 4,
        InviteMember = 5,
        AcceptInvitation = 6,
        RemoveMember = 7,
        SetMemberRole = 8,
        TransferLeadership = 9,
        TransferScience = 10,
        TransferFunds = 11,
        RegisterCraft = 12,
        GrantCraftAccess = 13,
        ProposeAlliance = 14,
        RespondToAgreement = 15,
        RevokeAgreement = 16,
        CreateTradeOffer = 17,
        ConfirmTrade = 18,
        CancelTrade = 19,
        PublishStory = 20,
        RecordObjectiveEvent = 21,
        UnlockTechnology = 22,
        RequestFlagAsset = 23,
        ApplyToFaction = 24,
        ReviewFactionApplication = 25,
        ReviewStory = 26,
        RequestLoan = 27,
        ApproveLoan = 28,
        RepayLoan = 29,
        ContributeResearch = 30,
        UpsertObjective = 31,
        ArchiveObjective = 32,
        SetEventMode = 33,
        ClearEventMode = 34,
        PurchaseStoreItem = 35
    }
    public sealed class AetherfrontWireRequest
    {
        public int ProtocolVersion { get; set; } = AetherfrontConstants.CurrentSchemaVersion;
        public AetherfrontWireOperation Operation { get; set; }
        public AetherfrontCommandType CommandType { get; set; }
        public string CommandId { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string TargetFactionId { get; set; } = string.Empty;
        public string TargetAccountId { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Abbreviation { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ColorHex { get; set; } = string.Empty;
        public string FlagAssetId { get; set; } = string.Empty;
        public string FlagSha256 { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Permission { get; set; } = string.Empty;
        public string AgreementId { get; set; } = string.Empty;
        public string InvitationId { get; set; } = string.Empty;
        public string TradeId { get; set; } = string.Empty;
        public long Amount { get; set; }
        public long Amount2 { get; set; }
        public bool Accept { get; set; }
        public bool Public { get; set; } = true;
        public string EventType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string AssetJson { get; set; } = string.Empty;
        public string PermissionsJson { get; set; } = string.Empty;
        public long ExpiresAtUtcTicks { get; set; }
        public string CounterAssetJson { get; set; } = string.Empty;
        public string ContentSha256 { get; set; } = string.Empty;
        public string TechnologyId { get; set; } = string.Empty;
        public string RecruitmentPolicy { get; set; } = string.Empty;
        public string FlagDataBase64 { get; set; } = string.Empty;
        public string ApplicationId { get; set; } = string.Empty;
        public long ExpectedRevision { get; set; } = -1;
        public string StoryId { get; set; } = string.Empty;
        public string TagsJson { get; set; } = string.Empty;
        public string RelatedEventIdsJson { get; set; } = string.Empty;
        public string LoanId { get; set; } = string.Empty;
        public int AprBasisPoints { get; set; }
        public int TermDays { get; set; }
        public string ResearchProjectId { get; set; } = string.Empty;
        public string ObjectiveId { get; set; } = string.Empty;
        public string SeasonId { get; set; } = string.Empty;
        public long ObjectiveTarget { get; set; }
        public long ScienceReward { get; set; }
        public long FundsRewardMicro { get; set; }
        public long ReputationReward { get; set; }
        public int RepeatPeriodDays { get; set; }
        public int CatchUpWindowDays { get; set; }
        public long StartsAtUtcTicks { get; set; }
        public long EndsAtUtcTicks { get; set; }
        public bool ObjectivePublicProgress { get; set; } = true;
        public string EventModeId { get; set; } = string.Empty;
        public string EventModeName { get; set; } = string.Empty;
        public string EventModeBodyName { get; set; } = string.Empty;
        public int EventModeSubspaceId { get; set; } = int.MinValue;
        public bool EventModeAllowAllCrafts { get; set; }
        public long EventModeStartsAtUtcTicks { get; set; }
        public long EventModeEndsAtUtcTicks { get; set; }
        public string StoreItemId { get; set; } = string.Empty;
    }
    public sealed class AetherfrontWireReply
    {
        public int ProtocolVersion { get; set; } = AetherfrontConstants.CurrentSchemaVersion;
        public AetherfrontWireOperation Operation { get; set; }
        public bool Accepted { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string ServerId { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string TradeId { get; set; } = string.Empty;
        public string StateJson { get; set; } = string.Empty;
        public string EventsJson { get; set; } = string.Empty;
        public string FlagAssetId { get; set; } = string.Empty;
        public string FlagSha256 { get; set; } = string.Empty;
        public string FlagDataBase64 { get; set; } = string.Empty;
        public string InterestJson { get; set; } = string.Empty;
    }
    public sealed class AetherfrontVesselInterestSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
        public List<AetherfrontVesselSummary> Vessels { get; set; } = new List<AetherfrontVesselSummary>();
    }
    public sealed class AetherfrontVesselSummary
    {
        public string VesselId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FactionId { get; set; } = string.Empty;
        public string FactionName { get; set; } = string.Empty;
        public string BodyName { get; set; } = string.Empty;
        public int SubspaceId { get; set; } = int.MinValue;
        public bool Landed { get; set; }
        public bool Splashed { get; set; }
        public bool CanControl { get; set; }
        public bool FullSyncAvailable { get; set; }
        public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    }

    public static class AetherfrontProtocol
    {
        public static AetherfrontWireRequest DeserializeRequest(string payload)
        {
            return AetherfrontJson.Deserialize<AetherfrontWireRequest>(payload);
        }

        public static string Serialize(AetherfrontWireRequest request)
        {
            return AetherfrontJson.Serialize(request);
        }

        public static string Serialize(AetherfrontWireReply reply)
        {
            return AetherfrontJson.Serialize(reply);
        }

        public static AetherfrontWireReply DeserializeReply(string payload)
        {
            return AetherfrontJson.Deserialize<AetherfrontWireReply>(payload);
        }
    }
}
