using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length is not (4 or 5)) throw new ArgumentException("MODE PINNED_INPUT DONOR_OR_DASH NEW_OUTPUT [kesas-rc49]");
string mode=args[0], input=Path.GetFullPath(args[1]), output=Path.GetFullPath(args[3]);
string release=args.Length==5?args[4]:"kesas-rc48";
if(release is not ("kesas-rc48" or "kesas-rc49"))throw new IOException("Unreviewed target release");
var pins=new Dictionary<string,string>{
 ["personal"]="9a167319ed463f90f019cd362fa9b77c9267f10ab30d37378fba432f206efce1",
 ["client"]="89bb44a60fb5def1193ee6aefcfc5dd4dcdd3a40394d9ff52e4ff405a2eddb67",
 ["clock"]="ff3a6c99ef8a8a076ca77c0344788f7f8b59110cdb959b81537e2c2bc745dea9",
 ["store"]="d37641645592b80b4e22065d813e65c68ba35b5cbd258b4a0b118d38109df59d",
 ["server"]="cc6112afa3af89d9fab6234c9413b5c3a8114ba2cda0430f15af138ca2adf9df"};
if(release=="kesas-rc49"){
 pins["personal"]="40871c4e16a31215cf8abb46d5477aba4b6bf890730d2d377d73a64c38f46ff2";
 pins["client"]="9f6e275f2e8d3537b5fda7c7b61cfe750de99059a94ac2ffda92e0f53dfa9eee";
 pins["server"]="5793258438c9bfa078f3f4187c9f302a78445537229fdf915b630aef91e94356";
}
if(mode is not ("personal" or "client" or "server")||Hash(input)!=pins[mode]||input==output||File.Exists(output))throw new IOException("Unreviewed input or existing output");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED")??throw new IOException("KSP references required"));
using var target=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});var module=target.MainModule;
var priorTypes=module.Types.SelectMany(All).ToArray();var before=priorTypes.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,Image);
var beforeFields=priorTypes.SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName,f=>f.Attributes+":"+f.Constant);
var resources=module.Resources.Select(ResourceImage).ToArray();var references=module.AssemblyReferences.Select(r=>r.FullName).ToArray();
var allowed=new HashSet<string>();var replaced=new HashSet<string>();var addedTypes=new List<string>();
if(mode is "personal" or "store" or "server"){
 using var donor=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
 var roots=mode=="personal"?new[]{"Aetherfront.Personal.Client.ScienceOutbox","Aetherfront.Personal.Client.ScienceEvents","Aetherfront.Personal.Client.SubjectHistory","Aetherfront.Personal.Client.RecoverySubjects"}:release=="kesas-rc49"&&mode=="server"?new[]{"Aetherfront.Personal.Server.OperatorScienceCommands"}:Array.Empty<string>();
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
 CustomAttributeArgument Argument(CustomAttributeArgument value)=>new(Type(value.Type),value.Value switch{CustomAttributeArgument a=>Argument(a),CustomAttributeArgument[] a=>a.Select(Argument).ToArray(),TypeReference t=>Type(t),_=>value.Value});
 void Attributes(ICustomAttributeProvider source,ICustomAttributeProvider destination){
  foreach(var a in source.CustomAttributes){var copy=new CustomAttribute(Method(a.Constructor));foreach(var x in a.ConstructorArguments)copy.ConstructorArguments.Add(Argument(x));foreach(var x in a.Fields)copy.Fields.Add(new CustomAttributeNamedArgument(x.Name,Argument(x.Argument)));foreach(var x in a.Properties)copy.Properties.Add(new CustomAttributeNamedArgument(x.Name,Argument(x.Argument)));destination.CustomAttributes.Add(copy);}
 }
 foreach(var s in sources){var n=typeMap[s.FullName];Attributes(s,n);foreach(var f in s.Fields)Attributes(f,fieldMap[f.FullName]);foreach(var m in s.Methods){var method=methodMap[m.FullName];Body(m,method);Attributes(m,method);for(int i=0;i<m.Parameters.Count;i++)Attributes(m.Parameters[i],method.Parameters[i]);}foreach(var p in s.Properties){var property=new PropertyDefinition(p.Name,p.Attributes,Type(p.PropertyType)){GetMethod=p.GetMethod==null?null:methodMap[p.GetMethod.FullName],SetMethod=p.SetMethod==null?null:methodMap[p.SetMethod.FullName]};Attributes(p,property);n.Properties.Add(property);}}
 if(mode=="personal"){
  if(release=="kesas-rc48"){var old=module.GetType("Aetherfront.Personal.Client.PersonalClient").Methods.Single(m=>m.Name=="RestoreSubject");Body(donor.MainModule.GetType(old.DeclaringType.FullName).Methods.Single(m=>m.Name==old.Name),old);allowed.Add(old.FullName);}
  var apply=module.GetType("Aetherfront.Personal.Client.PersonalClient").Methods.Single(m=>m.Name=="Apply");Body(donor.MainModule.GetType(apply.DeclaringType.FullName).Methods.Single(m=>m.Name==apply.Name),apply);allowed.Add(apply.FullName);
  var draw=module.GetType("Aetherfront.Personal.Client.PersonalClient").Methods.Single(m=>m.Name=="DrawSyncStatus");Body(donor.MainModule.GetType(draw.DeclaringType.FullName).Methods.Single(m=>m.Name==draw.Name),draw);allowed.Add(draw.FullName);
  // RC46 prepended Tick to Update. The replacement addon must retain that hook.
  var update=module.GetType("Aetherfront.Personal.Client.ScienceEvents").Methods.Single(m=>m.Name=="Update");update.Body.GetILProcessor().InsertBefore(update.Body.Instructions[0],Instruction.Create(OpCodes.Call,module.GetType("Aetherfront.Personal.Client.ResearchSync").Methods.Single(m=>m.Name=="Tick")));
 }
 if(mode=="store"){
  var write=module.GetType("Server.Aetherfront.AetherfrontServerStore").Methods.Single(m=>m.Name=="WriteAtomic");write.Body=new MethodBody(write);var il=write.Body.GetILProcessor();il.Append(Instruction.Create(OpCodes.Ldarg_0));il.Append(Instruction.Create(OpCodes.Ldarg_1));il.Append(Instruction.Create(OpCodes.Call,module.GetType("Server.Aetherfront.StateAtomic").Methods.Single(m=>m.Name=="Write")));il.Append(Instruction.Create(OpCodes.Ret));allowed.Add(write.FullName);
 }
 if(mode=="server"&&release=="kesas-rc48"){
  var old=module.GetType("Aetherfront.Personal.Server.PersonalServer").Methods.Single(m=>m.Name=="ReplyScience");Body(donor.MainModule.GetType(old.DeclaringType.FullName).Methods.Single(m=>m.Name==old.Name),old);allowed.Add(old.FullName);
 }
 if(mode=="server"&&release=="kesas-rc49"){
  var handler=module.GetType("Aetherfront.Personal.Server.PersonalServer").Methods.Single(m=>m.Name=="TryHandle");handler.Body.GetILProcessor().InsertBefore(handler.Body.Instructions[0],Instruction.Create(OpCodes.Call,module.GetType("Aetherfront.Personal.Server.OperatorScienceCommands").Methods.Single(m=>m.Name=="Install")));allowed.Add(handler.FullName);
 }
 // Rewrite references to replaced definitions throughout untouched code, while
 // keeping their semantic method images identical.
 foreach(var m in module.Types.SelectMany(All).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&!typeMap.ContainsKey(m.DeclaringType.FullName))){foreach(var i in m.Body.Instructions){if(i.Operand is MethodReference r&&replaced.Contains(r.DeclaringType.FullName))i.Operand=Method(r);else if(i.Operand is FieldReference f&&replaced.Contains(f.DeclaringType.FullName))i.Operand=Field(f);else if(i.Operand is TypeReference t&&replaced.Contains(t.FullName))i.Operand=Type(t);}}
}
if(mode=="client"){
 var getter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");var text=getter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);var constant=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");string expected=release=="kesas-rc49"?"kesas-rc48":"kesas-rc47";if(!Equals(text.Operand,expected)||!Equals(constant.Constant,expected))throw new IOException("Unexpected release");text.Operand=release;constant.Constant=release;allowed.Add(getter.FullName);
}
if(mode=="clock"){
 var checkpoint=module.GetType("Aetherfront.Continuity.ClockStore").Methods.Single(m=>m.Name=="Checkpoint");var tolerance=checkpoint.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldc_R8&&(double)i.Operand==.001d);tolerance.Operand=2d;allowed.Add(checkpoint.FullName);
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);target.Write(output);
using var verify=AssemblyDefinition.ReadAssembly(output,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});var actual=verify.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,Image);
var changes=before.Keys.Where(k=>!actual.ContainsKey(k)||actual[k]!=before[k]).ToArray();
if(changes.Any(k=>!allowed.Contains(k)&&!replaced.Any(t=>k.Contains(" "+t+"::",StringComparison.Ordinal))))throw new IOException("Unrelated methods changed: "+string.Join(";",changes));
if(allowed.Any(k=>!changes.Contains(k))||verify.Name.FullName!=target.Name.FullName||!verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources))throw new IOException("Audit contract failed");
var actualFields=verify.MainModule.Types.SelectMany(All).SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName,f=>f.Attributes+":"+f.Constant);
var fieldChanges=beforeFields.Keys.Where(k=>!actualFields.ContainsKey(k)||actualFields[k]!=beforeFields[k]).ToArray();
if(fieldChanges.Any(k=>!replaced.Any(t=>k.Contains(" "+t+"::",StringComparison.Ordinal))&&!(mode=="client"&&k.EndsWith("ApfClientBuildVersion::Value",StringComparison.Ordinal))&&!(mode=="server"&&k.EndsWith("ScienceNotices::DelayMilliseconds",StringComparison.Ordinal))))throw new IOException("Unrelated field changed");
var newRefs=verify.MainModule.AssemblyReferences.Select(r=>r.FullName).Except(references).ToArray();if(newRefs.Any(r=>r.Split(',')[0] is not("System.Runtime" or "System.Threading.Thread" or "System.Diagnostics.TraceSource")&&!(release=="kesas-rc49"&&mode=="server"&&r=="System.Collections.Concurrent, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")))throw new IOException("Unexpected dependency: "+string.Join(";",newRefs));
if(mode=="personal"){
 var addon=verify.MainModule.GetType("Aetherfront.Personal.Client.ScienceEvents");var markers=addon.CustomAttributes.Where(a=>a.AttributeType.FullName=="KSPAddon").ToArray();
 if(markers.Length!=1||markers[0].ConstructorArguments.Count!=2||Convert.ToInt32(markers[0].ConstructorArguments[0].Value)!=-2||!Equals(markers[0].ConstructorArguments[1].Value,true))throw new IOException("Science collector cannot be discovered by KSP startup");
}
var audit=new{version=release,mode,inputSha256=Hash(input),outputSha256=Hash(output),identityPreserved=true,allOtherMethodsUnchanged=true,changes,fieldChanges,replacedTypes=replaced,addedTypes,newReferences=newRefs};File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(audit,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(audit));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?Convert.ToHexString(SHA256.HashData(e.GetResourceData())):r.ToString());
static string Image(MethodDefinition m){if(!m.HasBody)return "none";var c=m.Body.Instructions;string O(object? o)=>o switch{null=>"",Instruction b=>"@"+c.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(c.IndexOf)),IFormattable v=>v.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return string.Join("|",c.Select(i=>i.OpCode+":"+O(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+c.IndexOf(h.TryStart)+":"+c.IndexOf(h.TryEnd)+":"+c.IndexOf(h.HandlerStart)+":"+c.IndexOf(h.HandlerEnd)+":"+c.IndexOf(h.FilterStart)+":"+h.CatchType));}
