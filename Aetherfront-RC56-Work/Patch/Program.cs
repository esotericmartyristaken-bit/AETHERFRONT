using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 4 || args.Length > 5) throw new ArgumentException("server|client|clock RC55_INPUT DONOR_OR_SERVER_OR_DASH NEW_OUTPUT [KSP_MANAGED]");
string mode = args[0], input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[3]);
var pins = new Dictionary<string, string>
{
 ["server"] = "08f8de5de2b4d50fa3e8314e35e821c347bb2d0a7f7f323526519cd8c538db36",
 ["client"] = "58dcfafb7a930cf1d1fcfe19be4780037becf2b277341e7d586307be5ff4f659",
 ["clock"] = "f940d7aebd4769582c16b546378cfb91e0f9047af6ffe57768929f6f7a94c5e1"
};
if (!pins.TryGetValue(mode, out var pin) || Hash(input) != pin || File.Exists(output) || output == input)
 throw new InvalidDataException("The pinned RC55 input and a fresh output are required.");
using var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
if (args[2] != "-") resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
if (args.Length == 5) resolver.AddSearchDirectory(Path.GetFullPath(args[4]));
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
var module = assembly.MainModule;
var originalMethods = module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Methods).ToArray();
var before = originalMethods.ToDictionary(m => m.FullName, Image);
var fields = module.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Fields).ToDictionary(f => f.FullName, f => f.Attributes + ":" + f.Constant);
var resources = module.Resources.Select(ResourceImage).ToArray();
var changed = new HashSet<string>(); var addedFields = new HashSet<string>(); var addedTypes = new List<string>();

if (mode == "client" || mode == "server") Wire();
if (mode == "client")
{
 var getter = Method("LmpClient.Apf.ApfClientSettings", "get_ClientVersion");
 var literal = getter.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
 var constant = module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
 if (!Equals(literal.Operand, "kesas-rc55") || !Equals(constant.Constant, "kesas-rc55")) throw new InvalidDataException("Client identity changed.");
 literal.Operand = constant.Constant = "kesas-rc57"; changed.Add(getter.FullName);
}
else if (mode == "server")
{
 using var donor = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
 string[] roots = { "TrafficScheduler", "TransportCapacity", "WarpRecoveryPolicy", "HoldRelease", "HoldLease", "BoundaryWire", "StateSection", "StateDelta", "JsonSlices",
  "Server.SyncTimings", "Server.SnapshotStore", "Server.SnapshotTransport", "Server.VesselBackup", "Server.BoundaryServer", "Server.ReceivePump", "Server.ServerTraffic", "Server.SurvivalRosterRepair" };
 roots = roots.Select(n => "Aetherfront.Synchronization." + n).ToArray();
 new ShippingTypeImporter(module, donor.MainModule).Copy(roots); addedTypes.AddRange(roots);
 var traffic = module.GetType("Aetherfront.Synchronization.Server.ServerTraffic");
 var drain = traffic.Methods.Single(m => m.Name == "Drain"); drain.Body = new MethodBody(drain);
 var il = drain.Body.GetILProcessor();
 il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Call, Method("Server.Client.ClientStructure", "get_OutboundBudget")));
 il.Append(Instruction.Create(OpCodes.Ldarg_1)); il.Append(Instruction.Create(OpCodes.Callvirt, Method("Server.Client.OutboundQueueBudget", "TryDequeue"))); il.Append(Instruction.Create(OpCodes.Ret));
 int redirects = 0;
 foreach (var method in originalMethods.Where(m => m.HasBody))
  foreach (var instruction in method.Body.Instructions)
   if (instruction.Operand is MethodReference call && (call.DeclaringType.FullName == "Aetherfront.Stability.Server.ServerTraffic" ||
       call.DeclaringType.FullName == "Aetherfront.Stability.Server.ReceivePump" || call.DeclaringType.FullName == "Aetherfront.Stability.Server.WarpRetry"))
   {
    string target = call.DeclaringType.Name == "WarpRetry" ? "BoundaryServer" : call.DeclaringType.Name;
    var replacement = module.GetType("Aetherfront.Synchronization.Server." + target).Methods.SingleOrDefault(m => m.Name == call.Name && m.Parameters.Count == call.Parameters.Count);
    if (replacement == null) continue; // Obsolete RC55 helper bodies remain unused, unchanged unless mapped.
    instruction.Operand = replacement; changed.Add(method.FullName); redirects++;
   }
 if (redirects < 8) throw new InvalidDataException("Native RC55 dispatch call sites changed: " + redirects);
 Forward(Method("Server.Aetherfront.AetherfrontServerStore", "ReadSnapshot"), Method("Aetherfront.Synchronization.Server.SnapshotStore", "Read"), true);
 Forward(Method("Server.System.VesselStoreSystem", "BackupVessels"), Method("Aetherfront.Synchronization.Server.VesselBackup", "Run"), false);
 var handle = Method("Server.Message.AetherfrontMsgReader", "HandleLocked");
 var snapshot = handle.Body.Instructions.Single(i => i.Operand is MethodReference c && c.Name == "SendSnapshot");
 handle.Body.GetILProcessor().InsertBefore(snapshot, Instruction.Create(OpCodes.Ldarg_2));
 snapshot.Operand = Method("Aetherfront.Synchronization.Server.SnapshotTransport", "Enqueue"); changed.Add(handle.FullName);
 var startup = Method("Server.MainServer", "Main");
 var roster = startup.Body.Instructions.Single(i => i.Operand is MethodReference call && call.DeclaringType.FullName == "Server.System.KerbalSystem" && call.Name == "MigrateCrewRoster" && call.Parameters.Count == 0);
 startup.Body.GetILProcessor().InsertAfter(roster, Instruction.Create(OpCodes.Call, Method("Aetherfront.Synchronization.Server.SurvivalRosterRepair", "Run")));
 changed.Add(startup.FullName);
 Instrument(module.GetType("Aetherfront.Synchronization.Server.SyncTimings"));
}
else
{
 using var server = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
 Instrument(server.MainModule.GetType("Aetherfront.Synchronization.Server.SyncTimings"));
}

foreach (var method in originalMethods.Where(m => changed.Contains(m.FullName) && m.HasBody))
{ foreach (var i in method.Body.Instructions) i.OpCode = Long(i.OpCode); method.Body.MaxStackSize += 8; }
Directory.CreateDirectory(Path.GetDirectoryName(output)!); assembly.Write(output);
using var verify = AssemblyDefinition.ReadAssembly(output);
var actual = verify.MainModule.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
if (!before.Keys.Where(k => actual[k] != before[k]).ToHashSet().SetEquals(changed) || assembly.Name.FullName != verify.Name.FullName ||
 !resources.SequenceEqual(verify.MainModule.Resources.Select(ResourceImage))) throw new InvalidDataException("Shipping method/resource identity audit failed.");
var afterFields = verify.MainModule.Types.SelectMany(ShippingTypeImporter.All).SelectMany(t => t.Fields).ToDictionary(f => f.FullName, f => f.Attributes + ":" + f.Constant);
if (fields.Any(p => afterFields[p.Key] != p.Value && !(mode == "client" && p.Key.EndsWith("ApfClientBuildVersion::Value")))) throw new InvalidDataException("Unrelated field changed.");
var audit = new { version = "kesas-rc57", mode, inputSha256 = Hash(input), outputSha256 = Hash(output), identityPreserved = true, unrelatedMethodsPreserved = true, changed, addedFields, addedTypes };
File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(audit));

void Wire()
{
 var type = module.GetType("LmpCommon.Message.Data.Warp.WarpNewSubspaceMsgData");
 foreach (string name in new[] { "BoundarySession", "BoundaryRequest" })
 { var field = new FieldDefinition(name, FieldAttributes.Public, module.TypeSystem.Int64); type.Fields.Add(field); addedFields.Add(field.FullName); }
 foreach (string name in new[] { "InternalSerialize", "InternalDeserialize" })
 {
  var method = type.Methods.Single(m => m.Name == name); var end = method.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
  var buffer = method.Parameters[0].ParameterType.Resolve();
  while (buffer.FullName != "Lidgren.Network.NetBuffer") buffer = buffer.BaseType.Resolve();
  var transfer = name == "InternalSerialize" ? buffer.Methods.Single(m => m.Name == "Write" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Int64") :
   buffer.Methods.Single(m => m.Name == "ReadInt64" && m.Parameters.Count == 0);
  foreach (var field in type.Fields.Where(f => f.Name == "BoundarySession" || f.Name == "BoundaryRequest"))
  {
   var instructions = name == "InternalSerialize" ? new[] { Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field), Instruction.Create(OpCodes.Callvirt, module.ImportReference(transfer)) } :
    new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Callvirt, module.ImportReference(transfer)), Instruction.Create(OpCodes.Stfld, field) };
   foreach (var i in instructions) method.Body.GetILProcessor().InsertBefore(end, i);
  }
  changed.Add(method.FullName);
 }
 var size = type.Methods.Single(m => m.Name == "InternalGetMessageSize"); var ret = size.Body.Instructions.Last();
 size.Body.GetILProcessor().InsertBefore(ret, Instruction.Create(OpCodes.Ldc_I4, 16)); size.Body.GetILProcessor().InsertBefore(ret, Instruction.Create(OpCodes.Add)); changed.Add(size.FullName);
}
void Forward(MethodDefinition method, MethodDefinition target, bool instance)
{
 method.Body = new MethodBody(method); var il = method.Body.GetILProcessor();
 if (instance) il.Append(Instruction.Create(OpCodes.Ldarg_0));
 il.Append(Instruction.Create(OpCodes.Call, target)); il.Append(Instruction.Create(OpCodes.Ret)); changed.Add(method.FullName);
}
void Instrument(TypeDefinition timing)
{
 MethodReference Hook(string name) => module.ImportReference(timing.Methods.Single(m => m.Name == name));
 foreach (var method in originalMethods.Where(m => m.HasBody))
 {
  bool store = method.DeclaringType.FullName == "Server.Aetherfront.AetherfrontServerStore";
  bool fleet = method.Body.Instructions.Any(i => i.Operand is FieldReference f && f.FullName == "System.Object Server.Aetherfront.AetherfrontFleetGuard::Gate");
  foreach (var instruction in method.Body.Instructions.ToArray())
  {
   if (!(instruction.Operand is MethodReference call)) continue;
   if ((store || fleet) && call.DeclaringType.FullName == "System.Threading.Monitor")
   {
    if (call.Name == "Enter" && call.Parameters.Count == 2) { instruction.Operand = Hook(store ? "EnterStore" : "EnterFleet"); changed.Add(method.FullName); }
    else if (call.Name == "Enter" && call.Parameters.Count == 1) { instruction.Operand = Hook(store ? "EnterStoreSimple" : "EnterFleetSimple"); changed.Add(method.FullName); }
    else if (call.Name == "Exit" && call.Parameters.Count == 1) { instruction.Operand = Hook("Exit"); changed.Add(method.FullName); }
   }
   if (call.DeclaringType.FullName == "System.IO.FileStream" && call.Name == "Flush" && call.Parameters.Count == 1 && call.Parameters[0].ParameterType.FullName == "System.Boolean")
   { instruction.OpCode = OpCodes.Call; instruction.Operand = Hook("Flush"); changed.Add(method.FullName); }
   else if ((call.DeclaringType.FullName == "System.IO.Stream" || call.DeclaringType.FullName == "System.IO.FileStream") && call.Name == "Write" && call.Parameters.Count == 3 && call.Parameters[0].ParameterType.FullName == "System.Byte[]")
   { instruction.OpCode = OpCodes.Call; instruction.Operand = Hook("Write"); changed.Add(method.FullName); }
   else if (store && call.DeclaringType.FullName == "Aetherfront.Core.AetherfrontJson" && (call.Name == "Serialize" || call.Name == "Deserialize"))
   {
    var ticks = new VariableDefinition(module.TypeSystem.Int64); method.Body.Variables.Add(ticks); method.Body.InitLocals = true;
    var il = method.Body.GetILProcessor(); il.InsertBefore(instruction, Instruction.Create(OpCodes.Call, Hook("Start"))); il.InsertBefore(instruction, Instruction.Create(OpCodes.Stloc, ticks));
    var label = Instruction.Create(OpCodes.Ldstr, "state." + call.Name.ToLowerInvariant()); var load = Instruction.Create(OpCodes.Ldloc, ticks); var finish = Instruction.Create(OpCodes.Call, Hook("Complete"));
    il.InsertAfter(instruction, label); il.InsertAfter(label, load); il.InsertAfter(load, finish); changed.Add(method.FullName);
   }
  }
 }
}
MethodDefinition Method(string type, string name) => module.GetType(type).Methods.Single(m => m.Name == name);
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
