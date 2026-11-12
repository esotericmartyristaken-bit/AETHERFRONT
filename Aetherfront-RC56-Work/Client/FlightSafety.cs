using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Aetherfront.Synchronization;
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
        sealed class Ack { internal int Generation, Id; internal long Session, Request; internal double Offset, Received; }
        static readonly ConcurrentQueue<Ack> Acknowledgements = new ConcurrentQueue<Ack>();
        static readonly WarpRecoveryPolicy Recovery = new WarpRecoveryPolicy();
        static readonly PropertyInfo GenerationProperty = AccessTools.Property(typeof(NetworkConnection), "ConnectionGeneration");
        static volatile bool disconnectRequested;
        static bool held, aligning, installed, applyingHold;
        static readonly HoldLease Lease = new HoldLease();
        static double stableOffset, forwardBudget;
        static bool hasStableOffset;
        static bool recoveryNotice;
        static bool fieldHookTried;
        static int generation;
        static long session, nextRequest;
        static Guid protectedVessel;
        static bool transitioning;
        static GameScenes observedScene;
        static string failure;
        float nextSnapshot;
        sealed class Lifecycle
        {
            internal void SceneRequested(GameScenes scene) => FlightSafety.SceneRequested(scene);
            internal void SceneReady(GameScenes scene) => FlightSafety.SceneReady(scene);
            internal void VesselDestroyed(Vessel vessel) => FlightSafety.VesselDestroyed(vessel);
            internal void VesselTerminated(ProtoVessel vessel) => FlightSafety.VesselTerminated(vessel);
        }
        static readonly Lifecycle Listener = new Lifecycle();
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
                var harmony = new Harmony("aetherfront.rc56.flight-safety");
                InstallHooks(harmony);
                GameEvents.onGameSceneLoadRequested.Add(Listener.SceneRequested);
                GameEvents.onLevelWasLoadedGUIReady.Add(Listener.SceneReady);
                GameEvents.onVesselWillDestroy.Add(Listener.VesselDestroyed);
                GameEvents.onVesselTerminated.Add(Listener.VesselTerminated);
                observedScene = HighLogic.LoadedScene;
                installed = true;
                Debug.Log("[Aetherfront Stability] RC58 identified synchronization and safe-exit lifecycle installed.");
            }
            catch (Exception error)
            {
                failure = "Flight safety initialization failed; do not connect. See KSP.log.";
                Debug.LogError("[Aetherfront Stability] " + error);
                var harmony = new Harmony("aetherfront.rc56.failed-safety");
                foreach (var method in typeof(NetworkConnection).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "ConnectToServer"))
                    Patch(harmony, method, nameof(RejectConnection));
            }
        }
        public static void InstallHooks(Harmony harmony)
        {
                Patch(harmony, AccessTools.Method(typeof(WarpMessageHandler), "HandleMessage"), nameof(CaptureAck));
                Patch(harmony, AccessTools.Method(AccessTools.TypeByName("LmpClient.Network.NetworkReceiver"), "QueueMessageToSystem",
                    new[] { typeof(IServerMessageBase), typeof(int) }), nameof(CaptureReceivedAck));
                Patch(harmony, AccessTools.Method(typeof(WarpMessageSender), "SendNewSubspace"), nameof(SendBoundary));
                Patch(harmony, AccessTools.Method(typeof(WarpSystem), "CheckStuckAtWarp"), nameof(ReplaceStuckCheck));
                Patch(harmony, AccessTools.Method(typeof(WarpSystem), "CheckWarpStopped"), nameof(ReplaceStuckCheck));
                Patch(harmony, AccessTools.Method(typeof(TimeSyncSystem), "CheckGameTime"), nameof(AllowClockTick));
                Patch(harmony, AccessTools.Method(typeof(TimeSyncSystem), "SetGameTime"), nameof(AllowSetTime));
                Patch(harmony, AccessTools.Method(typeof(TimeWarp), "SetRate", new[] { typeof(int), typeof(bool), typeof(bool) }), nameof(AllowWarpRate));
                harmony.Patch(AccessTools.Method(typeof(WarpEvents), "OnTimeWarpChanged"), postfix: new HarmonyMethod(typeof(FlightSafety), nameof(AfterWarpRate)));
                Patch(harmony, AccessTools.Method(typeof(FlightDriver), "SetPause", new[] { typeof(bool), typeof(bool) }), nameof(AllowPause));
                var nativePauseGuard = AccessTools.TypeByName("LmpClient.Harmony.FlightDriver_SetPause");
                Patch(harmony, AccessTools.Method(nativePauseGuard, "PrefixSetPause"), nameof(AllowProtectedNativePause));
                Patch(harmony, AccessTools.Method(typeof(VesselPositionUpdate), "UpdateVesselWithPositionData"), nameof(AllowPosition));
                Patch(harmony, AccessTools.Method(typeof(NetworkConnection), "Disconnect", new[] { typeof(string) }), nameof(BeforeDisconnect));
                Patch(harmony, AccessTools.Method(typeof(MainSystem), "StopGame"), nameof(BeforeStopGame));
                var oldAlignment = AccessTools.TypeByName("Aetherfront.Continuity.Client.ClockTransitionCompatibility");
                if (oldAlignment == null) throw new TypeLoadException("Shipping clock compatibility missing.");
                Patch(harmony, AccessTools.Method(oldAlignment, "Update"), nameof(ReplaceStuckCheck));
                Patch(harmony, AccessTools.Method(oldAlignment, "AfterMessage"), nameof(ReplaceStuckCheck));
                ClientTraffic.Install(harmony);
                SnapshotClient.Install(harmony);
                CheckpointReconnect.Install(harmony);
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
                Acknowledgements.Enqueue(new Ack { Generation = CurrentGeneration, Id = data.SubspaceKey, Offset = data.ServerTimeDifference,
                    Session = BoundaryWire.Session(data), Request = BoundaryWire.Request(data), Received = Now });
            else disconnectRequested = true;
            return false;
        }
        static bool CaptureReceivedAck(IServerMessageBase __0, int __1)
        {
            if (!Running || !(__0?.Data is WarpNewSubspaceMsgData data) || data.PlayerCreator != SettingsSystem.CurrentSettings.PlayerName) return true;
            // This boundary runs on packet intake, BEFORE any Unity message queue.
            // Consume/recycle exactly once, retaining only immutable copied values.
            try
            {
                if (__1 == CurrentGeneration)
                {
                    if (Acknowledgements.Count < 64)
                        Acknowledgements.Enqueue(new Ack { Generation = __1, Id = data.SubspaceKey, Offset = data.ServerTimeDifference,
                            Session = BoundaryWire.Session(data), Request = BoundaryWire.Request(data), Received = Now });
                    else disconnectRequested = true;
                }
            }
            finally { __0.Recycle(); }
            return false;
        }
        static bool SendBoundary()
        {
            if (!Running) return true;
            if (transitioning || !ReadyForBoundary()) return false;
            if (!MainSystem.IsUnityThread) { disconnectRequested = true; return false; }
            if (!Recovery.Pending)
            {
                generation = CurrentGeneration;
                var warp = WarpSystem.Singleton;
                if (warp.CurrentSubspace >= 0 && warp.Subspaces.TryGetValue(warp.CurrentSubspace, out var currentOffset))
                { stableOffset = currentOffset; hasStableOffset = true; }
                double naturalProposal = Planetarium.GetUniversalTime() - TimeSyncSystem.ServerClockSec;
                double proposal = naturalProposal;
                if (hasStableOffset) proposal = Math.Max(proposal, stableOffset);
                EnsureSession();
                Recovery.Begin(CurrentGeneration, session, checked(++nextRequest), proposal, Now);
                forwardBudget = WarpRecoveryPolicy.ForwardBudget(naturalProposal, proposal, SafeToCatchUp());
                recoveryNotice = false;
                Hold();
                if (warp.CurrentSubspace >= 0) warp.CurrentSubspace = -1;
                // Admit control traffic before any local snapshot work. Receipt
                // time is captured independently while Unity writes recovery.
                SendIfDue();
                SaveRecovery("warp-boundary");
                Debug.Log("[Aetherfront Stability] Warp recovery started; generation=" + CurrentGeneration + "; request=" + nextRequest + "; offset=" + proposal.ToString("R"));
            }
            SendIfDue(); return false;
        }
        static bool SafeToCatchUp()
        {
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION) return true;
            var vessel = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (vessel == null || VesselCommon.IsSpectating) return false;
            if (vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.LANDED ||
                vessel.situation == Vessel.Situations.SPLASHED) return true;
            return vessel.situation == Vessel.Situations.ORBITING && vessel.orbit != null && vessel.mainBody != null &&
                vessel.orbit.PeA > Math.Max(10000, vessel.mainBody.atmosphereDepth);
        }
        static void SendIfDue()
        {
            if (!Recovery.ShouldSend(Now)) return;
            WarpSystem.Singleton.WaitingSubspaceIdFromServer = true;
            var message = NetworkMain.CliMsgFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
            message.PlayerCreator = SettingsSystem.CurrentSettings.PlayerName;
            message.ServerTimeDifference = Recovery.Proposal;
            BoundaryWire.Set(message, Recovery.Session, Recovery.Request);
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
        static bool AllowPause(bool __0)
        {
            if (held && !aligning && !applyingHold) Lease.ObserveExternalPause(__0);
            return !held || aligning || __0;
        }
        static bool AllowProtectedNativePause(ref bool __result)
        { if (!held && !aligning) return true; __result = true; return false; }
        static void AfterWarpRate() { if (Running && MainSystem.IsUnityThread && TimeWarp.CurrentRateIndex == 0 && WarpSystem.Singleton.CurrentSubspace == -1) SendBoundary(); }
        static void BeforeDisconnect() { disconnectRequested = true; if (MainSystem.IsUnityThread && HighLogic.LoadedSceneIsFlight) ProtectDisconnect(); }
        static void BeforeStopGame()
        {
            if (HighLogic.LoadedSceneIsFlight) ProtectDisconnect();
            CancelBoundary(); transitioning = true;
        }
        static void ProtectDisconnect() { SaveRecovery("disconnect"); Hold(); }
        static void Hold()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (!held)
            {
                protectedVessel = FlightGlobals.ActiveVessel == null ? Guid.Empty : FlightGlobals.ActiveVessel.id;
                Lease.Begin(protectedVessel, FlightDriver.Pause, Time.timeScale, FlightInputHandler.state.mainThrottle);
            }
            held = true;
            FlightInputHandler.state.mainThrottle = 0;
            applyingHold = true;
            try { if (!FlightDriver.Pause) FlightDriver.SetPause(true, false); }
            finally { applyingHold = false; }
            Time.timeScale = 0;
        }
        static bool ReadyForBoundary() => HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION ||
            (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null);
        static void EnsureSession()
        {
            if (session != 0) return;
            var bytes = new byte[8]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            session = BitConverter.ToInt64(bytes, 0); if (session == 0) session = 1;
        }
        static void CancelBoundary()
        {
            Recovery.Cancel();
            WarpSystem.Singleton.WaitingSubspaceIdFromServer = false;
            while (Acknowledgements.TryDequeue(out _)) { }
        }
        static void ReleaseOwnedHold(bool completed)
        {
            if (!held) return;
            var release = Lease.End(completed, FlightGlobals.ActiveVessel == null ? Guid.Empty : FlightGlobals.ActiveVessel.id,
                FlightDriver.Pause, Time.timeScale, FlightInputHandler.state.mainThrottle);
            held = false; aligning = true;
            try
            {
                if (release.Unpause) FlightDriver.SetPause(false, false);
                if (release.RestoreScale) Time.timeScale = release.Scale;
                if (release.RestoreThrottle) FlightInputHandler.state.mainThrottle = release.Throttle;
            }
            finally { aligning = false; protectedVessel = Guid.Empty; }
        }
        static void SceneRequested(GameScenes next)
        {
            if (!installed) return;
            if (Recovery.Pending || held) SaveRecovery("scene-transition");
            CancelBoundary(); transitioning = true;
            // Keep flight frozen until Unity has actually left it. Update runs unscaled.
        }
        static void SceneReady(GameScenes scene)
        {
            observedScene = scene; transitioning = false;
            if (!HighLogic.LoadedSceneIsFlight) ReleaseOwnedHold(false);
        }
        static void VesselDestroyed(Vessel vessel)
        {
            if (vessel != null && vessel.id == protectedVessel) EndRemovedFlight();
        }
        static void VesselTerminated(ProtoVessel vessel)
        {
            if (vessel != null && vessel.vesselID == protectedVessel) EndRemovedFlight();
        }
        static void EndRemovedFlight()
        {
            SaveRecovery("vessel-removed"); CancelBoundary(); ReleaseOwnedHold(false);
            // A replacement vessel must negotiate a fresh request, never consume this vessel's ACK.
        }
        static void Fail(string reason)
        {
            if (failure != null) return;
            failure = reason; Hold(); SaveRecovery("sync-failure"); CancelBoundary();
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
                if (reason == "periodic" || reason == "warp-boundary")
                { owned.Clear(); if (!VesselCommon.IsSpectating) owned.Add(FlightGlobals.ActiveVessel.id); }
                int count = 0, skipped = 0;
                foreach (var vessel in FlightGlobals.Vessels.Where(v => v != null && owned.Contains(v.id)))
                {
                    try
                    {
                        var proto = vessel.loaded ? vessel.BackupVessel() : vessel.protoVessel;
                        if (proto == null) throw new InvalidDataException("Vessel proto unavailable.");
                        var node = new ConfigNode("VESSEL"); proto.Save(node); root.AddNode(node); count++;
                    }
                    catch (Exception error)
                    {
                        // A broken mod module on one stored vessel must not erase
                        // the active vessel's recovery. Never label this a complete
                        // capture or replace the last complete recovery file.
                        skipped++; var missing = root.AddNode("SKIPPED_VESSEL");
                        missing.AddValue("id", vessel.id); missing.AddValue("errorType", error.GetType().Name);
                    }
                }
                if (count == 0)
                {
                    if (skipped == 0) return; // A spectator has no owned active recovery to capture.
                    throw new InvalidDataException("No vessel could be captured; previous recovery files retained.");
                }
                root.AddValue("complete", skipped == 0);
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "APF", "LocalFlightRecovery");
                Directory.CreateDirectory(directory);
                string text = root.ToString();
                if (text.Length > 16777216) throw new InvalidDataException("Local recovery exceeds 16 MiB text limit; previous snapshot retained.");
                bool failed = reason == "disconnect" || reason == "sync-failure";
                string stem = failed ? "failure" : reason == "periodic" ? "active" : "latest";
                if (skipped != 0) stem += "-partial";
                string temporary = Path.Combine(directory, "pending.cfg"), current = Path.Combine(directory, stem + ".cfg"), previous = Path.Combine(directory, stem + "-previous.cfg");
                File.WriteAllText(temporary, text);
                if (File.Exists(current)) File.Replace(temporary, current, previous); else File.Move(temporary, current);
                Debug.Log("[Aetherfront Stability] Local recovery saved: " + reason + "; vessels=" + count + "; skipped=" + skipped +
                    (skipped == 0 ? ". " : ". PARTIAL capture; last complete file retained. ") + "No server state or science is overwritten.");
            }
            catch (Exception error) { Debug.LogError("[Aetherfront Stability] Recovery capture failed; previous snapshot retained: " + error); }
        }
        public void Update()
        {
            if (!installed) return;
            CheckpointReconnect.Tick();
            if (Running && !fieldHookTried)
            {
                fieldHookTried = true;
                try { ClientTraffic.InstallFieldHook(new Harmony("aetherfront.rc56.cosmetic-source")); Debug.Log("[Aetherfront Stability] Cosmetic field source filter installed after part-module initialization."); }
                catch (Exception error) { Debug.LogWarning("[Aetherfront Stability] Cosmetic source optimization unavailable; network filter remains active: " + error.GetType().Name); }
            }
            int current = CurrentGeneration;
            if (current != generation)
            {
                generation = current; Recovery.Reset(); hasStableOffset = false; session = nextRequest = 0;
                while (Acknowledgements.TryDequeue(out _)) { }
                failure = null;
            }
            if (HighLogic.LoadedScene != observedScene)
            {
                observedScene = HighLogic.LoadedScene;
                if (!HighLogic.LoadedSceneIsFlight) ReleaseOwnedHold(false);
            }
            if (disconnectRequested && HighLogic.LoadedSceneIsFlight) { disconnectRequested = false; ProtectDisconnect(); }
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                ReleaseOwnedHold(false);
                disconnectRequested = false; transitioning = false; Recovery.Reset(); return;
            }
            if (!Running) { CancelBoundary(); if (held && HighLogic.LoadedSceneIsFlight) Hold(); return; }
            if (transitioning) return;
            var warp = WarpSystem.Singleton;
            if (warp.CurrentSubspace >= 0 && !Recovery.Pending && warp.Subspaces.TryGetValue(warp.CurrentSubspace, out var offset))
            { stableOffset = offset; hasStableOffset = true; }
            while (Acknowledgements.TryDequeue(out var ack))
            {
                if (!Recovery.CanAccept(ack.Generation, ack.Session, ack.Request, ack.Id, ack.Offset, ack.Received) || TimeWarp.CurrentRateIndex != 0) continue;
                double target = TimeSyncSystem.ServerClockSec + ack.Offset, now = Planetarium.GetUniversalTime();
                if (!WarpRecoveryPolicy.Finite(target) || target < now - 0.05 || target - now > forwardBudget)
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
                    ReleaseOwnedHold(true);
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
            else if (!held && ReadyForBoundary() && warp.CurrentSubspace == -1 && TimeWarp.CurrentRateIndex == 0) SendBoundary();
            else if (!held && warp.CurrentSubspace >= 0 && HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && TimeWarp.CurrentRateIndex == 0 &&
                Math.Abs(warp.CurrentSubspaceTime - Planetarium.GetUniversalTime()) > 3.5) SendBoundary();
            if (!held && Time.realtimeSinceStartup >= nextSnapshot && HighLogic.LoadedSceneIsFlight)
            { nextSnapshot = Time.realtimeSinceStartup + 30; SaveRecovery("periodic"); }
        }
        public void LateUpdate() { if (held && HighLogic.LoadedSceneIsFlight) Hold(); }
        public void FixedUpdate() { if (disconnectRequested && HighLogic.LoadedSceneIsFlight) { disconnectRequested = false; ProtectDisconnect(); } }
        public void OnGUI()
        {
            CheckpointReconnect.Draw();
            if (!held && !Recovery.Pending && failure == null) return;
            // IMGUI and this button keep running at timeScale zero; no scaled coroutine is needed.
            if (GUI.Button(new Rect(12, 90, 310, 32), "Synchronizing: disconnect safely"))
            {
                Hold(); SaveRecovery("manual-safe-exit"); CancelBoundary();
                NetworkConnection.Disconnect("Player chose safe exit from synchronization; local recovery preserved.");
                MainSystem.Singleton.ForceQuit = true;
            }
        }
    }
}
