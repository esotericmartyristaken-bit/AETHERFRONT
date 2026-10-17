using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=4)throw new ArgumentException("Usage: Patcher <RC17 client> <RC17 core> <donor> <output directory>");
if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])))!="7885F7D1557A2A023A83817422C195111EB34723ECA74F5AB9A20858D75A21D3" ||
   Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1])))!="BD036B5E176CF465D30B8275E50BAFD37552814A6917FDC13EFB26C97AEAD99D")throw new InvalidDataException("Unreviewed RC17 input.");
var resolver=new DefaultAssemblyResolver();
foreach(string directory in new[]{"KESAS-RC17-Work/Patched","KESAS-RC15-Work/Inputs","Aetherfront-RC18-Work/Engine/bin/Release/net472","Aetherfront-RC18-Work/ClientUi/bin/Release/net472","Aetherfront-KSP-Smoke/KSP_x64_Data/Managed",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2")})resolver.AddSearchDirectory(Path.GetFullPath(directory));
using var client=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
using var donor=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
var module=client.MainModule;
var sourceFlow=donor.MainModule.GetType("LmpClient.Apf.ConnectionInstall");
if(sourceFlow.NestedTypes.Count!=0)throw new InvalidDataException("Review generated flow helper types before grafting.");
var sourceWindow=donor.MainModule.GetType("LmpClient.Windows.Connection.ConnectionWindow");
var flow=module.GetType(sourceFlow.FullName)??throw new InvalidDataException("RC17 connection flow missing.");
var window=module.GetType(sourceWindow.FullName);
var types=new Dictionary<string,TypeDefinition>{{sourceFlow.FullName,flow},{sourceWindow.FullName,window}};
var fields=new Dictionary<string,FieldDefinition>();
var methods=new Dictionary<string,MethodDefinition>();
TypeReference ImportType(TypeReference type)
{
    if(type is GenericParameter)return type;
    if(type is GenericInstanceType generic){var copy=new GenericInstanceType(ImportType(generic.ElementType));foreach(var argument in generic.GenericArguments)copy.GenericArguments.Add(ImportType(argument));return copy;}
    if(type is ArrayType array)return new ArrayType(ImportType(array.ElementType),array.Rank);
    if(type is ByReferenceType byref)return new ByReferenceType(ImportType(byref.ElementType));
    if(types.TryGetValue(type.FullName,out var mapped))return mapped;
    if(type.Scope.Name=="LmpClient"||type.Scope.Name=="LmpClient.dll")return module.GetType(type.FullName)??throw new InvalidDataException("Missing client type: "+type.FullName);
    return module.ImportReference(type);
}
MethodReference ImportMethod(MethodReference method)
{
    if(method is GenericInstanceMethod generic){var copy=new GenericInstanceMethod(ImportMethod(generic.ElementMethod));foreach(var argument in generic.GenericArguments)copy.GenericArguments.Add(ImportType(argument));return copy;}
    if(methods.TryGetValue(method.FullName,out var mapped))return mapped;
    var result=module.ImportReference(method);result.DeclaringType=ImportType(method.DeclaringType);
    if(method.ReturnType is not GenericParameter)result.ReturnType=ImportType(method.ReturnType);
    for(int i=0;i<method.Parameters.Count;i++)if(method.Parameters[i].ParameterType is not GenericParameter)result.Parameters[i].ParameterType=ImportType(method.Parameters[i].ParameterType);
    return result;
}
foreach(var source in new[]{sourceFlow,sourceWindow}) {
    var target=types[source.FullName];
    foreach(var field in source.Fields){var copy=target.Fields.FirstOrDefault(f=>f.Name==field.Name);if(copy==null){copy=new FieldDefinition(field.Name,field.Attributes,ImportType(field.FieldType));target.Fields.Add(copy);}fields[field.FullName]=copy;}
    foreach(var method in source.Methods){var copy=target.Methods.FirstOrDefault(m=>m.FullName==method.FullName);if(copy==null){copy=new MethodDefinition(method.Name,method.Attributes,ImportType(method.ReturnType)){ImplAttributes=method.ImplAttributes,CallingConvention=method.CallingConvention};foreach(var p in method.Parameters)copy.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,ImportType(p.ParameterType)));target.Methods.Add(copy);}methods[method.FullName]=copy;}
}
void CopyBody(MethodDefinition source,MethodDefinition target)
{
    if(!source.HasBody)return;
    target.Body=new MethodBody(target){InitLocals=source.Body.InitLocals,MaxStackSize=source.Body.MaxStackSize};
    foreach(var variable in source.Body.Variables)target.Body.Variables.Add(new VariableDefinition(ImportType(variable.VariableType)));
    var instructions=source.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));
    foreach(var instruction in source.Body.Instructions){var copy=instructions[instruction];copy.OpCode=instruction.OpCode;copy.Operand=instruction.Operand switch{
        null=>null,Instruction branch=>instructions[branch],Instruction[] branches=>branches.Select(b=>instructions[b]).ToArray(),VariableDefinition variable=>target.Body.Variables[variable.Index],ParameterDefinition parameter=>target.Parameters[parameter.Index],MethodReference called=>ImportMethod(called),FieldReference field when fields.ContainsKey(field.FullName)=>fields[field.FullName],FieldReference field=>new FieldReference(field.Name,ImportType(field.FieldType),ImportType(field.DeclaringType)),TypeReference type=>ImportType(type),_=>instruction.Operand};target.Body.Instructions.Add(copy);}
    foreach(var handler in source.Body.ExceptionHandlers)target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType){CatchType=handler.CatchType==null?null:ImportType(handler.CatchType),TryStart=instructions[handler.TryStart],TryEnd=handler.TryEnd==null?null:instructions[handler.TryEnd],HandlerStart=instructions[handler.HandlerStart],HandlerEnd=handler.HandlerEnd==null?null:instructions[handler.HandlerEnd],FilterStart=handler.FilterStart==null?null:instructions[handler.FilterStart]});
}
var changed=new HashSet<string>();
foreach(var method in sourceFlow.Methods){CopyBody(method,methods[method.FullName]);changed.Add(methods[method.FullName].FullName);}
foreach(var method in sourceWindow.Methods.Where(m=>new[]{"DrawWindowContent","DrawTopButtons","SetStyles"}.Contains(m.Name))){var target=methods[method.FullName];CopyBody(method,target);changed.Add(target.FullName);}
var version=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");if(!Equals(version.Constant,"kesas-rc17"))throw new InvalidDataException("Wrong input version.");version.Constant="kesas-rc18";
int versionEdits=0;
foreach(var type in module.Types.SelectMany(Flatten))foreach(var method in type.Methods.Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions)if(instruction.OpCode==OpCodes.Ldstr&&Equals(instruction.Operand,"kesas-rc17")){instruction.Operand="kesas-rc18";changed.Add(method.FullName);versionEdits++;}
if(versionEdits!=1)throw new InvalidDataException("Unexpected version reference count.");
int branded=0;
foreach(var type in module.Types.SelectMany(Flatten))foreach(var method in type.Methods.Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions)if(instruction.OpCode==OpCodes.Ldstr && instruction.Operand is string text) {
    string next=System.Text.RegularExpressions.Regex.Replace(text,"Luna Multiplayer","Aetherfront",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if(type.Namespace=="LmpClient.Localization.Structures")next=System.Text.RegularExpressions.Regex.Replace(next,@"(?i)(?<![A-Za-z/\\])Luna\s*Multi\s*player(?![/\\])|(?<![A-Za-z])LMP(?![A-Za-z/\\])","Aetherfront");
    if(type.FullName=="LmpClient.Windows.Apf.ApfClientUpdateWindow")next=next.Replace("KESAS","Aetherfront");
    if(next!=text){instruction.Operand=next;changed.Add(method.FullName);branded++;}
}
if(branded==0)throw new InvalidDataException("No visible branding strings found.");
foreach(var reference in module.AssemblyReferences.Where(r=>r.Name==donor.Name.Name||r.Name==client.Name.Name).ToArray())module.AssemblyReferences.Remove(reference);
Directory.CreateDirectory(args[3]);module.Mvid=Guid.NewGuid();string clientOutput=Path.Combine(args[3],"LmpClient.dll");client.Write(clientOutput);
using var verified=AssemblyDefinition.ReadAssembly(clientOutput,new ReaderParameters{AssemblyResolver=resolver});
using var pristine=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
int preserved=0;
foreach(var type in pristine.MainModule.Types.SelectMany(Flatten))foreach(var method in type.Methods){if(changed.Contains(method.FullName))continue;var next=verified.MainModule.Types.SelectMany(Flatten).Single(t=>t.FullName==type.FullName).Methods.Single(m=>m.FullName==method.FullName);if(Signature(method)!=Signature(next))throw new InvalidDataException("Unrelated client method changed: "+method.FullName);preserved++;}
foreach(var type in new[]{verified.MainModule.GetType(flow.FullName),verified.MainModule.GetType(window.FullName)})foreach(var method in type.Methods.Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions){if(instruction.Operand is MethodReference called&&called.Resolve()==null)throw new InvalidDataException("Unresolved: "+called.FullName);if(instruction.Operand is FieldReference field&&field.Resolve()==null)throw new InvalidDataException("Unresolved: "+field.FullName);}
Console.WriteLine($"PASS RC18 client: {changed.Count} targeted methods changed; {preserved} unrelated methods preserved; {branded} branding literals changed; injected references resolve.");
File.Copy(args[1],Path.Combine(args[3],"ApfCore.dll"),true);
Console.WriteLine("PASS core: preserved byte-for-byte from RC17.");
static string Signature(MethodDefinition method){if(!method.HasBody)return "none";var indexes=method.Body.Instructions.Select((i,n)=>(i,n)).ToDictionary(x=>x.i,x=>x.n);string Operand(object? value)=>value switch{null=>"",Instruction i=>"branch:"+indexes[i],Instruction[] a=>"branches:"+string.Join(",",a.Select(i=>indexes[i])),_=>value.ToString()??""};return string.Join("\n",method.Body.Instructions.Select(i=>i.OpCode.Name+" "+Operand(i.Operand)));}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes)foreach(var child in Flatten(nested))yield return child;}
