using System.Text.Json;
using Mono.Cecil;

string game=Path.GetFullPath(args[0]), output=Path.GetFullPath(args[1]);
var files=Directory.GetFiles(Path.Combine(game,"GameData"),"*.dll",SearchOption.AllDirectories)
 .Append(Path.Combine(game,"KSP_x64_Data/Managed/Assembly-CSharp.dll"));
var rows=new List<object>();
foreach(string file in files)
{
 try
 {
  using var assembly=AssemblyDefinition.ReadAssembly(file);
  foreach(var method in assembly.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).Where(m=>m.HasBody))
  {
   var code=method.Body.Instructions;
   for(int i=0;i<code.Count;i++)
    if(code[i].Operand is MethodReference called &&
       ((called.DeclaringType.FullName=="TimeWarp" && new[]{"SetRate","setRate","CancelAutoWarp","WarpTo"}.Contains(called.Name)) ||
        (called.DeclaringType.FullName=="Planetarium" && called.Name=="SetUniversalTime")))
     rows.Add(new {assembly=assembly.Name.Name,caller=method.FullName,call=called.FullName,
       file=Path.GetRelativePath(game,file).Replace('\\','/'),
       context=string.Join(" | ",code.Skip(Math.Max(0,i-8)).Take(Math.Min(9,i+1)).Select(c=>c.ToString()))});
  }
 }
 catch(BadImageFormatException){}
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Recorded "+rows.Count+" warp-rate/UT-changing call sites; no assemblies executed.");
static IEnumerable<TypeDefinition> All(TypeDefinition type){yield return type;foreach(var child in type.NestedTypes.SelectMany(All))yield return child;}
