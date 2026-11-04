using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using HarmonyLib;

internal static class GroundClearanceProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int warnings;
    private static Type candidateType;
    private static MethodInfo closestPoint, install;
    private static FieldInfo fallbackCount;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(c => c.Value);
    private sealed class Instruction { internal int Offset, Next; internal OpCode Code; internal object Operand; }
    private sealed class GroundCase { internal Vessel Vessel; internal MeshCollider Mesh; internal int Half; }
    internal static void Bind(Assembly grafted, Action<bool, string> need)
    {
        candidateType = grafted.GetType("Aetherfront.Continuity.Client.GroundClearance", true);
        closestPoint = candidateType.GetMethod("ClosestPoint", All, null, new[] { typeof(Collider), typeof(Vector3) }, null);
        install = candidateType.GetMethod("Install", All);
        fallbackCount = candidateType.GetField("FallbackCount", All);
        need(closestPoint != null && install != null && fallbackCount != null &&
            candidateType.Assembly == grafted, "Ground helper resolved from actual grafted continuity assembly");
    }
    private static void Log(string condition, string trace, LogType type)
    {
        if (condition.Contains("Physics.ClosestPoint can only be used")) warnings++;
    }
    internal static void Run(Action<bool, string> need, Action<string> say)
    {
        Application.logMessageReceived += Log;
        try
        {
            MethodInfo method = typeof(Vessel).GetMethod("getLowestPoint", All);
            need(method != null && method.ReturnType == typeof(float), "Exact stock getLowestPoint resolved");
            Func<Vessel, float> guarded = CloneWithGuard(method, need);
            var groundCases = new List<GroundCase>();
            for (int geometry = 0; geometry < 3; geometry++)
            {
                var origin = new[] { Vector3.zero, new Vector3(25, 37, -19), new Vector3(-300, 400, 600) }[geometry];
                var vesselObject = new GameObject("GroundProbe-Vessel-" + geometry);
                vesselObject.SetActive(false);
                vesselObject.transform.position = origin;
                Vessel vessel = vesselObject.AddComponent<Vessel>();
                vessel.vesselTransform = vesselObject.transform;
                vessel.loaded = false; // Alignment is already identity; no flight initialization is fabricated.
                vessel.packed = true;
                var hit = new RaycastHit(); hit.normal = Vector3.forward;
                typeof(Vessel).GetField("groundCollisionHit", All).SetValue(vessel, hit);
                var partObject = new GameObject("GroundProbe-Part-" + geometry);
                partObject.transform.position = origin;
                Part part = partObject.AddComponent<Part>();
                part.enabled = false;
                part.partTransform = partObject.transform;
                part.orgRot = Quaternion.identity;
                vessel.parts = new List<Part> { part };
                vessel.rootPart = part;
                MeshCollider mesh = partObject.AddComponent<MeshCollider>();
                mesh.sharedMesh = Cube(1 + geometry);
                mesh.convex = false;
                mesh.enabled = true;
                mesh.isTrigger = false;
                groundCases.Add(new GroundCase { Vessel = vessel, Mesh = mesh, Half = 1 + geometry });
                Physics.SyncTransforms();
                Vector3 probe = origin - Vector3.forward * 1000;
                int before = warnings;
                Vector3 rejectedResult = mesh.ClosestPoint(probe);
                need(warnings > before, "Native non-convex query emits exact reported warning");
                say("MEASURE geometry=" + geometry + " probe=" + probe.ToString("R") + " unsupportedResult=" + rejectedResult.ToString("R"));
                before = warnings;
                float stock = (float)method.Invoke(vessel, null);
                need(warnings > before, "Unmodified stock getLowestPoint reaches rejected native query");
                before = warnings;
                float candidate = guarded(vessel);
                need(warnings == before, "Guarded stock method avoids unsupported native query");
                need(Math.Abs(candidate - (1 + geometry)) < 0.01, "Guarded method retains finite geometry-derived clearance");
                say("MEASURE geometry=" + geometry + " stockLowest=" + stock.ToString("R") + " guardedLowest=" + candidate.ToString("R"));
                need(Math.Abs(stock - 1000) < 0.01, "Native rejected query reproduces stock 1000m sentinel contamination");
                say("PASS ground geometry=" + geometry + " actualStockBaselineToSingleCallGuard=true");
                mesh.enabled = false;
                foreach (Type type in new[] { typeof(BoxCollider), typeof(SphereCollider), typeof(CapsuleCollider), typeof(MeshCollider) })
                {
                    Collider collider = (Collider)partObject.AddComponent(type);
                    MeshCollider convex = collider as MeshCollider;
                    if (convex != null) { convex.sharedMesh = Cube(2); convex.convex = true; }
                    Physics.SyncTransforms();
                    before = warnings;
                    Vector3 expected = collider.ClosestPoint(probe);
                    Vector3 actual = (Vector3)closestPoint.Invoke(null, new object[] { collider, probe });
                    need(warnings == before && Vector3.Distance(expected, actual) < 1e-5f, "Supported " + type.Name + " retains native result");
                    collider.enabled = false;
                }
                // The stock bounds-only layers, exclusions and disabled/trigger
                // behavior are exercised through both complete engine methods.
                foreach (int layer in new[] { 26, 27, 30 })
                {
                    partObject.layer = layer; mesh.enabled = true; Physics.SyncTransforms();
                    before = warnings;
                    float originalBounds = (float)method.Invoke(vessel, null);
                    float guardedBounds = guarded(vessel);
                    // Stock temporarily enables disabled colliders on these
                    // layers, including the earlier convex-mesh fixture.
                    need(warnings == before && !float.IsInfinity(originalBounds) &&
                        Math.Abs(originalBounds - guardedBounds) < 1e-5, "Stock bounds-only layer " + layer + " unchanged");
                }
                partObject.layer = 21; mesh.enabled = true; Physics.SyncTransforms();
                before = warnings;
                need(float.IsNegativeInfinity((float)method.Invoke(vessel, null)) && float.IsNegativeInfinity(guarded(vessel)) && warnings == before,
                    "Excluded stock layer remains excluded without query");
                partObject.layer = 0; mesh.enabled = false;
                need(float.IsNegativeInfinity((float)method.Invoke(vessel, null)) && float.IsNegativeInfinity(guarded(vessel)),
                    "All-disabled colliders retain stock infinity result");
                BoxCollider trigger = partObject.AddComponent<BoxCollider>(); trigger.isTrigger = true;
                need(float.IsNegativeInfinity((float)method.Invoke(vessel, null)) && float.IsNegativeInfinity(guarded(vessel)),
                    "Trigger collider remains excluded");
                trigger.enabled = false;
                say("PASS ground geometry=" + geometry + " supportedTypes=4 boundsLayers=3 exclusionsDisabledTriggers=true");
                // Retain fixture objects until process quit; no asynchronous flight lifecycle.
            }
            // Exercise the actual RC45 Install/transpiler/prefix/finalizer chain,
            // not just the candidate's pure bounds seam or a copied fallback.
            install.Invoke(null, new object[] { new Harmony("Aetherfront.PhysicsTests.PrivateGroundClearance") });
            foreach (GroundCase fixture in groundCases)
            {
                fixture.Mesh.gameObject.layer = 0;
                foreach (Collider collider in fixture.Mesh.gameObject.GetComponents<Collider>()) collider.enabled = false;
                fixture.Mesh.enabled = true; Physics.SyncTransforms();
                int count = (int)fallbackCount.GetValue(null);
                int before = warnings;
                float repaired = (float)method.Invoke(fixture.Vessel, null);
                need(warnings == before && Math.Abs(repaired - fixture.Half) < 0.01 &&
                    (int)fallbackCount.GetValue(null) == count + 1,
                    "Actual RC45 Install and exact getLowestPoint invoke candidate helper");
                say("PASS actualRC45GraftedInstall geometryHalf=" + fixture.Half + " stockLowestAfterInstall=" + repaired.ToString("R") +
                    " helperAssembly=" + candidateType.Assembly.GetName().Name);
            }
        }
        finally { Application.logMessageReceived -= Log; }
    }
    private static Mesh Cube(float half)
    {
        var mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-half,-half,-half),new Vector3(half,-half,-half),
            new Vector3(half,half,-half),new Vector3(-half,half,-half),new Vector3(-half,-half,half),
            new Vector3(half,-half,half),new Vector3(half,half,half),new Vector3(-half,half,half) };
        mesh.triangles = new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,2,3,7,2,7,6,1,2,6,1,6,5,3,0,4,3,4,7 };
        mesh.RecalculateBounds(); return mesh;
    }

    // Copies the actual method IL at runtime. Only the one Collider.ClosestPoint
    // operand/opcode changes. KSP rotations, exclusions, wheel and bounds paths,
    // minimum reduction, cleanup and return remain the engine's own instructions.
    private static Func<Vessel, float> CloneWithGuard(MethodInfo method, Action<bool, string> need)
    {
        MethodBody body = method.GetMethodBody();
        need(body.ExceptionHandlingClauses.Count == 0, "Stock lowest-point body has no exception regions to remap");
        byte[] bytes = body.GetILAsByteArray();
        var instructions = new List<Instruction>();
        for (int p = 0; p < bytes.Length;)
        {
            int offset = p;
            short code = bytes[p++];
            if (code == 0xfe) code = (short)(0xfe00 | bytes[p++]);
            OpCode op = Codes[code]; object operand = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand = (sbyte)bytes[p++]; break;
                case OperandType.ShortInlineVar: operand = bytes[p++]; break;
                case OperandType.InlineVar: operand = BitConverter.ToInt16(bytes, p); p += 2; break;
                case OperandType.InlineI: operand = BitConverter.ToInt32(bytes, p); p += 4; break;
                case OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, p); p += 8; break;
                case OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, p); p += 4; break;
                case OperandType.InlineR: operand = BitConverter.ToDouble(bytes, p); p += 8; break;
                case OperandType.ShortInlineBrTarget: { int delta = (sbyte)bytes[p++]; operand = p + delta; break; }
                case OperandType.InlineBrTarget: { int delta = BitConverter.ToInt32(bytes, p); p += 4; operand = p + delta; break; }
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(bytes, p); p += 4;
                    int end = p + 4 * count; int[] targets = new int[count];
                    for (int i = 0; i < count; i++) { targets[i] = end + BitConverter.ToInt32(bytes, p); p += 4; }
                    operand = targets; break;
                case OperandType.InlineString: operand = method.Module.ResolveString(BitConverter.ToInt32(bytes, p)); p += 4; break;
                case OperandType.InlineField: operand = method.Module.ResolveField(BitConverter.ToInt32(bytes, p)); p += 4; break;
                case OperandType.InlineMethod: operand = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, p)); p += 4; break;
                case OperandType.InlineType: operand = method.Module.ResolveType(BitConverter.ToInt32(bytes, p)); p += 4; break;
                case OperandType.InlineTok: operand = method.Module.ResolveMember(BitConverter.ToInt32(bytes, p)); p += 4; break;
                default: throw new NotSupportedException("Unreviewed stock IL operand: " + op.OperandType);
            }
            instructions.Add(new Instruction { Offset = offset, Next = p, Code = op, Operand = operand });
        }
        var dynamic = new DynamicMethod("TestOnlyGuardedStockLowestPoint", typeof(float), new[] { typeof(Vessel) }, typeof(GroundClearanceProbe).Module, true);
        dynamic.InitLocals = body.InitLocals;
        ILGenerator il = dynamic.GetILGenerator();
        foreach (LocalVariableInfo local in body.LocalVariables) il.DeclareLocal(local.LocalType, local.IsPinned);
        var labels = instructions.ToDictionary(i => i.Offset, i => il.DefineLabel()); labels[bytes.Length] = il.DefineLabel();
        int replacements = 0;
        foreach (Instruction instruction in instructions)
        {
            il.MarkLabel(labels[instruction.Offset]); OpCode op = instruction.Code; object operand = instruction.Operand;
            MethodInfo called = operand as MethodInfo;
            if (called != null && called.DeclaringType == typeof(Collider) && called.Name == "ClosestPoint")
            { il.Emit(OpCodes.Call, closestPoint); replacements++; continue; }
            if (operand == null) il.Emit(op);
            else if (op.OperandType == OperandType.ShortInlineBrTarget || op.OperandType == OperandType.InlineBrTarget) il.Emit(op, labels[(int)operand]);
            else if (op.OperandType == OperandType.InlineSwitch) il.Emit(op, ((int[])operand).Select(t => labels[t]).ToArray());
            else if (operand is MethodInfo) il.Emit(op, (MethodInfo)operand);
            else if (operand is ConstructorInfo) il.Emit(op, (ConstructorInfo)operand);
            else if (operand is FieldInfo) il.Emit(op, (FieldInfo)operand);
            else if (operand is Type) il.Emit(op, (Type)operand);
            else if (operand is string) il.Emit(op, (string)operand);
            else if (operand is sbyte) il.Emit(op, (sbyte)operand);
            else if (operand is byte) il.Emit(op, (byte)operand);
            else if (operand is short) il.Emit(op, (short)operand);
            else if (operand is int) il.Emit(op, (int)operand);
            else if (operand is long) il.Emit(op, (long)operand);
            else if (operand is float) il.Emit(op, (float)operand);
            else if (operand is double) il.Emit(op, (double)operand);
            else throw new NotSupportedException("Unreviewed IL token " + operand);
        }
        il.MarkLabel(labels[bytes.Length]);
        need(replacements == 1, "Exactly one native closest-point call replaced; no other stock algorithm changes");
        return (Func<Vessel, float>)dynamic.CreateDelegate(typeof(Func<Vessel, float>));
    }
}
