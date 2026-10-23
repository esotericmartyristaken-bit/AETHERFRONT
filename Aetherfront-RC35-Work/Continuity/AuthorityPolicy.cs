using System;

namespace Aetherfront.Continuity
{
    public static class AuthorityPolicy
    {
        // Control owner wins over stale update/unloaded locks during handoff.
        // Missing authority freezes simulation instead of guessing from visibility.
        public static bool MaySimulate(string player, string controlOwner, string updateOwner, string unloadedOwner, bool loaded, string survivalOwner)
        {
            if (string.IsNullOrEmpty(player)) return false;
            if (!string.IsNullOrEmpty(controlOwner)) return string.Equals(controlOwner, player, StringComparison.Ordinal);
            // Automatic background lock reassignment must not hand a paused
            // faction vessel's survival clock to another player's warped timeline.
            if (!string.Equals(player,survivalOwner,StringComparison.Ordinal))return false;
            if (!string.IsNullOrEmpty(updateOwner)) return loaded && string.Equals(updateOwner, player, StringComparison.Ordinal);
            return !loaded && !string.IsNullOrEmpty(unloadedOwner) && string.Equals(unloadedOwner, player, StringComparison.Ordinal);
        }
    }
}
