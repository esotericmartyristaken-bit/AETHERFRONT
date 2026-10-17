using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length != 3) throw new ArgumentException("Usage: CorePatcher <input> <policy> <output>");
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != "CBD524DD590D96740B97772EA55A74504E9CB4920482718E3BF5F951CCD60BE8") throw new InvalidDataException("Unreviewed ApfCore input");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetFullPath("Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"));
resolver.AddSearchDirectory(Path.GetFullPath("KESAS-RC16-Work/CorePolicy/bin/Release/net472"));
resolver.AddSearchDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2"));
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var fixes = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
var source = fixes.MainModule.GetType("ApfCore.KesasInstallPolicy");
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
    if (type.Scope.Name == "ApfCore" || type.Scope.Name == "ApfCore.dll")
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


var scanner = module.GetType("ApfCore.ApfPackScanner").Methods.Single(m => m.Name == "Scan");
var ip = scanner.Body.GetILProcessor();
var first = scanner.Body.Instructions[0];
ip.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
ip.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
ip.InsertBefore(first, Instruction.Create(OpCodes.Call, cloned.Methods.Single(m => m.Name == "PrepareScan")));
ip.InsertBefore(first, Instruction.Create(OpCodes.Starg, scanner.Parameters[1]));
var installer = module.GetType("ApfCore.ApfArchiveInstaller");
foreach (var relaunch in installer.Methods.Where(m => m.Name == "Relaunch"))
{
    first = relaunch.Body.Instructions[0]; ip = relaunch.Body.GetILProcessor();
    ip.InsertBefore(first, Instruction.Create(OpCodes.Ldsfld, cloned.Fields.Single(f => f.Name == "SuppressRelaunch")));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    var normal = relaunch.Body.Instructions[1];
    var getter = relaunch.Parameters[0].ParameterType.Resolve().Methods.Single(m => m.Name == "get_GameRoot");
    ip.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Callvirt, getter));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_0));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Call, cloned.Methods.Single(m=>m.Name=="TryLaunchInstaller")));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
    ip.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    // Suppressed callers return immediately. Ordinary callers enter the launcher
    // probe rather than jumping over it when automatic relaunch is enabled.
    normal.Operand = relaunch.Body.Instructions[3];
}
var changed = new HashSet<string> { scanner.FullName };
foreach(var relaunch in installer.Methods.Where(m => m.Name == "Relaunch")) changed.Add(relaunch.FullName);
int limits = 0;
foreach(var type in module.Types)
foreach(var method in type.Methods.Where(m => m.HasBody))
{
    bool packLimits = (type.FullName == "ApfCore.ApfArchiveInstaller" && new[]{"ValidateArchive","CopyBounded"}.Contains(method.Name)) ||
        (type.FullName == "ApfCore.ApfZipSafety" && method.Name == "ValidateContainer") ||
        (type.FullName == "ApfCore.ApfManifestValidator" && method.Name == "Validate");
    if (!packLimits) continue;
    foreach(var instruction in method.Body.Instructions)
    {
        if(instruction.OpCode == OpCodes.Ldc_I4 && Equals(instruction.Operand, int.MinValue))
        { instruction.OpCode=OpCodes.Ldc_I8; instruction.Operand=34359738368L; limits++; changed.Add(method.FullName); }
        if(instruction.OpCode == OpCodes.Ldc_I8 && Equals(instruction.Operand,8589934592L))
        { instruction.Operand=68719476736L; limits++; changed.Add(method.FullName); }
        if(instruction.OpCode == OpCodes.Ldc_I4 && Equals(instruction.Operand,10000))
        { instruction.Operand=100000; limits++; changed.Add(method.FullName); }
        if(instruction.OpCode == OpCodes.Ldstr)
            instruction.Operand=((string)instruction.Operand).Replace("2 GiB","32 GiB").Replace("8 GiB","64 GiB");
    }
}
if(limits != 8) throw new InvalidDataException("Unexpected limit edits: "+limits);
foreach(var reference in module.AssemblyReferences.Where(r => r.Name == fixes.Name.Name || r.Name == assembly.Name.Name).ToArray()) module.AssemblyReferences.Remove(reference);
module.Mvid=Guid.NewGuid(); assembly.Write(args[2]);
using var check=AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters{AssemblyResolver=resolver});
using var pristine=AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters{AssemblyResolver=resolver});
int unchanged=0;
foreach(var type in pristine.MainModule.Types.SelectMany(Flatten))
foreach(var method in type.Methods)
{
    if(changed.Contains(method.FullName)) continue;
    var next=check.MainModule.Types.SelectMany(Flatten).Single(t=>t.FullName==type.FullName).Methods.Single(m=>m.FullName==method.FullName);
    if(BodySignature(method,null)!=BodySignature(next,null)) throw new InvalidDataException("Unrelated core method changed: "+method.FullName);
    unchanged++;
}
foreach(var method in check.MainModule.GetType(cloned.FullName).Methods.Where(m=>m.HasBody))
foreach(var instruction in method.Body.Instructions)
{
    if(instruction.Operand is MethodReference called && called.Resolve()==null) throw new InvalidDataException(called.FullName);
    if(instruction.Operand is FieldReference field && field.Resolve()==null) throw new InvalidDataException(field.FullName);
}
Console.WriteLine($"Patched {changed.Count} core methods; preserved {unchanged} unrelated methods; updated {limits} bounded limits.");
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


