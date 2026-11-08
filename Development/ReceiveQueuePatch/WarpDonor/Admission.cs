using Aetherfront.Continuity.Server;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;

namespace Server.System;

internal static class WarpSystemReceiver
{
    // Called with the existing fleet gate held, before changing player or vessel
    // subspace. Both offsets share the same server time, so compare them directly.
    private static bool KeepCurrentSubspace(ClientStructure client, WarpChangeSubspaceMsgData message)
    {
        if (client.DisconnectClient)
            return true;
        if (message.PlayerName != client.PlayerName || message.Subspace < 0 ||
            !PersonalClockServer.MayPersistPosition(client) ||
            !WarpContext.Subspaces.TryGetValue(client.Subspace, out var current) ||
            !WarpContext.Subspaces.TryGetValue(message.Subspace, out var proposed) ||
            proposed.Time >= current.Time)
            return false;

        // NewSubspace addressed to the player's name is the existing local
        // selection message. Re-send the authoritative current ID and offset to
        // this player alone. Older clients already understand this reply.
        var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
        reply.SubspaceKey = current.Id;
        reply.ServerTimeDifference = current.Time;
        reply.PlayerCreator = client.PlayerName;
        MessageQueuer.SendToClient<WarpSrvMsg>(client, reply);
        LunaLog.Debug("Preserved personal mission time after a stale subspace request.");
        return true;
    }
}
