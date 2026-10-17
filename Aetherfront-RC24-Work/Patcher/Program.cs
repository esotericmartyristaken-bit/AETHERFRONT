using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=2)throw new ArgumentException("Expected authenticated RC23 plugin directory and unused output directory.");
string source=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
if(Directory.Exists(output))throw new IOException("Patched output exists; preserve previous attempts.");
var feed=System.Text.Json.JsonDocument.Parse(File.ReadAllText("KESAS-VPS-Server/Updates/APFClientUpdate.json"));
if(feed.RootElement.GetProperty("version").GetString()!="kesas-rc23")throw new InvalidDataException("Expected active RC23 baseline.");
foreach(string name in new[]{"LmpClient.dll","ApfCore.dll"}) {
    var file=feed.RootElement.GetProperty("files").EnumerateArray().Single(f=>f.GetProperty("path").GetString()=="GameData/LunaMultiplayer/Plugins/"+name);
    if(!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,name)))).Equals(file.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Baseline differs from signed live feed: "+name);
}
var resolver=new DefaultAssemblyResolver();
foreach(string path in new[]{source,"Aetherfront-RC24-Work/Engine/bin/Release/net472","Aetherfront-KSP-Smoke/KSP_x64_Data/Managed","KESAS-RC15-Work/Inputs"})resolver.AddSearchDirectory(Path.GetFullPath(path));
Directory.CreateDirectory(output);
using(var client=AssemblyDefinition.ReadAssembly(Path.Combine(source,"LmpClient.dll"),new ReaderParameters{AssemblyResolver=resolver})) {
    var changed=new HashSet<string>();int versions=0,texts=0;
    var version=client.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");
    if(!Equals(version.Constant,"kesas-rc23"))throw new InvalidDataException("Unexpected client version.");version.Constant="kesas-rc24";
    foreach(var method in client.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods).Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions)if(instruction.OpCode==OpCodes.Ldstr) {
        if(Equals(instruction.Operand,"kesas-rc23")){instruction.Operand="kesas-rc24";versions++;changed.Add(method.FullName);}
        if(Equals(instruction.Operand,"Verified. Installing after KSP closes; restarting...")){instruction.Operand="Verified. KSP will close; installation continues in a progress window.";texts++;changed.Add(method.FullName);}
    }
    if(versions!=1||texts!=1)throw new InvalidDataException("Unexpected client edit sites.");
    client.MainModule.Mvid=Guid.NewGuid();client.Write(Path.Combine(output,"LmpClient.dll"));Audit(Path.Combine(source,"LmpClient.dll"),Path.Combine(output,"LmpClient.dll"),changed);
}
using var core=AssemblyDefinition.ReadAssembly(Path.Combine(source,"ApfCore.dll"),new ReaderParameters{AssemblyResolver=resolver});
using var engine=AssemblyDefinition.ReadAssembly("Aetherfront-RC24-Work/Engine/bin/Release/net472/KesasInstall.dll",new ReaderParameters{AssemblyResolver=resolver});
var hooks=engine.MainModule.GetType("KesasInstall.NativeInstall");
MethodReference Hook(string name)=>core.MainModule.ImportReference(hooks.Methods.Single(m=>m.Name==name));
var changedCore=new HashSet<string>();
var runner=core.MainModule.GetType("ApfCore.ApfPostExitRunner");
var wait=runner.Methods.Single(m=>m.Name=="WaitForExit");
foreach(var ret in wait.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray()) {
    // Preserve branch/exception-handler targets at the original return address.
    ret.OpCode=OpCodes.Nop;var il=wait.Body.GetILProcessor();
    var load=Instruction.Create(OpCodes.Ldarg_1);il.InsertAfter(ret,load);
    var call=Instruction.Create(OpCodes.Call,Hook("AcquireExecutionLocks"));il.InsertAfter(load,call);il.InsertAfter(call,Instruction.Create(OpCodes.Ret));
}
changedCore.Add(wait.FullName);
var installer=core.MainModule.GetType("ApfCore.ApfArchiveInstaller");
var apply=installer.Methods.Single(m=>m.Name=="ApplyTransaction");
var waitCall=apply.Body.Instructions.Single(i=>i.Operand is MethodReference m&&m.Name=="WaitForProcessExit");
apply.Body.GetILProcessor().InsertBefore(waitCall,Instruction.Create(OpCodes.Ldarg_0));
apply.Body.GetILProcessor().InsertBefore(waitCall,Instruction.Create(OpCodes.Call,Hook("BeginPack")));
var create=apply.Body.Instructions.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="ApfCore.ApfRecoveryStore"&&m.Name=="Create");
create.Operand=Hook("CreateRecovery");
var scan=apply.Body.Instructions.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="ApfCore.ApfPackScanner"&&m.Name=="Scan");
apply.Body.GetILProcessor().InsertBefore(scan,Instruction.Create(OpCodes.Call,Hook("Verifying")));changedCore.Add(apply.FullName);
var backup=installer.Methods.Single(m=>m.Name=="MovePathToBackup");
Prepend(backup,Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Call,Hook("RemoveApproved")),Instruction.Create(OpCodes.Brfalse,backup.Body.Instructions[0]),Instruction.Create(OpCodes.Ret));changedCore.Add(backup.FullName);
var extract=installer.Methods.Single(m=>m.Name=="ExtractPackage");
Prepend(extract,Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Call,Hook("Extracting")));changedCore.Add(extract.FullName);
foreach(var relaunch in installer.Methods.Where(m=>m.Name=="Relaunch")) {
    Prepend(relaunch,Instruction.Create(OpCodes.Call,Hook("BlockAutomaticRestart")),Instruction.Create(OpCodes.Brfalse,relaunch.Body.Instructions[0]),Instruction.Create(OpCodes.Ret));changedCore.Add(relaunch.FullName);
}
if(changedCore.Count!=6)throw new InvalidDataException("Unexpected core hook method count.");
core.MainModule.Mvid=Guid.NewGuid();core.Write(Path.Combine(output,"ApfCore.dll"));Audit(Path.Combine(source,"ApfCore.dll"),Path.Combine(output,"ApfCore.dll"),changedCore);
Console.WriteLine("PASS: authenticated RC23 inputs; client version/status only; six guarded native core hooks. All unrelated methods preserved.");
static void Prepend(MethodDefinition method,params Instruction[] additions){var il=method.Body.GetILProcessor();var first=method.Body.Instructions[0];foreach(var instruction in additions)il.InsertBefore(first,instruction);}
static void Audit(string before,string after,HashSet<string> changed) {
    using var original=AssemblyDefinition.ReadAssembly(before);using var result=AssemblyDefinition.ReadAssembly(after);int preserved=0;
    foreach(var method in original.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods))if(!changed.Contains(method.FullName)) {
        var next=result.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods).Single(m=>m.FullName==method.FullName);
        if(Signature(method)!=Signature(next))throw new InvalidDataException("Unrelated method changed: "+method.FullName);preserved++;
    }
    Console.WriteLine(Path.GetFileName(after)+": "+preserved+" unrelated methods preserved.");
}
static string Signature(MethodDefinition method){if(!method.HasBody)return "none";var indexes=method.Body.Instructions.Select((i,n)=>(i,n)).ToDictionary(x=>x.i,x=>x.n);string Operand(object? value)=>value switch{null=>"",Instruction i=>"branch:"+indexes[i],Instruction[] a=>"branches:"+string.Join(",",a.Select(i=>indexes[i])),_=>value.ToString()??""};return string.Join("\n",method.Body.Instructions.Select(i=>i.OpCode.Name+" "+Operand(i.Operand)));}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes)foreach(var child in Flatten(nested))yield return child;}
