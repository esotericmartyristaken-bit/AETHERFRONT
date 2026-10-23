using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    internal static class MapUpdateCompatibility
    {
        internal static void Install(Harmony harmony)=>Configure((target,prefix)=>harmony.Patch(target,prefix:prefix));
        internal static void Configure(Action<MethodInfo,HarmonyMethod> patch)
        {
            var type=AccessTools.TypeByName("KSPCommunityFixes.Performance.DisableMapUpdateInFlight");
            if(type==null)return;
            if(type.Assembly.ManifestModule.ModuleVersionId!=new Guid("62bb7f54-2099-4f40-9a34-377c091e8538"))
                throw new InvalidOperationException("Review changed KSPCommunityFixes map update prefix");
            var target=AccessTools.DeclaredMethod(type,"OrbitRendererBase_LateUpdate_Prefix",Type.EmptyTypes);
            if(target==null||!target.IsStatic||target.ReturnType!=typeof(bool))
                throw new InvalidOperationException("Native map update prefix signature changed");
            var code=PatchProcessor.GetOriginalInstructions(target).ToArray();
            if(code.Count(c=>c.Calls(AccessTools.PropertyGetter(typeof(MapView),"fetch")))!=1||
                code.Count(c=>(c.operand as FieldInfo)?.Name=="updateMap")!=1)
                throw new InvalidOperationException("Unreviewed native map update prefix shape");
            var render=AccessTools.DeclaredMethod(typeof(OrbitRendererBase),"LateUpdate",Type.EmptyTypes);
            if(render==null||render.ReturnType!=typeof(void))throw new InvalidOperationException("Native orbit render signature changed");
            // Guard the render entry itself. Patching another Harmony prefix
            // after it was installed does not replace its compiled call site.
            patch(render,new HarmonyMethod(typeof(MapUpdateCompatibility),nameof(MapAvailable)){priority=Priority.First});
        }

        private static bool MapAvailable()
        {
            if(!SimulationAuthority.InMultiplayer)return true;
            // Multiplayer keeps inert vessel renderers outside map scenes. The
            // native prefix dereferences a map singleton already destroyed on
            // Tracking Station exit. Preserve its normal map-update decision
            // when the singleton exists; do not draw in other game scenes.
            if((HighLogic.LoadedSceneIsFlight||HighLogic.LoadedScene==GameScenes.TRACKSTATION)&&MapView.fetch!=null)return true;
            return false;
        }
    }
}
