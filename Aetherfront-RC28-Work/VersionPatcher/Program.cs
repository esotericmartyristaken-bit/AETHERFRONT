using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string OldVersion = "kesas-rc27", NewVersion = "kesas-rc28";
const string CoreRelative = "GameData/LunaMultiplayer/Plugins/ApfCore.dll";
const string ClientRelative = "GameData/LunaMultiplayer/Plugins/LmpClient.dll";
const string StartupFailure = "Client startup checks failed; client startup stopped: ";
const string VerifierHash = "215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30";
const string KspHash = "d9e42483f25ee80a9c11d6c1c0a0d29b4ec78c1e08d76c971b71580c9cce51e4";
try
{
    Require(args.Length == 12 && args[0] is "patch" or "review",
        "Usage: VersionPatcher patch|review <immutable-baseline-feed> <signed-archive> <public-key> <native-verifier> <baseline-directory> <frozen-engine> <KSP-Managed> <output-directory> <feed-sha256> <archive-sha256> <engine-sha256>");
    var feedPath = Plain(args[1]); var archivePath = Plain(args[2]); var keyPath = Plain(args[3]);
    var verifier = Plain(args[4]); var baselinePath = Plain(args[5]); var enginePath = Plain(args[6]);
    var managed = Plain(args[7]); var output = Plain(args[8]);
    var feedHash = Hash(feedPath); var archiveHash = Hash(archivePath); var engineHash = Hash(enginePath);
    Require(feedHash == args[9] && archiveHash == args[10] && engineHash == args[11], "Frozen input hash mismatch.");
    Require(Hash(verifier) == VerifierHash, "Native verifier changed; review required.");
    using (var process = new Process())
    {
        process.StartInfo = new ProcessStartInfo(verifier) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var item in new[] { "--verify", "--manifest", feedPath, "--archive", archivePath, "--public-key-xml", keyPath }) process.StartInfo.ArgumentList.Add(item);
        process.Start();
        if (!process.WaitForExit(60000)) { process.Kill(); throw new IOException("Local signature verifier exceeded 60 seconds."); }
        Require(process.ExitCode == 0, "Immutable RC27 signed archive verification failed.");
    }
    using var feed = JsonDocument.Parse(File.ReadAllBytes(feedPath));
    Require(feed.RootElement.GetProperty("version").GetString() == OldVersion && feed.RootElement.GetProperty("files").GetArrayLength() == 464,
        "Expected immutable signed RC27 inventory of exactly 464 files.");
    Require(feed.RootElement.GetProperty("sha256").GetString() == archiveHash && feed.RootElement.GetProperty("sizeBytes").GetInt64() == new FileInfo(archivePath).Length,
        "Signed archive identity/size mismatch.");
    ValidateInventory(baselinePath, archivePath, feed.RootElement.GetProperty("files"));
    var corePath = Child(baselinePath, CoreRelative); var clientPath = Child(baselinePath, ClientRelative);
    using var resolver = new DefaultAssemblyResolver();
    foreach (var directory in new[] { Path.GetDirectoryName(corePath)!, Path.GetDirectoryName(enginePath)!, managed }) resolver.AddSearchDirectory(directory);
    var reader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };
    using var engine = AssemblyDefinition.ReadAssembly(enginePath, reader);
    using (var oldEngine = AssemblyDefinition.ReadAssembly(Child(baselinePath, "GameData/LunaMultiplayer/Plugins/KesasInstall.dll"), reader))
    {
        Require(engine.Name.FullName == oldEngine.Name.FullName && engine.Name.Name == "KesasInstall", "Frozen Engine identity changed.");
        var publicMethods = Methods(engine).Where(IsPublicApi).Select(x => x.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var method in Methods(oldEngine).Where(IsPublicApi)) Require(publicMethods.Contains(method.FullName), "Engine removed baseline public API: " + method.FullName);
        foreach (var reference in engine.MainModule.AssemblyReferences.Where(x => x.Name is "System.IO.Compression" or "System.IO.Compression.FileSystem"))
            Require(reference.Version.ToString() == "4.0.0.0", "Engine compression reference changed.");
    }
    var restore = engine.MainModule.GetType("KesasInstall.PackageCasing")?.Methods.SingleOrDefault(x => x.Name == "RestoreAtStartup")
        ?? throw new MissingMethodException("Frozen Engine lacks PackageCasing.RestoreAtStartup.");
    Require(restore.IsPublic && restore.IsStatic && !restore.HasGenericParameters && restore.ReturnType.FullName == "System.Void" &&
        restore.Parameters.Count == 2 && restore.Parameters.All(x => x.ParameterType.FullName == "System.String"), "RestoreAtStartup API must be public static void (string, string).");
    var kspPath = Plain(Path.Combine(managed, "Assembly-CSharp.dll"));
    Require(Hash(kspPath) == KspHash, "Actual KSP assembly differs from the reviewed 1.12.5 startup-order baseline.");
    using var ksp = AssemblyDefinition.ReadAssembly(kspPath, reader);
    var startupProof = StartupProof(ksp, kspPath, KspHash);
    using var baseline = AssemblyDefinition.ReadAssembly(clientPath, reader);
    var startup = baseline.MainModule.GetType("LmpClient.Apf.CanonicalStartup");
    var allow = startup.Methods.Single(x => x.Name == "AllowAwake");
    var getter = baseline.MainModule.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(x => x.Name == "get_ClientVersion");
    var field = baseline.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(x => x.Name == "Value");
    var before = Methods(baseline).ToDictionary(x => x.FullName, MethodImage, StringComparer.Ordinal);
    var fieldsBefore = Fields(baseline).ToDictionary(x => x.FullName, FieldImage, StringComparer.Ordinal);
    CheckAwake(baseline);
    Require(field.IsLiteral && Equals(field.Constant, OldVersion), "Expected RC27 build field.");
    var literal = getter.Body.Instructions.Single(x => x.OpCode == OpCodes.Ldstr);
    Require(Equals(literal.Operand, OldVersion), "Expected RC27 version getter.");
    Require(before.Count == 6327, "Expected the authentic 6327-method RC27 client.");
    CheckRecoveryHook(baseline);
    // RC27 already calls the version getter before RestoreAtStartup. Updating
    // that getter supplies RC28 without reinjecting or changing AllowAwake.
    field.Constant = NewVersion; literal.Operand = NewVersion;
    byte[] expected;
    using (var buffer = new MemoryStream()) { baseline.Write(buffer); expected = buffer.ToArray(); }
    byte[] actual;
    if (args[0] == "patch") actual = expected;
    else
    {
        Require(Directory.Exists(output) && Directory.GetFiles(output).Select(Path.GetFileName).Order().SequenceEqual(new[] { "ApfCore.dll", "LmpClient.dll", "audit.json" }.Order()) && !Directory.GetDirectories(output).Any(), "Unexpected Code output inventory.");
        actual = File.ReadAllBytes(Plain(Path.Combine(output, "LmpClient.dll")));
        Require(actual.SequenceEqual(expected), "Client differs from the deterministic RC28 graft.");
        Require(File.ReadAllBytes(Plain(Path.Combine(output, "ApfCore.dll"))).SequenceEqual(File.ReadAllBytes(corePath)), "Core must remain byte-identical to signed RC27.");
    }
    // Independently inspect output, then undo only the version getter and
    // build field changes. Every resulting method/field image must match the
    // signed baseline, including the existing recovery call and exception ranges.
    using var written = AssemblyDefinition.ReadAssembly(new MemoryStream(actual), reader);
    var after = Methods(written).ToDictionary(x => x.FullName, MethodImage, StringComparer.Ordinal);
    Require(before.Keys.Order().SequenceEqual(after.Keys.Order()), "Client method inventory changed.");
    var changed = before.Keys.Where(x => before[x] != after[x]).Order().ToArray();
    Require(changed.SequenceEqual(new[] { getter.FullName }), "Only get_ClientVersion may change; the existing AllowAwake hook must remain exact.");
    var changedFields = Fields(written).Where(x => !fieldsBefore.TryGetValue(x.FullName, out var image) || FieldImage(x) != image).Select(x => x.FullName).Order().ToArray();
    Require(Fields(written).Count() == fieldsBefore.Count && changedFields.SequenceEqual(new[] { field.FullName }), "Only the build-version field may change.");
    CheckAwake(written);
    CheckRecoveryHook(written);
    Require(MethodImage(written.MainModule.GetType(startup.FullName).Methods.Single(x => x.Name == "AllowAwake")) == before[allow.FullName],
        "Existing RC27 AllowAwake recovery hook changed.");
    var writtenGetter = written.MainModule.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(x => x.Name == "get_ClientVersion");
    var writtenLiteral = writtenGetter.Body.Instructions.Single(x => x.OpCode == OpCodes.Ldstr);
    Require(Equals(writtenLiteral.Operand, NewVersion), "Output version getter mismatch."); writtenLiteral.Operand = OldVersion;
    var writtenField = written.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(x => x.Name == "Value");
    Require(Equals(writtenField.Constant, NewVersion), "Output build field mismatch."); writtenField.Constant = OldVersion;
    foreach (var method in Methods(written)) Require(MethodImage(method) == before[method.FullName], "Reversed method differs from signed RC27: " + method.FullName);
    foreach (var item in Fields(written)) Require(FieldImage(item) == fieldsBefore[item.FullName], "Reversed field differs from signed RC27: " + item.FullName);
    using var pristine = AssemblyDefinition.ReadAssembly(clientPath, reader);
    Require(StructureImage(written) == StructureImage(pristine), "Client type/member/assembly metadata changed outside the approved field.");
    using var core = AssemblyDefinition.ReadAssembly(corePath, reader);
    var audit = new {
        schemaVersion = 1, version = NewVersion, baselineVersion = OldVersion, baselineFeedSha256 = feedHash, baselineArchiveSha256 = archiveHash,
        signedBaselineFiles = 464, baselineClientSha256 = Hash(clientPath), clientSha256 = BytesHash(actual), engineSha256 = engineHash,
        baselineCoreSha256 = Hash(corePath), coreSha256 = Hash(corePath), coreChangedMethods = Array.Empty<string>(), coreByteIdentical = true,
        changedField = field.FullName, changedMethods = changed, preservedMethods = before.Count - changed.Length, preservedCoreMethods = Methods(core).Count(),
        methodImages = changed.Select(x => new { method = x, beforeSha256 = TextHash(before[x]), afterSha256 = TextHash(after[x]) }).ToArray(),
        allOtherMethodsUnchanged = true, allOtherFieldsUnchanged = true, reversedClientImagesMatchBaseline = true, startupTryCatchPreserved = true,
        allowAwakeUnchanged = true, existingRestoreAtStartupRetained = true, startupVersionSuppliedByGetter = NewVersion,
        hook = "InstallAddonGuard; UrlDir.ApplicationRootPath; ApfClientSettings.ClientVersion; PackageCasing.RestoreAtStartup; owner assignment",
        startupOrdering = startupProof, finalReleaseEligible = true, productionWrites = false
    };
    var auditText = JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    Require(Hash(feedPath) == feedHash && Hash(archivePath) == archiveHash && Hash(enginePath) == engineHash, "Frozen input changed during graft/review.");
    if (args[0] == "patch")
    {
        Require(!Directory.Exists(output) && !File.Exists(output), "Immutable Code output already exists."); Directory.CreateDirectory(output);
        WriteNew(Path.Combine(output, "LmpClient.dll"), actual);
        WriteNew(Path.Combine(output, "ApfCore.dll"), File.ReadAllBytes(corePath));
        WriteNew(Path.Combine(output, "audit.json"), System.Text.Encoding.UTF8.GetBytes(auditText));
    }
    else Require(File.ReadAllText(Plain(Path.Combine(output, "audit.json"))) == auditText, "Independent review audit differs from recorded evidence.");
    Console.WriteLine($"PASS {args[0]}: 1 version getter, 1 build field; {before.Count - changed.Length} client methods preserved; existing AllowAwake hook unchanged; core byte-identical; actual KSP startup ordering verified.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static string Plain(string path)
{
    var full = Path.GetFullPath(path);
    for (var cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
        if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Refusing reparse point: " + cursor);
    return full;
}
static string Child(string root, string relative)
{
    Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) && !relative.Contains(':') &&
        relative.Replace('\\', '/').Split('/').All(x => x.Length > 0 && x is not "." and not ".."), "Unsafe signed relative path.");
    var full = Plain(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    Require(full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Signed path escapes baseline.");
    return full;
}
static string Hash(string path) { using var input = File.OpenRead(Plain(path)); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
static string BytesHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static string TextHash(string value) => BytesHash(System.Text.Encoding.UTF8.GetBytes(value));
static void WriteNew(string path, byte[] bytes) { using var output = new FileStream(Plain(path), FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true); }
static IEnumerable<string> TreeFiles(string directory, int depth = 0)
{
    Require(depth < 64, "Baseline directory nesting exceeds limit.");
    foreach (var entry in Directory.EnumerateFileSystemEntries(Plain(directory)))
    {
        Plain(entry);
        if (Directory.Exists(entry)) { foreach (var file in TreeFiles(entry, depth + 1)) yield return file; }
        else yield return entry;
    }
}
static void ValidateInventory(string baseline, string archive, JsonElement files)
{
    var records = files.EnumerateArray().ToDictionary(x => x.GetProperty("path").GetString()!, StringComparer.OrdinalIgnoreCase);
    Require(records.Count == 464 && TreeFiles(baseline).Take(465).Count() == 464, "Baseline must have exactly 464 files.");
    using var zip = ZipFile.OpenRead(archive);
    var entries = zip.Entries.Where(x => !x.FullName.EndsWith('/')).ToDictionary(x => x.FullName, StringComparer.OrdinalIgnoreCase);
    Require(entries.Count == 464 && entries.Keys.Order(StringComparer.Ordinal).SequenceEqual(records.Keys.Order(StringComparer.Ordinal)), "Signed ZIP inventory differs from feed.");
    foreach (var (relative, record) in records)
    {
        var path = Child(baseline, relative); var expectedHash = record.GetProperty("sha256").GetString(); var size = record.GetProperty("sizeBytes").GetInt64();
        Require(new FileInfo(path).Length == size && Hash(path) == expectedHash, "Extracted baseline differs from signed feed: " + relative);
        var entry = entries[relative]; using var stream = entry.Open();
        Require(entry.Length == size && Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() == expectedHash, "Baseline ZIP entry differs from signed feed: " + relative);
    }
}
static bool IsCall(Instruction instruction, string type, string name) => instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name;
static bool InRange(MethodDefinition method, Instruction instruction, Instruction start, Instruction? end) =>
    method.Body.Instructions.IndexOf(instruction) >= method.Body.Instructions.IndexOf(start) && (end == null || method.Body.Instructions.IndexOf(instruction) < method.Body.Instructions.IndexOf(end));
static ExceptionHandler GuardHandler(MethodDefinition method, Instruction guard) => method.Body.ExceptionHandlers.Single(x =>
    x.HandlerType == ExceptionHandlerType.Catch && x.CatchType?.FullName == "System.Exception" && InRange(method, guard, x.TryStart, x.TryEnd));
static void CheckAwake(AssemblyDefinition client)
{
    var main = client.MainModule.GetType("LmpClient.MainSystem");
    var addon = main.CustomAttributes.Single(x => x.AttributeType.FullName == "KSPAddon");
    Require(Convert.ToInt32(addon.ConstructorArguments[0].Value, CultureInfo.InvariantCulture) == -2 && Equals(addon.ConstructorArguments[1].Value, true), "MainSystem must remain Instantly, once.");
    var awake = main.Methods.Single(x => x.Name == "Awake").Body.Instructions;
    Require(awake[0].OpCode == OpCodes.Ldarg_0 && IsCall(awake[1], "LmpClient.Apf.CanonicalStartup", "AllowAwake"), "Canonical AllowAwake must remain the first Awake check.");
}
static void CheckRecoveryHook(AssemblyDefinition client)
{
    var startup = client.MainModule.GetType("LmpClient.Apf.CanonicalStartup");
    var allow = startup.Methods.Single(x => x.Name == "AllowAwake");
    var guard = allow.Body.Instructions.Single(x => IsCall(x, startup.FullName, "InstallAddonGuard"));
    var recovery = new[] { guard.Next, guard.Next?.Next, guard.Next?.Next?.Next };
    Require(recovery.All(x => x != null) && IsCall(recovery[0]!, "UrlDir", "get_ApplicationRootPath") &&
        IsCall(recovery[1]!, "LmpClient.Apf.ApfClientSettings", "get_ClientVersion") &&
        IsCall(recovery[2]!, "KesasInstall.PackageCasing", "RestoreAtStartup"), "Expected the exact existing RC27 startup recovery sequence.");
    Require(allow.Body.Instructions.Count(x => IsCall(x, "KesasInstall.PackageCasing", "RestoreAtStartup")) == 1,
        "Startup must retain exactly one RestoreAtStartup call.");
    var handler = GuardHandler(allow, guard);
    foreach (var instruction in recovery) Require(InRange(allow, instruction!, handler.TryStart, handler.TryEnd), "Startup recovery is outside the existing try/catch.");
    var owner = allow.Body.Instructions.Single(x => x.OpCode == OpCodes.Stsfld && x.Operand is FieldReference f && f.DeclaringType.FullName == startup.FullName && f.Name == "owner");
    Require(allow.Body.Instructions.IndexOf(recovery[2]!) < allow.Body.Instructions.IndexOf(owner), "Recovery must finish before startup ownership is published.");
    var failure = allow.Body.Instructions.Single(x => x.OpCode == OpCodes.Ldstr && Equals(x.Operand, StartupFailure));
    Require(InRange(allow, failure, handler.HandlerStart, handler.HandlerEnd) &&
        allow.Body.Instructions.Any(x => InRange(allow, x, handler.HandlerStart, handler.HandlerEnd) && IsCall(x, startup.FullName, "Reject")),
        "Existing startup catch must reject recovery failure.");
    foreach (var instruction in allow.Body.Instructions)
        Require(!(instruction.Operand is Instruction target && recovery.Contains(target)) &&
            !(instruction.Operand is Instruction[] targets && targets.Any(x => recovery.Contains(x))), "A branch enters the existing recovery sequence.");
}
static object StartupProof(AssemblyDefinition ksp, string path, string hash)
{
    var database = ksp.MainModule.GetType("GameDatabase");
    var iterator = database.NestedTypes.Single(x => x.Name.Contains("<LoadObjects>"));
    var move = iterator.Methods.Single(x => x.Name == "MoveNext"); var instructions = move.Body.Instructions;
    var roots = instructions.Where(x => x.OpCode == OpCodes.Stfld && x.Operand is FieldReference f && f.DeclaringType.FullName == "GameDatabase" && f.Name == "_root").ToArray();
    var addons = instructions.Single(x => x.Operand is MethodReference m && m.DeclaringType.FullName == "AddonLoader" && m.Name == "StartAddons");
    Require(roots.Length == 2 && roots[0].Offset == 0x0081 && addons.Offset == 0x079e && roots[1].Offset == 0x07c7 &&
        addons.Previous.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(addons.Previous.Operand, CultureInfo.InvariantCulture) == -2, "Actual KSP assembly/addon/asset tree ordering changed.");
    Require(instructions.Any(x => x.Offset == 0x07c2 && x.OpCode == OpCodes.Newobj && x.Operand is MethodReference m && m.DeclaringType.FullName == "UrlDir"), "Final asset tree is not rebuilt after Instantly addons.");
    var modelEnumeration = instructions.Single(x => x.Offset == 0x08aa);
    Require(modelEnumeration.Operand is MethodReference model && model.DeclaringType.FullName == "UrlDir" && model.Name == "GetFiles" && modelEnumeration.Previous.OpCode == OpCodes.Ldc_I4_3, "Actual model enumeration proof changed.");
    var startAddon = ksp.MainModule.GetType("AddonLoader").Methods.Single(x => x.Name == "StartAddon");
    Require(startAddon.Body.Instructions.Any(x => x.Offset == 0x0098 && x.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.GameObject" && m.Name == "AddComponent"), "Addon creation proof changed.");
    return new { assemblyPath = path, assemblySha256 = hash, method = move.FullName, methodImageSha256 = TextHash(MethodImage(move)),
        firstAssemblyTreeStore = "IL_0081", instantlyAddonsCall = "IL_079e", addonComponentCreation = "AddonLoader.StartAddon IL_0098",
        freshAssetTreeConstructor = "IL_07c2", freshAssetTreeStore = "IL_07c7", modelEnumeration = "IL_08aa",
        synchronousAwakeRequired = true, initialTreeExists = true, assetTreeRebuiltAfterAwake = true, inMemoryAssetUrlRepairRequired = false };
}
static IEnumerable<TypeDefinition> Types(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Types));
static IEnumerable<TypeDefinition> AllTypes(AssemblyDefinition assembly) => assembly.MainModule.Types.SelectMany(Types);
static IEnumerable<MethodDefinition> Methods(AssemblyDefinition assembly) => AllTypes(assembly).SelectMany(x => x.Methods);
static IEnumerable<FieldDefinition> Fields(AssemblyDefinition assembly) => AllTypes(assembly).SelectMany(x => x.Fields);
static bool IsVisibleType(TypeDefinition type) => type.DeclaringType == null ? type.IsPublic :
    (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) && IsVisibleType(type.DeclaringType);
static bool IsPublicApi(MethodDefinition method) =>
    (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly) && IsVisibleType(method.DeclaringType);
static string FieldImage(FieldDefinition field) => JsonSerializer.Serialize(new { field.FullName, field.Attributes, field.HasConstant, field.Constant, initialValue = Convert.ToHexString(field.InitialValue), field.Offset, custom = Attributes(field.CustomAttributes) });
static string Attributes(IEnumerable<CustomAttribute> attributes) => string.Join("|", attributes.Select(x => x.Constructor.FullName + ":" + Convert.ToHexString(x.GetBlob())));
static string StructureImage(AssemblyDefinition assembly) => JsonSerializer.Serialize(new {
    identity = assembly.Name.FullName, assemblyCustom = Attributes(assembly.CustomAttributes), moduleCustom = Attributes(assembly.MainModule.CustomAttributes),
    references = assembly.MainModule.AssemblyReferences.Select(x => x.FullName).Order().ToArray(),
    types = AllTypes(assembly).Select(t => new { t.FullName, t.Attributes, baseType = t.BaseType?.FullName, t.PackingSize, t.ClassSize,
        custom = Attributes(t.CustomAttributes), interfaces = t.Interfaces.Select(x => x.InterfaceType.FullName).ToArray(),
        fields = t.Fields.Select(FieldImage).ToArray(),
        methods = t.Methods.Select(m => new { m.FullName, m.Attributes, m.ImplAttributes, custom = Attributes(m.CustomAttributes),
            returnCustom = Attributes(m.MethodReturnType.CustomAttributes), parameters = m.Parameters.Select(p => new { p.Name, p.Attributes, p.HasConstant, p.Constant, custom = Attributes(p.CustomAttributes) }).ToArray() }).ToArray(),
        properties = t.Properties.Select(p => new { p.FullName, p.Attributes, get = p.GetMethod?.FullName, set = p.SetMethod?.FullName, custom = Attributes(p.CustomAttributes) }).ToArray(),
        events = t.Events.Select(e => new { e.FullName, e.Attributes, add = e.AddMethod?.FullName, remove = e.RemoveMethod?.FullName, custom = Attributes(e.CustomAttributes) }).ToArray()
    }).ToArray()
});
static string MethodImage(MethodDefinition method)
{
    if (!method.HasBody) return method.FullName + "|" + method.Attributes + "|" + method.ImplAttributes;
    var body = method.Body;
    string Operand(object? value) => value switch {
        null => "", Instruction instruction => "instruction:" + body.Instructions.IndexOf(instruction),
        Instruction[] instructions => string.Join(",", instructions.Select(x => body.Instructions.IndexOf(x))),
        VariableDefinition variable => "local:" + variable.Index, ParameterDefinition parameter => "parameter:" + parameter.Index,
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    return JsonSerializer.Serialize(new { method.FullName, method.Attributes, method.ImplAttributes, body.InitLocals, body.MaxStackSize,
        locals = body.Variables.Select(x => x.VariableType.FullName).ToArray(),
        instructions = body.Instructions.Select(x => new { code = x.OpCode.Code.ToString(), operand = Operand(x.Operand) }).ToArray(),
        handlers = body.ExceptionHandlers.Select(x => new { x.HandlerType, catchType = x.CatchType?.FullName, start = Operand(x.TryStart), end = Operand(x.TryEnd),
            handlerStart = Operand(x.HandlerStart), handlerEnd = Operand(x.HandlerEnd), filter = Operand(x.FilterStart) }).ToArray()
    });
}
