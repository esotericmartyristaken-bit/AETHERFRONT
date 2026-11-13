using Mono.Cecil;
using System.Text.Json;
if(args.Length!=4)throw new ArgumentException("Expected baseline engine, candidate engine, shipping plugins, KSP managed directory");
using var resolver=new DefaultAssemblyResolver();
foreach(var p in new[]{Path.GetDirectoryName(Path.GetFullPath(args[1]))!,args[2],args[3]})resolver.AddSearchDirectory(p);
using var old=AssemblyDefinition.ReadAssembly(args[0]);
using var next=AssemblyDefinition.ReadAssembly(args[1],new ReaderParameters{AssemblyResolver=resolver});
var types=next.MainModule.Types.SelectMany(Flatten).ToDictionary(t=>t.FullName);
var failures=new List<string>();int members=0,references=0;
foreach(var r in next.MainModule.AssemblyReferences.Where(r=>r.Name.StartsWith("System.IO.Compression")))if(r.Version!=new Version(4,0,0,0))failures.Add("Non-KSP compression reference "+r.FullName);
foreach(var t in old.MainModule.Types.SelectMany(Flatten).Where(t=>t.IsPublic||t.IsNestedPublic)){
 if(!types.TryGetValue(t.FullName,out var n)){failures.Add("Missing type "+t.FullName);continue;}
 foreach(var m in t.Methods.Where(m=>m.IsPublic||m.IsFamily)){members++;if(!n.Methods.Any(x=>x.FullName==m.FullName))failures.Add("Missing method "+m.FullName);}
 foreach(var f in t.Fields.Where(f=>f.IsPublic||f.IsFamily)){members++;if(!n.Fields.Any(x=>x.FullName==f.FullName))failures.Add("Missing field "+f.FullName);}
}
foreach(var path in Directory.GetFiles(args[2],"*.dll")){
 using var a=AssemblyDefinition.ReadAssembly(path,new ReaderParameters{AssemblyResolver=resolver});
 if(a.Name.Name=="KesasInstall")continue;
 foreach(var type in a.MainModule.GetTypeReferences().Where(t=>t.Scope.Name is "KesasInstall" or "KesasInstall.dll")){
  references++;try{if(type.Resolve()==null)failures.Add("Unresolved type "+type.FullName);}catch(Exception e){failures.Add(type.FullName+": "+e.Message);}
 }
 foreach(var member in a.MainModule.GetMemberReferences().Where(m=>m.DeclaringType.Scope.Name is "KesasInstall" or "KesasInstall.dll")){
  references++;try{if(member is MethodReference m&&m.Resolve()==null)failures.Add("Unresolved method "+m.FullName);if(member is FieldReference f&&f.Resolve()==null)failures.Add("Unresolved field "+f.FullName);}catch(Exception e){failures.Add(member.FullName+": "+e.Message);}
 }
}
Console.WriteLine(JsonSerializer.Serialize(new{members,references,failures},new JsonSerializerOptions{WriteIndented=true}));
if(failures.Count!=0)throw new Exception("Installer dependency/API regression");
Console.WriteLine("PASS complete baseline public installer API and shipping cross-assembly installer references");
static IEnumerable<TypeDefinition> Flatten(TypeDefinition t){yield return t;foreach(var n in t.NestedTypes.SelectMany(Flatten))yield return n;}
