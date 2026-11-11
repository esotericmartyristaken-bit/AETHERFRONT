using System;

namespace Aetherfront.Synchronization
{
    // Pure policy: network arrival time, not Unity frame time, decides lateness.
    public sealed class WarpRecoveryPolicy
    {
        public const double TimeoutSeconds = 15, RetrySeconds = 2;
        public bool Pending { get; private set; }
        public double Proposal { get; private set; }
        public double Started { get; private set; }
        public int Generation { get; private set; }
        public int LastAccepted { get; private set; } = -1;
        public long Session { get; private set; }
        public long Request { get; private set; }
        double lastSent;

        public void Begin(int generation, long session, long request, double proposal, double now)
        {
            if (session == 0 || request <= 0 || !Finite(proposal) || !Finite(now))
                throw new ArgumentException("A finite, identified warp boundary is required.");
            Pending = true; Generation = generation; Session = session; Request = request;
            Proposal = proposal; Started = now; lastSent = now - RetrySeconds;
        }
        public bool ShouldSend(double now)
        {
            if (!Pending || now - Started >= TimeoutSeconds || now - lastSent < RetrySeconds) return false;
            lastSent = now; return true;
        }
        public bool Expired(double now) => Pending && now - Started >= TimeoutSeconds;
        public bool CanAccept(int generation, long session, long request, int id, double offset, double received)
            => Pending && generation == Generation && session == Session && request == Request &&
                Finite(received) && received >= Started && received - Started < TimeoutSeconds &&
                id >= 0 && id >= LastAccepted && Finite(offset) && offset >= Proposal - 0.01 && offset <= Proposal + 3.500001;
        public static double ForwardBudget(double naturalProposal, double proposal, bool safeToCatchUp)
            => TimeoutSeconds + 5 + (safeToCatchUp && Finite(naturalProposal) && Finite(proposal)
                ? Math.Min(60, Math.Max(0, proposal - naturalProposal)) : 0);
        public void Accepted(int id) { LastAccepted = Math.Max(LastAccepted, id); Pending = false; }
        public void Cancel() { Pending = false; }
        public void Reset() { Pending = false; LastAccepted = -1; Session = Request = 0; }
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
