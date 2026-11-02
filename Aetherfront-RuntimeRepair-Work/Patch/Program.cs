using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 4) throw new ArgumentException("Patch <shipping engine> <shipping continuity> <compiled hooks> <output>");
string output = Path.GetFullPath(args[3]); Directory.CreateDirectory(output);
using var engine = AssemblyDefinition.ReadAssembly(args[0]);
using var continuity = AssemblyDefinition.ReadAssembly(args[1]);
using var hooks = AssemblyDefinition.ReadAssembly(args[2]);
var module = engine.MainModule;
var before = Images(engine); var beforeClient = Images(continuity);
var source = hooks.MainModule.GetType("KesasInstall.RuntimeAssemblyRepair");
if (source == null || source.HasNestedTypes || module.GetType(source.FullName) != null) throw new InvalidDataException("Unexpected repair hook inventory");
var copied = new TypeDefinition(source.Namespace, source.Name, source.Attributes, module.ImportReference(source.BaseType));
module.Types.Add(copied);
var fields = new Dictionary<string, FieldDefinition>(); var methods = new Dictionary<string, MethodDefinition>();
foreach (var field in source.Fields)
{
    var added = new FieldDefinition(field.Name, field.Attributes, module.ImportReference(field.FieldType));
    if (field.HasConstant) added.Constant = field.Constant;
    copied.Fields.Add(added); fields.Add(field.FullName, added);
}
foreach (var method in source.Methods)
{
    var added = new MethodDefinition(method.Name, method.Attributes, module.ImportReference(method.ReturnType)) { ImplAttributes = method.ImplAttributes };
    foreach (var parameter in method.Parameters) added.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, module.ImportReference(parameter.ParameterType)));
    copied.Methods.Add(added); methods.Add(method.FullName, added);
}
foreach (var method in source.Methods.Where(x => x.HasBody))
{
    var added = methods[method.FullName]; added.Body = new MethodBody(added) { InitLocals = method.Body.InitLocals, MaxStackSize = method.Body.MaxStackSize };
    foreach (var local in method.Body.Variables) added.Body.Variables.Add(new VariableDefinition(module.ImportReference(local.VariableType)));
    var instructions = method.Body.Instructions.ToDictionary(x => x, x => Instruction.Create(OpCodes.Nop));
    foreach (var instruction in method.Body.Instructions)
    {
        var next = instructions[instruction]; next.OpCode = instruction.OpCode;
        next.Operand = instruction.Operand switch
        {
            null => null,
            Instruction branch => instructions[branch],
            Instruction[] branches => branches.Select(x => instructions[x]).ToArray(),
            VariableDefinition local => added.Body.Variables[local.Index],
            ParameterDefinition parameter => added.Parameters[parameter.Index],
            MethodReference reference when methods.TryGetValue(reference.FullName, out var own) => own,
            MethodReference reference => module.ImportReference(reference),
            FieldReference reference when fields.TryGetValue(reference.FullName, out var own) => own,
            FieldReference reference => module.ImportReference(reference),
            TypeReference type when type.FullName == source.FullName => copied,
            TypeReference type => module.ImportReference(type),
            string or int or long or float or double or sbyte or byte => instruction.Operand,
            _ => throw new InvalidDataException("Unreviewed hook operand " + instruction.Operand.GetType())
        };
        added.Body.Instructions.Add(next);
    }
    foreach (var handler in method.Body.ExceptionHandlers)
    {
        Instruction? Map(Instruction? instruction) => instruction == null ? null : instructions[instruction];
        added.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
        {
            TryStart = Map(handler.TryStart), TryEnd = Map(handler.TryEnd), HandlerStart = Map(handler.HandlerStart), HandlerEnd = Map(handler.HandlerEnd),
            FilterStart = Map(handler.FilterStart), CatchType = handler.CatchType == null ? null : module.ImportReference(handler.CatchType)
        });
    }
}
MethodDefinition Hook(string name) => copied.Methods.Single(x => x.Name == name);
var installer = module.GetType("KesasInstall.Installer");
var root = installer.Fields.Single(x => x.Name == "Root");
var run = installer.Methods.Single(x => x.Name == "RunCore");
var noChange = run.Body.Instructions.Where(x => x.OpCode == OpCodes.Newobj && x.Operand is MethodReference m && m.DeclaringType.FullName == "KesasInstall.InstallResult" && x.Next.OpCode == OpCodes.Ret).ToArray();
if (noChange.Length != 2 || noChange.Any(x => x.Next.Next == null)) throw new InvalidDataException("Unreviewed install receipt bypasses");
foreach (var point in noChange)
    InsertRetarget(run, point, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, root), Instruction.Create(OpCodes.Call, Hook("NeedsRepair")), Instruction.Create(OpCodes.Brtrue, point.Next.Next));
var completed = installer.Methods.Single(x => x.Name == "CompletedClientMatches");
var original = completed.Body.Instructions[0];
Insert(completed, original, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, root), Instruction.Create(OpCodes.Call, Hook("NeedsClientPayload")), Instruction.Create(OpCodes.Brfalse, original), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ret));
var scan = installer.Methods.Single(x => x.Name == "ScanConnection");
Insert(scan, scan.Body.Instructions[0], Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, Hook("BeforeConnection")));
var native = module.GetType("KesasInstall.NativeInstall");
var nativeRun = native.Methods.Single(x => x.Name == "Run");
var applied = nativeRun.Body.Instructions.Single(x => x.Operand is MethodReference m && m.DeclaringType.FullName == "KesasInstall.PostExit" && m.Name == "Apply");
var merge = applied.Next;
if (merge.OpCode != OpCodes.Ldarg_2 || (merge.Next.Operand as string) != "pack") throw new InvalidDataException("Unreviewed helper apply/completion boundary");
var transactionLocal = nativeRun.Body.Variables.Single(x => x.VariableType.FullName == "System.Object");
InsertRetarget(nativeRun, merge, Instruction.Create(OpCodes.Ldsfld, native.Fields.Single(x => x.Name == "root")), Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Ldloc, transactionLocal), Instruction.Create(OpCodes.Call, Hook("BeforeHelperComplete")));
var postExit = module.GetType("KesasInstall.PostExit").Methods.Single(x => x.Name == "Apply");
var graphics = postExit.Body.Instructions.Single(x => x.Operand is MethodReference m && m.DeclaringType.FullName == "KesasInstall.Installer" && m.Name == "SetGraphics");
var packLocal = postExit.Body.Variables.Single(x => x.VariableType.FullName == "ApfCore.ApfSyncTransaction");
InsertRetarget(postExit, graphics, Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldloc, packLocal), Instruction.Create(OpCodes.Call, Hook("ShouldSetGraphics")),
    Instruction.Create(OpCodes.Brtrue, graphics), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Br, graphics.Next));
var awake = continuity.MainModule.GetType("Aetherfront.Continuity.Client.SimulationAuthority").Methods.Single(x => x.Name == "Awake");
var log = awake.Body.Instructions.Select(x => x.Operand).OfType<MethodReference>().First(x => x.DeclaringType.FullName == "UnityEngine.Debug" && x.Name == "Log");
var first = awake.Body.Instructions[0];
Insert(awake, first, Instruction.Create(OpCodes.Call, continuity.MainModule.ImportReference(Hook("LoadedGameCompatible"))), Instruction.Create(OpCodes.Brtrue, first),
    Instruction.Create(OpCodes.Ldstr, "[AetherfrontContinuity] This KSP game assembly needs the signed runtime repair. Connect and choose Restart & apply; multiplayer initialization is deferred."),
    Instruction.Create(OpCodes.Call, log), Instruction.Create(OpCodes.Ret));
var changedEngine = new[] { run, completed, scan, nativeRun, postExit };
foreach (var method in changedEngine.Concat(new[] { awake })) ExpandBranches(method);
string enginePath = Path.Combine(output, "KesasInstall.dll"), clientPath = Path.Combine(output, "AetherfrontContinuityClient.dll");
engine.Write(enginePath); continuity.Write(clientPath);
using var verify = AssemblyDefinition.ReadAssembly(enginePath); using var clientVerify = AssemblyDefinition.ReadAssembly(clientPath);
var after = Images(verify); var afterClient = Images(clientVerify);
var changed = before.Keys.Where(x => before[x] != after[x]).Order().ToArray();
var clientChanged = beforeClient.Keys.Where(x => beforeClient[x] != afterClient[x]).ToArray();
if (!changed.SequenceEqual(changedEngine.Select(x => x.FullName).Order()) || clientChanged.Length != 1 || clientChanged[0] != awake.FullName || before.Keys.Any(x => !after.ContainsKey(x))) throw new InvalidDataException("Patch exceeded reviewed methods");
if (verify.MainModule.AssemblyReferences.Any(x => x.Name == hooks.Name.Name)) throw new InvalidDataException("Repair hooks were not self-contained");
File.WriteAllText(Path.Combine(output, "METHOD-AUDIT.json"), JsonSerializer.Serialize(new
{
    engineInputSha256 = Hash(args[0]), engineOutputSha256 = Hash(enginePath), continuityInputSha256 = Hash(args[1]), continuityOutputSha256 = Hash(clientPath),
    changedEngineMethods = changed, changedContinuityMethods = clientChanged, addedMethods = after.Keys.Except(before.Keys).ToArray(),
    allOtherMethodsUnchanged = true, unchangedReturnPathsGuarded = noChange.Length, externalHookDependency = false
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Patched five updater boundaries and one startup boundary; unrelated method bodies unchanged.");

static void Insert(MethodDefinition method, Instruction before, params Instruction[] added)
{
    foreach (var instruction in added) method.Body.GetILProcessor().InsertBefore(before, instruction);
    method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 8);
}
static void InsertRetarget(MethodDefinition method, Instruction before, params Instruction[] added)
{
    // Preserve all existing control-flow entries through the new guard, including
    // the client and rollback branches joining the helper completion boundary.
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand == before) instruction.Operand = added[0];
        else if (instruction.Operand is Instruction[] targets)
            for (int i = 0; i < targets.Length; i++) if (targets[i] == before) targets[i] = added[0];
    }
    Insert(method, before, added);
}
static void ExpandBranches(MethodDefinition method)
{
    var opcodes = typeof(OpCodes).GetFields().Where(x => x.FieldType == typeof(OpCode)).Select(x => (OpCode)x.GetValue(null)!).ToArray();
    foreach (var instruction in method.Body.Instructions)
        if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            instruction.OpCode = opcodes.Single(x => x.Name == instruction.OpCode.Name.Substring(0, instruction.OpCode.Name.Length - 2));
}
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static IEnumerable<TypeDefinition> Types(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Types));
static Dictionary<string, string> Images(AssemblyDefinition assembly) => assembly.MainModule.Types.SelectMany(Types).SelectMany(x => x.Methods).ToDictionary(x => x.FullName, Image);
static string Image(MethodDefinition method)
{
    if (!method.HasBody) return "no-body";
    var code = method.Body.Instructions;
    string Operand(object? value) => value switch
    {
        null => "", Instruction branch => "@" + code.IndexOf(branch), Instruction[] branches => String.Join(",", branches.Select(x => code.IndexOf(x))),
        VariableDefinition local => "local:" + local.Index, ParameterDefinition parameter => "arg:" + parameter.Index,
        IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    return String.Join("|", code.Select(x => x.OpCode + ":" + Operand(x.Operand))) + " locals:" + String.Join(",", method.Body.Variables.Select(x => x.VariableType.FullName)) +
        " handlers:" + String.Join("|", method.Body.ExceptionHandlers.Select(x => x.HandlerType + ":" + code.IndexOf(x.TryStart) + ":" + code.IndexOf(x.TryEnd) + ":" + code.IndexOf(x.HandlerStart) + ":" + code.IndexOf(x.HandlerEnd)));
}
