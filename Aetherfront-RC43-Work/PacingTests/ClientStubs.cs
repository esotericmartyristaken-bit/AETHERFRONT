// Only session coordination and logging are substituted. Transport and message interfaces are shipping RC42 binaries.
using System;
using System.Collections.Concurrent;
using System.Threading;
using Lidgren.Network;

namespace LmpClient
{
    public static class LunaLog
    {
        public static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
        public static void Log(string text) { Lines.Enqueue(text); }
    }
}

namespace LmpClient.Network
{
    public static class NetworkMain
    {
        public static NetClient ClientConnection { get; set; }
        public static int Disconnects;
        public static Exception LastDisconnect;
        public static void HandleDisconnectException(Exception error)
        {
            Interlocked.Increment(ref Disconnects);
            LastDisconnect = error;
            NetworkConnection.DisconnectForTest();
        }
    }

    public static class NetworkConnection
    {
        private static readonly object DisconnectLock = new object();
        private static int generation = 1;
        public static volatile bool ResetRequested;
        public static volatile bool SessionAllowed = true;
        public static int Generation { get { return Volatile.Read(ref generation); } }
        internal static bool IsCurrentSession(int expected)
        {
            return !ResetRequested && SessionAllowed && Generation == expected;
        }
        internal static bool TryUseCurrentSession(int expected, Action action)
        {
            lock (DisconnectLock)
            {
                if (!IsCurrentSession(expected)) return false;
                action();
                return true;
            }
        }
        public static void Advance() { lock (DisconnectLock) Interlocked.Increment(ref generation); }
        public static void DisconnectForTest() { lock (DisconnectLock) { Advance(); SessionAllowed = false; } }
        public static void ResetForTest()
        {
            lock (DisconnectLock) { Advance(); ResetRequested = false; SessionAllowed = true; }
            NetworkMain.Disconnects = 0; NetworkMain.LastDisconnect = null;
        }
    }
}
