using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using LmpCommon.Enums;
using LmpCommon.Message.Interface;
using Server.Client;
using Server.Log;
using Server.Message.Base;
using Server.Server;

namespace Aetherfront.Synchronization.Server
{
    // Native deserialization/authentication/plugin guards still run at packet intake.
    // Only validated handler dispatch is queued. ONE worker preserves the server's
    // existing serial gameplay semantics; no concurrent mutation is introduced.
    public static class ReceivePump
    {
        sealed class State
        {
            internal readonly object Gate = new object();
            internal readonly ClientStructure Client;
            internal readonly Dictionary<IMessageBase, ReaderBase> Readers = new Dictionary<IMessageBase, ReaderBase>();
            internal readonly TrafficScheduler Queue;
            internal bool Scheduled;
            internal bool Closing;
            internal string CloseReason;
            internal State(ClientStructure client) { Client = client; Queue = new TrafficScheduler(m => { Readers.Remove(m); m.Recycle(); }); }
        }
        static readonly ConditionalWeakTable<ClientStructure, State> States = new ConditionalWeakTable<ClientStructure, State>();
        static readonly ConcurrentQueue<State> Ready = new ConcurrentQueue<State>();
        static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        static readonly Lazy<Thread> Worker = new Lazy<Thread>(() =>
        { var thread = new Thread(Run) { IsBackground = true, Name = "Aetherfront serial gameplay dispatcher" }; thread.Start(); return thread; });
        public static long Handled, Coalesced, SlowHandlers;
        public static void Enqueue(ReaderBase reader, ClientStructure client, IClientMessageBase message)
        {
            if (client == null || client.DisconnectClient || client.ConnectionStatus != ConnectionStatus.Connected) { message?.Recycle(); return; }
            var state = States.GetValue(client, value => new State(value));
            string failure = null;
            lock (state.Gate)
            {
                if (state.Closing) { message?.Recycle(); return; }
                try
                {
                    long previous = state.Queue.Coalesced;
                    state.Readers[message] = reader; state.Queue.Add(message);
                    Interlocked.Add(ref Coalesced, state.Queue.Coalesced - previous);
                    if (!state.Scheduled && state.Queue.Count > 0) { state.Scheduled = true; Ready.Enqueue(state); }
                }
                catch (Exception error)
                { state.Queue.Clear(); failure = error.Message; }
            }
            if (failure != null) { EnqueueDisconnect(client, failure); return; }
            var worker = Worker.Value; Wake.Set();
        }
        public static void EnqueueDisconnect(ClientStructure client, string reason)
        {
            if (client == null || client.DisconnectClient) return;
            var state = States.GetValue(client, value => new State(value));
            lock (state.Gate)
            {
                state.Closing = true; state.CloseReason = reason;
                state.Queue.Clear(); state.Readers.Clear();
                if (!state.Scheduled) { state.Scheduled = true; Ready.Enqueue(state); }
            }
            var worker = Worker.Value; Wake.Set();
        }
        static void Run()
        {
            LunaLog.Normal("[Stability] Serial gameplay dispatcher active; packet intake independent; per-peer bounded/coalesced lanes.");
            for (;;)
            {
                if (!Ready.TryDequeue(out var state)) { Wake.WaitOne(1000); continue; }
                IMessageBase message = null; ReaderBase reader = null; string close = null; double queueMilliseconds = 0;
                lock (state.Gate)
                {
                    if (state.Closing)
                    {
                        close = state.CloseReason ?? "Connection closed";
                        state.Queue.Clear(); state.Readers.Clear(); States.Remove(state.Client); state.Scheduled = false;
                    }
                    else
                    {
                    if (state.Client.DisconnectClient || state.Client.ConnectionStatus != ConnectionStatus.Connected)
                    { state.Queue.Clear(); state.Readers.Clear(); States.Remove(state.Client); state.Scheduled = false; continue; }
                    if (state.Queue.TryTake((_, __) => true, out message))
                    { reader = state.Readers[message]; state.Readers.Remove(message); queueMilliseconds = state.Queue.LastQueueMilliseconds; }
                    if (state.Queue.Count > 0) Ready.Enqueue(state); else state.Scheduled = false;
                    }
                }
                if (close != null)
                {
                    try { ClientConnectionHandler.DisconnectClient(state.Client, close); }
                    catch (Exception error) { LunaLog.Error("[Stability] Deferred disconnect failed: " + error); }
                    continue;
                }
                if (message == null) { Wake.WaitOne(2); continue; }
                // Native handlers may recycle the message and clear Data.
                // Capture diagnostics while this worker still owns the payload.
                string messageType = message.Data?.GetType().Name ?? "unavailable";
                long began = Stopwatch.GetTimestamp();
                try
                {
                    if (!state.Client.DisconnectClient && state.Client.ConnectionStatus == ConnectionStatus.Connected)
                        reader.HandleMessage(state.Client, (IClientMessageBase)message);
                    else message.Recycle();
                    Interlocked.Increment(ref Handled);
                }
                catch (Exception error) { LunaLog.Error("[Stability] Gameplay handler failed; type=" + messageType + "; error=" + error); }
                finally
                {
                    double milliseconds = (Stopwatch.GetTimestamp() - began) * 1000d / Stopwatch.Frequency;
                    if (milliseconds >= 250)
                    {
                        int pending;
                        lock (state.Gate) pending = state.Queue.Count;
                        Interlocked.Increment(ref SlowHandlers);
                        LunaLog.Normal("[Stability] Slow gameplay handler; type=" + messageType + "; milliseconds=" + milliseconds.ToString("F1") + "; queueMilliseconds=" + queueMilliseconds.ToString("F1") + "; pending=" + pending);
                    }
                    SyncTimings.ReportIfDue();
                }
            }
        }
    }
}
