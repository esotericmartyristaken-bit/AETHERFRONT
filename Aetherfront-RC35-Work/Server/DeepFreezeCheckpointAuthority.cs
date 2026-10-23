using System;
using LmpCommon.Locks;
using Server.System;
using Server.System.Vessel;

namespace Aetherfront.Continuity.Server
{
    public static class DeepFreezeCheckpointAuthority
    {
        // Replaces only the coordinator's control-lock predicate. Its verified
        // session, fleet permissions and atomic tuple validation remain intact.
        public static bool MayCheckpointFreeze(LockQuery query,Guid id,string player)
        {
            if(query==null||string.IsNullOrEmpty(player))return false;
            string control=query.GetControlLockOwner(id);
            if(!string.IsNullOrEmpty(control))return string.Equals(control,player,StringComparison.Ordinal);
            if(!string.IsNullOrEmpty(query.GetUpdateLockOwner(id))||
                !string.Equals(query.GetUnloadedUpdateLockOwner(id),player,StringComparison.Ordinal))return false;
            if(!VesselStoreSystem.CurrentVessels.TryGetValue(id,out var vessel))return false;
            var saved=vessel.VesselModules.GetNode("SurvivalClock")?.Value.GetNode("APF_SURVIVAL")?.Value;
            return saved!=null&&string.Equals(saved.GetValue("owner")?.Value,player,StringComparison.Ordinal);
        }
    }
}
