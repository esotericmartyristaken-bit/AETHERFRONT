using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length is 5 or 6 && args[0] == "version")
{
    string source = Path.GetFullPath(args[1]), target = Path.GetFullPath(args[2]);
    if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || args[3] != "kesas-rc39" || args[4] != "kesas-rc40") throw new IOException("Expected separate RC39 to RC40 version graft");
    if (Hash(source) != "a299b42076f9ef03bcf345f0c6e4f0d1de99261fda8d9ff8af7a66f4e6f1aaf8") throw new IOException("Unreviewed RC39 native client");
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(source)!);
    if (args.Length == 6) resolver.AddSearchDirectory(Path.GetFullPath(args[5]));
    using var native = AssemblyDefinition.ReadAssembly(source, new ReaderParameters { AssemblyResolver = resolver });
    var prior = native.MainModule.Types.SelectMany(All).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
    var getter = native.MainModule.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m => m.Name == "get_ClientVersion");
    var constant = native.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
    var literal = getter.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
    if (!Equals(constant.Constant, args[3]) || !Equals(literal.Operand, args[3])) throw new IOException("Unexpected client version");
    constant.Constant = args[4]; literal.Operand = args[4];
    Directory.CreateDirectory(Path.GetDirectoryName(target)!); native.Write(target);
    using var verifyClient = AssemblyDefinition.ReadAssembly(target);
    var changedClient = verifyClient.MainModule.Types.SelectMany(All).SelectMany(t => t.Methods).Where(m => prior[m.FullName] != Image(m)).Select(m => m.FullName).ToArray();
    if (changedClient.Length != 1 || changedClient[0] != getter.FullName) throw new IOException("Client changes exceeded one version getter");
    var audit = new { inputSha256 = Hash(source), outputSha256 = Hash(target), version = args[4], changed = changedClient, allOtherMethodsUnchanged = true };
    File.WriteAllText(target + ".version-audit.json", JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(audit));
    return;
}
if (args.Length != 2) throw new ArgumentException("Reviewed transport input and separate output required");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Never replace the baseline");
string inputHash = Hash(input);
var allowed = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["c7e85dc6139b7f37d92a6b5ac0797c53cfb56c9c440df7cbeb70f1f866982330"] = "Lidgren.Network",
    ["90a61a0e2db1f7e62aa8b63aa4d4e1f10c877a7a60ce5338d278c2bbc1fe6dc0"] = "Lidgren.Core"
};
if (!allowed.TryGetValue(inputHash, out var expectedName)) throw new IOException("Unreviewed transport baseline");
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true, ReadSymbols = false });
if (assembly.Name.Name != expectedName || assembly.Name.HasPublicKey) throw new IOException("Unexpected transport identity or signing requirement");
var module = assembly.MainModule;
var types = module.Types.SelectMany(All).ToArray();
var methods = types.SelectMany(t => t.Methods).ToArray();
var before = methods.ToDictionary(m => m.FullName, Image);
var references = module.AssemblyReferences.Select(r => r.FullName).ToArray();
var fields = types.SelectMany(t => t.Fields).Select(f => f.FullName).ToArray();
var resources = module.Resources.Select(ResourceImage).ToArray();
var peer = module.GetType("Lidgren.Network.NetPeer");
var buffer = module.GetType("Lidgren.Network.NetBuffer");
var retain = peer.Methods.Single(m => m.Name == "TryRetainOrderedMessage" && m.Parameters.Count == 2);
var data = buffer.Fields.Single(f => f.Name == "m_data");
var length = buffer.Methods.Single(m => m.Name == "get_LengthBytes" && m.Parameters.Count == 0);
if ((!data.IsAssembly && !data.IsFamilyOrAssembly) || data.FieldType.FullName != "System.Byte[]") throw new IOException("Unexpected buffer storage access");
if (peer.Methods.Any(m => m.Name == "CompactOrderedMessageStorage")) throw new IOException("Already patched");
if (!retain.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4 && Equals(i.Operand, 4194304)) ||
    !retain.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4 && Equals(i.Operand, 67108864)) ||
    !retain.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "Reliable receive buffer limit exceeded")))
    throw new IOException("Original allocated-memory protections differ");
var blockCopy = methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
    .FirstOrDefault(m => m.FullName == "System.Void System.Buffer::BlockCopy(System.Array,System.Int32,System.Array,System.Int32,System.Int32)")
    ?? throw new IOException("Reviewed framework BlockCopy reference unavailable");

// Compact only unusually oversized retained packets. Normal in-order traffic,
// allocator policy, pool limits and every existing receive guard stay unchanged.
// Keep the guard counting real allocations; counting only payload would allow
// an unbounded retained-memory attack through pooled backing arrays.
var helper = new MethodDefinition("CompactOrderedMessageStorage", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
helper.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, retain.Parameters[1].ParameterType));
peer.Methods.Add(helper);
helper.Body.InitLocals = true;
helper.Body.MaxStackSize = 8;
var storage = new VariableDefinition(data.FieldType);
var payload = new VariableDefinition(module.TypeSystem.Int32);
var compact = new VariableDefinition(data.FieldType);
helper.Body.Variables.Add(storage); helper.Body.Variables.Add(payload); helper.Body.Variables.Add(compact);
var il = helper.Body.GetILProcessor();
var done = Instruction.Create(OpCodes.Ret);
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse, done);
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, data); il.Emit(OpCodes.Stloc, storage);
il.Emit(OpCodes.Ldloc, storage); il.Emit(OpCodes.Brfalse, done);
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, length); il.Emit(OpCodes.Stloc, payload);
il.Emit(OpCodes.Ldloc, payload); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Blt, done);
il.Emit(OpCodes.Ldloc, payload); il.Emit(OpCodes.Ldloc, storage); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Bgt, done);
// Never allocate a new retained array larger than the existing connection cap.
il.Emit(OpCodes.Ldloc, payload); il.Emit(OpCodes.Ldc_I4, 4194304); il.Emit(OpCodes.Bgt, done);
// Avoid per-packet copies for ordinary pool slack: both tests must exceed bounds.
il.Emit(OpCodes.Ldloc, storage); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I8);
il.Emit(OpCodes.Ldloc, payload); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldc_I4, 1024); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ble, done);
il.Emit(OpCodes.Ldloc, storage); il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I8);
il.Emit(OpCodes.Ldloc, payload); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ble, done);
il.Emit(OpCodes.Ldloc, payload); il.Emit(OpCodes.Newarr, module.TypeSystem.Byte); il.Emit(OpCodes.Stloc, compact);
il.Emit(OpCodes.Ldloc, storage); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldloc, compact); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldloc, payload);
il.Emit(OpCodes.Call, module.ImportReference(blockCopy));
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, compact); il.Emit(OpCodes.Stfld, data);
// Do not put the oversize buffer straight back in the pool to repeat the cycle.
il.Append(done);
var entry = retain.Body.Instructions[0]; var hook = retain.Body.GetILProcessor();
hook.InsertBefore(entry, Instruction.Create(OpCodes.Ldarg_2));
hook.InsertBefore(entry, Instruction.Create(OpCodes.Call, helper));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
assembly.Write(output, new WriterParameters { WriteSymbols = false });

using var verify = AssemblyDefinition.ReadAssembly(output);
var afterTypes = verify.MainModule.Types.SelectMany(All).ToArray();
var after = afterTypes.SelectMany(t => t.Methods).ToArray();
var changed = after.Where(m => before.TryGetValue(m.FullName, out var old) && old != Image(m)).Select(m => m.FullName).ToArray();
var added = after.Where(m => !before.ContainsKey(m.FullName)).Select(m => m.FullName).ToArray();
if (changed.Length != 1 || changed[0] != retain.FullName || added.Length != 1 || added[0] != helper.FullName || after.Length != methods.Length + 1)
    throw new IOException("Transport changes exceeded one guard preamble and one helper");
if (verify.Name.FullName != assembly.Name.FullName || afterTypes.Length != types.Length ||
    !verify.MainModule.AssemblyReferences.Select(r => r.FullName).SequenceEqual(references) ||
    !afterTypes.SelectMany(t => t.Fields).Select(f => f.FullName).SequenceEqual(fields) ||
    !verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources)) throw new IOException("Transport identity, references, fields or resources changed");
var check = after.Single(m => m.FullName == retain.FullName);
if (check.Body.Instructions[0].OpCode != OpCodes.Ldarg_2 || check.Body.Instructions[1].Operand is not MethodReference called || called.Name != helper.Name)
    throw new IOException("Compaction does not precede allocated-byte accounting");
// Removing the reviewed two-instruction preamble must recover the exact old body.
check.Body.Instructions.RemoveAt(0); check.Body.Instructions.RemoveAt(0);
if (Image(check) != before[retain.FullName]) throw new IOException("Existing protection body changed");
var report = new
{
    inputSha256 = inputHash, outputSha256 = Hash(output), assembly = expectedName,
    changed, added, originalMethods = methods.Length, allOtherMethodsUnchanged = true,
    originalGuardBodyUnchanged = true, assemblyIdentityPreserved = true, referencesUnchanged = true,
    perConnectionLimitBytes = 4194304, perPeerLimitBytes = 67108864,
    minimumExcessBytes = 1024, maximumCapacityRatioBeforeCopy = 2, oversizedArrayReturnedToPool = false
};
File.WriteAllText(output + ".transport-audit.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition type) { yield return type; foreach (var child in type.NestedTypes.SelectMany(All)) yield return child; }
static string ResourceImage(Resource resource) => resource.Name + ":" + resource.ResourceType + ":" +
    (resource is EmbeddedResource embedded ? Convert.ToHexString(SHA256.HashData(embedded.GetResourceData())) : resource.ToString());
static string Image(MethodDefinition method)
{
    if (!method.HasBody) return "none";
    var code = method.Body.Instructions;
    string Operand(object? operand) => operand switch
    {
        null => "", Instruction branch => "@" + code.IndexOf(branch),
        Instruction[] branches => string.Join(",", branches.Select(code.IndexOf)),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => operand.ToString() ?? ""
    };
    return string.Join("|", code.Select(i => i.OpCode + ":" + Operand(i.Operand))) +
        " locals:" + string.Join(",", method.Body.Variables.Select(v => v.VariableType.FullName)) +
        " handlers:" + string.Join("|", method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + code.IndexOf(h.TryStart) + ":" + code.IndexOf(h.TryEnd) + ":" + code.IndexOf(h.HandlerStart) + ":" + code.IndexOf(h.HandlerEnd)));
}
