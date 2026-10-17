using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

if (args.Length != 4) throw new ArgumentException("Usage: Patcher <RC14 LmpClient.dll> <UI source.dll> <output.dll> <text.json>");
var replacements = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[3]))!;
const string expectedHash = "8D2E8B8AEA81E9703AB21D1159CE5CD464D92893F8E3C791B576FF1797580738";
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != expectedHash)
    throw new InvalidDataException("Input is not the reviewed RC14 client.");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetFullPath("Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"));
resolver.AddSearchDirectory(Path.GetFullPath("KESAS-RC15-Work/ClientUi/bin/Release/net472"));
resolver.AddSearchDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget/packages/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2"));
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var fixes = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
var source = fixes.MainModule.GetType("LmpClient.Windows.Aetherfront.ClientPanel");
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

var client = module.GetType("LmpClient.Aetherfront.AetherfrontClientSystem");
var update = client.Methods.Single(method => method.Name == "Update");
update.Body.GetILProcessor().InsertBefore(update.Body.Instructions[0],
    Instruction.Create(OpCodes.Call, cloned.Methods.Single(method => method.Name == "Tick")));
var window = module.GetType("LmpClient.Windows.Aetherfront.AetherfrontFactionWindow");
var home = window.Methods.Single(method => method.Name == "DrawHome");
home.Body.GetILProcessor().InsertBefore(home.Body.Instructions[0],
    Instruction.Create(OpCodes.Call, cloned.Methods.Single(method => method.Name == "DrawPlayers")));
var drawChat = window.Methods.Single(method => method.Name == "DrawUnifiedChat");
drawChat.Body = new MethodBody(drawChat);
drawChat.Body.Instructions.Add(Instruction.Create(OpCodes.Call, cloned.Methods.Single(method => method.Name == "DrawGlobalChat")));
drawChat.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
var visible = window.Methods.Single(method => method.Name == "get_ChatVisible");
var start = visible.Body.Instructions[0];
var processor = visible.Body.GetILProcessor();
processor.InsertBefore(start, Instruction.Create(OpCodes.Ldarg_0));
processor.InsertBefore(start, Instruction.Create(OpCodes.Ldc_I4_0));
processor.InsertBefore(start, Instruction.Create(OpCodes.Stfld, window.Fields.Single(field => field.Name == "_chatChannel")));
var chatSystem = module.GetType("LmpClient.Systems.Chat.ChatSystem");
var received = chatSystem.Methods.Single(method => method.Name == "ProcessReceivedMessages");
var tuple = received.Body.Variables.Single(variable => variable.VariableType.FullName.StartsWith("System.Tuple`3<"));
var visibilityCheck = received.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method &&
    method.DeclaringType.FullName == window.FullName && method.Name == "get_ChatVisible");
processor = received.Body.GetILProcessor();
var tupleLoad = Instruction.Create(OpCodes.Ldloc, tuple);
processor.InsertAfter(visibilityCheck, tupleLoad);
processor.InsertAfter(tupleLoad, Instruction.Create(OpCodes.Call, cloned.Methods.Single(method => method.Name == "HidePopup")));
var duration = received.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_R4 && Equals(instruction.Operand, 5f));
duration.OpCode = OpCodes.Ldloc;
duration.Operand = tuple;
processor.InsertAfter(duration, Instruction.Create(OpCodes.Call, cloned.Methods.Single(method => method.Name == "PopupDuration")));

var version = module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(field => field.Name == "Value");
if (!Equals(version.Constant, "kesas-rc14")) throw new InvalidDataException("Wrong embedded version.");
version.Constant = "kesas-rc15";
int versionStrings = 0;
int textChanges = 0;
foreach (var type in module.Types.SelectMany(Flatten))
foreach (var method in type.Methods.Where(method => method.HasBody))
foreach (var instruction in method.Body.Instructions)
{
    if (instruction.OpCode != OpCodes.Ldstr) continue;
    if (Equals(instruction.Operand, "kesas-rc14")) { instruction.Operand = "kesas-rc15"; versionStrings++; }
    else if (replacements.TryGetValue((string)instruction.Operand, out var rewritten)) { instruction.Operand = rewritten; textChanges++; }
}
if (versionStrings != 1) throw new InvalidDataException("Unexpected inlined version count " + versionStrings);

// Imported source references must resolve inside this module, never to an extra plugin.
foreach (var reference in module.AssemblyReferences.Where(reference =>
             reference.Name == fixes.Name.Name || reference.Name == assembly.Name.Name).ToArray())
    module.AssemblyReferences.Remove(reference);
module.Mvid = Guid.NewGuid();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
assembly.Write(args[2]);
using var verified = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { AssemblyResolver = resolver });
if (verified.MainModule.GetType(source.FullName)?.Methods.Count != source.Methods.Count)
    throw new InvalidDataException("Injected methods did not round-trip.");
Console.WriteLine($"RC15: player list, global chat, visible server notices, {textChanges} text changes.");
var allowedChanges = new HashSet<string>
{
    "LmpClient.Aetherfront.AetherfrontClientSystem::Update",
    "LmpClient.Apf.ApfClientSettings::get_ClientVersion",
    "LmpClient.Windows.Aetherfront.AetherfrontFactionWindow::DrawHome",
    "LmpClient.Windows.Aetherfront.AetherfrontFactionWindow::DrawUnifiedChat",
    "LmpClient.Windows.Aetherfront.AetherfrontFactionWindow::get_ChatVisible",
    "LmpClient.Systems.Chat.ChatSystem::ProcessReceivedMessages"
};
int unchangedMethods = 0;
using var pristine = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
foreach (var originalType in pristine.MainModule.Types.SelectMany(Flatten))
{
    if (originalType.FullName == source.FullName) continue;
    var nextType = verified.MainModule.Types.SelectMany(Flatten).Single(type => type.FullName == originalType.FullName);
    foreach (var original in originalType.Methods)
    {
        var next = nextType.Methods.Single(method => method.FullName == original.FullName);
        if (allowedChanges.Contains(originalType.FullName + "::" + original.Name)) continue;
        if (BodySignature(original, replacements) != BodySignature(next, null))
            throw new InvalidDataException("Unrelated method changed: " + original.FullName);
        unchangedMethods++;
    }
}
int resolved = 0;
foreach (var method in verified.MainModule.GetType(source.FullName).Methods.Where(method => method.HasBody))
foreach (var instruction in method.Body.Instructions)
{
    if (instruction.Operand is MethodReference called)
    {
        if (called.Resolve() == null) throw new InvalidDataException("Unresolved method " + called.FullName);
        resolved++;
    }
    if (instruction.Operand is FieldReference field && field.Resolve() == null)
        throw new InvalidDataException("Unresolved field " + field.FullName);
}
Console.WriteLine($"Verified {unchangedMethods} unrelated methods unchanged; {resolved} injected method references resolve.");

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
