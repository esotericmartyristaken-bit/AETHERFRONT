using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
class Program
{
 static string game,helper;
 static Assembly Resolve(object sender,ResolveEventArgs args)
 {
  string name=new AssemblyName(args.Name).Name+".dll";
  foreach(string root in new[]{helper,Path.Combine(game,"KSP_x64_Data/Managed"),Path.Combine(game,"GameData/LunaMultiplayer/Plugins"),Path.Combine(game,"GameData/000_Harmony")})
  {string path=Path.Combine(root,name);if(File.Exists(path))return Assembly.LoadFrom(path);}return null;
 }
 static void Main()
 {
  game=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT"); helper=Environment.GetEnvironmentVariable("RC55_MONO_HELPER");
  AppDomain.CurrentDomain.AssemblyResolve+=Resolve;Run();
 }
 [MethodImpl(MethodImplOptions.NoInlining)] static void Run()
 {
  Assembly.LoadFrom(Path.Combine(game,"GameData/LunaMultiplayer/Plugins/AetherfrontContinuityClient.dll"));
  var assembly=typeof(Aetherfront.Stability.Client.FlightSafety).Assembly;int methods=0;
  foreach(var type in assembly.GetTypes())foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly))
   if(!method.ContainsGenericParameters&&method.GetMethodBody()!=null){RuntimeHelpers.PrepareMethod(method.MethodHandle);methods++;}
  var harmony=new Harmony("aetherfront.rc55.mono-validation");
  Aetherfront.Stability.Client.FlightSafety.InstallHooks(harmony);
  Console.WriteLine("PASS actual KSP Mono JIT: "+methods+" helper methods and complete Harmony hook installation against shipping KSP/LMP");
 }
}
