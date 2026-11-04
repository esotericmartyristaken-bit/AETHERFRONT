using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Aetherfront.Core;
using Aetherfront.Personal.Transport;
using ApfCore;
using LmpClient;
using LmpClient.Aetherfront;
using LmpClient.Apf;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    // Persist evidence and retry state before sending. A rejected reward is
    // retained, not silently deleted or treated as an earned award.
    internal static class ScienceOutbox
    {
        private sealed class Flight
        {
            internal PendingScienceClaim Claim;
            internal string Server, Account, Source;
            internal int Command;
            internal decimal Amount;
            internal long Sent;
        }
        private static PendingScienceClaims pending;
        private static bool dirty, failed;
        private static int cursor;
        private static float nextSend, nextSave;
        private static string path;
        private static long wantedRevision;
        private static float nextSnapshot;
        private static readonly Dictionary<string, Flight> flights = new Dictionary<string, Flight>(StringComparer.Ordinal);
        private static readonly object receiptGate = new object();
        private static readonly Queue<ScienceReceiptEnvelope> receipts = new Queue<ScienceReceiptEnvelope>();
        internal static string Warning { get; private set; }
        internal static int PendingCount => pending == null ? 0 : pending.Claims.Count(c => Matches(c) && !c.Held);
        internal static int HeldCount => pending == null ? 0 : pending.Claims.Count(c => Matches(c) && c.Held);

        private static bool Matches(PendingScienceClaim c) => PersonalClient.Active && c.ServerId == AetherfrontClientSystem.State.ServerId && c.AccountId == MainSystem.UniqueIdentifier;

        private static bool ValidClaim(PendingScienceClaim c)
        {
            if (c == null || string.IsNullOrEmpty(c.ServerId) || c.ServerId.Length > 256 || string.IsNullOrEmpty(c.AccountId) || c.AccountId.Length > 256 ||
                string.IsNullOrEmpty(c.SourceId) || c.SourceId.Length > 256 || c.SourceId.Any(char.IsControl) ||
                (c.Command != 40 && c.Command != 42) || c.Amount <= 0 || c.Amount > PersonalProgression.MaxScience ||
                c.ChunkIndex < 0 || c.ChunkIndex > PersonalProgression.MaxScience / PersonalProgression.MaxAward || decimal.Round(c.Amount, 6) != c.Amount ||
                c.Attempts < 0 || c.Attempts > ScienceRetryPolicy.MaximumAttempts || c.NextAttemptUtcTicks < 0 || c.NextAttemptUtcTicks > DateTime.MaxValue.Ticks ||
                (c.LastRejection != null && (c.LastRejection.Length > 128 || c.LastRejection.Any(char.IsControl))) ||
                (c.HeldVersion != null && (c.HeldVersion.Length > 64 || c.HeldVersion.Any(char.IsControl)))) return false;
            if (c.Command == 42)
            {
                Guid id;
                if (!c.SourceId.StartsWith("award:", StringComparison.Ordinal) || !Guid.TryParseExact(c.SourceId.Substring(6), "N", out id) || id == Guid.Empty ||
                    c.Reason < 0 || (c.Reason & ~PersonalProgression.ScienceRewardReasons) != 0) return false;
                c.SourceId = "award:" + id.ToString("N");
            }
            return true;
        }

        private static bool Load()
        {
            if (failed) return false;
            if (pending != null) return true;
            try
            {
                string root = Path.GetFullPath(KSPUtil.ApplicationRootPath);
                path = Path.Combine(root, "APF", "PendingScience-v1.json");
                ApfPaths.EnsureNoReparsePoints(root, path);
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Science outbox too large");
                var loaded = File.Exists(path) ? AetherfrontJson.Deserialize<PendingScienceClaims>(File.ReadAllText(path)) : new PendingScienceClaims();
                if (loaded == null || loaded.Version != 1 || loaded.Claims == null || loaded.Claims.Count > 4096 || loaded.Claims.Any(c => !ValidClaim(c)))
                    throw new InvalidDataException("Invalid science outbox; preserve for recovery");
                pending = loaded;
                long latest = DateTime.UtcNow.AddSeconds(ScienceRetryPolicy.MaximumRetrySeconds).Ticks;
                foreach (var c in pending.Claims)
                {
                    if (c.NextAttemptUtcTicks > latest) { c.NextAttemptUtcTicks = latest; dirty = true; }
                    if (c.Held && c.HeldVersion != ApfClientSettings.ClientVersion) { ScienceRetryPolicy.Reset(c); dirty = true; }
                }
                return true;
            }
            catch (Exception error)
            {
                failed = true; Warning = "Science sync could not load pending rewards. Saved evidence was preserved; see KSP.log.";
                Debug.LogError("[Aetherfront] Science outbox load failed: " + error.GetType().Name); return false;
            }
        }

        internal static void Enqueue(PendingScienceClaim claim)
        {
            if (claim == null || claim.Amount <= 0 || !ValidClaim(claim) || !Load()) return;
            var existing = pending.Claims.FirstOrDefault(c => c.ServerId == claim.ServerId && c.AccountId == claim.AccountId && c.Command == claim.Command && c.SourceId == claim.SourceId);
            if (existing != null)
            {
                if (claim.Command == 40 && claim.Amount > existing.Amount) { existing.Amount = claim.Amount; dirty = true; }
                return;
            }
            if (pending.Claims.Count >= 4096)
            {
                Warning = "Science sync has too many pending rewards. Existing records were preserved.";
                Debug.LogError("[Aetherfront] Science outbox capacity reached; existing records preserved."); return;
            }
            pending.Claims.Add(claim); dirty = true;
        }

        internal static void Save(bool force = false)
        {
            if (!dirty || pending == null || failed || (!force && Time.realtimeSinceStartup < nextSave)) return;
            try
            {
                ApfPaths.EnsureNoReparsePoints(KSPUtil.ApplicationRootPath, path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                KesasInstall.Installer.AtomicText(path, AetherfrontJson.Serialize(pending));
                dirty = false; Warning = null;
            }
            catch (Exception error)
            {
                nextSave = Time.realtimeSinceStartup + 5;
                if (Warning != "Science rewards are waiting for local storage. See KSP.log.") Debug.LogError("[Aetherfront] Science outbox save failed: " + error.GetType().Name);
                Warning = "Science rewards are waiting for local storage. See KSP.log.";
            }
        }

        // Called on the network receive path. Only a bounded queue is shared;
        // all claim changes and file access remain on the Unity thread.
        internal static bool Capture(string payload)
        {
            if (string.IsNullOrEmpty(payload) || payload.Length > 16384 || payload.IndexOf("\"ScienceReceipt\"", StringComparison.Ordinal) < 0) return false;
            try
            {
                var e = AetherfrontJson.Deserialize<ScienceReceiptEnvelope>(payload); var r = e?.ScienceReceipt;
                if (r == null || e.ProtocolVersion != 1 || e.Revision < 0 || e.Operation != (int)AetherfrontWireOperation.SnapshotReply || string.IsNullOrEmpty(e.ServerId) || e.ServerId.Length > 256 ||
                    string.IsNullOrEmpty(r.RequestId) || r.RequestId.Length != 40 || !r.RequestId.StartsWith("science:", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(r.AccountId) || r.AccountId.Length > 256 || string.IsNullOrEmpty(r.SourceId) || r.SourceId.Length > 256 || r.SourceId.Any(char.IsControl) ||
                    (r.Command != 40 && r.Command != 42) || r.RequestedAmount <= 0 || r.RequestedAmount > PersonalProgression.MaxScience || r.RecordedCumulative < 0 || r.RecordedCumulative > PersonalProgression.MaxScience ||
                    e.ReasonCode == null || e.ReasonCode.Length > 128 || e.ReasonCode.Any(char.IsControl) ||
                    (r.Disposition != "accepted" && r.Disposition != "terminal" && r.Disposition != "retry") || e.Accepted != (r.Disposition == "accepted")) return false;
                Guid nonce; if (!Guid.TryParseExact(r.RequestId.Substring(8), "N", out nonce) || nonce == Guid.Empty) return false;
                lock (receiptGate) { if (receipts.Count >= 64) receipts.Dequeue(); receipts.Enqueue(e); }
                return true;
            }
            catch { /* An unrecognized optional receipt cannot alter evidence. */ }
            return false;
        }

        private static void Forget(PendingScienceClaim claim)
        {
            foreach (var id in flights.Where(p => ReferenceEquals(p.Value.Claim, claim)).Select(p => p.Key).ToArray()) flights.Remove(id);
        }
        private static void Complete(PendingScienceClaim claim)
        {
            Forget(claim);
            if (claim.AcknowledgeChunk()) pending.Claims.Remove(claim); else ScienceRetryPolicy.Reset(claim);
            dirty = true;
        }
        private static void ProcessReceipts()
        {
            for (;;)
            {
                ScienceReceiptEnvelope e;
                lock (receiptGate) { if (receipts.Count == 0) return; e = receipts.Dequeue(); }
                var r = e.ScienceReceipt; Flight f;
                if (!flights.TryGetValue(r.RequestId, out f)) continue;
                var c = f.Claim;
                if (!pending.Claims.Contains(c) || !Matches(c) || e.ServerId != f.Server || r.AccountId != f.Account || r.Command != f.Command || r.SourceId != f.Source || r.RequestedAmount != f.Amount) continue;
                flights.Remove(r.RequestId);
                if (r.Disposition == "accepted")
                {
                    if (c.GetChunkSourceId() == f.Source && ScienceRetryPolicy.Covered(c.Command, c.GetChunkAmount(), r.RecordedCumulative)) Complete(c);
                    else if (c.GetChunkSourceId() == f.Source) { c.Attempts = 0; c.NextAttemptUtcTicks = 0; dirty = true; }
                    wantedRevision = Math.Max(wantedRevision, e.Revision);
                }
                else if (r.Disposition == "terminal")
                {
                    c.Held = true; c.LastRejection = e.ReasonCode; c.HeldVersion = ApfClientSettings.ClientVersion; dirty = true;
                    Debug.LogWarning("[Aetherfront] Science reward paused: command=" + c.Command + " source=" + f.Source + " requested=" + f.Amount.ToString(CultureInfo.InvariantCulture) + " recorded=" + r.RecordedCumulative.ToString(CultureInfo.InvariantCulture) + " reason=" + e.ReasonCode + ". Evidence preserved in APF/PendingScience-v1.json.");
                }
            }
        }

        internal static void RetryHeld()
        {
            if (!Load()) return;
            foreach (var c in pending.Claims.Where(c => Matches(c) && c.Held)) { ScienceRetryPolicy.Reset(c); dirty = true; }
            Save(true);
        }

        internal static void Tick()
        {
            if (!PersonalClient.Active || HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX)
            { flights.Clear(); wantedRevision = 0; nextSnapshot = 0; lock (receiptGate) receipts.Clear(); return; }
            if (!Load()) return;
            long now = DateTime.UtcNow.Ticks;
            foreach (var id in flights.Where(p => now - p.Value.Sent >= ScienceRetryPolicy.ReceiptTimeoutSeconds * TimeSpan.TicksPerSecond || now < p.Value.Sent).Select(p => p.Key).ToArray()) flights.Remove(id);
            for (int i = pending.Claims.Count - 1; i >= 0; i--)
            {
                var c = pending.Claims[i];
                if (Matches(c) && ScienceRetryPolicy.Covered(c.Command, c.GetChunkAmount(), PersonalClient.CreditedScience(c.GetChunkSourceId()))) Complete(c);
            }
            ProcessReceipts(); Save();
            if (wantedRevision > AetherfrontClientSystem.State.Revision && Time.realtimeSinceStartup >= nextSnapshot)
            { AetherfrontClientSystem.RequestSnapshot(); nextSnapshot = Time.realtimeSinceStartup + 2f; }
            if (dirty || Time.realtimeSinceStartup < nextSend || flights.Count >= ScienceRetryPolicy.MaximumInFlight || pending.Claims.Count == 0) return;
            for (int checkedCount = 0; checkedCount < pending.Claims.Count; checkedCount++)
            {
                cursor %= pending.Claims.Count;
                var c = pending.Claims[cursor]; cursor = (cursor + 1) % pending.Claims.Count;
                if (!Matches(c) || c.Held || c.NextAttemptUtcTicks > now || flights.Values.Any(f => ReferenceEquals(f.Claim, c))) continue;
                ScienceRetryPolicy.Schedule(c, now); dirty = true; Save(true);
                if (dirty) return; // Never send a reward whose retry evidence was not saved.
                string nonce = "science:" + Guid.NewGuid().ToString("N");
                var f = new Flight { Claim = c, Server = c.ServerId, Account = c.AccountId, Command = c.Command, Source = c.GetChunkSourceId(), Amount = c.GetChunkAmount(), Sent = now };
                flights.Add(nonce, f); nextSend = Time.realtimeSinceStartup + 1f;
                try
                {
                    PersonalClient.SendScienceClaim(new AetherfrontWireRequest { CommandId = nonce, CommandType = (AetherfrontCommandType)c.Command, EntityId = f.Source, Body = f.Amount.ToString(CultureInfo.InvariantCulture), Amount2 = c.Reason });
                }
                catch (Exception error) { flights.Remove(nonce); Debug.LogWarning("[Aetherfront] Science reward send failed; retry scheduled: " + error.GetType().Name); }
                return;
            }
            nextSend = Time.realtimeSinceStartup + .25f;
        }
    }
}
