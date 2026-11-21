using Aetherfront.Core;
using Aetherfront.Tracking;
using Server.System;
using System;
using System.Collections.Generic;

namespace Server.Aetherfront
{
    // Donor stub: only ReadTrackingCatalog is copied. Fields bind to the existing store.
    public sealed class AetherfrontServerStore
    {
        private readonly object _gate = new object();
        private AetherfrontState _state = new AetherfrontState();

        public List<Marker> ReadTrackingCatalog(string account, int subspace)
        {
            lock (_gate)
                return TrackingCatalog.Build(_state, account, subspace, DateTime.UtcNow,
                    VesselStoreSystem.CurrentVessels, VesselContext.RemovedVessels);
        }
    }
}
