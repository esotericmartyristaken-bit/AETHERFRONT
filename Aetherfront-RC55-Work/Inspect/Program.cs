using Mono.Cecil;
using var asm = AssemblyDefinition.ReadAssembly(args[0]);
IEnumerable<TypeDefinition> All(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(All));
foreach(var t in asm.MainModule.Types.SelectMany(All).Where(t=>args[1].StartsWith("=") ? t.FullName == args[1].Substring(1) : t.FullName.Contains(args[1]))) {
 Console.WriteLine(t.FullName);
 if(args.Length < 3) foreach(var f in t.Fields) Console.WriteLine("FIELD " + f.FullName);
 foreach(var m in t.Methods.Where(m=>args.Length<3 || m.Name.Contains(args[2]))) {
  Console.WriteLine(m.FullName);
  if(args.Length>3 && m.HasBody) foreach(var i in m.Body.Instructions) Console.WriteLine(i);
 }
}
