using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("Expected pinned Server.dll, donor and fresh output path");
const string rc50 = "127eeaa83eadfade41912d78dc5fb55f3f725cf4a551a599128a67c285473cf9";
const string fixture = "cf9141209a2cafbbb990f95d5191b26f9e5483cc7547518995a16411abe1cdd6";
if (Hash(args[0]) is not (rc50 or fixture))
    throw new InvalidDataException("Unexpected input server SHA-256");
if (File.Exists(args[2])) throw new IOException("Use a fresh output path");

using var server = AssemblyDefinition.ReadAssembly(args[0]);
using var donor = AssemblyDefinition.ReadAssembly(args[1]);
var module = server.MainModule;
var target = module.GetType("Server.System.WarpSystemReceiver");
var source = donor.MainModule.GetType(target.FullName).Methods.Single(method => method.Name == "KeepCurrentSubspace");
var transition = target.Methods.Single(method => method.Name == "HandleChangeSubspaceLocked");
var before = Inventory(module);

var references = module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
    .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand)
    .OfType<MemberReference>().GroupBy(reference => reference.FullName)
    .ToDictionary(group => group.Key, group => group.First());
MemberReference Bind(MemberReference reference)
{
    if (module.GetType(reference.DeclaringType.FullName) is TypeDefinition type)
    {
        if (reference is MethodReference && reference is not GenericInstanceMethod)
            return type.Methods.Single(method => method.FullName == reference.FullName);
        if (reference is FieldReference)
            return type.Fields.Single(field => field.FullName == reference.FullName);
    }
    if (references.TryGetValue(reference.FullName, out var found)) return found;
    if (reference is GenericInstanceMethod generic)
    {
        var existing = references.Values.OfType<GenericInstanceMethod>().SingleOrDefault(method =>
            method.Name == generic.Name && method.DeclaringType.FullName == generic.DeclaringType.FullName &&
            method.Parameters.Count == generic.Parameters.Count &&
            method.GenericArguments.Select(argument => argument.FullName)
                .SequenceEqual(generic.GenericArguments.Select(argument => argument.FullName)));
        if (existing != null) return existing;
        if (module.GetType(generic.DeclaringType.FullName) is TypeDefinition declaringType)
        {
            var definition = declaringType.Methods.Single(method =>
                method.Name == generic.Name && method.GenericParameters.Count == generic.GenericArguments.Count &&
                method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(generic.Parameters.Select(parameter => parameter.ParameterType.FullName)));
            var constructed = new GenericInstanceMethod(definition);
            foreach (var argument in generic.GenericArguments)
                constructed.GenericArguments.Add(module.GetType(argument.FullName)
                    ?? throw new InvalidDataException("Unreviewed generic argument " + argument));
            return constructed;
        }
    }
    throw new InvalidDataException("Donor uses unreviewed reference " + reference.FullName);
}

var helper = new MethodDefinition(source.Name, source.Attributes, module.TypeSystem.Boolean);
foreach (var parameter in transition.Parameters)
    helper.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
target.Methods.Add(helper);
helper.Body.InitLocals = source.Body.InitLocals;
foreach (var local in source.Body.Variables)
    helper.Body.Variables.Add(new VariableDefinition(module.GetType(local.VariableType.FullName)
        ?? throw new InvalidDataException("Unexpected donor local " + local.VariableType)));

var copies = source.Body.Instructions.ToDictionary(instruction => instruction, _ => Instruction.Create(OpCodes.Nop));
foreach (var instruction in source.Body.Instructions)
{
    var copy = copies[instruction];
    copy.OpCode = instruction.OpCode;
    copy.Operand = instruction.Operand switch
    {
        null => null,
        Instruction branch => copies[branch],
        VariableDefinition local => helper.Body.Variables[local.Index],
        ParameterDefinition parameter => helper.Parameters[parameter.Index],
        MemberReference reference => Bind(reference),
        string => instruction.Operand,
        _ => throw new InvalidDataException("Unexpected donor operand " + instruction.Operand)
    };
    helper.Body.Instructions.Add(copy);
}

if (transition.HasThis || transition.Parameters.Count != 2 || transition.ReturnType.MetadataType != MetadataType.Void)
    throw new InvalidDataException("Unexpected transition signature");
var prefix = transition.Body.GetILProcessor();
var first = transition.Body.Instructions[0];
prefix.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
prefix.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
prefix.InsertBefore(first, Instruction.Create(OpCodes.Call, helper));
prefix.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
prefix.InsertBefore(first, Instruction.Create(OpCodes.Ret));

using var candidate = new MemoryStream();
server.Write(candidate);
candidate.Position = 0;
using var review = AssemblyDefinition.ReadAssembly(candidate);
var after = Inventory(review.MainModule);
if (review.Name.FullName != server.Name.FullName ||
    !review.MainModule.AssemblyReferences.Select(reference => reference.FullName)
        .SequenceEqual(module.AssemblyReferences.Select(reference => reference.FullName)) ||
    after.Count != before.Count + 1 ||
    before.Any(pair => !after.TryGetValue(pair.Key, out string? body) || (pair.Value != body) != (pair.Key == transition.FullName)))
    throw new InvalidDataException("Unexpected assembly identity, references, or method changes");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
using var output = new FileStream(args[2], FileMode.CreateNew);
candidate.Position = 0;
candidate.CopyTo(output);

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static Dictionary<string, string> Inventory(ModuleDefinition module) => module.GetTypes()
    .SelectMany(type => type.Methods).Where(method => method.HasBody)
    .ToDictionary(method => method.FullName, method => string.Join("\n", method.Body.Instructions));
