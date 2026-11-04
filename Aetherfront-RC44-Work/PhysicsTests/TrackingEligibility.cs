// Candidate predicate only. Not installed in, or referenced by, shipping code.
public static class TrackingEligibility
{
    // Call only after the existing connected-session and non-null-vessel gates.
    // Ownership and queued-position facts must come from the current LMP session.
    public static bool UseStock(OrbitDriver driver, Vessel activeVessel,
        bool spectating, bool ownsControlLock, bool hasPositionUpdatesQueued)
    {
        if (driver == null || driver.vessel == null || activeVessel == null ||
            driver.vessel != activeVessel || spectating || !ownsControlLock ||
            hasPositionUpdatesQueued) return false;
        Part root = driver.vessel.rootPart;
        return root != null && root.rb != null && root.rb.isKinematic;
    }
}
