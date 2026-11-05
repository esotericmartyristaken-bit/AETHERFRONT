using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 4) throw new ArgumentException("MODE PINNED_INPUT DONOR_OR_DASH NEW_OUTPUT");
string mode=args[0], input=Path.GetFullPath(args[1]), output=Path.GetFullPath(args[3]);
var pins=new Dictionary<string,string>{
 ["personal"]="902cbf05b356f98e47ba4f1c3b4f5e6a6e22d33f6ec6190760d59cfab2f3ee8c",
 ["client"]="7332406054ce7c590e918ba130df433b543fd3d329b4198877f74790f62a58cb",
 ["clock"]="ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9",
 ["store"]="d37641645592b80b4e22065d813e65c68ba35b5cbd258b4a0b118d38109df59d",
 ["server"]="da7be39a359b58fc32b5f7143546c0a8c30e5f620f42d9e587ee4d28826cfae7"};
if(!pins.ContainsKey(mode)||Hash(input)!=pins[mode]||input==output||File.Exists(output))throw new IOException("Unreviewed input or existing output");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED")??throw new IOException("KSP references required"));
using var target=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});var module=target.MainModule;
var priorTypes=module.Types.SelectMany(All).ToArray();var before=priorTypes.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,Image);
var beforeFields=priorTypes.SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName,f=>f.Attributes+":"+f.Constant);
var resources=module.Resources.Select(ResourceImage).ToArray();var references=module.AssemblyReferences.Select(r=>r.FullName).ToArray();
var allowed=new HashSet<string>();var replaced=new HashSet<string>();var addedTypes=new List<string>();
if(mode is "personal" or "store" or "server"){
 using var donor=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
 var roots=mode=="personal"?new[]{"Aetherfront.Personal.Client.ResearchQueue","Aetherfront.Personal.Client.ResearchSync","Aetherfront.Personal.Client.ScienceOutbox","Aetherfront.Personal.Client.ScienceEvents"}:mode=="store"?new[]{"Server.Aetherfront.StateAtomic"}:Array.Empty<string>();
 var sources=roots.Select(n=>donor.MainModule.GetType(n)??throw new IOException("Missing donor type "+n)).SelectMany(All).ToArray();
 var typeMap=new Dictionary<string,TypeDefinition>();var methodMap=new Dictionary<string,MethodDefinition>();var fieldMap=new Dictionary<string,FieldDefinition>();
 bool Local(TypeReference t)=>t.Scope?.Name==donor.Name.Name||t.Scope?.Name==donor.MainModule.Name||t.Scope?.Name==target.Name.Name||t.Scope?.Name==module.Name;
 foreach(var name in roots){var old=module.GetType(name);if(old!=null){foreach(var t in All(old))replaced.Add(t.FullName);module.Types.Remove(old);}else addedTypes.Add(name);}
 foreach(var source in sources){var copy=new TypeDefinition(source.Namespace,source.Name,source.Attributes,null);typeMap.Add(source.FullName,copy);if(source.IsNested)typeMap[source.DeclaringType.FullName].NestedTypes.Add(copy);else module.Types.Add(copy);}
 TypeReference Type(TypeReference t){
  if(t is GenericParameter)return t;
  if(t is GenericInstanceType g){var n=new GenericInstanceType(Type(g.ElementType));foreach(var argument in g.GenericArguments)n.GenericArguments.Add(Type(argument));return n;}
  if(t is ByReferenceType b)return new ByReferenceType(Type(b.ElementType));if(t is ArrayType a)return new ArrayType(Type(a.ElementType),a.Rank);
  if(t is PointerType p)return new PointerType(Type(p.ElementType));
  if(typeMap.TryGetValue(t.FullName,out var injected))return injected;
  if(Local(t)){var local=module.Types.SelectMany(All).SingleOrDefault(x=>x.FullName==t.FullName);if(local!=null)return local;}
  return module.ImportReference(t);
 }
 foreach(var s in sources){var n=typeMap[s.FullName];n.BaseType=Type(s.BaseType);foreach(var f in s.Fields){var c=new FieldDefinition(f.Name,f.Attributes,Type(f.FieldType));if(f.HasConstant)c.Constant=f.Constant;n.Fields.Add(c);fieldMap.Add(f.FullName,c);}foreach(var m in s.Methods){if(m.HasGenericParameters)throw new IOException("Generic donor definition unsupported");var c=new MethodDefinition(m.Name,m.Attributes,Type(m.ReturnType)){ImplAttributes=m.ImplAttributes};foreach(var p in m.Parameters)c.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Type(p.ParameterType)));n.Methods.Add(c);methodMap.Add(m.FullName,c);}}
 MethodReference Method(MethodReference m){
  if(m is GenericInstanceMethod g){var n=new GenericInstanceMethod(Method(g.ElementMethod));foreach(var a in g.GenericArguments)n.GenericArguments.Add(Type(a));return n;}
  if(methodMap.TryGetValue(m.FullName,out var mapped))return mapped;
  if(Local(m.DeclaringType)){var local=module.Types.SelectMany(All).SelectMany(t=>t.Methods).SingleOrDefault(x=>x.FullName==m.FullName);if(local!=null)return local;}
  var copy=new MethodReference(m.Name,Type(m.ReturnType),Type(m.DeclaringType)){HasThis=m.HasThis,ExplicitThis=m.ExplicitThis,CallingConvention=m.CallingConvention};
  foreach(var p in m.GenericParameters)copy.GenericParameters.Add(new GenericParameter(p.Name,copy));foreach(var p in m.Parameters)copy.Parameters.Add(new ParameterDefinition(Type(p.ParameterType)));return module.ImportReference(copy);
 }
 FieldReference Field(FieldReference f){if(fieldMap.TryGetValue(f.FullName,out var mapped))return mapped;if(Local(f.DeclaringType)){var local=module.Types.SelectMany(All).SelectMany(t=>t.Fields).SingleOrDefault(x=>x.FullName==f.FullName);if(local!=null)return local;}return module.ImportReference(new FieldReference(f.Name,Type(f.FieldType),Type(f.DeclaringType)));}
 void Body(MethodDefinition s,MethodDefinition n){
  n.Body=new MethodBody(n){InitLocals=s.Body.InitLocals,MaxStackSize=s.Body.MaxStackSize};var locals=s.Body.Variables.ToDictionary(v=>v,v=>new VariableDefinition(Type(v.VariableType)));foreach(var v in s.Body.Variables)n.Body.Variables.Add(locals[v]);
  var code=s.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));foreach(var i in s.Body.Instructions)n.Body.Instructions.Add(code[i]);
  foreach(var i in s.Body.Instructions){var c=code[i];c.OpCode=i.OpCode;c.Operand=i.Operand switch{Instruction b=>code[b],Instruction[] a=>a.Select(b=>code[b]).ToArray(),VariableDefinition v=>locals[v],ParameterDefinition p=>n.Parameters[p.Index],MethodReference r=>Method(r),FieldReference f=>Field(f),TypeReference t=>Type(t),CallSite=>throw new IOException("Indirect donor call"),_=>i.Operand};}
  Instruction? At(Instruction? i)=>i==null?null:code[i];foreach(var e in s.Body.ExceptionHandlers)n.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){TryStart=At(e.TryStart),TryEnd=At(e.TryEnd),HandlerStart=At(e.HandlerStart),HandlerEnd=At(e.HandlerEnd),FilterStart=At(e.FilterStart),CatchType=e.CatchType==null?null:Type(e.CatchType)});
 }
 foreach(var s in sources){var n=typeMap[s.FullName];foreach(var m in s.Methods)Body(m,methodMap[m.FullName]);foreach(var p in s.Properties)n.Properties.Add(new PropertyDefinition(p.Name,p.Attributes,Type(p.PropertyType)){GetMethod=p.GetMethod==null?null:methodMap[p.GetMethod.FullName],SetMethod=p.SetMethod==null?null:methodMap[p.SetMethod.FullName]});var old=priorTypes.SingleOrDefault(t=>t.FullName==s.FullName);if(old!=null)foreach(var a in old.CustomAttributes)n.CustomAttributes.Add(a);}
 if(mode=="personal"){
  var old=module.GetType("Aetherfront.Personal.Client.PersonalClient").Methods.Single(m=>m.Name=="Apply");Body(donor.MainModule.GetType(old.DeclaringType.FullName).Methods.Single(m=>m.Name==old.Name),old);allowed.Add(old.FullName);
  // RC46 prepended Tick to Update. The replacement addon must retain that hook.
  var update=module.GetType("Aetherfront.Personal.Client.ScienceEvents").Methods.Single(m=>m.Name=="Update");update.Body.GetILProcessor().InsertBefore(update.Body.Instructions[0],Instruction.Create(OpCodes.Call,module.GetType("Aetherfront.Personal.Client.ResearchSync").Methods.Single(m=>m.Name=="Tick")));
 }
 if(mode=="store"){
  var write=module.GetType("Server.Aetherfront.AetherfrontServerStore").Methods.Single(m=>m.Name=="WriteAtomic");write.Body=new MethodBody(write);var il=write.Body.GetILProcessor();il.Append(Instruction.Create(OpCodes.Ldarg_0));il.Append(Instruction.Create(OpCodes.Ldarg_1));il.Append(Instruction.Create(OpCodes.Call,module.GetType("Server.Aetherfront.StateAtomic").Methods.Single(m=>m.Name=="Write")));il.Append(Instruction.Create(OpCodes.Ret));allowed.Add(write.FullName);
 }
 if(mode=="server"){
  var old=module.GetType("Aetherfront.Personal.Server.PersonalServer").Methods.Single(m=>m.Name=="TryHandle");Body(donor.MainModule.GetType(old.DeclaringType.FullName).Methods.Single(m=>m.Name==old.Name),old);allowed.Add(old.FullName);
  var schedule=module.Types.SelectMany(All).Single(t=>t.Name=="ScienceNotices").Methods.Single(m=>m.Name=="Schedule");var delay=schedule.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldc_I4&&(int)i.Operand==2000);delay.Operand=10000;allowed.Add(schedule.FullName);
  schedule.DeclaringType.Fields.Single(f=>f.Name=="DelayMilliseconds").Constant=10000;
 }
 // Rewrite references to replaced definitions throughout untouched code, while
 // keeping their semantic method images identical.
 foreach(var m in module.Types.SelectMany(All).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&!typeMap.ContainsKey(m.DeclaringType.FullName))){foreach(var i in m.Body.Instructions){if(i.Operand is MethodReference r&&replaced.Contains(r.DeclaringType.FullName))i.Operand=Method(r);else if(i.Operand is FieldReference f&&replaced.Contains(f.DeclaringType.FullName))i.Operand=Field(f);else if(i.Operand is TypeReference t&&replaced.Contains(t.FullName))i.Operand=Type(t);}}
}
if(mode=="client"){
 var getter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");var text=getter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);var constant=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");if(!Equals(text.Operand,"kesas-rc46")||!Equals(constant.Constant,"kesas-rc46"))throw new IOException("Unexpected release");text.Operand="kesas-rc47";constant.Constant="kesas-rc47";allowed.Add(getter.FullName);
}
if(mode=="clock"){
 var checkpoint=module.GetType("Aetherfront.Continuity.ClockStore").Methods.Single(m=>m.Name=="Checkpoint");var tolerance=checkpoint.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldc_R8&&(double)i.Operand==.001d);tolerance.Operand=2d;allowed.Add(checkpoint.FullName);
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);target.Write(output);
using var verify=AssemblyDefinition.ReadAssembly(output);var actual=verify.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,Image);
var changes=before.Keys.Where(k=>!actual.ContainsKey(k)||actual[k]!=before[k]).ToArray();
if(changes.Any(k=>!allowed.Contains(k)&&!replaced.Any(t=>k.Contains(" "+t+"::",StringComparison.Ordinal))))throw new IOException("Unrelated methods changed: "+string.Join(";",changes));
if(allowed.Any(k=>!changes.Contains(k))||verify.Name.FullName!=target.Name.FullName||!verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources))throw new IOException("Audit contract failed");
var actualFields=verify.MainModule.Types.SelectMany(All).SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName,f=>f.Attributes+":"+f.Constant);
var fieldChanges=beforeFields.Keys.Where(k=>!actualFields.ContainsKey(k)||actualFields[k]!=beforeFields[k]).ToArray();
if(fieldChanges.Any(k=>!replaced.Any(t=>k.Contains(" "+t+"::",StringComparison.Ordinal))&&!(mode=="client"&&k.EndsWith("ApfClientBuildVersion::Value",StringComparison.Ordinal))&&!(mode=="server"&&k.EndsWith("ScienceNotices::DelayMilliseconds",StringComparison.Ordinal))))throw new IOException("Unrelated field changed");
var newRefs=verify.MainModule.AssemblyReferences.Select(r=>r.FullName).Except(references).ToArray();if(newRefs.Any(r=>r.Split(',')[0] is not("System.Runtime" or "System.Threading.Thread" or "System.Diagnostics.TraceSource")))throw new IOException("Unexpected dependency: "+string.Join(";",newRefs));
var audit=new{version="kesas-rc47",mode,inputSha256=Hash(input),outputSha256=Hash(output),identityPreserved=true,allOtherMethodsUnchanged=true,changes,fieldChanges,replacedTypes=replaced,addedTypes,newReferences=newRefs};File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(audit,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(audit));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?Convert.ToHexString(SHA256.HashData(e.GetResourceData())):r.ToString());
static string Image(MethodDefinition m){if(!m.HasBody)return "none";var c=m.Body.Instructions;string O(object? o)=>o switch{null=>"",Instruction b=>"@"+c.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(c.IndexOf)),IFormattable v=>v.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return string.Join("|",c.Select(i=>i.OpCode+":"+O(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+c.IndexOf(h.TryStart)+":"+c.IndexOf(h.TryEnd)+":"+c.IndexOf(h.HandlerStart)+":"+c.IndexOf(h.HandlerEnd)+":"+c.IndexOf(h.FilterStart)+":"+h.CatchType));}
