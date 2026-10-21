using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Aetherfront.Core;
using ApfCore;
using LmpClient;
using LmpClient.Aetherfront;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    // Persist before sending, retire only after the authoritative ledger echoes
    // the source high-water/receipt. Reconnects and retries cannot mint twice.
    internal static class ScienceOutbox
    {
        private static PendingScienceClaims pending;
        private static bool dirty, failed;
        private static int cursor;
        private static float nextSend;
        private static float nextSave;
        private static string path;
        internal static string Warning { get; private set; }
        internal static int PendingCount => pending == null ? 0 : pending.Claims.Count(c => Matches(c));

        private static bool Matches(PendingScienceClaim claim) => PersonalClient.Active &&
            claim.ServerId == AetherfrontClientSystem.State.ServerId && claim.AccountId == MainSystem.UniqueIdentifier;

        private static bool ValidClaim(PendingScienceClaim c)
        {
            if (c == null || string.IsNullOrEmpty(c.ServerId) || c.ServerId.Length > 256 || string.IsNullOrEmpty(c.AccountId) || c.AccountId.Length > 256 ||
                string.IsNullOrEmpty(c.SourceId) || c.SourceId.Length > 256 || c.SourceId.Any(char.IsControl) ||
                (c.Command != 40 && c.Command != 42) || c.Amount <= 0 || c.Amount > PersonalProgression.MaxScience ||
                c.ChunkIndex < 0 || c.ChunkIndex > PersonalProgression.MaxScience / PersonalProgression.MaxAward || decimal.Round(c.Amount, 6) != c.Amount) return false;
            if (c.Command == 42)
            {
                Guid receipt;
                if (!c.SourceId.StartsWith("award:", StringComparison.Ordinal) || !Guid.TryParseExact(c.SourceId.Substring(6), "N", out receipt) || receipt == Guid.Empty ||
                    c.Reason < 0 || (c.Reason & ~PersonalProgression.ScienceRewardReasons) != 0) return false;
                c.SourceId = "award:" + receipt.ToString("N");
            }
            return true;
        }

        private static bool Confirmed(PendingScienceClaim claim)
        {
            if (!Matches(claim)) return false;
            decimal recorded = PersonalClient.CreditedScience(claim.GetChunkSourceId());
            // Stock cumulative floats can exceed a completed cap by one ULP.
            // The server clamps that ULP; acknowledge its positive high-water.
            decimal tolerance = claim.Command == 40 ? Math.Max(.000001m, claim.Amount / 10000000m) : 0;
            return recorded > 0 && recorded + tolerance >= claim.GetChunkAmount();
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
                return true;
            }
            catch (Exception error)
            {
                failed = true;
                Warning = "Science sync could not load its pending rewards. See KSP.log; saved evidence was preserved.";
                Debug.LogError("[Aetherfront] Science outbox load failed: " + error.GetType().Name);
                return false;
            }
        }

        internal static void Enqueue(PendingScienceClaim claim)
        {
            if (claim.Amount <= 0 || !Load()) return;
            var existing = pending.Claims.FirstOrDefault(c => c.ServerId == claim.ServerId && c.AccountId == claim.AccountId && c.Command == claim.Command && c.SourceId == claim.SourceId);
            if (existing != null)
            {
                if (claim.Command == 40 && claim.Amount > existing.Amount) { existing.Amount = claim.Amount; dirty = true; }
                return;
            }
            if (pending.Claims.Count >= 4096)
            {
                Warning = "Science sync has too many pending rewards. Reconnect before collecting more science.";
                Debug.LogError("[Aetherfront] Science outbox capacity reached; existing records preserved.");
                return;
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
                if (Warning != "Science rewards are waiting for local storage. See KSP.log.")
                    Debug.LogError("[Aetherfront] Science outbox save failed: " + error.GetType().Name);
                Warning = "Science rewards are waiting for local storage. See KSP.log.";
            }
        }

        internal static void Tick()
        {
            if (!PersonalClient.Active || HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX || !Load()) return;
            for (int i = pending.Claims.Count - 1; i >= 0; i--)
                if (Confirmed(pending.Claims[i]))
                {
                    if (pending.Claims[i].AcknowledgeChunk()) pending.Claims.RemoveAt(i);
                    dirty = true;
                }
            Save();
            if (dirty || Time.realtimeSinceStartup < nextSend || pending.Claims.Count == 0) return;
            nextSend = Time.realtimeSinceStartup + .5f;
            for (int checkedCount = 0; checkedCount < pending.Claims.Count; checkedCount++)
            {
                cursor %= pending.Claims.Count;
                var claim = pending.Claims[cursor++];
                if (!Matches(claim)) continue;
                PersonalClient.SendScienceClaim(new AetherfrontWireRequest
                {
                    CommandType = (AetherfrontCommandType)claim.Command, EntityId = claim.GetChunkSourceId(),
                    Body = claim.GetChunkAmount().ToString(CultureInfo.InvariantCulture), Amount2 = claim.Reason
                });
                break;
            }
        }
    }
}
