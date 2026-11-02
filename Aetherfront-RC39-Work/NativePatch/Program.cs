using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if(args.Length!=4)throw new ArgumentException("Reviewed RC38 client, policy DLL, output and game references required");
string input=Path.GetFullPath(args[0]),policy=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
if(Hash(input)!="2becaa2ac98a4b81724ea2d0f7d35ed1316497b1a5c9219da53954c77c588e97")throw new IOException("Use the reviewed shipping RC38 client input");
using var resolver=new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.Combine(Path.GetFullPath(args[3]),"KSP_x64_Data/Managed"));
resolver.AddSearchDirectory(Path.Combine(Path.GetFullPath(args[3]),"GameData/LunaMultiplayer/Plugins"));
resolver.AddSearchDirectory(Path.GetDirectoryName(policy)!);
using var client=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{AssemblyResolver=resolver});
using var rules=AssemblyDefinition.ReadAssembly(policy);
var methods=client.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).ToArray();
var before=methods.ToDictionary(m=>m.FullName,Image);
var target=methods.Single(m=>m.DeclaringType.FullName=="LmpClient.Network.NetworkConnection"&&m.Name=="ConnectToServer"&&m.Parameters.Count==2&&m.Parameters[0].ParameterType.FullName=="System.Net.IPEndPoint[]");
var code=target.Body.Instructions;
var gate=code.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="LmpClient.Apf.ConnectionInstall"&&m.Name=="ShouldConnect");
var branch=gate.Next;
if(branch.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)||branch.Next.OpCode!=OpCodes.Ret||branch.Operand is not Instruction continuation||
   code.Any(i=>i.Operand is MethodReference m&&m.Name=="AllowNativeJoin"))throw new IOException("Unreviewed/already-patched native join gate");
if(target.Body.ExceptionHandlers.Any(h=>code.IndexOf(h.TryStart)<=code.IndexOf(continuation)))throw new IOException("Policy hook must precede existing native exception regions");
var hook=rules.MainModule.GetType("Aetherfront.SessionPolicy.ParallaxCollisionPolicy").Methods.Single(m=>m.Name=="AllowNativeJoin");
if(!hook.IsPublic||!hook.IsStatic||hook.Parameters.Count!=0||hook.ReturnType.FullName!="System.Boolean")throw new IOException("Invalid policy hook");
var first=Instruction.Create(OpCodes.Call,client.MainModule.ImportReference(hook));
var il=target.Body.GetILProcessor();
il.InsertBefore(continuation,first);
il.InsertBefore(continuation,Instruction.Create(OpCodes.Brtrue,continuation));
il.InsertBefore(continuation,Instruction.Create(OpCodes.Ret));
branch.Operand=first;
Directory.CreateDirectory(Path.GetDirectoryName(output)!);client.Write(output);
using var verify=AssemblyDefinition.ReadAssembly(output);
var after=verify.MainModule.Types.SelectMany(All).SelectMany(t=>t.Methods).ToArray();
var changed=after.Where(m=>before[m.FullName]!=Image(m)).Select(m=>m.FullName).ToArray();
if(after.Length!=methods.Length||changed.Length!=1||changed[0]!=target.FullName)throw new IOException("Native changes exceeded one connection method");
var check=after.Single(m=>m.FullName==target.FullName).Body.Instructions;
var readiness=check.Single(i=>i.Operand is MethodReference m&&m.Name=="ShouldConnect");
var checkHook=check.Single(i=>i.Operand is MethodReference m&&m.Name=="AllowNativeJoin");
if(!ReferenceEquals(readiness.Next.Operand,checkHook)||checkHook.Next.OpCode!=OpCodes.Brtrue||checkHook.Next.Next.OpCode!=OpCodes.Ret)
    throw new IOException("Policy must be mandatory after native installation readiness");
File.WriteAllText(output+".join-policy-audit.json",JsonSerializer.Serialize(new{
    release="RC39 local candidate",inputSha256=Hash(input),outputSha256=Hash(output),policySha256=Hash(policy),methodCount=after.Length,changed,
    policyAfterInstaller=true,earlyReturnOnDenied=true,allOtherMethodsUnchanged=true,versionRetargeted=false,published=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("PASS mandatory join-policy hook: one native method changed; all "+(after.Length-1)+" others unchanged. RC38 version retained only as an unpublished build input.");
static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
static IEnumerable<TypeDefinition> All(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes.SelectMany(All))yield return nested;}
static string Image(MethodDefinition m)
{
    if(!m.HasBody)return "none";var code=m.Body.Instructions;
    string Operand(object? o)=>o switch {null=>"",Instruction b=>"@"+code.IndexOf(b),Instruction[] bs=>string.Join(",",bs.Select(b=>code.IndexOf(b))),
        IFormattable n=>n.ToString(null,System.Globalization.CultureInfo.InvariantCulture),_=>o.ToString()??""};
    return string.Join("|",code.Select(i=>i.OpCode+":"+Operand(i.Operand)))+" locals:"+string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName))+
        " handlers:"+string.Join("|",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+":"+code.IndexOf(h.TryStart)+":"+code.IndexOf(h.TryEnd)+":"+code.IndexOf(h.HandlerStart)+":"+code.IndexOf(h.HandlerEnd)));
}
