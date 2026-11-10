using System;
using System.Runtime.CompilerServices;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;
using LmpCommon.Message.Interface;

namespace Aetherfront.Stability.Server
{
    public static class WarpRetry
    {
        sealed class Boundary { internal double Requested, Canonical; internal int Id = -1; }
        static readonly ConditionalWeakTable<ClientStructure, Boundary> Boundaries = new ConditionalWeakTable<ClientStructure, Boundary>();
        // Called under the existing FleetGuard gate, BEFORE clock validation can adjust the proposal.
        public static bool TryReplay(ClientStructure client, WarpNewSubspaceMsgData request)
        {
            if (client == null || request == null || !client.Authenticated || client.DisconnectClient || request.PlayerCreator != client.PlayerName) return false;
            var boundary = Boundaries.GetValue(client, _ => new Boundary());
            if (boundary.Id >= 0 && boundary.Requested == request.ServerTimeDifference && client.Subspace <= boundary.Id &&
                WarpContext.Subspaces.TryGetValue(boundary.Id, out var space) && space.Creator == client.PlayerName && space.Time == boundary.Canonical)
            {
                var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                reply.PlayerCreator = client.PlayerName; reply.SubspaceKey = boundary.Id; reply.ServerTimeDifference = boundary.Canonical;
                MessageQueuer.SendToClient<WarpSrvMsg>(client, reply);
                return true;
            }
            boundary.Requested = request.ServerTimeDifference; boundary.Id = -1;
            return false;
        }
        public static void Record(ClientStructure client, WarpNewSubspaceMsgData request, int id)
        {
            var boundary = Boundaries.GetValue(client, _ => new Boundary());
            boundary.Id = id; boundary.Canonical = request.ServerTimeDifference;
        }
        public static void Publish(IMessageData data, ClientStructure client, WarpNewSubspaceMsgData request)
        {
            var created = (WarpNewSubspaceMsgData)data;
            Record(client, request, created.SubspaceKey);
            // Other peers learn the ID before the native association/migration broadcasts it.
            foreach (var peer in ClientRetriever.GetAuthenticatedClients())
                if (!ReferenceEquals(peer, client)) MessageQueuer.SendToClient<WarpSrvMsg>(peer, created);
            var change = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpChangeSubspaceMsgData>();
            change.PlayerName = client.PlayerName; change.Subspace = created.SubspaceKey;
            new global::Server.System.WarpSystemReceiver().HandleChangeSubspace(client, change);
            // A validated transition is server-owned even if this ACK is lost; the
            // ordinary clock checkpoint/disconnect now persists the correct epoch.
            if (!client.DisconnectClient && client.Subspace == created.SubspaceKey) MessageQueuer.SendToClient<WarpSrvMsg>(client, created);
        }
    }
}
