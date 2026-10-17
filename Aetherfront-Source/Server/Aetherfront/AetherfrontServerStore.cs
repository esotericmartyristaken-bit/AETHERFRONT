using Aetherfront.Core;
using Server.Log;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Server.Aetherfront
{
    /// <summary>
    /// Durable, single-writer store for the Aetherfront state machine.
    /// KSP/LMP state remains separate. Aetherfront state is authoritative for faction
    /// progression, agreements, trades, objectives, and its public event ledger.
    /// </summary>
    public sealed class AetherfrontServerStore
    {
        private readonly object _gate = new object();
        private readonly string _root;
        private readonly string _statePath;
        private readonly string _backupPath;
        private readonly string _eventPath;
        private AetherfrontState _state;

        public AetherfrontServerStore(string serverRoot, string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverRoot)) throw new ArgumentException("Server root is required", nameof(serverRoot));
            if (string.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("Server id is required", nameof(serverId));
            _root = Path.Combine(serverRoot, "Aetherfront");
            _statePath = Path.Combine(_root, "state.json");
            _backupPath = _statePath + ".bak";
            _eventPath = Path.Combine(_root, "events.jsonl");
            Directory.CreateDirectory(_root);
            _state = Load(serverId);
            PersistLocked();
        }

        public string RootPath => _root;

        public AetherfrontState ReadSnapshot()
        {
            lock (_gate)
                return AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(_state));
        }

        public AetherfrontResult Execute(Func<AetherfrontState, AetherfrontResult> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (_gate)
            {
                var before = AetherfrontJson.Serialize(_state);
                var eventCount = _state.Events.Count;
                try
                {
                    var result = command(_state);
                    PersistNewEventsLocked(eventCount);
                    PersistLocked();
                    return result;
                }
                catch
                {
                    _state = AetherfrontJson.Deserialize<AetherfrontState>(before);
                    throw;
                }
            }
        }

        public void Persist()
        {
            lock (_gate) PersistLocked();
        }

        public void ReplaceObjectives(IList<ObjectiveDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            lock (_gate)
            {
                var json = AetherfrontJson.Serialize(definitions);
                _state.Objectives = AetherfrontJson.Deserialize<List<ObjectiveDefinition>>(json) ?? new List<ObjectiveDefinition>();
                var validIds = new HashSet<string>(_state.Objectives.Select(o => o.Id), StringComparer.OrdinalIgnoreCase);
                _state.ObjectiveProgress.RemoveAll(p => !validIds.Contains(p.ObjectiveId));
                PersistLocked();
            }
        }

        public void MergeConfiguredObjectives(IList<ObjectiveDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            lock (_gate)
            {
                var configured = AetherfrontJson.Deserialize<List<ObjectiveDefinition>>(AetherfrontJson.Serialize(definitions)) ?? new List<ObjectiveDefinition>();
                var dynamicObjectives = (_state.Objectives ?? new List<ObjectiveDefinition>())
                    .Where(o => o != null && !o.ConfigManaged).ToList();
                var merged = new List<ObjectiveDefinition>(dynamicObjectives);
                foreach (var definition in configured)
                {
                    var overridden = dynamicObjectives.Any(o => string.Equals(o.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
                    if (!overridden) merged.Add(definition);
                }
                _state.Objectives = merged;
                var validIds = new HashSet<string>(_state.Objectives.Select(o => o.Id), StringComparer.OrdinalIgnoreCase);
                _state.ObjectiveProgress.RemoveAll(p => !validIds.Contains(p.ObjectiveId));
                PersistLocked();
            }
        }

        public void MergeConfiguredStoreItems(IList<StoreItem> definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            lock (_gate)
            {
                var configured = AetherfrontJson.Deserialize<List<StoreItem>>(AetherfrontJson.Serialize(definitions)) ?? new List<StoreItem>();
                var dynamicItems = (_state.StoreItems ?? new List<StoreItem>())
                    .Where(i => i != null && !i.ConfigManaged).ToList();
                var merged = new List<StoreItem>(dynamicItems);
                foreach (var definition in configured)
                {
                    var overridden = dynamicItems.Any(i => string.Equals(i.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
                    if (!overridden) merged.Add(definition);
                }
                _state.StoreItems = merged;
                PersistLocked();
            }
        }

        public void ReplaceModerators(IList<string> accountIds)
        {
            lock (_gate)
            {
                _state.ModeratorAccountIds = (accountIds ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                PersistLocked();
            }
        }

        public void ReplaceBankPolicy(CentralBankPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            lock (_gate)
            {
                _state.BankPolicy = AetherfrontJson.Deserialize<CentralBankPolicy>(AetherfrontJson.Serialize(policy));
                PersistLocked();
            }
        }

        public void ReplaceResearchProjects(IList<ResearchProjectDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            lock (_gate)
            {
                var json = AetherfrontJson.Serialize(definitions);
                _state.ResearchProjects = AetherfrontJson.Deserialize<List<ResearchProjectDefinition>>(json) ?? new List<ResearchProjectDefinition>();
                var validIds = new HashSet<string>(_state.ResearchProjects.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
                _state.ResearchProgress.RemoveAll(p => !validIds.Contains(p.ProjectId));
                PersistLocked();
            }
        }

        public void ReplaceTechnologyDefinitions(IList<TechnologyDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            lock (_gate)
            {
                var json = AetherfrontJson.Serialize(definitions);
                _state.TechnologyDefinitions = AetherfrontJson.Deserialize<List<TechnologyDefinition>>(json) ?? new List<TechnologyDefinition>();
                foreach (var faction in _state.Factions)
                {
                    if (faction?.Progression == null) continue;
                    faction.Progression.TechnologyUnlocks = faction.Progression.TechnologyUnlocks ?? new List<string>();
                    foreach (var starter in _state.TechnologyDefinitions.Where(t => t != null && t.Starter))
                        if (!faction.Progression.TechnologyUnlocks.Any(id => string.Equals(id, starter.Id, StringComparison.OrdinalIgnoreCase)))
                            faction.Progression.TechnologyUnlocks.Add(starter.Id.Trim());
                }
                PersistLocked();
            }
        }

        public bool TouchAccount(string accountId, string displayName, DateTime nowUtc)
        {
            var normalizedId = AetherfrontDomain.NormalizeAccountId(accountId);
            if (string.IsNullOrWhiteSpace(normalizedId)) return false;
            lock (_gate)
            {
                _state.Accounts = _state.Accounts ?? new List<AetherfrontAccountRecord>();
                var account = _state.FindAccount(normalizedId);
                if (account == null)
                {
                    account = new AetherfrontAccountRecord
                    {
                        AccountId = normalizedId,
                        DisplayName = AetherfrontDomain.NormalizeDisplayName(displayName),
                        FirstSeenAtUtc = nowUtc,
                        LastSeenAtUtc = nowUtc
                    };
                    _state.Accounts.Add(account);
                }
                else
                {
                    account.DisplayName = AetherfrontDomain.NormalizeDisplayName(displayName);
                    account.LastSeenAtUtc = nowUtc;
                }
                PersistLocked();
                return !account.Suspended;
            }
        }

        private AetherfrontState Load(string serverId)
        {
            AetherfrontState loaded = null;
            Exception primaryError = null;
            foreach (var path in new[] { _statePath, _backupPath })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    loaded = DeserializeAndValidate(File.ReadAllText(path, Encoding.UTF8), serverId);
                    if (loaded != null)
                        break;
                }
                catch (Exception e)
                {
                    primaryError = e;
                    LunaLog.Warning($"Aetherfront snapshot recovery skipped '{path}': {e.Message}");
                }
            }

            if (loaded == null && File.Exists(_eventPath))
            {
                foreach (var line in File.ReadLines(_eventPath, Encoding.UTF8).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var journal = AetherfrontJson.Deserialize<JournalRecord>(line);
                        if (journal == null || string.IsNullOrWhiteSpace(journal.StateJson)) continue;
                        loaded = DeserializeAndValidate(journal.StateJson, serverId);
                        if (loaded != null)
                        {
                            LunaLog.Warning($"Aetherfront recovered revision {loaded.Revision} from journal.");
                            break;
                        }
                    }
                    catch
                    {
                        // Ignore legacy event-only lines and truncated tail records.
                    }
                }
            }

            if (loaded == null)
            {
                if (primaryError != null && File.Exists(_statePath)) throw new InvalidDataException("Aetherfront state and recovery snapshots are invalid", primaryError);
                loaded = new AetherfrontState { ServerId = serverId, SchemaVersion = AetherfrontConstants.CurrentSchemaVersion };
            }

            loaded.Factions = loaded.Factions ?? new global::System.Collections.Generic.List<FactionRecord>();
            loaded.Crafts = loaded.Crafts ?? new global::System.Collections.Generic.List<CraftRecord>();
            loaded.Trades = loaded.Trades ?? new global::System.Collections.Generic.List<TradeOffer>();
            loaded.Objectives = loaded.Objectives ?? new global::System.Collections.Generic.List<ObjectiveDefinition>();
            loaded.ObjectiveProgress = loaded.ObjectiveProgress ?? new global::System.Collections.Generic.List<ObjectiveProgress>();
            loaded.Events = loaded.Events ?? new global::System.Collections.Generic.List<AetherfrontEvent>();
            loaded.CommandReceipts = loaded.CommandReceipts ?? new global::System.Collections.Generic.List<CommandReceipt>();
            loaded.Invitations = loaded.Invitations ?? new global::System.Collections.Generic.List<FactionInvitation>();
            loaded.Applications = loaded.Applications ?? new global::System.Collections.Generic.List<FactionApplication>();
            loaded.ModeratorAccountIds = loaded.ModeratorAccountIds ?? new global::System.Collections.Generic.List<string>();
            loaded.BankPolicy = loaded.BankPolicy ?? new CentralBankPolicy();
            loaded.Loans = loaded.Loans ?? new global::System.Collections.Generic.List<LoanRecord>();
            loaded.ResearchProjects = loaded.ResearchProjects ?? new global::System.Collections.Generic.List<ResearchProjectDefinition>();
            loaded.ResearchProgress = loaded.ResearchProgress ?? new global::System.Collections.Generic.List<ResearchProjectProgress>();
            loaded.TechnologyDefinitions = loaded.TechnologyDefinitions ?? new global::System.Collections.Generic.List<TechnologyDefinition>();
            loaded.Accounts = loaded.Accounts ?? new global::System.Collections.Generic.List<AetherfrontAccountRecord>();
            loaded.Agreements = loaded.Agreements ?? new global::System.Collections.Generic.List<DiplomaticAgreement>();
            loaded.Stories = loaded.Stories ?? new global::System.Collections.Generic.List<StoryPublication>();
            foreach (var faction in loaded.Factions)
            {
                if (faction == null) continue;
                faction.Members = faction.Members ?? new List<FactionMember>();
                faction.Progression = faction.Progression ?? new FactionProgression();
                faction.Progression.TechnologyUnlocks = faction.Progression.TechnologyUnlocks ?? new List<string>();
                faction.Progression.CompletedContractIds = faction.Progression.CompletedContractIds ?? new List<string>();
                faction.Progression.AchievementIds = faction.Progression.AchievementIds ?? new List<string>();
                faction.Progression.LaunchedVesselIds = faction.Progression.LaunchedVesselIds ?? new List<string>();
                faction.Progression.ReachedBodyKeys = faction.Progression.ReachedBodyKeys ?? new List<string>();
                faction.Progression.RescueOperationIds = faction.Progression.RescueOperationIds ?? new List<string>();
                faction.Progression.RecoveredVesselIds = faction.Progression.RecoveredVesselIds ?? new List<string>();
                faction.Progression.ResearchProjectIds = faction.Progression.ResearchProjectIds ?? new List<string>();
            }
            foreach (var progress in loaded.ObjectiveProgress)
                if (progress != null && string.IsNullOrWhiteSpace(progress.PeriodKey)) progress.PeriodKey = "permanent";
            foreach (var progress in loaded.ResearchProgress)
                if (progress != null && string.IsNullOrWhiteSpace(progress.PeriodKey)) progress.PeriodKey = "permanent";
            foreach (var story in loaded.Stories)
            {
                if (story == null) continue;
                story.Tags = story.Tags ?? new List<string>();
                story.RelatedEventIds = story.RelatedEventIds ?? new List<string>();
                if (string.IsNullOrWhiteSpace(story.ModerationState))
                    story.ModerationState = story.Public ? StoryModerationStates.Approved : StoryModerationStates.Pending;
            }
            return loaded;
        }

        private static AetherfrontState DeserializeAndValidate(string json, string serverId)
        {
            var loaded = AetherfrontJson.Deserialize<AetherfrontState>(json);
            if (loaded == null) throw new InvalidDataException("Aetherfront state is empty");
            if (loaded.SchemaVersion != AetherfrontConstants.CurrentSchemaVersion)
                throw new InvalidDataException($"Unsupported Aetherfront state schema {loaded.SchemaVersion}; expected {AetherfrontConstants.CurrentSchemaVersion}");
            if (!string.Equals(loaded.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Aetherfront state belongs to server '{loaded.ServerId}', not '{serverId}'");
            return loaded;
        }

        private void PersistNewEventsLocked(int previousCount)
        {
            if (_state.Events.Count <= previousCount) return;
            using (var stream = new FileStream(_eventPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                var record = new JournalRecord
                {
                    Revision = _state.Revision,
                    StateJson = AetherfrontJson.Serialize(_state),
                    Events = _state.Events.Skip(previousCount).ToList()
                };
                writer.WriteLine(AetherfrontJson.Serialize(record));
                writer.Flush();
                stream.Flush(true);
            }
        }

        private void PersistLocked()
        {
            var tempPath = _statePath + ".tmp";
            File.WriteAllText(tempPath, AetherfrontJson.Serialize(_state), new UTF8Encoding(false));
            using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                stream.Flush(true);
            if (File.Exists(_statePath))
                File.Copy(_statePath, _backupPath, true);
            File.Move(tempPath, _statePath, true);
        }

        [DataContract]
        private sealed class JournalRecord
        {
            [DataMember(Order = 0)] public long Revision { get; set; }
            [DataMember(Order = 1)] public string StateJson { get; set; } = string.Empty;
            [DataMember(Order = 2)] public List<AetherfrontEvent> Events { get; set; } = new List<AetherfrontEvent>();
        }
    }
}
