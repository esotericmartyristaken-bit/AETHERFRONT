using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 3) throw new ArgumentException("ContinuityPatch reference|server|client <input> <output> [helper]");
string command = args[0], input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Never modify a baseline");
string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));
if(command=="client" && hash!="E41732F0FD1552522E942034B49EABA57B4F9316827058B1E9ED27035A8280FB")
    throw new IOException("Unreviewed deployed client baseline");
if (command != "client" && hash != "2D91414C403E43081BBD75E7A5F5CB9A7491F1B0FEFD0D832FC020F6A6FA3064")
    throw new IOException("Unreviewed deployed server baseline");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
if (command == "client")
{
    if (args.Length != 5) throw new ArgumentException("Client helper and explicit KSP Managed directory required");
    resolver.AddSearchDirectory(Path.GetFullPath(args[4]));
}
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
var module = assembly.MainModule;
var methods = module.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToArray();
var before = methods.ToDictionary(m => m.FullName, Image);
var expected = new HashSet<string>();
if (command is "reference" or "server")
{
    var scope = module.AssemblyReferences.Single(a => a.Name == "System.Runtime");
    var attributeType = new TypeReference("System.Runtime.CompilerServices", "InternalsVisibleToAttribute", module, scope);
    var constructor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };
    constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
    var attribute = new CustomAttribute(constructor);
    attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "AetherfrontContinuityServer"));
    assembly.CustomAttributes.Add(attribute);
}
if (command is "server" or "client")
{
    if (args.Length != (command == "client" ? 5 : 4)) throw new ArgumentException("Helper required");
    using var helper = AssemblyDefinition.ReadAssembly(args[3]);
    string helperName = command == "server" ? "AetherfrontContinuityServer" : "AetherfrontContinuityClient";
    if (helper.Name.Name != helperName) throw new IOException("Unexpected helper");
    MethodReference Hook(string name) => module.ImportReference(helper.MainModule.Types.SelectMany(AllTypes).SelectMany(t=>t.Methods).Single(m=>m.IsPublic && m.IsStatic && m.Name==name));
    MethodDefinition Target(string type, string name) => module.GetType(type).Methods.Single(m => m.Name == name);
    void Prefix(MethodDefinition method, string hook, int? argument)
    {
        var first = method.Body.Instructions[0]; var edit = method.Body.GetILProcessor();
        if (argument.HasValue) edit.InsertBefore(first, Instruction.Create(OpCodes.Ldarg, method.Parameters[argument.Value]));
        edit.InsertBefore(first, Instruction.Create(OpCodes.Call, Hook(hook))); expected.Add(method.FullName);
    }
    void ResourceWireHooks()
    {
        foreach(string name in new[]{"InternalSerialize","InternalDeserialize","InternalGetMessageSize"})
        {
            var method=Target("LmpCommon.Message.Data.Vessel.VesselResourceMsgData",name);
            if(method.Body.ExceptionHandlers.Count!=0)throw new IOException("Unreviewed resource wire exception handlers");
            var edit=method.Body.GetILProcessor();
            foreach(var ret in method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray())
            {
                ret.OpCode=OpCodes.Ldarg_0;ret.Operand=null;
                var cursor=ret;
                void Append(Instruction next){edit.InsertAfter(cursor,next);cursor=next;}
                if(name=="InternalGetMessageSize")
                {
                    Append(Instruction.Create(OpCodes.Call,Hook("ResourceClockSize")));Append(Instruction.Create(OpCodes.Add));
                }
                else
                {
                    Append(Instruction.Create(OpCodes.Ldarg_1));
                    Append(Instruction.Create(OpCodes.Call,Hook(name=="InternalSerialize"?"SerializeResourceClock":"DeserializeResourceClock")));
                }
                Append(Instruction.Create(OpCodes.Ret));
            }
            method.Body.MaxStackSize=Math.Max(method.Body.MaxStackSize,6);expected.Add(method.FullName);
        }
    }
    ResourceWireHooks();
    void FrozenStatusComparison(MethodDefinition method)
    {
        var comparisons=method.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ldstr && (string)i.Operand=="Missing").ToArray();
        if(comparisons.Length!=1 || comparisons[0].Previous.Operand is not MethodReference statusGetter ||
           statusGetter.DeclaringType.FullName!="LmpCommon.DeepFreezeScenarioCodec/KerbalRecord" || statusGetter.Name!="get_Status" ||
           comparisons[0].Next.Operand is not MethodReference equal || equal.FullName!="System.Boolean System.String::op_Equality(System.String,System.String)")
            throw new IOException("Unreviewed native frozen-state comparison: "+method.FullName);
        comparisons[0].Next.Operand=Hook("FrozenStatusMatches");expected.Add(method.FullName);
    }
    FrozenStatusComparison(Target("LmpCommon.DeepFreezeScenarioMerge","Replace"));
    if(command=="client")
    {
        FrozenStatusComparison(Target("LmpClient.Systems.Scenario.DeepFreezeClientAdapter","IsActuallyFrozen"));
        FrozenStatusComparison(Target("LmpClient.Systems.Scenario.DeepFreezeNativeMapAdapter","ValidateSnapshot"));
    }
    if (command == "server")
    {
        var freeze=Target("Server.Aetherfront.AetherfrontDeepFreezeCheckpointCoordinator","Process");
        var freezeLocks=freeze.Body.Instructions.Where(i=>i.Operand is MethodReference m&&
            m.DeclaringType.FullName=="LmpCommon.Locks.LockQuery"&&m.Name=="ControlLockBelongsToPlayer").ToArray();
        if(freezeLocks.Length!=1)throw new IOException("Unreviewed DeepFreeze checkpoint lock predicate");
        freezeLocks[0].OpCode=OpCodes.Call;freezeLocks[0].Operand=Hook("MayCheckpointFreeze");expected.Add(freeze.FullName);
        var send = Target("Server.System.WarpSystemSender", "SendAllSubspaces");
        send.Body = new MethodBody(send); var il = send.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, Hook("ResumeAndSend")); il.Emit(OpCodes.Ret); expected.Add(send.FullName);
        Prefix(Target("Server.Client.ClientConnectionHandler", "DisconnectClient"), "BeforeDisconnect", 0);
        Prefix(Target("Server.System.BackupSystem", "RunBackup"), "CheckpointAll", null);
        var change = Target("Server.System.WarpSystemReceiver", "HandleChangeSubspace");
        Prefix(change, "Checkpoint", 0);
        foreach (var ret in change.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            ret.OpCode = OpCodes.Ldarg_1; var edit = change.Body.GetILProcessor();
            var call = Instruction.Create(OpCodes.Call, Hook("Checkpoint")); edit.InsertAfter(ret, call); edit.InsertAfter(call, Instruction.Create(OpCodes.Ret));
        }
        expected.Add(change.FullName);
        var position = Target("Server.Message.VesselMsgReader", "HandleLocked");
        var save = position.Body.Instructions.Single(i => i.Operand is MethodReference m &&
            m.DeclaringType.FullName == "Server.System.Vessel.VesselDataUpdater" && m.Name == "WritePositionDataToFile");
        int slot = position.Body.Instructions.IndexOf(save);
        var positionCheck = position.Body.Instructions[slot - 5];
        var latest = position.Body.Instructions[slot - 4];
        var id = position.Body.Instructions[slot - 3];
        var branch = position.Body.Instructions[slot - 2];
        if (positionCheck.Operand is not MethodReference subspaceGetter || subspaceGetter.Name != "get_Subspace" ||
            latest.Operand is not MethodReference latestGetter || latestGetter.FullName != "Server.Context.Subspace Server.Context.WarpContext::get_LatestSubspace()" ||
            id.Operand is not MethodReference idGetter || idGetter.FullName != "System.Int32 Server.Context.Subspace::get_Id()" ||
            branch.OpCode != OpCodes.Bne_Un || position.Body.Instructions[slot - 6].OpCode != OpCodes.Ldarg_1)
            throw new IOException("Unreviewed position persistence condition");
        positionCheck.OpCode = OpCodes.Call; positionCheck.Operand = Hook("MayPersistPosition");
        latest.OpCode = id.OpCode = OpCodes.Nop; latest.Operand = id.Operand = null;
        branch.OpCode = OpCodes.Brfalse;
        var resource=position.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="Server.System.Vessel.VesselDataUpdater" && m.Name=="WriteResourceDataToFile");
        var load=resource.Previous;
        if(position.Body.ExceptionHandlers.Count!=0 || load.OpCode!=OpCodes.Ldloc_0 || position.Body.Variables[0].VariableType.FullName!="LmpCommon.Message.Data.Vessel.VesselBaseMsgData")
            throw new IOException("Unreviewed resource persistence call stack");
        var resourceEdit=position.Body.GetILProcessor();
        resourceEdit.InsertBefore(load,Instruction.Create(OpCodes.Ldarg_1));
        resource.Operand=Hook("CommitResourceClock");
        resourceEdit.InsertAfter(resource,Instruction.Create(OpCodes.Brfalse,position.Body.Instructions.Last(i=>i.OpCode==OpCodes.Ret)));
        expected.Add(position.FullName);
    }
    else
    {
        // Review against RC34's exact client hash before applying this local candidate.
        if (hash != "E41732F0FD1552522E942034B49EABA57B4F9316827058B1E9ED27035A8280FB") throw new IOException("Unreviewed client baseline");
        var scene = Target("LmpClient.Systems.Warp.WarpEvents", "OnSceneChanged");
        var call = scene.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "LmpClient.Systems.Warp.WarpSystem" && m.Name == "get_LatestSubspace");
        call.OpCode = OpCodes.Call; call.Operand = Hook("InitialSubspace"); expected.Add(scene.FullName);
        var capture=Target("LmpClient.Systems.VesselResourceSys.VesselResourceMessageSender","SendVesselResources");
        var dto=capture.Body.Variables.Single(v=>v.VariableType.FullName=="LmpCommon.Message.Data.Vessel.VesselResourceMsgData");
        var header=capture.Body.Instructions.Single(i=>i.OpCode==OpCodes.Stfld && i.Operand is FieldReference f && f.Name=="VesselId" && f.Resolve().DeclaringType.FullName=="LmpCommon.Message.Data.Vessel.VesselBaseMsgData");
        var captureEdit=capture.Body.GetILProcessor();var push=Instruction.Create(OpCodes.Ldloc,dto);
        captureEdit.InsertAfter(header,push);var vessel=Instruction.Create(OpCodes.Ldarg_1);captureEdit.InsertAfter(push,vessel);
        captureEdit.InsertAfter(vessel,Instruction.Create(OpCodes.Call,Hook("CaptureResourceClock")));expected.Add(capture.FullName);
        var copy=Target("LmpClient.Systems.VesselResourceSys.VesselResourceQueue","AssignFromMessage");
        var copyEdit=copy.Body.GetILProcessor();
        foreach(var ret in copy.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray())
        {
            ret.OpCode=OpCodes.Ldarg_1;ret.Operand=null;var data=Instruction.Create(OpCodes.Ldarg_2);copyEdit.InsertAfter(ret,data);
            var attach=Instruction.Create(OpCodes.Call,Hook("CopyResourceClock"));copyEdit.InsertAfter(data,attach);copyEdit.InsertAfter(attach,Instruction.Create(OpCodes.Ret));
        }
        expected.Add(copy.FullName);
    }
}
else if (command != "reference") throw new ArgumentException("Unknown patch mode");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
assembly.Write(output);
using var check = AssemblyDefinition.ReadAssembly(output);
var after = check.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToArray();
var changed = after.Where(m => !before.TryGetValue(m.FullName, out var old) || old != Image(m)).Select(m => m.FullName).ToHashSet();
if (after.Length != before.Count || !changed.SetEquals(expected)) throw new IOException("Changes exceed reviewed hook methods");
File.WriteAllText(output + ".continuity-audit.json", JsonSerializer.Serialize(new { inputSha256 = hash, outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))), methodCount = after.Length, changed }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Reviewed " + command + ": " + changed.Count + " method changes; all other method images unchanged.");

static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) { yield return type; foreach (var child in type.NestedTypes.SelectMany(AllTypes)) yield return child; }
static string Image(MethodDefinition method)
{
    if (!method.HasBody) return "no-body";
    var code = method.Body.Instructions;
    string Operand(object? value) => value switch
    {
        null => "", Instruction branch => "@" + code.IndexOf(branch), Instruction[] branches => string.Join(",", branches.Select(b => code.IndexOf(b))),
        VariableDefinition local => "local:" + local.Index, ParameterDefinition parameter => "arg:" + parameter.Index,
        IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    return method.Body.InitLocals + "|" + string.Join(";", method.Body.Variables.Select(v => v.VariableType.FullName)) + "|" +
        string.Join(";", code.Select(i => i.OpCode.Code + ":" + Operand(i.Operand))) + "|" +
        string.Join(";", method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + h.CatchType + ":" + Operand(h.TryStart) + ":" + Operand(h.TryEnd) + ":" + Operand(h.HandlerStart) + ":" + Operand(h.HandlerEnd) + ":" + Operand(h.FilterStart)));
}
