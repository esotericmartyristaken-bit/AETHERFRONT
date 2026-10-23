using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using LmpClient;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class PackCompatibility
    {
        private static readonly HashSet<int> SkippedMenus=new HashSet<int>();
        private static FieldInfo fileHandler,messageHandler;
        private static FieldInfo organizerPartList,organizerSorter;
        private static MethodInfo compatAwake;
        internal static void Install(Harmony harmony)
        {
            DisconnectLifecycle.Install(harmony);
            ProbeRoomCompatibility.Install(harmony);
            MapCleanupCompatibility.Install(harmony);
            MapUpdateCompatibility.Install(harmony);
            TrackingLabelCompatibility.Install(harmony);
            ScattererSettingsCompatibility.Install(harmony);
            var menu=AccessTools.TypeByName("TarsierSpaceTech.TSTMenu");
            if(menu==null)throw new InvalidOperationException("Required Tarsier menu missing");
            var awake=AccessTools.DeclaredMethod(menu,"Awake",Type.EmptyTypes);
            var destroy=AccessTools.DeclaredMethod(menu,"OnDestroy",Type.EmptyTypes);
            if(awake==null||destroy==null)throw new InvalidOperationException("Tarsier lifecycle signatures changed");
            harmony.Patch(awake,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(MenuAwake)));
            harmony.Patch(destroy,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(MenuDestroy)));
            var compat=AccessTools.TypeByName("LunaCompat.LunaCompat");
            fileHandler=AccessTools.Field(compat,"_fileInteractionHandler");
            messageHandler=AccessTools.Field(compat,"_messageHandler");
            compatAwake=AccessTools.DeclaredMethod(compat,"Awake",Type.EmptyTypes);
            var fixedUpdate=AccessTools.DeclaredMethod(compat,"FixedUpdate",Type.EmptyTypes);
            var compatDestroy=AccessTools.DeclaredMethod(compat,"OnDestroy",Type.EmptyTypes);
            if(fileHandler==null||messageHandler==null||compatAwake==null||fixedUpdate==null||compatDestroy==null)
                throw new InvalidOperationException("LunaCompat lifecycle signatures changed");
            harmony.Patch(compatAwake,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(CompatAwake)));
            harmony.Patch(fixedUpdate,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(CompatTick)));
            harmony.Patch(compatDestroy,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(CompatDestroy)));
            var stats=AccessTools.TypeByName("KerbalStats.Experience.ExperienceTrackerEvents");
            var scan=AccessTools.DeclaredMethod(stats,"ScanVesselCrew",new[]{typeof(Vessel)});
            if(scan==null)throw new InvalidOperationException("KerbalStats crew scan signature changed");
            harmony.Patch(scan,transpiler:new HarmonyMethod(typeof(PackCompatibility),nameof(SafeSeatIndices)));
            var sorting=AccessTools.TypeByName("VABOrganizer.AdvancedSorting");
            organizerPartList=AccessTools.Field(sorting,"uiPartList");organizerSorter=AccessTools.Field(sorting,"uiSorterBase");
            var refresh=AccessTools.DeclaredMethod(sorting,"Refresh",Type.EmptyTypes);
            if(organizerPartList==null||organizerSorter==null||refresh==null)throw new InvalidOperationException("VABOrganizer sorting signatures changed");
            harmony.Patch(refresh,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(SortingReady)));
            var createSorting=AccessTools.DeclaredMethod(sorting,"CreateVABSortUI",Type.EmptyTypes);
            if(createSorting==null||createSorting.ReturnType!=typeof(void)||sorting.Assembly.ManifestModule.ModuleVersionId!=new Guid("61e5371b-9fe4-4562-9ae9-d1819834650f"))
                throw new InvalidOperationException("Review changed VABOrganizer sorter creation before applying compatibility");
            harmony.Patch(createSorting,prefix:new HarmonyMethod(typeof(PackCompatibility),nameof(EditorSortingOnly)));
            var computer=AccessTools.TypeByName("AvionicsSystems.MASFlightComputer");
            var computerStart=AccessTools.DeclaredMethod(computer,"Start",Type.EmptyTypes);
            if(computerStart==null)throw new InvalidOperationException("MAS startup signature changed");
            harmony.Patch(computerStart,transpiler:new HarmonyMethod(typeof(PackCompatibility),nameof(ControlPointLookup)));
            var sound=AccessTools.TypeByName("RocketSoundEnhancement.PartModules.RSE_Module");
            if(sound!=null)
            {
                var soundStart=AccessTools.DeclaredMethod(sound,"OnStart",new[]{typeof(PartModule.StartState)});
                if(soundStart==null)throw new InvalidOperationException("RocketSoundEnhancement startup signature changed");
                harmony.Patch(soundStart,transpiler:new HarmonyMethod(typeof(PackCompatibility),nameof(SoundPrefabLookup)));
            }
        }
        // Crew cabins/cryopods can have an IVA computer without ModuleCommand.
        // MAS already handles that case elsewhere, but dereferences it in Start.
        private static string ControlPointName(ModuleCommand command) => command==null?string.Empty:command.ActiveControlPointName;
        private static IEnumerable<CodeInstruction> ControlPointLookup(IEnumerable<CodeInstruction> instructions)
        {
            var getter=AccessTools.PropertyGetter(typeof(ModuleCommand),nameof(ModuleCommand.ActiveControlPointName));int changed=0;
            foreach(var code in instructions)
            {
                if(code.Calls(getter)){code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(PackCompatibility),nameof(ControlPointName));changed++;}
                yield return code;
            }
            if(changed!=1)throw new InvalidOperationException("Unreviewed MAS startup control-point reads: "+changed);
        }
        // Multiplayer can insert/reorder live EVA modules. RSE assumes that a
        // live module's numeric index also identifies its prefab, causing either
        // an out-of-range access or the wrong sound configuration. Match the
        // concrete module type and its occurrence instead. No save data changes.
        private static PartModule SoundPrefab(PartModuleList prefabs,int unused,PartModule live) =>
            SelectSoundPrefab(prefabs.Cast<PartModule>(),live.part.Modules.Cast<PartModule>(),live);
        private static PartModule SelectSoundPrefab(IEnumerable<PartModule> prefabs,IEnumerable<PartModule> active,PartModule live)
        {
            int occurrence=0;bool found=false;
            foreach(var module in active)
            {
                if(ReferenceEquals(module,live)){found=true;break;}
                if(!ReferenceEquals(module,null)&&module.GetType()==live.GetType())occurrence++;
            }
            if(found)
                foreach(var module in prefabs)
                    if(!ReferenceEquals(module,null)&&module.GetType()==live.GetType()&&occurrence--==0)return module;
            // A dynamically added module has no prefab. Its own OnLoad state
            // is safer than indexing an unrelated module or disabling startup.
            return live;
        }
        private static IEnumerable<CodeInstruction> SoundPrefabLookup(IEnumerable<CodeInstruction> instructions)
        {
            var lookup=AccessTools.Method(typeof(PartModuleList),"get_Item",new[]{typeof(int)});
            int changed=0;
            foreach(var code in instructions)
            {
                if(code.Calls(lookup))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(code);
                    code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(PackCompatibility),nameof(SoundPrefab));changed++;
                }
                yield return code;
            }
            if(changed!=1)throw new InvalidOperationException("Unreviewed RSE prefab lookups: "+changed);
        }
        // Upstream destroys its menu in unsupported scenes but then continues
        // Awake, accessing textures before GameDatabase loading has completed.
        private static bool MenuAwake(MonoBehaviour __instance)
        {
            if(HighLogic.LoadedScene==GameScenes.SPACECENTER||HighLogic.LoadedScene==GameScenes.FLIGHT)return true;
            SkippedMenus.Add(__instance.GetInstanceID());
            UnityEngine.Object.Destroy(__instance); return false;
        }
        private static bool MenuDestroy(MonoBehaviour __instance) => !SkippedMenus.Remove(__instance.GetInstanceID());
        private static bool CompatAwake() => MainSystem.Singleton!=null && MainSystem.Singleton.Enabled;
        private static bool CompatTick(object __instance)
        {
            if(fileHandler.GetValue(__instance)==null && CompatAwake())compatAwake.Invoke(__instance,null);
            return fileHandler.GetValue(__instance)!=null;
        }
        private static bool CompatDestroy(object __instance) => messageHandler.GetValue(__instance)!=null;
        // Rememberer can request a saved sort while editor categories/widgets are
        // still being rebuilt. The next native refresh handles the finished UI.
        private static bool SortingReady()
        {
            var list=organizerPartList.GetValue(null) as KSP.UI.Screens.EditorPartList;
            if(list==null || list.CategorizerFilters==null || list.CategorizerFilters.Count==0)return false;
            var sorter=organizerSorter.GetValue(null);
            if(sorter==null)return false;
            var states=AccessTools.Field(sorter.GetType(),"sortingButtonStates")?.GetValue(sorter) as System.Collections.ICollection;
            return states!=null && states.Count>5;
        }
        // Vendor hooks every stock sorter Start, including Tracking Station.
        // Skip editor customization there; its outer prefix still returns true
        // so the stock sorter initializes normally.
        private static bool EditorSortingOnly()=>HighLogic.LoadedSceneIsEditor;
        // Remote/unloaded crew can legitimately have seatIdx=-1 while no IVA is
        // instantiated. Upstream tests only index < Count, then indexes -1. Use
        // its existing unassigned-seat fallback without rewriting crew/save data.
        private static int SafeSeatIndex(ProtoCrewMember crew) => crew.seatIdx<0?int.MaxValue:crew.seatIdx;
        private static IEnumerable<CodeInstruction> SafeSeatIndices(IEnumerable<CodeInstruction> instructions)
        {
            var seat=AccessTools.Field(typeof(ProtoCrewMember),nameof(ProtoCrewMember.seatIdx));
            if(seat==null)throw new InvalidOperationException("KSP crew seat signature changed");
            int changed=0;
            foreach(var code in instructions)
            {
                if(code.opcode==OpCodes.Ldfld && Equals(code.operand,seat))
                {
                    code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(PackCompatibility),nameof(SafeSeatIndex));changed++;
                }
                yield return code;
            }
            if(changed!=2)throw new InvalidOperationException("Unreviewed KerbalStats seat-index reads: "+changed);
        }
    }
}
