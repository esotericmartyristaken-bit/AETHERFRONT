using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static partial class Program
{
    static int checks;
    static readonly Dictionary<string,string> Assemblies=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    static void Check(bool condition,string name)
    {
        if(!condition)throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("PASS "+name);
    }
    static void Main(string[] args)
    {
        if(args.Length==0)args=new[]{Environment.GetEnvironmentVariable("APF_DF_EVENT_CANDIDATE"),
            Environment.GetEnvironmentVariable("APF_DF_EVENT_GAMEDATA"),Environment.GetEnvironmentVariable("APF_DF_EVENT_MANAGED")};
        if(args.Length!=3)throw new ArgumentException("Candidate, mod GameData and KSP Managed directories are required");
        foreach(var directory in args)
            if(!Directory.Exists(directory))throw new DirectoryNotFoundException(directory);
        foreach(var directory in new[]{args[2],args[1],args[0]})
            foreach(var file in Directory.GetFiles(directory,"*.dll",SearchOption.AllDirectories))
                Assemblies[Path.GetFileNameWithoutExtension(file)]=file;
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>
        {
            string path;
            return Assemblies.TryGetValue(new AssemblyName(eventArgs.Name).Name,out path)?Assembly.LoadFrom(path):null;
        };
        var native=Assembly.LoadFrom(Assemblies["DeepFreeze"]);
        Check(native.GetName().Version.ToString()=="1.32.0.0"&&
            native.ManifestModule.ModuleVersionId==new Guid("3dfd9a02-2102-4022-9ef5-8326bd22a723"),"native binary matches the affected log's DeepFreeze module");
        DeepFreezeVesselEventRegression();
        Console.WriteLine("PASS "+checks+" identity-policy, native-IL and Harmony-installation assertions. No rendered gameplay or crew-save acceptance claim.");
    }
}
