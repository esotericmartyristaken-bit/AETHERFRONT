using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Collections.Concurrent;
using HarmonyLib;
using Aetherfront.SessionPolicy;

internal static class Program
{
    static int checks;
    static void Check(bool good,string why){checks++;if(!good)throw new Exception(why);}
    static void Main()
    {
        string game=Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        var lookup=Directory.GetFiles(Path.Combine(game,"GameData"),"*.dll",SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(game,"KSP_x64_Data/Managed"),"*.dll"))
            .GroupBy(p=>Path.GetFileNameWithoutExtension(p),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>lookup.TryGetValue(new AssemblyName(e.Name).Name,out string p)?Assembly.LoadFrom(p):null;
        Run(game);
        Console.WriteLine("PASS "+checks+" session-policy assertions under actual KSP Mono: struct writeback, cached flags, native cleanup delegation, repeated join checks, fail-closed errors and shipped Parallax hook signatures.");
    }
    static void Run(string game)
    {
        for(int level=-2;level<=5;level++){
            Reset();Loader.parallaxGlobalSettings.scatterGlobalSettings=new ScatterSettings{collisionLevel=level,density=0.37f};
            var state=new ParallaxState(typeof(Loader),typeof(Manager));state.Enforce();
            Check(state.Level==-1,"all alternate collision levels forced to exact off sentinel");
            Check(Loader.parallaxGlobalSettings.scatterGlobalSettings.density==0.37f,"other struct settings preserved");
            Check(state.CachedScattersOff&&!state.HasWork,"no latent per-scatter or manager collisions");
        }
        Reset();var adapter=new ParallaxState(typeof(Loader),typeof(Manager));
        var visual=Loader.parallaxScatterBodies["Kerbin"].scatters["rock"];
        visual.collideable=true;visual.collideableArrayIndex=2;
        Loader.parallaxScatterBodies["Kerbin"].collideableScatters=new[]{visual};
        Check(adapter.Level==-1&&!adapter.CachedScattersOff,"global off alone does not hide cached collisions");
        Check(adapter.Enforce()&&!visual.collideable&&visual.collideableArrayIndex==-1,"cached flags/index reset");
        Check(visual.renderDensity==17,"scatter visual settings untouched");
        Check(!adapter.Enforce(),"unchanged policy recheck is idempotent");
        var own=new Settings{scatterGlobalSettings=new ScatterSettings{collisionLevel=4,density=0.2f}};
        adapter.ForceLevelOff(own);Check(own.scatterGlobalSettings.collisionLevel==-1&&own.scatterGlobalSettings.density==0.2f,"saving a distinct settings object cannot persist enabled collisions");
        foreach(string work in new[]{"count","array","objects","data","incoming","outgoing","quadJob","colliderJob"}){
            Reset();adapter=new ParallaxState(typeof(Loader),typeof(Manager));
            switch(work){
                case "count":Manager.numCollideableScatters=1;break;
                case "array":Manager.collideableScatters=new[]{new Scatter()};break;
                case "objects":Manager.activeObjects[0][1]=new object();break;
                case "data":Manager.collisionData.Length=1;break;
                case "incoming":Manager.incomingData.Enqueue(new object());break;
                case "outgoing":Manager.outgoingData.Enqueue(new object());break;
                case "quadJob":Manager.inQuadJob=true;break;
                case "colliderJob":Manager.inColliderJob=true;break;
            }
            Check(adapter.HasWork,"active work recognized: "+work);adapter.Enforce();
            Check(Manager.CleanupCalls==1&&!adapter.HasWork,"native job/object cleanup delegated once: "+work);
        }
        Reset();adapter=new ParallaxState(typeof(Loader),typeof(Manager));Manager.inQuadJob=true;Manager.Instance=null;
        bool refused=false;try{adapter.Enforce();}catch(InvalidOperationException){refused=true;}
        Check(refused,"uninitialized native manager cannot be claimed safe");
        Reset();adapter=new ParallaxState(typeof(Loader),typeof(Manager));Manager.inQuadJob=true;Manager.FailCleanup=true;
        refused=false;try{adapter.Enforce();}catch(TargetInvocationException){refused=true;}
        Check(refused&&adapter.HasWork,"failed cleanup remains failure, not a forged off state");
        Reset();Loader.parallaxScatterBodies["Kerbin"].scatters["rock"]=null;
        refused=false;try{new ParallaxState(typeof(Loader),typeof(Manager)).Enforce();}catch(InvalidOperationException){refused=true;}
        Check(refused,"malformed runtime registry fails closed");
        var method=typeof(ParallaxCollisionPolicy).GetMethod("ApplyJoinCheck",BindingFlags.NonPublic|BindingFlags.Static);
        int attempts=0,rejected=0;bool safe=true;
        Func<bool> enforce=()=>{attempts++;return safe;};Action<string> reject=s=>{Check(s.Contains("Parallax")&&s.Contains("Restart"),"clear rejection instruction");rejected++;};
        Func<bool,bool> join=native=>(bool)method.Invoke(null,new object[]{native,enforce,reject});
        Check(!join(false)&&attempts==0,"normal download/preparation path is not obstructed");
        Check(join(true)&&attempts==1,"first actual join checked");
        Check(join(true)&&attempts==2,"unchanged repeat join still performs a fresh check");
        safe=false;Check(!join(true)&&attempts==3&&rejected==1,"reconnect after tampering checked again, no success cache");
        safe=true;Check(join(true)&&attempts==4,"corrected state accepted on next fresh check");
        Func<bool> broken=()=>throw new IOException("simulated unavailable settings");
        Check(!(bool)method.Invoke(null,new object[]{true,broken,reject})&&rejected==2,"inspection exceptions deny admission");
        // Review the installed production assembly metadata without running
        // Unity native jobs in this standalone Mono process.
        var parallax=Assembly.LoadFrom(Path.Combine(game,"GameData/ParallaxContinued/Plugins/ParallaxContinued.dll"));
        Check(parallax.ManifestModule.ModuleVersionId==new Guid("dbeb6341-53e9-493d-bc96-27a5fb3320e6"),"exact shipped Parallax module reviewed");
        new ParallaxState(parallax.GetType("Parallax.ConfigLoader",true),parallax.GetType("Parallax.CollisionManager",true));
        Check(true,"production field graph including boxed collision struct binds");
        var harmony=new Harmony("aetherfront.session.tests.signatures");
        typeof(ParallaxCollisionPolicy).GetMethod("Install",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{harmony});
        foreach(var target in new[]{new[]{"Parallax.ConfigLoader","InitializeGlobalSettings"},new[]{"Parallax.ConfigLoader","ModuleManagerPostLoad"},new[]{"Parallax.ParallaxSettings","SaveSettings"},new[]{"Parallax.CollisionManager","Load"},new[]{"Parallax.CollisionManager","Update"}}){
            var m=AccessTools.DeclaredMethod(parallax.GetType(target[0],true),target[1]);var patch=Harmony.GetPatchInfo(m);
            Check(patch!=null&&patch.Owners.Contains(harmony.Id),"actual shipped hook installed: "+target[1]);
        }
        harmony.UnpatchAll(harmony.Id);
    }
    static void Reset()
    {
        Loader.parallaxGlobalSettings=new Settings{scatterGlobalSettings=new ScatterSettings{collisionLevel=-1,density=0.37f}};
        var body=new Body();body.scatters["rock"]=new Scatter{collideableArrayIndex=-1,renderDensity=17};
        Loader.parallaxScatterBodies=new Dictionary<string,Body>{{"Kerbin",body}};
        Manager.Instance=new Manager();Manager.numCollideableScatters=0;Manager.collideableScatters=new Scatter[0];
        Manager.activeObjects=new[]{new Dictionary<int,object>()};Manager.collisionData=new Data();
        Manager.incomingData=new ConcurrentQueue<object>();Manager.outgoingData=new ConcurrentQueue<object>();
        Manager.inQuadJob=Manager.inColliderJob=Manager.FailCleanup=false;Manager.CleanupCalls=0;
    }
    internal struct ScatterSettings { public int collisionLevel;public float density; }
    internal sealed class Settings {public ScatterSettings scatterGlobalSettings;}
    internal sealed class Scatter {public bool collideable;public int collideableArrayIndex;public int renderDensity;}
    internal sealed class Body {public Dictionary<string,Scatter> scatters=new Dictionary<string,Scatter>();public Scatter[] collideableScatters=new Scatter[0];}
    internal static class Loader {public static Settings parallaxGlobalSettings;public static Dictionary<string,Body> parallaxScatterBodies;}
    internal sealed class Data {public int Length;}
    internal sealed class Manager
    {
        public static Manager Instance;public static int numCollideableScatters;public static Scatter[] collideableScatters;
        public static Dictionary<int,object>[] activeObjects;public static Data collisionData;
        public static ConcurrentQueue<object> incomingData,outgoingData;public static bool inQuadJob,inColliderJob,FailCleanup;public static int CleanupCalls;
        public void Cleanup(){CleanupCalls++;if(FailCleanup)throw new IOException("simulated native cleanup failure");
            numCollideableScatters=0;collideableScatters=new Scatter[0];foreach(var map in activeObjects)map.Clear();collisionData.Length=0;
            incomingData=new ConcurrentQueue<object>();outgoingData=new ConcurrentQueue<object>();inQuadJob=inColliderJob=false;}
    }
}
