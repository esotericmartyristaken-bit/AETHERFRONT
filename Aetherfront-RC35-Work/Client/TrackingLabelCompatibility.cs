using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class TrackingLabelCompatibility
    {
        private static MethodInfo clearLabels;
        internal static void Install(Harmony harmony)
        {
            if(typeof(VesselLabels).Assembly.ManifestModule.ModuleVersionId!=new Guid("4b449f28-41f8-4227-adfa-ad3149c8fdba"))
                throw new InvalidOperationException("Review changed KSP vessel-label lifecycle");
            var awake=AccessTools.DeclaredMethod(typeof(VesselLabels),"Awake",Type.EmptyTypes);
            var destroy=AccessTools.DeclaredMethod(typeof(VesselLabels),"OnDestroy",Type.EmptyTypes);
            clearLabels=AccessTools.DeclaredMethod(typeof(VesselLabels),"DestroyAllLabels",Type.EmptyTypes);
            if(awake==null||destroy==null||clearLabels==null||awake.ReturnType!=typeof(void)||clearLabels.ReturnType!=typeof(void))
                throw new InvalidOperationException("Native vessel-label lifecycle signatures changed");
            harmony.Patch(awake,prefix:new HarmonyMethod(typeof(TrackingLabelCompatibility),nameof(BeforeAwake)));
        }
        private static void BeforeAwake(VesselLabels __instance)
        {
            if(!SimulationAuthority.InMultiplayer||HighLogic.LoadedScene!=GameScenes.TRACKSTATION||
                __instance==null||__instance.GetComponent<SpaceTracking>()==null)return;
            var previous=VesselLabels.Instance;
            if(previous==null||previous==__instance||previous.gameObject==__instance.gameObject)return;
            var oldScene=previous.gameObject.scene;var newScene=__instance.gameObject.scene;
            if(!oldScene.IsValid()||!oldScene.isLoaded||oldScene==newScene)return;
            // Stock duplicate Awake destroys its entire gameObject. Here that
            // object also owns SpaceTracking, so a lingering flight label would
            // delete the new station before its Start can load any scenarios.
            // Retire only the departing label component, using its native label
            // cleanup and OnDestroy unsubscription; keep both scene roots intact.
            clearLabels.Invoke(previous,null);
            UnityEngine.Object.DestroyImmediate(previous);
            if(VesselLabels.Instance!=null)throw new InvalidOperationException("Departing vessel-label singleton did not release normally");
            Debug.Log("[AetherfrontContinuity] Retired departing vessel-label component before Tracking Station startup; station root retained");
        }
    }
}
