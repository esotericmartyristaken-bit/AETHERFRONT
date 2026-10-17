using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.Aetherfront
{
    /// <summary>
    /// Keeps scenario progress baselines per authenticated client. LMP shares scenario totals,
    /// not authoritative APF deltas; this guard prevents cross-player baselines and rejects
    /// implausible jumps before they reach faction progression.
    /// </summary>
    internal static class AetherfrontProgressionGuard
    {
        private const double ScienceMaxPerMessage = 1000000d;
        private const double ScienceMaxPerWindow = 5000000d;
        private const double FundsMaxMicroPerMessage = 1000000000000d;
        private const double FundsMaxMicroPerWindow = 5000000000000d;
        private const double ReputationMaxPerMessage = 10000d;
        private const double ReputationMaxPerWindow = 100000d;
        private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(10);
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Baseline> Baselines = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);

        public static bool TryGetScienceDelta(string accountId, double current, out long delta)
        {
            return TryGetDelta(accountId, Resource.Science, current, ScienceMaxPerMessage, ScienceMaxPerWindow, out delta);
        }

        public static bool TryGetFundsDelta(string accountId, double currentFunds, out long deltaMicro)
        {
            if (!TryGetDelta(accountId, Resource.Funds, currentFunds, FundsMaxMicroPerMessage / 1000000d,
                FundsMaxMicroPerWindow / 1000000d, out var deltaFunds))
            {
                deltaMicro = 0;
                return false;
            }
            deltaMicro = (long)Math.Floor(deltaFunds * 1000000d);
            return deltaMicro > 0;
        }

        public static bool TryGetReputationDelta(string accountId, double current, out long delta)
        {
            return TryGetDelta(accountId, Resource.Reputation, current, ReputationMaxPerMessage, ReputationMaxPerWindow, out delta);
        }

        private static bool TryGetDelta(string accountId, Resource resource, double current, double maxPerMessage,
            double maxPerWindow, out long delta)
        {
            delta = 0;
            if (string.IsNullOrWhiteSpace(accountId) || double.IsNaN(current) || double.IsInfinity(current) || current < 0)
                return false;

            lock (Gate)
            {
                if (!Baselines.TryGetValue(accountId, out var baseline))
                {
                    baseline = new Baseline { LastSeenUtc = DateTime.UtcNow };
                    Baselines[accountId] = baseline;
                    SetCurrent(baseline, resource, current);
                    TrimIfNeeded();
                    return false;
                }

                var previous = GetCurrent(baseline, resource);
                SetCurrent(baseline, resource, current);
                var now = DateTime.UtcNow;
                if (now - baseline.WindowStartedUtc >= WindowLength)
                {
                    baseline.WindowStartedUtc = now;
                    baseline.ScienceWindowTotal = 0;
                    baseline.FundsWindowTotal = 0;
                    baseline.ReputationWindowTotal = 0;
                }
                baseline.LastSeenUtc = now;
                if (current <= previous + 0.000001d) return false;

                var rawDelta = current - previous;
                var windowTotal = GetWindowTotal(baseline, resource);
                if (rawDelta > maxPerMessage || windowTotal + rawDelta > maxPerWindow)
                {
                    LunaLog.Warning($"Aetherfront progression guard rejected {resource} jump {rawDelta:0.######} for account {accountId}");
                    return false;
                }

                SetWindowTotal(baseline, resource, windowTotal + rawDelta);
                delta = (long)Math.Floor(rawDelta);
                return delta > 0;
            }
        }

        private static double GetCurrent(Baseline baseline, Resource resource)
        {
            switch (resource)
            {
                case Resource.Science: return baseline.Science;
                case Resource.Funds: return baseline.Funds;
                default: return baseline.Reputation;
            }
        }

        private static void SetCurrent(Baseline baseline, Resource resource, double value)
        {
            switch (resource)
            {
                case Resource.Science: baseline.Science = value; break;
                case Resource.Funds: baseline.Funds = value; break;
                default: baseline.Reputation = value; break;
            }
        }

        private static double GetWindowTotal(Baseline baseline, Resource resource)
        {
            switch (resource)
            {
                case Resource.Science: return baseline.ScienceWindowTotal;
                case Resource.Funds: return baseline.FundsWindowTotal;
                default: return baseline.ReputationWindowTotal;
            }
        }

        private static void SetWindowTotal(Baseline baseline, Resource resource, double value)
        {
            switch (resource)
            {
                case Resource.Science: baseline.ScienceWindowTotal = value; break;
                case Resource.Funds: baseline.FundsWindowTotal = value; break;
                default: baseline.ReputationWindowTotal = value; break;
            }
        }

        private static void TrimIfNeeded()
        {
            if (Baselines.Count <= 2048) return;
            foreach (var key in new List<string>(Baselines.Keys))
            {
                if (Baselines.Count <= 1536) break;
                Baselines.Remove(key);
            }
        }

        private enum Resource { Science, Funds, Reputation }

        private sealed class Baseline
        {
            public double Science;
            public double Funds;
            public double Reputation;
            public double ScienceWindowTotal;
            public double FundsWindowTotal;
            public double ReputationWindowTotal;
            public DateTime WindowStartedUtc = DateTime.UtcNow;
            public DateTime LastSeenUtc;
        }
    }
}
