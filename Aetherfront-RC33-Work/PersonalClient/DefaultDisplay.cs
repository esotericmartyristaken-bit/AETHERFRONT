using System;
using System.IO;
using LmpClient.Apf;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class DefaultDisplay : MonoBehaviour
    {
        private void Start()
        {
            if (!ApfClientSettings.Enabled) return;
            try
            {
                string root = Path.GetFullPath(KSPUtil.ApplicationRootPath);
                string directory = Path.Combine(root, "APF");
                string marker = Path.Combine(directory, "DisplayDefaults-v1.txt");
                RequireNormalPath(marker);
                if (File.Exists(marker)) return;
                Directory.CreateDirectory(directory);
                if (DefaultDisplayPolicy.IsStockDefault(GameSettings.SCREEN_RESOLUTION_WIDTH, GameSettings.SCREEN_RESOLUTION_HEIGHT))
                {
                    var native = Screen.currentResolution;
                    int width, height;
                    DefaultDisplayPolicy.Select(native.width, native.height, out width, out height);
                    GameSettings.SCREEN_RESOLUTION_WIDTH = width;
                    GameSettings.SCREEN_RESOLUTION_HEIGHT = height;
                    // Leave fullscreen, UI scale, quality and audio choices alone.
                    Screen.SetResolution(width, height, GameSettings.FULLSCREEN);
                    GameSettings.SaveSettings();
                    Debug.Log("[Aetherfront] Default display resolution: " + width + "x" + height);
                }
                // A custom saved resolution also completes initialization. It
                // must not be reset on later launches or client updates.
                using (var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(output)) writer.WriteLine("Display defaults initialized; use KSP Settings for later changes.");
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Aetherfront] Could not initialize display defaults: " + error.GetType().Name);
            }
        }

        private static void RequireNormalPath(string path)
        {
            for (string cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Display preferences must not traverse a link");
        }
    }
}
