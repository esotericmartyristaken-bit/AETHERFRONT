using Server.Log;
using System;
using System.Threading;

namespace Server.Client
{
    internal enum StartupReplyKind
    {
        Settings,
        Kerbals,
        WarpSubspaces,
        Colors,
        Flags,
        Players,
        Scenarios,
        Locks,
        Count
    }

    /// <summary>
    /// Stock LMP retries startup requests while a large earlier response is still being
    /// processed. Several stock reply handlers unconditionally move NetworkState backwards,
    /// so a delayed duplicate can restart the game after the player has entered the editor.
    /// ReliableOrdered already retransmits the original reply; queue each startup reply once.
    /// </summary>
    internal sealed class StartupReplyGate
    {
        private const int Ready = 0;
        private const int Queueing = 1;
        private const int Queued = 2;

        private readonly int[] _states = new int[(int)StartupReplyKind.Count];
        private readonly int[] _suppressionCounts = new int[(int)StartupReplyKind.Count];

        internal bool TryBegin(StartupReplyKind kind)
        {
            var index = GetIndex(kind);
            if (Interlocked.CompareExchange(ref _states[index], Queueing, Ready) == Ready)
                return true;

            Interlocked.Increment(ref _suppressionCounts[index]);
            return false;
        }

        internal void Complete(StartupReplyKind kind)
        {
            var index = GetIndex(kind);
            if (Interlocked.CompareExchange(ref _states[index], Queued, Queueing) != Queueing)
                throw new InvalidOperationException($"Startup reply {kind} was not being queued.");
        }

        internal void Abort(StartupReplyKind kind)
        {
            var index = GetIndex(kind);
            Interlocked.CompareExchange(ref _states[index], Ready, Queueing);
        }

        internal int SuppressionCount(StartupReplyKind kind)
        {
            return Volatile.Read(ref _suppressionCounts[GetIndex(kind)]);
        }

        internal bool IsQueued(StartupReplyKind kind)
        {
            return Volatile.Read(ref _states[GetIndex(kind)]) == Queued;
        }

        private static int GetIndex(StartupReplyKind kind)
        {
            var index = (int)kind;
            if (index < 0 || index >= (int)StartupReplyKind.Count)
                throw new ArgumentOutOfRangeException(nameof(kind));
            return index;
        }
    }

    internal static class StartupReplyGuard
    {
        internal static bool QueueOnce(ClientStructure client, StartupReplyKind kind, Action queueReply)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (queueReply == null) throw new ArgumentNullException(nameof(queueReply));

            if (!client.StartupReplies.TryBegin(kind))
            {
                if (client.StartupReplies.SuppressionCount(kind) == 1)
                    LunaLog.Debug($"Suppressed duplicate startup {kind} request from {client.PlayerName}; original reliable reply is already queued.");
                return false;
            }

            try
            {
                queueReply();
                client.StartupReplies.Complete(kind);
                return true;
            }
            catch
            {
                // Construction or enqueue failed. Let the client's next retry try again.
                client.StartupReplies.Abort(kind);
                throw;
            }
        }
    }
}
