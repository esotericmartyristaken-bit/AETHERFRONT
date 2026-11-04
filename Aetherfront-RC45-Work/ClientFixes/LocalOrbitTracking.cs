using System;
using LmpClient;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;

namespace Aetherfront.Continuity.Client
{
    /// <summary>Retain stock kinematic tracking only for the authenticated local active vessel.</summary>
    public static class LocalOrbitTracking
    {
        // Insert after the existing connected/non-null-vessel prefix gates.
        // True returns control to stock; false preserves the original LMP path.
        public static bool UseStockKinematicTracking(OrbitDriver driver)
        {
            if (MainSystem.NetworkState != ClientState.Running || driver == null) return false;
            Vessel vessel = driver.vessel;
            if (vessel == null || vessel != FlightGlobals.ActiveVessel || VesselCommon.IsSpectating) return false;
            Part root = vessel.rootPart;
            if (root == null || root.rb == null || !root.rb.isKinematic) return false;

            SettingStructure settings = SettingsSystem.CurrentSettings;
            if (settings == null || string.IsNullOrWhiteSpace(settings.PlayerName)) return false;
            if (LockSystem.LockQuery == null) return false;
            string owner = LockSystem.LockQuery.GetControlLockOwner(vessel.id);
            bool ownsControl = !string.IsNullOrEmpty(owner) &&
                string.Equals(owner, settings.PlayerName, StringComparison.Ordinal);
            if (!ownsControl) return false;

            VesselPositionSystem positions = VesselPositionSystem.Singleton;
            if (positions == null) return false;
            return IsEligible(true, true, false, true, true, ownsControl,
                positions.VesselHavePositionUpdatesQueued(vessel.id));
        }

        // Pure policy seam matching the native eligibility matrix. No identities,
        // craft names, altitude thresholds, physics mutation or queue mutation.
        public static bool IsEligible(bool isRunning, bool isActiveVessel, bool isSpectating,
            bool hasRootRigidbody, bool isKinematic, bool ownsControlLock, bool hasIncomingPositionUpdates)
        {
            return isRunning && isActiveVessel && !isSpectating && hasRootRigidbody &&
                isKinematic && ownsControlLock && !hasIncomingPositionUpdates;
        }
    }
}
