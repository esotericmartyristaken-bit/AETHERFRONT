using System;
using System.Linq;
using Aetherfront.Core;

namespace AetherfrontCoreTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                CreateAndJoinFaction();
                IndependentProgressionAndIdempotency();
                CraftAccessIsPermissionBound();
                CraftAccessRespectsSubspace();
                ObjectiveRewardsComeFromEvents();
                MembershipGovernanceWorks();
                AtomicTradeWorks();
                CraftTradeTransfersControl();
                AllianceAndStoryLedgerWork();
                ScienceDeltaIsFactionOwned();
                FundsAndReputationAreFactionOwned();
                ContractsAndAchievementsAreFactionScoped();
                CentralBankLoanLifecycleWorks();
                TrustedMilestonesAreDeduplicated();
                RecurringObjectivesUsePeriodsAndCatchUp();
                ResearchProjectsAreFactionScoped();
                AccountCraftGrantWorks();
                TechnologyUnlockIsFactionScoped();
                JsonRoundTripPreservesState();
                AccountRegistryRoundTrip();
                ModeratorObjectiveLifecycleWorks();
                EventModeLifecycleWorks();
                StorePurchaseLifecycleWorks();
                Console.WriteLine("AetherfrontCoreTests: 23/23 passed");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }

        private static void CreateAndJoinFaction()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var created = AetherfrontDomain.CreateFaction(state, "create-1", "alice", "Aster Union", "AST", "Research first", "#12ABEF", "flag-1", "hash-1", now, RecruitmentPolicies.Open);
            Require(created.Accepted, "faction creation accepted");
            Require(state.Factions.Count == 1, "one faction exists");
            var joined = AetherfrontDomain.JoinFaction(state, "join-1", "bob", created.FactionId, "Bob", now);
            Require(joined.Accepted, "member join accepted");
            Require(state.FindFaction(created.FactionId).Members.Count == 2, "member was added");
            Require(!AetherfrontDomain.JoinFaction(state, "join-2", "bob", created.FactionId, "Bob", now).Accepted, "duplicate membership rejected");

            var applicationState = NewState();
            var applicationFaction = AetherfrontDomain.CreateFaction(applicationState, "create-app", "owner", "Applicants", "APP", "", "#AABBCC", "", "", now, RecruitmentPolicies.Application);
            var application = AetherfrontDomain.ApplyToFaction(applicationState, "apply-1", "carol", applicationFaction.FactionId, "Carol", now);
            Require(application.Accepted, "application accepted");
            Require(AetherfrontDomain.ReviewFactionApplication(applicationState, "review-1", "owner", applicationState.Applications.Single().Id, true, now).Accepted, "application review accepted");
        }

        private static void IndependentProgressionAndIdempotency()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var first = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var second = AetherfrontDomain.CreateFaction(state, "create-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            var a = state.FindFaction(first.FactionId);
            var b = state.FindFaction(second.FactionId);
            a.Progression.Science = 500;
            var transfer = AetherfrontDomain.TransferScience(state, "science-1", "alice", a.Id, b.Id, 125, now);
            Require(transfer.Accepted, "science transfer accepted");
            Require(a.Progression.Science == 375 && b.Progression.Science == 125, "science isolated and transferred");
            var duplicate = AetherfrontDomain.TransferScience(state, "science-1", "alice", a.Id, b.Id, 125, now);
            Require(duplicate.Accepted, "duplicate returns original success");
            Require(a.Progression.Science == 375 && b.Progression.Science == 125, "duplicate did not duplicate reward");
            Require(!AetherfrontDomain.TransferScience(state, "science-2", "bob", a.Id, b.Id, 1, now).Accepted, "non-owner economy action rejected");
        }

        private static void CraftAccessIsPermissionBound()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var a = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "create-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            var craft = AetherfrontDomain.RegisterCraft(state, "craft-1", "alice", a.FactionId, "craft-1", "Scout", "craft-hash", now);
            Require(craft.Accepted, "craft registered");
            Require(!AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "craft-1", CraftAccess.Launch, now), "unlicensed control rejected");
            var grant = AetherfrontDomain.GrantCraftAccess(state, "grant-1", "alice", "craft-1", b.FactionId, new[] { CraftAccess.ViewFull, CraftAccess.Launch }, null, "agreement-1", now);
            Require(grant.Accepted, "craft grant accepted");
            Require(AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "craft-1", CraftAccess.Launch, now), "licensed control accepted");
            Require(!AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "craft-1", CraftAccess.Modify, now), "ungranted control rejected");
            var alliance = AetherfrontDomain.ProposeAlliance(state, "alliance-1", "alice", a.FactionId, b.FactionId, null, now);
            Require(alliance.Accepted, "craft access alliance proposed");
            Require(AetherfrontDomain.RespondToAgreement(state, "alliance-2", "bob", state.Agreements.Single().Id, true, now).Accepted,
                "craft access alliance accepted");
            Require(AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "craft-1", CraftAccess.Launch, now),
                "alliance launch access accepted");
        }

        private static void CraftAccessRespectsSubspace()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var a = AetherfrontDomain.CreateFaction(state, "subspace-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "subspace-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            var craft = AetherfrontDomain.RegisterCraft(state, "subspace-craft", "alice", a.FactionId,
                "subspace-craft", "Scout", "hash", now);
            Require(craft.Accepted, "subspace craft registered");
            var grant = AetherfrontDomain.GrantCraftAccess(state, "subspace-grant", "alice", "subspace-craft",
                b.FactionId, new[] { CraftAccess.ViewFull }, null, "subspace-agreement", now);
            Require(grant.Accepted, "subspace grant accepted");
            Require(!AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "subspace-craft",
                CraftAccess.ViewFull, now, 1, 2), "cross-subspace craft access rejected");
            Require(AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "subspace-craft",
                CraftAccess.ViewFull, now, 2, 2), "same-subspace craft access accepted");
            state.FindCraft("subspace-craft").Archived = true;
            Require(!AetherfrontDomain.CanControlCraft(state, "alice", a.FactionId, "subspace-craft",
                CraftAccess.Modify, now, 2, 2), "archived craft control rejected");
        }

        private static void ObjectiveRewardsComeFromEvents()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var faction = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            state.Objectives.Add(new ObjectiveDefinition
            {
                Id = "first-orbit",
                EventType = "OrbitAchieved",
                Target = 2,
                ScienceReward = 50,
                FundsRewardMicro = 1000,
                ReputationReward = 3
            });
            var first = AetherfrontDomain.RecordObjectiveEvent(state, "objective-1", "alice", faction.FactionId, "OrbitAchieved", 1, "vessel-1", now);
            Require(first.Accepted, "objective progress accepted");
            Require(state.FindFaction(faction.FactionId).Progression.Science == 0, "partial objective has no reward");
            var second = AetherfrontDomain.RecordObjectiveEvent(state, "objective-2", "alice", faction.FactionId, "OrbitAchieved", 1, "vessel-2", now);
            Require(second.Accepted, "objective completion accepted");
            var progress = state.ObjectiveProgress.Single();
            Require(progress.Completed, "objective completed");
            Require(state.FindFaction(faction.FactionId).Progression.Science == 50, "objective reward applied");
        }

        private static void JsonRoundTripPreservesState()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "desc", "#AAAAAA", "flag", "hash", now);
            var copy = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(state));
            Require(copy.Factions.Count == 1, "json faction count preserved");
            Require(copy.Factions[0].Name == "A", "json faction identity preserved");
            Require(copy.Events.Count == 1, "json event ledger preserved");
        }

        private static void AccountRegistryRoundTrip()
        {
            var state = NewState();
            state.Accounts.Add(new AetherfrontAccountRecord
            {
                AccountId = "device-account",
                DisplayName = "Pilot",
                FirstSeenAtUtc = DateTime.UtcNow.AddDays(-1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            var copy = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(state));
            Require(copy.FindAccount("DEVICE-ACCOUNT") != null, "account registry survives serialization");
            Require(AetherfrontDomain.NormalizeAccountId("  device-account  ") == "device-account", "account identity normalized");
            Require(AetherfrontDomain.NormalizeDisplayName(new string('x', 100)).Length == 64, "display name bounded");
        }

        private static void ModeratorObjectiveLifecycleWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            state.ModeratorAccountIds.Add("dm");
            var faction = AetherfrontDomain.CreateFaction(state, "objective-admin-faction", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var created = AetherfrontDomain.UpsertObjective(state, "objective-admin-create", "dm", "weekly-launch",
                "season-1", "Launch week", "Complete one launch", AetherfrontEventTypes.VesselLaunched, 1,
                25, 1000, 2, true, null, null, 7, 2, now);
            Require(created.Accepted, "moderator objective create accepted");
            Require(state.Objectives.Single().ConfigManaged == false && !state.Objectives.Single().Archived,
                "dynamic objective is live and not config managed");
            var updated = AetherfrontDomain.UpsertObjective(state, "objective-admin-update", "dm", "weekly-launch",
                "season-2", "Launch week updated", "Complete two launches", AetherfrontEventTypes.VesselLaunched, 2,
                50, 2000, 3, false, null, null, 0, 0, now);
            Require(updated.Accepted && state.Objectives.Single().Target == 2 && !state.Objectives.Single().PublicProgress,
                "moderator objective update accepted");
            Require(!AetherfrontDomain.UpsertObjective(state, "objective-admin-denied", "alice", "denied",
                "", "Denied", "", AetherfrontEventTypes.VesselLaunched, 1, 0, 0, 0, true,
                null, null, 0, 0, now).Accepted, "non-moderator objective edit rejected");
            Require(AetherfrontDomain.ArchiveObjective(state, "objective-admin-archive", "dm", "weekly-launch", now).Accepted,
                "moderator objective archive accepted");
            Require(state.Objectives.Single().Archived, "objective archived");
            Require(!AetherfrontDomain.RecordObjectiveEvent(state, "objective-admin-event", "alice", faction.FactionId,
                AetherfrontEventTypes.VesselLaunched, 1, "vessel", now).Accepted, "archived objective cannot progress");
        }

        private static void EventModeLifecycleWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            state.ModeratorAccountIds.Add("dm");
            var started = AetherfrontDomain.SetEventMode(state, "event-start", "dm", "launch-window",
                "Launch window", "Mun", 4, true, now, now.AddHours(2), now);
            Require(started.Accepted && state.EventMode.Active && state.EventMode.TargetSubspaceId == 4,
                "event mode started");
            Require(state.Events.Any(e => e.Type == AetherfrontEventTypes.EventModeStarted),
                "event mode start ledgered");
            Require(!AetherfrontDomain.SetEventMode(state, "event-denied", "pilot", "bad", "Bad", "", int.MinValue,
                true, now, null, now).Accepted, "non-moderator event mode rejected");
            Require(AetherfrontDomain.ClearEventMode(state, "event-clear", "dm", now.AddMinutes(5)).Accepted &&
                !state.EventMode.Active, "event mode cleared");
            Require(state.Events.Any(e => e.Type == AetherfrontEventTypes.EventModeEnded),
                "event mode end ledgered");
        }

        private static void StorePurchaseLifecycleWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var factionResult = AetherfrontDomain.CreateFaction(state, "store-faction", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var faction = state.FindFaction(factionResult.FactionId);
            faction.Progression.Science = 25;
            state.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "engineering101", Title = "Engineering", ScienceCost = 20 });
            state.StoreItems.Add(new StoreItem
            {
                Id = "engineering-license",
                Title = "Engineering license",
                Description = "Test license",
                PriceFundsMicro = 500,
                PriceScience = 25,
                GrantTechnologyId = "engineering101",
                Stock = 1,
                Enabled = true,
                Public = true
            });
            var purchase = AetherfrontDomain.PurchaseStoreItem(state, "store-buy", "alice", "engineering-license", now);
            Require(purchase.Accepted && faction.Progression.StoreItemIds.Contains("engineering-license"), "store purchase accepted");
            Require(faction.Progression.TechnologyUnlocks.Contains("engineering101") && state.StoreItems[0].Stock == 0,
                "store reward and stock persisted");
            Require(!AetherfrontDomain.PurchaseStoreItem(state, "store-buy-again", "alice", "engineering-license", now).Accepted,
                "duplicate store purchase rejected");
        }

        private static void ScienceDeltaIsFactionOwned()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var faction = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var award = AetherfrontDomain.AwardScience(state, "science-award", "alice", faction.FactionId, 37, "Research", now);
            Require(award.Accepted, "science award accepted");
            Require(state.FindFaction(faction.FactionId).Progression.Science == 37, "faction science increased");
            Require(state.FindFaction(faction.FactionId).Progression.TotalScienceEarned == 37, "science lifetime total increased");
        }

        private static void AccountCraftGrantWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var faction = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var craft = AetherfrontDomain.RegisterCraft(state, "craft-1", "alice", faction.FactionId, "craft-1", "Scout", "hash", now);
            Require(craft.Accepted, "craft registered for account grant");
            state.FindCraft("craft-1").AccessGrants.Add(new CraftAccessGrant
            {
                GranteeAccountId = "bob",
                Permissions = new System.Collections.Generic.List<string> { CraftAccess.ViewFull }
            });
            Require(AetherfrontDomain.CanControlCraft(state, "bob", "", "craft-1", CraftAccess.ViewFull, now), "account craft grant accepted");
        }

        private static void FundsAndReputationAreFactionOwned()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var faction = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var funds = AetherfrontDomain.AwardFunds(state, "funds-award", "alice", faction.FactionId, 1250000, "Contract payout", now);
            var reputation = AetherfrontDomain.AwardReputation(state, "rep-award", "alice", faction.FactionId, 4, "Mission complete", now);
            Require(funds.Accepted && reputation.Accepted, "funds and reputation awards accepted");
            Require(state.FindFaction(faction.FactionId).Progression.FundsMicro == 101250000, "funds award isolated to faction");
            Require(state.FindFaction(faction.FactionId).Progression.TotalFundsEarnedMicro == 1250000, "funds lifetime total increased");
            Require(state.FindFaction(faction.FactionId).Progression.Reputation == 4, "reputation award isolated to faction");
            Require(state.Events.Any(e => e.Type == AetherfrontEventTypes.FundsEarned) &&
                    state.Events.Any(e => e.Type == AetherfrontEventTypes.ReputationEarned), "progression events recorded");
        }

        private static void TechnologyUnlockIsFactionScoped()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            state.TechnologyDefinitions.Add(new TechnologyDefinition
            {
                Id = "start",
                Title = "Starter",
                ScienceCost = 0,
                Starter = true
            });
            state.TechnologyDefinitions.Add(new TechnologyDefinition
            {
                Id = "basic-orbit",
                Title = "Basic orbit",
                ScienceCost = AetherfrontConstants.TechnologyUnlockCost,
                PrerequisiteIds = new System.Collections.Generic.List<string> { "start" }
            });
            var faction = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            state.FindFaction(faction.FactionId).Progression.Science = AetherfrontConstants.TechnologyUnlockCost;
            Require(state.FindFaction(faction.FactionId).Progression.TechnologyUnlocks.Contains("start"), "starter technology granted");
            Require(!AetherfrontDomain.UnlockTechnology(state, "tech-unknown", "alice", faction.FactionId, "not-server-defined", now).Accepted,
                "unknown technology rejected");
            var unlock = AetherfrontDomain.UnlockTechnology(state, "tech-1", "alice", faction.FactionId, "basic-orbit", now);
            Require(unlock.Accepted, "technology unlock accepted");
            Require(state.FindFaction(faction.FactionId).Progression.TechnologyUnlocks.Contains("basic-orbit"), "technology persisted");
            Require(state.FindFaction(faction.FactionId).Progression.Science == 0, "technology cost deducted from faction science");
        }

        private static void ContractsAndAchievementsAreFactionScoped()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var a = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "create-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            Require(AetherfrontDomain.RecordMissionCompleted(state, "mission-a", "alice", a.FactionId, "contract-1", now).Accepted,
                "mission completion accepted");
            Require(AetherfrontDomain.RecordAchievementCompleted(state, "achievement-a", "alice", a.FactionId, "orbit-1", now).Accepted,
                "achievement completion accepted");
            Require(!AetherfrontDomain.RecordMissionCompleted(state, "mission-a-duplicate", "alice", a.FactionId, "contract-1", now).Accepted,
                "duplicate mission rejected");
            Require(state.FindFaction(a.FactionId).Progression.ContractsCompleted == 1 &&
                    state.FindFaction(a.FactionId).Progression.AchievementsCompleted == 1,
                "faction counters updated");
            Require(state.FindFaction(b.FactionId).Progression.ContractsCompleted == 0 &&
                    state.FindFaction(b.FactionId).Progression.AchievementsCompleted == 0,
                "other faction counters unchanged");
        }

        private static void CentralBankLoanLifecycleWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var faction = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            state.ModeratorAccountIds.Add("dm");
            var request = AetherfrontDomain.RequestLoan(state, "loan-request", "alice", faction.FactionId, 10000000, 0, 30, now);
            Require(request.Accepted && state.Loans.Count == 1 && state.Loans[0].State == LoanStates.Pending, "loan requested");
            Require(AetherfrontDomain.ApproveLoan(state, "loan-approve", "dm", state.Loans[0].Id, now).Accepted, "loan approved");
            Require(state.FindFaction(faction.FactionId).Progression.FundsMicro == 110000000, "loan principal credited");
            Require(AetherfrontDomain.RepayLoan(state, "loan-repay", "alice", state.Loans[0].Id, 1000000000, now.AddDays(1)).Accepted,
                "loan repayment accepted");
            Require(state.Loans[0].State == LoanStates.Repaid, "loan repaid");
        }

        private static void TrustedMilestonesAreDeduplicated()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var faction = AetherfrontDomain.CreateFaction(state, "create-milestone", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var launch = AetherfrontDomain.RecordTrustedMilestone(state, "launch-1", "alice", faction.FactionId,
                AetherfrontEventTypes.VesselLaunched, "vessel-1", now);
            Require(launch.Accepted && state.FindFaction(faction.FactionId).Progression.SuccessfulLaunches == 1,
                "trusted launch counted");
            Require(!AetherfrontDomain.RecordTrustedMilestone(state, "launch-2", "alice", faction.FactionId,
                AetherfrontEventTypes.VesselLaunched, "vessel-1", now).Accepted, "duplicate launch rejected");
            Require(AetherfrontDomain.RecordTrustedMilestone(state, "body-1", "alice", faction.FactionId,
                AetherfrontEventTypes.BodyReached, "Mun", now).Accepted, "body milestone counted");
            Require(!AetherfrontDomain.RecordTrustedMilestone(state, "body-2", "alice", faction.FactionId,
                AetherfrontEventTypes.BodyReached, "Mun", now).Accepted, "duplicate body rejected");
            Require(state.FindFaction(faction.FactionId).Progression.BodiesReached == 1, "body counter deduplicated");
        }

        private static void RecurringObjectivesUsePeriodsAndCatchUp()
        {
            var state = NewState();
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var faction = AetherfrontDomain.CreateFaction(state, "create-weekly", "alice", "A", "A", "", "#AAAAAA", "", "", start);
            state.Objectives.Add(new ObjectiveDefinition
            {
                Id = "weekly-body",
                SeasonId = "season-1",
                EventType = AetherfrontEventTypes.BodyReached,
                Target = 1,
                RepeatPeriodDays = 7,
                ScienceReward = 5
            });
            Require(AetherfrontDomain.RecordObjectiveEvent(state, "weekly-1", "alice", faction.FactionId,
                AetherfrontEventTypes.BodyReached, 1, "Kerbin", start.AddDays(1)).Accepted, "weekly period one accepted");
            Require(AetherfrontDomain.RecordObjectiveEvent(state, "weekly-2", "alice", faction.FactionId,
                AetherfrontEventTypes.BodyReached, 1, "Mun", start.AddDays(8)).Accepted, "weekly period two accepted");
            Require(state.ObjectiveProgress.Count == 2, "weekly progress has separate periods");

            state.Objectives.Add(new ObjectiveDefinition
            {
                Id = "timed-catch-up",
                SeasonId = "season-2",
                EventType = AetherfrontEventTypes.BodyReached,
                Target = 1,
                StartsAtUtc = start,
                EndsAtUtc = start.AddDays(7),
                CatchUpWindowDays = 2,
                ScienceReward = 5
            });
            Require(AetherfrontDomain.RecordObjectiveEvent(state, "catch-up", "alice", faction.FactionId,
                AetherfrontEventTypes.BodyReached, 1, "Duna", start.AddDays(8)).Accepted, "catch-up window accepted");
            Require(!AetherfrontDomain.RecordObjectiveEvent(state, "too-late", "alice", faction.FactionId,
                AetherfrontEventTypes.BodyReached, 1, "Eve", start.AddDays(10)).Accepted, "expired catch-up rejected");
        }

        private static void ResearchProjectsAreFactionScoped()
        {
            var state = NewState();
            var now = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            var a = AetherfrontDomain.CreateFaction(state, "research-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "research-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            state.FindFaction(a.FactionId).Progression.Science = 25;
            state.FindFaction(b.FactionId).Progression.Science = 25;
            state.ResearchProjects.Add(new ResearchProjectDefinition
            {
                Id = "research-project-1",
                SeasonId = "research-season",
                Title = "Shared breakthrough",
                TargetScience = 25,
                RewardTechnologyId = "tech-research-1",
                FundsRewardMicro = 1000,
                ReputationReward = 2
            });
            var contribution = AetherfrontDomain.ContributeResearch(state, "research-contribute-1", "alice", a.FactionId,
                "research-project-1", 25, now);
            Require(contribution.Accepted, "research contribution accepted");
            var faction = state.FindFaction(a.FactionId);
            Require(faction.Progression.Science == 0 && faction.Progression.ResearchProjectsCompleted == 1,
                "research cost and completion isolated to contributing faction");
            Require(faction.Progression.TechnologyUnlocks.Contains("tech-research-1") && faction.Progression.FundsMicro == 100001000,
                "research rewards applied");
            Require(state.FindFaction(b.FactionId).Progression.Science == 25 &&
                state.FindFaction(b.FactionId).Progression.ResearchProjectsCompleted == 0,
                "other faction research remains independent");
            Require(!AetherfrontDomain.ContributeResearch(state, "research-overflow", "alice", a.FactionId,
                "research-project-1", 1, now).Accepted, "completed research cannot be over-contributed");
        }

        private static void MembershipGovernanceWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var created = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var invite = AetherfrontDomain.InviteMember(state, "invite-1", "alice", created.FactionId, "bob", now);
            Require(invite.Accepted, "invitation accepted");
            var invitation = state.Invitations.Single();
            var join = AetherfrontDomain.AcceptInvitation(state, "accept-1", "bob", invitation.Id, "Bob", now);
            Require(join.Accepted, "invitation join accepted");
            var role = AetherfrontDomain.SetMemberRole(state, "role-1", "alice", created.FactionId, "bob", FactionRoles.Diplomat, now);
            Require(role.Accepted && state.FindFaction(created.FactionId).Members.Single(m => m.AccountId == "bob").Role == FactionRoles.Diplomat, "role change accepted");
            var remove = AetherfrontDomain.RemoveMember(state, "remove-1", "alice", created.FactionId, "bob", now);
            Require(remove.Accepted && state.FindFactionForAccount("bob") == null, "member removal accepted: " + remove.ReasonCode);
        }

        private static void AtomicTradeWorks()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var a = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "create-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            state.FindFaction(a.FactionId).Progression.FundsMicro = 5000;
            state.FindFaction(b.FactionId).Progression.Science = 100;
            Require(!AetherfrontDomain.CreateTradeOffer(state, "trade-empty", "alice", a.FactionId, b.FactionId,
                new TradeAsset[0], new TradeAsset[0], now).Accepted, "empty trade rejected");
            var offer = AetherfrontDomain.CreateTradeOffer(state, "trade-1", "alice", a.FactionId, b.FactionId,
                new[] { new TradeAsset { Type = TradeAssetTypes.FundsMicro, Amount = 1000 } },
                new[] { new TradeAsset { Type = TradeAssetTypes.Science, Amount = 25 } }, now);
            Require(offer.Accepted, "trade proposed");
            Require(AetherfrontDomain.ConfirmTrade(state, "trade-confirm-a", "alice", offer.TradeId, now).Accepted, "proposer confirmation accepted");
            var committed = AetherfrontDomain.ConfirmTrade(state, "trade-confirm-b", "bob", offer.TradeId, now);
            Require(committed.Accepted && state.FindTrade(offer.TradeId).State == TradeStates.Committed, "trade committed atomically");
            Require(state.FindFaction(a.FactionId).Progression.FundsMicro == 4000, "proposer funds settled");
            Require(state.FindFaction(b.FactionId).Progression.Science == 75, "recipient science settled");
        }

        private static void AllianceAndStoryLedgerWork()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var a = AetherfrontDomain.CreateFaction(state, "create-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "create-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            var proposal = AetherfrontDomain.ProposeAlliance(state, "alliance-1", "alice", a.FactionId, b.FactionId, null, now);
            Require(proposal.Accepted, "alliance proposed");
            Require(AetherfrontDomain.RespondToAgreement(state, "alliance-2", "bob", state.Agreements.Single().Id, true, now).Accepted, "alliance accepted");
            Require(AetherfrontDomain.HasActiveAlliance(state, a.FactionId, b.FactionId, now), "active alliance visible");
            var story = AetherfrontDomain.PublishStory(state, "story-1", "alice", a.FactionId, "First Light", "A faction story.", true,
                new[] { "launch", "origin" }, new string[0], now);
            Require(story.Accepted && state.Stories.Count == 1 && !state.Stories[0].Public, "story submitted for moderation");
            state.ModeratorAccountIds.Add("dm");
            Require(AetherfrontDomain.ReviewStory(state, "story-review", "dm", state.Stories[0].Id, true, now).Accepted && state.Stories[0].Public,
                "moderated story published");
            Require(state.Events.Any(e => e.Type == AetherfrontEventTypes.StoryPublished), "story event ledger entry");
        }

        private static void CraftTradeTransfersControl()
        {
            var state = NewState();
            var now = DateTime.UtcNow;
            var a = AetherfrontDomain.CreateFaction(state, "craft-trade-a", "alice", "A", "A", "", "#AAAAAA", "", "", now);
            var b = AetherfrontDomain.CreateFaction(state, "craft-trade-b", "bob", "B", "B", "", "#BBBBBB", "", "", now);
            var craft = AetherfrontDomain.RegisterCraft(state, "craft-trade-register", "alice", a.FactionId,
                "craft-trade", "Scout", "hash", now);
            var offer = AetherfrontDomain.CreateTradeOffer(state, "craft-trade-offer", "alice", a.FactionId, b.FactionId,
                new[] { new TradeAsset { Type = TradeAssetTypes.Craft, EntityId = "craft-trade", ContentSha256 = "hash" } },
                new[] { new TradeAsset { Type = TradeAssetTypes.FundsMicro, Amount = 1000 } }, now);
            Require(craft.Accepted && offer.Accepted, "craft trade proposed");
            Require(AetherfrontDomain.ConfirmTrade(state, "craft-trade-confirm-a", "alice", offer.TradeId, now).Accepted,
                "craft trade proposer confirmed");
            Require(AetherfrontDomain.ConfirmTrade(state, "craft-trade-confirm-b", "bob", offer.TradeId, now).Accepted,
                "craft trade recipient confirmed");
            Require(!AetherfrontDomain.CanControlCraft(state, "alice", a.FactionId, "craft-trade", CraftAccess.Launch, now),
                "seller loses craft control");
            Require(AetherfrontDomain.CanControlCraft(state, "bob", b.FactionId, "craft-trade", CraftAccess.Launch, now),
                "buyer gains craft control");
        }

        private static AetherfrontState NewState()
        {
            return new AetherfrontState { ServerId = "test-server" };
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
        }
    }
}
