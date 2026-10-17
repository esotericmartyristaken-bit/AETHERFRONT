using Aetherfront.Core;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Aetherfront
{
    public static class AetherfrontGameplayBridge
    {
        public static List<AetherfrontEvent> RecordCommandOutcome(AetherfrontWireRequest request, AetherfrontResult result, string accountId)
        {
            if (request == null || result == null || !result.Accepted || string.IsNullOrWhiteSpace(accountId))
                return new List<AetherfrontEvent>();

            var eventType = EventTypeFor(request.CommandType);
            if (request.CommandType == AetherfrontCommandType.ContributeResearch)
            {
                var completed = (result.Events ?? new List<AetherfrontEvent>()).FirstOrDefault(e =>
                    e != null && string.Equals(e.Type, AetherfrontEventTypes.ResearchCompleted, StringComparison.OrdinalIgnoreCase));
                if (completed == null) return new List<AetherfrontEvent>();
                return RecordObjectiveEvent(accountId, result.FactionId, AetherfrontEventTypes.ResearchCompleted,
                    completed.EntityId, "objective:research:" + request.CommandId);
            }
            if (string.IsNullOrWhiteSpace(eventType))
                return new List<AetherfrontEvent>();

            var factionId = result.FactionId;
            if (string.IsNullOrWhiteSpace(factionId))
                factionId = request.FactionId;

            if (string.IsNullOrWhiteSpace(factionId))
                return new List<AetherfrontEvent>();

            var commandId = "objective:" + request.CommandId + ":" + eventType;
            try
            {
                var objectiveResult = AetherfrontServerSystem.Execute(state =>
                    AetherfrontDomain.RecordObjectiveEvent(state, commandId, accountId, factionId, eventType, 1,
                        request.EntityId ?? string.Empty, DateTime.UtcNow));
                return objectiveResult.Events ?? new List<AetherfrontEvent>();
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront objective event was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        public static List<AetherfrontEvent> RecordTrustedEvent(string accountId, string factionId, string eventType, string entityId)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(factionId) || string.IsNullOrWhiteSpace(eventType))
                return new List<AetherfrontEvent>();

            try
            {
                var now = DateTime.UtcNow;
                var milestoneCommand = "milestone:" + eventType + ":" + factionId + ":" + entityId;
                var result = AetherfrontServerSystem.Execute(state =>
                {
                    var milestone = AetherfrontDomain.RecordTrustedMilestone(state, milestoneCommand, accountId,
                        factionId, eventType, entityId, now);
                    if (!milestone.Accepted) return milestone;
                    var events = new List<AetherfrontEvent>(milestone.Events ?? new List<AetherfrontEvent>());
                    var objective = AetherfrontDomain.RecordObjectiveEvent(state,
                        "objective:trusted:" + eventType + ":" + factionId + ":" + entityId,
                        accountId, factionId, eventType, 1, entityId, now);
                    if (objective.Events != null) events.AddRange(objective.Events);
                    return AetherfrontResult.Success(state.Revision, factionId, events: events);
                });
                return result.Events ?? new List<AetherfrontEvent>();
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront trusted objective event was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        private static List<AetherfrontEvent> RecordObjectiveEvent(string accountId, string factionId, string eventType,
            string entityId, string commandId)
        {
            if (string.IsNullOrWhiteSpace(factionId)) return new List<AetherfrontEvent>();
            try
            {
                var result = AetherfrontServerSystem.Execute(state => AetherfrontDomain.RecordObjectiveEvent(state,
                    commandId, accountId, factionId, eventType, 1, entityId ?? string.Empty, DateTime.UtcNow));
                return result.Events ?? new List<AetherfrontEvent>();
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront objective event was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        public static List<AetherfrontEvent> RecordScienceDelta(string accountId, long amount, string reason)
        {
            if (string.IsNullOrWhiteSpace(accountId) || amount <= 0)
                return new List<AetherfrontEvent>();

            try
            {
                var snapshot = AetherfrontServerSystem.Snapshot();
                var faction = snapshot.FindFactionForAccount(accountId);
                if (faction == null) return new List<AetherfrontEvent>();

                var events = new List<AetherfrontEvent>();
                var award = AetherfrontServerSystem.Execute(state => AetherfrontDomain.AwardScience(state,
                    "science-award:" + Guid.NewGuid().ToString("N"), accountId, faction.Id, amount, reason, DateTime.UtcNow));
                if (award.Events != null) events.AddRange(award.Events);
                var objective = AetherfrontServerSystem.Execute(state => AetherfrontDomain.RecordObjectiveEvent(state,
                    "science-objective:" + Guid.NewGuid().ToString("N"), accountId, faction.Id,
                    AetherfrontEventTypes.ScienceEarned, amount, string.Empty, DateTime.UtcNow));
                if (objective.Events != null) events.AddRange(objective.Events);
                return events;
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront science delta was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        public static List<AetherfrontEvent> RecordFundsDelta(string accountId, long amountMicro, string reason)
        {
            if (string.IsNullOrWhiteSpace(accountId) || amountMicro <= 0)
                return new List<AetherfrontEvent>();

            try
            {
                var snapshot = AetherfrontServerSystem.Snapshot();
                var faction = snapshot.FindFactionForAccount(accountId);
                if (faction == null) return new List<AetherfrontEvent>();
                var result = AetherfrontServerSystem.Execute(state => AetherfrontDomain.AwardFunds(state,
                    "funds-award:" + Guid.NewGuid().ToString("N"), accountId, faction.Id, amountMicro, reason, DateTime.UtcNow));
                return result.Events ?? new List<AetherfrontEvent>();
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront funds delta was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        public static List<AetherfrontEvent> RecordReputationDelta(string accountId, long amount, string reason)
        {
            if (string.IsNullOrWhiteSpace(accountId) || amount <= 0)
                return new List<AetherfrontEvent>();

            try
            {
                var snapshot = AetherfrontServerSystem.Snapshot();
                var faction = snapshot.FindFactionForAccount(accountId);
                if (faction == null) return new List<AetherfrontEvent>();
                var result = AetherfrontServerSystem.Execute(state => AetherfrontDomain.AwardReputation(state,
                    "reputation-award:" + Guid.NewGuid().ToString("N"), accountId, faction.Id, amount, reason, DateTime.UtcNow));
                return result.Events ?? new List<AetherfrontEvent>();
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront reputation delta was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        public static List<AetherfrontEvent> RecordMissionCompleted(string accountId, string contractId)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(contractId))
                return new List<AetherfrontEvent>();

            try
            {
                var snapshot = AetherfrontServerSystem.Snapshot();
                var faction = snapshot.FindFactionForAccount(accountId);
                if (faction == null) return new List<AetherfrontEvent>();
                var result = AetherfrontServerSystem.Execute(state => AetherfrontDomain.RecordMissionCompleted(state,
                    "mission-completed:" + faction.Id + ":" + contractId, accountId, faction.Id, contractId, DateTime.UtcNow));
                var events = new List<AetherfrontEvent>(result.Events ?? new List<AetherfrontEvent>());
                var objective = AetherfrontServerSystem.Execute(state => AetherfrontDomain.RecordObjectiveEvent(state,
                    "mission-objective:" + faction.Id + ":" + contractId, accountId, faction.Id, AetherfrontEventTypes.MissionCompleted,
                    1, contractId, DateTime.UtcNow));
                if (objective.Events != null) events.AddRange(objective.Events);
                return events;
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront mission completion was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        public static List<AetherfrontEvent> RecordAchievementCompleted(string accountId, string achievementId)
        {
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(achievementId))
                return new List<AetherfrontEvent>();

            try
            {
                var snapshot = AetherfrontServerSystem.Snapshot();
                var faction = snapshot.FindFactionForAccount(accountId);
                if (faction == null) return new List<AetherfrontEvent>();
                var result = AetherfrontServerSystem.Execute(state => AetherfrontDomain.RecordAchievementCompleted(state,
                    "achievement-completed:" + faction.Id + ":" + achievementId, accountId, faction.Id, achievementId, DateTime.UtcNow));
                var events = new List<AetherfrontEvent>(result.Events ?? new List<AetherfrontEvent>());
                var objective = AetherfrontServerSystem.Execute(state => AetherfrontDomain.RecordObjectiveEvent(state,
                    "achievement-objective:" + faction.Id + ":" + achievementId, accountId, faction.Id, AetherfrontEventTypes.AchievementCompleted,
                    1, achievementId, DateTime.UtcNow));
                if (objective.Events != null) events.AddRange(objective.Events);
                return events;
            }
            catch (Exception e)
            {
                LunaLog.Warning("Aetherfront achievement was not recorded: " + e.Message);
                return new List<AetherfrontEvent>();
            }
        }

        private static string EventTypeFor(AetherfrontCommandType commandType)
        {
            switch (commandType)
            {
                case AetherfrontCommandType.CreateFaction: return AetherfrontEventTypes.FactionCreated;
                case AetherfrontCommandType.RegisterCraft: return AetherfrontEventTypes.CraftRegistered;
                case AetherfrontCommandType.PublishStory: return AetherfrontEventTypes.StoryPublished;
                case AetherfrontCommandType.ConfirmTrade: return AetherfrontEventTypes.TradeCommitted;
                case AetherfrontCommandType.RespondToAgreement: return AetherfrontEventTypes.AgreementAccepted;
                default: return string.Empty;
            }
        }
    }
}
