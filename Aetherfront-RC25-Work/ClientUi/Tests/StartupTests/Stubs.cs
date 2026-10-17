using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

public static class UrlDir { public static string ApplicationRootPath; }
public class AssemblyLoader {
    public sealed class LoadedAssembly {
        public Assembly assembly;public string path;
        public LoadedAssembly(Assembly assembly,string path){this.assembly=assembly;this.path=path;}
    }
    public static List<LoadedAssembly> loadedAssemblies;
}
public class KSPAddon { public enum Startup { Instantly } }
public sealed class AddonLoader {
    public int Created;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartAddon(AssemblyLoader.LoadedAssembly asm,Type type,KSPAddon addon,KSPAddon.Startup level) {
        Created++;
        object instance=Activator.CreateInstance(type);
        type.GetMethod("Awake")?.Invoke(instance,null);
    }
    public void Dispatch(AssemblyLoader.LoadedAssembly asm,Type type){StartAddon(asm,type,new KSPAddon(),KSPAddon.Startup.Instantly);}
}
namespace UnityEngine {
    public class Object {
        public static readonly List<Object> Destroyed=new List<Object>();
        public static void Destroy(Object value){Destroyed.Add(value);}
    }
    public class Behaviour:Object { public bool enabled=true; }
    public static class Debug {
        public static readonly List<string> Messages=new List<string>();
        public static void LogError(object value){Messages.Add(value.ToString());}
        public static void LogWarning(object value){Messages.Add(value.ToString());}
    }
}
namespace LmpClient {
    public class MainSystem:UnityEngine.Behaviour {
        public static MainSystem Singleton;
        public bool Enabled=true;
        public void Awake(){StartupSideEffects.CanonicalAwakes++;}
    }
    public class ColorEffect:UnityEngine.Behaviour { }
}
public static class StartupSideEffects {
    public static int LegacyAwakes,CanonicalAwakes;
    public static void LegacyAwake(){LegacyAwakes++;}
}
