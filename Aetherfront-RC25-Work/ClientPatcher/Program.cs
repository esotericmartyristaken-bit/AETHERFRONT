using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string clientHash="8FBA4CA5C78C89399BCF5E0AABA470E157873EA20D107B21E27E6FE90F4672F6";
const string coreHash="3B6A0A7959D249C3228D70BCB5946FA42F38F0DC301FEDA3B8A64BC9BBA256EF";
if(args.Length!=4)throw new ArgumentException("Usage: ClientPatcher <authenticated RC24 LmpClient.dll> <authenticated RC24 ApfCore.dll> <RC25 donor> <unused output directory>");
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
if(Hash(args[0])!=clientHash || Hash(args[1])!=coreHash)throw new InvalidDataException("Unreviewed RC24 input.");
string output=Path.GetFullPath(args[3]);
if(Directory.Exists(output))throw new IOException("Patched output exists; preserve previous attempts.");
using var resolver=new DefaultAssemblyResolver();
foreach(string directory in new[]{"Aetherfront-RC25-Work/Engine/bin/Release/net472",Path.GetDirectoryName(Path.GetFullPath(args[0]))!,Path.GetDirectoryName(Path.GetFullPath(args[1]))!,
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"..","..","000_Harmony"),
    Path.GetDirectoryName(Path.GetFullPath(args[2]))!,"Aetherfront-RC25-Work/Engine/bin/Release/net472",
    "Aetherfront-KSP-Smoke/KSP_x64_Data/Managed","KESAS-RC15-Work/Inputs"})resolver.AddSearchDirectory(Path.GetFullPath(directory));
using var client=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
using var donor=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
string enginePath=Path.GetFullPath("Aetherfront-RC25-Work/Engine/bin/Release/net472/KesasInstall.dll");
using var engine=AssemblyDefinition.ReadAssembly(enginePath,new ReaderParameters{AssemblyResolver=resolver});
var connectionScan=engine.MainModule.GetType("KesasInstall.Installer")?.Methods.SingleOrDefault(m=>
    m.Name=="ScanConnection" && m.IsPublic && m.IsStatic && m.ReturnType.FullName=="ApfCore.ApfSyncPlan" &&
    m.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(new[]{"System.String","ApfCore.ApfPackManifest","ApfCore.ApfPackState"}))
    ??throw new InvalidDataException("Rebuild the RC25 engine exposing public static ApfSyncPlan Installer.ScanConnection(string, ApfPackManifest, ApfPackState) before grafting.");
var module=client.MainModule;
var sourceFlow=donor.MainModule.GetType("LmpClient.Apf.ConnectionInstall")??throw new InvalidDataException("Donor flow missing.");
var sourceWindow=donor.MainModule.GetType("LmpClient.Windows.Connection.ConnectionWindow")??throw new InvalidDataException("Donor window missing.");
var sourceStartup=donor.MainModule.GetType("LmpClient.Apf.CanonicalStartup")??throw new InvalidDataException("Donor startup guard missing.");
if(sourceFlow.NestedTypes.Count!=0 || sourceWindow.NestedTypes.Count!=0 || sourceStartup.NestedTypes.Count!=0)throw new InvalidDataException("Review generated helper types before grafting.");
var flow=module.GetType(sourceFlow.FullName)??throw new InvalidDataException("RC24 connection flow missing.");
var window=module.GetType(sourceWindow.FullName)??throw new InvalidDataException("RC24 connection window missing.");
if(module.GetType(sourceStartup.FullName)!=null)throw new InvalidDataException("Unexpected existing startup guard.");
var startup=new TypeDefinition(sourceStartup.Namespace,sourceStartup.Name,sourceStartup.Attributes,module.TypeSystem.Object);module.Types.Add(startup);
var types=new Dictionary<string,TypeDefinition>{{sourceFlow.FullName,flow},{sourceWindow.FullName,window},{sourceStartup.FullName,startup}};
var fields=new Dictionary<string,FieldDefinition>();
var methods=new Dictionary<string,MethodDefinition>();
TypeReference ImportType(TypeReference type)
{
    if(type is GenericParameter)return type;
    if(type is GenericInstanceType generic){var copy=new GenericInstanceType(ImportType(generic.ElementType));foreach(var argument in generic.GenericArguments)copy.GenericArguments.Add(ImportType(argument));return copy;}
    if(type is ArrayType array)return new ArrayType(ImportType(array.ElementType),array.Rank);
    if(type is ByReferenceType byref)return new ByReferenceType(ImportType(byref.ElementType));
    if(type is RequiredModifierType required)return new RequiredModifierType(ImportType(required.ModifierType),ImportType(required.ElementType));
    if(type is OptionalModifierType optional)return new OptionalModifierType(ImportType(optional.ModifierType),ImportType(optional.ElementType));
    if(types.TryGetValue(type.FullName,out var mapped))return mapped;
    if(type.Scope.Name=="LmpClient" || type.Scope.Name=="LmpClient.dll")return module.GetType(type.FullName)??throw new InvalidDataException("Missing client type: "+type.FullName);
    return module.ImportReference(type);
}
MethodReference ImportMethod(MethodReference method)
{
    if(method is GenericInstanceMethod generic){var copy=new GenericInstanceMethod(ImportMethod(generic.ElementMethod));foreach(var argument in generic.GenericArguments)copy.GenericArguments.Add(ImportType(argument));return copy;}
    if(methods.TryGetValue(method.FullName,out var mapped))return mapped;
    var result=module.ImportReference(method);result.DeclaringType=ImportType(method.DeclaringType);
    if(method.ReturnType is not GenericParameter)result.ReturnType=ImportType(method.ReturnType);
    for(int i=0;i<method.Parameters.Count;i++)if(method.Parameters[i].ParameterType is not GenericParameter)result.Parameters[i].ParameterType=ImportType(method.Parameters[i].ParameterType);
    return result;
}
foreach(var source in new[]{sourceFlow,sourceWindow,sourceStartup}) {
    var target=types[source.FullName];
    foreach(var field in source.Fields) {
        var copy=target.Fields.FirstOrDefault(f=>f.Name==field.Name);
        if(copy==null){copy=new FieldDefinition(field.Name,field.Attributes,ImportType(field.FieldType));if(field.HasConstant)copy.Constant=field.Constant;target.Fields.Add(copy);}
        else if(copy.FieldType.FullName!=ImportType(field.FieldType).FullName)throw new InvalidDataException("Field type changed: "+field.FullName);
        fields[field.FullName]=copy;
    }
    foreach(var method in source.Methods) {
        var copy=target.Methods.FirstOrDefault(m=>m.FullName==method.FullName);
        if(copy==null){copy=new MethodDefinition(method.Name,method.Attributes,ImportType(method.ReturnType)){ImplAttributes=method.ImplAttributes,CallingConvention=method.CallingConvention};foreach(var p in method.Parameters)copy.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,ImportType(p.ParameterType)));target.Methods.Add(copy);}
        methods[method.FullName]=copy;
    }
    foreach(var property in source.Properties.Where(p=>!target.Properties.Any(t=>t.Name==p.Name)))
        target.Properties.Add(new PropertyDefinition(property.Name,property.Attributes,ImportType(property.PropertyType)){
            GetMethod=property.GetMethod==null?null:methods[property.GetMethod.FullName],SetMethod=property.SetMethod==null?null:methods[property.SetMethod.FullName]});
}
void CopyBody(MethodDefinition source,MethodDefinition target)
{
    if(!source.HasBody)throw new InvalidDataException("Donor method has no body: "+source.FullName);
    target.Body=new MethodBody(target){InitLocals=source.Body.InitLocals,MaxStackSize=source.Body.MaxStackSize};
    foreach(var variable in source.Body.Variables)target.Body.Variables.Add(new VariableDefinition(ImportType(variable.VariableType)));
    var instructions=source.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));
    foreach(var instruction in source.Body.Instructions) {
        var copy=instructions[instruction];copy.OpCode=instruction.OpCode;copy.Operand=instruction.Operand switch{
            null=>null,Instruction branch=>instructions[branch],Instruction[] branches=>branches.Select(b=>instructions[b]).ToArray(),
            VariableDefinition variable=>target.Body.Variables[variable.Index],ParameterDefinition parameter=>target.Parameters[parameter.Index],
            MethodReference called=>ImportMethod(called),FieldReference field when fields.ContainsKey(field.FullName)=>fields[field.FullName],
            FieldReference field=>new FieldReference(field.Name,ImportType(field.FieldType),ImportType(field.DeclaringType)),TypeReference type=>ImportType(type),_=>instruction.Operand};
        target.Body.Instructions.Add(copy);
    }
    foreach(var handler in source.Body.ExceptionHandlers)target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType){
        CatchType=handler.CatchType==null?null:ImportType(handler.CatchType),TryStart=instructions[handler.TryStart],TryEnd=handler.TryEnd==null?null:instructions[handler.TryEnd],
        HandlerStart=instructions[handler.HandlerStart],HandlerEnd=handler.HandlerEnd==null?null:instructions[handler.HandlerEnd],FilterStart=handler.FilterStart==null?null:instructions[handler.FilterStart]});
}
var allowed=new HashSet<string>();
foreach(var method in sourceFlow.Methods){CopyBody(method,methods[method.FullName]);allowed.Add(methods[method.FullName].FullName);}
foreach(var method in sourceStartup.Methods){CopyBody(method,methods[method.FullName]);allowed.Add(methods[method.FullName].FullName);}
foreach(var method in sourceWindow.Methods.Where(m=>new[]{"DrawWindowContent","DrawTopButtons","SetStyles","DrawGui"}.Contains(m.Name))) {
    var target=methods[method.FullName];CopyBody(method,target);allowed.Add(target.FullName);
}
var version=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");
if(!Equals(version.Constant,"kesas-rc24"))throw new InvalidDataException("Wrong input version.");version.Constant="kesas-rc25";
var versionGetter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");
var versionLiteral=versionGetter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr && Equals(i.Operand,"kesas-rc24"));
versionLiteral.Operand="kesas-rc25";allowed.Add(versionGetter.FullName);

// Replace only the production handshake scan. Core/helper final verification
// remains untouched; the engine owns authenticated receipt validation/fallback.
var fetchAndScan=module.GetType("LmpClient.Apf.ApfSyncCoordinator").Methods.Single(m=>m.FullName=="System.Void LmpClient.Apf.ApfSyncCoordinator::CheckManifestAndGameData()");
var connectionScanCall=fetchAndScan.Body.Instructions.Single(i=>i.OpCode==OpCodes.Call && i.Operand is MethodReference called &&
    called.FullName=="ApfCore.ApfSyncPlan ApfCore.ApfPackScanner::Scan(System.String,ApfCore.ApfPackManifest,ApfCore.ApfPackState)");
int connectionScanOffset=connectionScanCall.Offset;
connectionScanCall.Operand=module.ImportReference(connectionScan);
allowed.Add(fetchAndScan.FullName);

// The private log's Awake stack and authenticated baseline identify two unguarded
// Dictionary.Add calls. Skip only an existing key; never replace its binding.
var redirects=module.GetType("LmpClient.MainSystem").Methods.Single(m=>m.Name=="FixBindingRedirects");
if(redirects.Body.ExceptionHandlers.Count!=0)throw new InvalidDataException("Unexpected binding redirect exception handlers.");
var additions=redirects.Body.Instructions.Where(i=>i.Operand is MethodReference m && m.Name=="Add" && m.DeclaringType.FullName=="System.Collections.Generic.Dictionary`2<System.String,System.Reflection.Assembly>").ToArray();
if(additions.Length!=2)throw new InvalidDataException("Unexpected serializer registration sites.");
foreach(string key in new[]{"LmpClient.XmlSerializers, Culture=neutral, PublicKeyToken=null","LmpCommon.XmlSerializers, Culture=neutral, PublicKeyToken=null"}) {
    var literal=redirects.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr && Equals(i.Operand,key));
    if(literal.Next?.OpCode!=OpCodes.Ldstr || !Equals(literal.Next.Operand,"System.Xml") || literal.Next.Next?.Operand is not MethodReference load || load.FullName!="System.Reflection.Assembly System.Reflection.Assembly::Load(System.String)" || !additions.Contains(literal.Next.Next.Next))
        throw new InvalidDataException("Unexpected serializer registration sequence.");
    var add=(MethodReference)literal.Next.Next.Next.Operand;
    var contains=new MethodReference("ContainsKey",module.TypeSystem.Boolean,add.DeclaringType){HasThis=true};
    contains.Parameters.Add(new ParameterDefinition(add.Parameters[0].ParameterType));
    var after=literal.Next.Next.Next.Next??throw new InvalidDataException("Missing registration continuation.");
    var il=redirects.Body.GetILProcessor();
    foreach(var guard in new[]{Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Ldstr,key),Instruction.Create(OpCodes.Callvirt,contains),
        Instruction.Create(OpCodes.Brfalse,literal),Instruction.Create(OpCodes.Pop),Instruction.Create(OpCodes.Br,after)})il.InsertBefore(literal,guard);
}
allowed.Add(redirects.FullName);
// Prefix before Singleton assignment, binding redirects, subscriptions or network
// startup. Rejected duplicate components cannot execute shared cleanup callbacks.
var main=module.GetType("LmpClient.MainSystem");
foreach(string name in new[]{"Awake","OnDestroy","OnApplicationQuit"}) {
    var method=main.Methods.Single(m=>m.Name==name && !m.HasParameters);
    var first=method.Body.Instructions[0];var il=method.Body.GetILProcessor();
    var guard=startup.Methods.Single(m=>m.Name==(name=="Awake"?"AllowAwake":"Owns"));
    foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Call,guard),Instruction.Create(OpCodes.Brtrue,first),Instruction.Create(OpCodes.Ret)})il.InsertBefore(first,instruction);
    allowed.Add(method.FullName);
}
// Inserting guards can exceed the range of an original short branch.
foreach(var i in redirects.Body.Instructions) {
    if(i.OpCode==OpCodes.Brfalse_S)i.OpCode=OpCodes.Brfalse;
    if(i.OpCode==OpCodes.Brtrue_S)i.OpCode=OpCodes.Brtrue;
    if(i.OpCode==OpCodes.Br_S)i.OpCode=OpCodes.Br;
}
foreach(var reference in module.AssemblyReferences.Where(r=>r.Name==donor.Name.Name || r.Name==client.Name.Name).ToArray())module.AssemblyReferences.Remove(reference);
Directory.CreateDirectory(output);
module.Mvid=new Guid(SHA256.HashData(File.ReadAllBytes(args[2])).AsSpan(0,16));
string clientOutput=Path.Combine(output,"LmpClient.dll");client.Write(clientOutput);
using var verified=AssemblyDefinition.ReadAssembly(clientOutput,new ReaderParameters{AssemblyResolver=resolver});
using var pristine=AssemblyDefinition.ReadAssembly(args[0],new ReaderParameters{AssemblyResolver=resolver});
var allVerified=verified.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
var changed=new List<string>();int preserved=0;
foreach(var method in pristine.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods)) {
    if(!allVerified.TryGetValue(method.FullName,out var next))throw new InvalidDataException("Original method removed: "+method.FullName);
    if(Signature(method)!=Signature(next)) {
        if(!allowed.Contains(method.FullName))throw new InvalidDataException("Unrelated client method changed: "+method.FullName);
        changed.Add(method.FullName);
    }else preserved++;
}
int resolved=0;
foreach(var method in allVerified.Values.Where(m=>allowed.Contains(m.FullName) && m.HasBody)) {
    foreach(var variable in method.Body.Variables)ResolveType(variable.VariableType);
    foreach(var handler in method.Body.ExceptionHandlers)if(handler.CatchType!=null)ResolveType(handler.CatchType);
    foreach(var instruction in method.Body.Instructions) {
        if(instruction.Operand is MethodReference called){if(called.Resolve()==null)throw new InvalidDataException("Unresolved: "+called.FullName);resolved++;}
        if(instruction.Operand is FieldReference field){if(field.Resolve()==null)throw new InvalidDataException("Unresolved: "+field.FullName);resolved++;}
        if(instruction.Operand is TypeReference type)ResolveType(type);
    }
}
void ResolveType(TypeReference type) {
    if(type is GenericParameter)return;
    if(type is GenericInstanceType generic)foreach(var argument in generic.GenericArguments)ResolveType(argument);
    if(type.Resolve()==null)throw new InvalidDataException("Unresolved type: "+type.FullName);resolved++;
}
if(verified.MainModule.AssemblyReferences.Any(r=>r.Name==donor.Name.Name || r.Name==client.Name.Name || r.Name=="System.Private.CoreLib"))throw new InvalidDataException("Unexpected donor/self/runtime assembly reference.");
if(Hash(args[0])!=clientHash || Hash(args[1])!=coreHash)throw new InvalidDataException("Baseline changed during graft.");
int readyTimeout;
using(var core=AssemblyDefinition.ReadAssembly(args[1])) {
    var launch=core.MainModule.GetType("ApfCore.ApfPostExitLauncher").Methods.Single(m=>m.Name=="Launch");
    readyTimeout=(int)launch.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldc_I4 && Equals(i.Operand,20000)).Operand;
}
var report=new{version="kesas-rc25",baselineClientSha256=clientHash,baselineCoreSha256=coreHash,
    donorSha256=Hash(args[2]),engineSha256=Hash(enginePath),clientSha256=Hash(clientOutput),changedMethods=changed.OrderBy(m=>m).ToArray(),
    preservedMethods=preserved,newMethods=allVerified.Keys.Except(pristine.MainModule.Types.SelectMany(Flatten).SelectMany(t=>t.Methods).Select(m=>m.FullName)).ToArray(),
    resolvedReferences=resolved,serializerGuards=2,coreWritten=false,baselineReadyTimeoutMilliseconds=readyTimeout,requiredReadyTimeoutMilliseconds=600000,
    canonicalStartupGuard="Awake ownership plus exact-path AddonLoader.StartAddon prefix; noncanonical LmpClient addons are skipped before component creation; rejected components cannot run canonical cleanup.",
    startupGuardLimitation="Cannot undo legacy Awake that ran before the canonical RC25 Awake; approved duplicate-folder cleanup remains required.",
    handshakeScanMethod=fetchAndScan.FullName,handshakeScanOriginalOffset=$"IL_{connectionScanOffset:x4}",handshakeScanReplacedCalls=1,
    handshakeScanTarget=connectionScan.FullName,finalCoreApplyScanUnchanged=true,
    integrationRequirement="Main-owned core patcher must set ApfPostExitLauncher.Launch timeout to 600000 milliseconds; silent helper owns apply and relaunch."};
File.WriteAllText(Path.Combine(output,"audit.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS RC25: {changed.Count} existing methods changed; {preserved} methods preserved; {resolved} references resolved; two idempotent serializer guards.");
Console.WriteLine("LmpClient.dll SHA256 "+report.clientSha256);
Console.WriteLine("CORE NOT WRITTEN: .ready timeout 20000ms requires main-owned 600000ms patch.");

static string Signature(MethodDefinition method)
{
    string metadata=$"{method.Attributes}|{method.ImplAttributes}|{method.CallingConvention}|{method.ReturnType}|{string.Join(",",method.Parameters.Select(p=>$"{p.Attributes}:{p.ParameterType}"))}";
    if(!method.HasBody)return metadata+"|none";
    var indexes=method.Body.Instructions.Select((i,n)=>(i,n)).ToDictionary(x=>x.i,x=>x.n);
    string At(Instruction? i)=>i==null?"end":indexes[i].ToString();
    string Operand(object? value)=>value switch{null=>"",Instruction i=>"branch:"+At(i),Instruction[] a=>"branches:"+string.Join(",",a.Select(At)),
        VariableDefinition v=>"local:"+v.Index,ParameterDefinition p=>"argument:"+p.Index,_=>value.ToString()??""};
    return metadata+"|"+method.Body.InitLocals+"|"+string.Join(",",method.Body.Variables.Select(v=>v.VariableType.FullName))+"|"+
        string.Join("\n",method.Body.Instructions.Select(i=>i.OpCode.Name+" "+Operand(i.Operand)))+"|"+
        string.Join("\n",method.Body.ExceptionHandlers.Select(h=>$"{h.HandlerType}:{h.CatchType}:{At(h.TryStart)}:{At(h.TryEnd)}:{At(h.HandlerStart)}:{At(h.HandlerEnd)}:{At(h.FilterStart)}"));
}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes)foreach(var child in Flatten(nested))yield return child;}
