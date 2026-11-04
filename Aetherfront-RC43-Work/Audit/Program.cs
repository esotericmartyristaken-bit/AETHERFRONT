using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=4||args[0] is not("client" or "server"))throw new ArgumentException("client|server BASELINE CANDIDATE REPORT");
bool client=args[0]=="client";
var expected=client?"6d9f6a05f5b8b75a4cb6ef55cdad3bfc35e9c06eadab9a135287bd202a1263d2":"033461f09f4db405cb6d2e9415936f2e1ef7d25aa24114d2412624aceb0ad06b";
if(Hash(args[1])!=expected)throw new IOException("Unreviewed helper baseline");
using var before=AssemblyDefinition.ReadAssembly(args[1]);using var after=AssemblyDefinition.ReadAssembly(args[2]);
if(before.Name.FullName!=after.Name.FullName||before.Name.HasPublicKey!=after.Name.HasPublicKey)throw new IOException("Helper identity changed");
var oldTypes=before.MainModule.Types.SelectMany(All).ToDictionary(t=>t.FullName);var newTypes=after.MainModule.Types.SelectMany(All).ToDictionary(t=>t.FullName);
bool Outbox(string n)=>client&&(n=="Aetherfront.Personal.Client.ScienceOutbox"||n.StartsWith("Aetherfront.Personal.Client.ScienceOutbox/",StringComparison.Ordinal));
var removedTypes=oldTypes.Keys.Where(k=>!newTypes.ContainsKey(k)).ToArray();
if(removedTypes.Any(k=>!Outbox(k)))throw new IOException("Unrelated type removed");
var oldMethods=oldTypes.Values.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);var newMethods=newTypes.Values.SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
var removedMethods=oldMethods.Keys.Where(k=>!newMethods.ContainsKey(k)).ToArray();
if(removedMethods.Any(k=>!Outbox(oldMethods[k].DeclaringType.FullName)))throw new IOException("Unrelated method removed");
bool Allowed(MethodDefinition m)=>client
 ? m.DeclaringType.FullName.StartsWith("Aetherfront.Personal.Client.ScienceOutbox",StringComparison.Ordinal)
   || m.DeclaringType.FullName=="Aetherfront.Personal.Client.PendingScienceClaim"&&m.Name==".ctor"
   || m.DeclaringType.FullName=="Aetherfront.Personal.Client.PersonalClient"&&m.Name is "SendScienceClaim" or "DrawSyncStatus"
 : m.DeclaringType.FullName=="Aetherfront.Personal.Server.PersonalServer"&&m.Name=="TryHandle";
var changed=oldMethods.Where(p=>newMethods.ContainsKey(p.Key)&&Image(p.Value)!=Image(newMethods[p.Key])).Select(p=>p.Key).ToArray();
var unexpected=changed.Where(k=>!Allowed(oldMethods[k])).ToArray();
if(unexpected.Length!=0)throw new IOException("Unexpected existing method changes: "+string.Join("; ",unexpected));
var oldFields=oldTypes.Values.SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName);var newFields=newTypes.Values.SelectMany(t=>t.Fields).ToDictionary(f=>f.FullName);
var missing=oldFields.Where(p=>!newFields.TryGetValue(p.Key,out var n)||FieldImage(p.Value)!=FieldImage(n)).Select(p=>p.Key).ToArray();
// Outbox implementation fields may be replaced, but unrelated state and all
// original public claim properties/chunk methods must keep their contracts.
if(missing.Any(n=>!Outbox(oldFields[n].DeclaringType.FullName)))throw new IOException("Unrelated field changed or removed: "+string.Join("; ",missing));
var oldReferences=before.MainModule.AssemblyReferences.Select(r=>r.FullName).ToHashSet();var newReferences=after.MainModule.AssemblyReferences.Select(r=>r.FullName).ToHashSet();
if(!oldReferences.IsSubsetOf(newReferences))throw new IOException("Assembly reference removed or replaced");
var resources=before.MainModule.Resources.Select(ResourceImage).Order().ToArray();if(!resources.SequenceEqual(after.MainModule.Resources.Select(ResourceImage).Order()))throw new IOException("Resources changed");
var result=new{kind=args[0],version="kesas-rc43",inputSha256=Hash(args[1]),outputSha256=Hash(args[2]),identityPreserved=true,
 originalMethods=oldMethods.Count,changed,allUnrelatedMethodsUnchanged=true,unchangedOriginalMethods=oldMethods.Count-changed.Length-removedMethods.Length,removedOutboxTypes=removedTypes,removedOutboxMethods=removedMethods,
 newTypes=newTypes.Keys.Except(oldTypes.Keys).ToArray(),newMethods=newMethods.Keys.Except(oldMethods.Keys).ToArray(),removedOutboxFields=missing,
 addedReferences=newReferences.Except(oldReferences).ToArray(),existingReferencesAndResourcesPreserved=true};
var report=Path.GetFullPath(args[3]);Directory.CreateDirectory(Path.GetDirectoryName(report)!);File.WriteAllText(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(result));
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static string HashBytes(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
static string ResourceImage(Resource r)=>r.Name+":"+r.ResourceType+":"+(r is EmbeddedResource e?HashBytes(e.GetResourceData()):r.ToString());
static string FieldImage(FieldDefinition f)=>f.FullName+":"+f.Attributes+":"+f.Constant;
static IEnumerable<TypeDefinition> All(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(All))yield return n;}
static OpCode Long(OpCode c)=>c.Code switch{Code.Br_S=>OpCodes.Br,Code.Brfalse_S=>OpCodes.Brfalse,Code.Brtrue_S=>OpCodes.Brtrue,Code.Beq_S=>OpCodes.Beq,Code.Bge_S=>OpCodes.Bge,Code.Bge_Un_S=>OpCodes.Bge_Un,Code.Bgt_S=>OpCodes.Bgt,Code.Bgt_Un_S=>OpCodes.Bgt_Un,Code.Ble_S=>OpCodes.Ble,Code.Ble_Un_S=>OpCodes.Ble_Un,Code.Blt_S=>OpCodes.Blt,Code.Blt_Un_S=>OpCodes.Blt_Un,Code.Bne_Un_S=>OpCodes.Bne_Un,Code.Leave_S=>OpCodes.Leave,_=>c};
static string Image(MethodDefinition m){if(!m.HasBody)return "none";var code=m.Body.Instructions;string Operand(object? o)=>o switch{null=>"",Instruction b=>"@"+code.IndexOf(b),Instruction[] a=>string.Join(",",a.Select(code.IndexOf)),IFormattable n=>n.ToString(null,CultureInfo.InvariantCulture),_=>o.ToString()??""};return string.Join("|",code.Select(i=>Long(i.OpCode)+":"+Operand(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+" handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)+":"+code.IndexOf(h.FilterStart)+":"+h.CatchType));}
