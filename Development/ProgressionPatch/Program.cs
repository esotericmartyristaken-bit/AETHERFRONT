using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length == 4 && args[0] == "engine-review33")
{
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))) != "ED61774722CDD37505472568999B3E889CC25E527E2A25F8A4FFE2C2CEFD3D0A") throw new IOException("Unreviewed prior engine");
    using var prior = AssemblyDefinition.ReadAssembly(args[1]);
    using var next = AssemblyDefinition.ReadAssembly(args[2]);
    if (prior.Name.FullName != next.Name.FullName) throw new IOException("Engine identity changed");
    var oldMethods = prior.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
    var newMethods = next.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToArray();
    if (newMethods.Length != oldMethods.Count) throw new IOException("Engine method count changed");
    var engineChanges = newMethods.Where(m => oldMethods[m.FullName] != Image(m)).Select(m => m.FullName).ToArray();
    foreach (var method in newMethods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
            instruction.Operand = ((string)instruction.Operand).Replace("kesas-rc33", "kesas-rc31").Replace("LegacySettingsRecovery-rc33", "LegacySettingsRecovery-rc31").Replace("completed RC33 release", "completed RC31 release");
    if (newMethods.Any(m => oldMethods[m.FullName] != Image(m))) throw new IOException("Engine changes exceed recovery version/marker retargeting");
    if (engineChanges.Length == 0 || engineChanges.Any(n => !n.Contains("KesasInstall.LegacySettingsRecovery"))) throw new IOException("Unexpected changed engine methods");
    File.WriteAllText(args[3], JsonSerializer.Serialize(new { version = "kesas-rc33", oldSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))), newSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))), methodCount = oldMethods.Count, engineChanges, allOtherMethodsUnchanged = true }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Engine retarget reviewed; all other method bodies unchanged.");
    return;
}

if (args.Length == 5 && args[0] == "release33")
{
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    resolver.AddSearchDirectory(Path.GetFullPath(args[4]));
    using var assembly = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
    var before = assembly.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
    var versionGetter = assembly.MainModule.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m => m.Name == "get_ClientVersion");
    var value = assembly.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
    var literal = versionGetter.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
    if (!Equals(value.Constant, "kesas-rc31") || !Equals(literal.Operand, "kesas-rc31")) throw new IOException("Expected reviewed RC31-based candidate");
    value.Constant = "kesas-rc33"; literal.Operand = "kesas-rc33";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    assembly.Write(args[2]);
    using var verification = AssemblyDefinition.ReadAssembly(args[2]);
    var versionChanges = verification.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).Where(m => before[m.FullName] != Image(m)).Select(m => m.FullName).ToArray();
    if (versionChanges.Length != 1 || versionChanges[0] != versionGetter.FullName) throw new IOException("Unexpected release-version method changes");
    File.WriteAllText(args[3], JsonSerializer.Serialize(new { version = "kesas-rc33", inputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))), outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))), changed = versionChanges }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("RC33 version graft: one getter and one build constant; existing startup authorization retained.");
    return;
}

if (args.Length == 5 && args[0] == "client-hooks")
{
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))) != "2781F634E5B17A15B9590DA2093F56297DFA12711897DD43A8275D2950CE47BE") throw new IOException("Unreviewed client baseline");
    if (Path.GetFullPath(args[1]).Equals(Path.GetFullPath(args[3]), StringComparison.OrdinalIgnoreCase)) throw new IOException("Never replace baseline");
    using var clientResolver = new DefaultAssemblyResolver();
    clientResolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    clientResolver.AddSearchDirectory(Path.GetFullPath(args[4]));
    using var client = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = clientResolver });
    using var helper = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters { AssemblyResolver = clientResolver });
    if (helper.Name.Name != "AetherfrontPersonalClient") throw new IOException("Unexpected client helper");
    var beforeClient = client.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
    var personal = helper.MainModule.GetType("Aetherfront.Personal.Client.PersonalClient");
    var targets = new[] { ("LmpClient.Aetherfront.ProgressionMirror", "Apply", "Apply"), ("LmpClient.Aetherfront.ProgressionMirror", "CanRequest", "CanRequest"), ("LmpClient.Aetherfront.AetherfrontClientSystem", "RequestStockTechnology", "RequestTechnology") };
    var expected = new HashSet<string>();
    foreach (var (type, name, replacement) in targets)
    {
        var method = client.MainModule.GetType(type).Methods.Single(m => m.Name == name);
        var hook = personal.Methods.Single(m => m.Name == replacement);
        if (method.Parameters.Count != hook.Parameters.Count || method.ReturnType.FullName != hook.ReturnType.FullName) throw new IOException("Hook signature mismatch");
        method.Body = new MethodBody(method); var edit = method.Body.GetILProcessor();
        foreach (var parameter in method.Parameters) edit.Emit(OpCodes.Ldarg, parameter);
        edit.Emit(OpCodes.Call, client.MainModule.ImportReference(hook)); edit.Emit(OpCodes.Ret);
        expected.Add(method.FullName);
    }
    var window = client.MainModule.GetType("LmpClient.Windows.Aetherfront.AetherfrontFactionWindow");
    MethodReference UiHook(string name) => client.MainModule.ImportReference(personal.Methods.Single(m => m.Name == name));
    void ReplaceLabel(string methodName, string marker, string hookName)
    {
        var method = window.Methods.Single(m => m.Name == methodName);
        var markerInstruction = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, marker));
        var label = method.Body.Instructions.Skip(method.Body.Instructions.IndexOf(markerInstruction)).First(i => i.Operand is MethodReference m && m.DeclaringType.FullName == window.FullName && m.Name == "Label");
        if (((MethodReference)label.Operand).HasThis || personal.Methods.Single(m => m.Name == hookName).Parameters.Count != 1) throw new IOException("Unexpected label hook signature");
        label.OpCode = OpCodes.Call; label.Operand = UiHook(hookName); expected.Add(method.FullName);
    }
    ReplaceLabel("DrawHome", " science  |  ", "DrawSummary");
    ReplaceLabel("DrawOwnFaction", "Science: ", "DrawGroupExplanation");
    ReplaceLabel("DrawTradeComposer", "Available: ", "DrawTradingSummary");
    ReplaceLabel("DrawWindowContent", "Join a faction to research technology.", "DrawResearchNoFaction");
    var researchUi = window.Methods.Single(m => m.Name == "DrawResearch");
    researchUi.Body = new MethodBody(researchUi); var researchEdit = researchUi.Body.GetILProcessor();
    researchEdit.Emit(OpCodes.Ldarg_1); researchEdit.Emit(OpCodes.Ldarg_2); researchEdit.Emit(OpCodes.Call, UiHook("DrawResearch")); researchEdit.Emit(OpCodes.Ret); expected.Add(researchUi.FullName);
    var modeUi = window.Methods.Single(m => m.Name == "GameModeDescription");
    modeUi.Body = new MethodBody(modeUi); var modeEdit = modeUi.Body.GetILProcessor(); modeEdit.Emit(OpCodes.Call, UiHook("ModeDescription")); modeEdit.Emit(OpCodes.Ret); expected.Add(modeUi.FullName);
    var tradeUi = window.Methods.Single(m => m.Name == "DrawFactionOperations");
    tradeUi.Body = new MethodBody(tradeUi); var tradeEdit = tradeUi.Body.GetILProcessor();
    tradeEdit.Emit(OpCodes.Ldarg_0); tradeEdit.Emit(OpCodes.Ldarg_1); tradeEdit.Emit(OpCodes.Ldarg_2); tradeEdit.Emit(OpCodes.Call, window.Methods.Single(m => m.Name == "DrawTradeDesk")); tradeEdit.Emit(OpCodes.Ret); expected.Add(tradeUi.FullName);
    var contentUi = window.Methods.Single(m => m.Name == "DrawWindowContent");
    foreach (var instruction in contentUi.Body.Instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.GUILayout" && m.Name == "Toolbar" && m.Parameters.Count == 3))
        instruction.Operand = UiHook("ProgressToolbar");
    var validateOffer = client.MainModule.GetType("LmpClient.Aetherfront.AetherfrontUiPolicy").Methods.Single(m => m.Name == "ValidateOffer");
    foreach (var instruction in validateOffer.Body.Instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Aetherfront.Core.FactionProgression" && m.Name is "get_FundsMicro" or "get_Science"))
    {
        var methodName = ((MethodReference)instruction.Operand).Name;
        instruction.OpCode = OpCodes.Call; instruction.Operand = UiHook(methodName == "get_FundsMicro" ? "PersonalFunds" : "WholeScience");
    }
    expected.Add(validateOffer.FullName);
    var confirmation = window.Methods.Single(m => m.Name == "Confirm");
    var confirmFirst = confirmation.Body.Instructions[0]; var confirmEdit = confirmation.Body.GetILProcessor();
    confirmEdit.InsertBefore(confirmFirst, Instruction.Create(OpCodes.Ldarg_1)); confirmEdit.InsertBefore(confirmFirst, Instruction.Create(OpCodes.Ldarg_2));
    confirmEdit.InsertBefore(confirmFirst, Instruction.Create(OpCodes.Call, UiHook("ConfirmPersonalPayment"))); confirmEdit.InsertBefore(confirmFirst, Instruction.Create(OpCodes.Starg, confirmation.Parameters[0])); expected.Add(confirmation.FullName);
    foreach (var method in window.Methods.Concat(new[] { validateOffer }))
        if (method.HasBody)
            foreach (var instruction in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
            {
                string? replacement = (string)instruction.Operand switch
                {
                    "Join or create a faction to share vessels and research." => "Join or create a faction to share vessels and permissions. Your progression is personal.",
                    "Join a faction to trade and borrow funds." => "Join a faction to trade shared vessels.",
                    "Add what each faction gives. Leave one side empty to make a gift." => "Use your personal funds or science, plus vessels your faction can trade. An empty side is a gift.",
                    "Your faction cannot afford its side of this offer. Reduce the funds or science amount." => "Your personal tradable balance cannot cover this offer. Shared and starting science are for research only.",
                    _ => null
                };
                if (replacement != null) { instruction.Operand = replacement; expected.Add(method.FullName); }
            }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!); client.Write(args[3]);
    using var verification = AssemblyDefinition.ReadAssembly(args[3]);
    var changedClient = verification.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).Where(m => beforeClient[m.FullName] != Image(m)).Select(m => m.FullName).ToHashSet();
    if (!changedClient.SetEquals(expected)) throw new IOException("Unexpected client method changes");
    File.WriteAllText(args[3] + ".audit.json", JsonSerializer.Serialize(new { baselineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))), outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[3]))), existingMethods = beforeClient.Count, changed = changedClient }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Prepared client with " + changedClient.Count + " reviewed method changes.");
    return;
}
if ((args.Length == 3 && args[0] == "server-reference") || (args.Length == 4 && args[0] == "server-hooks"))
{
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))) != "32E7BEEA41224B6368127EE346DD2F3F0069D66ED15682C78526A621871E845E") throw new IOException("Unreviewed server baseline");
    var serverOutput = args[^1];
    if (Path.GetFullPath(args[1]).Equals(Path.GetFullPath(serverOutput), StringComparison.OrdinalIgnoreCase)) throw new IOException("Never replace baseline");
    using var server = AssemblyDefinition.ReadAssembly(args[1]);
    var beforeServer = server.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
    var attrType = new TypeReference("System.Runtime.CompilerServices", "InternalsVisibleToAttribute", server.MainModule, server.MainModule.AssemblyReferences.Single(r => r.Name == "System.Runtime"));
    var ctor = new MethodReference(".ctor", server.MainModule.TypeSystem.Void, attrType) { HasThis = true };
    ctor.Parameters.Add(new ParameterDefinition(server.MainModule.TypeSystem.String));
    var attribute = new CustomAttribute(ctor);
    attribute.ConstructorArguments.Add(new CustomAttributeArgument(server.MainModule.TypeSystem.String, "AetherfrontPersonalServer"));
    server.CustomAttributes.Add(attribute);
    var expectedChanges = new HashSet<string>();
    if (args[0] == "server-hooks")
    {
        using var helper = AssemblyDefinition.ReadAssembly(args[2]);
        if (helper.Name.Name != "AetherfrontPersonalServer") throw new IOException("Unexpected helper identity");
        var personal = helper.MainModule.GetType("Aetherfront.Personal.Server.PersonalServer");
        var reader = server.MainModule.GetType("Server.Message.AetherfrontMsgReader").Methods.Single(m => m.Name == "HandleLocked");
        var first = reader.Body.Instructions[0]; var edit = reader.Body.GetILProcessor();
        edit.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
        edit.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
        edit.InsertBefore(first, Instruction.Create(OpCodes.Call, server.MainModule.ImportReference(personal.Methods.Single(m => m.Name == "TryHandle"))));
        edit.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
        edit.InsertBefore(first, Instruction.Create(OpCodes.Ret));
        expectedChanges.Add(reader.FullName);
        var visibility = server.MainModule.GetType("Server.Aetherfront.AetherfrontVisibility").Methods.Single(m => m.Name == "ForAccount");
        foreach (var visibilityReturn in visibility.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            visibilityReturn.OpCode = OpCodes.Nop; edit = visibility.Body.GetILProcessor();
            var argument = Instruction.Create(OpCodes.Ldarg_1); var call = Instruction.Create(OpCodes.Call, server.MainModule.ImportReference(personal.Methods.Single(m => m.Name == "Filter")));
            edit.InsertAfter(visibilityReturn, argument); edit.InsertAfter(argument, call); edit.InsertAfter(call, Instruction.Create(OpCodes.Ret));
        }
        expectedChanges.Add(visibility.FullName);
        var applyTrade = server.MainModule.GetType("Server.Message.AetherfrontMsgReader").Methods.Single(m => m.Name == "Apply");
        var tradeResult = new VariableDefinition(applyTrade.ReturnType); applyTrade.Body.Variables.Add(tradeResult); applyTrade.Body.InitLocals = true;
        var applyFirst = applyTrade.Body.Instructions[0]; var applyEdit = applyTrade.Body.GetILProcessor();
        applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Ldarg_0)); applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Ldarg_1)); applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Ldarg_2));
        applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Ldloca, tradeResult));
        applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Call, server.MainModule.ImportReference(personal.Methods.Single(m => m.Name == "TryApplyTrade"))));
        applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Brfalse, applyFirst)); applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Ldloc, tradeResult)); applyEdit.InsertBefore(applyFirst, Instruction.Create(OpCodes.Ret));
        expectedChanges.Add(applyTrade.FullName);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(serverOutput))!);
    server.Write(serverOutput);
    using var serverCheck = AssemblyDefinition.ReadAssembly(serverOutput);
    var changedServer = serverCheck.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).Where(m => beforeServer[m.FullName] != Image(m)).Select(m => m.FullName).ToHashSet();
    if (!changedServer.SetEquals(expectedChanges)) throw new IOException("Unexpected server method changes");
    File.WriteAllText(serverOutput + ".audit.json", JsonSerializer.Serialize(new { baselineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))), outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(serverOutput))), existingMethods = beforeServer.Count, changed = changedServer }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Prepared server with " + changedServer.Count + " reviewed method changes.");
    return;
}
if (args.Length != 4) throw new ArgumentException("ProgressionPatch <frozen-core> <authored-models-assembly> <output-core> <expected-input-sha256>");
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[2]);
if (input.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new IOException("Never patch an input in place.");
if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).Equals(args[3], StringComparison.OrdinalIgnoreCase)) throw new IOException("Unreviewed baseline.");
using var baseline = AssemblyDefinition.ReadAssembly(input);
using var authored = AssemblyDefinition.ReadAssembly(args[1]);
var module = baseline.MainModule;
var state = module.GetType("Aetherfront.Core.AetherfrontState");
if (state == null || state.Properties.Any(p => p.Name == "PersonalProgression")) throw new IOException("Expected unmodified AetherfrontCore baseline.");
var original = module.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
var mapTypes = new Dictionary<string, TypeDefinition>();
var mapMethods = new Dictionary<string, MethodDefinition>();
var mapFields = new Dictionary<string, FieldDefinition>();
var roots = new[] { "PersonalProgressionState", "PlayerProgression", "ScienceSourceCredit", "PersonalTradeSponsor", "PersonalProgression" }
    .Select(n => authored.MainModule.GetType("Aetherfront.Core." + n) ?? throw new IOException("Missing authored type " + n)).ToArray();
var sourceTypes = roots.SelectMany(AllTypes).ToArray();
foreach (var type in sourceTypes)
{
    var copy = new TypeDefinition(type.Namespace, type.Name, type.Attributes, null);
    if (type.DeclaringType == null) module.Types.Add(copy); else mapTypes[type.DeclaringType.FullName].NestedTypes.Add(copy);
    mapTypes.Add(type.FullName, copy);
}
TypeReference ImportType(TypeReference type)
{
    if (type is GenericParameter) return type;
    if (type is GenericInstanceType generic)
    {
        var result = new GenericInstanceType(ImportType(generic.ElementType));
        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(ImportType(argument));
        return result;
    }
    if (type is ArrayType array) return new ArrayType(ImportType(array.ElementType), array.Rank);
    if (type is ByReferenceType reference) return new ByReferenceType(ImportType(reference.ElementType));
    if (mapTypes.TryGetValue(type.FullName, out var mapped)) return mapped;
    if (type.Namespace == "Aetherfront.Core") return module.GetType(type.FullName) ?? throw new IOException("Missing baseline type " + type.FullName);
    return module.ImportReference(type);
}
MethodReference ImportMethod(MethodReference method)
{
    if (method is GenericInstanceMethod generic)
    {
        var result = new GenericInstanceMethod(ImportMethod(generic.ElementMethod));
        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(ImportType(argument));
        return result;
    }
    if (mapMethods.TryGetValue(method.FullName, out var mapped)) return mapped;
    if (method.DeclaringType.FullName == "Aetherfront.Core.AetherfrontState" && method.Name == "get_PersonalProgression")
        return new MethodReference(method.Name, ImportType(method.ReturnType), state) { HasThis = true };
    if (method.DeclaringType.Namespace == "Aetherfront.Core" && !mapTypes.ContainsKey(method.DeclaringType.FullName))
        return module.GetType(method.DeclaringType.FullName).Methods.SingleOrDefault(m => m.FullName == method.FullName) ?? throw new IOException("Missing baseline method " + method.FullName);
    var imported = new MethodReference(method.Name, ImportType(method.ReturnType), ImportType(method.DeclaringType))
        { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
    foreach (var parameter in method.GenericParameters) imported.GenericParameters.Add(new GenericParameter(parameter.Name, imported));
    foreach (var parameter in method.Parameters) imported.Parameters.Add(new ParameterDefinition(ImportType(parameter.ParameterType)));
    return imported;
}
foreach (var type in sourceTypes)
{
    var copy = mapTypes[type.FullName];
    copy.BaseType = ImportType(type.BaseType);
    foreach (var field in type.Fields)
    {
        var added = new FieldDefinition(field.Name, field.Attributes, ImportType(field.FieldType));
        if (field.HasConstant) added.Constant = field.Constant;
        copy.Fields.Add(added); mapFields.Add(field.FullName, added);
    }
    foreach (var method in type.Methods)
    {
        if (method.HasGenericParameters) throw new IOException("Generic authored methods require explicit support.");
        var added = new MethodDefinition(method.Name, method.Attributes, ImportType(method.ReturnType)) { ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention };
        foreach (var parameter in method.Parameters)
        {
            var p = new ParameterDefinition(parameter.Name, parameter.Attributes, ImportType(parameter.ParameterType));
            if (parameter.HasConstant) p.Constant = parameter.Constant;
            added.Parameters.Add(p);
        }
        copy.Methods.Add(added); mapMethods.Add(method.FullName, added);
    }
}
foreach (var type in sourceTypes)
{
    var copy = mapTypes[type.FullName];
    foreach (var property in type.Properties)
        copy.Properties.Add(new PropertyDefinition(property.Name, property.Attributes, ImportType(property.PropertyType))
        { GetMethod = property.GetMethod == null ? null : mapMethods[property.GetMethod.FullName], SetMethod = property.SetMethod == null ? null : mapMethods[property.SetMethod.FullName] });
    foreach (var field in type.Fields)
        foreach (var attribute in field.CustomAttributes.Where(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute"))
        {
            var added = new CustomAttribute(ImportMethod(attribute.Constructor));
            foreach (var argument in attribute.ConstructorArguments) added.ConstructorArguments.Add(new CustomAttributeArgument(ImportType(argument.Type), argument.Value));
            mapFields[field.FullName].CustomAttributes.Add(added);
        }
    foreach (var method in type.Methods)
    {
        if (!method.HasBody) continue;
        var copyMethod = mapMethods[method.FullName];
        for (int i = 0; i < method.Parameters.Count; i++)
            foreach (var attribute in method.Parameters[i].CustomAttributes.Where(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute"))
            {
                var added = new CustomAttribute(ImportMethod(attribute.Constructor));
                foreach (var argument in attribute.ConstructorArguments) added.ConstructorArguments.Add(new CustomAttributeArgument(ImportType(argument.Type), argument.Value));
                copyMethod.Parameters[i].CustomAttributes.Add(added);
            }
        copyMethod.Body = new MethodBody(copyMethod) { InitLocals = method.Body.InitLocals, MaxStackSize = method.Body.MaxStackSize };
        foreach (var variable in method.Body.Variables) copyMethod.Body.Variables.Add(new VariableDefinition(ImportType(variable.VariableType)));
        var instructions = method.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in method.Body.Instructions)
        {
            var added = instructions[instruction]; added.OpCode = instruction.OpCode;
            added.Operand = instruction.Operand switch
            {
                null => null,
                Instruction branch => instructions[branch],
                Instruction[] branches => branches.Select(b => instructions[b]).ToArray(),
                VariableDefinition variable => copyMethod.Body.Variables[variable.Index],
                ParameterDefinition parameter => copyMethod.Parameters[parameter.Index],
                MethodReference called => ImportMethod(called),
                FieldReference field when mapFields.TryGetValue(field.FullName, out var mapped) => mapped,
                FieldReference field => new FieldReference(field.Name, ImportType(field.FieldType), ImportType(field.DeclaringType)),
                TypeReference reference => ImportType(reference),
                _ => instruction.Operand
            };
            copyMethod.Body.Instructions.Add(added);
        }
        foreach (var handler in method.Body.ExceptionHandlers)
            copyMethod.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                CatchType = handler.CatchType == null ? null : ImportType(handler.CatchType),
                TryStart = instructions[handler.TryStart], TryEnd = handler.TryEnd == null ? null : instructions[handler.TryEnd],
                HandlerStart = instructions[handler.HandlerStart], HandlerEnd = handler.HandlerEnd == null ? null : instructions[handler.HandlerEnd],
                FilterStart = handler.FilterStart == null ? null : instructions[handler.FilterStart]
            });
    }
}
var propertyType = mapTypes["Aetherfront.Core.PersonalProgressionState"];
var backing = new FieldDefinition("_personalProgression", FieldAttributes.Private, propertyType); state.Fields.Add(backing);
var getter = new MethodDefinition("get_PersonalProgression", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, propertyType);
var setter = new MethodDefinition("set_PersonalProgression", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, module.TypeSystem.Void);
setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, propertyType));
state.Methods.Add(getter); state.Methods.Add(setter);
var getIL = getter.Body.GetILProcessor(); getIL.Emit(OpCodes.Ldarg_0); getIL.Emit(OpCodes.Ldfld, backing); getIL.Emit(OpCodes.Ret);
var setIL = setter.Body.GetILProcessor(); setIL.Emit(OpCodes.Ldarg_0); setIL.Emit(OpCodes.Ldarg_1); setIL.Emit(OpCodes.Stfld, backing); setIL.Emit(OpCodes.Ret);
state.Properties.Add(new PropertyDefinition("PersonalProgression", PropertyAttributes.None, propertyType) { GetMethod = getter, SetMethod = setter });
var constructor = state.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
var ret = constructor.Body.Instructions.Last();
if (ret.OpCode != OpCodes.Ret || constructor.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret) != 1) throw new IOException("Review constructor control flow.");
var il = constructor.Body.GetILProcessor();
il.InsertBefore(ret, Instruction.Create(OpCodes.Ldarg_0));
il.InsertBefore(ret, Instruction.Create(OpCodes.Newobj, propertyType.Methods.Single(m => m.IsConstructor && !m.IsStatic)));
il.InsertBefore(ret, Instruction.Create(OpCodes.Stfld, backing));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
module.Write(output);
using var check = AssemblyDefinition.ReadAssembly(output);
var after = check.MainModule.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, Image);
var changed = original.Where(pair => !after.TryGetValue(pair.Key, out var body) || body != pair.Value).Select(pair => pair.Key).ToArray();
if (changed.Length != 1 || changed[0] != constructor.FullName) throw new IOException("Unexpected changes to existing methods: " + string.Join(",", changed));
if (check.MainModule.AssemblyReferences.Any(a => a.Name == authored.Name.Name)) throw new IOException("Leaked test-assembly reference.");
File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(new { inputSha256 = args[3], outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))), existingMethods = original.Count, changed, addedTypes = sourceTypes.Select(t => t.FullName), newMethods = after.Count - original.Count }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Patched " + output + "; existing method changes: only state constructor; " + original.Count + " reviewed methods.");

static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(AllTypes));
static string Image(MethodDefinition method) => !method.HasBody ? "" : string.Join("|", method.Body.Instructions.Select(i => i.OpCode + ":" + (i.Operand switch
{
    Instruction branch => "@" + method.Body.Instructions.IndexOf(branch),
    Instruction[] branches => string.Join(",", branches.Select(b => method.Body.Instructions.IndexOf(b))),
    _ => i.Operand?.ToString() ?? ""
}))) + ";" + string.Join("|", method.Body.Variables.Select(v => v.VariableType.FullName));
