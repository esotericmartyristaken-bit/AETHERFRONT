using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length<3||args[0] is not("clock" or "client"))throw new ArgumentException("clock BASELINE REPAIR OUTPUT | client BASELINE OUTPUT");
bool clock=args[0]=="clock";if(args.Length!=(clock?4:3))throw new ArgumentException("Wrong argument count");
string input=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[^1]);
string expected=clock?"98a5541f2701b7961039da3261f6c4bb74149d313f33dcafbeafb6950d824fa1":"6eb9ecd45e45fa908ca532eea1d632ddefae7a24547d9bd1d976390fcd86f801";
if(input==output||Hash(input)!=expected||File.Exists(output))throw new IOException("Unreviewed baseline or existing output");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
if(!clock){string? managed=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED");if(string.IsNullOrEmpty(managed)||!Directory.Exists(managed))throw new IOException("Set AETHERFRONT_KSP_MANAGED to licensed KSP managed references");resolver.AddSearchDirectory(managed);}
using var baseline=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{InMemory=true,AssemblyResolver=resolver});var module=baseline.MainModule;
var types=module.Types.SelectMany(All).ToArray();var methods=types.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
var images=methods.ToDictionary(p=>p.Key,p=>Image(p.Value));var fields=types.SelectMany(t=>t.Fields).Select(FieldImage).ToArray();
var resources=module.Resources.Select(ResourceImage).ToArray();var references=module.AssemblyReferences.Select(r=>r.FullName).ToArray();
string changedMethod;int changedIndex=-1;MethodReference? originalCall=null;
if(clock)
{
 if(baseline.Name.Name!="AetherfrontContinuityServer"||baseline.Name.HasPublicKey)throw new IOException("Wrong continuity assembly");
 using var repair=AssemblyDefinition.ReadAssembly(args[2]);
 var source=repair.MainModule.GetType("Aetherfront.Continuity.ClockStore");var target=module.GetType(source.FullName);
 var additions=source.Methods.Where(m=>m.Name.StartsWith("ReplaceClock",StringComparison.Ordinal)).ToArray();
 if(additions.Length!=5||target.Methods.Any(m=>m.Name.StartsWith("ReplaceClock",StringComparison.Ordinal)))throw new IOException("Unexpected repair contract");
 var map=new Dictionary<MethodDefinition,MethodDefinition>();
 foreach(var method in additions){var copy=new MethodDefinition(method.Name,method.Attributes,module.ImportReference(method.ReturnType)){ImplAttributes=method.ImplAttributes};foreach(var p in method.Parameters)copy.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,module.ImportReference(p.ParameterType)));target.Methods.Add(copy);map.Add(method,copy);}
 MethodReference Method(MethodReference method){if(method.DeclaringType.FullName==source.FullName)return map.Single(p=>p.Key.FullName==method.FullName).Value;return module.ImportReference(method);}
 foreach(var method in additions)
 {
  var copy=map[method];copy.Body=new MethodBody(copy){InitLocals=method.Body.InitLocals,MaxStackSize=method.Body.MaxStackSize};
  var locals=method.Body.Variables.ToDictionary(v=>v,v=>new VariableDefinition(module.ImportReference(v.VariableType)));foreach(var v in method.Body.Variables)copy.Body.Variables.Add(locals[v]);
  var instructions=method.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));foreach(var i in method.Body.Instructions)copy.Body.Instructions.Add(instructions[i]);
  foreach(var i in method.Body.Instructions){var n=instructions[i];n.OpCode=i.OpCode;n.Operand=i.Operand switch{Instruction b=>instructions[b],Instruction[] branches=>branches.Select(b=>instructions[b]).ToArray(),VariableDefinition v=>locals[v],ParameterDefinition p=>copy.Parameters[p.Index],MethodReference m=>Method(m),FieldReference f=>module.ImportReference(f),TypeReference t=>module.ImportReference(t),CallSite=>throw new IOException("Unexpected indirect repair call"),_=>i.Operand};}
  Instruction? At(Instruction? i)=>i==null?null:instructions[i];
  foreach(var h in method.Body.ExceptionHandlers)copy.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType){TryStart=At(h.TryStart),TryEnd=At(h.TryEnd),HandlerStart=At(h.HandlerStart),HandlerEnd=At(h.HandlerEnd),FilterStart=At(h.FilterStart),CatchType=h.CatchType==null?null:module.ImportReference(h.CatchType)});
 }
 var write=target.Methods.Single(m=>m.Name=="Write");var call=write.Body.Instructions.Single(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference r&&r.DeclaringType.FullName=="System.IO.File"&&r.Name=="Replace");
 originalCall=(MethodReference)call.Operand;if(originalCall.Parameters.Count!=3)throw new IOException("Unexpected Replace signature");changedIndex=write.Body.Instructions.IndexOf(call);
 call.Operand=map.Single(p=>p.Key.Name=="ReplaceClockCheckpoint").Value;changedMethod=write.FullName;
}
else
{
 if(baseline.Name.Name!="LmpClient"||baseline.Name.HasPublicKey)throw new IOException("Wrong client assembly");
 var getter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");var text=getter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);
 var constant=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");if(!Equals(text.Operand,"kesas-rc43")||!Equals(constant.Constant,"kesas-rc43"))throw new IOException("Unexpected client version");
 text.Operand="kesas-rc44";constant.Constant="kesas-rc44";changedMethod=getter.FullName;
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);baseline.Write(output);
using var verified=AssemblyDefinition.ReadAssembly(output);var checkTypes=verified.MainModule.Types.SelectMany(All).ToArray();var checkMethods=checkTypes.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
if(verified.Name.FullName!=baseline.Name.FullName||verified.Name.HasPublicKey||!checkTypes.Select(t=>t.FullName).SequenceEqual(types.Select(t=>t.FullName))||methods.Keys.Any(k=>!checkMethods.ContainsKey(k))||!verified.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources))throw new IOException("Identity/type/resource changes outside repair");
var changed=methods.Keys.Where(k=>Image(checkMethods[k])!=images[k]).ToArray();if(changed.Length!=1||changed[0]!=changedMethod)throw new IOException("Unrelated original methods changed: "+string.Join(";",changed));
var actualFields=checkTypes.SelectMany(t=>t.Fields).Select(FieldImage).ToArray();if(actualFields.Length!=fields.Length||actualFields.Where((v,n)=>v!=fields[n]).Count()!=(clock?0:1))throw new IOException("Unexpected field changes");
if(clock){var call=checkMethods[changedMethod].Body.Instructions[changedIndex];var replacement=call.Operand;call.Operand=originalCall;if(Image(checkMethods[changedMethod])!=images[changedMethod])throw new IOException("Original atomic writer structure changed");call.Operand=replacement;}
var addedReferences=verified.MainModule.AssemblyReferences.Select(r=>r.FullName).Except(references).ToArray();if(references.Except(verified.MainModule.AssemblyReferences.Select(r=>r.FullName)).Any()||addedReferences.Any(r=>r.Split(',')[0] is not("System.Threading.Thread" or "System.Runtime")))throw new IOException("Unexpected assembly dependency");
var addedMethods=checkMethods.Keys.Except(methods.Keys).ToArray();if(addedMethods.Length!=(clock?5:0))throw new IOException("Unexpected method additions");
var result=new{mode=args[0],version="kesas-rc44",inputSha256=Hash(input),outputSha256=Hash(output),identityPreserved=true,changed,originalMethods=methods.Count,allOtherMethodsUnchanged=true,originalAtomicWriterRecovered=clock,clockValidationAndSessionGuardsUnchanged=clock,existingScienceAndPacingHooksUnchanged=!clock,addedMethods,addedReferences};
File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(result));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string FieldImage(FieldDefinition f)=>f.FullName+":"+f.Attributes+":"+f.Constant;
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?Convert.ToHexString(SHA256.HashData(e.GetResourceData())):r.ToString());
static string Image(MethodDefinition m){if(!m.HasBody)return "none";var code=m.Body.Instructions;string Operand(object? o)=>o switch{null=>"",Instruction b=>"@"+code.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(code.IndexOf)),IFormattable n=>n.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return string.Join("|",code.Select(i=>i.OpCode+":"+Operand(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)+":"+code.IndexOf(h.FilterStart)+":"+h.CatchType));}
