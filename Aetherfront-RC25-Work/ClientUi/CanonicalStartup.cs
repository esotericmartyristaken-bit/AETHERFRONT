using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LmpClient.Apf
{
    public static class CanonicalStartup
    {
        private static MainSystem owner;
        private static bool installed;

        public static bool AllowAwake(MainSystem instance)
        {
            if(ReferenceEquals(owner,instance))return false;
            if(!ReferenceEquals(owner,null) || !ReferenceEquals(MainSystem.Singleton,null)) {
                Reject(instance,"Duplicate MainSystem component rejected.");return false;
            }
            if(!IsCanonicalAssembly(typeof(CanonicalStartup).Assembly)) {
                Reject(instance,"Client startup rejected outside GameData/LunaMultiplayer/Plugins/LmpClient.dll.");return false;
            }
            try { InstallAddonGuard(); }
            catch(Exception error) {
                Reject(instance,"Canonical addon guard could not be installed; client startup stopped: "+error);return false;
            }
            owner=instance;
            return true;
        }

        public static bool Owns(MainSystem instance)
        {
            return !ReferenceEquals(owner,null) && ReferenceEquals(owner,instance) && ReferenceEquals(MainSystem.Singleton,instance);
        }

        private static void Reject(MainSystem instance,string reason)
        {
            Debug.LogError("[Aetherfront] "+reason+" Close KSP and remove or approve disabling the duplicate client installation.");
            instance.Enabled=false;
            ((Behaviour)instance).enabled=false;
            // Audited OnDestroy/OnApplicationQuit prefixes prevent a rejected
            // component from disconnecting the canonical component's network.
            UnityEngine.Object.Destroy(instance);
        }

        private static void InstallAddonGuard()
        {
            if(installed)return;
            var method=typeof(AddonLoader).GetMethod("StartAddon",BindingFlags.Instance|BindingFlags.NonPublic,null,
                new[]{typeof(AssemblyLoader.LoadedAssembly),typeof(Type),typeof(KSPAddon),typeof(KSPAddon.Startup)},null);
            if(method==null || method.ReturnType!=typeof(void))throw new MissingMethodException("Expected KSP 1.12.5 AddonLoader.StartAddon signature.");
            var prefix=typeof(CanonicalStartup).GetMethod("AllowAddon",BindingFlags.Public|BindingFlags.Static);
            new HarmonyLib.Harmony("aetherfront.canonical-client-startup").Patch(method,new HarmonyMethod(prefix));
            installed=true;
        }

        // KSP identifies addons by Type plus LoadedAssembly, not by assembly name.
        // Check its actual registration path: KSP may load rewritten DLL bytes,
        // leaving Assembly.Location empty. Never mutate its assembly/type lists.
        public static bool AllowAddon(AssemblyLoader.LoadedAssembly __0,Type __1)
        {
            if(__1==null || __1.Assembly.GetName().Name!=typeof(MainSystem).Assembly.GetName().Name ||
                __1.Assembly.GetType("LmpClient.MainSystem",false)==null)return true;
            if(__0==null || !IsCanonicalPath(__0.path)) {
                Debug.LogWarning("[Aetherfront] Ignoring duplicate client addon "+__1.FullName+" from "+(__0==null?"an unknown path":__0.path));
                return false;
            }
            return __1.FullName!="LmpClient.MainSystem" || ReferenceEquals(owner,null);
        }

        private static bool IsCanonicalAssembly(Assembly assembly)
        {
            if(AssemblyLoader.loadedAssemblies!=null) {
                foreach(var loaded in AssemblyLoader.loadedAssemblies)
                    if(ReferenceEquals(loaded.assembly,assembly) && IsCanonicalPath(loaded.path))return true;
                return false;
            }
            return IsCanonicalPath(assembly.Location);
        }

        public static bool IsCanonicalPath(string path)
        {
            if(string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))return false;
            string expected=Path.GetFullPath(Path.Combine(UrlDir.ApplicationRootPath,"GameData","LunaMultiplayer","Plugins","LmpClient.dll"));
            return string.Equals(Path.GetFullPath(path),expected,StringComparison.OrdinalIgnoreCase);
        }
    }
}
