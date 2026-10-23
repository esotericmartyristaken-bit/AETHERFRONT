using System;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.Warp;

namespace Aetherfront.Continuity.Client
{
    public static class PersonalClockClient
    {
        // Called only by the original scene handler after its enabled/scene/once guards.
        public static int InitialSubspace(WarpSystem system)
        {
            if (!SimulationAuthority.Ready)
                throw new InvalidOperationException("Life support protection did not initialize. Refusing to enter multiplayer without simulation authority guards.");
            int id;
            if (!system.ClientSubspaceList.TryGetValue(SettingsSystem.CurrentSettings.PlayerName, out id) ||
                id < 0 || !system.Subspaces.ContainsKey(id))
                throw new InvalidOperationException("The server did not provide your saved mission time. Refusing to advance to another player's time.");
            return id;
        }
    }
}
