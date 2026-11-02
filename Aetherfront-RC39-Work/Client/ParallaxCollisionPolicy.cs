using System;
using System.IO;
using HarmonyLib;
using LmpClient;
using LmpClient.Network;
using LmpCommon.Enums;
using UnityEngine;

namespace Aetherfront.SessionPolicy
{
    [KSPAddon(KSPAddon.Startup.Instantly,true)]
    public sealed class ParallaxCollisionPolicy : MonoBehaviour
    {
        internal static readonly Guid SupportedParallax=new Guid("dbeb6341-53e9-493d-bc96-27a5fb3320e6");
        internal const string HarmonyId="aetherfront.session.parallax-collisions-off";
        private static ParallaxState state;
        private static bool loaded,installed,hooksReady;
        private static string failure;
        private static readonly string Blocked="Connection blocked: Parallax collisions must be off. Restart KSP to apply the required server settings.";
        private void Awake()
        {
            if(installed){Destroy(this);return;}
            DontDestroyOnLoad(this);installed=true;
            try{Install(new Harmony(HarmonyId));hooksReady=failure==null;}
            catch(Exception e){failure=e.GetBaseException().Message;Debug.LogError("[Aetherfront Collision Policy] Could not install join protection: "+failure);}
        }
        internal static void Install(Harmony harmony)
        {
            var loader=AccessTools.TypeByName("Parallax.ConfigLoader");
            if(loader==null)return;
            try{
                if(loader.Assembly.ManifestModule.ModuleVersionId!=SupportedParallax)throw new InvalidOperationException("Unreviewed Parallax version");
                var manager=loader.Assembly.GetType("Parallax.CollisionManager",true);
                state=new ParallaxState(loader,manager);
                Patch(harmony,loader,"InitializeGlobalSettings",true,nameof(BeforeGlobal));
                Patch(harmony,loader,"InitializeGlobalSettings",false,nameof(AfterGlobal));
                Patch(harmony,loader,"ModuleManagerPostLoad",false,nameof(AfterLoaded));
                Patch(harmony,loader.Assembly.GetType("Parallax.ParallaxSettings",true),"SaveSettings",true,nameof(BeforeSave));
                Patch(harmony,manager,"Load",true,nameof(BeforeBodyLoad));
                Patch(harmony,manager,"Update",true,nameof(BeforeCollisionUpdate));
            }catch(Exception e){state=null;failure=e.GetBaseException().Message;Debug.LogError("[Aetherfront Collision Policy] "+failure+"; joins will remain blocked.");}
        }
        private static void Patch(Harmony harmony,Type type,string name,bool prefix,string callback)
        {
            var method=AccessTools.DeclaredMethod(type,name);
            if(method==null)throw new MissingMethodException(type.FullName,name);
            var hook=new HarmonyMethod(typeof(ParallaxCollisionPolicy),callback);
            if(prefix)harmony.Patch(method,prefix:hook);else harmony.Patch(method,postfix:hook);
        }
        // This assembly belongs only to the APF client. Deleting connection
        // configuration must not turn a mandatory physics policy into an opt-out.
        private static void BeforeGlobal(UrlDir.UrlConfig __0)
        {
            try{
                var node=__0?.config?.GetNode("ScatterSystemSettings");
                if(node==null)throw new InvalidOperationException("Parallax collision settings node unavailable");
                string requested=node.GetValue("collisionLevel");
                node.SetValue("collisionLevel","-1",true);
                int value;string diagnostic=int.TryParse(requested,out value)?value.ToString(System.Globalization.CultureInfo.InvariantCulture):"invalid";
                if(requested!="-1")Debug.LogWarning("[Aetherfront Collision Policy] Overrode requested collision level "+diagnostic+" with required off level -1.");
            }catch(Exception e){Fail(e);}
        }
        private static void AfterGlobal(){if(state!=null)try{state.ForceLevelOff();}catch(Exception e){Fail(e);}}
        private static void BeforeSave(object __instance)
        {
            if(state==null)return;
            try{state.ForceLevelOff(__instance);state.ForceLevelOff();}catch(Exception e){Fail(e);}
        }
        private static void AfterLoaded()
        {
            if(state==null)return;
            try{state.Enforce();loaded=true;failure=null;Debug.Log("[Aetherfront Collision Policy] Parallax collision runtime initialized off; native collision processing locked.");}
            catch(Exception e){Fail(e);}
        }
        private static bool BeforeBodyLoad()
        {
            if(state==null||!loaded)return false;
            try{state.Enforce();return true;}
            catch(Exception e){Fail(e);return false;}
        }
        private static bool BeforeCollisionUpdate()
        {
            if(state==null||!loaded)return false;
            try{
                state.ForceLevelOff();
                if(state.HasWork){state.Enforce();Debug.LogWarning("[Aetherfront Collision Policy] Prevented Parallax collision work from being enabled.");}
            }catch(Exception e){Fail(e);}
            // No collision jobs or collider creation can run through this frame
            // entry point, even if another settings UI changes a value later.
            return false;
        }
        internal static bool ApplyJoinCheck(bool nativeReady,Func<bool> enforce,Action<string> reject)
        {
            if(!nativeReady)return false;
            try{if(enforce())return true;}catch{}
            reject(Blocked);return false;
        }
        // The RC39 native networking method calls this after its normal install
        // gate and before starting a connection. No successful-join cache, and
        // no Harmony dependency for the final admission decision.
        public static bool AllowNativeJoin()
        {
            if(!installed||!hooksReady){Reject(Blocked);return false;}
            if(!MainSystem.IsUnityThread){Reject(Blocked);return false;}
            return ApplyJoinCheck(true,VerifyJoin,Reject);
        }
        private static void Reject(string message)
        {
                if(MainSystem.Singleton!=null)MainSystem.Singleton.Status=message;
                Debug.LogWarning("[Aetherfront Collision Policy] Join denied: "+(failure??"runtime policy unavailable"));
        }
        private static bool VerifyJoin()
        {
            if(state==null){
                // A genuine Potato/vanilla starter has no collision subsystem.
                // A present but failed/unsupported DLL is never treated as absent.
                bool file=File.Exists(Path.Combine(KSPUtil.ApplicationRootPath,"GameData/ParallaxContinued/Plugins/ParallaxContinued.dll"));
                return failure==null&&!file&&AccessTools.TypeByName("Parallax.ConfigLoader")==null;
            }
            if(!loaded||failure!=null)return false;
            try{
                bool repaired=state.Enforce();
                Debug.Log("[Aetherfront Collision Policy] Fresh join check passed: collisions off"+(repaired?" (changed collision settings restored).":"."));
                return true;
            }catch(Exception e){Fail(e);return false;}
        }
        private static void Fail(Exception error)
        {
            string message=error.GetBaseException().Message;
            if(failure==message)return;failure=message;
            Debug.LogError("[Aetherfront Collision Policy] "+message);
            if(MainSystem.NetworkState>ClientState.Disconnected)
                NetworkConnection.Disconnect(Blocked);
        }
    }
}
