using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Aetherfront.Presentation
{
    public static class EditorScrollCompatibility
    {
        public static readonly Guid SupportedWasd = new Guid("5395b68c-6bb7-4963-9ec5-c7d6ace1abd4");

        internal static bool Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("WasdEditorCamera.WasdEditorCameraBehaviour");
            if (type == null) return false; // A vanilla starter has no editor camera yet.
            if (type.Assembly.ManifestModule.ModuleVersionId != SupportedWasd)
                throw new InvalidOperationException("Changed WASD editor camera needs scroll compatibility review");
            var late = AccessTools.DeclaredMethod(type, "LateUpdate", Type.EmptyTypes);
            if (late == null) throw new MissingMethodException(type.FullName, "LateUpdate");
            harmony.Patch(late, transpiler: new HarmonyMethod(typeof(EditorScrollCompatibility), nameof(RewriteWheelReads)));
            return true;
        }

        // Replace only the four wheel reads in WASD's camera method. Do not
        // consume/reset Unity input: the parts palette must still get its wheel.
        public static IEnumerable<CodeInstruction> RewriteWheelReads(IEnumerable<CodeInstruction> source)
        {
            var codes = source.ToList();
            var original = AccessTools.Method(typeof(Input), nameof(Input.GetAxis), new[] { typeof(string) });
            var replacement = AccessTools.Method(typeof(EditorScrollCompatibility), nameof(CameraAxis));
            int rewritten = 0;
            for (int i = 1; i < codes.Count; i++)
                if (codes[i].Calls(original) && Equals(codes[i-1].operand, "Mouse ScrollWheel"))
                {
                    codes[i].operand = replacement;
                    rewritten++;
                }
            if (rewritten != 4) throw new InvalidOperationException("Expected four scoped WASD wheel reads, found " + rewritten);
            return codes;
        }

        public static float Filter(float wheel, bool editor, bool overUi, bool cameraLocked)
            => editor && (overUi || cameraLocked) ? 0f : wheel;

        public static float CameraAxis(string axis)
        {
            if (axis != "Mouse ScrollWheel") return Input.GetAxis(axis);
            bool editor = HighLogic.LoadedSceneIsEditor;
            bool overUi = editor && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            bool locked = editor && InputLockManager.IsLocked(ControlTypes.CAMERACONTROLS);
            return Filter(Input.GetAxis(axis), editor, overUi, locked);
        }
    }
}
