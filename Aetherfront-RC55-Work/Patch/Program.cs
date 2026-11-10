using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 4 || args.Length > 5) throw new ArgumentException("server|client RC54_INPUT DONOR_OR_DASH NEW_OUTPUT [KSP_MANAGED]");
string mode = args[0], input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[3]);
string pin = mode == "server" ? "7b6a1a6ea9f2a5f96200dc61eb68f41685c8f514ab0dd9c31623f74521b93081" :
    mode == "client" ? "7369dcc89a6979e54d21223d2f3d428f2748e81add86cc0fa773c39985d73388" : throw new ArgumentException(mode);
if (Hash(input) != pin || File.Exists(output) || input == output) throw new InvalidDataException("Pinned RC54 input and new output required.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
if (args.Length == 5) resolver.AddSearchDirectory(Path.GetFullPath(args[4]));
using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
var module = asm.MainModule;
var before = module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
var fields = module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Fields).ToDictionary(f => f.FullName, f => f.Attributes + ":" + f.Constant);
var resources = module.Resources.Select(ResourceImage).ToArray();
var changed = new HashSet<string>();
if (mode == "client")
{
    var getter = module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m => m.Name == "get_ClientVersion");
    var literal = getter.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
    var constant = module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
    if (!Equals(literal.Operand, "kesas-rc54") || !Equals(constant.Constant, "kesas-rc54")) throw new InvalidDataException("Build identity changed.");
    literal.Operand = constant.Constant = "kesas-rc55"; changed.Add(getter.FullName);
}
else
{
    using var donor = AssemblyDefinition.ReadAssembly(args[2]);
    new ShippingTypeImporter(module, donor.MainModule).Copy("Aetherfront.Stability.TrafficScheduler", "Aetherfront.Stability.TransportCapacity",
        "Aetherfront.Stability.Server.ServerTraffic", "Aetherfront.Stability.Server.WarpRetry", "Aetherfront.Stability.Server.ReceivePump");
    var traffic = module.GetType("Aetherfront.Stability.Server.ServerTraffic");
    var drain = traffic.Methods.Single(m => m.Name == "Drain");
    drain.Body = new MethodBody(drain);
    var il = drain.Body.GetILProcessor();
    il.Append(Instruction.Create(OpCodes.Ldarg_0));
    il.Append(Instruction.Create(OpCodes.Call, module.GetType("Server.Client.ClientStructure").Methods.Single(m => m.Name == "get_OutboundBudget")));
    il.Append(Instruction.Create(OpCodes.Ldarg_1));
    il.Append(Instruction.Create(OpCodes.Callvirt, module.GetType("Server.Client.OutboundQueueBudget").Methods.Single(m => m.Name == "TryDequeue")));
    il.Append(Instruction.Create(OpCodes.Ret));
    var move = module.Types.SelectMany(ShippingTypeImporter.All).Single(t => t.FullName.StartsWith("Server.Client.ClientStructure/<SendMessagesThread>")).Methods.Single(m => m.Name == "MoveNext");
    var getter = move.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "get_OutboundBudget");
    getter.OpCode = OpCodes.Nop; getter.Operand = null;
    var dequeue = move.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Server.Client.OutboundQueueBudget" && m.Name == "TryDequeue");
    dequeue.OpCode = OpCodes.Call; dequeue.Operand = traffic.Methods.Single(m => m.Name == "TryDequeue"); changed.Add(move.FullName);
    var enqueue = module.GetType("Server.Server.MessageQueuer").Methods.Single(m => m.Name == "SendToClient" && !m.HasGenericParameters);
    var originalEnqueue = enqueue.Body.Instructions[0];
    Insert(enqueue, originalEnqueue, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1),
        Instruction.Create(OpCodes.Call, traffic.Methods.Single(m => m.Name == "Enqueue")), Instruction.Create(OpCodes.Ret) });
    var reader = module.GetType("Server.Message.VesselMsgReader").Methods.Single(m => m.Name == "HandleMessage");
    // The donor predicate accepts the shared base interface; no wire or permission semantics change.
    var cosmetic = module.GetType("Aetherfront.Stability.TrafficScheduler").Methods.Single(m => m.Name == "Cosmetic");
    var first = reader.Body.Instructions[0];
    var dataGetter = donor.MainModule.GetType("Aetherfront.Stability.Server.ServerTraffic").Methods.Single(m => m.Name == "AllowEnqueue").Body.Instructions
        .Select(i => i.Operand).OfType<MethodReference>().First(m => m.Name == "get_Data");
    Insert(reader, first, new[] { Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Callvirt, module.ImportReference(dataGetter)),
        Instruction.Create(OpCodes.Call, cosmetic), Instruction.Create(OpCodes.Brfalse, first), Instruction.Create(OpCodes.Ret) });
    var create = module.GetType("Server.System.WarpSystemReceiver").Methods.Single(m => m.Name == "TryCreateSubspace");
    var retry = module.GetType("Aetherfront.Stability.Server.WarpRetry");
    first = create.Body.Instructions[0];
    Insert(create, first, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1),
        Instruction.Create(OpCodes.Call, retry.Methods.Single(m => m.Name == "TryReplay")), Instruction.Create(OpCodes.Brfalse, first),
        Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ret) });
    var publish = create.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "SendToAllClients");
    Insert(create, publish, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1) });
    publish.Operand = retry.Methods.Single(m => m.Name == "Publish");
    var receive = module.GetType("Server.Server.MessageReceiver").Methods.Single(m => m.Name == "ReceiveCallback");
    var dispatch = receive.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Server.Message.Base.ReaderBase" && m.Name == "HandleMessage");
    dispatch.OpCode = OpCodes.Call; dispatch.Operand = module.GetType("Aetherfront.Stability.Server.ReceivePump").Methods.Single(m => m.Name == "Enqueue");
    var deferDisconnect = module.GetType("Aetherfront.Stability.Server.ReceivePump").Methods.Single(m => m.Name == "EnqueueDisconnect");
    var intakeDisconnects = receive.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "SendConnectionEnd").ToArray();
    if (intakeDisconnects.Length != 3) throw new InvalidDataException("Native intake rejection guards changed.");
    foreach (var item in intakeDisconnects) item.Operand = deferDisconnect;
    changed.Add(receive.FullName);
    var receiveLoop = module.Types.SelectMany(ShippingTypeImporter.All).Single(t => t.FullName.StartsWith("Server.Server.LidgrenServer/<StartReceivingMessages>")).Methods.Single(m => m.Name == "MoveNext");
    var lifecycle = receiveLoop.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "DisconnectClient" && m.DeclaringType.FullName == "Server.Client.ClientConnectionHandler");
    lifecycle.Operand = deferDisconnect; changed.Add(receiveLoop.FullName);
}
foreach (var method in module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Methods).Where(m => changed.Contains(m.FullName)))
{ foreach (var i in method.Body.Instructions) i.OpCode = Long(i.OpCode); method.Body.MaxStackSize += 4; }
Directory.CreateDirectory(Path.GetDirectoryName(output)!); asm.Write(output);
using var verify = AssemblyDefinition.ReadAssembly(output);
var actual = verify.MainModule.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
var differences = before.Keys.Where(k => !actual.TryGetValue(k, out var value) || value != before[k]).ToHashSet();
if (!differences.SetEquals(changed) || !verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources) || asm.Name.FullName != verify.Name.FullName)
    throw new InvalidDataException("Unrelated shipping behavior changed.");
var actualFields = verify.MainModule.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Fields).ToDictionary(f => f.FullName, f => f.Attributes + ":" + f.Constant);
if (fields.Any(p => actualFields[p.Key] != p.Value && !(mode == "client" && p.Key.EndsWith("ApfClientBuildVersion::Value")))) throw new InvalidDataException("Unrelated field changed.");
var audit = new { version = "kesas-rc55", mode, inputSha256 = Hash(input), outputSha256 = Hash(output), identityPreserved = true, unrelatedMethodsPreserved = true, changed };
File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(audit));
void Insert(MethodDefinition method, Instruction first, IEnumerable<Instruction> additions)
{ foreach (var item in additions) method.Body.GetILProcessor().InsertBefore(first, item); changed.Add(method.FullName); }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static string ResourceImage(Resource r) => r.Name + ":" + r.ResourceType + ":" + (r is EmbeddedResource e ? Convert.ToHexString(SHA256.HashData(e.GetResourceData())) : r.ToString());
static OpCode Long(OpCode code) => code.Code switch
{
 Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue, Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge,
 Code.Bge_Un_S => OpCodes.Bge_Un, Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble, Code.Ble_Un_S => OpCodes.Ble_Un,
 Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un, Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave, _ => code
};
static string Image(MethodDefinition method)
{
 if (!method.HasBody) return "none"; var code = method.Body.Instructions;
 string Operand(object? value) => value switch { null => "", Instruction b => "@" + code.IndexOf(b), Instruction[] bs => string.Join(",", bs.Select(code.IndexOf)), IFormattable n => n.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
 return string.Join("|", code.Select(i => Long(i.OpCode) + ":" + Operand(i.Operand))) + " locals:" + string.Join(",", method.Body.Variables.Select(v => v.VariableType.FullName)) +
 " handlers:" + string.Join("|", method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + code.IndexOf(h.TryStart) + ":" + code.IndexOf(h.TryEnd) + ":" + code.IndexOf(h.HandlerStart) + ":" + code.IndexOf(h.HandlerEnd) + ":" + code.IndexOf(h.FilterStart) + ":" + h.CatchType));
}
