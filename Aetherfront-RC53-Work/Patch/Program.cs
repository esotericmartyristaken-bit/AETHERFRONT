using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 5) throw new ArgumentException("MODE RC52_INPUT DONOR_OR_DASH NEW_OUTPUT KSP_MANAGED");
string mode = args[0], input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[3]);
var pins = new Dictionary<string, string>
{
    ["server-helper"] = "3a4f9a633c9fa345f4cd2d4ae88fc163d1ee93b01b005e1ac9539496ade23c89",
    ["client-helper"] = "ccd52d49da5c1e6237340fbb19872935d409bda74ed448b7e31b4c0576c55afc",
    ["clock"] = "fa77ccf1f305aa68331981ac2e24f5a41386238b0216a164788fb1ad8194d8af",
    ["server"] = "ee563a78af5b0ac2d690e855883b26154392059a991ac2585470ad617975e804",
    ["client"] = "e653cfb3b10eb84ad8f211832b11e7a33849b33ee94128107a810cfe5f9fb9bb"
};
if (!pins.TryGetValue(mode, out var pin) || Hash(input) != pin || input == output || File.Exists(output))
    throw new InvalidDataException("Use the pinned RC52 input and a new output file.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Path.GetFullPath(args[4]));
if (args[2] != "-") resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
var module = assembly.MainModule;
var before = module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(type => type.Methods).ToDictionary(method => method.FullName, Image);
var beforeFields = module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(type => type.Fields).ToDictionary(field => field.FullName, FieldImage);
var resources = module.Resources.Select(ResourceImage).ToArray();
var references = module.AssemblyReferences.Select(reference => reference.FullName).ToArray();
var changed = new HashSet<string>();
var added = new List<string>();

if (mode == "server-helper" || mode == "client-helper")
{
    using var donor = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
    var roots = mode == "server-helper" ? new[]
    {
        "Server.Aetherfront.PersonalFlightCheckpoints", "Aetherfront.Personal.Server.FlightCheckpointRuntime", "Aetherfront.Personal.Server.FlightCheckpointServer", "Aetherfront.Personal.Server.DeepFreezeRecovery"
    } : new[] { "Aetherfront.Personal.Client.FlightCheckpointClient" };
    new ShippingTypeImporter(module, donor.MainModule).Copy(roots);
    added.AddRange(roots);
    if (mode == "server-helper")
    {
        var original = module.GetType("Aetherfront.Personal.Server.PersonalServer").Methods.Single(method => method.Name == "TryHandle");
        var handler = module.GetType(roots[2]).Methods.Single(method => method.Name == "TryHandle" && method.Parameters.Count == 2);
        PrefixHandled(original, handler, 2);
    }
    else
    {
        var personal = module.GetType("Aetherfront.Personal.Client.PersonalClient");
        var checkpoint = module.GetType(roots[0]);
        var apply = personal.Methods.Single(method => method.Name == "Apply");
        apply.Body.GetILProcessor().InsertBefore(apply.Body.Instructions[0], Instruction.Create(OpCodes.Call,
            checkpoint.Methods.Single(method => method.Name == "EnsureStarted")));
        changed.Add(apply.FullName);
        PrefixHandled(personal.Methods.Single(method => method.Name == "CaptureScienceReceipt"),
            checkpoint.Methods.Single(method => method.Name == "Capture"), 1);
    }
}
else if (mode == "clock" || mode == "server")
{
    using var helper = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
    var service = helper.MainModule.GetType("Aetherfront.Personal.Server.FlightCheckpointServer") ?? throw new InvalidDataException("Patched server helper is required.");
    if (mode == "clock")
    {
        var resume = module.GetType("Aetherfront.Continuity.Server.PersonalClockServer").Methods.Single(method => method.Name == "ResumeAndSend");
        var returns = resume.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray();
        foreach (var exit in returns)
        {
            var load = Instruction.Create(OpCodes.Ldarg_0);
            var call = Instruction.Create(OpCodes.Call, module.ImportReference(service.Methods.Single(method => method.Name == "AttachAfterResume")));
            resume.Body.GetILProcessor().InsertBefore(exit, load);
            resume.Body.GetILProcessor().InsertBefore(exit, call);
            Redirect(resume, exit, load, new[] { load, call });
        }
        var first = resume.Body.Instructions[0];
        Insert(resume, first, new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, module.ImportReference(service.Methods.Single(method => method.Name == "RecoverBeforeResume"))),
            Instruction.Create(OpCodes.Brtrue, first), Instruction.Create(OpCodes.Ret)
        });
        changed.Add(resume.FullName);
    }
    else
    {
        var recovery = helper.MainModule.GetType("Aetherfront.Personal.Server.DeepFreezeRecovery");
        var validate = module.GetType("Server.Aetherfront.AetherfrontDeepFreezeCheckpointCoordinator").Methods.Single(method => method.Name == "ValidateRecoveredWorld");
        Insert(validate, validate.Body.Instructions[0], new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, module.ImportReference(recovery.Methods.Single(method => method.Name == "PrepareRecoveredWorld")))
        });
        changed.Add(validate.FullName);
        var proto = module.GetType("Server.Message.VesselMsgReader").Methods.Single(method => method.Name == "HandleVesselProto");
        var relay = proto.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && method.Name == "RelayVesselMessage");
        var freshCheck = proto.Body.Instructions.Last(instruction => instruction.Operand is MethodReference method && method.Name == "VesselExists");
        if (freshCheck.Next?.OpCode != OpCodes.Ldc_I4_0 || freshCheck.Next.Next?.OpCode != OpCodes.Ceq)
            throw new InvalidDataException("The pinned native fresh-vessel guard changed.");
        var storeFresh = freshCheck.Next.Next.Next;
        VariableDefinition fresh = storeFresh.OpCode.Code switch
        {
            Code.Stloc_0 => proto.Body.Variables[0], Code.Stloc_1 => proto.Body.Variables[1],
            Code.Stloc_2 => proto.Body.Variables[2], Code.Stloc_3 => proto.Body.Variables[3],
            Code.Stloc or Code.Stloc_S => (VariableDefinition)storeFresh.Operand,
            _ => throw new InvalidDataException("Expected the native fresh-vessel boolean local.")
        };
        if (fresh.VariableType.FullName != "System.Boolean") throw new InvalidDataException("Invalid native launch flag type.");
        Insert(proto, relay, new[]
        {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldloc, fresh),
            Instruction.Create(OpCodes.Call, module.ImportReference(service.Methods.Single(method => method.Name == "ObserveProto")))
        });
        changed.Add(proto.FullName);
    }
}
else if (mode == "client")
{
    var getter = module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(method => method.Name == "get_ClientVersion");
    var literal = getter.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr);
    var constant = module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(field => field.Name == "Value");
    if (!Equals(literal.Operand, "kesas-rc52") || !Equals(constant.Constant, "kesas-rc52")) throw new InvalidDataException("Unexpected client build identity.");
    literal.Operand = "kesas-rc53"; constant.Constant = "kesas-rc53";
    changed.Add(getter.FullName);
}

foreach (var method in module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(type => type.Methods).Where(method => changed.Contains(method.FullName)))
{
    foreach (var instruction in method.Body.Instructions) instruction.OpCode = Long(instruction.OpCode);
    method.Body.MaxStackSize += 4;
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
assembly.Write(output);
using var verify = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
var verified = verify.MainModule.Types.SelectMany(ShippingTypeImporter.All).ToArray();
var actual = verified.SelectMany(type => type.Methods).ToDictionary(method => method.FullName, Image);
var differences = before.Keys.Where(name => !actual.TryGetValue(name, out var image) || image != before[name]).ToHashSet();
if (!differences.SetEquals(changed) || !verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources) || verify.Name.FullName != assembly.Name.FullName)
    throw new InvalidDataException("The checkpoint graft changed unrelated shipping behavior.");
var fields = verified.SelectMany(type => type.Fields).ToDictionary(field => field.FullName, FieldImage);
var fieldChanges = beforeFields.Keys.Where(name => !fields.TryGetValue(name, out var image) || image != beforeFields[name]).ToArray();
if (fieldChanges.Any(name => mode != "client" || !name.EndsWith("ApfClientBuildVersion::Value", StringComparison.Ordinal)))
    throw new InvalidDataException("An unrelated shipping field changed.");
var newReferences = verify.MainModule.AssemblyReferences.Select(reference => reference.FullName).Except(references).ToArray();
var allowedReferences = new HashSet<string>(StringComparer.Ordinal)
{
    "AetherfrontPersonalServer", "AetherfrontContinuityServer", "UnityEngine.InputLegacyModule", "LunaConfigNode", "System.Net.Primitives",
    "System.Text.Json", "System.IO.Compression", "System.IO.Compression.Brotli", "System.Threading.Timer",
    "System.Threading", "System.Reflection", "System.Runtime", "System.Runtime.Extensions", "System.Linq",
    "System.Collections", "System.Security.Cryptography", "System.Memory", "System.Text.Encoding.Extensions", "ApfCore"
};
if (newReferences.Any(reference => !allowedReferences.Contains(reference.Split(',')[0])))
    throw new InvalidDataException("Unreviewed graft dependency: " + string.Join("; ", newReferences));
var audit = new { version = "kesas-rc53", mode, inputSha256 = Hash(input), outputSha256 = Hash(output),
    identityPreserved = true, unrelatedMethodsPreserved = true, changed = differences, fieldChanges, addedTypes = added, newReferences };
File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(audit));

void PrefixHandled(MethodDefinition original, MethodDefinition handler, int arguments)
{
    var first = original.Body.Instructions[0];
    var instructions = new List<Instruction>();
    for (int index = 0; index < arguments; index++) instructions.Add(Instruction.Create(OpCodes.Ldarg, original.Parameters[index]));
    instructions.Add(Instruction.Create(OpCodes.Call, handler));
    instructions.Add(Instruction.Create(OpCodes.Brfalse, first));
    instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); instructions.Add(Instruction.Create(OpCodes.Ret));
    Insert(original, first, instructions);
    changed.Add(original.FullName);
}

static void Insert(MethodDefinition method, Instruction before, IEnumerable<Instruction> instructions)
{
    foreach (var instruction in instructions) method.Body.GetILProcessor().InsertBefore(before, instruction);
}

static void Redirect(MethodDefinition method, Instruction from, Instruction to, IEnumerable<Instruction> excluded)
{
    var skip = excluded.ToHashSet();
    foreach (var instruction in method.Body.Instructions.Where(instruction => !skip.Contains(instruction)))
    {
        if (ReferenceEquals(instruction.Operand, from)) instruction.Operand = to;
        if (instruction.Operand is Instruction[] branches)
            for (int index = 0; index < branches.Length; index++) if (branches[index] == from) branches[index] = to;
    }
    foreach (var handler in method.Body.ExceptionHandlers)
    {
        if (handler.TryStart == from) handler.TryStart = to;
        if (handler.TryEnd == from) handler.TryEnd = to;
        if (handler.HandlerStart == from) handler.HandlerStart = to;
        if (handler.HandlerEnd == from) handler.HandlerEnd = to;
        if (handler.FilterStart == from) handler.FilterStart = to;
    }
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static string FieldImage(FieldDefinition field) => field.Attributes + ":" + field.Constant;
static string ResourceImage(Resource resource) => resource.Name + ":" + resource.ResourceType + ":" +
    (resource is EmbeddedResource embedded ? Convert.ToHexString(SHA256.HashData(embedded.GetResourceData())) : resource.ToString());
static OpCode Long(OpCode code) => code.Code switch
{
    Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
    Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
    Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
    Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
    Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave, _ => code
};
static string Image(MethodDefinition method)
{
    if (!method.HasBody) return "none";
    var code = method.Body.Instructions;
    string Operand(object? value) => value switch
    {
        null => "", Instruction branch => "@" + code.IndexOf(branch), Instruction[] branches => string.Join(",", branches.Select(code.IndexOf)),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    return string.Join("|", code.Select(instruction => Long(instruction.OpCode) + ":" + Operand(instruction.Operand))) +
        " locals:" + string.Join(",", method.Body.Variables.Select(local => local.VariableType.FullName)) +
        " handlers:" + string.Join("|", method.Body.ExceptionHandlers.Select(handler => handler.HandlerType + ":" + code.IndexOf(handler.TryStart) + ":" +
            code.IndexOf(handler.TryEnd) + ":" + code.IndexOf(handler.HandlerStart) + ":" + code.IndexOf(handler.HandlerEnd) + ":" + code.IndexOf(handler.FilterStart) + ":" + handler.CatchType));
}
