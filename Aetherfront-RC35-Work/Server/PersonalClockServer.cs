using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfront.Continuity;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Server;
using LmpCommon.Time;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Base;
using Server.Settings.Definition;

namespace Aetherfront.Continuity.Server
{
    public static class PersonalClockServer
    {
        private sealed class Session
        {
            internal string Account;
            internal string Universe;
            internal ClockStore Store;
            internal double LastStableUT;
        }
        private static readonly Dictionary<ClientStructure, Session> Sessions = new Dictionary<ClientStructure, Session>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, ClockStore> Stores = new Dictionary<string, ClockStore>(StringComparer.OrdinalIgnoreCase);

        public static double ServerSeconds => TimeUtil.TicksToSeconds(LunaNetworkTime.UtcNow.Ticks - TimeContext.StartTime.Ticks);

        // LMP normally skews a running client's physics clock for errors up to
        // 3.5 seconds. Its 1x offset can therefore trail the server's inferred
        // personal UT when warp settles (including Escape/menu transitions).
        // Normalize only a newly created OWN subspace, before broadcasting it.
        // The durable clock guard and arbitrary existing-subspace changes remain strict.
        public static string PrepareNewSubspace(ClientStructure client, WarpNewSubspaceMsgData message)
        {
            lock (AetherfrontFleetGuard.Gate)
            {
                Session session;
                if (client == null || message == null || message.PlayerCreator != client.PlayerName ||
                    !Sessions.TryGetValue(client, out session)) return null;
                if (!global::Server.System.WarpSystem.IsRepresentableTimeDifference(message.ServerTimeDifference) || WarpContext.Subspaces.IsEmpty)
                    return null; // Retain the native validation/drop behavior.
                try
                {
                    if (!client.Authenticated || client.DisconnectClient || session.Account != AetherfrontServerSystem.GetBoundAccountId(client) ||
                        !string.Equals(session.Universe, Path.GetFullPath(ServerContext.UniverseDirectory), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Clock identity or universe changed during transition");
                    Subspace oldSpace;
                    if (!WarpContext.Subspaces.TryGetValue(client.Subspace, out oldSpace))
                        throw new InvalidOperationException("Missing stable personal subspace at warp boundary");
                    Checkpoint(client);
                    if (!Sessions.ContainsKey(client)) return "Your personal clock could not be checkpointed safely.";
                    double now = ServerSeconds;
                    double minimumOffset = Math.Max(oldSpace.Time, session.LastStableUT - now);
                    double behind = minimumOffset - message.ServerTimeDifference;
                    if (behind > 3.5d)
                    {
                        LunaLog.Error("Personal subspace rollback refused: subspace=" + client.Subspace + ", seconds=" + behind.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        return "Your proposed mission time is behind your saved clock. Reconnect to restore your saved time.";
                    }
                    double canonicalOffset = Math.Max(minimumOffset, message.ServerTimeDifference);
                    ClockStore.ValidateTime(now + canonicalOffset);
                    if (behind > 0)
                    {
                        message.ServerTimeDifference = canonicalOffset;
                        LunaLog.Debug("Personal warp boundary advanced by " + behind.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                            " seconds to preserve saved time (subspace " + client.Subspace + ").");
                    }
                    return null;
                }
                catch (Exception error)
                {
                    LunaLog.Error("Personal subspace preparation refused: " + error.GetType().Name + ": " + error.Message);
                    return "Your proposed mission time could not be validated safely. Reconnect to restore your saved time.";
                }
            }
        }

        // The caller retains its existing vessel lock/permission/data validation.
        // Persistence is per authorized vessel, not reserved for the latest global time.
        public static bool MayPersistPosition(ClientStructure client)
        {
            lock (AetherfrontFleetGuard.Gate)
            {
                Session session;
                return client != null && client.Authenticated && !client.DisconnectClient &&
                    Sessions.TryGetValue(client, out session) &&
                    session.Account == AetherfrontServerSystem.GetBoundAccountId(client) &&
                    string.Equals(session.Universe, Path.GetFullPath(ServerContext.UniverseDirectory), StringComparison.OrdinalIgnoreCase) &&
                    client.Subspace >= 0 && WarpContext.Subspaces.ContainsKey(client.Subspace);
            }
        }

        public static void ResumeAndSend(ClientStructure client)
        {
            lock (AetherfrontFleetGuard.Gate)
            {
                try
                {
                    EnsureSession(client);
                    var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpSubspacesReplyMsgData>();
                    var players = ClientRetriever.GetAuthenticatedClients().ToLookup(c => c.Subspace, c => c.PlayerName);
                    reply.Subspaces = WarpContext.Subspaces.ToArray().OrderBy(p => p.Key).Select(p =>
                    {
                        // Include this client: its authenticated association is the resume selector.
                        string[] names = players[p.Key].ToArray();
                        if (p.Key == client.Subspace && !names.Contains(client.PlayerName, StringComparer.Ordinal))
                            names = names.Concat(new[] { client.PlayerName }).ToArray();
                        return new SubspaceInfo { SubspaceKey = p.Key, SubspaceTime = p.Value.Time, Players = names, PlayerCount = names.Length };
                    }).ToArray();
                    reply.SubspaceCount = reply.Subspaces.Length;
                    MessageQueuer.SendToClient<WarpSrvMsg>(client, reply);
                }
                catch (Exception error)
                {
                    LunaLog.Error("Personal time resume refused: " + error.GetType().Name + ": " + error.Message);
                    MessageQueuer.SendConnectionEnd(client, "Your saved mission time could not be restored safely. Ask the server operator to review the personal clock log.");
                }
            }
        }

        private static void EnsureSession(ClientStructure client)
        {
            if (client == null || !client.Authenticated) throw new InvalidOperationException("Authenticated client required");
            string account = AetherfrontServerSystem.GetBoundAccountId(client);
            if (string.IsNullOrWhiteSpace(account)) throw new InvalidOperationException("Bound account required");
            string universe = Path.GetFullPath(ServerContext.UniverseDirectory);
            Session existing;
            if (Sessions.TryGetValue(client, out existing))
            {
                if (existing.Account != account || !string.Equals(existing.Universe, universe, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Clock identity or universe changed during connection");
                if (!WarpContext.Subspaces.ContainsKey(client.Subspace)) throw new InvalidOperationException("Resume subspace no longer exists");
                return;
            }
            if (Sessions.Any(p => !ReferenceEquals(p.Key, client) && p.Key.Authenticated && p.Value.Account == account &&
                string.Equals(p.Value.Universe, universe, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Account already has an active clock session");
            if (WarpContext.Subspaces.IsEmpty) throw new InvalidOperationException("Server clock is not initialized");
            int maximum = Math.Max(1, SettingsBase<WarpSettingsDefinition>.SettingsStore?.MaxSubspaces ?? 4096);
            if (WarpContext.Subspaces.Count >= maximum) throw new InvalidOperationException("Server subspace limit reached");
            int id = WarpContext.NextSubspaceId;
            if (id < 0) throw new InvalidOperationException("Invalid next subspace");
            while (WarpContext.Subspaces.ContainsKey(id)) id = checked(id + 1);
            if (id == int.MaxValue) throw new InvalidOperationException("Subspace IDs exhausted");
            double now = ServerSeconds;
            ClockStore store;
            if (!Stores.TryGetValue(universe, out store)) Stores.Add(universe, store = new ClockStore(universe));
            double resumed = store.Resume(account, now + WarpContext.LatestSubspace.Time);
            double difference = resumed - now;
            if (!global::Server.System.WarpSystem.IsRepresentableTimeDifference(difference))
                throw new InvalidDataException("Unrepresentable personal subspace");
            if (!WarpContext.Subspaces.TryAdd(id, new Subspace(id, difference, client.PlayerName)))
                throw new InvalidOperationException("Subspace allocation raced");
            WarpContext.NextSubspaceId = id + 1;
            client.Subspace = id;
            Sessions.Add(client, new Session { Account = account, Universe = universe, Store = store, LastStableUT = resumed });
            var created = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
            created.SubspaceKey = id; created.ServerTimeDifference = difference; created.PlayerCreator = client.PlayerName;
            foreach (var peer in ClientRetriever.GetAuthenticatedClients())
                if (!ReferenceEquals(peer, client)) MessageQueuer.SendToClient<WarpSrvMsg>(peer, created);
            LunaLog.Debug("Personal mission time restored in subspace " + id + " at UT " + resumed.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        public static void Checkpoint(ClientStructure client)
        {
            lock (AetherfrontFleetGuard.Gate)
            {
                Session session;
                if (client == null || !Sessions.TryGetValue(client, out session)) return;
                double observed = double.NaN;
                try
                {
                    // During warp the protocol uses -1 without a stable server
                    // offset. Preserve the checkpoint made before entering warp;
                    // never advance it using somebody else's latest subspace.
                    if (client.Subspace == -1)
                    {
                        observed = session.LastStableUT;
                        session.Store.Checkpoint(session.Account, session.LastStableUT);
                        return;
                    }
                    Subspace space;
                    if (!WarpContext.Subspaces.TryGetValue(client.Subspace, out space))
                        throw new InvalidOperationException("Missing active personal subspace");
                    double current = observed = ServerSeconds + space.Time;
                    session.Store.Checkpoint(session.Account, current);
                    session.LastStableUT = current;
                }
                catch (Exception error)
                {
                    LunaLog.Error("Personal time checkpoint failed: " + error.GetType().Name + ": " + error.Message +
                        "; subspace=" + client.Subspace + "; previousUT=" + session.LastStableUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "; proposedUT=" + observed.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    // Do not continue play with an unpersistable survival clock.
                    // Remove first: SendConnectionEnd invokes the patched disconnect hook.
                    Sessions.Remove(client);
                    MessageQueuer.SendConnectionEnd(client, "Your mission time could not be saved safely. Please reconnect after the server operator reviews the clock log.");
                }
            }
        }

        public static void BeforeDisconnect(ClientStructure client)
        {
            lock (AetherfrontFleetGuard.Gate)
            {
                Checkpoint(client);
                if (client != null) Sessions.Remove(client);
            }
        }

        public static void CheckpointAll()
        {
            lock (AetherfrontFleetGuard.Gate)
                foreach (ClientStructure client in Sessions.Keys.ToArray()) Checkpoint(client);
        }
    }
}
