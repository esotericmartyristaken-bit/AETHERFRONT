using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length is not (1 or 2))throw new ArgumentException("EXACT_PACKAGED_PERSONAL_CLIENT_DLL [KSP_MANAGED_DIRECTORY]");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
string? managed=args.Length==2?args[1]:Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED");
if(managed!=null)resolver.AddSearchDirectory(managed);
using var assembly=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
var type=assembly.MainModule.GetType("Aetherfront.Personal.Client.ScienceEvents")??throw new IOException("Collector absent");
var attributes=type.CustomAttributes.Where(a=>a.AttributeType.FullName=="KSPAddon").ToArray();
bool discovered=attributes.Length==1&&attributes[0].ConstructorArguments.Count==2&&
    Convert.ToInt32(attributes[0].ConstructorArguments[0].Value)==-2&&Equals(attributes[0].ConstructorArguments[1].Value,true);
var awake=type.Methods.SingleOrDefault(m=>m.Name=="Awake");
var update=type.Methods.SingleOrDefault(m=>m.Name=="Update");
bool lifecycle=awake?.HasBody==true&&update?.HasBody==true;
bool recovery=type.Methods.Any(m=>m.Name=="BeforeRecovery")&&type.Methods.Any(m=>m.Name=="AfterRecovery");
var apply=assembly.MainModule.GetType("Aetherfront.Personal.Client.PersonalClient").Methods.Single(m=>m.Name=="Apply");
bool fallback=apply.Body.Instructions.Any(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference m&&m.DeclaringType.FullName==type.FullName&&m.Name=="EnsureStarted");
bool passed=discovered&&lifecycle&&recovery&&fallback;
Console.WriteLine(JsonSerializer.Serialize(new{passed,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant(),discovered,lifecycle,recovery,fallback,
    scope="Metadata and call-site admission over exact packaged artifact, not a manually constructed source collector"}));
Environment.ExitCode=passed?0:1;
