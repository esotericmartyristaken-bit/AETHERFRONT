using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=5)throw new ArgumentException("Expected verified RC60 plugins, connection donor, new engine, fresh output directory and KSP managed references.");
string baseline=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[3]);
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
if(Hash(Path.Combine(baseline,"LmpClient.dll"))!="d39414f07ea282285ef553cbded2017db1a05b898f4ddc6770051400012ebfad" ||
   Hash(Path.Combine(baseline,"ApfCore.dll"))!="0a4b6bd7c25ee4e18aba88ec8d41cb66b8c0034da3ba02d2d090ddb798bdc6c0")throw new InvalidDataException("Unreviewed RC60 baseline.");
if(Directory.Exists(output))throw new IOException("Preserve previous attempts; use fresh output.");
using var resolver=new DefaultAssemblyResolver();
foreach(string path in new[]{baseline,Path.GetDirectoryName(Path.GetFullPath(args[1]))!,Path.GetDirectoryName(Path.GetFullPath(args[2]))!,Path.GetFullPath(args[4]),Path.GetFullPath(Path.Combine(baseline,"..","..","000_Harmony"))})resolver.AddSearchDirectory(path);
using var client=AssemblyDefinition.ReadAssembly(Path.Combine(baseline,"LmpClient.dll"),new ReaderParameters{AssemblyResolver=resolver});
using var donor=AssemblyDefinition.ReadAssembly(args[1],new ReaderParameters{AssemblyResolver=resolver});
using var engine=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
var module=client.MainModule;
var source=donor.MainModule.GetType("LmpClient.Apf.ConnectionInstall")??throw new InvalidDataException("Connection donor missing.");
var target=module.GetType(source.FullName)??throw new InvalidDataException("Connection baseline missing.");
if(source.NestedTypes.Count!=0)throw new InvalidDataException("Review generated nested types before copying.");
var fields=new Dictionary<string,FieldDefinition>();var methods=new Dictionary<string,MethodDefinition>();
TypeReference ImportType(TypeReference type){
 if(type is GenericParameter)return type;
 if(type is GenericInstanceType generic){var copy=new GenericInstanceType(ImportType(generic.ElementType));foreach(var argument in generic.GenericArguments)copy.GenericArguments.Add(ImportType(argument));return copy;}
 if(type is ArrayType array)return new ArrayType(ImportType(array.ElementType),array.Rank);
 if(type is ByReferenceType byref)return new ByReferenceType(ImportType(byref.ElementType));
 if(type is RequiredModifierType required)return new RequiredModifierType(ImportType(required.ModifierType),ImportType(required.ElementType));
 if(type is OptionalModifierType optional)return new OptionalModifierType(ImportType(optional.ModifierType),ImportType(optional.ElementType));
 if(type.FullName==source.FullName)return target;
 if(type.Scope.Name is "LmpClient" or "LmpClient.dll")return module.GetType(type.FullName)??throw new InvalidDataException("Missing client type: "+type.FullName);
 return module.ImportReference(type);
}
MethodReference ImportMethod(MethodReference method){
 if(method is GenericInstanceMethod generic){var copy=new GenericInstanceMethod(ImportMethod(generic.ElementMethod));foreach(var argument in generic.GenericArguments)copy.GenericArguments.Add(ImportType(argument));return copy;}
 if(methods.TryGetValue(method.FullName,out var mapped))return mapped;
 var result=module.ImportReference(method);result.DeclaringType=ImportType(method.DeclaringType);
 if(method.ReturnType is not GenericParameter)result.ReturnType=ImportType(method.ReturnType);
 for(int i=0;i<method.Parameters.Count;i++)if(method.Parameters[i].ParameterType is not GenericParameter)result.Parameters[i].ParameterType=ImportType(method.Parameters[i].ParameterType);
 return result;
}
foreach(var field in source.Fields){
 var copy=target.Fields.FirstOrDefault(f=>f.Name==field.Name);
 if(copy==null){copy=new FieldDefinition(field.Name,field.Attributes,ImportType(field.FieldType));if(field.HasConstant)copy.Constant=field.Constant;target.Fields.Add(copy);}
 else if(copy.FieldType.FullName!=ImportType(field.FieldType).FullName)throw new InvalidDataException("Field type changed: "+field.FullName);
 fields[field.FullName]=copy;
}
foreach(var method in source.Methods){
 var copy=target.Methods.FirstOrDefault(m=>m.FullName==method.FullName);
 if(copy==null){copy=new MethodDefinition(method.Name,method.Attributes,ImportType(method.ReturnType)){ImplAttributes=method.ImplAttributes,CallingConvention=method.CallingConvention};foreach(var p in method.Parameters)copy.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,ImportType(p.ParameterType)));target.Methods.Add(copy);}
 methods[method.FullName]=copy;
}
foreach(var method in source.Methods){
 var copy=methods[method.FullName];copy.Body=new MethodBody(copy){InitLocals=method.Body.InitLocals,MaxStackSize=method.Body.MaxStackSize};
 foreach(var local in method.Body.Variables)copy.Body.Variables.Add(new VariableDefinition(ImportType(local.VariableType)));
 var instructions=method.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));
 foreach(var i in method.Body.Instructions){var next=instructions[i];next.OpCode=i.OpCode;next.Operand=i.Operand switch{
  null=>null,Instruction branch=>instructions[branch],Instruction[] branches=>branches.Select(b=>instructions[b]).ToArray(),
  VariableDefinition local=>copy.Body.Variables[local.Index],ParameterDefinition p=>copy.Parameters[p.Index],
  MethodReference call=>ImportMethod(call),FieldReference f when fields.ContainsKey(f.FullName)=>fields[f.FullName],
  FieldReference f=>new FieldReference(f.Name,ImportType(f.FieldType),ImportType(f.DeclaringType)),TypeReference t=>ImportType(t),_=>i.Operand};copy.Body.Instructions.Add(next);}
 foreach(var h in method.Body.ExceptionHandlers)copy.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType){CatchType=h.CatchType==null?null:ImportType(h.CatchType),TryStart=instructions[h.TryStart],TryEnd=h.TryEnd==null?null:instructions[h.TryEnd],HandlerStart=instructions[h.HandlerStart],HandlerEnd=h.HandlerEnd==null?null:instructions[h.HandlerEnd],FilterStart=h.FilterStart==null?null:instructions[h.FilterStart]});
}
Directory.CreateDirectory(output);client.MainModule.Mvid=Guid.NewGuid();client.Write(Path.Combine(output,"LmpClient.dll"));
using var core=AssemblyDefinition.ReadAssembly(Path.Combine(baseline,"ApfCore.dll"),new ReaderParameters{AssemblyResolver=resolver});
var apply=core.MainModule.GetType("ApfCore.ApfArchiveInstaller").Methods.Single(m=>m.Name=="ApplyClientUpdate");
var delta=engine.MainModule.GetType("KesasInstall.DeltaClientFiles");
var fullValidation=apply.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.Name=="ValidateClientUpdateArchive");
var stopped=apply.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.Name=="WaitForProcessExit");
var enumerations=apply.Body.Instructions.Where(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="ApfCore.ApfClientUpdateManifest" && m.Name=="get_Files").ToArray();
if(enumerations.Length<3 || enumerations.Any(i=>i.Offset<=fullValidation.Offset || i.Offset<=stopped.Offset))throw new InvalidDataException("Unreviewed archive/exit/selection order.");
foreach(var i in enumerations){i.OpCode=OpCodes.Call;i.Operand=core.MainModule.ImportReference(delta.Methods.Single(m=>m.Name=="Files"));}
var first=apply.Body.Instructions[0];var il=apply.Body.GetILProcessor();il.InsertBefore(first,Instruction.Create(OpCodes.Ldarg_0));il.InsertBefore(first,Instruction.Create(OpCodes.Call,core.MainModule.ImportReference(delta.Methods.Single(m=>m.Name=="Begin"))));
core.MainModule.Mvid=Guid.NewGuid();core.Write(Path.Combine(output,"ApfCore.dll"));
int clientPreserved=Review(Path.Combine(baseline,"LmpClient.dll"),Path.Combine(output,"LmpClient.dll"),m=>m.DeclaringType.FullName==source.FullName);
int corePreserved=Review(Path.Combine(baseline,"ApfCore.dll"),Path.Combine(output,"ApfCore.dll"),m=>m.FullName==apply.FullName);
File.WriteAllText(Path.Combine(output,"Audit.json"),JsonSerializer.Serialize(new{baseline="kesas-rc60",clientPreserved,corePreserved,replacementEnumerations=enumerations.Length,fullArchiveValidationPreserved=true,networkAndGameplayMethodsPreserved=true,productionWrites=false,clientSha256=Hash(Path.Combine(output,"LmpClient.dll")),coreSha256=Hash(Path.Combine(output,"ApfCore.dll"))},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {clientPreserved} unrelated client methods and {corePreserved} unrelated core methods preserved; {enumerations.Length} delta enumerations follow full archive validation and stopped-game check.");
static int Review(string before,string after,Func<MethodDefinition,bool> changed){
 using var old=AssemblyDefinition.ReadAssembly(before);using var next=AssemblyDefinition.ReadAssembly(after);int count=0;
 var methods=next.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
 foreach(var m in old.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods))if(!changed(m)){if(!methods.TryGetValue(m.FullName,out var n) || Signature(m)!=Signature(n))throw new InvalidDataException("Unrelated method changed: "+m.FullName);count++;}return count;
}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var child in type.NestedTypes)foreach(var nested in Flatten(child))yield return nested;}
static string Signature(MethodDefinition m){if(!m.HasBody)return "none";var indexes=m.Body.Instructions.Select((i,n)=>(i,n)).ToDictionary(x=>x.i,x=>x.n);string Operand(object? o)=>o switch{null=>"",Instruction i=>"branch:"+indexes[i],Instruction[] a=>"branches:"+string.Join(",",a.Select(i=>indexes[i])),_=>o.ToString()??""};return string.Join("\n",m.Body.Instructions.Select(i=>i.OpCode.Name+" "+Operand(i.Operand)));}
