using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length is not (3 or 4) || args[0] is not ("transport" or "version")) throw new ArgumentException("transport|version INPUT OUTPUT [DEPENDENCIES]");
string input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Separate output required");
string inputHash = Hash(input);
string expectedName;
if (args[0] == "version") {
    if (inputHash != "aac0a288b3a349f13be6afeb598eb36d0bc611dcfe3c9f122a81e4de31c2d340") throw new IOException("Unreviewed RC41 client");
    expectedName = "LmpClient";
} else {
    expectedName = inputHash switch {
        "4b57bce899c7ab814165cc9d46737122bb953bfc6f2bd353a03013458dc22bf3" => "Lidgren.Network",
        "b1508b4051942377168a4f1bb18708145c1054441ddb5b5b7c3ca213ee3fa778" => "Lidgren.Core",
        _ => throw new IOException("Unreviewed RC41 transport")
    };
}
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
if (args.Length == 4) resolver.AddSearchDirectory(Path.GetFullPath(args[3]));
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
if (assembly.Name.Name != expectedName || assembly.Name.HasPublicKey) throw new IOException("Unexpected assembly identity");
var module = assembly.MainModule;
var types = module.Types.SelectMany(All).ToArray();
var methods = types.SelectMany(t => t.Methods).ToArray();
var before = methods.ToDictionary(m => m.FullName, Image);
var fields = types.SelectMany(t => t.Fields).Select(FieldImage).ToArray();
var references = module.AssemblyReferences.Select(r => r.FullName).ToArray();
var resources = module.Resources.Select(ResourceImage).ToArray();
MethodDefinition changed;
MethodDefinition? helper = null;
if (args[0] == "version") {
    changed = module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m => m.Name == "get_ClientVersion");
    var constant = module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
    var literal = changed.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
    if (!Equals(literal.Operand, "kesas-rc41") || !Equals(constant.Constant, "kesas-rc41")) throw new IOException("Unexpected client version");
    literal.Operand = "kesas-rc42"; constant.Constant = "kesas-rc42";
} else {
    var peer = module.GetType("Lidgren.Network.NetPeer");
    changed = peer.Methods.Single(m => m.Name == "GetStorage" && m.Parameters.Count == 1);
    if (changed.Parameters[0].ParameterType.FullName != "System.Int32" || changed.ReturnType.FullName != "System.Byte[]" ||
        peer.Methods.Any(m => m.Name == "IsPooledStorageSuitable") || !peer.Methods.Any(m => m.Name == "CompactOrderedMessageStorage")) throw new IOException("Unexpected RC40 receive fix or allocator contract");
    var condition = changed.Body.Instructions.Single(i => Long(i.OpCode) == OpCodes.Blt && i.Previous.OpCode == OpCodes.Ldarg_1 && i.Previous.Previous.OpCode == OpCodes.Conv_I4 && i.Previous.Previous.Previous.OpCode == OpCodes.Ldlen);
    if (condition.Previous.Previous.Previous.Previous.OpCode != OpCodes.Ldloc_3 || changed.Body.Variables[3].VariableType.FullName != "System.Byte[]" || condition.Next.OpCode != OpCodes.Ldarg_0 || condition.Operand is not Instruction skip)
        throw new IOException("Unexpected existing pool-candidate branch");
    helper = new MethodDefinition("IsPooledStorageSuitable", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
    helper.Parameters.Add(new ParameterDefinition("capacity", ParameterAttributes.None, module.TypeSystem.Int32));
    helper.Parameters.Add(new ParameterDefinition("requested", ParameterAttributes.None, module.TypeSystem.Int32));
    peer.Methods.Add(helper);
    var il = helper.Body.GetILProcessor();
    var accept = Instruction.Create(OpCodes.Ldc_I4_1);
    // Preserve the allocator's pre-existing invalid-negative-input behavior.
    // Real message requests are non-negative; do not broaden this fix's scope.
    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Blt, accept);
    // Keep ordinary pool slack. Skip only when BOTH excess limits are exceeded.
    // Int64 arithmetic prevents overflow for requests near Int32.MaxValue.
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_I8);
    il.Emit(OpCodes.Ldc_I4, 1024); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ble, accept);
    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_I8);
    il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ble, accept);
    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); il.Append(accept); il.Emit(OpCodes.Ret);
    var entry = condition.Next;
    var hook = new[] { Instruction.Create(OpCodes.Ldloc, changed.Body.Variables[3]), Instruction.Create(OpCodes.Ldlen), Instruction.Create(OpCodes.Conv_I4), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, helper), Instruction.Create(OpCodes.Brfalse, skip) };
    Redirect(changed, entry, hook[0]);
    foreach (var i in hook) changed.Body.GetILProcessor().InsertBefore(entry, i);
    foreach (var i in changed.Body.Instructions) i.OpCode = Long(i.OpCode);
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
assembly.Write(output);
using var verify = AssemblyDefinition.ReadAssembly(output);
var afterTypes = verify.MainModule.Types.SelectMany(All).ToArray();
var after = afterTypes.SelectMany(t => t.Methods).ToArray();
var changes = after.Where(m => before.TryGetValue(m.FullName, out var old) && old != Image(m)).Select(m => m.FullName).ToArray();
var added = after.Where(m => !before.ContainsKey(m.FullName)).Select(m => m.FullName).ToArray();
if (changes.Length != 1 || changes[0] != changed.FullName || after.Length != methods.Length + (helper == null ? 0 : 1) || added.Length != (helper == null ? 0 : 1) ||
    afterTypes.Length != types.Length || verify.Name.FullName != assembly.Name.FullName ||
    !verify.MainModule.AssemblyReferences.Select(r => r.FullName).SequenceEqual(references) || !verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources))
    throw new IOException("Changes exceeded reviewed allocator/version graft");
var afterFields = afterTypes.SelectMany(t => t.Fields).Select(FieldImage).ToArray();
if (helper != null) {
    if (added[0] != helper.FullName || !afterFields.SequenceEqual(fields)) throw new IOException("Unexpected transport field/helper change");
    var check = after.Single(m => m.FullName == changed.FullName);
    var call = check.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.FullName == helper.FullName);
    var first = call.Previous.Previous.Previous.Previous;
    var last = call.Next;
    if (first.OpCode != OpCodes.Ldloc || first.Next.OpCode != OpCodes.Ldlen || first.Next.Next.OpCode != OpCodes.Conv_I4 || first.Next.Next.Next.OpCode != OpCodes.Ldarg_1 || last.OpCode != OpCodes.Brfalse)
        throw new IOException("Allocator suitability hook differs");
    var original = last.Next;
    Redirect(check, first, original);
    for (int n = 0; n < 6; n++) { var next = first.Next; check.Body.Instructions.Remove(first); first = next; }
    if (Image(check) != before[changed.FullName]) throw new IOException("Existing allocator mutation, synchronization or accounting changed");
} else if (afterFields.Where((s, n) => s != fields[n]).Count() != 1) throw new IOException("Expected one version constant change");
var report = new { mode = args[0], assembly = expectedName, inputSha256 = inputHash, outputSha256 = Hash(output), version = "kesas-rc42", changed = changes, added,
    allOtherMethodsUnchanged = true, originalAllocatorBodyPreserved = helper != null, assemblyIdentityPreserved = true,
    sendMemoryLimitBytes = 67108864, receiveConnectionLimitBytes = 4194304, receivePeerLimitBytes = 67108864,
    poolMemoryLimitBytes = 67108864, maximumExcessBytesWithoutRatioLimit = 1024, maximumCapacityRatioWithoutExcessLimit = 2,
    existingReceiveFixPreserved = true, sendReservationAndFragmentationMethodsUnchanged = true };
File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t) { yield return t; foreach (var child in t.NestedTypes.SelectMany(All)) yield return child; }
static string FieldImage(FieldDefinition f) => f.FullName + ":" + f.Attributes + ":" + f.Constant;
static string ResourceImage(Resource r) => r.Name + ":" + r.ResourceType + ":" + (r is EmbeddedResource e ? Convert.ToHexString(SHA256.HashData(e.GetResourceData())) : r.ToString());
static OpCode Long(OpCode c) => c.Code switch { Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
    Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un, Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un,
    Code.Ble_S => OpCodes.Ble, Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un, Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave, _ => c };
static void Redirect(MethodDefinition m, Instruction from, Instruction to) {
    foreach (var i in m.Body.Instructions) { if (ReferenceEquals(i.Operand, from)) i.Operand = to; if (i.Operand is Instruction[] a) for (int n = 0; n < a.Length; n++) if (a[n] == from) a[n] = to; }
    foreach (var h in m.Body.ExceptionHandlers) { if (h.TryStart == from) h.TryStart = to; if (h.TryEnd == from) h.TryEnd = to; if (h.HandlerStart == from) h.HandlerStart = to; if (h.HandlerEnd == from) h.HandlerEnd = to; if (h.FilterStart == from) h.FilterStart = to; }
}
static string Image(MethodDefinition m) {
    if (!m.HasBody) return "none";
    var code = m.Body.Instructions;
    string Operand(object? o) => o switch { null => "", Instruction b => "@" + code.IndexOf(b), Instruction[] a => string.Join(",", a.Select(code.IndexOf)), IFormattable n => n.ToString(null, CultureInfo.InvariantCulture), _ => o.ToString() ?? "" };
    return string.Join("|", code.Select(i => Long(i.OpCode) + ":" + Operand(i.Operand))) + " locals:" + string.Join(",", m.Body.Variables.Select(v => v.VariableType.FullName)) +
        " handlers:" + string.Join("|", m.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + code.IndexOf(h.TryStart) + ":" + code.IndexOf(h.TryEnd) + ":" + code.IndexOf(h.HandlerStart) + ":" + code.IndexOf(h.HandlerEnd) + ":" + code.IndexOf(h.FilterStart) + ":" + h.CatchType));
}
