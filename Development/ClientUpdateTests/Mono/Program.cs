using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

class Program
{
 static string fixture,managed;
 static Assembly Resolve(object sender,ResolveEventArgs args){string name=new AssemblyName(args.Name).Name+".dll";foreach(string directory in new[]{fixture,managed}){string path=Path.Combine(directory,name);if(File.Exists(path))return Assembly.LoadFrom(path);}return null;}
 static System.Collections.Generic.IEnumerable<Type> Nested(Type type){yield return type;foreach(var child in type.GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))foreach(var nested in Nested(child))yield return nested;}
 static void Main(){
  fixture=Environment.GetEnvironmentVariable("RC56_MONO_ROOT");managed=Path.Combine(Environment.GetEnvironmentVariable("RC56_KSP_REFERENCE"),"KSP_x64_Data","Managed");
  AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
  var engine=Assembly.LoadFrom(Path.Combine(fixture,"KesasInstall.dll"));var client=Assembly.LoadFrom(Path.Combine(fixture,"LmpClient.dll"));int count=0;
  foreach(var type in engine.GetTypes().Where(t=>new[]{"IncrementalInstall","DeltaClientFiles","PreparedPack","NativeInstall","Installer"}.Contains(t.Name)).SelectMany(Nested).Concat(new[]{client.GetType("LmpClient.Apf.ConnectionInstall",true)})){
   foreach(var method in type.GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))if(!method.IsAbstract && !method.ContainsGenericParameters && (method.Attributes&MethodAttributes.PinvokeImpl)==0){RuntimeHelpers.PrepareMethod(method.MethodHandle);count++;}
  }
  Console.WriteLine("PASS actual KSP Mono prepared "+count+" updater/connection methods.");
  var tests=Assembly.LoadFrom(Path.Combine(fixture,"ClientUpdateTests.exe"));
  string[] arguments=Environment.GetEnvironmentVariable("CLIENT_UPDATE_TEST_ARGUMENTS").Split('|');
  try{tests.EntryPoint.Invoke(null,new object[]{arguments});}catch(TargetInvocationException error){throw error.InnerException;}
 }
}
