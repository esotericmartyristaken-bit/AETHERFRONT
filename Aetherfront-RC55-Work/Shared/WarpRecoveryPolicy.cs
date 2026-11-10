using System;

namespace Aetherfront.Stability
{
    public sealed class WarpRecoveryPolicy
    {
        public const double TimeoutSeconds = 15, RetrySeconds = 2;
        const double SafeSceneDebtLimit = 60, ClockMargin = 5;
        public bool Pending { get; private set; }
        public double Proposal { get; private set; }
        public double Started { get; private set; }
        public int Generation { get; private set; }
        public int LastAccepted { get; private set; } = -1;
        double lastSent;
        public void Begin(int generation, double proposal, double now)
        {
            if (!Finite(proposal) || !Finite(now)) throw new ArgumentException("Finite warp boundary required.");
            Pending = true; Generation = generation; Proposal = proposal; Started = now; lastSent = now - RetrySeconds;
        }
        public bool ShouldSend(double now)
        { if (!Pending || now - Started >= TimeoutSeconds || now - lastSent < RetrySeconds) return false; lastSent = now; return true; }
        public bool Expired(double now) => Pending && now - Started >= TimeoutSeconds;
        public bool CanAccept(int generation, int id, double offset) => Pending && generation == Generation && id >= 0 && id >= LastAccepted &&
            Finite(offset) && offset >= Proposal - 0.01 && offset <= Proposal + 3.500001;
        // A flight scene load can pause simulation while the last accepted
        // personal timeline advances. Budget only the measured, trusted floor
        // debt at request time, and only where skipping it is safe.
        public static double ForwardBudget(double naturalProposal, double proposal, bool safeToCatchUp)
            => TimeoutSeconds + ClockMargin + (safeToCatchUp && Finite(naturalProposal) && Finite(proposal)
                ? Math.Min(SafeSceneDebtLimit, Math.Max(0, proposal - naturalProposal)) : 0);
        public void Accepted(int id) { LastAccepted = Math.Max(LastAccepted, id); Pending = false; }
        public void Reset() { Pending = false; LastAccepted = -1; }
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
