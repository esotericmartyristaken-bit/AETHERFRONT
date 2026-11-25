using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Lidgren.Network;
using LmpCommon.Message.Interface;

namespace Aetherfront.Synchronization
{
    // Reads the shipping transport's accounting under its existing lock order.
    // Admission never sleeps and never allocates a transport buffer.
    public sealed class TransportCapacity
    {
        readonly FieldInfo connectionGate = Field(typeof(NetConnection), "m_reliableOutgoingLock"),
            peerGate = Field(typeof(NetPeer), "m_reliableStorageLock"),
            owner = Field(typeof(NetConnection), "m_peer"),
            connectionBytes = Field(typeof(NetConnection), "m_reliableOutgoingBytes"),
            peerBytes = Field(typeof(NetPeer), "m_reliableOutgoingBytes"),
            connectionMessages = Field(typeof(NetConnection), "m_reliableOutgoingMessages"),
            peerMessages = Field(typeof(NetPeer), "m_reliableOutgoingMessages"),
            connectionBuffers = Field(typeof(NetConnection), "m_reliableOutgoingBuffers"),
            peerBuffers = Field(typeof(NetPeer), "m_reliableOutgoingBuffers");
        static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? throw new MissingFieldException(type.FullName, name);
        public sealed class Stall
        {
            long lastProgress = -1, lowBytes;
            int lowReservations, lowArrays;
            public void Reset() => lastProgress = -1;
            public bool Blocked(long now, long bytes, int reservations, int arrays)
            {
                if (lastProgress < 0)
                {
                    lastProgress = now;
                    lowBytes = bytes; lowReservations = reservations; lowArrays = arrays;
                }
                // Retiring fragments counts even before their shared backing array is released.
                // Local low watermarks keep refills or other peers from extending the deadline.
                if (bytes < lowBytes || reservations < lowReservations || arrays < lowArrays)
                {
                    lowBytes = Math.Min(lowBytes, bytes);
                    lowReservations = Math.Min(lowReservations, reservations);
                    lowArrays = Math.Min(lowArrays, arrays);
                    lastProgress = now;
                }
                return now - lastProgress >= Stopwatch.Frequency * 30L;
            }
        }
        public bool Stalled(NetPeer peer, NetConnection connection, Stall watch)
        {
            if (peer == null || connection == null || !ReferenceEquals(owner.GetValue(connection), peer))
                return watch.Blocked(Stopwatch.GetTimestamp(), long.MaxValue, int.MaxValue, int.MaxValue);
            lock (connectionGate.GetValue(connection)) lock (peerGate.GetValue(peer))
                return watch.Blocked(Stopwatch.GetTimestamp(), (long)connectionBytes.GetValue(connection),
                    (int)connectionMessages.GetValue(connection), ((Dictionary<byte[], int>)connectionBuffers.GetValue(connection)).Count);
        }
        public bool CanSend(NetPeer peer, NetConnection connection, IMessageBase message, int size)
        {
            if (message.NetDeliveryMethod == NetDeliveryMethod.Unreliable || message.NetDeliveryMethod == NetDeliveryMethod.UnreliableSequenced) return true;
            if (peer == null || connection == null || !ReferenceEquals(owner.GetValue(connection), peer)) return false;
            long maximum = TrafficScheduler.Critical(message) ? 12582912 : size >= 16384 ? 2097152 : 8388608;
            long cost = Math.Max(2048L, size * 2L + 1024L);
            lock (connectionGate.GetValue(connection)) lock (peerGate.GetValue(peer))
            {
                long local = (long)connectionBytes.GetValue(connection), shared = (long)peerBytes.GetValue(peer);
                int reserved = (int)connectionMessages.GetValue(connection), all = (int)peerMessages.GetValue(peer);
                // One large validated message may exceed the soft window only when drained.
                int arrays = ((Dictionary<byte[], int>)connectionBuffers.GetValue(connection)).Count;
                int sharedArrays = ((Dictionary<byte[], int>)peerBuffers.GetValue(peer)).Count;
                return local <= Math.Max(maximum, cost) - cost && shared <= 50331648L - cost &&
                    arrays < (TrafficScheduler.Critical(message) ? 2048 : 512) && sharedArrays < 8192 &&
                    reserved < (TrafficScheduler.Critical(message) ? 65536 : 16384) && all < 131072;
            }
        }
    }
}
