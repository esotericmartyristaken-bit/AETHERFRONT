using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=3)throw new ArgumentException("Expected RC24 core, RC25 engine, and unused output path.");
using var feed=JsonDocument.Parse(File.ReadAllText("KESAS-VPS-Server/Updates/APFClientUpdate.json"));
if(feed.RootElement.GetProperty("version").GetString()!="kesas-rc24")throw new InvalidDataException("Expected active RC24 baseline.");
var baseline=feed.RootElement.GetProperty("files").EnumerateArray().Single(f=>f.GetProperty("path").GetString()=="GameData/LunaMultiplayer/Plugins/ApfCore.dll");
if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).Equals(baseline.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Core baseline hash differs from live signed feed.");
if(File.Exists(args[2]))throw new IOException("Output already exists.");
var resolver=new DefaultAssemblyResolver();
foreach(string directory in new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!,Path.GetDirectoryName(Path.GetFullPath(args[1]))!,"Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"})resolver.AddSearchDirectory(directory);
using var core=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
using var engine=AssemblyDefinition.ReadAssembly(args[1],new ReaderParameters{AssemblyResolver=resolver});
MethodReference Hook(string type,string method)=>core.MainModule.ImportReference(engine.MainModule.GetType("KesasInstall."+type).Methods.Single(m=>m.Name==method));
var changed=new HashSet<string>();
void GuardVoid(MethodDefinition method,MethodReference hook) {
    var first=method.Body.Instructions[0];var il=method.Body.GetILProcessor();
    foreach(var value in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Ldarg_2),Instruction.Create(OpCodes.Call,hook),Instruction.Create(OpCodes.Brfalse,first),Instruction.Create(OpCodes.Ret)})il.InsertBefore(first,value);
    changed.Add(method.FullName);
}
var installer=core.MainModule.GetType("ApfCore.ApfArchiveInstaller");
var restore=core.MainModule.GetType("ApfCore.ApfRecoveryStore").Methods.Single(m=>m.Name=="Restore");
restore.Body.GetILProcessor().InsertBefore(restore.Body.Instructions[0],Instruction.Create(OpCodes.Call,Hook("PreparedPack","Close")));
changed.Add(restore.FullName);
var extract=installer.Methods.Single(m=>m.Name=="ExtractPackage");
{
    var first=extract.Body.Instructions[0];var il=extract.Body.GetILProcessor();var empty=Instruction.Create(OpCodes.Pop);
    foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Ldarg_2),Instruction.Create(OpCodes.Call,Hook("PreparedPack","Extract")),Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Brfalse,empty),Instruction.Create(OpCodes.Ret),empty})il.InsertBefore(first,instruction);
    changed.Add(extract.FullName);
}
GuardVoid(installer.Methods.Single(m=>m.Name=="ValidateArchive"),Hook("PreparedPack","ArchiveReady"));
var scanner=core.MainModule.GetType("ApfCore.ApfPackScanner");
var same=scanner.Methods.Single(m=>m.Name=="SameFiles");
same.Body=new MethodBody(same);
foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Call,Hook("RuntimeFiles","SameFiles")),Instruction.Create(OpCodes.Ret)})same.Body.Instructions.Add(instruction);
changed.Add(same.FullName);
var files=scanner.Methods.Single(m=>m.Name=="BuildFileState");
{
    var first=files.Body.Instructions[0];var il=files.Body.GetILProcessor();var empty=Instruction.Create(OpCodes.Pop);
    foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Call,Hook("PreparedPack","Files")),Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Brfalse,empty),Instruction.Create(OpCodes.Ret),empty})il.InsertBefore(first,instruction);
    changed.Add(files.FullName);
}
var apply=installer.Methods.Single(m=>m.Name=="ApplyTransaction");
var scan=apply.Body.Instructions.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="ApfCore.ApfPackScanner"&&m.Name=="Scan");
scan.Operand=Hook("RuntimeFiles","ScanFinal");changed.Add(apply.FullName);
var launch=core.MainModule.GetType("ApfCore.ApfPostExitLauncher").Methods.Single(m=>m.Name=="Launch");
int timeout=0;foreach(var instruction in launch.Body.Instructions)if(instruction.OpCode==OpCodes.Ldc_I4&&Equals(instruction.Operand,20000)){instruction.Operand=600000;timeout++;}
if(timeout!=1)throw new InvalidDataException("Unexpected helper handshake timeout sites.");changed.Add(launch.FullName);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
core.MainModule.Mvid=Guid.NewGuid();core.Write(args[2]);
using var old=AssemblyDefinition.ReadAssembly(args[0]);using var next=AssemblyDefinition.ReadAssembly(args[2]);int preserved=0;
foreach(var type in old.MainModule.Types.SelectMany(Flatten))foreach(var method in type.Methods)if(!changed.Contains(method.FullName)) {
    var result=next.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods).Single(m=>m.FullName==method.FullName);
    if(Signature(method)!=Signature(result))throw new InvalidDataException("Unrelated method changed: "+method.FullName);preserved++;
}
Console.WriteLine($"PASS: {changed.Count} targeted core methods; {preserved} unrelated methods preserved.");
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes)foreach(var child in Flatten(nested))yield return child;}
static string Signature(MethodDefinition method){if(!method.HasBody)return "none";var indexes=method.Body.Instructions.Select((i,n)=>(i,n)).ToDictionary(x=>x.i,x=>x.n);string Operand(object? value)=>value switch{null=>"",Instruction i=>"branch:"+indexes[i],Instruction[] a=>"branches:"+string.Join(",",a.Select(i=>indexes[i])),_=>value.ToString()??""};return string.Join("\n",method.Body.Instructions.Select(i=>i.OpCode.Name+" "+Operand(i.Operand)));}
