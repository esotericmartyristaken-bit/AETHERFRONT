using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Aetherfront.Continuity;
using Aetherfront.Continuity.Server;
using LmpCommon;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Server;
using Server.System;
using StoredVessel = Server.System.Vessel.Classes.Vessel;

namespace Aetherfront.Personal.Server
{
    // These native access points are checked against the pinned shipping runtime
    // by the release tests. Ordinary packet handling keeps its existing guards.
    internal static class FlightCheckpointRuntime
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Assembly Native = typeof(AetherfrontServerSystem).Assembly;
        private static readonly Type Fleet = Native.GetType("Server.Aetherfront.AetherfrontFleetGuard", true);
        private static readonly Type Freeze = Native.GetType("Server.Aetherfront.AetherfrontDeepFreezeSystem", true);
        private static readonly Type Natural = Native.GetType("Server.System.Vessel.VesselNaturalAuthority", true);
        internal static object Gate => Fleet.GetField("Gate", Static).GetValue(null);

        private static object Call(Type type, string method, object target, params object[] arguments)
        {
            try { return type.GetMethod(method, target == null ? Static : Instance).Invoke(target, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        internal static DeepFreezeScenarioCodec.Snapshot FreezeSnapshot()
        {
            return (DeepFreezeScenarioCodec.Snapshot)Call(Freeze, "Snapshot", null) ?? EmptyFreeze();
        }

        internal static DeepFreezeScenarioCodec.Snapshot EmptyFreeze() =>
            new DeepFreezeScenarioCodec.Snapshot("DeepFreeze", new[] { "5", "6", "7", "8" }, true, null, null, null, null);

        internal static DeepFreezeScenarioCodec.Snapshot ProjectFreeze(DeepFreezeScenarioCodec.Snapshot source, HashSet<Guid> ids)
        {
            return new DeepFreezeScenarioCodec.Snapshot(source.ScenarioName, source.Scenes, source.Enabled,
                source.Kerbals.Values.Where(crew => ids.Contains(crew.VesselGuid)),
                source.Vessels.Values.Where(vessel => ids.Contains(vessel.VesselGuid)),
                source.Parts.Values.Where(part => ids.Contains(part.VesselGuid)),
                source.Alarms.Values.Where(alarm => ids.Contains(alarm.VesselGuid)));
        }

        internal static IEnumerable<string> Crew(StoredVessel vessel) => vessel.Parts.GetAllValues()
            .SelectMany(part => part.Fields.GetSeveral("crew").Select(value => value.Value));

        internal static void RequireOrdinaryFleet(HashSet<Guid> ids)
        {
            Call(Natural, "Load", null);
            object state = Natural.GetField("_state", Static).GetValue(null);
            var vessels = (IEnumerable)state.GetType().GetProperty("Vessels").GetValue(state);
            foreach (object vessel in vessels)
            {
                Type type = vessel.GetType();
                Guid id = (Guid)type.GetProperty("Id").GetValue(vessel);
                Guid parent = (Guid)type.GetProperty("ParentId").GetValue(vessel);
                Guid detached = (Guid)type.GetProperty("DetachedFromParentId").GetValue(vessel);
                if (ids.Contains(id) || ids.Contains(parent) || ids.Contains(detached))
                    throw new InvalidOperationException("Checkpoints currently require a fleet without captured asteroids or comets.");
            }
        }

        internal static void CommitWorld(PersonalFlightCheckpoints.RestoreTransaction transaction)
        {
            var snapshot = transaction.Snapshot;
            var writes = snapshot.Vessels.ToDictionary(vessel => vessel.Id, vessel => Encoding.UTF8.GetBytes(vessel.Proto));
            var targetIds = new HashSet<Guid>(writes.Keys);
            var replacedIds = new HashSet<Guid>(targetIds.Concat(transaction.DeleteVessels));
            var crewWrites = snapshot.Crew.Concat(transaction.ReleasedCrew).ToDictionary(crew => crew.Name,
                crew => Encoding.UTF8.GetBytes(crew.Config), StringComparer.OrdinalIgnoreCase);
            var store = new AetherfrontDeepFreezeCheckpointStore(ServerContext.UniverseDirectory);
            var previous = store.Recover();
            var current = previous.ScenarioData.Length == 0 ? EmptyFreeze() :
                DeepFreezeScenarioCodec.Parse(previous.ScenarioData, previous.ScenarioData.Length);
            byte[] savedBytes = Encoding.UTF8.GetBytes(snapshot.FreezeScenario);
            var saved = DeepFreezeScenarioCodec.Parse(savedBytes, savedBytes.Length);
            var crewNames = new HashSet<string>(crewWrites.Keys, StringComparer.OrdinalIgnoreCase);
            if (current.Kerbals.Values.Any(crew => crewNames.Contains(crew.KerbalName) && !replacedIds.Contains(crew.VesselGuid)))
                throw new InvalidOperationException("A checkpoint crew member is aboard another player's vessel.");
            var merged = new DeepFreezeScenarioCodec.Snapshot(current.ScenarioName, current.Scenes, current.Enabled,
                current.Kerbals.Values.Where(crew => !replacedIds.Contains(crew.VesselGuid) && !crewNames.Contains(crew.KerbalName)).Concat(saved.Kerbals.Values),
                current.Vessels.Values.Where(vessel => !replacedIds.Contains(vessel.VesselGuid)).Concat(saved.Vessels.Values),
                current.Parts.Values.Where(part => !replacedIds.Contains(part.VesselGuid)).Concat(saved.Parts.Values),
                current.Alarms.Values.Where(alarm => !replacedIds.Contains(alarm.VesselGuid)).Concat(saved.Alarms.Values));
            byte[] scenario = DeepFreezeScenarioCodec.Serialize(merged);
            var committed = store.Commit(previous.Revision, scenario, writes, crewWrites, transaction.DeleteVessels);
            foreach (Guid id in transaction.DeleteVessels) VesselStoreSystem.CurrentVessels.TryRemove(id, out _);
            foreach (var vessel in snapshot.Vessels)
            {
                VesselStoreSystem.CurrentVessels[vessel.Id] = new StoredVessel(vessel.Proto);
                VesselContext.RemovedVessels.TryRemove(vessel.Id, out _);
            }
            // Preserve revision guards for every other pilot. Reloading the
            // coordinator would discard the existing per-crew session guards.
            object coordinator = Freeze.GetField("coordinator", Static).GetValue(null);
            if (coordinator != null)
            {
                Type type = coordinator.GetType();
                type.GetField("current", Instance).SetValue(coordinator, merged);
                type.GetField("scenario", Instance).SetValue(coordinator, scenario);
                type.GetField("revision", Instance).SetValue(coordinator, committed.Revision);
                var crewRevisions = (Dictionary<string, long>)type.GetField("crewRevisions", Instance).GetValue(coordinator);
                var vesselRevisions = (Dictionary<Guid, long>)type.GetField("vesselRevisions", Instance).GetValue(coordinator);
                foreach (string name in crewWrites.Keys) crewRevisions[name] = committed.Revision;
                foreach (Guid id in replacedIds) vesselRevisions[id] = committed.Revision;
                type.GetProperty("RequiresRestart", Instance).SetValue(coordinator, false);
            }
            var reader = Native.GetType("Server.Message.VesselMsgReader", true);
            var subspaces = (IDictionary<Guid, int>)reader.GetField("VesselSubspaces", Static).GetValue(null);
            foreach (Guid id in replacedIds) subspaces.Remove(id);
            foreach (Guid id in transaction.DeleteVessels) VesselContext.RemovedVessels.TryAdd(id, 0);
        }

        internal static void RestoreTime(string owner, double missionTime)
        {
            ClockStore.ValidateTime(missionTime);
            var stores = (Dictionary<string, ClockStore>)typeof(PersonalClockServer).GetField("Stores", Static).GetValue(null);
            string universe = Path.GetFullPath(ServerContext.UniverseDirectory);
            if (!stores.TryGetValue(universe, out var store))
                stores.Add(universe, store = new ClockStore(universe));
            Type type = typeof(ClockStore);
            lock (type.GetField("gate", Instance).GetValue(store))
            {
                Call(type, "EnsureSafeRoot", store);
                string key = (string)Call(type, "AccountKey", null, owner);
                object[] record = { key, 0d, 0L };
                bool exists = (bool)Call(type, "Read", store, record);
                if (!exists) throw new InvalidDataException("The saved personal clock is missing.");
                if ((double)record[1] == missionTime) return;
                Call(type, "Write", store, key, missionTime, checked((long)record[2] + 1));
            }
        }

        internal static void RequireInactiveClock(string owner)
        {
            var sessions = (IDictionary)typeof(PersonalClockServer).GetField("Sessions", Static).GetValue(null);
            foreach (DictionaryEntry entry in sessions)
                if ((string)entry.Value.GetType().GetField("Account", Instance).GetValue(entry.Value) == owner)
                    throw new InvalidOperationException("The pilot's previous clock session is still closing.");
        }

        internal static void RequireRecovery(bool required)
        {
            Freeze.GetField("recoveryRequired", Static).SetValue(null, required);
        }

        internal static void PublishRestored(PersonalFlightCheckpoints.RestoreTransaction transaction)
        {
            var interest = Native.GetType("Server.Aetherfront.AetherfrontVesselInterest", true);
            var changed = transaction.Snapshot.Vessels.Select(vessel => vessel.Id).Concat(transaction.DeleteVessels).Distinct().ToArray();
            foreach (Guid id in changed) Call(interest, "Remove", null, id);
            foreach (var client in ServerContext.Clients.Values.Where(client => client.Authenticated && !client.DisconnectClient))
                foreach (Guid id in changed)
                {
                    var removal = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
                    removal.VesselId = id;
                    removal.AddToKillList = false;
                    removal.Reason = "Personal checkpoint reload";
                    MessageQueuer.SendToClient<VesselSrvMsg>(client, removal);
                }
            // Peers fetch visible replacement protos through the native paced
            // sync path after removing their old copies.
        }

        internal static void AttachFleet(ClientStructure client, PersonalFlightCheckpoints.Snapshot snapshot)
        {
            var reader = Native.GetType("Server.Message.VesselMsgReader", true);
            var subspaces = (IDictionary<Guid, int>)reader.GetField("VesselSubspaces", Static).GetValue(null);
            var bodies = (IDictionary<Guid, string>)reader.GetField("VesselBodyNames", Static).GetValue(null);
            var interest = Native.GetType("Server.Aetherfront.AetherfrontVesselInterest", true);
            foreach (var saved in snapshot.Vessels)
            {
                if (!VesselStoreSystem.CurrentVessels.TryGetValue(saved.Id, out var vessel))
                    throw new InvalidDataException("A restored vessel is missing during resume.");
                string name = vessel.Fields.GetSingle("name")?.Value ?? saved.Id.ToString();
                string body = vessel.GetOrbitingBodyName();
                string situation = vessel.Fields.GetSingle("sit")?.Value;
                subspaces[saved.Id] = client.Subspace;
                bodies[saved.Id] = body;
                Call(interest, "UpdateProto", null, client, saved.Id, name, client.Subspace);
                Call(interest, "UpdatePosition", null, client, saved.Id, body, client.Subspace,
                    situation == "LANDED" || situation == "PRELAUNCH", situation == "SPLASHED");
            }
        }
    }
}
