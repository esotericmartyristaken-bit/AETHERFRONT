using System;
using System.IO;
using HarmonyLib;
using LmpClient.Apf;
using UnityEngine;

namespace Aetherfront.Presentation
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class DesktopPresentation : MonoBehaviour
    {
        private IntPtr window, largeIcon, smallIcon, oldLarge, oldSmall;
        private bool windows, borderless = true, prepared, resized;
        private float nextAttempt, applyWindowAfter;

        private void Awake()
        {
            if (!ApfClientSettings.Enabled) { Destroy(this); return; }
            DontDestroyOnLoad(this);
            try
            {
                bool patched = EditorScrollCompatibility.Install(new Harmony("aetherfront.presentation.editor-scroll"));
                Debug.Log("[Aetherfront Presentation] " + (patched ? "Four WASD camera wheel reads respect UI and camera locks." : "WASD camera absent; no wheel patch needed."));
            }
            catch (Exception error) { Debug.LogError("[Aetherfront Presentation] Editor scroll compatibility failed: " + error); }
            windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            if (!windows) return;
            try
            {
                string preferences = Path.Combine(KSPUtil.ApplicationRootPath, "APF/DesktopPresentation.cfg");
                NormalPath(preferences);
                if (File.Exists(preferences))
                {
                    bool value;
                    var node = ConfigNode.Load(preferences);
                    if (node != null && bool.TryParse(node.GetValue("borderlessWindow"), out value)) borderless = value;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(preferences));
                    var node = new ConfigNode(); node.AddValue("borderlessWindow", true); node.Save(preferences);
                }
            }
            catch (Exception error) { Debug.LogWarning("[Aetherfront Presentation] Could not load desktop preference: " + error.Message); }
            // A missing branding asset must not disable the independent window fix.
            prepared = true;
            try
            {
                string icon = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/LunaMultiplayer/Presentation/APF.ico");
                NormalPath(icon);
                largeIcon = NativeWindow.LoadIcon(icon, false);
                smallIcon = NativeWindow.LoadIcon(icon, true);
                int result = NativeWindow.SetCurrentProcessExplicitAppUserModelID("Aetherfront.KSP");
                if (result < 0) Debug.LogWarning("[Aetherfront Presentation] Windows taskbar grouping could not be set: " + result);
            }
            catch (Exception error) { Debug.LogWarning("[Aetherfront Presentation] Desktop setup failed: " + error.Message); }
        }

        private void Update()
        {
            if (!windows || !prepared || Time.realtimeSinceStartup < nextAttempt) return;
            nextAttempt = Time.realtimeSinceStartup + 1;
            try
            {
                IntPtr current = NativeWindow.FindOwnWindow();
                if (current == IntPtr.Zero || NativeWindow.IsIconic(current)) return; // Respect explicit minimization.
                NativeWindow.RequireOwnWindow(current);
                if (current != window)
                {
                    window = current; resized = false;
                    if (largeIcon != IntPtr.Zero) oldLarge = NativeWindow.SendMessage(window, 0x80, new IntPtr(1), largeIcon);
                    if (smallIcon != IntPtr.Zero) oldSmall = NativeWindow.SendMessage(window, 0x80, IntPtr.Zero, smallIcon);
                    NativeWindow.SetWindowText(window, "Aetherfront — Kerbal Space Program");
                    Debug.Log("[Aetherfront Presentation] APF window and taskbar icons applied.");
                }
                if (!borderless || HighLogic.LoadedScene < GameScenes.MAINMENU) return;
                if (!resized || Screen.fullScreen)
                {
                    var bounds = NativeWindow.MonitorBounds(window);
                    int width = bounds.Right-bounds.Left, height = bounds.Bottom-bounds.Top;
                    // Unity must regard this as WINDOWED. FullScreenWindow can
                    // still minimize when the shipped player has visibleInBackground=false.
                    Screen.SetResolution(width, height, FullScreenMode.Windowed);
                    Application.runInBackground = true;
                    GameSettings.FULLSCREEN = false;
                    GameSettings.SCREEN_RESOLUTION_WIDTH = width;
                    GameSettings.SCREEN_RESOLUTION_HEIGHT = height;
                    GameSettings.SaveSettings();
                    resized = true; applyWindowAfter = Time.realtimeSinceStartup + 0.5f;
                    return; // Unity applies resolution at the end of the frame.
                }
                if (Time.realtimeSinceStartup < applyWindowAfter) return;
                if (applyWindowAfter > 0 || ((uint)NativeWindow.GetStyle(window, NativeWindow.StyleIndex) & NativeWindow.OverlappedWindow) != 0)
                {
                    NativeWindow.Borderless(window, NativeWindow.MonitorBounds(window));
                    applyWindowAfter = 0;
                    Debug.Log("[Aetherfront Presentation] Borderless window active; switching apps does not request minimization.");
                }
            }
            catch (Exception error)
            {
                prepared = false; // Log once; leave KSP's own window behavior available.
                Debug.LogWarning("[Aetherfront Presentation] Desktop compatibility stopped: " + error.Message);
            }
        }

        private void OnDestroy()
        {
            if (!windows) return;
            if (window != IntPtr.Zero && NativeWindow.IsWindow(window))
            {
                if (largeIcon != IntPtr.Zero) NativeWindow.SendMessage(window, 0x80, new IntPtr(1), oldLarge);
                if (smallIcon != IntPtr.Zero) NativeWindow.SendMessage(window, 0x80, IntPtr.Zero, oldSmall);
            }
            if (largeIcon != IntPtr.Zero) NativeWindow.DestroyIcon(largeIcon);
            if (smallIcon != IntPtr.Zero) NativeWindow.DestroyIcon(smallIcon);
        }

        private static void NormalPath(string path)
        {
            for (string cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Presentation paths must not traverse links");
        }
    }
}
