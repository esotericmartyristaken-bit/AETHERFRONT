using System;

namespace Aetherfront.Personal.Client
{
    public static class ScienceRetryPolicy
    {
        public const int MaximumAttempts = 30;
        public const int MaximumInFlight = 2;
        public const int ReceiptTimeoutSeconds = 15;
        public const int MaximumRetrySeconds = 60;

        public static long Schedule(PendingScienceClaim claim, long now)
        {
            if (claim == null || now < 0 || now > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid retry input");
            claim.Attempts = Math.Min(MaximumAttempts, Math.Min(MaximumAttempts, Math.Max(0, claim.Attempts)) + 1);
            int delay = Math.Min(MaximumRetrySeconds, 2 << Math.Min(claim.Attempts - 1, 5));
            claim.NextAttemptUtcTicks = Math.Min(DateTime.MaxValue.Ticks, now + delay * TimeSpan.TicksPerSecond);
            return claim.NextAttemptUtcTicks;
        }

        public static decimal Tolerance(int command, decimal amount) => command == 40 ? Math.Max(.000001m, amount / 10000000m) : 0m;
        public static bool Covered(int command, decimal requested, decimal recorded) => recorded > 0 && recorded + Tolerance(command, requested) >= requested;
        public static void Reset(PendingScienceClaim claim)
        {
            claim.Attempts = 0; claim.NextAttemptUtcTicks = 0;
            claim.Held = false; claim.LastRejection = string.Empty; claim.HeldVersion = string.Empty;
        }
    }
}
