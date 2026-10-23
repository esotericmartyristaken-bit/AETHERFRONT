using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

// Read-only metadata audit. No Unity scene or gameplay acceptance claim.
internal static class DeepFreezeTimerAudit
{
    internal static void Write(string game,string output)
    {
        var paths=Directory.GetFiles(Path.Combine(game,"GameData"),"*.dll",SearchOption.AllDirectories)
            .Where(p=>new[]{"DeepFreeze.dll","BackgroundResources.dll"}.Contains(Path.GetFileName(p),StringComparer.OrdinalIgnoreCase)).ToArray();
        if(paths.Length!=2)throw new InvalidDataException("Exactly one installed DeepFreeze and BackgroundResources image required");
        var utc=AccessTools.Method(typeof(Planetarium),nameof(Planetarium.GetUniversalTime),Type.EmptyTypes);
        var lines=new List<string>{"Read-only actual installed IL timing audit. Not a simulation or gameplay pass."};
        int methods=0,calls=0;
        foreach(var path in paths)
        {
            var assembly=Assembly.LoadFrom(path);
            lines.Add("ASSEMBLY "+assembly.GetName().Name+"; MVID="+assembly.ManifestModule.ModuleVersionId);
            foreach(var type in assembly.GetTypes().OrderBy(t=>t.FullName,StringComparer.Ordinal))
            {
                var targets=AccessTools.GetDeclaredMethods(type).Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
                    .Where(m=>!m.ContainsGenericParameters&&m.GetMethodBody()!=null);
                foreach(var method in targets)
                {
                    var code=PatchProcessor.GetOriginalInstructions(method).ToArray();
                    var reads=Enumerable.Range(0,code.Length).Where(i=>code[i].Calls(utc)).ToArray();
                    if(reads.Length==0)continue;
                    methods++;calls+=reads.Length;
                    lines.Add("METHOD "+type.FullName+"."+method.Name+"("+string.Join(",",method.GetParameters().Select(p=>p.ParameterType.FullName+" "+p.Name))+"); static="+method.IsStatic+"; utcCalls="+reads.Length);
                    foreach(var index in reads)
                    {
                        lines.Add(" CALL index="+index);
                        for(int i=Math.Max(0,index-2);i<=Math.Min(code.Length-1,index+9);i++)
                            lines.Add("  "+i+": "+code[i]);
                    }
                }
                var fields=type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)
                    .Where(f=>(f.FieldType==typeof(double)||f.FieldType==typeof(float))&&
                        new[]{"time","last","death","update"}.Any(word=>f.Name.IndexOf(word,StringComparison.OrdinalIgnoreCase)>=0)).ToArray();
                if(fields.Length>0)lines.Add("FIELDS "+type.FullName+": "+string.Join(";",fields.Select(f=>f.FieldType.Name+" "+f.Name)));
            }
        }
        lines.Add("TOTAL methods="+methods+"; utcCalls="+calls);
        if(methods<10||calls<15)throw new InvalidDataException("Unexpectedly incomplete native timer audit");
        File.WriteAllLines(output,lines);
        Console.WriteLine("AUDIT actual installed timer readers: "+methods+" methods, "+calls+" UTC calls. No gameplay claim.");
    }
}
