using System;
using System.Linq;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    internal static class ScattererSettingsCompatibility
    {
        internal static void Install(Harmony harmony)
        {
            var type=AccessTools.TypeByName("Scatterer.MainSettingsReadWrite");
            if(type==null)return;
            var save=AccessTools.DeclaredMethod(type,"saveMainSettingsIfChanged",Type.EmptyTypes);
            var configs=AccessTools.Method(typeof(GameDatabase),"GetConfigs",new[]{typeof(string)});
            if(type.Assembly.ManifestModule.ModuleVersionId!=new Guid("6c952942-cf2d-4994-afb0-f8c8e483d93a")||
                save==null||save.ReturnType!=typeof(void)||configs==null||
                PatchProcessor.GetOriginalInstructions(save).Count(c=>c.Calls(configs))!=1)
                throw new InvalidOperationException("Review changed Scatterer settings shutdown before applying compatibility");
            harmony.Patch(save,prefix:new HarmonyMethod(typeof(ScattererSettingsCompatibility),nameof(DatabaseReady)));
        }
        // Unity may destroy GameDatabase before Scatterer's OnDestroy. Its
        // native settings comparison requires that database. Keep normal scene
        // saves and the separate plugin-data save; skip only this unavailable
        // comparison, without catching failures from a live database.
        private static bool DatabaseReady()=>GameDatabase.Instance!=null;
    }
}
