using System;
using System.Runtime.CompilerServices;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;
using Server.Log;

namespace Aetherfront.Synchronization.Server
{
    public static class BoundaryServer
    {
        sealed class Boundary { internal long Session, Request; internal double Proposed, Canonical; internal int Id = -1; internal bool Committed; }
        static readonly ConditionalWeakTable<ClientStructure, Boundary> Boundaries = new ConditionalWeakTable<ClientStructure, Boundary>();
        // Invoked inside the native fleet gate, before canonical clock normalization.
        public static bool TryReplay(ClientStructure client, WarpNewSubspaceMsgData request)
        {
            if (client == null || request == null || !client.Authenticated || client.DisconnectClient || request.PlayerCreator != client.PlayerName) return true;
            long session = BoundaryWire.Session(request), number = BoundaryWire.Request(request);
            var boundary = Boundaries.GetValue(client, _ => new Boundary());
            if (session == 0 || number <= 0 || (boundary.Session != 0 && session != boundary.Session))
            { MessageQueuer.SendConnectionEnd(client, "Invalid synchronization session; reconnect to resynchronize."); return true; }
            if (number < boundary.Request) return true; // Retired request; never create another timeline.
            if (number == boundary.Request)
            {
                if (request.ServerTimeDifference != boundary.Proposed)
                { MessageQueuer.SendConnectionEnd(client, "Synchronization request changed during retry."); return true; }
                if (boundary.Committed && client.Subspace == boundary.Id && boundary.Id >= 0 && WarpContext.Subspaces.TryGetValue(boundary.Id, out var space) && space.Creator == client.PlayerName && space.Time == boundary.Canonical)
                {
                    var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                    reply.PlayerCreator = client.PlayerName; reply.SubspaceKey = boundary.Id; reply.ServerTimeDifference = boundary.Canonical;
                    BoundaryWire.Set(reply, boundary.Session, boundary.Request);
                    MessageQueuer.SendToClient<WarpSrvMsg>(client, reply);
                }
                return true;
            }
            boundary.Session = session; boundary.Request = number; boundary.Proposed = request.ServerTimeDifference; boundary.Id = -1; boundary.Committed = false;
            LunaLog.Normal("[Synchronization] Identified boundary accepted; request=" + number);
            return false;
        }
        public static void Publish(IMessageData data, ClientStructure client, WarpNewSubspaceMsgData request)
        {
            var created = (WarpNewSubspaceMsgData)data;
            var boundary = Boundaries.GetValue(client, _ => new Boundary());
            if (boundary.Session != BoundaryWire.Session(request) || boundary.Request != BoundaryWire.Request(request))
                throw new InvalidOperationException("Unidentified synchronization publication.");
            boundary.Id = created.SubspaceKey; boundary.Canonical = created.ServerTimeDifference;
            BoundaryWire.Set(created, boundary.Session, boundary.Request);
            foreach (var peer in ClientRetriever.GetAuthenticatedClients())
                if (!ReferenceEquals(peer, client)) MessageQueuer.SendToClient<WarpSrvMsg>(peer, created);
            var change = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpChangeSubspaceMsgData>();
            change.PlayerName = client.PlayerName; change.Subspace = created.SubspaceKey;
            new global::Server.System.WarpSystemReceiver().HandleChangeSubspace(client, change);
            if (!client.DisconnectClient && client.Subspace == created.SubspaceKey)
            {
                boundary.Committed = true; MessageQueuer.SendToClient<WarpSrvMsg>(client, created);
                LunaLog.Normal("[Synchronization] Boundary committed and acknowledgement queued; request=" + boundary.Request + "; subspace=" + boundary.Id);
            }
        }
    }
}
