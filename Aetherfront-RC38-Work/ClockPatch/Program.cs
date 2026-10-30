using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=3) throw new ArgumentException("Reviewed server, candidate continuity assembly, output required");
string input=Path.GetFullPath(args[0]), helper=Path.GetFullPath(args[1]), output=Path.GetFullPath(args[2]);
using var server=AssemblyDefinition.ReadAssembly(input);
using var hooks=AssemblyDefinition.ReadAssembly(helper);
var methods=server.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).ToArray();
var before=methods.ToDictionary(m=>m.FullName,Image);
var target=methods.Single(m=>m.DeclaringType.FullName=="Server.System.WarpSystemReceiver"&&m.Name=="TryCreateSubspace");
if(target.ReturnType.FullName!="System.String"||target.Parameters.Count!=2||target.Body.ExceptionHandlers.Count!=0||
 target.Parameters[0].ParameterType.FullName!="Server.Client.ClientStructure"||target.Parameters[1].ParameterType.FullName!="LmpCommon.Message.Data.Warp.WarpNewSubspaceMsgData"||
 target.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="PrepareNewSubspace"))
 throw new IOException("Unreviewed/already patched subspace creator");
var hook=hooks.MainModule.Types.Single(t=>t.FullName=="Aetherfront.Continuity.Server.PersonalClockServer").Methods.Single(m=>m.Name=="PrepareNewSubspace");
var edit=target.Body.GetILProcessor();
var first=target.Body.Instructions[0];
var pop=Instruction.Create(OpCodes.Pop);
foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),
 Instruction.Create(OpCodes.Call,server.MainModule.ImportReference(hook)),Instruction.Create(OpCodes.Dup),
 Instruction.Create(OpCodes.Brfalse,pop),Instruction.Create(OpCodes.Ret),pop}) edit.InsertBefore(first,instruction);
target.Body.MaxStackSize=Math.Max(target.Body.MaxStackSize,2);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
server.Write(output);
using var check=AssemblyDefinition.ReadAssembly(output);
var after=check.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).ToArray();
var changed=after.Where(m=>!before.TryGetValue(m.FullName,out var image)||image!=Image(m)).Select(m=>m.FullName).ToArray();
if(after.Length!=methods.Length||changed.Length!=1||changed[0]!=target.FullName) throw new IOException("Changes exceeded one reviewed warp-creation boundary");
File.WriteAllText(output+".clock-audit.json",JsonSerializer.Serialize(new{
 inputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))),
 outputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))),
 methodCount=after.Length,changed,allOtherMethodsUnchanged=true},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("One server boundary hook changed; all "+(after.Length-1)+" other method bodies unchanged.");

static IEnumerable<TypeDefinition> All(TypeDefinition type){yield return type;foreach(var child in type.NestedTypes.SelectMany(All))yield return child;}
static string Image(MethodDefinition method)
{
 if(!method.HasBody)return "no-body";
 var code=method.Body.Instructions;
 string Operand(object? value)=>value switch {
  null=>"",Instruction branch=>"@"+code.IndexOf(branch),Instruction[] branches=>string.Join(",",branches.Select(b=>code.IndexOf(b))),
  VariableDefinition local=>"local:"+local.Index,ParameterDefinition parameter=>"arg:"+parameter.Index,
  IFormattable number=>number.ToString(null,System.Globalization.CultureInfo.InvariantCulture),_=>value.ToString()??""};
 return string.Join("|",code.Select(i=>i.OpCode+":"+Operand(i.Operand)))+" locals:"+string.Join(",",method.Body.Variables.Select(v=>v.VariableType.FullName))+
  " handlers:"+string.Join("|",method.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)));
}
