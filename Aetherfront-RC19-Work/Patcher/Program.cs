using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

string input=Path.GetFullPath("Aetherfront-RC18-Work/Patched/LmpClient.dll");
if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)))!="8115ABA138FB1B991F7DF821A1C996BC52CD66E8C59AF0D1BF95F33E3A9F8D2E")throw new InvalidDataException("Unreviewed RC18 client");
var resolver=new DefaultAssemblyResolver();
foreach(string directory in new[]{"Aetherfront-KSP-Smoke/KSP_x64_Data/Managed","Aetherfront-RC18-Work/Patched","Aetherfront-RC19-Work/Engine/bin/Release/net472","KESAS-RC15-Work/Inputs"})resolver.AddSearchDirectory(Path.GetFullPath(directory));
using var client=AssemblyDefinition.ReadAssembly(input,new ReaderParameters{AssemblyResolver=resolver});
var version=client.MainModule.GetType("LmpClient.Apf.ApfClientBuildVersion").Fields.Single(f=>f.Name=="Value");
if(!Equals(version.Constant,"kesas-rc18"))throw new InvalidDataException("Unexpected input version");
version.Constant="kesas-rc19";int count=0;var changed=new HashSet<string>();
foreach(var type in client.MainModule.Types.SelectMany(Flatten))foreach(var method in type.Methods.Where(m=>m.HasBody))foreach(var instruction in method.Body.Instructions)if(instruction.OpCode==OpCodes.Ldstr && Equals(instruction.Operand,"kesas-rc18")){instruction.Operand="kesas-rc19";changed.Add(method.FullName);count++;}
if(count!=1)throw new InvalidDataException("Unexpected version reference count");
string output=Path.GetFullPath("Aetherfront-RC19-Work/Patched");Directory.CreateDirectory(output);client.MainModule.Mvid=Guid.NewGuid();client.Write(Path.Combine(output,"LmpClient.dll"));
File.Copy(Path.GetFullPath("Aetherfront-RC18-Work/Patched/ApfCore.dll"),Path.Combine(output,"ApfCore.dll"),true);
using var pristine=AssemblyDefinition.ReadAssembly(input);using var verified=AssemblyDefinition.ReadAssembly(Path.Combine(output,"LmpClient.dll"));int preserved=0;
foreach(var type in pristine.MainModule.Types.SelectMany(Flatten))foreach(var method in type.Methods){if(changed.Contains(method.FullName))continue;var next=verified.MainModule.Types.SelectMany(Flatten).Single(t=>t.FullName==type.FullName).Methods.Single(m=>m.FullName==method.FullName);if(Signature(method)!=Signature(next))throw new InvalidDataException("Unrelated client method changed: "+method.FullName);preserved++;}
Console.WriteLine("PASS client: one version method changed; "+preserved+" unrelated methods preserved. Core copied byte-for-byte.");
static string Signature(MethodDefinition method){if(!method.HasBody)return "none";var indexes=method.Body.Instructions.Select((i,n)=>(i,n)).ToDictionary(x=>x.i,x=>x.n);string Operand(object? value)=>value switch{null=>"",Instruction i=>"branch:"+indexes[i],Instruction[] a=>"branches:"+string.Join(",",a.Select(i=>indexes[i])),_=>value.ToString()??""};return string.Join("\n",method.Body.Instructions.Select(i=>i.OpCode.Name+" "+Operand(i.Operand)));}
static IEnumerable<TypeDefinition> Flatten(TypeDefinition type){yield return type;foreach(var nested in type.NestedTypes)foreach(var child in Flatten(nested))yield return child;}
