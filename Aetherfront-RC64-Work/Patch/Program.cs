using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 4) throw new ArgumentException("MODE PINNED_INPUT DONOR_OR_DASH NEW_OUTPUT");
string mode=args[0], input=Path.GetFullPath(args[1]), output=Path.GetFullPath(args[3]);
var pins=new Dictionary<string,string>{
 ["personal"]="4424502ca38d7fcdd04e3f09642c0afea36e351068e7111b481eeb08463aea84",
 ["client"]="fe5ea047399c92516fdea52a1542273b77f6c797096f94e35e1d7ed893852058",
 ["clock"]="acbdb141064f6d8b91eab88503703cfd1d063b41d7c35cf23bc3159053b33f0c",
 ["reference"]="1a077ee3bb5887898bb741dbd088e9638a57d70b47237c0408d90c945e861586",
 ["server"]="1a077ee3bb5887898bb741dbd088e9638a57d70b47237c0408d90c945e861586"};
if(!pins.ContainsKey(mode)||Hash(input)!=pins[mode]||input==output||File.Exists(output))throw new IOException("Unreviewed input or existing output");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED")??throw new IOException("KSP references required"));
using var target=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});var module=target.MainModule;
if(mode=="reference"){
 foreach(var t in module.Types.SelectMany(All).Where(t=>t.Name!="<Module>")){
  if(t.IsNested)t.IsNestedPublic=true;else t.IsPublic=true;
  foreach(var f in t.Fields)f.IsPublic=true;
  foreach(var m in t.Methods)m.IsPublic=true;
 }
 Directory.CreateDirectory(Path.GetDirectoryName(output)!);target.Write(output);
 Console.WriteLine("Reference-only public metadata written; never ship this assembly: "+output);return;
}
var priorTypes=module.Types.SelectMany(All).ToArray();var before=priorTypes.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,Image);
var beforeFields=priorTypes.SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName,f=>f.Attributes+":"+f.Constant);
var resources=module.Resources.Select(ResourceImage).ToArray();var references=module.AssemblyReferences.Select(r=>r.FullName).ToArray();
var allowed=new HashSet<string>();var replaced=new HashSet<string>();var addedTypes=new List<string>();
if(mode is "server" or "client"){
 var wire=module.GetType("LmpCommon.Message.Data.Warp.WarpNewSubspaceMsgData");
 if(wire.Fields.Any(f=>f.Name=="BoundaryMissionTime"))throw new IOException("Mission-time wire already present");
 var field=new FieldDefinition("BoundaryMissionTime",FieldAttributes.Public,module.TypeSystem.Double);wire.Fields.Add(field);
 foreach(string operation in new[]{"InternalSerialize","InternalDeserialize"}){
  var method=wire.Methods.Single(m=>m.Name==operation);var end=method.Body.Instructions.Last(i=>i.OpCode==OpCodes.Ret);
  var buffer=method.Parameters[0].ParameterType.Resolve();while(buffer.FullName!="Lidgren.Network.NetBuffer")buffer=buffer.BaseType.Resolve();
  var transfer=operation=="InternalSerialize"?buffer.Methods.Single(m=>m.Name=="Write"&&m.Parameters.Count==1&&m.Parameters[0].ParameterType.FullName=="System.Double"):buffer.Methods.Single(m=>m.Name=="ReadDouble"&&m.Parameters.Count==0);
  var code=operation=="InternalSerialize"?new[]{Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,field),Instruction.Create(OpCodes.Callvirt,module.ImportReference(transfer))}:
   new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Callvirt,module.ImportReference(transfer)),Instruction.Create(OpCodes.Stfld,field)};
  foreach(var i in code)method.Body.GetILProcessor().InsertBefore(end,i);allowed.Add(method.FullName);
 }
 var size=wire.Methods.Single(m=>m.Name=="InternalGetMessageSize");var ret=size.Body.Instructions.Last();size.Body.GetILProcessor().InsertBefore(ret,Instruction.Create(OpCodes.Ldc_I4,8));size.Body.GetILProcessor().InsertBefore(ret,Instruction.Create(OpCodes.Add));allowed.Add(size.FullName);
}
if(mode is "personal" or "server" or "clock"){
 using var donor=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
 var roots=mode=="personal"?new[]{"Aetherfront.Personal.Client.ResearchQueue","Aetherfront.Personal.Client.ResearchSync"}:mode=="clock"?new[]{"Aetherfront.Continuity.Server.PersonalClockServer"}:new[]{"Aetherfront.Synchronization.Server.RecoveryJournal","Aetherfront.Synchronization.Server.BoundaryServer","Aetherfront.Synchronization.BoundaryWire"};
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
 if(mode=="server"){
  var store=module.GetType("Server.Aetherfront.AetherfrontServerStore");
  var helper=module.GetType("Aetherfront.Synchronization.Server.RecoveryJournal");
  var compact=store.Methods.Single(m=>m.Name=="CompactJournalLocked");
  compact.Body=new MethodBody(compact);var il=compact.Body.GetILProcessor();
  il.Append(Instruction.Create(OpCodes.Ldarg_0));il.Append(Instruction.Create(OpCodes.Ldarg_1));
  il.Append(Instruction.Create(OpCodes.Call,helper.Methods.Single(m=>m.Name=="Compact")));il.Append(Instruction.Create(OpCodes.Ret));allowed.Add(compact.FullName);
  var initialize=helper.Methods.Single(m=>m.Name=="Initialize");
  var constructor=store.Methods.Single(m=>m.IsConstructor&&!m.IsStatic&&m.Parameters.Count==3);
  foreach(var end in constructor.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray()){
   constructor.Body.GetILProcessor().InsertBefore(end,Instruction.Create(OpCodes.Ldarg_0));
   constructor.Body.GetILProcessor().InsertBefore(end,Instruction.Create(OpCodes.Call,initialize));
  }
  allowed.Add(constructor.FullName);
  var reader=module.GetType("Server.Message.VesselMsgReader");
  var position=reader.Methods.Single(m=>m.HasBody&&m.Body.Instructions.Any(i=>i.Operand is MethodReference call&&call.DeclaringType.FullName=="Aetherfront.Continuity.Server.PersonalClockServer"&&call.Name=="MayPersistPosition"));
  var predicate=position.Body.Instructions.Single(i=>i.Operand is MethodReference call&&call.Name=="MayPersistPosition");
  var oldPredicate=(MethodReference)predicate.Operand;
  var reported=new MethodReference("MayPersistReportedPosition",oldPredicate.ReturnType,oldPredicate.DeclaringType){HasThis=false};
  reported.Parameters.Add(new ParameterDefinition(oldPredicate.Parameters[0].ParameterType));
  var message=position.Body.Variables.Single(v=>v.VariableType.FullName=="LmpCommon.Message.Data.Vessel.VesselBaseMsgData");
  reported.Parameters.Add(new ParameterDefinition(message.VariableType));
  position.Body.GetILProcessor().InsertBefore(predicate,Instruction.Create(OpCodes.Ldloc,message));
  predicate.Operand=reported;allowed.Add(position.FullName);
  var keep=module.GetType("Server.System.WarpSystemReceiver").Methods.Single(m=>m.Name=="KeepCurrentSubspace");
  var entry=keep.Body.Instructions.First();var keepIl=keep.Body.GetILProcessor();
  foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldarg_1),Instruction.Create(OpCodes.Call,module.GetType("Aetherfront.Synchronization.Server.BoundaryServer").Methods.Single(m=>m.Name=="CanActivateBoundary")),Instruction.Create(OpCodes.Brfalse,entry),Instruction.Create(OpCodes.Ldc_I4_0),Instruction.Create(OpCodes.Ret)})keepIl.InsertBefore(entry,instruction);
  allowed.Add(keep.FullName);
 }
 // Rewrite references to replaced definitions throughout untouched code, while
 // keeping their semantic method images identical.
 foreach(var m in module.Types.SelectMany(All).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&!typeMap.ContainsKey(m.DeclaringType.FullName))){foreach(var i in m.Body.Instructions){if(i.Operand is MethodReference r&&replaced.Contains(r.DeclaringType.FullName))i.Operand=Method(r);else if(i.Operand is FieldReference f&&replaced.Contains(f.DeclaringType.FullName))i.Operand=Field(f);else if(i.Operand is TypeReference t&&replaced.Contains(t.FullName))i.Operand=Type(t);}}
}
if(mode=="client"){
 var getter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");var text=getter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);var constant=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");if(!Equals(text.Operand,"kesas-rc63")||!Equals(constant.Constant,"kesas-rc63"))throw new IOException("Unexpected release");text.Operand="kesas-rc64";constant.Constant="kesas-rc64";allowed.Add(getter.FullName);
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
var audit=new{version="kesas-rc64",mode,inputSha256=Hash(input),outputSha256=Hash(output),identityPreserved=true,allOtherMethodsUnchanged=true,changes,fieldChanges,replacedTypes=replaced,addedTypes,newReferences=newRefs};File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(audit,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(audit));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?Convert.ToHexString(SHA256.HashData(e.GetResourceData())):r.ToString());
static string Image(MethodDefinition m){if(!m.HasBody)return "none";var c=m.Body.Instructions;string O(object? o)=>o switch{null=>"",Instruction b=>"@"+c.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(c.IndexOf)),IFormattable v=>v.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return string.Join("|",c.Select(i=>i.OpCode+":"+O(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+c.IndexOf(h.TryStart)+":"+c.IndexOf(h.TryEnd)+":"+c.IndexOf(h.HandlerStart)+":"+c.IndexOf(h.HandlerEnd)+":"+c.IndexOf(h.FilterStart)+":"+h.CatchType));}
