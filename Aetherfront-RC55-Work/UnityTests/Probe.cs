using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using LmpClient;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.Systems.Warp;
using Aetherfront.Stability.Client;

// Test-only, owned process and loopback only. Never shipped. No cursor/keyboard automation.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class RC55TestProbe : MonoBehaviour
{
    string root, output, commands, player; int port, pid; float next; bool active;
    public void Awake()
    {
        root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
        string workspace = @"C:\Users\jkell\Documents\Codex\2026-09-07\kesas-multiplayer-beta\work";
        output = Environment.GetEnvironmentVariable("RC55_TEST_EVIDENCE");
        if ((root != Path.Combine(workspace, "apf-test") && root != Path.Combine(workspace, "apf-peer")) ||
            string.IsNullOrEmpty(output) || !string.Equals(Path.GetFullPath(output).TrimEnd('\\', '/'), Path.Combine(workspace, "rc50-input", "rc55-stability"), StringComparison.OrdinalIgnoreCase))
        { Destroy(this); return; }
        pid = Process.GetCurrentProcess().Id;
        player = root.EndsWith("apf-peer") ? "RC55Beta" : "RC55Alpha"; port = root.EndsWith("apf-peer") ? 28877 : 28876;
        Directory.CreateDirectory(output); commands = Path.Combine(output, "command-" + pid + ".txt");
        active = true; DontDestroyOnLoad(this); Application.runInBackground = true;
        Log("BEGIN owned modded KSP; root=" + root + "; PID=" + pid + "; no stale commands, no auto quit");
    }
    void Log(string value)
    {
        File.AppendAllText(Path.Combine(output, "probe-" + pid + ".log"), DateTime.UtcNow.ToString("O") + " " + value + "\n");
        UnityEngine.Debug.Log("[RC55TestProbe] " + value);
    }
    public void Update()
    {
        if (!active || Time.realtimeSinceStartup < next) return; next = Time.realtimeSinceStartup + 1;
        try
        {
            if (HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER) return;
            var vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            Log("STATE scene=" + HighLogic.LoadedScene + "; network=" + MainSystem.NetworkState + "; subspace=" + WarpSystem.Singleton.CurrentSubspace +
                "; awaiting=" + WarpSystem.Singleton.WaitingSubspaceIdFromServer + "; hold=" + FlightSafety.Holding + "; rate=" + TimeWarp.CurrentRate +
                "; UT=" + Planetarium.GetUniversalTime().ToString("R") + "; vessel=" + (vessel == null ? "none" : vessel.id + "; parts=" + vessel.parts.Count + "; altitude=" + vessel.altitude.ToString("F2")));
            if (!File.Exists(commands) || HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER) return;
            string command = File.ReadAllText(commands).Trim(); File.Move(commands, commands + ".used-" + DateTime.UtcNow.Ticks);
            Log("COMMAND " + command);
            switch (command)
            {
                case "connect":
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU || PartLoader.LoadedPartsList.Count < 100) throw new Exception("Completed MAINMENU required");
                    SettingsSystem.CurrentSettings.PlayerName = player;
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                    var bypass = AccessTools.Field(AccessTools.TypeByName("LmpClient.Apf.ConnectionInstall"), "bypass");
                    bypass.SetValue(null, true);
                    try { NetworkConnection.ConnectToServer("127.0.0.1", port, ""); }
                    finally { bypass.SetValue(null, false); }
                    Log("SCOPE installed candidate code connection; signed updater is a separate test");
                    break;
                case "pack-status":
                    Log("PACK state=" + LmpClient.Apf.ApfSyncCoordinator.State + "; error=" + LmpClient.Apf.ApfSyncCoordinator.Error);
                    break;
                case "pack-retry": LmpClient.Apf.ApfSyncCoordinator.Retry(); break;
                case "launch":
                    if (HighLogic.CurrentGame == null || HighLogic.LoadedSceneIsFlight) throw new Exception("Connected space center required");
                    string craftPath = Path.Combine(root, "GameData", "Squad", "Tutorials", "Hopper.craft");
                    var craft = ConfigNode.Load(craftPath); craft.SetValue("ship", player + " test craft", true);
                    string ownCraft = Path.Combine(output, player + ".craft"); craft.Save(ownCraft);
                    var roster = HighLogic.CurrentGame.CrewRoster;
                    var manifest = roster.DefaultCrewForVessel(craft, null, false, false);
                    if (manifest == null || manifest.CrewCount == 0) { roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew); manifest = roster.DefaultCrewForVessel(craft, null, false, false); }
                    FlightDriver.StartWithNewLaunch(ownCraft, "Squad/Flags/default", "LaunchPad", manifest);
                    break;
                case "orbit":
                    if (vessel == null || !HighLogic.LoadedSceneIsFlight) throw new Exception("Flight required");
                    var body = vessel.mainBody;
                    FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, 0, body.Radius + 1000000, 0, 0, 0, 0, Planetarium.GetUniversalTime());
                    FloatingOrigin.ResetTerrainShaderOffset();
                    Log("SETUP native stock SetShipOrbit placed only the disposable active craft in circular 1000km orbit; subsequent warp/sync use shipping code");
                    break;
                case "menu": MainSystem.Singleton.ForceQuit = true; break;
                case "load-own":
                    if (HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null) throw new Exception("Connected space center required");
                    int index = HighLogic.CurrentGame.flightState.protoVessels.FindLastIndex(p => p.vesselName == player + " test craft");
                    if (index < 0) throw new Exception("No preserved own flight in the synchronized state");
                    FlightDriver.StartAndFocusVessel(HighLogic.CurrentGame, index); break;
                case "inspect":
                    if (vessel != null) Log("ORBIT id=" + vessel.id + "; eccentricity=" + vessel.orbit.eccentricity.ToString("R") + "; periapsis=" + vessel.orbit.PeA.ToString("R") + "; apoapsis=" + vessel.orbit.ApA.ToString("R") + "; speed=" + vessel.obt_speed.ToString("R") + "; pause=" + FlightDriver.Pause);
                    break;
                case "warp-high": TimeWarp.SetRate(5, true); break;
                case "warp-stop": TimeWarp.SetRate(0, true); break;
                case "pause": FlightDriver.SetPause(true, false); break;
                case "resume": FlightDriver.SetPause(false, false); break;
                case "stale-own":
                    if (vessel == null) throw new Exception("Active vessel required");
                    double epoch = vessel.orbit.epoch; var position = vessel.GetWorldPos3D();
                    new VesselPositionUpdate { VesselId = vessel.id, GameTimeStamp = 0, SubspaceId = -1 }.UpdateVesselWithPositionData();
                    if ((vessel.GetWorldPos3D() - position).magnitude > 0.001 || vessel.orbit.epoch != epoch) throw new Exception("Stale own-vessel update mutated flight");
                    Log("PASS stale queued own-vessel update fenced at native apply method"); break;
                case "capture": ScreenCapture.CaptureScreenshot(Path.Combine(output, "screen-" + pid + "-" + DateTime.UtcNow.Ticks + ".png")); break;
                case "disconnect": NetworkConnection.Disconnect("Owned RC55 disconnect test"); break;
                case "quit-own": Log("END owned test instance"); Application.Quit(); break;
                default: throw new Exception("Unknown test command");
            }
        }
        catch (Exception error) { Log("TEST ERROR " + error); }
    }
}
