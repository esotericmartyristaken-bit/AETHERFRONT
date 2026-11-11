using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Server.Aetherfront;
using Server.System;
using Server.Log;

namespace Aetherfront.Synchronization.Server
{
    public static class SyncTimings
    {
        sealed class Metric { internal long Count, Ticks, Maximum; }
        sealed class Lease { internal object Gate; internal long Started; internal string Phase; }
        sealed class Scope : IDisposable
        {
            readonly object gate;
            internal Scope(object gate) { this.gate = gate; bool taken = false; EnterFleet(gate, ref taken); }
            public void Dispose() => Exit(gate);
        }
        static readonly ConcurrentDictionary<string, Metric> Metrics = new ConcurrentDictionary<string, Metric>();
        [ThreadStatic] static Stack<Lease> leases;
        static long nextReport;
        static readonly object Fleet = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontFleetGuard", true)
            .GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        static readonly object Backup = typeof(VesselStoreSystem).GetField("BackupLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        public static long Start() => Stopwatch.GetTimestamp();
        public static IDisposable FleetScope() => new Scope(Fleet);
        public static IDisposable BackupScope() => new Scope(Backup);
        public static void Complete(string phase, long began)
        {
            long elapsed = Math.Max(0, Start() - began);
            var metric = Metrics.GetOrAdd(phase, _ => new Metric());
            Interlocked.Increment(ref metric.Count); Interlocked.Add(ref metric.Ticks, elapsed);
            long old;
            do { old = Interlocked.Read(ref metric.Maximum); if (old >= elapsed) break; }
            while (Interlocked.CompareExchange(ref metric.Maximum, elapsed, old) != old);
        }
        public static string Summary()
        {
            var rows = new List<string>();
            foreach (var item in Metrics)
            {
                long count = Interlocked.Read(ref item.Value.Count);
                rows.Add(item.Key + "=" + count + "/" + (Interlocked.Read(ref item.Value.Ticks) * 1000d / Stopwatch.Frequency).ToString("F1") +
                    "/" + (Interlocked.Read(ref item.Value.Maximum) * 1000d / Stopwatch.Frequency).ToString("F1"));
            }
            return string.Join(";", rows);
        }
        public static void ReportIfDue()
        {
            long now = Start(), due = Interlocked.Read(ref nextReport);
            if (now < due || Interlocked.CompareExchange(ref nextReport, now + Stopwatch.Frequency * 30, due) != due) return;
            LunaLog.Normal("[Synchronization] Phase timings count/totalMs/maxMs; " + Summary());
        }
        static void Enter(object gate, ref bool taken, string phase)
        {
            long began = Start(); Monitor.Enter(gate, ref taken); Complete(phase + ".wait", began);
            if (taken) { if (leases == null) leases = new Stack<Lease>(); leases.Push(new Lease { Gate = gate, Started = Start(), Phase = phase }); }
        }
        public static void EnterFleet(object gate, ref bool taken) => Enter(gate, ref taken, ReferenceEquals(gate, Fleet) ? "fleet" : ReferenceEquals(gate, Backup) ? "backup" : "native");
        public static void EnterStore(object gate, ref bool taken) => Enter(gate, ref taken, "store");
        public static void EnterFleetSimple(object gate) { bool taken = false; EnterFleet(gate, ref taken); }
        public static void EnterStoreSimple(object gate) { bool taken = false; EnterStore(gate, ref taken); }
        public static void Exit(object gate)
        {
            Lease lease = leases != null && leases.Count > 0 && ReferenceEquals(leases.Peek().Gate, gate) ? leases.Pop() : null;
            Monitor.Exit(gate);
            if (lease != null) Complete(lease.Phase + ".hold", lease.Started);
        }
        public static void Write(Stream stream, byte[] bytes, int offset, int count)
        { long began = Start(); try { stream.Write(bytes, offset, count); } finally { Complete("disk.write", began); } }
        public static void Flush(FileStream stream, bool durable)
        { long began = Start(); try { stream.Flush(durable); } finally { Complete(durable ? "disk.flush.durable" : "disk.flush", began); } }
    }
}
