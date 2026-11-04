using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using Lidgren.Network;
using LmpClient;
using LmpClient.Network;
using LmpCommon.Message.Interface;

namespace Aetherfront.Pacing
{
    public static class ClientPacing
    {
        private static readonly Lazy<ReliableCounterReader> Counters = new Lazy<ReliableCounterReader>(() => new ReliableCounterReader());
        private static readonly Lazy<MethodInfo> CurrentSession = new Lazy<MethodInfo>(() =>
        {
            MethodInfo method = typeof(NetworkConnection).GetMethod("IsCurrentSession", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(int) }, null);
            if (method == null || method.ReturnType != typeof(bool))
                throw new InvalidDataException("Reliable sender generation contract changed.");
            return method;
        });
        private static readonly Lazy<MethodInfo> UseSession = new Lazy<MethodInfo>(() =>
        {
            MethodInfo method = typeof(NetworkConnection).GetMethod("TryUseCurrentSession", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(Action) }, null);
            if (method == null || method.ReturnType != typeof(bool))
                throw new InvalidDataException("Reliable sender session contract changed.");
            return method;
        });
        private static long nextLogTick;
        public const int PollMilliseconds = 10;
        public const int StallTimeoutMilliseconds = 30000;

        // Graft INSIDE SendNetworkMessage's original try; false must use its existing return block.
        // This hook never recycles. The original finally always retains sole message ownership.
        public static bool WaitForCapacity(IMessageBase message, CancellationToken cancellationToken, int generation)
        {
            NetClient client = null;
            NetConnection connection = null;
            try
            {
                if (message == null) return false;
                if (message is IMasterServerMessageBase) return true;
                NetDeliveryMethod delivery = message.NetDeliveryMethod;
                if (delivery != NetDeliveryMethod.ReliableOrdered && delivery != NetDeliveryMethod.ReliableSequenced &&
                    delivery != NetDeliveryMethod.ReliableUnordered) return true;
                client = NetworkMain.ClientConnection;
                connection = client == null ? null : client.ServerConnection;
                if (connection == null || cancellationToken.IsCancellationRequested || NetworkConnection.ResetRequested) return false;
                MethodInfo sessionMethod = CurrentSession.Value;
                if (!IsCurrent(client, connection, sessionMethod, generation, cancellationToken)) return false;
                ReliableCounterReader counters = Counters.Value;
                int messageSize = message.GetMessageSize();
                PacingProgress progress = null;

                for (;;)
                {
                    if (!IsCurrent(client, connection, sessionMethod, generation, cancellationToken)) return false;
                    OutgoingBudgetSnapshot snapshot;
                    if (!counters.TryRead(client, connection, out snapshot)) return false;
                    OutgoingMessageCost cost;
                    if (!ReliableBudget.TryEstimate(messageSize, snapshot.Mtu, out cost) || ReliableBudget.CanSend(snapshot, cost))
                    {
                        if (!IsCurrent(client, connection, sessionMethod, generation, cancellationToken)) return false;
                        if (progress != null && Stopwatch.GetTimestamp() - progress.FirstWait >= Stopwatch.Frequency * 5L)
                            LogLimited("[APF]: Reliable sender pacing resumed; waitedMs=" +
                                ((Stopwatch.GetTimestamp() - progress.FirstWait) * 1000L / Stopwatch.Frequency).ToString(CultureInfo.InvariantCulture));
                        return true;
                    }

                    if (!Thread.CurrentThread.IsBackground)
                        throw new InvalidOperationException("Reliable sender pacing cannot wait on a foreground thread.");
                    if (progress == null) progress = new PacingProgress(Stopwatch.Frequency * (StallTimeoutMilliseconds / 1000L));
                    long now = Stopwatch.GetTimestamp();
                    if (progress.Expired(snapshot, cost, now))
                    {
                        DisconnectIfCurrent(client, connection, generation,
                            new TimeoutException("Reliable sender pacing made no budget progress for 30 seconds; reconnect to resynchronize."));
                        return false;
                    }
                    LogLimited(string.Format(CultureInfo.InvariantCulture,
                        "[APF]: Reliable sender pacing stalled; message={0}; size={1}; backingBound={2}; reservationsBound={3}; " +
                        "connectionBytes={4}; connectionArrays={5}; connectionReservations={6}; peerBytes={7}; peerArrays={8}; peerReservations={9}; mtu={10}; waitedMs={11}; noProgressMs={12}",
                        message.GetType().Name, messageSize, cost.BackingBytes, cost.Reservations,
                        snapshot.ConnectionBytes, snapshot.ConnectionArrays, snapshot.ConnectionReservations,
                        snapshot.PeerBytes, snapshot.PeerArrays, snapshot.PeerReservations, snapshot.Mtu,
                        (now - progress.FirstWait) * 1000L / Stopwatch.Frequency,
                        (now - progress.LastProgress) * 1000L / Stopwatch.Frequency));
                    // A flush only signals the independent transport worker. It is not an ACK.
                    if (!IsCurrent(client, connection, sessionMethod, generation, cancellationToken)) return false;
                    client.FlushSendQueue();
                    Thread.Sleep(PollMilliseconds);
                }
            }
            catch (Exception error)
            {
                LogLimited("[APF]: Reliable sender pacing unavailable: " + error.GetType().Name + ": " + error.Message);
                DisconnectIfCurrent(client, connection, generation,
                    new InvalidOperationException("Reliable sender pacing counter contract unavailable.", error));
                return false;
            }
        }

        private static bool IsCurrent(NetClient client, NetConnection connection, MethodInfo method, int generation, CancellationToken token)
        {
            return !token.IsCancellationRequested && !NetworkConnection.ResetRequested && (bool)method.Invoke(null, new object[] { generation }) &&
                   ReferenceEquals(client, NetworkMain.ClientConnection) && ReferenceEquals(connection, client.ServerConnection) &&
                   client.Status == NetPeerStatus.Running && connection.Status == NetConnectionStatus.Connected;
        }

        private static void DisconnectIfCurrent(NetClient client, NetConnection connection, int generation, Exception error)
        {
            try
            {
                if (client == null || connection == null) return;
                Action action = () =>
                {
                    if (ReferenceEquals(client, NetworkMain.ClientConnection) && ReferenceEquals(connection, client.ServerConnection))
                        NetworkMain.HandleDisconnectException(error);
                };
                // The existing session gate prevents a timeout from disconnecting a replacement session.
                UseSession.Value.Invoke(null, new object[] { generation, action });
            }
            catch (Exception failure) { LogLimited("[APF]: Reliable sender session failure: " + failure.GetType().Name); }
        }

        private static void LogLimited(string text)
        {
            try
            {
                long now = Stopwatch.GetTimestamp(), previous = Interlocked.Read(ref nextLogTick);
                if (now < previous || Interlocked.CompareExchange(ref nextLogTick, now + Stopwatch.Frequency * 5L, previous) != previous)
                    return;
                LunaLog.Log(text);
            }
            catch { /* Diagnostics must not escape the entry hook. */ }
        }
    }
}
