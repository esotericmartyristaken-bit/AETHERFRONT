using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=4)throw new ArgumentException("INPUT_CLIENT NEW_HELPER OUTPUT_CLIENT KSP_MANAGED");
var input=Path.GetFullPath(args[0]);var helperPath=Path.GetFullPath(args[1]);var output=Path.GetFullPath(args[2]);
if(input==output||Hash(input)!="82c5e3d6c1b59613c0d7f8db2b1bde391c90d56818e37ce88d11244787ca1329")throw new IOException("Unreviewed RC42 client or in-place edit");
using var resolver=new DefaultAssemblyResolver();resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);resolver.AddSearchDirectory(Path.GetDirectoryName(helperPath)!);resolver.AddSearchDirectory(Path.GetFullPath(args[3]));
using var client=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});
using var helper=AssemblyDefinition.ReadAssembly(helperPath,new ReaderParameters{AssemblyResolver=resolver,InMemory=true});
if(client.Name.Name!="LmpClient"||helper.Name.Name!="AetherfrontPersonalClient"||client.Name.HasPublicKey||helper.Name.HasPublicKey)throw new IOException("Unexpected assembly identity");
var module=client.MainModule;var all=module.Types.SelectMany(All).ToArray();var methods=all.SelectMany(t=>t.Methods).ToArray();
var before=methods.ToDictionary(m=>m.FullName,Image);var fields=all.SelectMany(t=>t.Fields).Select(FieldImage).ToArray();
var references=module.AssemblyReferences.Select(r=>r.FullName).ToArray();var resources=module.Resources.Select(ResourceImage).ToArray();
var version=module.GetType("LmpClient.Apf.ApfClientSettings").Methods.Single(m=>m.Name=="get_ClientVersion");
var constant=module.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");
var literal=version.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldstr);
if(!Equals(literal.Operand,"kesas-rc42")||!Equals(constant.Constant,"kesas-rc42"))throw new IOException("Unexpected release getter");
literal.Operand="kesas-rc43";constant.Constant="kesas-rc43";

var receiver=module.GetType("LmpClient.Aetherfront.AetherfrontClientSystem").Methods.Single(m=>m.Name=="HandleMessage");
var deserialize=receiver.Body.Instructions.Single(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference r&&r.DeclaringType.FullName=="Aetherfront.Core.AetherfrontProtocol"&&r.Name=="DeserializeReply");
var capture=helper.MainModule.GetType("Aetherfront.Personal.Client.PersonalClient").Methods.Single(m=>m.Name=="CaptureScienceReceipt");
if(capture.ReturnType.FullName!="System.Boolean"||capture.Parameters.Count!=1||capture.Parameters[0].ParameterType.FullName!="System.String")throw new IOException("Unexpected receipt hook");
var finalRet=receiver.Body.Instructions.Last(i=>i.OpCode==OpCodes.Ret);
if(!receiver.Body.ExceptionHandlers.Any(h=>h.HandlerType==ExceptionHandlerType.Finally))throw new IOException("Message recycle finally missing");
var receiveHook=new[]{Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Call,module.ImportReference(capture)),Instruction.Create(OpCodes.Brfalse,deserialize),Instruction.Create(OpCodes.Pop),Instruction.Create(OpCodes.Leave,finalRet)};
foreach(var i in receiveHook)receiver.Body.GetILProcessor().InsertBefore(deserialize,i);
// Branches that originally supplied the argument at this call must also run
// the receipt filter. Handler boundaries are retargeted without skipping finally.
Redirect(receiver,deserialize,receiveHook[0],receiveHook);

var sender=module.GetType("LmpClient.Network.NetworkSender").Methods.Single(m=>m.Name=="SendNetworkMessage");
if(sender.Parameters.Count!=3||sender.ReturnType.FullName!="System.Boolean"||!sender.Body.ExceptionHandlers.Any(h=>h.HandlerType==ExceptionHandlerType.Finally))throw new IOException("Unexpected sender lifecycle");
var pace=helper.MainModule.Types.SelectMany(All).Single(t=>t.Name=="ClientPacing").Methods.Single(m=>m.Name=="WaitForCapacity");
if(pace.ReturnType.FullName!="System.Boolean"||!pace.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(sender.Parameters.Select(p=>p.ParameterType.FullName)))throw new IOException("Pacing generation/cancellation contract mismatch");
// The compiler initializes its closure before the try. Enter at the shared
// catch/finally protected start, preserving that initialization unchanged.
var first=sender.Body.ExceptionHandlers.Single(h=>h.HandlerType==ExceptionHandlerType.Finally).TryStart;
if(sender.Body.Instructions.IndexOf(first)!=8||!sender.Body.ExceptionHandlers.Any(h=>h.HandlerType==ExceptionHandlerType.Catch&&h.TryStart==first))throw new IOException("Unexpected protected sender entry");
var falseTarget=sender.Body.Instructions.First(i=>i.OpCode==OpCodes.Ldc_I4_0&&i.Next!=null&&(i.Next.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3)&&i.Next.Next!=null&&Long(i.Next.Next.OpCode)==OpCodes.Leave);
var sendHook=sender.Parameters.Select(p=>Instruction.Create(OpCodes.Ldarg,p)).Concat(new[]{Instruction.Create(OpCodes.Call,module.ImportReference(pace)),Instruction.Create(OpCodes.Brfalse,falseTarget)}).ToArray();
foreach(var i in sendHook)sender.Body.GetILProcessor().InsertBefore(first,i);
Redirect(sender,first,sendHook[0],sendHook);
foreach(var method in new[]{receiver,sender})foreach(var i in method.Body.Instructions)i.OpCode=Long(i.OpCode);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);if(File.Exists(output))throw new IOException("Use new immutable output");client.Write(output);

using var check=AssemblyDefinition.ReadAssembly(output);
var checkTypes=check.MainModule.Types.SelectMany(All).ToArray();var checkMethods=checkTypes.SelectMany(t=>t.Methods).ToArray();
var expected=new[]{receiver.FullName,sender.FullName,version.FullName}.Order().ToArray();
var changes=checkMethods.Where(m=>before.GetValueOrDefault(m.FullName)!=Image(m)).Select(m=>m.FullName).Order().ToArray();
if(!changes.SequenceEqual(expected)||checkMethods.Length!=methods.Length||checkTypes.Length!=all.Length||check.Name.FullName!=client.Name.FullName||!check.MainModule.AssemblyReferences.Select(r=>r.FullName).SequenceEqual(references)||!check.MainModule.Resources.Select(ResourceImage).SequenceEqual(resources))throw new IOException("Changes exceed reviewed release/receipt/pacing hooks");
var checkFields=checkTypes.SelectMany(t=>t.Fields).Select(FieldImage).ToArray();
if(checkFields.Length!=fields.Length||checkFields.Where((v,n)=>v!=fields[n]).Count()!=1)throw new IOException("Expected only release constant change");
foreach(var target in new[]{(receiver,"CaptureScienceReceipt",5),(sender,"WaitForCapacity",5)})
{
 var method=checkMethods.Single(m=>m.FullName==target.Item1.FullName);
 var call=method.Body.Instructions.Single(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference r&&r.Name==target.Item2);
 var begin=call;for(int n=0;n<(target.Item2=="CaptureScienceReceipt"?1:3);n++)begin=begin.Previous;
 var after=begin;for(int n=0;n<target.Item3;n++)after=after.Next;
 Redirect(method,begin,after,Array.Empty<Instruction>());
 for(int n=0;n<target.Item3;n++){var next=begin.Next;method.Body.Instructions.Remove(begin);begin=next;}
 if(Image(method)!=before[method.FullName])throw new IOException("Recovered original lifecycle differs: "+method.FullName);
}
var report=new{version="kesas-rc43",inputSha256=Hash(input),outputSha256=Hash(output),helperSha256=Hash(helperPath),changed=changes,allOtherMethodsUnchanged=true,originalSenderAndReceiverRecovered=true,recycleFinallyPreserved=true,existingSessionAndEndpointChecksPreserved=true,coreAndTransportUnchanged=true};
File.WriteAllText(output+".audit.json",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));

static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static string FieldImage(FieldDefinition f)=>f.FullName+":"+f.Attributes+":"+f.Constant;
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?HashBytes(e.GetResourceData()):r.ToString());
static string HashBytes(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
static OpCode Long(OpCode c)=>c.Code switch{Code.Br_S=>OpCodes.Br,Code.Brfalse_S=>OpCodes.Brfalse,Code.Brtrue_S=>OpCodes.Brtrue,Code.Beq_S=>OpCodes.Beq,Code.Bge_S=>OpCodes.Bge,Code.Bge_Un_S=>OpCodes.Bge_Un,Code.Bgt_S=>OpCodes.Bgt,Code.Bgt_Un_S=>OpCodes.Bgt_Un,Code.Ble_S=>OpCodes.Ble,Code.Ble_Un_S=>OpCodes.Ble_Un,Code.Blt_S=>OpCodes.Blt,Code.Blt_Un_S=>OpCodes.Blt_Un,Code.Bne_Un_S=>OpCodes.Bne_Un,Code.Leave_S=>OpCodes.Leave,_=>c};
static void Redirect(MethodDefinition m,Instruction from,Instruction to,IEnumerable<Instruction> excluded){var skip=excluded.ToHashSet();foreach(var i in m.Body.Instructions.Where(i=>!skip.Contains(i))){if(ReferenceEquals(i.Operand,from))i.Operand=to;if(i.Operand is Instruction[] a)for(int n=0;n<a.Length;n++)if(a[n]==from)a[n]=to;}foreach(var h in m.Body.ExceptionHandlers){if(h.TryStart==from)h.TryStart=to;if(h.TryEnd==from)h.TryEnd=to;if(h.HandlerStart==from)h.HandlerStart=to;if(h.HandlerEnd==from)h.HandlerEnd=to;if(h.FilterStart==from)h.FilterStart=to;}}
static string Image(MethodDefinition m){if(!m.HasBody)return "none";var code=m.Body.Instructions;string Operand(object? o)=>o switch{null=>"",Instruction b=>"@"+code.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(code.IndexOf)),IFormattable n=>n.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return string.Join("|",code.Select(i=>Long(i.OpCode)+":"+Operand(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)+":"+code.IndexOf(h.FilterStart)+":"+h.CatchType));}
