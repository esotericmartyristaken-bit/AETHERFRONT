using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length<3 || args[0] is not ("research" or "stamp")) throw new ArgumentException("research BASELINE DONOR OUTPUT | stamp BASELINE OUTPUT");
bool research=args[0]=="research";
if(args.Length!=(research?4:3))throw new ArgumentException("Wrong argument count");
string input=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[^1]);
string expected=research?"02157055d47a3fe3c68b05fe8637f666db5af1815b8d5d23e14f2b78eb36da61":"5441fc826e5861dcad99257640805386a8ae774f9f69c18f9c2876ec01a24799";
if(input==output||Hash(input)!=expected||File.Exists(output))throw new IOException("Unreviewed baseline or existing output");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("AETHERFRONT_KSP_MANAGED")??throw new IOException("KSP references required"));
using var original=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{InMemory=true,AssemblyResolver=resolver});var module=original.MainModule;
if(original.Name.Name!=(research?"AetherfrontPersonalClient":"LmpClient")||original.Name.HasPublicKey)throw new IOException("Wrong target identity");
var types=module.Types.SelectMany(All).ToArray();var methods=types.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);var images=methods.ToDictionary(x=>x.Key,x=>Image(x.Value));
var fields=types.SelectMany(t=>t.Fields).Select(FieldImage).ToArray();var resources=module.Resources.Select(ResourceImage).ToArray();var refs=module.AssemblyReferences.Select(r=>r.FullName).ToArray();
var changedMethods=new List<string>();var helperImages=new Dictionary<string,string>();string? donorSha=null;
if(research){
 donorSha=Hash(args[2]);using var donor=AssemblyDefinition.ReadAssembly(args[2],new ReaderParameters{AssemblyResolver=resolver});
 var source=donor.MainModule.Types.Where(t=>t.Name!="<Module>").ToArray();
 if(!source.Select(t=>t.FullName).Order().SequenceEqual(new[]{"Aetherfront.Personal.Client.ResearchQueue","Aetherfront.Personal.Client.ResearchSync"})||source.Any(t=>t.HasNestedTypes||t.HasGenericParameters||t.Methods.Any(m=>m.HasGenericParameters)))throw new IOException("Unexpected donor shape");
 var typeMap=new Dictionary<string,TypeDefinition>();var fieldMap=new Dictionary<string,FieldDefinition>();var methodMap=new Dictionary<string,MethodDefinition>();
 foreach(var t in source){if(module.GetType(t.FullName)!=null)throw new IOException("Existing repair type");var n=new TypeDefinition(t.Namespace,t.Name,t.Attributes,module.ImportReference(t.BaseType));module.Types.Add(n);typeMap.Add(t.FullName,n);}
 TypeReference Type(TypeReference t){
  if(typeMap.TryGetValue(t.FullName,out var injected))return injected;
  if(t is GenericInstanceType g){var n=new GenericInstanceType(Type(g.ElementType));foreach(var argument in g.GenericArguments)n.GenericArguments.Add(Type(argument));return n;}
  if(t is ByReferenceType b)return new ByReferenceType(Type(b.ElementType));if(t is ArrayType a)return new ArrayType(Type(a.ElementType),a.Rank);if(t is GenericParameter)return t;
  if(t.Scope?.Name==original.Name.Name){var local=types.SingleOrDefault(x=>x.FullName==t.FullName);if(local!=null)return local;}
  return module.ImportReference(t);
 }
 foreach(var t in source){var target=typeMap[t.FullName];foreach(var f in t.Fields){var n=new FieldDefinition(f.Name,f.Attributes,Type(f.FieldType));if(f.HasConstant)n.Constant=f.Constant;target.Fields.Add(n);fieldMap.Add(f.FullName,n);}
  foreach(var m in t.Methods){var n=new MethodDefinition(m.Name,m.Attributes,Type(m.ReturnType)){ImplAttributes=m.ImplAttributes};foreach(var p in m.Parameters)n.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Type(p.ParameterType)));target.Methods.Add(n);methodMap.Add(m.FullName,n);}}
 MethodReference Method(MethodReference m){if(methodMap.TryGetValue(m.FullName,out var n))return n;if(m.DeclaringType.Scope?.Name==original.Name.Name&&methods.TryGetValue(m.FullName,out var local))return local;return module.ImportReference(m);}
 FieldReference Field(FieldReference f)=>fieldMap.TryGetValue(f.FullName,out var n)?n:module.ImportReference(f);
 foreach(var t in source){foreach(var m in t.Methods){
  if(!m.HasBody)throw new IOException("External donor method");var n=methodMap[m.FullName];n.Body=new MethodBody(n){InitLocals=m.Body.InitLocals,MaxStackSize=m.Body.MaxStackSize};
  var locals=m.Body.Variables.ToDictionary(v=>v,v=>new VariableDefinition(Type(v.VariableType)));foreach(var v in m.Body.Variables)n.Body.Variables.Add(locals[v]);
  var code=m.Body.Instructions.ToDictionary(i=>i,i=>Instruction.Create(OpCodes.Nop));foreach(var i in m.Body.Instructions)n.Body.Instructions.Add(code[i]);
  foreach(var i in m.Body.Instructions){var c=code[i];c.OpCode=i.OpCode;c.Operand=i.Operand switch{Instruction b=>code[b],Instruction[] a=>a.Select(b=>code[b]).ToArray(),VariableDefinition v=>locals[v],ParameterDefinition p=>n.Parameters[p.Index],MethodReference r=>Method(r),FieldReference f=>Field(f),TypeReference r=>Type(r),CallSite=>throw new IOException("Indirect donor call"),_=>i.Operand};}
  Instruction? At(Instruction? i)=>i==null?null:code[i];foreach(var e in m.Body.ExceptionHandlers)n.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){TryStart=At(e.TryStart),TryEnd=At(e.TryEnd),HandlerStart=At(e.HandlerStart),HandlerEnd=At(e.HandlerEnd),FilterStart=At(e.FilterStart),CatchType=e.CatchType==null?null:Type(e.CatchType)});
  if(Image(n)!=Image(m))throw new IOException("Donor body changed: "+m.FullName);helperImages.Add(n.FullName,Image(n));
 }
 foreach(var p in t.Properties){var n=new PropertyDefinition(p.Name,p.Attributes,Type(p.PropertyType)){GetMethod=p.GetMethod==null?null:methodMap[p.GetMethod.FullName],SetMethod=p.SetMethod==null?null:methodMap[p.SetMethod.FullName]};typeMap[t.FullName].Properties.Add(n);}}
 var personal=module.GetType("Aetherfront.Personal.Client.PersonalClient");
 var request=personal.Methods.Single(m=>m.Name=="RequestTechnology");changedMethods.Add(request.FullName);
 request.Body=new MethodBody(request);var il=request.Body.GetILProcessor();il.Append(Instruction.Create(OpCodes.Ldarg_0));il.Append(Instruction.Create(OpCodes.Call,methodMap["System.Void Aetherfront.Personal.Client.ResearchSync::Request(System.String)"]));il.Append(Instruction.Create(OpCodes.Ret));
 foreach(var row in new[]{(module.GetType("Aetherfront.Personal.Client.ScienceEvents").Methods.Single(m=>m.Name=="Update"),"Tick"),(personal.Methods.Single(m=>m.Name=="DrawSyncStatus"),"DrawStatus")}){
  changedMethods.Add(row.Item1.FullName);var first=row.Item1.Body.Instructions[0];row.Item1.Body.GetILProcessor().InsertBefore(first,Instruction.Create(OpCodes.Call,typeMap["Aetherfront.Personal.Client.ResearchSync"].Methods.Single(m=>m.Name==row.Item2)));
 }
}else{
 var getter=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");var text=getter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);
 var value=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");if(!Equals(text.Operand,"kesas-rc45")||!Equals(value.Constant,"kesas-rc45"))throw new IOException("Unexpected version");
 text.Operand="kesas-rc46";value.Constant="kesas-rc46";changedMethods.Add(getter.FullName);
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);original.Write(output);
using var verify=AssemblyDefinition.ReadAssembly(output,new ReaderParameters{AssemblyResolver=resolver});var checkedTypes=verify.MainModule.Types.SelectMany(All).ToArray();var checkedMethods=checkedTypes.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
var changes=methods.Keys.Where(k=>Image(checkedMethods[k])!=images[k]).Order().ToArray();if(!changes.SequenceEqual(changedMethods.Order()))throw new IOException("Unrelated method changed");
if(verify.Name.FullName!=original.Name.FullName||!verify.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources)||!verify.MainModule.AssemblyReferences.Select(r=>r.FullName).SequenceEqual(refs))throw new IOException("Identity/resource/dependency changed");
var originalFields=checkedTypes.Where(t=>types.Any(p=>p.FullName==t.FullName)).SelectMany(t=>t.Fields).Select(FieldImage).ToArray();if(originalFields.Length!=fields.Length||originalFields.Where((x,i)=>x!=fields[i]).Count()!=(research?0:1))throw new IOException("Unexpected field change");
if(checkedTypes.Length-types.Length!=(research?2:0)||checkedMethods.Count-methods.Count!=helperImages.Count||helperImages.Any(p=>!checkedMethods.ContainsKey(p.Key)||Image(checkedMethods[p.Key])!=p.Value))throw new IOException("Donor roundtrip differs");
var audit=new{version="kesas-rc46",mode=args[0],inputSha256=Hash(input),outputSha256=Hash(output),donorSha256=donorSha,identityPreserved=true,allOtherMethodsUnchanged=true,changes,addedTypes=checkedTypes.Select(t=>t.FullName).Except(types.Select(t=>t.FullName)).ToArray()};
File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(audit,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(audit));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string FieldImage(FieldDefinition f)=>f.FullName+":"+f.Attributes+":"+f.Constant;
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?Convert.ToHexString(SHA256.HashData(e.GetResourceData())):r.ToString());
static string TypeImage(TypeReference t)=>t switch{GenericParameter p=>(p.Type==GenericParameterType.Method?"!!":"!")+p.Position,GenericInstanceType g=>TypeImage(g.ElementType)+"<"+string.Join(",",g.GenericArguments.Select(TypeImage))+">",ByReferenceType b=>TypeImage(b.ElementType)+"&",ArrayType a=>TypeImage(a.ElementType)+"["+new string(',',a.Rank-1)+"]",_=>t.FullName};
static string MethodImage(MethodReference r)=>TypeImage(r.ReturnType)+" "+TypeImage(r.DeclaringType)+"::"+r.Name+(r is GenericInstanceMethod g?"<"+string.Join(",",g.GenericArguments.Select(TypeImage))+">":"")+"("+string.Join(",",r.Parameters.Select(p=>TypeImage(p.ParameterType)))+") this:"+r.HasThis+":"+r.ExplicitThis+":"+r.CallingConvention;
static string Image(MethodDefinition m){var signature=MethodImage(m)+":"+m.Attributes+":"+m.ImplAttributes;if(!m.HasBody)return signature+" none";var code=m.Body.Instructions;string Operand(object? o)=>o switch{null=>"",Instruction b=>"@"+code.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(code.IndexOf)),MethodReference r=>MethodImage(r),FieldReference f=>TypeImage(f.FieldType)+" "+TypeImage(f.DeclaringType)+"::"+f.Name,TypeReference t=>TypeImage(t),IFormattable n=>n.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return signature+" "+string.Join("|",code.Select(i=>i.OpCode+":"+Operand(i.Operand)))+" init:"+m.Body.InitLocals+" locals:"+string.Join(",",m.Body.Variables.Select(v=>TypeImage(v.VariableType)))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)+":"+code.IndexOf(h.FilterStart)+":"+h.CatchType));}
