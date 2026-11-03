using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 4 || args[0] is not ("server" or "client")) throw new ArgumentException("server|client INPUT OUTPUT DEPENDENCIES");
string input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Separate output required");
string expectedHash = args[0] == "server" ? "4f3ffa6838b6b9946646273d6cbe586a9b75ecc134531379bd247d37c915d8e4" : "0a0252b971afb8044282429af2401cffc1c46891778a9436f0d2968e408d4c73";
if (Hash(input) != expectedHash) throw new IOException("Unreviewed RC40 baseline");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Path.GetFullPath(args[3]));
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
if (assembly.Name.HasPublicKey) throw new IOException("Unexpected strong-name requirement");
var module = assembly.MainModule;
var types = module.Types.SelectMany(All).ToArray();
var before = types.SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
var references = module.AssemblyReferences.Select(r => r.FullName).ToArray();
var fields = types.SelectMany(t => t.Fields).Select(f => f.FullName + ":" + f.Attributes + ":" + f.Constant).ToArray();
var resources = module.Resources.Select(ResourceImage).ToArray();
MethodDefinition changed;
var insertions = new List<(Instruction Entry, Instruction[] Added)>();
string[] reasons = [
    "Account details are invalid. Please update your client.",
    "Name already in use. Please choose another player name.",
    "Account key changed. Restore your saved key or contact the server owner.",
    "Please choose another player name, or contact the server owner."
];
if (args[0] == "server")
{
    changed = module.GetType("Server.System.IdentityBindingStore").Methods.Single(m => m.Name == "AuthorizeAndPersist");
    if (changed.Parameters.Count != 5 || changed.Parameters[4].ParameterType.FullName != "System.String&" ||
        !changed.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "Identity account or player name belongs to another key; ask the server owner about account enrollment")))
        throw new IOException("Unexpected identity method");
    var blocks = changed.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldc_I4_0 && i.Next.OpCode.Name.StartsWith("stloc") && i.Next.Next.OpCode.Code is Code.Leave or Code.Leave_S).ToArray();
    if (blocks.Length != 4 || reasons.Any(s => s.Length > 80)) throw new IOException("Expected four existing rejection blocks");
    var il = changed.Body.GetILProcessor();
    for (int n = 0; n < blocks.Length; n++)
    {
        var added = new[] { Instruction.Create(OpCodes.Ldarg, changed.Parameters[4]), Instruction.Create(OpCodes.Ldstr, reasons[n]), Instruction.Create(OpCodes.Stind_Ref) };
        Redirect(changed, blocks[n], added[0]);
        foreach (var i in added) il.InsertBefore(blocks[n], i);
        insertions.Add((blocks[n], added));
    }
    // Widen branches without changing their condition or destination.
    foreach (var i in changed.Body.Instructions) i.OpCode = Long(i.OpCode);
}
else
{
    changed = module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m => m.Name == "get_ClientVersion");
    var constant = module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
    var literal = changed.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
    if (!Equals(literal.Operand, "kesas-rc40") || !Equals(constant.Constant, "kesas-rc40")) throw new IOException("Unexpected version");
    literal.Operand = "kesas-rc41"; constant.Constant = "kesas-rc41";
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
assembly.Write(output);
using var verify = AssemblyDefinition.ReadAssembly(output);
var afterTypes = verify.MainModule.Types.SelectMany(All).ToArray();
var after = afterTypes.SelectMany(t => t.Methods).ToArray();
var changes = after.Where(m => !before.TryGetValue(m.FullName, out var image) || image != Image(m)).Select(m => m.FullName).ToArray();
if (changes.Length != 1 || changes[0] != changed.FullName || after.Length != before.Count || afterTypes.Length != types.Length ||
    verify.Name.FullName != assembly.Name.FullName || !verify.MainModule.AssemblyReferences.Select(r => r.FullName).SequenceEqual(references) ||
    !verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources)) throw new IOException("Changes exceeded reviewed method");
var afterFields = afterTypes.SelectMany(t => t.Fields).Select(f => f.FullName + ":" + f.Attributes + ":" + f.Constant).ToArray();
if (args[0] == "server" && !afterFields.SequenceEqual(fields)) throw new IOException("Server fields changed");
if (args[0] == "client" && afterFields.Where((s, n) => s != fields[n]).Count() != 1) throw new IOException("Expected one version constant");
if (args[0] == "server")
{
    var check = after.Single(m => m.FullName == changed.FullName);
    foreach (var reason in reasons)
    {
        var literal = check.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, reason));
        var first = literal.Previous; var last = literal.Next; var original = last.Next;
        if (first.OpCode != OpCodes.Ldarg || ((ParameterDefinition)first.Operand).Index != 4 || last.OpCode != OpCodes.Stind_Ref || original.OpCode != OpCodes.Ldc_I4_0)
            throw new IOException("Rejection reason assignment differs");
        Redirect(check, first, original);
        check.Body.Instructions.Remove(first); check.Body.Instructions.Remove(literal); check.Body.Instructions.Remove(last);
    }
    if (Image(check) != before[changed.FullName]) throw new IOException("Authentication conditions changed");
}
var report = new { mode = args[0], inputSha256 = Hash(input), outputSha256 = Hash(output), version = "kesas-rc41", changed = changes,
    allOtherMethodsUnchanged = true, authenticationConditionsUnchanged = true, assemblyIdentityPreserved = true, reasons = args[0] == "server" ? reasons : [] };
File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition type) { yield return type; foreach (var child in type.NestedTypes.SelectMany(All)) yield return child; }
static string ResourceImage(Resource r) => r.Name + ":" + r.ResourceType + ":" + (r is EmbeddedResource e ? Convert.ToHexString(SHA256.HashData(e.GetResourceData())) : r.ToString());
static OpCode Long(OpCode c) => c.Code switch {
    Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue, Code.Beq_S => OpCodes.Beq,
    Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un, Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un,
    Code.Ble_S => OpCodes.Ble, Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
    Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave, _ => c };
static void Redirect(MethodDefinition method, Instruction from, Instruction to)
{
    foreach (var i in method.Body.Instructions) {
        if (ReferenceEquals(i.Operand, from)) i.Operand = to;
        if (i.Operand is Instruction[] targets) for (int n = 0; n < targets.Length; n++) if (targets[n] == from) targets[n] = to;
    }
    foreach (var h in method.Body.ExceptionHandlers) {
        if (h.TryStart == from) h.TryStart = to; if (h.TryEnd == from) h.TryEnd = to;
        if (h.HandlerStart == from) h.HandlerStart = to; if (h.HandlerEnd == from) h.HandlerEnd = to; if (h.FilterStart == from) h.FilterStart = to;
    }
}
static string Image(MethodDefinition method)
{
    if (!method.HasBody) return "none";
    var code = method.Body.Instructions;
    string Operand(object? o) => o switch { null => "", Instruction b => "@" + code.IndexOf(b), Instruction[] bs => string.Join(",", bs.Select(code.IndexOf)), IFormattable n => n.ToString(null, CultureInfo.InvariantCulture), _ => o.ToString() ?? "" };
    return string.Join("|", code.Select(i => Long(i.OpCode) + ":" + Operand(i.Operand))) + " locals:" + string.Join(",", method.Body.Variables.Select(v => v.VariableType.FullName)) +
        " handlers:" + string.Join("|", method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + code.IndexOf(h.TryStart) + ":" + code.IndexOf(h.TryEnd) + ":" + code.IndexOf(h.HandlerStart) + ":" + code.IndexOf(h.HandlerEnd) + ":" + code.IndexOf(h.FilterStart) + ":" + h.CatchType));
}
