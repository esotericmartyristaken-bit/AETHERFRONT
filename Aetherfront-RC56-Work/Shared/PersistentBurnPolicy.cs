using System;

namespace Aetherfront.Synchronization
{
    // Ephemeral pilot intent, never restored from a mod's persisted throttle cache.
    public sealed class PersistentBurnPolicy
    {
        public bool Armed { get; private set; }
        public float Demand { get; private set; }
        public long Topology { get; private set; }
        public int Generation { get; private set; }
        public bool SawCutoff { get; private set; }
        bool initialized;

        public void Observe(long topology, int generation, bool protectedPhysics, bool activePilot, float throttle)
        {
            if (!initialized || Topology != topology || Generation != generation)
            {
                initialized = true; Topology = topology; Generation = generation;
                Armed = false; Demand = 0; SawCutoff = false;
            }
            // A synchronization lease temporarily writes zero. It is not pilot cutoff.
            if (protectedPhysics || !activePilot) return;
            if (float.IsNaN(throttle) || float.IsInfinity(throttle) || throttle < 0 || throttle > 1)
            { Invalidate(); return; }
            if (throttle <= 0.00001f) { Demand = 0; Armed = false; SawCutoff = true; return; }
            if (SawCutoff) { Demand = throttle; Armed = true; }
        }
        public void Invalidate() { Armed = false; Demand = 0; SawCutoff = false; }
        public bool Allows(bool authority, bool protectedPhysics, bool intentionalRailsWarp, bool ignition, bool heading)
            => initialized && Armed && Demand > 0 && authority && !protectedPhysics && intentionalRailsWarp && ignition && heading;

        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        // Limit each impulse to 0.25% of local orbital speed. The warp-rate gate
        // normally enforces this before time advances; vector validation is backstop.
        public static double DeltaVBudget(double speed) => Finite(speed) && speed > 0 ? Math.Max(0.05, speed * 0.0025) : 0;
        public static bool ValidStep(double mass, double dt, double thrust, double isp, double speed, double deltaV)
            => Finite(mass) && mass > 0 && Finite(dt) && dt > 0 && Finite(thrust) && thrust >= 0 &&
               Finite(isp) && isp > 0 && Finite(deltaV) && deltaV >= 0 && deltaV <= DeltaVBudget(speed);
    }
}
