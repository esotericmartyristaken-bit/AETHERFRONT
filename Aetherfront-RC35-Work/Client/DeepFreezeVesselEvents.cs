using System;
using System.Reflection;
using HarmonyLib;

namespace Aetherfront.Continuity.Client
{
    internal static class DeepFreezeVesselEvents
    {
        internal static void Install(Harmony harmony)
        {
            Configure((target,prefix)=>harmony.Patch(target,prefix:prefix));
        }

        internal static void Configure(Action<MethodInfo,HarmonyMethod> register)
        {
            var freezer=AccessTools.TypeByName("DF.DeepFreezer");
            var destroyed=AccessTools.DeclaredMethod(freezer,"onVesselDestroy",new[]{typeof(Vessel)});
            if(destroyed==null||destroyed.ReturnType!=typeof(void)||destroyed.IsStatic)
                throw new InvalidOperationException("DeepFreeze vessel destruction signature changed");
            register(destroyed,new HarmonyMethod(typeof(DeepFreezeVesselEvents),nameof(BeforeVesselDestroyed)));
        }

        private static bool BeforeVesselDestroyed(PartModule __instance,Vessel __0)
        {
            // Do not inspect detached Unity objects after multiplayer has stopped.
            if(!SimulationAuthority.InMultiplayer)return true;
            var part=__instance.part;
            return ShouldHandleDestroy(true,ReferenceEquals(part,null)?null:part.vessel,__0);
        }

        internal static bool ShouldHandleDestroy(bool multiplayer,object owner,object destroyed)
        {
            // DeepFreeze subscribes every freezer to the global destruction event.
            // Unrelated asteroid/replica cleanup must not cancel this operation.
            // Compare object identity, not GUID or Unity's destroyed-object equality:
            // unloading an old replica with the same GUID is not unloading this ship.
            // Retain native cleanup if its owner has already detached during teardown.
            // Do not require a control lock: it can be released before own-ship cleanup.
            return !multiplayer||ReferenceEquals(owner,null)||ReferenceEquals(owner,destroyed);
        }
    }
}
