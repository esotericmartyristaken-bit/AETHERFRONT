using System;
using System.Reflection;
using Aetherfront.Core;
using Server.Aetherfront;

namespace Aetherfront.Synchronization.Server
{
    public static class SnapshotStore
    {
        static readonly FieldInfo Gate = Field("_gate"), Committed = Field("_committedJson"), State = Field("_state"), Deferred = Field("_deferCommits");
        static FieldInfo Field(string name) => typeof(AetherfrontServerStore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(AetherfrontServerStore).FullName, name);
        public static long Revision(AetherfrontServerStore store)
        { lock (Gate.GetValue(store)) return ((AetherfrontState)State.GetValue(store)).Revision; }
        public static bool Current(AetherfrontServerStore store, string account, long revision)
        {
            lock (Gate.GetValue(store))
            {
                var state = (AetherfrontState)State.GetValue(store);
                return state.Revision == revision && state.FindAccount(account)?.Suspended == false;
            }
        }
        public static AetherfrontState Read(AetherfrontServerStore store)
        {
            string json; bool taken = false; object gate = Gate.GetValue(store);
            try
            {
                SyncTimings.EnterStore(gate, ref taken);
                json = (string)Committed.GetValue(store);
                // Bootstrap has no committed image yet. Do not substitute stale persisted data there.
                if ((bool)Deferred.GetValue(store) || json == null)
                {
                    long began = SyncTimings.Start();
                    try { json = AetherfrontJson.Serialize((AetherfrontState)State.GetValue(store)); }
                    finally { SyncTimings.Complete("snapshot.bootstrap.serialize", began); }
                }
            }
            finally { if (taken) SyncTimings.Exit(gate); }
            long deserialize = SyncTimings.Start();
            try { return AetherfrontJson.Deserialize<AetherfrontState>(json); }
            finally { SyncTimings.Complete("snapshot.deserialize.outside-lock", deserialize); }
        }
    }
}
