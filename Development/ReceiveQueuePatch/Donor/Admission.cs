namespace Lidgren.Network;

// Compiles the reviewed admission logic. These shapes bind to the shipping
// definitions during the graft. The donor assembly is never distributed.
internal sealed class NetPeer
{
    private long m_receiveQueueGeneration;
    private long m_pendingIncomingBytes = 0; // Uses the existing shipping field.

    // The existing receive queue lock protects these counters and both call sites.
    private bool RejectIncoming(bool sharedLimitReached, NetIncomingMessage message, int pendingCount, bool dedicatedServer)
    {
        if (sharedLimitReached)
            return true;
        var sender = message.SenderConnection;
        if (message.MessageType != NetIncomingMessageType.Data || sender == null)
            return false;
        if (sender.m_receiveQueueGeneration != m_receiveQueueGeneration)
        {
            sender.m_receiveQueueGeneration = m_receiveQueueGeneration;
            sender.m_queuedIncomingCount = 0;
            sender.m_queuedIncomingBytes = 0;
        }

        int bytes = IncomingStorageBytes(message);
        bool unreliable = IsUnreliableIncoming(message);
        // Unreliable updates can be superseded. Reserve room for reliable traffic
        // from this sender, including a legal large fragmented payload.
        if (unreliable && (pendingCount >= 2048 || m_pendingIncomingBytes + bytes > 16 * 1024 * 1024))
            return true;
        int countLimit = dedicatedServer ? (unreliable ? 256 : 1024) : (unreliable ? 512 : 4096);
        int byteLimit = dedicatedServer ? (unreliable ? 4 : 32) * 1024 * 1024 : (unreliable ? 8 : 64) * 1024 * 1024;
        if (sender.m_queuedIncomingCount >= countLimit || sender.m_queuedIncomingBytes + bytes > byteLimit)
            return true;

        sender.m_queuedIncomingCount++;
        sender.m_queuedIncomingBytes += bytes;
        return false;
    }

    private static int AccountIncomingRemoval(NetIncomingMessage message)
    {
        int bytes = IncomingStorageBytes(message);
        var sender = message.SenderConnection;
        if (message.MessageType == NetIncomingMessageType.Data && sender != null)
        {
            sender.m_queuedIncomingCount--;
            sender.m_queuedIncomingBytes -= bytes;
        }
        return bytes;
    }

    private static bool IsUnreliableIncoming(NetIncomingMessage message)
    {
        var delivery = message.DeliveryMethod;
        return delivery == NetDeliveryMethod.Unreliable || delivery == NetDeliveryMethod.UnreliableSequenced;
    }

    private void AdvanceReceiveQueueGeneration() => m_receiveQueueGeneration++;

    private static int IncomingStorageBytes(NetIncomingMessage message) =>
        throw new System.NotSupportedException("Bound to the shipping method");
}

internal sealed class NetConnection
{
    internal long m_receiveQueueGeneration;
    internal int m_queuedIncomingCount;
    internal int m_queuedIncomingBytes;
}

internal sealed class NetIncomingMessage
{
    public NetConnection? SenderConnection => throw new System.NotSupportedException();
    public NetIncomingMessageType MessageType => throw new System.NotSupportedException();
    public NetDeliveryMethod DeliveryMethod => throw new System.NotSupportedException();
}

internal enum NetIncomingMessageType : byte { Data = 8 }
internal enum NetDeliveryMethod : byte { Unreliable = 1, UnreliableSequenced = 2 }
