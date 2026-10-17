using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LmpClient;
using LmpClient.Apf;

internal static class Program {
    private static int checks;
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    private static Type AddLegacyType(ModuleBuilder module,string name) {
        var type=module.DefineType(name,TypeAttributes.Public,typeof(UnityEngine.Behaviour));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var awake=type.DefineMethod("Awake",MethodAttributes.Public,typeof(void),Type.EmptyTypes);
        var il=awake.GetILGenerator();il.Emit(OpCodes.Call,typeof(StartupSideEffects).GetMethod("LegacyAwake"));il.Emit(OpCodes.Ret);
        return type.CreateType();
    }
    public static void Main() {
        UrlDir.ApplicationRootPath=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FixtureRoot"));
        string canonical=Path.Combine(UrlDir.ApplicationRootPath,"GameData","LunaMultiplayer","Plugins","LmpClient.dll");
        string nested=Path.Combine(UrlDir.ApplicationRootPath,"Resources","GameData","LunaMultiplayer","Plugins","LmpClient.dll");
        Check(CanonicalStartup.IsCanonicalPath(canonical),"Exact canonical path allowed");
        Check(CanonicalStartup.IsCanonicalPath(canonical.ToUpperInvariant()),"Windows path comparison ignores case");
        Check(CanonicalStartup.IsCanonicalPath(Path.Combine(Path.GetDirectoryName(canonical),"..","Plugins","LmpClient.dll")),"Canonical path normalized");
        Check(!CanonicalStartup.IsCanonicalPath(nested),"Nested Resources copy rejected");
        Check(!CanonicalStartup.IsCanonicalPath(canonical+".old"),"Suffix copy rejected");
        Check(!CanonicalStartup.IsCanonicalPath(null) && !CanonicalStartup.IsCanonicalPath("LmpClient.dll"),"Unknown or relative path rejected");
        var current=typeof(MainSystem).Assembly;
        var canonicalRegistration=new AssemblyLoader.LoadedAssembly(current,canonical);
        var sameAssemblyDuplicate=new AssemblyLoader.LoadedAssembly(current,nested);
        var oldAssembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(current.GetName().Name),AssemblyBuilderAccess.Run);
        var oldModule=oldAssembly.DefineDynamicModule("OldClient");
        var oldMain=AddLegacyType(oldModule,"LmpClient.MainSystem");
        var oldDrawer=AddLegacyType(oldModule,"LmpClient.DebugDrawer");
        var oldRegistration=new AssemblyLoader.LoadedAssembly(oldAssembly,nested);
        AssemblyLoader.loadedAssemblies=new List<AssemblyLoader.LoadedAssembly>{canonicalRegistration,oldRegistration};
        var owner=new MainSystem();
        Check(CanonicalStartup.AllowAwake(owner),"Canonical registered byte-load allowed; guard installed");
        MainSystem.Singleton=owner;
        Check(CanonicalStartup.Owns(owner),"Canonical component owns lifecycle");
        var loader=new AddonLoader();
        loader.Dispatch(oldRegistration,oldMain);loader.Dispatch(oldRegistration,oldDrawer);
        Check(loader.Created==0 && StartupSideEffects.LegacyAwakes==0,"Real Harmony prefix skips old MainSystem and DebugDrawer before AddComponent/Awake");
        loader.Dispatch(sameAssemblyDuplicate,typeof(MainSystem));
        Check(loader.Created==0,"Same assembly duplicate registration blocked");
        loader.Dispatch(canonicalRegistration,typeof(MainSystem));
        Check(loader.Created==0 && StartupSideEffects.CanonicalAwakes==0,"Second canonical MainSystem not created");
        loader.Dispatch(canonicalRegistration,typeof(ColorEffect));
        Check(loader.Created==1,"Other canonical client addons retained");
        var unrelatedAssembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("UnrelatedAddonFixture"),AssemblyBuilderAccess.Run);
        var unrelatedType=AddLegacyType(unrelatedAssembly.DefineDynamicModule("Unrelated"),"UnrelatedAddon");
        loader.Dispatch(new AssemblyLoader.LoadedAssembly(unrelatedAssembly,nested),unrelatedType);
        Check(loader.Created==2 && StartupSideEffects.LegacyAwakes==1,"Unrelated assembly addons not suppressed");
        var duplicate=new MainSystem();
        Check(!CanonicalStartup.AllowAwake(duplicate),"Duplicate direct component rejected");
        Check(!duplicate.Enabled && !duplicate.enabled && UnityEngine.Object.Destroyed.Contains(duplicate),"Rejected component disabled and destroyed");
        Check(!CanonicalStartup.Owns(duplicate) && ReferenceEquals(MainSystem.Singleton,owner),"Rejected cleanup cannot own/disconnect canonical singleton");
        Check(!CanonicalStartup.AllowAwake(owner) && owner.Enabled && owner.enabled && !UnityEngine.Object.Destroyed.Contains(owner),"Repeated owner Awake does not reinitialize or destroy owner");
        Check(!CanonicalStartup.AllowAddon(null,oldMain),"Unknown old-client registration path rejected");
        Check(CanonicalStartup.AllowAddon(null,unrelatedType),"Unknown unrelated addon unaffected");
        typeof(CanonicalStartup).GetField("owner",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,null);MainSystem.Singleton=null;
        AssemblyLoader.loadedAssemblies=new List<AssemblyLoader.LoadedAssembly>{sameAssemblyDuplicate};
        var nestedComponent=new MainSystem();
        Check(!CanonicalStartup.AllowAwake(nestedComponent) && !nestedComponent.enabled,"Noncanonical own assembly never initializes");
        Check(!CanonicalStartup.Owns(nestedComponent),"Noncanonical cleanup rejected");
        new Harmony("aetherfront.canonical-client-startup").UnpatchAll("aetherfront.canonical-client-startup");
        Console.WriteLine("PASS "+checks+" startup assertions; real production Harmony prefix; old distinct-assembly and same-assembly addons skipped; no game/filesystem mutations.");
    }
}
