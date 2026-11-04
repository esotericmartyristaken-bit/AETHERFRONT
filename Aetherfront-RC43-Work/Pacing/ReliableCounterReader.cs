using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Lidgren.Network;

namespace Aetherfront.Pacing
{
    public sealed class ReliableCounterReader
    {
        public const string ExpectedTransportSha256 = "b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3";
        private readonly FieldInfo connectionGate, peerGate, owner, connectionBuffers, peerBuffers;
        private readonly FieldInfo connectionBytes, peerBytes, connectionMessages, peerMessages, mtu, status, disconnectRequested;

        public ReliableCounterReader()
        {
            VerifyTransport(typeof(NetPeer).Assembly.Location, ExpectedTransportSha256);
            Type connection = typeof(NetConnection), peer = typeof(NetPeer);
            connectionGate = RequireField(connection, "m_reliableOutgoingLock", typeof(object));
            peerGate = RequireField(peer, "m_reliableStorageLock", typeof(object));
            owner = RequireField(connection, "m_peer", peer);
            connectionBuffers = RequireField(connection, "m_reliableOutgoingBuffers", typeof(Dictionary<byte[], int>));
            peerBuffers = RequireField(peer, "m_reliableOutgoingBuffers", typeof(Dictionary<byte[], int>));
            connectionBytes = RequireField(connection, "m_reliableOutgoingBytes", typeof(long));
            peerBytes = RequireField(peer, "m_reliableOutgoingBytes", typeof(long));
            connectionMessages = RequireField(connection, "m_reliableOutgoingMessages", typeof(int));
            peerMessages = RequireField(peer, "m_reliableOutgoingMessages", typeof(int));
            mtu = RequireField(connection, "m_currentMTU", typeof(int));
            status = RequireField(connection, "m_status", typeof(NetConnectionStatus));
            disconnectRequested = RequireField(connection, "m_disconnectRequested", typeof(bool));
        }

        internal static FieldInfo RequireField(Type type, string name, Type fieldType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field == null || field.IsStatic || field.FieldType != fieldType)
                throw new InvalidDataException("Reliable sender field contract changed: " + type.FullName + "." + name);
            return field;
        }

        internal static void VerifyTransport(string path, string expected)
        {
            using (Stream stream = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create())
            {
                string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    throw new InvalidDataException("Reliable sender transport hash changed.");
            }
        }

        public bool TryRead(NetPeer peer, NetConnection connection, out OutgoingBudgetSnapshot value)
        {
            value = new OutgoingBudgetSnapshot();
            if (peer == null || connection == null || !ReferenceEquals(owner.GetValue(connection), peer))
                throw new InvalidDataException("Reliable sender connection owner changed.");
            object first = connectionGate.GetValue(connection), second = peerGate.GetValue(peer);
            if (first == null || second == null || ReferenceEquals(first, second))
                throw new InvalidDataException("Reliable sender gate contract changed.");

            // Preserve the transport's connection-then-peer lock order. Never wait or flush here.
            lock (first)
            lock (second)
            {
                if ((NetConnectionStatus)status.GetValue(connection) != NetConnectionStatus.Connected ||
                    (bool)disconnectRequested.GetValue(connection))
                    return false;
                var local = connectionBuffers.GetValue(connection) as Dictionary<byte[], int>;
                var shared = peerBuffers.GetValue(peer) as Dictionary<byte[], int>;
                if (local == null || shared == null)
                    throw new InvalidDataException("Reliable sender buffer contract changed.");
                value.ConnectionBytes = (long)connectionBytes.GetValue(connection);
                value.PeerBytes = (long)peerBytes.GetValue(peer);
                value.ConnectionArrays = local.Count;
                value.PeerArrays = shared.Count;
                value.ConnectionReservations = (int)connectionMessages.GetValue(connection);
                value.PeerReservations = (int)peerMessages.GetValue(peer);
                value.Mtu = (int)mtu.GetValue(connection);
                ReliableBudget.Validate(value);
                return true;
            }
        }
    }
}
