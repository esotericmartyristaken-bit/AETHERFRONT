using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class EditorInputCompatibility
    {
        private const string LockName="WasdEditorCameraBehaviour";
        private static FieldInfo mouseWasDown,cameraEnabled,cursorLocker,movePart,movementBounds;
        private static MethodInfo unlockCursor;

        internal static void Install(Harmony harmony)
        {
            var type=AccessTools.TypeByName("WasdEditorCamera.WasdEditorCameraBehaviour");
            if(type==null)return;
            if(type.Assembly.ManifestModule.ModuleVersionId!=new Guid("5395b68c-6bb7-4963-9ec5-c7d6ace1abd4"))
                throw new InvalidOperationException("Review changed WASD editor camera before applying input compatibility");
            mouseWasDown=AccessTools.Field(type,"mouseWasDown");cameraEnabled=AccessTools.Field(type,"cameraEnabled");
            cursorLocker=AccessTools.Field(type,"cursorLocker");movePart=AccessTools.Field(type,"movePart");movementBounds=AccessTools.Field(type,"movementBounds");
            unlockCursor=cursorLocker==null?null:AccessTools.Method(cursorLocker.FieldType,"UnlockCursor",Type.EmptyTypes);
            var late=AccessTools.DeclaredMethod(type,"LateUpdate",Type.EmptyTypes);
            var mode=AccessTools.DeclaredMethod(type,"SwitchMode",new[]{typeof(bool),typeof(bool),typeof(bool)});
            var destroy=AccessTools.DeclaredMethod(type,"OnDestroy",Type.EmptyTypes);
            if(mouseWasDown?.FieldType!=typeof(bool)||cameraEnabled?.FieldType!=typeof(bool)||movePart?.FieldType!=typeof(bool)||
                movementBounds?.FieldType!=typeof(Bounds)||!movementBounds.IsStatic||unlockCursor==null||late==null||mode==null||destroy==null)
                throw new InvalidOperationException("WASD editor input cleanup signatures changed");
            harmony.Patch(late,prefix:new HarmonyMethod(typeof(EditorInputCompatibility),nameof(BeforeCamera)),
                finalizer:new HarmonyMethod(typeof(EditorInputCompatibility),nameof(AfterCamera)));
            harmony.Patch(mode,postfix:new HarmonyMethod(typeof(EditorInputCompatibility),nameof(ModeChanged)));
            harmony.Patch(destroy,prefix:new HarmonyMethod(typeof(EditorInputCompatibility),nameof(Release)));
        }

        private static void BeforeCamera(object __instance,out bool __state)=>__state=(bool)mouseWasDown.GetValue(__instance);
        private static Exception AfterCamera(object __instance,bool __state,Exception __exception)
        {
            // Native LateUpdate records RMB-up, then can return during a left-
            // button gizmo drag before reaching its normal release branch.
            if(__state && !(bool)mouseWasDown.GetValue(__instance))Release(__instance);
            return __exception;
        }
        private static void ModeChanged(object __instance)
        {
            if(!(bool)cameraEnabled.GetValue(__instance))Release(__instance);
        }
        private static void Release(object __instance)
        {
            // Other mods, modal dialogs and the stock editor keep their locks.
            // In the normal native release path this lock is already gone.
            if(!InputLockManager.lockStack.ContainsKey(LockName))return;
            InputLockManager.RemoveControlLock(LockName);
            var cursor=cursorLocker.GetValue(__instance);
            if(cursor!=null)unlockCursor.Invoke(cursor,null);
            if((bool)movePart.GetValue(__instance) && EditorBounds.Instance!=null)
                EditorBounds.Instance.constructionBounds=(Bounds)movementBounds.GetValue(null);
            movePart.SetValue(__instance,false);mouseWasDown.SetValue(__instance,false);
        }
    }
}
