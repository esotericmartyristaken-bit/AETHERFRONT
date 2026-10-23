using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LmpClient;

namespace Aetherfront.Continuity.Client
{
    internal static class DisconnectLifecycle
    {
        private static bool pending;
        private sealed class Callbacks
        {
            internal void Loaded(GameScenes scene)=>SceneLoaded(scene);
        }
        private static readonly Callbacks Listener=new Callbacks();
        internal static void Install(Harmony harmony)
        {
            var stop=AccessTools.DeclaredMethod(typeof(MainSystem),"StopGame",Type.EmptyTypes);
            if(stop==null)throw new InvalidOperationException("Multiplayer stop signature changed");
            harmony.Patch(stop,transpiler:new HarmonyMethod(typeof(DisconnectLifecycle),nameof(DeferredCleanup)));
            GameEvents.onLevelWasLoadedGUIReady.Add(Listener.Loaded);
        }
        // Keep flight objects available throughout ordinary scene-exit callbacks.
        // Their native teardown finishes before KSP's next-scene GUI-ready event.
        private static void RetainList(List<Vessel> vessels)
        {
            if(HighLogic.LoadedScene==GameScenes.MAINMENU)vessels.Clear();else pending=true;
        }
        private static void RetainActive(FlightGlobals globals,Vessel value)
        {
            if(HighLogic.LoadedScene==GameScenes.MAINMENU)globals.activeVessel=value;else pending=true;
        }
        private static void SceneLoaded(GameScenes scene)
        {
            if(!pending||scene!=GameScenes.MAINMENU)return;
            FlightGlobals.Vessels?.Clear();FlightGlobals.VesselsLoaded?.Clear();FlightGlobals.VesselsUnloaded?.Clear();
            if(FlightGlobals.fetch!=null)FlightGlobals.fetch.activeVessel=null;
            pending=false;
        }
        private static IEnumerable<CodeInstruction> DeferredCleanup(IEnumerable<CodeInstruction> source)
        {
            var code=source.ToList();int lists=0,active=0;
            var clear=AccessTools.Method(typeof(List<Vessel>),nameof(List<Vessel>.Clear),Type.EmptyTypes);
            var activeField=AccessTools.Field(typeof(FlightGlobals),nameof(FlightGlobals.activeVessel));
            foreach(var instruction in code)
            {
                if(instruction.Calls(clear))
                {
                    instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(DisconnectLifecycle),nameof(RetainList));lists++;
                }
                if(instruction.opcode==OpCodes.Stfld&&Equals(instruction.operand,activeField))
                {
                    instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(DisconnectLifecycle),nameof(RetainActive));active++;
                }
            }
            if(lists!=3||active!=1)throw new InvalidOperationException("Unreviewed multiplayer scene cleanup shape: "+lists+"/"+active);
            return code;
        }
    }
}
