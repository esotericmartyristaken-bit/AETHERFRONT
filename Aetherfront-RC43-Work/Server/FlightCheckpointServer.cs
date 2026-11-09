using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Aetherfront.Continuity.Server;
using Aetherfront.Core;
using LmpCommon;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using LunaConfigNode;
using LunaConfigNode.CfgNode;
using Server.Aetherfront;
using Server.Apf;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using StoredVessel = Server.System.Vessel.Classes.Vessel;

namespace Aetherfront.Personal.Server
{
    public static class FlightCheckpointServer
    {
        private static Timer recoveryTimer;
        private static Timer automaticTimer;
        private static int automaticBusy;
        private static volatile bool restoreRequiresRecovery;
        private sealed class AutomaticState
        {
            internal ClientStructure Client;
            internal DateTime Due;
            internal string Error;
        }
        private static readonly Dictionary<string, AutomaticState> Automatic = new Dictionary<string, AutomaticState>(StringComparer.Ordinal);
        internal static Action<string> BeforeRestorePhase { get; set; }
        private static readonly Dictionary<string, DateTime> NextSave = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly PropertyInfo[] CraftProperties = typeof(CraftRecord).GetProperties().Where(property => property.CanRead && property.CanWrite).ToArray();
        private static PersonalFlightCheckpoints Store => new PersonalFlightCheckpoints(ServerContext.UniverseDirectory);

        private sealed class ReceiptEnvelope
        {
            public int ProtocolVersion { get; set; } = 1;
            public int Operation { get; set; } = (int)AetherfrontWireOperation.CommandReply;
            public bool Accepted { get; set; }
            public string ReasonCode { get; set; }
            public long Revision { get; set; }
            public string ServerId { get; set; }
            public CheckpointInfo FlightCheckpoint { get; set; }
        }

        private sealed class CheckpointInfo
        {
            public string RequestId { get; set; }
            public string CheckpointId { get; set; }
            public string SavedUtc { get; set; }
            public double MissionTime { get; set; }
            public int VesselCount { get; set; }
            public long Generation { get; set; }
            public string LastRestoreId { get; set; }
            public List<RestorePoint> Points { get; set; }
            public string AutomaticStatus { get; set; }
        }

        private sealed class RestorePoint
        {
            public string CheckpointId { get; set; }
            public string Label { get; set; }
            public string SavedUtc { get; set; }
            public double MissionTime { get; set; }
            public int VesselCount { get; set; }
        }

        public static bool TryHandle(ClientStructure client, AetherfrontWireRequest request)
        {
            if (request != null && client != null && client.Authenticated && AetherfrontServerSystem.Enabled && restoreRequiresRecovery)
            {
                var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
                data.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireReply
                {
                    Operation = AetherfrontWireOperation.CommandReply, Accepted = false,
                    ReasonCode = "WorldCheckpointRecoveryPending"
                });
                MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
                return true;
            }
            if (request == null || request.Operation != AetherfrontWireOperation.CommandRequest ||
                (int)request.CommandType < 80 || (int)request.CommandType > 82) return false;
            if (!AetherfrontServerSystem.Enabled || client == null || !client.Authenticated) return false;
            return TryHandle(client, AetherfrontServerSystem.GetBoundAccountId(client), request);
        }

        public static bool TryHandle(ClientStructure client, string actor, AetherfrontWireRequest request)
        {
            int command = (int)request.CommandType;
            if (command < 80 || command > 82) return false;
            lock (FlightCheckpointRuntime.Gate)
            {
                try
                {
                    if (!client.Authenticated || client.DisconnectClient || string.IsNullOrEmpty(actor) ||
                        AetherfrontServerSystem.GetBoundAccountId(client) != actor ||
                        !AetherfrontServerSystem.Read(state => state.FindAccount(actor)?.Suspended == false))
                        throw new InvalidOperationException("An active personal session is required.");
                    if (!Guid.TryParseExact(request.CommandId, "N", out var requestId) || requestId == Guid.Empty)
                        throw new InvalidOperationException("Invalid checkpoint request.");
                    if (Store.Pending(actor) != null) RecoverOwner(actor);
                    if (command == 80) Save(client, actor, request, requestId);
                    else if (command == 82) Restore(client, actor, request, requestId);
                    if (client.Authenticated) Reply(client, actor, request.CommandId, null);
                }
                catch (Exception error)
                {
                    LunaLog.Warning("Personal checkpoint request failed: " + error.GetType().Name + ": " + error.Message);
                    if (client.Authenticated) Reply(client, actor, request.CommandId, Friendly(error));
                }
            }
            return true;
        }

        private static string Friendly(Exception error) => error is InvalidOperationException || error is InvalidDataException
            ? error.Message : "Checkpoint storage is unavailable. Existing checkpoint data was preserved. See the server log.";

        private static void Save(ClientStructure client, string actor, AetherfrontWireRequest request, Guid requestId)
        {
            Guid previous = Guid.Empty;
            if (!string.IsNullOrEmpty(request.EntityId) && !Guid.TryParseExact(request.EntityId, "N", out previous))
                throw new InvalidOperationException("Refresh the checkpoint before saving.");
            if (Store.ReadLatest(actor)?.Id == requestId) return;
            if (NextSave.TryGetValue(actor, out var next) && DateTime.UtcNow < next)
                throw new InvalidOperationException("Wait a few seconds before saving another checkpoint.");
            if (client.Subspace < 0 || !WarpContext.Subspaces.TryGetValue(client.Subspace, out var space))
                throw new InvalidOperationException("Stop time warp before saving a checkpoint.");
            var snapshot = Capture(client, actor, requestId);
            Store.Save(snapshot, previous);
            NextSave[actor] = DateTime.UtcNow.AddSeconds(5);
            LunaLog.Normal("Personal fleet checkpoint saved: vessels=" + snapshot.Vessels.Count + " UT=" + snapshot.MissionTime.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Track(ClientStructure client)
        {
            string actor = AetherfrontServerSystem.GetBoundAccountId(client);
            if (string.IsNullOrEmpty(actor)) return;
            if (!Automatic.TryGetValue(actor, out var state)) Automatic.Add(actor, state = new AutomaticState { Due = DateTime.UtcNow.AddSeconds(5) });
            state.Client = client;
            if (automaticTimer == null) automaticTimer = new Timer(_ => CaptureAutomatic(), null, 1000, 1000);
        }

        private static void CaptureAutomatic()
        {
            if (Interlocked.CompareExchange(ref automaticBusy, 1, 0) != 0) return;
            AutomaticState selected = null;
            try
            {
                PersonalFlightCheckpoints.Snapshot snapshot;
                PersonalFlightCheckpoints storage;
                if (!Monitor.TryEnter(FlightCheckpointRuntime.Gate)) return;
                try
                {
                    foreach (string actor in Automatic.Keys.ToArray())
                    {
                        var client = Automatic[actor].Client;
                        if (!client.Authenticated || client.DisconnectClient ||
                            !ServerContext.Clients.TryGetValue(client.Endpoint, out var current) || !ReferenceEquals(current, client))
                        { Automatic.Remove(actor); NextSave.Remove(actor); }
                    }
                    if (Automatic.Count == 0) { automaticTimer?.Dispose(); automaticTimer = null; return; }
                    var due = Automatic.OrderBy(pair => pair.Value.Due).First();
                    selected = due.Value;
                    if (selected.Due > DateTime.UtcNow) return;
                    if (selected.Client.Subspace < 0) { selected.Due = DateTime.UtcNow.AddSeconds(5); return; }
                    selected.Due = DateTime.UtcNow.AddMinutes(1);
                    snapshot = Capture(selected.Client, due.Key, Guid.NewGuid());
                    storage = Store;
                }
                finally { Monitor.Exit(FlightCheckpointRuntime.Gate); }
                // Compression and disk retention run outside the world gate.
                storage.SaveTimed(snapshot, false);
                lock (FlightCheckpointRuntime.Gate) selected.Error = null;
            }
            catch (Exception error)
            {
                lock (FlightCheckpointRuntime.Gate)
                {
                    if (selected != null && selected.Error != Friendly(error))
                        LunaLog.Warning("Automatic personal checkpoint paused: " + error.GetType().Name + ": " + error.Message);
                    if (selected != null) selected.Error = Friendly(error);
                }
            }
            finally { Interlocked.Exchange(ref automaticBusy, 0); }
        }

        public static void ObserveProto(ClientStructure client, VesselBaseMsgData message)
        {
            if (!(message is VesselProtoMsgData proto) || string.IsNullOrEmpty(proto.Reason) ||
                !proto.Reason.EndsWith("Launch", StringComparison.OrdinalIgnoreCase) || !client.Authenticated || client.DisconnectClient) return;
            lock (FlightCheckpointRuntime.Gate)
            {
                try
                {
                    string actor = AetherfrontServerSystem.GetBoundAccountId(client);
                    if (Store.ReadLaunch(actor)?.LaunchVesselId == proto.VesselId) return;
                    var snapshot = Capture(client, actor, Guid.NewGuid());
                    if (!snapshot.Vessels.Any(vessel => vessel.Id == proto.VesselId)) return;
                    snapshot.LaunchVesselId = proto.VesselId;
                    Store.SaveTimed(snapshot, true);
                    Track(client);
                    Automatic[actor].Due = DateTime.UtcNow.AddMinutes(1);
                }
                catch (Exception error) { LunaLog.Warning("Launch checkpoint could not be saved: " + error.GetType().Name + ": " + error.Message); }
            }
        }

        private static PersonalFlightCheckpoints.Snapshot Capture(ClientStructure client, string actor, Guid requestId)
        {
            if (client.Subspace < 0 || !WarpContext.Subspaces.TryGetValue(client.Subspace, out var space))
                throw new InvalidOperationException("Stop time warp before saving a checkpoint.");
            return AetherfrontServerSystem.Read(state =>
            {
                var owned = Owned(state, actor);
                var ids = new HashSet<Guid>(owned.Select(craft => Guid.Parse(craft.Id)));
                RequireAvailableFleet(client, state, actor, owned);
                FlightCheckpointRuntime.RequireOrdinaryFleet(ids);
                var captured = new PersonalFlightCheckpoints.Snapshot
                {
                    Id = requestId, Owner = actor, CapturedUtc = DateTimeOffset.UtcNow,
                    MissionTime = PersonalClockServer.ServerSeconds + space.Time,
                    GameMode = GeneralSettings.SettingsStore.GameMode.ToString(), PackStamp = PackStamp(),
                    RegistrationsJson = AetherfrontJson.Serialize(owned), ProgressionStamp = ProgressionStamp(state, actor)
                };
                var crewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var craft in owned.Where(craft => string.IsNullOrEmpty(craft.DockedToCraftId)))
                {
                    Guid id = Guid.Parse(craft.Id);
                    string proto = VesselStoreSystem.GetVesselInConfigNodeFormat(id);
                    if (string.IsNullOrEmpty(proto) || VesselContext.RemovedVessels.ContainsKey(id))
                        throw new InvalidOperationException("Your complete fleet has not finished synchronizing. Try again shortly.");
                    captured.Vessels.Add(new PersonalFlightCheckpoints.VesselSnapshot { Id = id, Proto = proto });
                    crewNames.UnionWith(FlightCheckpointRuntime.Crew(new StoredVessel(proto)));
                }
                var frozen = FlightCheckpointRuntime.ProjectFreeze(FlightCheckpointRuntime.FreezeSnapshot(), ids);
                crewNames.UnionWith(frozen.Kerbals.Keys);
                captured.FreezeScenario = Encoding.UTF8.GetString(DeepFreezeScenarioCodec.Serialize(frozen));
                foreach (string name in crewNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    captured.Crew.Add(ReadCrew(name));
                return captured;
            });
        }

        private static List<CraftRecord> Owned(AetherfrontState state, string actor) => state.Crafts
            .Where(craft => !craft.Archived && craft.OwnerAccountId == actor).OrderBy(craft => craft.Id, StringComparer.Ordinal).ToList();

        private static void RequireAvailableFleet(ClientStructure client, AetherfrontState state, string actor, List<CraftRecord> owned)
        {
            var ids = new HashSet<string>(owned.Select(craft => craft.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var craft in owned)
            {
                if (!string.IsNullOrEmpty(craft.DockedToCraftId) && !ids.Contains(craft.DockedToCraftId))
                    throw new InvalidOperationException("Undock from other players' craft before using a personal checkpoint.");
                string controller = global::Server.System.LockSystem.LockQuery.GetControlLockOwner(Guid.Parse(craft.Id));
                if (!string.IsNullOrEmpty(controller) && controller != client.PlayerName)
                    throw new InvalidOperationException("Another player is controlling part of your fleet. Ask them to leave the craft first.");
            }
            if (state.Crafts.Any(craft => !craft.Archived && craft.OwnerAccountId != actor && ids.Contains(craft.DockedToCraftId ?? string.Empty)))
                throw new InvalidOperationException("Undock other players' craft before using a personal checkpoint.");
        }

        private static PersonalFlightCheckpoints.CrewSnapshot ReadCrew(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Invalid crew member in the stored fleet.");
            string path = Path.Combine(KerbalSystem.KerbalsPath, name + ".txt");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length < 1 || stream.Length > 65536) throw new InvalidDataException("Stored crew record exceeds its limit.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return new PersonalFlightCheckpoints.CrewSnapshot { Name = name, Config = reader.ReadToEnd() };
        }

        private static string ProgressionStamp(AetherfrontState state, string actor)
        {
            var player = PersonalProgression.Find(state, actor) ?? throw new InvalidOperationException("Personal progression is still synchronizing.");
            // Subject science keeps its existing cumulative receipts. Anonymous
            // mod awards, trades, recovery funds and purchases cannot be replayed.
            var image = new StringBuilder().Append(player.NextCommandSequence.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(player.FundsMicro.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var award in player.ScienceSources.Where(source => source.Id.StartsWith("award:", StringComparison.Ordinal))
                .OrderBy(source => source.Id, StringComparer.Ordinal))
                image.Append(award.Id).Append('=').Append(award.CumulativeScience.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return Hash(image.ToString());
        }

        private static string PackStamp() => Hash(ApfServerSettings.ReadManifestJson() + "\n" + ApfServerSettings.CareerProfileId + "\n" + GeneralSettings.SettingsStore.GameMode);
        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static void Restore(ClientStructure client, string actor, AetherfrontWireRequest request, Guid requestId)
        {
            var receipt = Store.Receipt(actor);
            if (receipt.RequestId == requestId) return;
            if (request.Amount != receipt.Generation) throw new InvalidOperationException("The checkpoint changed. Refresh before loading it.");
            if (!Guid.TryParseExact(request.EntityId, "N", out var checkpoint)) throw new InvalidOperationException("Select a saved personal checkpoint.");
            var snapshot = Store.Read(actor, checkpoint) ?? throw new InvalidOperationException("The selected checkpoint has been replaced.");
            if (snapshot.GameMode != GeneralSettings.SettingsStore.GameMode.ToString() || snapshot.PackStamp != PackStamp())
                throw new InvalidOperationException("This checkpoint belongs to an earlier server mode or modpack. Save a new checkpoint.");
            PersonalClockServer.Checkpoint(client);
            if (client.DisconnectClient) throw new InvalidOperationException("Your personal clock could not be checkpointed for loading.");
            var transaction = AetherfrontServerSystem.Read(state => Plan(client, state, actor, snapshot, requestId, receipt.Generation));
            Store.BeginRestore(transaction);
            // The native disconnect checkpoints the current clock and releases
            // all pilot locks before any saved world data is applied.
            try
            {
                MessageQueuer.SendConnectionEnd(client, "Loading your personal checkpoint. Connect again to resume your saved fleet and mission time.");
                client.Authenticated = false;
                client.DisconnectClient = true;
                Apply(transaction);
            }
            catch
            {
                client.Authenticated = false;
                client.DisconnectClient = true;
                FlightCheckpointRuntime.RequireRecovery(true);
                restoreRequiresRecovery = true;
                StartRecoveryTimer();
                throw;
            }
        }

        private static PersonalFlightCheckpoints.RestoreTransaction Plan(ClientStructure client, AetherfrontState state, string actor,
            PersonalFlightCheckpoints.Snapshot snapshot, Guid requestId, long generation)
        {
            if (snapshot.ProgressionStamp != ProgressionStamp(state, actor))
                throw new InvalidOperationException("Trading, purchases, funds changes or laboratory awards occurred after this checkpoint. Save a new checkpoint.");
            var savedCraft = AetherfrontJson.Deserialize<List<CraftRecord>>(snapshot.RegistrationsJson);
            var savedIds = new HashSet<string>(savedCraft.Select(craft => craft.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var craft in savedCraft)
            {
                var current = state.FindCraft(craft.Id);
                if (craft.OwnerAccountId != actor || current == null || current.OwnerAccountId != actor)
                    throw new InvalidOperationException("Craft ownership changed after this checkpoint.");
                if (!current.Archived && (current.OwnerFactionId != craft.OwnerFactionId ||
                    AetherfrontJson.Serialize(current.AccessGrants) != AetherfrontJson.Serialize(craft.AccessGrants) ||
                    AetherfrontJson.Serialize(current.MemberPermissions) != AetherfrontJson.Serialize(craft.MemberPermissions)))
                    throw new InvalidOperationException("Craft sharing changed after this checkpoint.");
            }
            var owned = Owned(state, actor);
            RequireAvailableFleet(client, state, actor, owned);
            if (owned.Any(craft => !savedIds.Contains(craft.Id) && craft.CreatorAccountId != actor))
                throw new InvalidOperationException("A craft was received from another player after this checkpoint.");
            var affected = new HashSet<Guid>(owned.Select(craft => Guid.Parse(craft.Id)).Concat(snapshot.Vessels.Select(vessel => vessel.Id)));
            FlightCheckpointRuntime.RequireOrdinaryFleet(affected);
            var savedCrew = new HashSet<string>(snapshot.Crew.Select(crew => crew.Name), StringComparer.OrdinalIgnoreCase);
            var savedParts = new HashSet<uint>(snapshot.Vessels.SelectMany(vessel => new StoredVessel(vessel.Proto).Parts.GetAll().Select(part => part.Key)));
            foreach (var pair in VesselStoreSystem.CurrentVessels)
            {
                var craft = state.FindCraft(pair.Key.ToString());
                if (craft?.OwnerAccountId == actor) continue;
                if (FlightCheckpointRuntime.Crew(pair.Value).Any(savedCrew.Contains) || pair.Value.Parts.GetAll().Any(part => savedParts.Contains(part.Key)))
                    throw new InvalidOperationException("Checkpoint crew or parts are now aboard another player's craft.");
            }
            var transaction = new PersonalFlightCheckpoints.RestoreTransaction
            {
                RequestId = requestId, ExpectedGeneration = generation, Snapshot = snapshot,
                DeleteVessels = owned.Select(craft => Guid.Parse(craft.Id)).Where(id => !snapshot.Vessels.Any(vessel => vessel.Id == id)).ToList()
            };
            var releasedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Guid id in affected)
                if (VesselStoreSystem.CurrentVessels.TryGetValue(id, out var vessel))
                    releasedNames.UnionWith(FlightCheckpointRuntime.Crew(vessel));
            releasedNames.ExceptWith(savedCrew);
            foreach (string name in releasedNames)
            {
                var crew = ReadCrew(name);
                var node = new ConfigNode(crew.Config);
                node.UpdateValue("state", "Available");
                crew.Config = CfgNodeWriter.WriteConfigNode(node);
                transaction.ReleasedCrew.Add(crew);
            }
            return transaction;
        }

        private static void Apply(PersonalFlightCheckpoints.RestoreTransaction transaction)
        {
            FlightCheckpointRuntime.RequireInactiveClock(transaction.Snapshot.Owner);
            var receipt = Store.Receipt(transaction.Snapshot.Owner);
            if (receipt.Generation == transaction.ExpectedGeneration + 1 && receipt.RequestId == transaction.RequestId)
            {
                Store.FinishRestore(transaction);
                FlightCheckpointRuntime.RequireRecovery(false);
                restoreRequiresRecovery = false;
                return;
            }
            AetherfrontServerSystem.Read(state =>
            {
                var records = AetherfrontJson.Deserialize<List<CraftRecord>>(transaction.Snapshot.RegistrationsJson);
                if (records.Any(craft => state.FindCraft(craft.Id)?.OwnerAccountId != transaction.Snapshot.Owner) ||
                    transaction.DeleteVessels.Any(id => state.FindCraft(id.ToString())?.OwnerAccountId != transaction.Snapshot.Owner))
                    throw new InvalidDataException("Craft ownership changed before restore recovery.");
                return true;
            });
            BeforeRestorePhase?.Invoke("world");
            FlightCheckpointRuntime.CommitWorld(transaction);
            BeforeRestorePhase?.Invoke("registry");
            var saved = AetherfrontJson.Deserialize<List<CraftRecord>>(transaction.Snapshot.RegistrationsJson);
            AetherfrontServerSystem.Execute(state =>
            {
                foreach (var craft in saved)
                    if (state.FindCraft(craft.Id)?.OwnerAccountId != transaction.Snapshot.Owner)
                        throw new InvalidDataException("Craft ownership changed during restore recovery.");
                var ids = new HashSet<string>(saved.Select(craft => craft.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var craft in state.Crafts.Where(craft => craft.OwnerAccountId == transaction.Snapshot.Owner && !ids.Contains(craft.Id)))
                { craft.Archived = true; craft.AccessGrants.Clear(); }
                foreach (var craft in saved)
                {
                    var current = state.FindCraft(craft.Id);
                    long revision = checked(Math.Max(current.Revision, craft.Revision) + 1);
                    foreach (var property in CraftProperties) property.SetValue(current, property.GetValue(craft));
                    current.Revision = revision;
                }
                state.Revision = checked(state.Revision + 1);
                return AetherfrontResult.Success(state.Revision);
            });
            BeforeRestorePhase?.Invoke("clock");
            FlightCheckpointRuntime.RestoreTime(transaction.Snapshot.Owner, transaction.Snapshot.MissionTime);
            BeforeRestorePhase?.Invoke("receipt");
            Store.FinishRestore(transaction);
            FlightCheckpointRuntime.RequireRecovery(false);
            restoreRequiresRecovery = false;
            try { FlightCheckpointRuntime.PublishRestored(transaction); }
            catch (Exception error) { LunaLog.Warning("Checkpoint was restored; peer refresh will resume through vessel synchronization: " + error.GetType().Name); }
            LunaLog.Normal("Personal fleet checkpoint restored: vessels=" + transaction.Snapshot.Vessels.Count + " UT=" + transaction.Snapshot.MissionTime.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void RecoverOwner(string actor)
        {
            var pending = Store.Pending(actor);
            if (pending != null) Apply(pending);
        }

        public static bool RecoverBeforeResume(ClientStructure client)
        {
            if (!AetherfrontServerSystem.Enabled || client == null || !client.Authenticated) return true;
            lock (FlightCheckpointRuntime.Gate)
            {
                try
                {
                    // Complete journals left by a process restart before any
                    // player resumes, even when the restoring pilot is offline.
                    foreach (string owner in Store.PendingOwners().ToArray()) RecoverOwner(owner);
                    return true;
                }
                catch (Exception error)
                {
                    FlightCheckpointRuntime.RequireRecovery(true);
                    restoreRequiresRecovery = true;
                    StartRecoveryTimer();
                    LunaLog.Error("Personal restore recovery pending: " + error.GetType().Name + ": " + error.Message);
                    MessageQueuer.SendConnectionEnd(client, "Your checkpoint restore is still recovering. Try connecting again shortly.");
                    return false;
                }
            }
        }

        public static void AttachAfterResume(ClientStructure client)
        {
            if (!AetherfrontServerSystem.Enabled || !client.Authenticated || client.DisconnectClient || client.Subspace < 0) return;
            lock (FlightCheckpointRuntime.Gate)
            {
                try
                {
                    string actor = AetherfrontServerSystem.GetBoundAccountId(client);
                    if (string.IsNullOrEmpty(actor)) return;
                    Track(client);
                    var receipt = Store.Receipt(actor);
                    if (!receipt.NeedsRehome) return;
                    var snapshot = Store.Read(actor, receipt.CheckpointId) ?? throw new InvalidDataException("Restored checkpoint is unavailable.");
                    FlightCheckpointRuntime.AttachFleet(client, snapshot);
                    Store.MarkRehomed(receipt);
                }
                catch (Exception error)
                {
                    LunaLog.Error("Personal fleet resume failed: " + error.GetType().Name + ": " + error.Message);
                    MessageQueuer.SendConnectionEnd(client, "Your restored fleet could not finish synchronizing. Connect again to retry.");
                }
            }
        }

        private static void StartRecoveryTimer()
        {
            if (recoveryTimer == null) recoveryTimer = new Timer(_ => RecoverPending(), null, 1000, 5000);
        }

        private static void RecoverPending()
        {
            lock (FlightCheckpointRuntime.Gate)
            {
                try
                {
                    foreach (string actor in Store.PendingOwners().ToArray()) RecoverOwner(actor);
                    recoveryTimer?.Dispose(); recoveryTimer = null;
                }
                catch (Exception error) { LunaLog.Warning("Personal checkpoint recovery will retry: " + error.GetType().Name); }
            }
        }

        private static void Reply(ClientStructure client, string actor, string requestId, string error)
        {
            try { ReplyStored(client, actor, requestId, error); }
            catch (Exception storageError)
            {
                // A damaged optional history must not escape into the ordinary
                // network dispatcher or prevent the pilot from staying joined.
                LunaLog.Warning("Checkpoint metadata unavailable: " + storageError.GetType().Name);
                var envelope = AetherfrontServerSystem.Read(state => new ReceiptEnvelope { Revision = state.Revision, ServerId = state.ServerId });
                envelope.Accepted = false;
                envelope.ReasonCode = "Checkpoint storage is unavailable. Existing data was preserved. Ask the server operator to review the log.";
                envelope.FlightCheckpoint = new CheckpointInfo { RequestId = requestId, CheckpointId = string.Empty, Points = new List<RestorePoint>() };
                var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
                data.PayloadJson = AetherfrontJson.Serialize(envelope);
                MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
            }
        }

        private static void ReplyStored(ClientStructure client, string actor, string requestId, string error)
        {
            var snapshot = Store.ReadLatest(actor);
            var receipt = Store.Receipt(actor);
            var envelope = AetherfrontServerSystem.Read(state => new ReceiptEnvelope { Revision = state.Revision, ServerId = state.ServerId });
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
            envelope.Accepted = error == null;
            envelope.ReasonCode = error ?? string.Empty;
            envelope.FlightCheckpoint = new CheckpointInfo
            {
                RequestId = requestId, CheckpointId = snapshot?.Id.ToString("N") ?? string.Empty,
                SavedUtc = snapshot?.CapturedUtc.ToString("O") ?? string.Empty, MissionTime = snapshot?.MissionTime ?? 0,
                VesselCount = snapshot?.Vessels.Count ?? 0, Generation = receipt.Generation,
                LastRestoreId = receipt.RequestId.ToString("N"),
                Points = Store.Available(actor, DateTimeOffset.UtcNow, snapshot).Select(point => new RestorePoint
                {
                    CheckpointId = point.Id.ToString("N"), Label = point.Label, SavedUtc = point.CapturedUtc.ToString("O"),
                    MissionTime = point.MissionTime, VesselCount = point.VesselCount
                }).ToList(),
                AutomaticStatus = Automatic.TryGetValue(actor, out var automatic) && automatic.Error != null ? automatic.Error :
                    "Automatic points are collected once a minute while your personal time is synchronized."
            };
            data.PayloadJson = AetherfrontJson.Serialize(envelope);
            MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
        }
    }
}
