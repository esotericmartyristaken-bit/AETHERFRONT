using System;
using System.Linq;

namespace Aetherfront.Synchronization
{
    public static class CrewRetryScope
    {
        public static Guid[] Reconcile(Guid[] scope, bool inFlight, Func<Guid, bool> owns, Action<Guid[], Guid[]> preserve)
        {
            if (scope == null || inFlight) return scope;
            var retained = scope.Where(owns).ToArray();
            if (retained.Length == scope.Length) return scope;
            var retired = scope.Except(retained).ToArray();
            // Never retire unsent retry work unless its local recovery is durable.
            preserve(retired, retained);
            return retained;
        }
    }
}
