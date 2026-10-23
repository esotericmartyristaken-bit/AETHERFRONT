using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class MapCleanupCompatibility
    {
        private static FieldInfo inspector;
        internal static void Install(Harmony harmony)
        {
            var type=AccessTools.TypeByName("SpaceDust.MapOverlay");
            if(type==null)return;
            if(type.Assembly.ManifestModule.ModuleVersionId!=new Guid("4258eaad-b1df-4e0b-910c-d13847f31a48"))
                throw new InvalidOperationException("Review changed SpaceDust before map cleanup compatibility");
            var method=AccessTools.DeclaredMethod(type,"DestroyInspectorPanel",Type.EmptyTypes);
            inspector=AccessTools.Field(type,"inspector");
            if(method==null||method.ReturnType!=typeof(void)||inspector==null||!typeof(Component).IsAssignableFrom(inspector.FieldType))
                throw new InvalidOperationException("SpaceDust map cleanup signatures changed");
            var codes=PatchProcessor.GetOriginalInstructions(method).ToArray();
            if(codes.Count(c=>Equals(c.operand,inspector))!=2||
                codes.Count(c=>(c.operand as MethodInfo)?.Name=="get_gameObject")!=1||
                codes.Count(c=>(c.operand as MethodInfo)?.Name=="Destroy")!=1)
                throw new InvalidOperationException("Unreviewed SpaceDust inspector cleanup shape");
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(MapCleanupCompatibility),nameof(InspectorExists)));
        }
        private static bool InspectorExists(object __instance)
        {
            var panel=inspector.GetValue(__instance) as Component;
            if(panel!=null)return true;
            // A flight scene can exit without ever opening the map. A second
            // exit may also see an already destroyed panel. Leave all other
            // OnMapExited cleanup and event unsubscription in their native paths.
            inspector.SetValue(__instance,null);
            return false;
        }
    }
}
