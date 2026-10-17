using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
if(args.Length!=3)throw new ArgumentException("Usage: VersionPatcher <RC15 client> <first-install source> <output>");
if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])))!="5B26A14EC163537E95D846B80131D7A543A42EB7054B55E869F7E03C054B349A")throw new InvalidDataException("Unreviewed client input");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath("KESAS-RC16-Work"));
resolver.AddSearchDirectory(Path.GetFullPath("KESAS-RC15-Work/Inputs"));
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetFullPath("Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"));
resolver.AddSearchDirectory(Path.GetFullPath("KESAS-RC16-Work/FirstInstall/bin/Release/net472"));
resolver.AddSearchDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2"));
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var fixes = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
var source = fixes.MainModule.GetType("LmpClient.Apf.FirstInstall");
var cloned = new TypeDefinition(source.Namespace, source.Name, source.Attributes, module.ImportReference(source.BaseType));
module.Types.Add(cloned);
var fields = new Dictionary<FieldDefinition, FieldDefinition>();
var methods = new Dictionary<MethodDefinition, MethodDefinition>();

TypeReference ImportType(TypeReference type)
{
    if (type is GenericParameter) return type;
    if (type is GenericInstanceType generic)
    {
        var result = new GenericInstanceType(ImportType(generic.ElementType));
        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(ImportType(argument));
        return result;
    }
    if (type is ArrayType array) return new ArrayType(ImportType(array.ElementType), array.Rank);
    if (type is ByReferenceType reference) return new ByReferenceType(ImportType(reference.ElementType));
    if (type.FullName == source.FullName) return cloned;
    if (type.Scope.Name == "LmpClient" || type.Scope.Name == "LmpClient.dll")
        return module.GetType(type.FullName) ?? throw new InvalidDataException("Missing client type " + type.FullName);
    return module.ImportReference(type);
}

MethodReference ImportMethod(MethodReference method)
{
    if (method is GenericInstanceMethod generic)
    {
        var result = new GenericInstanceMethod(ImportMethod(generic.ElementMethod));
        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(ImportType(argument));
        return result;
    }
    if (method.DeclaringType.FullName == source.FullName)
        return methods[source.Methods.Single(candidate => candidate.FullName == method.FullName)];
    var imported = module.ImportReference(method);
    imported.DeclaringType = ImportType(method.DeclaringType);
    if (method.ReturnType is not GenericParameter) imported.ReturnType = ImportType(method.ReturnType);
    for (int i = 0; i < method.Parameters.Count; i++)
        if (method.Parameters[i].ParameterType is not GenericParameter)
            imported.Parameters[i].ParameterType = ImportType(method.Parameters[i].ParameterType);
    return imported;
}

foreach (var field in source.Fields)
{
    var copy = new FieldDefinition(field.Name, field.Attributes, ImportType(field.FieldType));
    cloned.Fields.Add(copy);
    fields[field] = copy;
}
foreach (var method in source.Methods)
{
    var copy = new MethodDefinition(method.Name, method.Attributes, ImportType(method.ReturnType))
        { ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention };
    foreach (var parameter in method.Parameters)
        copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, ImportType(parameter.ParameterType)));
    cloned.Methods.Add(copy);
    methods[method] = copy;
}
foreach (var method in source.Methods)
{
    if (!method.HasBody) continue;
    var copy = methods[method];
    copy.Body = new MethodBody(copy) { InitLocals = method.Body.InitLocals, MaxStackSize = method.Body.MaxStackSize };
    foreach (var variable in method.Body.Variables) copy.Body.Variables.Add(new VariableDefinition(ImportType(variable.VariableType)));
    var instructions = method.Body.Instructions.ToDictionary(item => item, item => Instruction.Create(OpCodes.Nop));
    foreach (var instruction in method.Body.Instructions)
    {
        var target = instructions[instruction];
        target.OpCode = instruction.OpCode;
        target.Operand = instruction.Operand switch
        {
            null => null,
            Instruction branch => instructions[branch],
            Instruction[] branches => branches.Select(branch => instructions[branch]).ToArray(),
            VariableDefinition variable => copy.Body.Variables[variable.Index],
            ParameterDefinition parameter => copy.Parameters[parameter.Index],
            MethodReference called => ImportMethod(called),
            FieldReference field when field.DeclaringType.FullName == source.FullName =>
                fields[source.Fields.Single(candidate => candidate.FullName == field.FullName)],
            FieldReference field => new FieldReference(field.Name, ImportType(field.FieldType), ImportType(field.DeclaringType)),
            TypeReference type => ImportType(type),
            _ => instruction.Operand
        };
        copy.Body.Instructions.Add(target);
    }
    foreach (var handler in method.Body.ExceptionHandlers)
        copy.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
        {
            CatchType = handler.CatchType == null ? null : ImportType(handler.CatchType),
            TryStart = instructions[handler.TryStart],
            TryEnd = handler.TryEnd == null ? null : instructions[handler.TryEnd],
            HandlerStart = instructions[handler.HandlerStart],
            HandlerEnd = handler.HandlerEnd == null ? null : instructions[handler.HandlerEnd],
            FilterStart = handler.FilterStart == null ? null : instructions[handler.FilterStart]
        });
}


var update=module.GetType("LmpClient.MainSystem").Methods.Single(m=>m.Name=="Update"&&m.HasBody);
update.Body.GetILProcessor().InsertBefore(update.Body.Instructions[0],Instruction.Create(OpCodes.Call,cloned.Methods.Single(m=>m.Name=="Tick")));
var version=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");
if(!Equals(version.Constant,"kesas-rc15"))throw new InvalidDataException("Wrong input version");
version.Constant="kesas-rc16";
int edits=0;
foreach(var t in module.Types.SelectMany(Flatten))foreach(var m in t.Methods.Where(m=>m.HasBody))foreach(var i in m.Body.Instructions)
 if(i.OpCode==OpCodes.Ldstr&&Equals(i.Operand,"kesas-rc15")){i.Operand="kesas-rc16";edits++;}
if(edits!=1)throw new InvalidDataException("Unexpected version reference count");
foreach(var reference in module.AssemblyReferences.Where(r=>r.Name==fixes.Name.Name||r.Name==assembly.Name.Name).ToArray())module.AssemblyReferences.Remove(reference);
module.Mvid=Guid.NewGuid();assembly.Write(args[2]);
using var check=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
using var pristine=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
int unchanged=0;
foreach(var t in pristine.MainModule.Types.SelectMany(Flatten))foreach(var m in t.Methods)
{
 if(m.FullName==update.FullName || (t.FullName=="LmpClient.Apf.ApfClientSettings"&&m.Name=="get_ClientVersion"))continue;
 var next=check.MainModule.Types.SelectMany(Flatten).Single(n=>n.FullName==t.FullName).Methods.Single(n=>n.FullName==m.FullName);
 if(BodySignature(m,null)!=BodySignature(next,null))throw new InvalidDataException("Unrelated client method changed: "+m.FullName);
 unchanged++;
}
foreach(var m in check.MainModule.GetType(cloned.FullName).Methods.Where(m=>m.HasBody))foreach(var i in m.Body.Instructions)
 if(i.Operand is MethodReference called && called.Resolve()==null)throw new InvalidDataException(called.FullName);
Console.WriteLine("RC16 first-install startup and version patched; "+unchanged+" unrelated client methods preserved.");
static string BodySignature(MethodDefinition method, Dictionary<string,string>? replacements)
{
    if (!method.HasBody) return "no-body";
    var indexes = method.Body.Instructions.Select((instruction, index) => (instruction, index))
        .ToDictionary(item => item.instruction, item => item.index);
    string Operand(object? operand) => operand switch
    {
        null => "",
        Instruction branch => "branch:" + indexes[branch],
        Instruction[] branches => "branches:" + string.Join(",", branches.Select(branch => indexes[branch])),
        string text when replacements != null && replacements.TryGetValue(text, out var replacement) => replacement,
        _ => operand.ToString() ?? ""
    };
    return string.Join("\n", method.Body.Instructions.Select(item => item.OpCode.Name + " " + Operand(item.Operand)));
}

static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes)
    foreach (var descendant in Flatten(nested)) yield return descendant;
}
