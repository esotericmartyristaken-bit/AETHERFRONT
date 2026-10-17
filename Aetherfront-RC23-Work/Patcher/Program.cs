using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("Expected input DLL, authenticated SHA256 and new output DLL.");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[2]);
if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).Equals(args[1], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unreviewed RC22 client.");
if (File.Exists(output)) throw new IOException("Version-patched output exists; preserve immutable artifacts.");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Path.GetFullPath("Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"));
using var client = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
var version = client.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f => f.Name == "Value");
if (!Equals(version.Constant, "kesas-rc22")) throw new InvalidDataException("Unexpected input version.");
version.Constant = "kesas-rc23";
int count = 0;
var changed = new HashSet<string>();
foreach (var type in client.MainModule.Types.SelectMany(Flatten))
foreach (var method in type.Methods.Where(m => m.HasBody))
foreach (var instruction in method.Body.Instructions)
    if (instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, "kesas-rc22")) { instruction.Operand = "kesas-rc23"; changed.Add(method.FullName); count++; }
if (count != 1) throw new InvalidDataException("Unexpected version reference count.");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
client.MainModule.Mvid = Guid.NewGuid();
client.Write(output);
using var pristine = AssemblyDefinition.ReadAssembly(input);
using var verified = AssemblyDefinition.ReadAssembly(output);
int preserved = 0;
foreach (var type in pristine.MainModule.Types.SelectMany(Flatten))
foreach (var method in type.Methods) {
    if (changed.Contains(method.FullName)) continue;
    var next = verified.MainModule.Types.SelectMany(Flatten).Single(t => t.FullName == type.FullName).Methods.Single(m => m.FullName == method.FullName);
    if (Signature(method) != Signature(next)) throw new InvalidDataException("Unrelated method changed: " + method.FullName);
    preserved++;
}
Console.WriteLine("PASS: one client version method changed; " + preserved + " unrelated methods preserved. No installer/core behavior changes.");
static string Signature(MethodDefinition method) {
    if (!method.HasBody) return "none";
    var indexes = method.Body.Instructions.Select((i,n) => (i,n)).ToDictionary(x => x.i,x => x.n);
    string Operand(object? value) => value switch { null => "", Instruction i => "branch:" + indexes[i], Instruction[] a => "branches:" + string.Join(",",a.Select(i => indexes[i])), _ => value.ToString() ?? "" };
    return string.Join("\n",method.Body.Instructions.Select(i => i.OpCode.Name + " " + Operand(i.Operand)));
}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) { yield return type; foreach (var nested in type.NestedTypes) foreach (var child in Flatten(nested)) yield return child; }
