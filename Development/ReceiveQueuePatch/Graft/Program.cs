using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("Expected shipping Lidgren.Core.dll, donor DLL and fresh output path");
const string shippingHash = "99288eb5872e7a9608dc830295eb353b15db9b5fa26b33ceab94846201e329fa";
const string shippingClientHash = "b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3";
string inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant();
bool dedicatedServer = inputHash == shippingHash;
if (!dedicatedServer && inputHash != shippingClientHash)
    throw new InvalidDataException("Unexpected shipping transport SHA-256");
if (File.Exists(args[2])) throw new IOException("Use a fresh candidate output path");

using var shipping = AssemblyDefinition.ReadAssembly(args[0]);
using var donor = AssemblyDefinition.ReadAssembly(args[1]);
var module = shipping.MainModule;
var peer = module.GetType("Lidgren.Network.NetPeer");
var sourcePeer = donor.MainModule.GetType(peer.FullName);
foreach (var name in new[] { "NetPeer", "NetConnection" })
{
    var type = module.GetType("Lidgren.Network." + name);
    foreach (var field in donor.MainModule.GetType(type.FullName).Fields)
        if (field.Name != "m_pendingIncomingBytes")
            type.Fields.Add(new FieldDefinition(field.Name, field.Attributes, Primitive(field.FieldType)));
}

var sources = new[] { "RejectIncoming", "AccountIncomingRemoval", "IsUnreliableIncoming", "AdvanceReceiveQueueGeneration" }
    .Select(name => sourcePeer.Methods.Single(method => method.Name == name)).ToArray();
foreach (var source in sources)
{
    var method = new MethodDefinition(source.Name, source.Attributes, Primitive(source.ReturnType));
    foreach (var parameter in source.Parameters)
        method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, Type(parameter.ParameterType)));
    peer.Methods.Add(method);
}
foreach (var source in sources)
{
    var method = Method(source.Name);
    method.Body.InitLocals = source.Body.InitLocals;
    method.Body.MaxStackSize = source.Body.MaxStackSize;
    foreach (var variable in source.Body.Variables)
        method.Body.Variables.Add(new VariableDefinition(Type(variable.VariableType)));

    var copies = source.Body.Instructions.ToDictionary(instruction => instruction, _ => Instruction.Create(OpCodes.Nop));
    foreach (var instruction in source.Body.Instructions)
    {
        var copy = copies[instruction];
        copy.OpCode = instruction.OpCode;
        copy.Operand = instruction.Operand switch
        {
            null => null,
            Instruction branch => copies[branch],
            VariableDefinition variable => method.Body.Variables[variable.Index],
            ParameterDefinition parameter => method.Parameters[parameter.Index],
            MethodReference callee => module.GetType(callee.DeclaringType.FullName).Methods.Single(candidate => candidate.FullName == callee.FullName),
            FieldReference field => module.GetType(field.DeclaringType.FullName).Fields.Single(candidate => candidate.FullName == field.FullName),
            int or sbyte => instruction.Operand,
            _ => throw new InvalidDataException("Unexpected admission operand " + instruction.Operand)
        };
        method.Body.Instructions.Add(copy);
    }
}

// The admission hook runs under the existing lock, after the shared limit is
// computed. Keep the branch targets at the original instruction.
var release = Method("ReleaseMessage");
var code = release.Body.GetILProcessor();
var decision = release.Body.Instructions.Single(instruction => instruction.Offset == 0x58 && instruction.OpCode == OpCodes.Stloc_0);
var sharedLimit = new VariableDefinition(module.TypeSystem.Boolean);
var pendingCount = new VariableDefinition(module.TypeSystem.Int32);
release.Body.Variables.Add(sharedLimit);
release.Body.Variables.Add(pendingCount);
var queueCount = release.Body.Instructions.Single(instruction => instruction.Offset == 0x30 &&
    instruction.Operand is MethodReference called && called.Name == "get_Count");
InsertAfter(code, queueCount, Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Stloc, pendingCount));
// Stash the Boolean already on the evaluation stack before pushing 'this'.
decision.OpCode = OpCodes.Stloc;
decision.Operand = sharedLimit;
InsertAfter(code, decision,
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldloc, sharedLimit),
    Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Ldloc, pendingCount),
    Instruction.Create(dedicatedServer ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0),
    Instruction.Create(OpCodes.Call, Method("RejectIncoming")),
    Instruction.Create(OpCodes.Stloc_0));

// Rejected unreliable messages are recycled without a connection shutdown.
var recycle = release.Body.Instructions.Single(instruction => instruction.Offset == 0xba && instruction.OpCode == OpCodes.Ldarg_0);
var check = release.Body.Instructions.Single(instruction => instruction.Offset == 0x8b && instruction.OpCode == OpCodes.Ldarg_1);
var reason = release.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr &&
    Equals(instruction.Operand, "Incoming message queue limit exceeded"));
reason.Operand = dedicatedServer ? "Incoming message queue limit exceeded (server receive)" : "Incoming message queue limit exceeded (client receive)";
InsertAfter(code, check,
    Instruction.Create(OpCodes.Call, Method("IsUnreliableIncoming")),
    Instruction.Create(OpCodes.Brtrue, recycle),
    Instruction.Create(OpCodes.Ldarg_1));

var singleRead = peer.Methods.Single(method => method.Name == "ReadMessage" && method.Parameters.Count == 0);
foreach (var reader in new[] { singleRead, Method("ReadMessages") })
{
    var countBytes = reader.Body.Instructions.Single(instruction =>
        instruction.Operand is MethodReference called && called.Name == "IncomingStorageBytes");
    countBytes.Operand = Method("AccountIncomingRemoval");
}

var initialize = Method("InitializeNetwork");
var reset = initialize.Body.Instructions.Single(instruction =>
    instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field && field.Name == "m_pendingIncomingBytes");
InsertAfter(initialize.Body.GetILProcessor(), reset, Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Method("AdvanceReceiveQueueGeneration")));

// Widen existing short branches whose span changed.
foreach (var method in new[] { release, initialize })
    foreach (var instruction in method.Body.Instructions)
        instruction.OpCode = instruction.OpCode.Code switch
        {
            Code.Br_S => OpCodes.Br,
            Code.Brfalse_S => OpCodes.Brfalse,
            Code.Brtrue_S => OpCodes.Brtrue,
            Code.Beq_S => OpCodes.Beq,
            Code.Bne_Un_S => OpCodes.Bne_Un,
            Code.Bge_S => OpCodes.Bge,
            Code.Leave_S => OpCodes.Leave,
            _ => instruction.OpCode
        };

using var candidate = new MemoryStream();
shipping.Write(candidate);
candidate.Position = 0;
using var review = AssemblyDefinition.ReadAssembly(candidate);
using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
var previous = Inventory(baseline.MainModule);
var current = Inventory(review.MainModule);
var changed = new[] { release, singleRead, Method("ReadMessages"), initialize }.Select(method => method.FullName).ToHashSet();
if (review.Name.FullName != baseline.Name.FullName ||
    !review.MainModule.AssemblyReferences.Select(reference => reference.FullName)
        .SequenceEqual(baseline.MainModule.AssemblyReferences.Select(reference => reference.FullName)) ||
    current.Count != previous.Count + sources.Length ||
    previous.Any(pair => !current.TryGetValue(pair.Key, out string? body) || (pair.Value != body) != changed.Contains(pair.Key)))
    throw new InvalidDataException("Unexpected assembly identity, references, or method changes");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
using var output = new FileStream(args[2], FileMode.CreateNew);
candidate.Position = 0;
candidate.CopyTo(output);

MethodDefinition Method(string name) => peer.Methods.Single(method => method.Name == name);
TypeReference Type(TypeReference type) => module.GetType(type.FullName) ?? Primitive(type);
TypeReference Primitive(TypeReference type) => type.MetadataType switch
{
    MetadataType.Void => module.TypeSystem.Void,
    MetadataType.Boolean => module.TypeSystem.Boolean,
    MetadataType.Int32 => module.TypeSystem.Int32,
    MetadataType.Int64 => module.TypeSystem.Int64,
    _ => throw new InvalidDataException("Unexpected donor type " + type)
};

static void InsertAfter(ILProcessor processor, Instruction anchor, params Instruction[] instructions)
{
    foreach (var instruction in instructions)
    {
        processor.InsertAfter(anchor, instruction);
        anchor = instruction;
    }
}

static Dictionary<string, string> Inventory(ModuleDefinition module) => module.GetTypes()
    .SelectMany(type => type.Methods).Where(method => method.HasBody)
    .ToDictionary(method => method.FullName, method => string.Join("\n", method.Body.Instructions));
