using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class Program
{
    static string game,candidate;
    static readonly string[] Blocked={"USILifeSupport","USITools","KolonyTools","DeepFreeze","LunaCompat","TarsierSpaceTech"};
    static Assembly Resolve(object sender,ResolveEventArgs e)
    {
        var name=new AssemblyName(e.Name).Name;
        if(Blocked.Contains(name)){Console.WriteLine("DEPENDENCY_WITHHELD "+name);return null;}
        foreach(var folder in new[]{candidate,Path.Combine(game,"KSP_x64_Data/Managed"),Path.Combine(game,"GameData/LunaMultiplayer/Plugins"),Path.Combine(game,"GameData/000_Harmony")})
        {var path=Path.Combine(folder,name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}
        return null;
    }
    static void Main()
    {
        game=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        candidate=Environment.GetEnvironmentVariable("APF_CONTINUITY_CANDIDATE");
        if(!Directory.Exists(game)||!Directory.Exists(candidate))throw new InvalidOperationException("Explicit runtime and candidate required");
        AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
        if(AppDomain.CurrentDomain.GetAssemblies().Any(a=>Blocked.Contains(a.GetName().Name)))throw new InvalidOperationException("Optional pack dependencies already loaded");
        var assembly=Assembly.LoadFrom(Path.Combine(candidate,"AetherfrontContinuityClient.dll"));
        try
        {
            var types=assembly.GetTypes();Console.WriteLine("GET_TYPES_PASS "+types.Length);
            var authority=types.Single(t=>t.FullName=="Aetherfront.Continuity.Client.SimulationAuthority");
            var awake=authority.GetMethod("Awake");
            RuntimeHelpers.PrepareMethod(awake.MethodHandle);Console.WriteLine("PREPARE_AWAKE_RETURNED");
            Console.WriteLine("LIMITATION: isolated Mono metadata/JIT preparation only, not a Unity bootstrap acceptance pass.");
        }
        catch(Exception e){Console.WriteLine("BOOTSTRAP_PROBE_FAILURE "+e);Environment.ExitCode=1;}
        Console.WriteLine("WITHHELD_ASSEMBLIES_LOADED="+AppDomain.CurrentDomain.GetAssemblies().Count(a=>Blocked.Contains(a.GetName().Name)));
    }
}
