using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// This tool never loads or executes a game assembly. The native verifier checks
// the existing signed chain before either patching or reviewing an output.
const string FeedHash = "f249dc0712ad4599ea74d9b213283321024808ead3884f98ec1c9e41e714d22f";
const string CoreHash = "c2357dd0bd15ffff821f020b8b08653b68b48163c345ef0e7554f11f0b168001";
const string ClientHash = "b7b30bd15fd7a55e2b4fa7a0731c086666b080409e1ec18be574ebf3d50eb255";
const string ToolHash = "215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30";
const string CoreRelative = "GameData/LunaMultiplayer/Plugins/ApfCore.dll";
const string ClientRelative = "GameData/LunaMultiplayer/Plugins/LmpClient.dll";
var options = new JsonSerializerOptions { WriteIndented = true };
try
{
    bool clientOnly = args.Length > 0 && args[0] is "patch-client" or "review-client";
    if (args.Length != (clientOnly ? 8 : 9) || args[0] is not ("patch" or "review" or "patch-client" or "review-client"))
        throw new InvalidDataException("Usage: VersionPatcher patch|review <active-feed> <signed-archive> <public-key> <native-update-tool> <baseline-core> <baseline-client> <output-directory> <frozen-engine>; patch-client|review-client omit frozen-engine and are NOT release eligible.");
    string feedPath = Plain(args[1]), archivePath = Plain(args[2]), publicKey = Plain(args[3]), tool = Plain(args[4]);
    string corePath = Plain(args[5]), clientPath = Plain(args[6]), output = Plain(args[7]);
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(Path.GetDirectoryName(corePath));
    resolver.AddSearchDirectory(Path.GetDirectoryName(clientPath));
    var workspace = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(feedPath)))!;
    var managed = Environment.GetEnvironmentVariable("APF_RC26_UNITY_MANAGED") ?? Path.Combine(workspace, "Aetherfront-KSP-Smoke", "KSP_x64_Data", "Managed");
    resolver.AddSearchDirectory(Plain(managed));
    if (!clientOnly) resolver.AddSearchDirectory(Path.GetDirectoryName(Plain(args[8])));
    var reader = new ReaderParameters { AssemblyResolver = resolver };
    Require(Hash(feedPath) == FeedHash, "The active signed RC25 feed is not the reviewed baseline.");
    Require(Hash(tool) == ToolHash, "Native verifier changed; review its source and executable before proceeding.");
    using (var process = new Process())
    {
        process.StartInfo = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var item in new[] { "--verify", "--manifest", feedPath, "--archive", archivePath, "--public-key-xml", publicKey }) process.StartInfo.ArgumentList.Add(item);
        process.Start(); process.WaitForExit();
        Require(process.ExitCode == 0, "Signed RC25 archive verification failed.");
    }
    using var feed = JsonDocument.Parse(File.ReadAllBytes(feedPath));
    Require(feed.RootElement.GetProperty("version").GetString() == "kesas-rc25", "Expected active RC25.");
    Require(feed.RootElement.GetProperty("files").GetArrayLength() == 464, "Reviewed RC25 signed inventory must contain exactly 464 files.");
    using (var zip = ZipFile.OpenRead(archivePath))
    {
        foreach (var item in new[] { (CoreRelative, corePath, CoreHash), (ClientRelative, clientPath, ClientHash) })
        {
            var record = feed.RootElement.GetProperty("files").EnumerateArray().Single(x => x.GetProperty("path").GetString() == item.Item1);
            Require(record.GetProperty("sha256").GetString() == item.Item3 && Hash(item.Item2) == item.Item3, "Baseline DLL does not match the signed RC25 feed: " + item.Item1);
            var entry = zip.Entries.Single(x => x.FullName == item.Item1);
            using var stream = entry.Open();
            Require(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() == item.Item3 && entry.Length == new FileInfo(item.Item2).Length,
                "Baseline DLL does not match the signed ZIP: " + item.Item1);
        }
    }
    byte[] expectedCore = File.ReadAllBytes(corePath);
    string[] coreChanged = []; int preservedCore; string? engineHash = null;
    using (var core = AssemblyDefinition.ReadAssembly(corePath, reader))
    {
        Require(core.MainModule.GetMemberReferences().OfType<MethodReference>().Any(x => x.DeclaringType.FullName == "KesasInstall.RuntimeFiles" && x.Name == "SameFiles"),
            "RC25 core must already contain the RuntimeFiles.SameFiles hook; never reapply historical core grafts.");
        var coreBefore = Methods(core).ToDictionary(x => x.FullName, MethodImage);
        if (!clientOnly)
        {
            var enginePath = Plain(args[8]); engineHash = Hash(enginePath);
            using var engine = AssemblyDefinition.ReadAssembly(enginePath, reader);
            Require(engine.Name.Name == "KesasInstall" && engine.Name.Version.ToString() == "1.0.0.0", "Unexpected frozen Engine identity.");
            var scanFiles = engine.MainModule.GetType("KesasInstall.RuntimeFiles").Methods.Single(x => x.Name == "ScanFiles");
            Require(scanFiles.IsPublic && scanFiles.IsStatic && scanFiles.Parameters.Count == 2 && scanFiles.Parameters.All(x => x.ParameterType.FullName == "System.String") &&
                scanFiles.ReturnType.FullName == "System.Collections.Generic.List`1<ApfCore.ApfFileState>", "Frozen Engine ScanFiles API mismatch.");
            var scanner = core.MainModule.GetType("ApfCore.ApfPackScanner");
            var scan = scanner.Methods.Single(x => x.Name == "Scan");
            var build = scanner.Methods.Single(x => x.Name == "BuildFileState");
            Require(build.Body.Instructions.Count(x => x.Operand is MethodReference method && method.DeclaringType.FullName == "KesasInstall.PreparedPack" && method.Name == "Files") == 1,
                "Global BuildFileState must retain its existing PreparedPack.Files hook.");
            var call = scan.Body.Instructions.Single(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference method && method.FullName == build.FullName);
            var originalScan = scan.Body.Instructions.Select(x => x.OpCode.Code + "|" + Convert.ToString(x.Operand)).ToArray();
            call.Operand = core.MainModule.ImportReference(scanFiles);
            using var buffer = new MemoryStream(); core.Write(buffer); expectedCore = buffer.ToArray();
            using var result = AssemblyDefinition.ReadAssembly(new MemoryStream(expectedCore), reader);
            var coreAfter = Methods(result).ToDictionary(x => x.FullName, MethodImage);
            Require(coreBefore.Keys.Order().SequenceEqual(coreAfter.Keys.Order()), "Core method inventory changed.");
            coreChanged = coreBefore.Keys.Where(x => coreBefore[x] != coreAfter[x]).Order().ToArray();
            Require(coreChanged.SequenceEqual(new[] { scan.FullName }), "Only ApfPackScanner.Scan may change in core.");
            var resultScan = result.MainModule.GetType("ApfCore.ApfPackScanner").Methods.Single(x => x.Name == "Scan");
            var oldImages = scan.Body.Instructions.Select(x => x.OpCode.Code + "|" + Convert.ToString(x.Operand)).ToArray();
            var newImages = resultScan.Body.Instructions.Select(x => x.OpCode.Code + "|" + Convert.ToString(x.Operand)).ToArray();
            Require(oldImages.SequenceEqual(newImages) && originalScan.Length == newImages.Length && originalScan.Zip(newImages).Count(x => x.First != x.Second) == 1,
                "Core scan must change exactly one imported callsite.");
        }
        preservedCore = coreBefore.Count - coreChanged.Length;
    }

    using var baseline = AssemblyDefinition.ReadAssembly(clientPath, reader);
    var before = Methods(baseline).ToDictionary(x => x.FullName, MethodImage);
    var field = baseline.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(x => x.Name == "Value");
    Require(field.IsLiteral && Equals(field.Constant, "kesas-rc25"), "Expected exactly the RC25 build-version constant.");
    var getter = baseline.MainModule.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(x => x.Name == "get_ClientVersion");
    var literals = getter.Body.Instructions.Where(x => x.OpCode == OpCodes.Ldstr).ToArray();
    Require(literals.Length == 1 && Equals(literals[0].Operand, "kesas-rc25"), "Unexpected client-version getter; no UI graft is authorized.");
    field.Constant = "kesas-rc26"; literals[0].Operand = "kesas-rc26";
    byte[] expected;
    using (var buffer = new MemoryStream()) { baseline.Write(buffer); expected = buffer.ToArray(); }
    // Compare every method after a write/read cycle, not just an allowlist count.
    using var written = AssemblyDefinition.ReadAssembly(new MemoryStream(expected), reader);
    var after = Methods(written).ToDictionary(x => x.FullName, MethodImage);
    Require(before.Keys.Order().SequenceEqual(after.Keys.Order()), "Method inventory changed.");
    var changed = before.Keys.Where(x => before[x] != after[x]).Order().ToArray();
    Require(changed.SequenceEqual(new[] { getter.FullName }), "Only get_ClientVersion may change; every other method must be identical.");
    var audit = new Audit(2, "kesas-rc26", "kesas-rc25", FeedHash, Hash(archivePath), CoreHash, ClientHash,
        Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), field.FullName, changed, before.Count - changed.Length,
        Convert.ToHexString(SHA256.HashData(expectedCore)).ToLowerInvariant(), coreChanged, preservedCore, engineHash,
        !clientOnly, true, true, true, "Client: two version constants only. Core: one Scan callsite only when a frozen Engine is supplied. Global BuildFileState and its prepared-receipt hook remain unchanged.");
    var auditText = JsonSerializer.Serialize(audit, options) + "\n";
    if (args[0] is "patch" or "patch-client")
    {
        Require(!Directory.Exists(output) && !File.Exists(output), "Immutable version output already exists; use a new attempt.");
        Directory.CreateDirectory(output);
        using (var file = new FileStream(Path.Combine(output, "LmpClient.dll"), FileMode.CreateNew)) file.Write(expected);
        using (var file = new FileStream(Path.Combine(output, "ApfCore.dll"), FileMode.CreateNew)) file.Write(expectedCore);
        using var fileAudit = new StreamWriter(new FileStream(Path.Combine(output, "audit.json"), FileMode.CreateNew));
        fileAudit.Write(auditText);
    }
    else
    {
        Require(Directory.GetFiles(output).Select(Path.GetFileName).Order().SequenceEqual(new[] { "ApfCore.dll", "LmpClient.dll", "audit.json" }.Order()) && !Directory.GetDirectories(output).Any(), "Unexpected version output inventory.");
        Require(File.ReadAllBytes(Plain(Path.Combine(output, "LmpClient.dll"))).SequenceEqual(expected), "Client differs from the deterministic two-constant graft.");
        Require(File.ReadAllBytes(Plain(Path.Combine(output, "ApfCore.dll"))).SequenceEqual(expectedCore), "Core differs from the deterministic single-callsite graft.");
        Require(File.ReadAllText(Plain(Path.Combine(output, "audit.json"))) == auditText, "Version-graft audit differs from recomputed evidence.");
    }
    Console.WriteLine(auditText);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

static string Plain(string path)
{
    var full = Path.GetFullPath(path);
    for (var cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
        if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Refusing reparse point: " + cursor);
    return full;
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
static IEnumerable<TypeDefinition> Types(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Types));
static IEnumerable<MethodDefinition> Methods(AssemblyDefinition assembly) => assembly.MainModule.Types.SelectMany(Types).SelectMany(x => x.Methods);
static string MethodImage(MethodDefinition method)
{
    if (!method.HasBody) return method.FullName + "|" + method.Attributes + "|" + method.ImplAttributes;
    var body = method.Body;
    string Operand(object? value) => value switch
    {
        null => "", Instruction instruction => "instruction:" + body.Instructions.IndexOf(instruction),
        Instruction[] instructions => string.Join(",", instructions.Select(x => body.Instructions.IndexOf(x))),
        VariableDefinition variable => "local:" + variable.Index,
        ParameterDefinition parameter => "parameter:" + parameter.Index,
        _ => value.GetType().FullName + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };
    var image = new { method.FullName, method.Attributes, method.ImplAttributes, body.InitLocals, body.MaxStackSize,
        locals = body.Variables.Select(x => x.VariableType.FullName).ToArray(),
        instructions = body.Instructions.Select(x => new { code = x.OpCode.Code.ToString(), operand = Operand(x.Operand) }).ToArray(),
        handlers = body.ExceptionHandlers.Select(x => new { x.HandlerType, catchType = x.CatchType?.FullName,
            start = Operand(x.TryStart), end = Operand(x.TryEnd), handlerStart = Operand(x.HandlerStart), handlerEnd = Operand(x.HandlerEnd), filter = Operand(x.FilterStart) }).ToArray() };
    return JsonSerializer.Serialize(image);
}
record Audit(int schemaVersion, string version, string baseVersion, string baselineFeedSha256, string baselineArchiveSha256,
    string baselineCoreSha256, string baselineClientSha256, string clientSha256, string changedField,
    string[] changedMethods, int preservedMethods, string coreSha256, string[] coreChangedMethods, int preservedCoreMethods,
    string? engineSha256, bool finalReleaseEligible, bool allOtherMethodsUnchanged, bool coreScanCallsiteOnly, bool preparedBuildFileStateUnchanged, string note);
