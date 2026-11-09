using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using Aetherfront.Personal.Client;
using UnityEngine;

// Test-only rendering fixture; no network, state injection, purchases or save edits.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class PersonalCheckpointSceneProbe : MonoBehaviour
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    string output; int pid; float readyAt = -1; bool complete, failed; FlightCheckpointClient fixture;
    bool Owns => File.Exists(Path.Combine(output, "OWNER.txt")) && File.ReadAllText(Path.Combine(output, "OWNER.txt")).Trim() == pid.ToString();
    public void Awake()
    {
        output = Environment.GetEnvironmentVariable("APF_CHECKPOINT_SCENE_EVIDENCE");
        string root = Environment.GetEnvironmentVariable("AETHERFRONT_KSP_TEST_ROOT");
        if (string.IsNullOrEmpty(root) || Path.GetFileName(root) != "apf-test" || Path.GetFileName(Path.GetDirectoryName(root)) != "work" ||
            string.IsNullOrEmpty(output) || !Path.GetFullPath(output).StartsWith(Path.GetDirectoryName(root) + "\\rc50-input\\rc53-checkpoint\\scene-", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) { Destroy(this); return; }
        pid = Process.GetCurrentProcess().Id; DontDestroyOnLoad(this); Application.runInBackground = true;
        File.AppendAllText(Path.Combine(output, "probe.log"), "Owned isolated render test; no automatic quit during loading.\n");
    }
    public void Update()
    {
        if (failed || complete || output == null || !Owns || HighLogic.LoadedScene != GameScenes.MAINMENU) return;
        if (PartLoader.LoadedPartsList == null || PartLoader.LoadedPartsList.Count < 100) return;
        if (readyAt < 0) { readyAt = Time.realtimeSinceStartup; return; }
        if (Time.realtimeSinceStartup - readyAt < 15) return;
        try
        {
            if (fixture == null)
            {
                fixture = new GameObject("Isolated checkpoint render fixture").AddComponent<FlightCheckpointClient>();
                fixture.enabled = false;
                if (!(bool)typeof(FlightCheckpointClient).GetField("hooked", Private).GetValue(fixture))
                    throw new InvalidOperationException("Shipping stock quicksave hooks did not initialize.");
                var info = new FlightCheckpointClient.CheckpointInfo { Points = new List<FlightCheckpointClient.RestorePoint>(), AutomaticStatus = "Automatic points are collected once a minute while your personal time is synchronized." };
                foreach (string label in new[] { "Manual save", "1 minute ago", "5 minutes ago", "30 minutes ago", "Latest launch" })
                    info.Points.Add(new FlightCheckpointClient.RestorePoint { CheckpointId = Guid.NewGuid().ToString("N"), Label = label, SavedUtc = DateTimeOffset.UtcNow.ToString("O"), VesselCount = 12, MissionTime = 12345678 });
                typeof(FlightCheckpointClient).GetField("info", Private).SetValue(fixture, info);
                typeof(FlightCheckpointClient).GetField("selectedPoint", Private).SetValue(fixture, info.Points[2].CheckpointId);
                typeof(FlightCheckpointClient).GetField("status", Private).SetValue(fixture, "Personal checkpoint ready.");
                typeof(FlightCheckpointClient).GetField("confirm", Private).SetValue(fixture, true);
                readyAt = Time.realtimeSinceStartup;
                return;
            }
            string image = Path.Combine(output, "checkpoint-window.png");
            if (!File.Exists(image)) { ScreenCapture.CaptureScreenshot(image); return; }
            if (new FileInfo(image).Length < 1) return;
            complete = true;
            File.WriteAllText(Path.Combine(output, "RESULTS.txt"), "PASS actual KSP MAINMENU/part compilation completed; shipping checkpoint window rendered with disabled synthetic content. No authenticated gameplay claim.\n");
            Application.Quit();
        }
        catch (Exception error) { failed = true; File.WriteAllText(Path.Combine(output, "FAIL.txt"), error.ToString()); }
    }
    public void OnGUI()
    {
        if (fixture == null || failed || complete || !Owns) return;
        GUILayout.Window(1886554, new Rect(40, 40, 520, 480), Draw, "RC53 checkpoint render fixture (controls disabled)");
    }
    void Draw(int id)
    {
        bool enabled = GUI.enabled; GUI.enabled = false;
        try { typeof(FlightCheckpointClient).GetMethod("DrawWindow", Private).Invoke(fixture, new object[] { id }); }
        catch (Exception error) { failed = true; File.WriteAllText(Path.Combine(output, "FAIL.txt"), error.ToString()); }
        finally { GUI.enabled = enabled; }
    }
}
