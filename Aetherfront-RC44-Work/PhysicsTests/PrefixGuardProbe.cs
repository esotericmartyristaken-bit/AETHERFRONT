using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static class PrefixGuardProbe
{
    internal delegate bool Callback(OrbitDriver driver, CelestialBody body, double offset, ref double updateUT);
    private sealed class Instruction { internal int Offset; internal OpCode Code; internal object Operand; }

    // Replays the actual shipping prefix IL, preserving its original gates and
    // calls. Only a helper/true-return gate is inserted before Track's arg loads.
    internal static Callback Insert(MethodInfo source, MethodInfo guard, Action<bool, string> need)
    {
        MethodBody body = source.GetMethodBody();
        need(body.ExceptionHandlingClauses.Count == 0 && body.LocalVariables.Count == 0, "Pinned shipping prefix needs no local/EH remapping");
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(c => c.Value);
        byte[] bytes = body.GetILAsByteArray(); var instructions = new List<Instruction>();
        for (int p = 0; p < bytes.Length;)
        {
            int offset = p; short value = bytes[p++];
            if (value == 0xfe) value = (short)(0xfe00 | bytes[p++]);
            OpCode code = codes[value]; object operand = null;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: { int delta = (sbyte)bytes[p++]; operand = p + delta; break; }
                case OperandType.InlineBrTarget: { int delta = BitConverter.ToInt32(bytes, p); p += 4; operand = p + delta; break; }
                case OperandType.InlineMethod: operand = source.Module.ResolveMethod(BitConverter.ToInt32(bytes, p)); p += 4; break;
                case OperandType.InlineField: operand = source.Module.ResolveField(BitConverter.ToInt32(bytes, p)); p += 4; break;
                default: throw new NotSupportedException("Unreviewed prefix IL operand " + code.OperandType);
            }
            instructions.Add(new Instruction { Offset = offset, Code = code, Operand = operand });
        }
        int calls = 0, at = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            MethodInfo called = instructions[i].Operand as MethodInfo;
            if (called != null && called.Name == "TrackRigidbody" && called.DeclaringType == source.DeclaringType) { calls++; at = i - 4; }
        }
        need(calls == 1 && at >= 0 && instructions[at].Code == OpCodes.Ldarg_0 &&
            instructions[at + 1].Code == OpCodes.Ldarg_1 && instructions[at + 2].Code == OpCodes.Ldarg_2 &&
            instructions[at + 3].Code == OpCodes.Ldarg_3, "Exact shipping Track call and four argument loads identified");
        var dynamic = new DynamicMethod("PrivateShippingPrefixWithProductionGuard", typeof(bool),
            new[] { typeof(OrbitDriver), typeof(CelestialBody), typeof(double), typeof(double).MakeByRefType() }, typeof(PrefixGuardProbe).Module, true);
        ILGenerator il = dynamic.GetILGenerator();
        var labels = instructions.ToDictionary(i => i.Offset, i => il.DefineLabel()); labels[bytes.Length] = il.DefineLabel();
        for (int i = 0; i < instructions.Count; i++)
        {
            Instruction instruction = instructions[i]; il.MarkLabel(labels[instruction.Offset]);
            if (i == at)
            {
                Label original = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, guard); il.Emit(OpCodes.Brfalse, original);
                il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret); il.MarkLabel(original);
            }
            OpCode code = instruction.Code; object operand = instruction.Operand;
            if (operand == null) il.Emit(code);
            else if (code.OperandType == OperandType.ShortInlineBrTarget || code.OperandType == OperandType.InlineBrTarget) il.Emit(code, labels[(int)operand]);
            else if (operand is MethodInfo) il.Emit(code, (MethodInfo)operand);
            else if (operand is FieldInfo) il.Emit(code, (FieldInfo)operand);
            else throw new NotSupportedException("Unreviewed prefix token " + operand);
        }
        il.MarkLabel(labels[bytes.Length]);
        return (Callback)dynamic.CreateDelegate(typeof(Callback));
    }
}
