using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3 || !System.Text.RegularExpressions.Regex.IsMatch(args[2], "^kesas-rc[1-9][0-9]{0,8}$")) throw new ArgumentException("Expected validated updater client, fresh versioned output and explicit release version.");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
string desired = args[2];
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
if (Hash(input) != "db028bfee60f0cc8d058fc0643327a05094899b6e608e9240d83997893d8c3a2")
    throw new InvalidDataException("Expected exact previously validated four-component updater client.");
if (File.Exists(output)) throw new IOException("Preserve previous versioned candidate.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(@"C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke\KSP_x64_Data\Managed");
resolver.AddSearchDirectory(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\ClientUpdate-20261113-ValidatedC\Baseline\GameData\LunaMultiplayer\Plugins");
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
var version = assembly.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
var getter = assembly.MainModule.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m => m.Name == "get_ClientVersion");
var literal = getter.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "kesas-rc60");
if ((string)version.Constant != "kesas-rc60") throw new InvalidDataException("Unexpected compiled version.");
var before = assembly.MainModule.Types.SelectMany(Flatten).SelectMany(t => t.Methods).Where(m => m.HasBody && m != getter)
    .ToDictionary(m => m.FullName, Signature);
version.Constant = desired; literal.Operand = desired; assembly.MainModule.Mvid = Guid.NewGuid();
Directory.CreateDirectory(Path.GetDirectoryName(output)!); assembly.Write(output);
using var check = AssemblyDefinition.ReadAssembly(output);
foreach (var method in check.MainModule.Types.SelectMany(Flatten).SelectMany(t => t.Methods).Where(m => m.HasBody && m.FullName != getter.FullName))
    if (!before.TryGetValue(method.FullName, out var expected) || Signature(method) != expected)
        throw new InvalidDataException("Unrelated client method changed: " + method.FullName);
File.WriteAllText(output + ".version-audit.json", JsonSerializer.Serialize(new { version = desired, inputSha256 = Hash(input), outputSha256 = Hash(output), unchangedMethods = before.Count, changedMethod = getter.FullName, changedConstant = version.FullName }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS compiled version and constant updated; " + before.Count + " other methods preserved.");
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) { yield return type; foreach (var child in type.NestedTypes.SelectMany(Flatten)) yield return child; }
static string Signature(MethodDefinition method) => string.Join("|", method.Body.Instructions.Select(i => i.OpCode.Name + ":" + i.Operand));
