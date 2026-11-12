using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Aetherfront.Synchronization;
using HarmonyLib;
using LmpClient;
using LmpCommon.Enums;
using UnityEngine;

namespace Aetherfront.Stability.Client
{
    public static class CrewSaveRecovery
    {
        static FieldInfo scope, committed;
        static PropertyInfo pending, restoring, recapture, error;
        static MethodInfo maySimulate;
        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("LmpClient.Systems.Scenario.DeepFreezeClientSystem");
            scope = AccessTools.Field(type, "recaptureScope"); committed = AccessTools.Field(type, "committedBytes");
            pending = AccessTools.Property(type, "SavePending"); restoring = AccessTools.Property(type, "IsRestoring");
            recapture = AccessTools.Property(type, "NeedsRecapture"); error = AccessTools.Property(type, "LastSaveError");
            maySimulate = AccessTools.Method(AccessTools.TypeByName("Aetherfront.Continuity.Client.SimulationAuthority"), "MaySimulate", new[] { typeof(Vessel) });
            var capture = AccessTools.Method(type, "CaptureAndSend", Type.EmptyTypes);
            if (scope == null || committed == null || pending == null || restoring == null || recapture == null || error == null || maySimulate == null || capture == null)
                throw new MissingMemberException("Shared crew-save recovery contract changed.");
            // Run before the existing background adapter, including its First
            // priority prefix, so it never sees an already invalid retry scope.
            var owners = Harmony.GetPatchInfo(capture)?.Prefixes.Select(p => p.owner).Distinct().ToArray() ?? new string[0];
            harmony.Patch(capture, prefix: new HarmonyMethod(typeof(CrewSaveRecovery), nameof(BeforeCapture)) { priority = Priority.First, before = owners });
        }
        static bool Owns(Guid id)
        {
            var vessel = FlightGlobals.FindVessel(id);
            return vessel != null && (bool)maySimulate.Invoke(null, new object[] { vessel });
        }
        static void BeforeCapture()
        {
            if (MainSystem.NetworkState != ClientState.Running || (bool)pending.GetValue(null, null) || (bool)restoring.GetValue(null, null)) return;
            var old = scope.GetValue(null) as Guid[];
            if (old == null) return;
            var next = CrewRetryScope.Reconcile(old, false, Owns, (retired, retained) => Preserve(retired, retained));
            if (ReferenceEquals(next, old)) return;
            scope.SetValue(null, next.Length == 0 ? null : next);
            if (next.Length == 0) { recapture.SetValue(null, false, null); error.SetValue(null, null, null); }
            Debug.LogWarning("[Aetherfront Stability] Retired stale crew-save retry scope after durable local recovery; removed=" + (old.Length - next.Length) + "; retained=" + next.Length + ". Shared crew state and vessel ownership were not changed.");
        }
        static void Preserve(Guid[] retired, Guid[] retained)
        {
            var game = HighLogic.CurrentGame;
            if (game == null) throw new InvalidOperationException("Crew-save recovery requires the current game; retry scope retained.");
            var root = new ConfigNode("AETHERFRONT_DEFERRED_CREW_SAVE");
            root.AddValue("savedUtc", DateTime.UtcNow.ToString("O"));
            foreach (var id in retired) root.AddValue("retiredRetryVessel", id);
            foreach (var id in retained) root.AddValue("remainingRetryVessel", id);
            game.CrewRoster.Save(root.AddNode("LOCAL_ROSTER"));
            foreach (var module in game.scenarios.Where(s => s != null && (s.moduleName == "DeepFreeze" || s.moduleName == "DF.DeepFreeze" || s.moduleRef?.GetType().FullName == "DF.DeepFreeze")))
                module.Save(root.AddNode("LOCAL_DEEPFREEZE"));
            if (game.flightState != null)
                foreach (var vessel in game.flightState.protoVessels.Where(v => v != null && retired.Contains(v.vesselID)))
                    vessel.Save(root.AddNode("LOCAL_VESSEL"));
            var confirmed = committed.GetValue(null) as byte[];
            if (confirmed != null) root.AddValue("confirmedSharedBaseline", Convert.ToBase64String(confirmed));
            root.AddValue("notServerAcknowledgement", true);
            var bytes = Encoding.UTF8.GetBytes(root.ToString());
            if (bytes.Length > 33554432) throw new IOException("Deferred crew recovery exceeds 32 MiB; retry scope retained.");
            string directory = Path.Combine(KSPUtil.ApplicationRootPath, "APF", "DeferredCrewSaves");
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N") + ".cfg"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
    }
}
