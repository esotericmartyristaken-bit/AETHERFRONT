using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Patcher <input LmpClient.dll> <output LmpClient.dll>");
    return 2;
}

const string TypeName = "LmpClient.Windows.Connection.ConnectionWindow";
const string MethodName = "DrawWindowContent";
const string FingerprintLabel = "Certificate fingerprint (optional)";
const string FingerprintHelp = "Leave blank to keep the certificate from your patch ZIP. For a new server, use the fingerprint supplied by its operator.";
const string OldVersion = "kesas-rc11";
const string NewVersion = "kesas-rc12";

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
var kspManaged = Path.Combine(Directory.GetCurrentDirectory(), "Aetherfront-KSP-Smoke", "KSP_x64_Data", "Managed");
if (Directory.Exists(kspManaged))
    resolver.AddSearchDirectory(kspManaged);
var kspPlugins = Path.Combine(Directory.GetCurrentDirectory(), "Aetherfront-KSP-Smoke", "GameData", "LunaMultiplayer", "Plugins");
if (Directory.Exists(kspPlugins))
    resolver.AddSearchDirectory(kspPlugins);

var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters
{
    AssemblyResolver = resolver,
    ReadSymbols = false
});

var type = assembly.MainModule.GetType(TypeName)
    ?? throw new InvalidOperationException($"Missing type: {TypeName}");
var method = type.Methods.SingleOrDefault(candidate => candidate.Name == MethodName)
    ?? throw new InvalidOperationException($"Missing method: {TypeName}.{MethodName}");
var instructions = method.Body.Instructions;

var startMatches = instructions
    .Select((instruction, index) => (instruction, index))
    .Where(item => item.instruction.OpCode == OpCodes.Ldstr && Equals(item.instruction.Operand, FingerprintLabel))
    .Select(item => item.index)
    .ToArray();
var helpMatches = instructions
    .Select((instruction, index) => (instruction, index))
    .Where(item => item.instruction.OpCode == OpCodes.Ldstr && Equals(item.instruction.Operand, FingerprintHelp))
    .Select(item => item.index)
    .ToArray();

if (startMatches.Length != 1 || helpMatches.Length != 1 || helpMatches[0] <= startMatches[0])
    throw new InvalidOperationException("Expected one intact certificate fingerprint UI block");

var startIndex = startMatches[0];
var endIndex = -1;
for (var index = helpMatches[0] + 1; index < instructions.Count; index++)
{
    if (instructions[index].Operand is MethodReference called &&
        called.DeclaringType.FullName == "UnityEngine.GUILayout" &&
        called.Name == "Label")
    {
        endIndex = index;
        break;
    }
}

if (endIndex < 0)
    throw new InvalidOperationException("Could not find end of certificate fingerprint UI block");

var removed = new HashSet<Instruction>(instructions.Skip(startIndex).Take(endIndex - startIndex + 1));
foreach (var instruction in instructions)
{
    if (instruction.Operand is Instruction target && removed.Contains(target) && !removed.Contains(instruction))
        throw new InvalidOperationException($"External branch enters removed UI block at IL_{target.Offset:x4}");
    if (instruction.Operand is Instruction[] targets && targets.Any(removed.Contains) && !removed.Contains(instruction))
        throw new InvalidOperationException("External switch enters removed UI block");
}
foreach (var handler in method.Body.ExceptionHandlers)
{
    if (new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart }
        .Where(boundary => boundary != null)
        .Any(boundary => removed.Contains(boundary!)))
        throw new InvalidOperationException("Exception-handler boundary intersects removed UI block");
}

foreach (var instruction in removed)
{
    instruction.OpCode = OpCodes.Nop;
    instruction.Operand = null;
}

var buildVersionType = assembly.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion")
    ?? throw new InvalidOperationException("Missing APF client build-version type");
var buildVersionField = buildVersionType.Fields.SingleOrDefault(field => field.Name == "Value" && field.IsLiteral)
    ?? throw new InvalidOperationException("Missing APF client build-version constant");
if (!Equals(buildVersionField.Constant, OldVersion))
    throw new InvalidOperationException($"Unexpected APF client build version: {buildVersionField.Constant}");
buildVersionField.Constant = NewVersion;

var versionStrings = assembly.MainModule.Types
    .SelectMany(Flatten)
    .SelectMany(candidate => candidate.Methods)
    .Where(candidate => candidate.HasBody)
    .SelectMany(candidate => candidate.Body.Instructions)
    .Where(instruction => instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, OldVersion))
    .ToArray();
if (versionStrings.Length != 1)
    throw new InvalidOperationException($"Expected one inlined APF client version, found {versionStrings.Length}");
versionStrings[0].Operand = NewVersion;

if (method.Body.Instructions.Any(instruction =>
        instruction.OpCode == OpCodes.Ldstr &&
        (Equals(instruction.Operand, FingerprintLabel) || Equals(instruction.Operand, FingerprintHelp))))
    throw new InvalidOperationException("Certificate fingerprint UI strings remain after patch");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1], new WriterParameters { WriteSymbols = false });
Console.WriteLine($"Removed {removed.Count} IL instructions from the manual certificate fingerprint UI; set build identity to {NewVersion}.");
return 0;

static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes)
    foreach (var descendant in Flatten(nested))
        yield return descendant;
}
