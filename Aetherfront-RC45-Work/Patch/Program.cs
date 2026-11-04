using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length<3||args[0] is not("ground" or "orbit" or "stamp"))throw new ArgumentException("ground|orbit BASELINE REPAIR OUTPUT | stamp BASELINE OUTPUT");
bool ground=args[0]=="ground",orbit=args[0]=="orbit",graft=ground||orbit;if(args.Length!=(graft?4:3))throw new ArgumentException("Wrong argument count");
string input=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[^1]);
string expected=ground?"188ab4582a1eccc840a205c0280491dcf1aa5828299121ba1d0684a8f97abeba":"e90c85067eefc8baf99589fd8625df22d373e0abcb37f4374e31a0ce14aa1b31";
if(input==output||Hash(input)!=expected||File.Exists(output))throw new IOException("Unreviewed baseline or existing output");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
string managed=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED")??throw new IOException("KSP managed references required");resolver.AddSearchDirectory(managed);
string harmony=Environment.GetEnvironmentVariable("AETHERFRONT_HARMONY_DIRECTORY")??throw new IOException("Harmony reference directory required");resolver.AddSearchDirectory(harmony);
using var original=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{InMemory=true,AssemblyResolver=resolver});var module=original.MainModule;
var types=module.Types.SelectMany(All).ToArray();var methods=types.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);var images=methods.ToDictionary(x=>x.Key,x=>Image(x.Value));
var fields=types.SelectMany(t=>t.Fields).Select(FieldImage).ToArray();var resources=module.Resources.Select(ResourceImage).ToArray();var refs=module.AssemblyReferences.Select(r=>r.FullName).ToArray();
var changedMethods=new List<string>();var injected=new List<Instruction>();string? helperSha=null;var helperImages=new Dictionary<string,string>();
if(graft){
 if(original.Name.Name!=(ground?"AetherfrontContinuityClient":"LmpClient")||original.Name.HasPublicKey)throw new IOException("Wrong repair target");
 helperSha=Hash(args[2]);using var repair=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
 var source=repair.MainModule.GetType("Aetherfront.Continuity.Client."+(ground?"GroundClearance":"LocalOrbitTracking"));
 if(source==null||source.HasNestedTypes||source.HasGenericParameters||source.Methods.Any(m=>m.HasGenericParameters)||module.GetType(source.FullName)!=null)throw new IOException("Unexpected ground repair shape");
 var target=new TypeDefinition(source.Namespace,source.Name,source.Attributes,module.ImportReference(source.BaseType));module.Types.Add(target);
 var fieldMap=new Dictionary<FieldDefinition,FieldDefinition>();var methodMap=new Dictionary<MethodDefinition,MethodDefinition>();
 TypeReference Type(TypeReference t){
  if(t.FullName==source.FullName)return target;
  if(t is GenericInstanceType g){var n=new GenericInstanceType(Type(g.ElementType));foreach(var argument in g.GenericArguments)n.GenericArguments.Add(Type(argument));return n;}
  if(t is ByReferenceType b)return new ByReferenceType(Type(b.ElementType));
  if(t is ArrayType a)return new ArrayType(Type(a.ElementType),a.Rank);
  if(t is PointerType p)return new PointerType(Type(p.ElementType));
  if(t is GenericParameter)return t;
  if(t.Scope?.Name==original.Name.Name||t.Scope?.Name==module.Name){var local=types.SingleOrDefault(x=>x.FullName==t.FullName);if(local!=null)return local;}
  return module.ImportReference(t);
 }
 foreach(var f in source.Fields){var n=new FieldDefinition(f.Name,f.Attributes,Type(f.FieldType));if(f.HasConstant)n.Constant=f.Constant;target.Fields.Add(n);fieldMap.Add(f,n);}
 foreach(var m in source.Methods){var n=new MethodDefinition(m.Name,m.Attributes,Type(m.ReturnType)){ImplAttributes=m.ImplAttributes};foreach(var p in m.Parameters)n.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Type(p.ParameterType)));target.Methods.Add(n);methodMap.Add(m,n);}
 MethodReference Method(MethodReference m){
  if(m.DeclaringType.FullName==source.FullName)return methodMap.Single(x=>x.Key.FullName==m.FullName).Value;
  if(m.DeclaringType.GetElementType().Scope?.Name==original.Name.Name){
   var n=new MethodReference(m.Name,Type(m.ReturnType),Type(m.DeclaringType)){HasThis=m.HasThis,ExplicitThis=m.ExplicitThis,CallingConvention=m.CallingConvention};
   foreach(var p in m.Parameters)n.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Type(p.ParameterType)));
   foreach(var p in m.GenericParameters)n.GenericParameters.Add(new GenericParameter(p.Name,n));
   return n;
  }
  return module.ImportReference(m);
 }
 FieldReference Field(FieldReference f){if(f.DeclaringType.FullName==source.FullName)return fieldMap.Single(x=>x.Key.FullName==f.FullName).Value;if(f.DeclaringType.GetElementType().Scope?.Name==original.Name.Name)return new FieldReference(f.Name,Type(f.FieldType),Type(f.DeclaringType));return module.ImportReference(f);}
 foreach(var m in source.Methods){
  if(!m.HasBody)throw new IOException("Unexpected external helper method");var n=methodMap[m];n.Body=new MethodBody(n){InitLocals=m.Body.InitLocals,MaxStackSize=m.Body.MaxStackSize};
  var locals=m.Body.Variables.ToDictionary(v=>v,v=>new VariableDefinition(Type(v.VariableType)));foreach(var v in m.Body.Variables)n.Body.Variables.Add(locals[v]);
  var code=m.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));foreach(var i in m.Body.Instructions)n.Body.Instructions.Add(code[i]);
  foreach(var i in m.Body.Instructions){var c=code[i];c.OpCode=i.OpCode;c.Operand=i.Operand switch{Instruction b=>code[b],Instruction[] a=>a.Select(b=>code[b]).ToArray(),VariableDefinition v=>locals[v],ParameterDefinition p=>n.Parameters[p.Index],MethodReference r=>Method(r),FieldReference f=>Field(f),TypeReference t=>Type(t),CallSite=>throw new IOException("Indirect helper call"),_=>i.Operand};}
  Instruction? At(Instruction? i)=>i==null?null:code[i];
  foreach(var e in m.Body.ExceptionHandlers)n.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){TryStart=At(e.TryStart),TryEnd=At(e.TryEnd),HandlerStart=At(e.HandlerStart),HandlerEnd=At(e.HandlerEnd),FilterStart=At(e.FilterStart),CatchType=e.CatchType==null?null:Type(e.CatchType)});
  if(Image(n)!=Image(m))throw new IOException("Grafted helper differs from normalized donor: "+m.FullName);
  helperImages.Add(n.FullName,Image(n));
 }
 if(ground){
 var awake=module.GetType("Aetherfront.Continuity.Client.SimulationAuthority").Methods.Single(m=>m.Name=="Awake");changedMethods.Add(awake.FullName);
 var harmonyLocal=awake.Body.Variables.Single(v=>v.VariableType.FullName=="HarmonyLib.Harmony");
 var ready=awake.Body.Instructions.Single(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name=="installed");
 var value=ready.Previous;if(value.OpCode!=OpCodes.Ldc_I4_1)throw new IOException("Unexpected authority readiness assignment");
 var load=Instruction.Create(OpCodes.Ldloc,harmonyLocal);var call=Instruction.Create(OpCodes.Call,target.Methods.Single(m=>m.Name=="Install"));
 foreach(var branch in awake.Body.Instructions){if(ReferenceEquals(branch.Operand,value))branch.Operand=load;if(branch.Operand is Instruction[] labels)for(int i=0;i<labels.Length;i++)if(labels[i]==value)labels[i]=load;}
 awake.Body.GetILProcessor().InsertBefore(value,load);awake.Body.GetILProcessor().InsertBefore(value,call);injected.Add(load);injected.Add(call);
 // Widen branches only in the changed method, never relying on old byte offsets.
 foreach(var i in awake.Body.Instructions)i.OpCode=i.OpCode.Code switch{Code.Br_S=>OpCodes.Br,Code.Brfalse_S=>OpCodes.Brfalse,Code.Brtrue_S=>OpCodes.Brtrue,Code.Beq_S=>OpCodes.Beq,Code.Bge_S=>OpCodes.Bge,Code.Bgt_S=>OpCodes.Bgt,Code.Ble_S=>OpCodes.Ble,Code.Blt_S=>OpCodes.Blt,Code.Bne_Un_S=>OpCodes.Bne_Un,Code.Bge_Un_S=>OpCodes.Bge_Un,Code.Bgt_Un_S=>OpCodes.Bgt_Un,Code.Ble_Un_S=>OpCodes.Ble_Un,Code.Blt_Un_S=>OpCodes.Blt_Un,Code.Leave_S=>OpCodes.Leave,_=>i.OpCode};
 }else{
  var prefix=types.SelectMany(t=>t.Methods).Single(m=>m.Name=="PrefixTrackRigidbody");changedMethods.Add(prefix.FullName);
  if(prefix.IsStatic==false||!prefix.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(new[]{"OrbitDriver","CelestialBody","System.Double","System.Double&"})||prefix.ReturnType.FullName!="System.Boolean")throw new IOException("Unexpected orbit prefix signature");
  var first=prefix.Body.Instructions[0];var il=prefix.Body.GetILProcessor();
  foreach(var i in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Call,target.Methods.Single(m=>m.Name=="UseStockKinematicTracking")),Instruction.Create(OpCodes.Brfalse,first),Instruction.Create(OpCodes.Ldc_I4_1),Instruction.Create(OpCodes.Ret)})il.InsertBefore(first,i);
 }
}
if(!ground){
 if(original.Name.Name!="LmpClient"||original.Name.HasPublicKey)throw new IOException("Wrong client");
 var getter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");var text=getter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);
 var value=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");if(!Equals(text.Operand,"kesas-rc44")||!Equals(value.Constant,"kesas-rc44"))throw new IOException("Unexpected client revision");
 text.Operand="kesas-rc45";value.Constant="kesas-rc45";changedMethods.Add(getter.FullName);
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);original.Write(output);
using var verified=AssemblyDefinition.ReadAssembly(output,new ReaderParameters{AssemblyResolver=resolver});var check=verified.MainModule.Types.SelectMany(All).ToArray();var checkMethods=check.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
if(verified.Name.FullName!=original.Name.FullName||verified.Name.HasPublicKey||!verified.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources))throw new IOException("Identity/resource changed");
var changed=methods.Keys.Where(k=>Image(checkMethods[k])!=images[k]).ToArray();if(!changed.Order().SequenceEqual(changedMethods.Order()))throw new IOException("Unrelated original method changed");
var originalFields=check.Where(t=>types.Any(p=>p.FullName==t.FullName)).SelectMany(t=>t.Fields).Select(FieldImage).ToArray();if(originalFields.Where((x,i)=>x!=fields[i]).Count()!=(ground?0:1))throw new IOException("Original field changed");
var addedTypes=check.Select(t=>t.FullName).Except(types.Select(t=>t.FullName)).ToArray();if(addedTypes.Length!=(graft?1:0))throw new IOException("Unexpected type additions");
var addedReferences=verified.MainModule.AssemblyReferences.Select(r=>r.FullName).Except(refs).ToArray();
if(addedReferences.Any(r=>!r.StartsWith("UnityEngine.PhysicsModule,",StringComparison.Ordinal))||refs.Except(verified.MainModule.AssemblyReferences.Select(r=>r.FullName)).Any())throw new IOException("Unexpected dependency: "+string.Join(";",addedReferences));
if(verified.MainModule.AssemblyReferences.Any(r=>(r.Name is "AetherfrontGroundClearance" or "AetherfrontLocalOrbitTracking"||r.Name==original.Name.Name)&&!refs.Contains(r.FullName)))throw new IOException("New helper/self assembly reference leaked");
var helperDifferences=helperImages.Where(p=>!checkMethods.ContainsKey(p.Key)||Image(checkMethods[p.Key])!=p.Value).ToArray();
if(helperDifferences.Length>0){File.WriteAllText(output+".graft-diff.json",JsonSerializer.Serialize(helperDifferences.Select(p=>new{method=p.Key,before=p.Value,after=checkMethods.ContainsKey(p.Key)?Image(checkMethods[p.Key]):"missing"})));throw new IOException("Grafted helper method body changed");}
var result=new{mode=args[0],version="kesas-rc45",inputSha256=Hash(input),outputSha256=Hash(output),helperSha256=helperSha,identityPreserved=true,originalMethods=methods.Count,changed,allOtherMethodsUnchanged=true,groundInstallBeforeAuthorityReady=ground,addedTypes,addedReferences};
File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(result));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string FieldImage(FieldDefinition f)=>f.FullName+":"+f.Attributes+":"+f.Constant;
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?Convert.ToHexString(SHA256.HashData(e.GetResourceData())):r.ToString());
static string TypeImage(TypeReference t)=>t switch{GenericParameter p=>(p.Type==GenericParameterType.Method?"!!":"!")+p.Position,GenericInstanceType g=>TypeImage(g.ElementType)+"<"+string.Join(",",g.GenericArguments.Select(TypeImage))+">",ByReferenceType b=>TypeImage(b.ElementType)+"&",ArrayType a=>TypeImage(a.ElementType)+"["+new string(',',a.Rank-1)+"]",_=>t.FullName};
static string MethodImage(MethodReference r)=>TypeImage(r.ReturnType)+" "+TypeImage(r.DeclaringType)+"::"+r.Name+(r is GenericInstanceMethod g?"<"+string.Join(",",g.GenericArguments.Select(TypeImage))+">":"")+"("+string.Join(",",r.Parameters.Select(p=>TypeImage(p.ParameterType)))+") this:"+r.HasThis+":"+r.ExplicitThis+":"+r.CallingConvention;
static string Image(MethodDefinition m){var signature=MethodImage(m)+":"+m.Attributes+":"+m.ImplAttributes;if(!m.HasBody)return signature+" none";var code=m.Body.Instructions;string Operand(object? o)=>o switch{null=>"",Instruction b=>"@"+code.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(code.IndexOf)),MethodReference r=>MethodImage(r),FieldReference f=>TypeImage(f.FieldType)+" "+TypeImage(f.DeclaringType)+"::"+f.Name,TypeReference t=>TypeImage(t),IFormattable n=>n.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return signature+" "+string.Join("|",code.Select(i=>i.OpCode+":"+Operand(i.Operand)))+" init:"+m.Body.InitLocals+" locals:"+string.Join(",",m.Body.Variables.Select(v=>TypeImage(v.VariableType)))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)+":"+code.IndexOf(h.FilterStart)+":"+h.CatchType));}
