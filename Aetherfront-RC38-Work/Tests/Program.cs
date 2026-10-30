using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Aetherfront.Presentation;

internal static class Program
{
    private static int checks;
    private static readonly Dictionary<string,string> Lookup = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }

    private static void Main()
    {
        string game = Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        string candidate = Environment.GetEnvironmentVariable("APF_PRESENTATION_CANDIDATE");
        string overlay = Environment.GetEnvironmentVariable("APF_PRESENTATION_OVERLAY");
        if (!Directory.Exists(game) || !Directory.Exists(candidate) || !Directory.Exists(overlay)) throw new ArgumentException("Explicit runtime, candidate and overlay required");
        foreach (string path in Directory.GetFiles(Path.Combine(game,"GameData"),"*.dll",SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(game,"KSP_x64_Data/Managed"),"*.dll"))) Lookup[Path.GetFileNameWithoutExtension(path)] = path;
        Lookup["AetherfrontPresentationClient"] = Path.Combine(candidate,"AetherfrontPresentationClient.dll");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            string path;
            return Lookup.TryGetValue(new AssemblyName(args.Name).Name,out path) ? Assembly.LoadFrom(path) : null;
        };
        Run(game, overlay);
        Console.WriteLine("PASS " + checks + " assertions: actual KSP Mono, four WASD wheel sites, existing lock-patch coexistence, hidden-window branding/borderless and foreground preservation. No live KSP gameplay claim.");
    }

    private static void Run(string game, string overlay)
    {
        foreach (float wheel in new[] {-1f, -.125f, 0f, .125f, 1f})
        {
            Check(EditorScrollCompatibility.Filter(wheel,true,true,false)==0,"UI consumes camera wheel");
            Check(EditorScrollCompatibility.Filter(wheel,true,false,true)==0,"camera lock respected");
            Check(EditorScrollCompatibility.Filter(wheel,true,false,false)==wheel,"viewport wheel preserved");
            Check(EditorScrollCompatibility.Filter(wheel,false,true,true)==wheel,"outside editor unchanged");
        }
        var wasd = Assembly.LoadFrom(Path.Combine(game,"GameData/WasdEditorCamera/Plugins/WasdEditorCameraContinued.dll"));
        Check(wasd.ManifestModule.ModuleVersionId == EditorScrollCompatibility.SupportedWasd,"shipped WASD build fingerprint");
        var late = AccessTools.DeclaredMethod(wasd.GetType("WasdEditorCamera.WasdEditorCameraBehaviour",true),"LateUpdate",Type.EmptyTypes);
        Check(late != null,"actual WASD LateUpdate found");
        var before = PatchProcessor.GetOriginalInstructions(late);
        var mouse = AccessTools.Method(typeof(UnityEngine.Input),"GetAxis",new[]{typeof(string)});
        Check(before.Count(c=>c.Calls(mouse))==6,"four wheel plus two mouse-look reads");
        var after = EditorScrollCompatibility.RewriteWheelReads(before.Select(c=>new CodeInstruction(c))).ToList();
        Check(after.Count==before.Count,"no branches/instructions added to camera");
        Check(after.Count(c=>c.Calls(AccessTools.Method(typeof(EditorScrollCompatibility),"CameraAxis")))==4,"four scoped replacements");
        Check(after.Count(c=>c.Calls(mouse))==2,"mouse look remains native");
        Check(after.Select(c=>c.labels.Count).SequenceEqual(before.Select(c=>c.labels.Count)),"branch labels retained");
        bool rejected = false;
        try { EditorScrollCompatibility.RewriteWheelReads(new CodeInstruction[0]).ToArray(); } catch (InvalidOperationException) { rejected=true; }
        Check(rejected,"changed wheel call count fails closed");

        var oldHarmony = new Harmony("aetherfront.presentation.tests.existing-lock-fix");
        var newHarmony = new Harmony("aetherfront.presentation.tests.scroll");
        try
        {
            var continuity = Assembly.LoadFrom(Path.Combine(game,"GameData/LunaMultiplayer/Plugins/AetherfrontContinuityClient.dll"));
            continuity.GetType("Aetherfront.Continuity.Client.EditorInputCompatibility",true)
                .GetMethod("Install",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{oldHarmony});
            var installed = typeof(EditorScrollCompatibility).GetMethod("Install",BindingFlags.NonPublic|BindingFlags.Static)
                .Invoke(null,new object[]{newHarmony});
            Check((bool)installed,"real WASD scroll patch installs");
            var info = Harmony.GetPatchInfo(late);
            Check(info.Prefixes.Any(p=>p.owner==oldHarmony.Id),"existing RMB prefix retained");
            Check(info.Finalizers.Any(p=>p.owner==oldHarmony.Id),"existing RMB cleanup retained");
            Check(info.Transpilers.Any(p=>p.owner==newHarmony.Id),"new wheel transpiler coexists");
        }
        finally { newHarmony.UnpatchAll(newHarmony.Id); oldHarmony.UnpatchAll(oldHarmony.Id); }

        HiddenWindow(overlay);
        foreach (uint style in new uint[]{0,0x10CF0000,0x14CF0000,0x94000000,0x11CF0000})
        {
            uint transformed = NativeWindow.BorderlessStyle(style);
            Check((transformed&NativeWindow.OverlappedWindow)==0,"window chrome removed");
            Check((transformed&NativeWindow.Popup)!=0,"borderless popup style");
            Check((transformed&0x01000000)==0,"maximized frame state cleared");
            Check((transformed&0x14000000)==(style&0x14000000),"visibility/clipping flags preserved");
        }
    }

    [DllImport("user32",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(uint ex,string type,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window,out NativeWindow.Rectangle rect);

    private static void HiddenWindow(string overlay)
    {
        IntPtr foreground = GetForegroundWindow();
        IntPtr test = CreateWindowEx(0,"STATIC","APF invisible test",NativeWindow.OverlappedWindow,-32000,-32000,320,200,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        IntPtr large=IntPtr.Zero,small=IntPtr.Zero;
        try
        {
            Check(test!=IntPtr.Zero,"hidden native window created");
            Check(NativeWindow.FindOwnWindow("STATIC")==test,"own HWND discovered under shipped Mono without Process.MainWindowHandle");
            Check(NativeWindow.FindOwnWindow("AetherfrontNonexistentWindowClass")==IntPtr.Zero,"unknown window class is not borrowed from another process");
            Check(!IsWindowVisible(test),"test starts invisible");
            NativeWindow.RequireOwnWindow(test);
            var bounds = NativeWindow.MonitorBounds(test);
            NativeWindow.Borderless(test,bounds);
            Check(!IsWindowVisible(test),"borderless operation never shows test window");
            Check(GetForegroundWindow()==foreground,"borderless does not steal focus");
            NativeWindow.Rectangle rect;
            Check(GetWindowRect(test,out rect)&&rect.Left==bounds.Left&&rect.Top==bounds.Top&&rect.Right==bounds.Right&&rect.Bottom==bounds.Bottom,"borderless rectangle matches monitor");
            Check(((uint)NativeWindow.GetStyle(test,NativeWindow.StyleIndex)&NativeWindow.OverlappedWindow)==0,"actual native frame stripped");
            string path=Path.Combine(overlay,"GameData/LunaMultiplayer/Presentation/APF.ico");
            large=NativeWindow.LoadIcon(path,false); small=NativeWindow.LoadIcon(path,true);
            NativeWindow.SendMessage(test,0x80,new IntPtr(1),large);
            NativeWindow.SendMessage(test,0x80,IntPtr.Zero,small);
            Check(NativeWindow.SendMessage(test,0x7F,new IntPtr(1),IntPtr.Zero)==large,"large icon assigned");
            Check(NativeWindow.SendMessage(test,0x7F,IntPtr.Zero,IntPtr.Zero)==small,"small icon assigned");
            Check(NativeWindow.SetWindowText(test,"Aetherfront test"),"APF title assigned");
            Check(!IsWindowVisible(test)&&GetForegroundWindow()==foreground,"branding remains invisible and inactive");
            bool rejected=false;
            try { NativeWindow.RequireOwnWindow(IntPtr.Zero); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected,"invalid HWND rejected");
            uint owner;
            if(foreground!=IntPtr.Zero && NativeWindow.GetWindowThreadProcessId(foreground,out owner)!=0 && owner!=(uint)System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                rejected=false;
                try { NativeWindow.RequireOwnWindow(foreground); } catch(InvalidOperationException) { rejected=true; }
                Check(rejected,"foreign-process window rejected without modification");
            }
        }
        finally
        {
            if(test!=IntPtr.Zero) DestroyWindow(test);
            if(large!=IntPtr.Zero) NativeWindow.DestroyIcon(large);
            if(small!=IntPtr.Zero) NativeWindow.DestroyIcon(small);
        }
        Check(GetForegroundWindow()==foreground,"test cleanup leaves foreground unchanged");
    }
}
