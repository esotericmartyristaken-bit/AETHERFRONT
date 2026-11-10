using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LmpClient;
using LmpClient.Extensions;
using LmpClient.Network;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.VesselPositionSys;
using LmpClient.Systems.Warp;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Interface;
using UnityEngine;

namespace Aetherfront.Stability.Client
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    [DefaultExecutionOrder(-20000)]
    public sealed class FlightSafety : MonoBehaviour
    {
        sealed class Ack { internal int Generation, Id; internal double Offset; }
        static readonly ConcurrentQueue<Ack> Acknowledgements = new ConcurrentQueue<Ack>();
        static readonly WarpRecoveryPolicy Recovery = new WarpRecoveryPolicy();
        static readonly PropertyInfo GenerationProperty = AccessTools.Property(typeof(NetworkConnection), "ConnectionGeneration");
        static volatile bool disconnectRequested;
        static bool held, previouslyPaused, aligning, installed;
        static double stableOffset;
        static bool hasStableOffset;
        static float previousThrottle;
        static bool recoveryNotice;
        static bool fieldHookTried;
        static int generation;
        static string failure;
        float nextSnapshot;
        static bool Running => MainSystem.NetworkState == ClientState.Running;
        public static bool Holding => held;
        public static string Failure => failure;
        static int CurrentGeneration => (int)GenerationProperty.GetValue(null, null);
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

        public void Awake()
        {
            if (installed) { Destroy(this); return; }
            DontDestroyOnLoad(this);
            try
            {
                var harmony = new Harmony("aetherfront.rc55.flight-safety");
                InstallHooks(harmony);
                installed = true;
                Debug.Log("[Aetherfront Stability] RC55 flight safety and nonblocking traffic scheduling installed.");
            }
            catch (Exception error)
            {
                failure = "Flight safety initialization failed; do not connect. See KSP.log.";
                Debug.LogError("[Aetherfront Stability] " + error);
                var harmony = new Harmony("aetherfront.rc55.failed-safety");
                foreach (var method in typeof(NetworkConnection).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "ConnectToServer"))
                    Patch(harmony, method, nameof(RejectConnection));
            }
        }
        public static void InstallHooks(Harmony harmony)
        {
                Patch(harmony, AccessTools.Method(typeof(WarpMessageHandler), "HandleMessage"), nameof(CaptureAck));
                Patch(harmony, AccessTools.Method(typeof(WarpMessageSender), "SendNewSubspace"), nameof(SendBoundary));
                Patch(harmony, AccessTools.Method(typeof(WarpSystem), "CheckStuckAtWarp"), nameof(ReplaceStuckCheck));
                Patch(harmony, AccessTools.Method(typeof(WarpSystem), "CheckWarpStopped"), nameof(ReplaceStuckCheck));
                Patch(harmony, AccessTools.Method(typeof(TimeSyncSystem), "CheckGameTime"), nameof(AllowClockTick));
                Patch(harmony, AccessTools.Method(typeof(TimeSyncSystem), "SetGameTime"), nameof(AllowSetTime));
                Patch(harmony, AccessTools.Method(typeof(TimeWarp), "SetRate", new[] { typeof(int), typeof(bool), typeof(bool) }), nameof(AllowWarpRate));
                harmony.Patch(AccessTools.Method(typeof(WarpEvents), "OnTimeWarpChanged"), postfix: new HarmonyMethod(typeof(FlightSafety), nameof(AfterWarpRate)));
                Patch(harmony, AccessTools.Method(typeof(FlightDriver), "SetPause", new[] { typeof(bool), typeof(bool) }), nameof(AllowPause));
                Patch(harmony, AccessTools.Method(typeof(VesselPositionUpdate), "UpdateVesselWithPositionData"), nameof(AllowPosition));
                Patch(harmony, AccessTools.Method(typeof(NetworkConnection), "Disconnect", new[] { typeof(string) }), nameof(BeforeDisconnect));
                Patch(harmony, AccessTools.Method(typeof(MainSystem), "StopGame"), nameof(BeforeStopGame));
                var oldAlignment = AccessTools.TypeByName("Aetherfront.Continuity.Client.ClockTransitionCompatibility");
                if (oldAlignment == null) throw new TypeLoadException("Shipping clock compatibility missing.");
                Patch(harmony, AccessTools.Method(oldAlignment, "Update"), nameof(ReplaceStuckCheck));
                Patch(harmony, AccessTools.Method(oldAlignment, "AfterMessage"), nameof(ReplaceStuckCheck));
                ClientTraffic.Install(harmony);
        }
        static void Patch(Harmony harmony, MethodBase target, string prefix)
        {
            if (target == null) throw new MissingMethodException(prefix);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FlightSafety), prefix) { priority = Priority.First });
        }
        static bool RejectConnection() { MainSystem.Singleton.Status = failure; return false; }
        static bool ReplaceStuckCheck() => !Running;
        static bool AllowClockTick() => !held;
        static bool CaptureAck(IServerMessageBase msg)
        {
            if (!Running || !(msg?.Data is WarpNewSubspaceMsgData data) || data.PlayerCreator != SettingsSystem.CurrentSettings.PlayerName) return true;
            // Copy values now: the network handler recycles its message after this call.
            if (Acknowledgements.Count < 64)
                Acknowledgements.Enqueue(new Ack { Generation = CurrentGeneration, Id = data.SubspaceKey, Offset = data.ServerTimeDifference });
            return false;
        }
        static bool SendBoundary()
        {
            if (!Running) return true;
            if (!MainSystem.IsUnityThread) { disconnectRequested = true; return false; }
            if (!Recovery.Pending)
            {
                generation = CurrentGeneration;
                var warp = WarpSystem.Singleton;
                if (warp.CurrentSubspace >= 0 && warp.Subspaces.TryGetValue(warp.CurrentSubspace, out var currentOffset))
                { stableOffset = currentOffset; hasStableOffset = true; }
                double proposal = Planetarium.GetUniversalTime() - TimeSyncSystem.ServerClockSec;
                if (hasStableOffset) proposal = Math.Max(proposal, stableOffset);
                Recovery.Begin(CurrentGeneration, proposal, Now);
                recoveryNotice = false;
                SaveRecovery("warp-boundary");
                Hold();
                if (warp.CurrentSubspace >= 0) warp.CurrentSubspace = -1;
                Debug.Log("[Aetherfront Stability] Warp recovery started; generation=" + CurrentGeneration + "; offset=" + proposal.ToString("R"));
            }
            SendIfDue(); return false;
        }
        static void SendIfDue()
        {
            if (!Recovery.ShouldSend(Now)) return;
            WarpSystem.Singleton.WaitingSubspaceIdFromServer = true;
            var message = NetworkMain.CliMsgFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
            message.PlayerCreator = SettingsSystem.CurrentSettings.PlayerName;
            message.ServerTimeDifference = Recovery.Proposal;
            WarpSystem.Singleton.MessageSender.SendMessage(message);
        }
        static bool AllowPosition(VesselPositionUpdate __instance)
        {
            // Recheck at APPLY time, not only when a packet originally entered the queue.
            if (held || !Running) return false;
            bool own = VesselCommon.UpdateIsForOwnVessel(__instance.VesselId) ||
                LockSystem.LockQuery.ControlLockBelongsToPlayer(__instance.VesselId, SettingsSystem.CurrentSettings.PlayerName);
            if (own) { VesselPositionSystem.Singleton.RemoveVessel(__instance.VesselId); return false; }
            return true;
        }
        static bool AllowSetTime(double targetTick)
        {
            if (!Running || aligning || !HighLogic.LoadedSceneIsFlight) return true;
            if (held) return false;
            double delta = targetTick - Planetarium.GetUniversalTime();
            if (!WarpRecoveryPolicy.Finite(targetTick))
            {
                Fail("Unsafe flight clock correction refused (seconds=" + delta.ToString("R") + "). Reconnect to restore your saved mission time.");
                return false;
            }
            if (delta < -0.05 || delta > 3.5) { SendBoundary(); return false; }
            return true;
        }
        static bool AllowWarpRate(int __0) => !held || aligning || __0 == 0;
        static bool AllowPause(bool __0) => !held || aligning || __0;
        static void AfterWarpRate() { if (Running && MainSystem.IsUnityThread && TimeWarp.CurrentRateIndex == 0 && WarpSystem.Singleton.CurrentSubspace == -1) SendBoundary(); }
        static void BeforeDisconnect() { disconnectRequested = true; if (MainSystem.IsUnityThread && HighLogic.LoadedSceneIsFlight) ProtectDisconnect(); }
        static void BeforeStopGame() { if (HighLogic.LoadedSceneIsFlight) ProtectDisconnect(); }
        static void ProtectDisconnect() { SaveRecovery("disconnect"); Hold(); }
        static void Hold()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (!held) { previouslyPaused = FlightDriver.Pause; previousThrottle = FlightInputHandler.state.mainThrottle; }
            held = true;
            FlightInputHandler.state.mainThrottle = 0;
            if (!FlightDriver.Pause) FlightDriver.SetPause(true, false);
            Time.timeScale = 0;
        }
        static void Fail(string reason)
        {
            if (failure != null) return;
            failure = reason; SaveRecovery("sync-failure"); Hold(); Recovery.Reset();
            Debug.LogError("[Aetherfront Stability] " + reason);
            NetworkConnection.Disconnect(reason);
        }
        static void SaveRecovery(string reason)
        {
            if (!MainSystem.IsUnityThread || !HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null || FlightGlobals.ActiveVessel == null) return;
            try
            {
                var root = new ConfigNode("AETHERFRONT_LOCAL_RECOVERY");
                root.AddValue("reason", reason); root.AddValue("savedUtc", DateTime.UtcNow.ToString("O"));
                root.AddValue("missionTime", Planetarium.GetUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                root.AddValue("subspace", WarpSystem.Singleton.CurrentSubspace);
                var owned = new HashSet<Guid>(LockSystem.LockQuery.GetAllControlLocks(SettingsSystem.CurrentSettings.PlayerName).Select(l => l.VesselId));
                foreach (var item in LockSystem.LockQuery.GetAllUnloadedUpdateLocks(SettingsSystem.CurrentSettings.PlayerName)) owned.Add(item.VesselId);
                if (!VesselCommon.IsSpectating) owned.Add(FlightGlobals.ActiveVessel.id);
                if (reason == "periodic") { owned.Clear(); if (!VesselCommon.IsSpectating) owned.Add(FlightGlobals.ActiveVessel.id); }
                int count = 0;
                foreach (var vessel in FlightGlobals.Vessels.Where(v => v != null && owned.Contains(v.id)))
                {
                    var proto = vessel.loaded ? vessel.BackupVessel() : vessel.protoVessel;
                    if (proto == null) continue;
                    var node = new ConfigNode("VESSEL"); proto.Save(node); root.AddNode(node); count++;
                }
                if (count == 0) return;
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "APF", "LocalFlightRecovery");
                Directory.CreateDirectory(directory);
                string text = root.ToString();
                if (text.Length > 16777216) throw new InvalidDataException("Local recovery exceeds 16 MiB text limit; previous snapshot retained.");
                bool failed = reason == "disconnect" || reason == "sync-failure";
                string stem = failed ? "failure" : reason == "periodic" ? "active" : "latest";
                string temporary = Path.Combine(directory, "pending.cfg"), current = Path.Combine(directory, stem + ".cfg"), previous = Path.Combine(directory, stem + "-previous.cfg");
                File.WriteAllText(temporary, text);
                if (File.Exists(current)) File.Replace(temporary, current, previous); else File.Move(temporary, current);
                Debug.Log("[Aetherfront Stability] Local recovery saved: " + reason + "; vessels=" + count + ". No server state or science is overwritten.");
            }
            catch (Exception error) { Debug.LogError("[Aetherfront Stability] Recovery capture failed; previous snapshot retained: " + error); }
        }
        public void Update()
        {
            if (!installed) return;
            if (Running && !fieldHookTried)
            {
                fieldHookTried = true;
                try { ClientTraffic.InstallFieldHook(new Harmony("aetherfront.rc55.cosmetic-source")); Debug.Log("[Aetherfront Stability] Cosmetic field source filter installed after part-module initialization."); }
                catch (Exception error) { Debug.LogWarning("[Aetherfront Stability] Cosmetic source optimization unavailable; network filter remains active: " + error.GetType().Name); }
            }
            int current = CurrentGeneration;
            if (current != generation)
            {
                generation = current; Recovery.Reset(); hasStableOffset = false;
                while (Acknowledgements.TryDequeue(out _)) { }
                failure = null;
            }
            if (disconnectRequested && HighLogic.LoadedSceneIsFlight) { disconnectRequested = false; ProtectDisconnect(); }
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                if (held) { held = false; FlightDriver.SetPause(false, false); Time.timeScale = 1; }
                disconnectRequested = false; Recovery.Reset(); return;
            }
            if (!Running) { if (held && HighLogic.LoadedSceneIsFlight) Hold(); return; }
            if (Recovery.Expired(Now)) { Fail("Warp acknowledgement timed out after 15 seconds. Flight held and local recovery preserved; reconnect to resynchronize."); return; }
            var warp = WarpSystem.Singleton;
            if (warp.CurrentSubspace >= 0 && !Recovery.Pending && warp.Subspaces.TryGetValue(warp.CurrentSubspace, out var offset))
            { stableOffset = offset; hasStableOffset = true; }
            while (Acknowledgements.TryDequeue(out var ack))
            {
                if (!Recovery.CanAccept(ack.Generation, ack.Id, ack.Offset) || TimeWarp.CurrentRateIndex != 0) continue;
                double target = TimeSyncSystem.ServerClockSec + ack.Offset, now = Planetarium.GetUniversalTime();
                if (!WarpRecoveryPolicy.Finite(target) || target < now - 0.05 || target - now > WarpRecoveryPolicy.TimeoutSeconds + 5)
                { Fail("Warp acknowledgement does not match the protected flight clock. Reconnect to recover."); break; }
                aligning = true;
                try
                {
                    if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null && !VesselCommon.IsSpectating && target > now)
                        FlightGlobals.ActiveVessel.AdvanceShipPosition(target);
                    warp.Subspaces[ack.Id] = ack.Offset;
                    warp.WaitingSubspaceIdFromServer = false; warp.SkipSubspaceProcess = true; warp.CurrentSubspace = ack.Id;
                    TimeSyncSystem.Singleton.SetGameTime(Math.Max(now, target));
                    Recovery.Accepted(ack.Id); stableOffset = ack.Offset; hasStableOffset = true;
                    LmpClient.Events.WarpEvent.onTimeWarpStopped.Fire();
                    if (held) { held = false; FlightInputHandler.state.mainThrottle = previousThrottle; FlightDriver.SetPause(previouslyPaused, false); if (!previouslyPaused) Time.timeScale = 1; }
                    if (recoveryNotice) ScreenMessages.PostScreenMessage("Mission time synchronized.", 3, ScreenMessageStyle.UPPER_CENTER);
                    Debug.Log("[Aetherfront Stability] Warp recovery complete on Unity thread; subspace=" + ack.Id + "; seconds=" + (Now - Recovery.Started).ToString("F3"));
                }
                catch (Exception error) { Fail("Warp alignment failed safely: " + error.GetType().Name); }
                finally { aligning = false; }
            }
            if (Recovery.Pending)
            {
                Hold();
                if (!recoveryNotice && Now - Recovery.Started >= 1.5)
                { recoveryNotice = true; ScreenMessages.PostScreenMessage("Synchronizing mission time. Flight safely held (up to 15 seconds).", 12, ScreenMessageStyle.UPPER_CENTER); }
                if (Recovery.Expired(Now)) Fail("Warp acknowledgement timed out after 15 seconds. Flight held and local recovery preserved; reconnect to resynchronize.");
                else SendIfDue();
            }
            else if (!held && warp.CurrentSubspace == -1 && TimeWarp.CurrentRateIndex == 0) SendBoundary();
            else if (!held && warp.CurrentSubspace >= 0 && HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && TimeWarp.CurrentRateIndex == 0 &&
                Math.Abs(warp.CurrentSubspaceTime - Planetarium.GetUniversalTime()) > 3.5) SendBoundary();
            if (!held && Time.realtimeSinceStartup >= nextSnapshot && HighLogic.LoadedSceneIsFlight)
            { nextSnapshot = Time.realtimeSinceStartup + 30; SaveRecovery("periodic"); }
        }
        public void LateUpdate() { if (held && HighLogic.LoadedSceneIsFlight) Hold(); }
        public void FixedUpdate() { if (disconnectRequested && HighLogic.LoadedSceneIsFlight) { disconnectRequested = false; ProtectDisconnect(); } }
    }
}
