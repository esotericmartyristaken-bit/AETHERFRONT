// Test-only instrumentation. Never included in the client release inventory.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;
using HarmonyLib;
using Aetherfront.Presentation;

[KSPAddon(KSPAddon.Startup.Instantly,true)]
public sealed class RC38LiveProbe : MonoBehaviour
{
    const string Allowed=@"C:\Users\jkell\Documents\Codex\2026-09-07\kesas-multiplayer-beta\work\apf-test";
    const string Output=@"C:\Users\jkell\Documents\Codex\2026-09-07\kesas-multiplayer-beta\work\aetherfront-controller-rc28-20261017\tests\rc38-live";
    string file, command; float next, stableSince; GameScenes lastScene; bool entered, failed;
    static BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    void Awake()
    {
        if(!String.Equals(Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\','/'),Allowed,StringComparison.OrdinalIgnoreCase)){Destroy(this);return;}
        DontDestroyOnLoad(this); Directory.CreateDirectory(Output);
        var id=Process.GetCurrentProcess().Id;
        file=Path.Combine(Output,"launch-"+id+".txt");command=Path.Combine(Output,"command-"+id+".txt");
        Log("BEGIN test-only PID-scoped commands. No connect or updater actions; no automatic quit.");
    }
    void Log(string message)
    {
        File.AppendAllText(file,DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine);
        UnityEngine.Debug.Log("[RC38LiveProbe] "+message);
    }
    void Check(bool good,string message){Log((good?"PASS ":"FAIL ")+message);if(!good)failed=true;}
    void Update()
    {
        if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+2;
        try {
            if(lastScene!=HighLogic.LoadedScene){lastScene=HighLogic.LoadedScene;stableSince=Time.realtimeSinceStartup;entered=false;Log("SCENE "+lastScene);}
            if(!entered && HighLogic.LoadedScene>=GameScenes.MAINMENU && Time.realtimeSinceStartup-stableSince>15){entered=true;Inspect();}
            if(!File.Exists(command))return;
            string value=File.ReadAllText(command).Trim();File.Move(command,command+".used-"+DateTime.UtcNow.Ticks);
            Log("COMMAND "+value);
            if(value=="load-fixture"){
                if(HighLogic.LoadedScene!=GameScenes.MAINMENU)throw new Exception("Fixture load requires main menu");
                var game=GamePersistence.LoadGame("persistent","RC38Probe",true,true);
                if(game==null)throw new Exception("Fixture could not load");
                HighLogic.SaveFolder="RC38Probe";
                HighLogic.CurrentGame=game;
                game.Start();
            } else if(value=="vab") EditorDriver.StartEditor(EditorFacility.VAB);
            else if(value=="sph") EditorDriver.StartEditor(EditorFacility.SPH);
            else if(value=="ksc") HighLogic.LoadScene(GameScenes.SPACECENTER);
            else if(value=="inspect") Inspect();
            else if(value=="quit-own-probe") {Log("END own test instance; failed="+failed);Application.Quit();}
            else throw new Exception("Unknown explicit command");
        }catch(Exception e){failed=true;Log("PROBE ERROR "+e);}
    }
    static object Member(object instance,string name)
    {
        var type=instance as Type ?? instance.GetType();object target=instance is Type?null:instance;
        return type.GetField(name,Flags)?.GetValue(target) ?? type.GetProperty(name,Flags)?.GetValue(target,null);
    }
    void Inspect()
    {
        Log("READY scene="+HighLogic.LoadedScene+" parts="+PartLoader.LoadedPartsList.Count+" mode="+Screen.fullScreenMode+" size="+Screen.width+"x"+Screen.height);
        if(HighLogic.LoadedScene==GameScenes.MAINMENU){
            Check(PartLoader.LoadedPartsList.Count>100,"part compilation completed");
            // The preserved KSPCF policy intentionally disables MH missions and
            // its Installed flag while retaining the part pack.
            Check(PartLoader.LoadedPartsList.Any(p=>p.partUrl!=null&&p.partUrl.StartsWith("SquadExpansion/MakingHistory/",StringComparison.Ordinal)),"Making History parts compiled in this startup");
            Check(Expansions.ExpansionsLoader.IsExpansionInstalled("Serenity"),"stock loader recognizes Breaking Ground in this startup");
            Check(Screen.fullScreenMode==FullScreenMode.Windowed,"Unity windowed mode prevents exclusive-focus minimization");
            var handle=NativeWindow.FindOwnWindow();
            Check(handle!=IntPtr.Zero && !NativeWindow.IsIconic(handle),"own game window not minimized");
            if(handle!=IntPtr.Zero){
                var style=(uint)NativeWindow.GetStyle(handle,NativeWindow.StyleIndex);
                Check((style&NativeWindow.OverlappedWindow)==0 && (style&NativeWindow.Popup)!=0,"native borderless style active");
                Check(NativeWindow.SendMessage(handle,0x7f,new IntPtr(1),IntPtr.Zero)!=IntPtr.Zero,"large APF icon assigned");
                Check(NativeWindow.SendMessage(handle,0x7f,IntPtr.Zero,IntPtr.Zero)!=IntPtr.Zero,"small APF icon assigned");
            }
            var globals=GameDatabase.Instance.GetConfigNodes("ParallaxGlobal");
            Check(globals.Length>0 && globals.All(n=>n.GetNode("ScatterSystemSettings")?.GetValue("collisionLevel")=="-1"),"ModuleManager final Parallax collisionLevel=-1");
            var loader=AccessTools.TypeByName("Parallax.ConfigLoader");
            if(loader!=null){
                var settings=Member(loader,"parallaxGlobalSettings");
                var scatter=settings==null?null:Member(settings,"scatterGlobalSettings");
                var level=scatter==null?null:Member(scatter,"collisionLevel");
                Log("PARALLAX runtime collision level="+(level??"unknown"));
                if(level!=null)Check(Convert.ToInt32(level)==-1,"Parallax actual runtime collisions disabled");
            }
        }
        var db=AccessTools.TypeByName("HUDReplacer.Database");
        Check(db!=null,"HUDReplacer plugin loaded");
        if(db!=null){
            var images=Member(db,"Images") as IDictionary;var scenes=Member(db,"SceneImages") as IDictionary;
            Log("HUD replacements="+images?.Count+" scene groups="+scenes?.Count);
            Check(images!=null&&images.Count>50,"ZTheme replacement textures loaded");
        }
        if(HighLogic.LoadedSceneIsEditor){
            var method=AccessTools.Method(AccessTools.TypeByName("WasdEditorCamera.WasdEditorCameraBehaviour"),"LateUpdate");
            var info=Harmony.GetPatchInfo(method);
            Check(info!=null&&info.Transpilers.Any(p=>p.owner=="aetherfront.presentation.editor-scroll"),"UI wheel filter installed on actual WASD camera");
            Check(EventSystem.current!=null && EventSystem.current.currentInputModule!=null,"editor Unity pointer system available");
            Check(EditorLogic.fetch!=null,"editor initialized without craft edits");
        }
        ScreenCapture.CaptureScreenshot(Path.Combine(Output,"launch-"+Process.GetCurrentProcess().Id+"-"+HighLogic.LoadedScene+"-"+DateTime.UtcNow.Ticks+".png"));
    }
}
