using System;
using System.IO;

namespace Aetherfront.Pacing
{
    public struct OutgoingBudgetSnapshot
    {
        public long ConnectionBytes, PeerBytes;
        public int ConnectionArrays, ConnectionReservations, PeerArrays, PeerReservations, Mtu;
    }

    public struct OutgoingMessageCost
    {
        public long BackingBytes;
        public int Reservations;
    }

    // This policy only reads budgets. It never reserves, releases, or alters transport state.
    public static class ReliableBudget
    {
        public const long ByteLimit = 67108864;
        public const int ConnectionArrayLimit = 4096, PeerArrayLimit = 16384;
        public const int ConnectionReservationLimit = 131072, PeerReservationLimit = 262144;
        public const long ByteHeadroom = 8388608;
        public const int ArrayHeadroom = 512, ReservationHeadroom = 16384;

        public static void Validate(OutgoingBudgetSnapshot value)
        {
            if (value.ConnectionBytes < 0 || value.ConnectionBytes > ByteLimit ||
                value.PeerBytes < 0 || value.PeerBytes > ByteLimit ||
                value.ConnectionArrays < 0 || value.ConnectionArrays > ConnectionArrayLimit ||
                value.PeerArrays < 0 || value.PeerArrays > PeerArrayLimit ||
                value.ConnectionReservations < 0 || value.ConnectionReservations > ConnectionReservationLimit ||
                value.PeerReservations < 0 || value.PeerReservations > PeerReservationLimit)
                throw new InvalidDataException("Reliable sender counter contract is invalid.");
        }

        public static bool TryEstimate(int messageSize, int mtu, out OutgoingMessageCost cost)
        {
            cost = new OutgoingMessageCost();
            // Leave invalid/impossible requests to the original serializer and hard guards.
            if (messageSize <= 0 || messageSize > ByteLimit || mtu <= 26 || mtu > 8191)
                return false;

            long size = messageSize;
            cost.BackingBytes = Math.Max(size, Math.Min(ByteLimit, Math.Max(size + 1024L, size * 2L)));
            if (size + 5L <= mtu)
            {
                cost.Reservations = 1;
                return true;
            }

            // The largest permitted fragment-group identifier has the largest header.
            // This mirrors RC42 GetBestChunkSize without allocating a transport message.
            int chunk = mtu - 9;
            int header = HeaderSize(65534, messageSize, chunk, messageSize / chunk);
            chunk = mtu - 5 - header;
            do
            {
                if (--chunk <= 0) return false;
                int fragments = (int)((size + chunk - 1L) / chunk);
                header = HeaderSize(65534, messageSize, chunk, fragments);
            } while ((long)chunk + header + 6L >= mtu);

            long count = (size + chunk - 1L) / chunk;
            if (count > ConnectionReservationLimit) return false;
            cost.Reservations = (int)count;
            return true;
        }

        private static int HeaderSize(int group, int size, int chunk, int fragments)
        {
            return VariableBytes((uint)group) + VariableBytes((uint)size * 8U) +
                   VariableBytes((uint)chunk) + VariableBytes((uint)fragments);
        }

        private static int VariableBytes(uint value)
        {
            int bytes = 1;
            while (value >= 128) { value >>= 7; bytes++; }
            return bytes;
        }

        public static bool CanSend(OutgoingBudgetSnapshot value, OutgoingMessageCost next)
        {
            Validate(value);
            if (next.BackingBytes <= 0 || next.BackingBytes > ByteLimit ||
                next.Reservations <= 0 || next.Reservations > ConnectionReservationLimit)
                throw new InvalidDataException("Reliable sender cost contract is invalid.");

            return ConnectionFits(value, next) && PeerFits(value, next);
        }

        internal static bool ConnectionFits(OutgoingBudgetSnapshot value, OutgoingMessageCost next)
        {
            return Fits(value.ConnectionBytes, next.BackingBytes, ByteLimit, ByteHeadroom) &&
                   Fits(value.ConnectionArrays, 1, ConnectionArrayLimit, ArrayHeadroom) &&
                   Fits(value.ConnectionReservations, next.Reservations, ConnectionReservationLimit, ReservationHeadroom);
        }

        internal static bool PeerFits(OutgoingBudgetSnapshot value, OutgoingMessageCost next)
        {
            return Fits(value.PeerBytes, next.BackingBytes, ByteLimit, ByteHeadroom) &&
                   Fits(value.PeerArrays, 1, PeerArrayLimit, ArrayHeadroom) &&
                   Fits(value.PeerReservations, next.Reservations, PeerReservationLimit, ReservationHeadroom);
        }

        private static bool Fits(long used, long next, long limit, long headroom)
        {
            // A single valid large message may exceed the soft window, never the hard cap.
            long admissionLimit = next > limit - headroom ? limit : limit - headroom;
            return used <= admissionLimit - next;
        }
    }

    // Pure monotonic-clock policy: enqueue/ACK churn above an old low-water mark is not progress.
    public sealed class PacingProgress
    {
        private readonly long timeoutTicks;
        private bool started, connectionBlocked, peerBlocked;
        private OutgoingBudgetSnapshot lowest;
        public long FirstWait { get; private set; }
        public long LastProgress { get; private set; }

        public PacingProgress(long timeoutTicks)
        {
            if (timeoutTicks <= 0) throw new ArgumentOutOfRangeException("timeoutTicks");
            this.timeoutTicks = timeoutTicks;
        }

        public bool Expired(OutgoingBudgetSnapshot value, OutgoingMessageCost next, long now)
        {
            ReliableBudget.CanSend(value, next); // Validate the same contracts as admission.
            bool localBlocked = !ReliableBudget.ConnectionFits(value, next);
            bool sharedBlocked = !ReliableBudget.PeerFits(value, next);
            if (!started)
            {
                started = true; lowest = value; FirstWait = LastProgress = now;
            }
            else
            {
                bool localDrain = value.ConnectionBytes < lowest.ConnectionBytes || value.ConnectionArrays < lowest.ConnectionArrays ||
                                  value.ConnectionReservations < lowest.ConnectionReservations;
                bool sharedDrain = value.PeerBytes < lowest.PeerBytes || value.PeerArrays < lowest.PeerArrays ||
                                   value.PeerReservations < lowest.PeerReservations;
                if (((connectionBlocked || localBlocked) && localDrain) || ((peerBlocked || sharedBlocked) && sharedDrain))
                    LastProgress = now;
                lowest.ConnectionBytes = Math.Min(lowest.ConnectionBytes, value.ConnectionBytes);
                lowest.ConnectionArrays = Math.Min(lowest.ConnectionArrays, value.ConnectionArrays);
                lowest.ConnectionReservations = Math.Min(lowest.ConnectionReservations, value.ConnectionReservations);
                lowest.PeerBytes = Math.Min(lowest.PeerBytes, value.PeerBytes);
                lowest.PeerArrays = Math.Min(lowest.PeerArrays, value.PeerArrays);
                lowest.PeerReservations = Math.Min(lowest.PeerReservations, value.PeerReservations);
            }
            connectionBlocked = localBlocked; peerBlocked = sharedBlocked;
            return now - LastProgress >= timeoutTicks;
        }
    }
}
