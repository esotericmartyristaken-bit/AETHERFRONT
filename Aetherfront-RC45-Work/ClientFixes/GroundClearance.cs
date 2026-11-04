using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    /// <summary>Compatibility for the single closest-point query in Vessel.getLowestPoint.</summary>
    public static class GroundClearance
    {
        private static bool installed;
        public static int FallbackCount;
        public static int RejectedCount;
        private const int MaximumFallbackReports = 8;

        public static void Install(Harmony harmony)
        {
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            if (installed) return;

            MethodInfo target = AccessTools.DeclaredMethod(typeof(Vessel), "getLowestPoint", Type.EmptyTypes);
            if (target == null || target.IsStatic || target.ReturnType != typeof(float))
                throw new MissingMethodException("Expected instance float Vessel.getLowestPoint()");

            // An exception must not leave the stock method's temporary rotation applied.
            // The finalizer preserves the exception: it does not permit unpack to continue.
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(GroundClearance), nameof(CaptureRotation)),
                transpiler: new HarmonyMethod(typeof(GroundClearance), nameof(ReplaceClosestPoint)),
                finalizer: new HarmonyMethod(typeof(GroundClearance), nameof(RestoreRotationOnFailure)));
            installed = true;
            Debug.Log("[AetherfrontGroundClearance] Installed single getLowestPoint query replacement.");
        }

        public static IEnumerable<CodeInstruction> ReplaceClosestPoint(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = typeof(Collider).GetMethod(nameof(Collider.ClosestPoint), new[] { typeof(Vector3) });
            MethodInfo replacement = typeof(GroundClearance).GetMethod(nameof(ClosestPoint),
                new[] { typeof(Collider), typeof(Vector3) });
            if (original == null || replacement == null)
                throw new MissingMethodException("Ground-clearance closest-point signatures changed");

            var codes = new List<CodeInstruction>(instructions);
            int count = 0;
            int match = -1;
            for (int index = 0; index < codes.Count; index++)
            {
                if (codes[index].opcode == OpCodes.Callvirt && Equals(codes[index].operand, original))
                {
                    count++;
                    match = index;
                }
            }
            if (count != 1)
                throw new InvalidOperationException("Expected exactly one Collider.ClosestPoint call in getLowestPoint; found " + count);

            // Mutate the existing instruction to preserve its labels and exception blocks.
            // Stack remains (Collider, Vector3) -> Vector3; no pose override is involved.
            codes[match].opcode = OpCodes.Call;
            codes[match].operand = replacement;
            return codes;
        }

        public static void CaptureRotation(Vessel __instance, out Quaternion __state)
        {
            __state = __instance.vesselTransform.rotation;
        }

        public static Exception RestoreRotationOnFailure(Exception __exception, Vessel __instance, Quaternion __state)
        {
            if (__exception != null && __instance != null && __instance.vesselTransform != null)
                __instance.SetRotation(__state);
            return __exception;
        }

        public static Vector3 ClosestPoint(Collider collider, Vector3 point)
        {
            if (collider == null) return Reject("Missing collider");
            if (!IsFinite(point)) return Reject("Non-finite ground-clearance probe");

            MeshCollider mesh = collider as MeshCollider;
            if (UsesBoundsFallback(mesh != null, mesh != null && mesh.convex))
            {
                if (!collider.enabled || !collider.gameObject.activeInHierarchy)
                    return Reject("Inactive non-convex mesh collider");

                Bounds bounds = collider.bounds;
                Vector3 support;
                if (!TryGetBoundsSupport(point, bounds.center, bounds.extents, out support))
                    return Reject("Invalid non-convex mesh world bounds");

                FallbackCount++;
                if (FallbackCount <= MaximumFallbackReports)
                    Debug.Log("[AetherfrontGroundClearance] Non-convex world-bounds support: collider=" + collider.name
                        + ", probe=" + point + ", center=" + bounds.center + ", extents=" + bounds.extents
                        + ", support=" + support);
                return support;
            }

            // Preserve Unity's exact supported-shape query, including points inside shapes.
            if (collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider || mesh != null)
                return collider.ClosestPoint(point);

            return Reject("Unsupported collider type " + collider.GetType().FullName);
        }

        // Pure policy seam: supported convex meshes must not enter the fallback.
        public static bool UsesBoundsFallback(bool isMeshCollider, bool isConvex)
        {
            return isMeshCollider && !isConvex;
        }

        /// <summary>
        /// Returns a conservative negative-world-Z support point in the current world AABB.
        /// getLowestPoint has already rotated the actual vessel and consumes only result.z;
        /// this matches its existing layer-26/27/30 bounds.min.z heuristic. It is not an
        /// arbitrary-pose Physics.ClosestPoint replacement and must not be used as one.
        /// No native Unity API, scene state, or collider object is needed by this test seam.
        /// </summary>
        public static bool TryGetBoundsSupport(Vector3 point, Vector3 center, Vector3 extents, out Vector3 support)
        {
            // A failed result is deliberately unusable, never an authoritative world origin.
            support = new Vector3(float.NaN, float.NaN, float.NaN);
            if (!IsFinite(point) || !IsFinite(center) || !IsFinite(extents)) return false;
            if (extents.x < 0f || extents.y < 0f || extents.z < 0f) return false;
            if (extents.x == 0f && extents.y == 0f && extents.z == 0f) return false;

            Vector3 minimum = new Vector3(center.x - extents.x, center.y - extents.y, center.z - extents.z);
            Vector3 maximum = new Vector3(center.x + extents.x, center.y + extents.y, center.z + extents.z);
            if (!IsFinite(minimum) || !IsFinite(maximum)) return false;
            if (minimum.x > maximum.x || minimum.y > maximum.y || minimum.z > maximum.z) return false;

            support = new Vector3(Clamp(point.x, minimum.x, maximum.x),
                Clamp(point.y, minimum.y, maximum.y), minimum.z);
            return IsFinite(support);
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            if (value < minimum) return minimum;
            return value > maximum ? maximum : value;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Vector3 Reject(string reason)
        {
            RejectedCount++;
            Debug.LogError("[AetherfrontGroundClearance] Rejected query: " + reason);
            throw new InvalidOperationException("Ground-clearance query rejected: " + reason);
        }
    }
}
