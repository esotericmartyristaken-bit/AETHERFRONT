using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class Program
{
    private static Assembly Resolve(object sender,ResolveEventArgs args)
    {
        string name=new AssemblyName(args.Name).Name+".dll";
        foreach(string path in new[]{"KESAS-RC17-Work/Patched","KESAS-RC17-Work/Engine/bin/Release/net472","KESAS-RC15-Work/Inputs","Aetherfront-KSP-Smoke/KSP_x64_Data/Managed"}) {
            string file=Path.GetFullPath(Path.Combine(path,name));if(File.Exists(file))return Assembly.LoadFrom(file);
        }
        return null;
    }
    private static void Main()
    {
        try {
            AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
            var assembly=Assembly.LoadFrom(Path.GetFullPath("KESAS-RC17-Work/Patched/LmpClient.dll"));
            int checkedMethods=0,nativeMethods=0;
            foreach(string name in new[]{"LmpClient.Apf.ConnectionInstall","LmpClient.Windows.Connection.ConnectionWindow"}) {
                var type=assembly.GetType(name,true);
                foreach(var method in type.GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) {
                    if(method.IsAbstract || method.ContainsGenericParameters)continue;
                    try{RuntimeHelpers.PrepareMethod(method.MethodHandle);checkedMethods++;}
                    catch(System.Security.SecurityException error) when(error.Message.Contains("ECall methods")){nativeMethods++;}
                }
            }
            Console.WriteLine("PASS "+checkedMethods+" shipped methods JIT successfully; "+nativeMethods+" require Unity's native runtime and cannot be JIT-tested in ordinary .NET. No game launched.");
            string root=Path.GetFullPath("KESAS-RC17-Work/DropdownFixture-"+Guid.NewGuid().ToString("N").Substring(0,10));Directory.CreateDirectory(root);
            assembly.GetType("LmpClient.MainSystem",true).GetProperty("KspPath",BindingFlags.Static|BindingFlags.Public).GetSetMethod(true).Invoke(null,new object[]{root});
            var flow=assembly.GetType("LmpClient.Apf.ConnectionInstall",true);
            var select=flow.GetMethod("Select",BindingFlags.Static|BindingFlags.NonPublic);
            var load=flow.GetMethod("LoadSelection",BindingFlags.Static|BindingFlags.NonPublic);
            var loaded=flow.GetField("loaded",BindingFlags.Static|BindingFlags.NonPublic);
            var mode=flow.GetField("mode",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(string value in new[]{"potato","full"}) {
                select.Invoke(null,new object[]{value});loaded.SetValue(null,false);mode.SetValue(null,"not loaded");load.Invoke(null,null);
                if((string)mode.GetValue(null)!=value||File.ReadAllText(Path.Combine(root,"GameData/LunaMultiplayer/Data/KesasInstallSelection.txt")).Trim()!=value)throw new Exception("Dropdown selection did not persist.");
            }
            Console.WriteLine("PASS actual shipped dropdown callbacks save and reload both Regular and Potato selections.");
        }catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
    }
}
