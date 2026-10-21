using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    // Kerbal Konstructs repositions PRELAUNCH vessels on every off-rails event.
    // Map-view packing must not repeat the initial launch-site placement or
    // move another multiplayer vessel to this client's selected launch site.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class LaunchPositionCompatibility : MonoBehaviour
    {
        private static readonly HashSet<Guid> Positioned = new HashSet<Guid>();
        private static Type sites;
        private bool finished;
        private float retryAt;
        private void Awake() { DontDestroyOnLoad(this); GameEvents.onVesselDestroy.Add(Forget); }
        private void OnDestroy() { GameEvents.onVesselDestroy.Remove(Forget); }
        private void Forget(Vessel vessel) { if (vessel != null) Positioned.Remove(vessel.id); }
        private void Update()
        {
            if (HighLogic.LoadedScene == GameScenes.MAINMENU) Positioned.Clear();
            if (finished || Time.realtimeSinceStartup < retryAt) return;
            retryAt = Time.realtimeSinceStartup + 10;
            var harmony = Type.GetType("HarmonyLib.Harmony, 0Harmony", false);
            var harmonyMethod = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", false);
            var targetType = Type.GetType("KerbalKonstructs.KerbalKonstructs, KerbalKonstructs", false);
            if (harmony == null || harmonyMethod == null || targetType == null) return;
            try
            {
                sites = targetType.Assembly.GetType("KerbalKonstructs.Core.LaunchSiteManager", true);
                var target = targetType.GetMethod("FixWaterLaunch", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Vessel) }, null);
                if (target == null) throw new MissingMethodException("Kerbal Konstructs launch positioning hook");
                var patch = Array.Find(harmony.GetMethods(), m => m.Name == "Patch" && m.GetParameters().Length >= 3 && m.GetParameters()[0].ParameterType == typeof(MethodBase));
                var args = new object[patch.GetParameters().Length]; args[0] = target;
                args[1] = Activator.CreateInstance(harmonyMethod, typeof(LaunchPositionCompatibility).GetMethod(nameof(BeforePosition)));
                patch.Invoke(Activator.CreateInstance(harmony, "aetherfront.launch-position-once"), args);
                finished = true;
                Debug.Log("[Aetherfront] Launch-site positioning compatibility ready.");
            }
            catch (Exception error)
            {
                finished = true;
                Debug.LogError("[Aetherfront] Launch-site compatibility failed: " + (error.InnerException ?? error));
            }
        }
        public static bool BeforePosition(Vessel __0)
        {
            if (__0 == null || __0.situation != Vessel.Situations.PRELAUNCH) return false;
            if (FlightGlobals.ActiveVessel != __0 || Positioned.Contains(__0.id)) return false;
            // A persisted PRELAUNCH craft can be loaded while a different site
            // is selected. The old handler ignores the craft's launch origin.
            string selected = sites.GetMethod("getCurrentLaunchSite", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string;
            if (!string.Equals(__0.launchedFrom, selected, StringComparison.Ordinal)) return false;
            Positioned.Add(__0.id);
            return true;
        }
    }
}
