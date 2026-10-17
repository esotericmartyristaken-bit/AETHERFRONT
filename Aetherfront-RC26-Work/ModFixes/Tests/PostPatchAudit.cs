using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
internal static class PostPatchAudit
{
    static string workspace;
    public static int Main(string[] args)
    {
        workspace=Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
            string path=Path.Combine(workspace,"Aetherfront-KSP-Smoke/KSP_x64_Data/Managed",new AssemblyName(e.Name).Name+".dll");
            return File.Exists(path)?Assembly.LoadFrom(path):null;
        };
        try { return Run(args[1]); } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run(string directory)
    {
        ConfigNode[] before=ConfigNode.Load(Path.Combine(directory,"potato/BeforePolicyParts.cfg")).GetNodes("PART");
        ConfigNode[] after=ConfigNode.Load(Path.Combine(directory,"potato/FinalParts.cfg")).GetNodes("PART");
        int groups=0,modules=0;
        foreach(ConfigNode part in after)
        {
            string name=part.GetValue("name");
            ConfigNode original=before.Single(p=>p.GetValue("name")==name);
            foreach(string type in new[]{"ModuleRCSFX","ModuleEnginesFX"})
            {
                ConfigNode[] a=original.GetNodes("MODULE").Where(m=>m.GetValue("name")==type).ToArray();
                ConfigNode[] b=part.GetNodes("MODULE").Where(m=>m.GetValue("name")==type).ToArray();
                if(a.Length!=b.Length || !a.Select(m=>m.ToString()).SequenceEqual(b.Select(m=>m.ToString()))) throw new Exception("Physics/effect-identity module changed: "+name+"/"+type);
                modules+=a.Length;
            }
            string[] oldGroups=original.GetNodes("EFFECTS").SelectMany(n=>n.GetNodes()).Select(n=>n.name).ToArray();
            ConfigNode[] newGroups=part.GetNodes("EFFECTS").SelectMany(n=>n.GetNodes()).ToArray();
            if(!oldGroups.SequenceEqual(newGroups.Select(n=>n.name))) throw new Exception("Effect group identities changed: "+name);
            foreach(ConfigNode group in newGroups)
            {
                if(group.nodes.Count!=0 || group.values.Count!=0) throw new Exception("Nonempty Potato effect group: "+name+"/"+group.name);
                groups++;
            }
        }
        foreach(string profile in new[]{"full","potato"})
            foreach(string line in File.ReadLines(Path.Combine(directory,profile+"/Selectors.tsv")).Skip(1))
                if(line.Split('\t')[4]!="1") throw new Exception("Nonunique or missing B9 selector: "+profile+" "+line);
        if(groups==0 || modules==0 || after.Length!=before.Length) throw new Exception("Incomplete physics/effect audit coverage");
        Console.WriteLine("PASS postpatch audit: parts="+after.Length+" unchangedPhysicsModules="+modules+" emptyNamedEffectGroups="+groups+"; all retained Full/Potato B9 selectors uniquely match.");
        return 0;
    }
}
