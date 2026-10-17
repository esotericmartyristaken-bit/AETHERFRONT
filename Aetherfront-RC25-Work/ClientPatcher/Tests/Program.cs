using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilMethodAttributes=Mono.Cecil.MethodAttributes;
using CecilFieldAttributes=Mono.Cecil.FieldAttributes;
using CecilTypeAttributes=Mono.Cecil.TypeAttributes;

if(args.Length is not (1 or 3))throw new ArgumentException("Expected patched LmpClient.dll, optionally integration ApfCore.dll and KesasInstall.dll.");
using var client=AssemblyDefinition.ReadAssembly(args[0]);
var source=client.MainModule.GetType("LmpClient.MainSystem").Methods.Single(m=>m.Name=="FixBindingRedirects");
using var fixture=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SerializerRegistryFixture",new Version(1,0)),"SerializerRegistryFixture.dll",ModuleKind.Dll);
var module=fixture.MainModule;
var loader=new TypeDefinition("","AssemblyLoader",CecilTypeAttributes.Public|CecilTypeAttributes.Class,module.ImportReference(typeof(object)));
var access=new TypeDefinition("HarmonyLib","AccessTools",CecilTypeAttributes.Public|CecilTypeAttributes.Class,module.ImportReference(typeof(object)));
var target=new TypeDefinition("","Registry",CecilTypeAttributes.Public|CecilTypeAttributes.Class,module.ImportReference(typeof(object)));
module.Types.Add(loader);module.Types.Add(access);module.Types.Add(target);
var bindings=new FieldDefinition("bindingRedirect",CecilFieldAttributes.Public|CecilFieldAttributes.Static,module.ImportReference(typeof(Dictionary<string,Assembly>)));
var flags=new FieldDefinition("all",CecilFieldAttributes.Public|CecilFieldAttributes.Static,module.ImportReference(typeof(BindingFlags)));
loader.Fields.Add(bindings);access.Fields.Add(flags);
TypeReference MapType(TypeReference type) {
    if(type is GenericParameter)return type;
    if(type is GenericInstanceType generic){var result=new GenericInstanceType(MapType(generic.ElementType));foreach(var argument in generic.GenericArguments)result.GenericArguments.Add(MapType(argument));return result;}
    if(type.FullName=="AssemblyLoader")return loader;
    if(type.FullName=="HarmonyLib.AccessTools")return access;
    return module.ImportReference(Type.GetType(type.FullName,throwOnError:true)!);
}
MethodReference MapMethod(MethodReference method) {
    var result=new MethodReference(method.Name,MapType(method.ReturnType),MapType(method.DeclaringType)){HasThis=method.HasThis,ExplicitThis=method.ExplicitThis,CallingConvention=method.CallingConvention};
    foreach(var parameter in method.Parameters)result.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
    return result;
}
var run=new MethodDefinition("Run",CecilMethodAttributes.Public|CecilMethodAttributes.Static,module.ImportReference(typeof(void)));
target.Methods.Add(run);
run.Body=new Mono.Cecil.Cil.MethodBody(run){InitLocals=source.Body.InitLocals,MaxStackSize=source.Body.MaxStackSize};
foreach(var variable in source.Body.Variables)run.Body.Variables.Add(new VariableDefinition(MapType(variable.VariableType)));
var instructions=source.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));
foreach(var i in source.Body.Instructions) {
    var copy=instructions[i];copy.OpCode=i.OpCode;copy.Operand=i.Operand switch{
        Instruction branch=>instructions[branch],VariableDefinition variable=>run.Body.Variables[variable.Index],
        MethodReference called=>MapMethod(called),FieldReference field when field.FullName=="System.Reflection.BindingFlags HarmonyLib.AccessTools::all"=>flags,
        FieldReference field=>throw new InvalidDataException("Unexpected fixture field: "+field.FullName),TypeReference type=>MapType(type),_=>i.Operand};
    run.Body.Instructions.Add(copy);
}
int checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
Check(source.Body.ExceptionHandlers.Count==0,"No catch-all registry suppression");
Check(source.Body.Instructions.Count(i=>i.Operand is MethodReference m && m.Name=="ContainsKey")==2,"Exactly two guarded keys");
Check(source.Body.Instructions.Count(i=>i.Operand is MethodReference m && m.Name=="Add")==2,"Original serializer registrations retained");
string path=Path.Combine(AppContext.BaseDirectory,"SerializerRegistryFixture.dll");fixture.Write(path);
var assembly=Assembly.LoadFile(path);
var dictionaryField=assembly.GetType("AssemblyLoader")!.GetField("bindingRedirect")!;
assembly.GetType("HarmonyLib.AccessTools")!.GetField("all")!.SetValue(null,(BindingFlags)62);
var invoke=assembly.GetType("Registry")!.GetMethod("Run")!;
const string clientKey="LmpClient.XmlSerializers, Culture=neutral, PublicKeyToken=null";
const string commonKey="LmpCommon.XmlSerializers, Culture=neutral, PublicKeyToken=null";
var dictionary=new Dictionary<string,Assembly>();dictionaryField.SetValue(null,dictionary);invoke.Invoke(null,null);
Check(dictionary.Count==2,"First invocation registers both serializers");
Check(dictionary[clientKey]==Assembly.Load("System.Xml") && dictionary[commonKey]==Assembly.Load("System.Xml"),"Original System.Xml behavior preserved");
invoke.Invoke(null,null);Check(dictionary.Count==2,"Repeated Awake registration is idempotent");
var existing=typeof(string).Assembly;
dictionary=new Dictionary<string,Assembly>{{clientKey,existing}};dictionaryField.SetValue(null,dictionary);invoke.Invoke(null,null);
Check(dictionary.Count==2 && dictionary[clientKey]==existing,"Existing client binding preserved; missing common binding added");
Check(dictionary[commonKey]==Assembly.Load("System.Xml"),"Missing common binding uses System.Xml");
dictionary=new Dictionary<string,Assembly>{{commonKey,existing}};dictionaryField.SetValue(null,dictionary);invoke.Invoke(null,null);
Check(dictionary[commonKey]==existing && dictionary[clientKey]==Assembly.Load("System.Xml"),"Existing common binding preserved; missing client binding added");
dictionary=new Dictionary<string,Assembly>{{clientKey,existing},{commonKey,typeof(Enumerable).Assembly}};
dictionaryField.SetValue(null,dictionary);invoke.Invoke(null,null);
Check(dictionary.Count==2 && dictionary[clientKey]==existing && dictionary[commonKey]==typeof(Enumerable).Assembly,"Both existing bindings remain untouched");
dictionaryField.SetValue(null,null);invoke.Invoke(null,null);Check(dictionaryField.GetValue(null)==null,"Existing null-registry behavior retained");
Console.WriteLine($"PASS {checks} serializer assertions executing extracted graft IL against isolated registry; no KSP assemblies loaded.");
foreach(string name in new[]{"Awake","OnDestroy","OnApplicationQuit"}) {
    var callback=client.MainModule.GetType("LmpClient.MainSystem").Methods.Single(m=>m.Name==name && !m.HasParameters);
    Check(callback.Body.Instructions[0].OpCode==OpCodes.Ldarg_0 && callback.Body.Instructions[1].Operand is MethodReference guard &&
        guard.DeclaringType.FullName=="LmpClient.Apf.CanonicalStartup" && guard.Name==(name=="Awake"?"AllowAwake":"Owns") &&
        callback.Body.Instructions[2].OpCode==OpCodes.Brtrue && callback.Body.Instructions[3].OpCode==OpCodes.Ret,"Ownership prefix precedes original "+name);
}
Console.WriteLine("PASS three startup callback prefix checks in grafted IL.");
var handshake=client.MainModule.GetType("LmpClient.Apf.ApfSyncCoordinator").Methods.Single(m=>m.Name=="CheckManifestAndGameData");
var handshakeCalls=handshake.Body.Instructions.Where(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="KesasInstall.Installer" && m.Name=="ScanConnection").ToArray();
Check(handshakeCalls.Length==1 && handshakeCalls[0].Offset==0x0262 && handshakeCalls[0].OpCode==OpCodes.Call,"Exactly one production handshake call replaced at IL_0262");
Check(!handshake.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="ApfCore.ApfPackScanner" && m.Name=="Scan"),"Handshake cannot accidentally run original scan after receipt validation");
Console.WriteLine("PASS two production handshake callsite checks in grafted IL.");
if(args.Length==3) {
    using var resolver=new DefaultAssemblyResolver();
    foreach(string directory in new[]{Path.GetDirectoryName(Path.GetFullPath(args[1]))!,Path.GetDirectoryName(Path.GetFullPath(args[2]))!,
        "Aetherfront-RC24-Work/Candidate/Client/GameData/LunaMultiplayer/Plugins","Aetherfront-RC24-Work/Candidate/Client/GameData/000_Harmony",
        "Aetherfront-KSP-Smoke/KSP_x64_Data/Managed","KESAS-RC15-Work/Inputs"})resolver.AddSearchDirectory(Path.GetFullPath(directory));
    using var integrated=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
    using var core=AssemblyDefinition.ReadAssembly(args[1],new ReaderParameters{AssemblyResolver=resolver});
    var launch=core.MainModule.GetType("ApfCore.ApfPostExitLauncher").Methods.Single(m=>m.Name=="Launch");
    Check(launch.Body.Instructions.Count(i=>i.OpCode==OpCodes.Ldc_I4 && Equals(i.Operand,1800000))==1,"Integrated core waits at most 1800 seconds for readiness");
    var flow=integrated.MainModule.GetType("LmpClient.Apf.ConnectionInstall");
    int resolved=0;
    foreach(var method in new[]{flow,integrated.MainModule.GetType("LmpClient.Windows.Connection.ConnectionWindow"),integrated.MainModule.GetType("LmpClient.Apf.CanonicalStartup")}.SelectMany(t=>t.Methods)
        .Concat(integrated.MainModule.GetType("LmpClient.Apf.ApfSyncCoordinator").Methods.Where(m=>m.Name=="CheckManifestAndGameData")).Where(m=>m.HasBody))
        foreach(var instruction in method.Body.Instructions) {
            if(instruction.Operand is MethodReference called){Check(called.Resolve()!=null,"Integrated method resolves: "+called.FullName);resolved++;}
            if(instruction.Operand is FieldReference field){Check(field.Resolve()!=null,"Integrated field resolves: "+field.FullName);resolved++;}
            if(instruction.Operand is Instruction branch)Check(method.Body.Instructions.Contains(branch),"Branch remains inside body");
        }
    Check(!flow.Methods.SelectMany(m=>m.HasBody?m.Body.Instructions:Enumerable.Empty<Instruction>()).Any(i=>i.Operand is MethodReference m && (m.DeclaringType.FullName.StartsWith("System.Windows.Forms") || m.DeclaringType.FullName=="KesasInstall.NativeInstall")),"No in-game native UI invocation");
    Console.WriteLine($"PASS integration: {resolved} references resolve against main-owned RC25 core and engine; readiness timeout 1800000ms.");
}
