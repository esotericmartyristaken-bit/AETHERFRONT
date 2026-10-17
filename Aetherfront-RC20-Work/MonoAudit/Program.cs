using Mono.Cecil;
using Mono.Cecil.Cil;
var resolver=new DefaultAssemblyResolver();string managed=Path.GetFullPath("Aetherfront-KSP-Smoke/KSP_x64_Data/Managed");resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.GetFullPath("Aetherfront-RC20-Work/Patched"));
using var system=AssemblyDefinition.ReadAssembly(Path.Combine(managed,"System.dll"));
var setter=system.MainModule.GetType("System.Net.ServicePoint").Methods.Single(m=>m.Name=="set_ReceiveBufferSize");
if(!Stub(setter))throw new Exception("Expected KSP Mono stub not found");
Console.WriteLine("PASS exact KSP System.dll proves ReceiveBufferSize setter is an unconditional NotImplemented stub");
foreach(var version in new[]{"18","20"}) {
    using var engine=AssemblyDefinition.ReadAssembly($"Aetherfront-RC{version}-Work/Engine/bin/Release/net472/KesasInstall.dll",new ReaderParameters{AssemblyResolver=resolver});
    int calls=0;var stubs=new HashSet<string>();
    foreach(var type in engine.MainModule.Types.SelectMany(Flatten))foreach(var method in type.Methods.Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions)if(instruction.Operand is MethodReference called && called.DeclaringType.Scope is AssemblyNameReference scope && new[]{"mscorlib","System","System.Core"}.Contains(scope.Name)) {
        var target=called.Resolve();if(target==null)throw new Exception("Unresolved runtime API: "+called.FullName);calls++;
        if(Stub(target))stubs.Add(target.FullName);
    }
    foreach(string stub in stubs)Console.WriteLine("RC"+version+" calls KSP unsupported API: "+stub);
    if(version=="18" && stubs.Count!=1 || version=="20" && stubs.Count!=0)throw new Exception("Unexpected Mono API audit result for RC"+version);
    Console.WriteLine("PASS RC"+version+": "+calls+" runtime calls resolved against KSP libraries; "+stubs.Count+" unconditional unsupported stubs");
}
static bool Stub(MethodDefinition method)=>method.HasBody && !method.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ret) && method.Body.Instructions.Any(i=>i.OpCode==OpCodes.Throw) && method.Body.Instructions.Any(i=>i.Operand is MethodReference called && (called.Name=="GetMustImplement" || called.DeclaringType.FullName=="System.NotImplementedException"));
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes)foreach(var child in Flatten(nested))yield return child;}
