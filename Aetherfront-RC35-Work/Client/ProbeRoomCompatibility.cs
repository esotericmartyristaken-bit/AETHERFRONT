using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    internal static class ProbeRoomCompatibility
    {
        internal static void Install(Harmony harmony)
        {
            var type=AccessTools.TypeByName("ProbeControlRoom.ProbeControlRoom");
            if(type==null)return;
            if(type.Assembly.ManifestModule.ModuleVersionId!=new Guid("0e7106b9-f3db-4e98-98f3-c2de0e7b3db1"))
                throw new InvalidOperationException("Review changed Probe Control Room before IVA cleanup compatibility");
            var stop=AccessTools.DeclaredMethod(type,"stopIVA",Type.EmptyTypes);
            if(stop==null)throw new InvalidOperationException("Probe room cleanup signature changed");
            harmony.Patch(stop,transpiler:new HarmonyMethod(typeof(ProbeRoomCompatibility),nameof(SafeCameraRoom)));
        }
        private static bool CameraInsideRoom(Part room)
        {
            if(room==null || room.internalModel==null)return false;
            var camera=InternalCamera.Instance;
            return camera!=null && camera.transform.IsChildOf(room.internalModel.transform);
        }
        private static IEnumerable<CodeInstruction> SafeCameraRoom(IEnumerable<CodeInstruction> source)
        {
            var codes=source.ToList();var inside=AccessTools.Method(typeof(Transform),nameof(Transform.IsChildOf),new[]{typeof(Transform)});
            var indices=codes.Select((code,index)=>new{code,index}).Where(x=>x.code.Calls(inside)).Select(x=>x.index).ToArray();
            if(indices.Length!=1)throw new InvalidOperationException("Unreviewed probe room camera ancestry checks");
            int end=indices[0],start=end-6;
            if(start<0 || !codes[start].Calls(AccessTools.PropertyGetter(typeof(InternalCamera),nameof(InternalCamera.Instance))) ||
                !codes[start+1].Calls(AccessTools.PropertyGetter(typeof(Component),nameof(Component.transform))) ||
                codes[start+2].opcode!=OpCodes.Ldarg_0 ||
                !(codes[start+3].operand is System.Reflection.MethodInfo getter) || getter.Name!="get_aPart" || getter.ReturnType!=typeof(Part) ||
                codes[start+4].opcode!=OpCodes.Ldfld || !Equals(codes[start+4].operand,AccessTools.Field(typeof(Part),nameof(Part.internalModel))) ||
                !codes[start+5].Calls(AccessTools.PropertyGetter(typeof(Component),nameof(Component.transform))) ||
                codes.Skip(start+1).Take(6).Any(c=>c.labels.Count!=0||c.blocks.Count!=0) || codes[start].blocks.Count!=0)
                throw new InvalidOperationException("Unreviewed probe room cleanup instruction shape");
            var first=new CodeInstruction(OpCodes.Ldarg_0);first.labels.AddRange(codes[start].labels);
            codes.RemoveRange(start,7);
            codes.InsertRange(start,new[]{first,new CodeInstruction(OpCodes.Call,getter),new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(ProbeRoomCompatibility),nameof(CameraInsideRoom)))});
            return codes;
        }
    }
}
