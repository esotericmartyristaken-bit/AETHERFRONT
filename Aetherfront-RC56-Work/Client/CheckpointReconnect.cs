using System;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Synchronization;
using HarmonyLib;
using LmpClient;
using LmpClient.Network;
using LmpCommon.Enums;
using UnityEngine;

namespace Aetherfront.Stability.Client
{
    public static class CheckpointReconnect
    {
        static readonly object Gate = new object();
        static readonly CheckpointReconnectPolicy Policy = new CheckpointReconnectPolicy();
        static readonly PropertyInfo Generation = AccessTools.Property(typeof(NetworkConnection), "ConnectionGeneration");
        static FieldInfo Pending, Status;
        static MethodInfo StopGame;
        static string address; static int port;
        static volatile bool leaveFlight;
        static bool notice;
        static double nextSafetyReconnect;
        static double nextExitAttempt;
        public static bool Exiting { get; private set; }
        public static void NotifyDisconnect(){leaveFlight=true;}
        public static void SafeExit(bool reconnect)
        {
            // Only called for our own local safety failure or explicit safe-exit
            // button. Ordinary disconnects/kicks do not authorize reconnect.
            lock (Gate)
            {
                Policy.Cancel(); notice = false; address = null;
                if (reconnect && Now >= nextSafetyReconnect)
                {
                    address = NetworkConnection.LastConnectAddress; port = NetworkConnection.LastConnectPort;
                    if (!string.IsNullOrEmpty(address) && port > 0 && port <= 65535)
                    {
                        Policy.BeginLocalRecovery((int)Generation.GetValue(null, null), Now);
                        nextSafetyReconnect = Now + 300; // Never create a repeated launch/reconnect loop.
                    }
                }
                leaveFlight = true;
            }
        }
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        public static void Install(Harmony harmony)
        {
            var type = Type.GetType("Aetherfront.Personal.Client.FlightCheckpointClient, AetherfrontPersonalClient", false)
                ?? AccessTools.TypeByName("Aetherfront.Personal.Client.FlightCheckpointClient");
            if (type == null) throw new TypeLoadException("Shipping personal checkpoint controls missing.");
            Pending = AccessTools.Field(type, "pending"); Status = AccessTools.Field(type, "status");
            if (Pending == null || Status == null) throw new MissingFieldException("Personal checkpoint request state changed.");
            StopGame = AccessTools.Method(typeof(MainSystem), "StopGame", Type.EmptyTypes);
            if (StopGame == null || !StopGame.IsStatic) throw new MissingMethodException("Native main-menu exit is unavailable.");
            harmony.Patch(AccessTools.Method(type, "SendPending"), postfix: new HarmonyMethod(typeof(CheckpointReconnect), nameof(Sent)));
            harmony.Patch(AccessTools.Method(typeof(NetworkConnection), "Disconnect", new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(CheckpointReconnect), nameof(Disconnecting)) { priority = Priority.First });
        }
        static void Sent(object __instance)
        {
            var request = Pending.GetValue(__instance) as AetherfrontWireRequest;
            if (request == null || (int)request.CommandType != 82 || string.IsNullOrEmpty(request.CommandId)) return;
            lock (Gate)
            {
                if (Policy.Active || Policy.Armed && Policy.Request == request.CommandId) return;
                address = NetworkConnection.LastConnectAddress; port = NetworkConnection.LastConnectPort;
                if (string.IsNullOrEmpty(address) || port < 1 || port > 65535) return;
                Policy.Arm((int)Generation.GetValue(null, null), request.CommandId, Now);
                Status.SetValue(__instance, "Loading your checkpoint. Returning to the menu and reconnecting automatically after the server finishes.");
                Debug.Log("[Aetherfront Stability] Checkpoint restore requested; request=" + request.CommandId + "; generation=" + Policy.Generation);
            }
        }
        static void Disconnecting(string reason)
        {
            lock (Gate)
                if (Policy.Disconnect((int)Generation.GetValue(null, null), reason, Now)) leaveFlight = true;
        }
        public static void Tick()
        {
            if (leaveFlight&&Now>=nextExitAttempt)
            {
                nextExitAttempt=Now+0.5;Exiting=true;
                lock (Gate) notice = Policy.Active;
                // Native StopGame exits flight and resets its systems. Do not thaw
                // a disconnected vessel or join the new timeline in the old scene.
                MainSystem.Singleton.ForceQuit = true;
                try
                {
                    // Do not wait behind SystemsHandler.Update: a failing scene
                    // system can throw before MainSystem reaches its ForceQuit
                    // branch. This addon runs on Unity's unscaled Update thread.
                    if (MainSystem.NetworkState != ClientState.Disconnected)
                        MainSystem.NetworkState = ClientState.Disconnected;
                    StopGame.Invoke(null, null);
                    FlightSafety.CompleteMenuExit();
                    MainSystem.Singleton.ForceQuit = false;
                    leaveFlight=false;
                    Debug.Log("[Aetherfront Stability] Safe disconnect confirmed; native main-menu exit requested.");
                }
                catch (Exception error)
                {
                    // Keep the native exit request armed. Never resume the old
                    // flight, bypass a failed reset, or connect in that scene.
                    Debug.LogError("[Aetherfront Stability] Checkpoint menu exit failed; native safe exit remains requested: " + (error.InnerException ?? error));
                }
            }
            if(HighLogic.LoadedScene==GameScenes.MAINMENU)Exiting=false;
            bool connect, stopped; string target; int targetPort, attempt;
            lock (Gate)
            {
                bool wasActive = Policy.Active;
                connect = Policy.Tick(Now, HighLogic.LoadedScene == GameScenes.MAINMENU,
                    MainSystem.NetworkState == ClientState.Disconnected, NetworkConnection.ResetRequested,
                    MainSystem.NetworkState == ClientState.Running);
                target = address; targetPort = port; attempt = Policy.Attempts;
                stopped = wasActive && !Policy.Active;
                if (stopped)
                {
                    notice = false; address = null;
                }
            }
            // Native callbacks can enter here while already owning the connection
            // gate. Never invert that order by connecting while owning our gate.
            if (connect)
            {
                MainSystem.Singleton.Status = "Reconnecting to your saved session (attempt " + attempt + "/3).";
                Debug.Log("[Aetherfront Stability] Reconnecting to saved session; attempt=" + attempt);
                // Reuse the normal approved endpoint/identity/pack admission.
                // Password remains only in the existing native memory field.
                NetworkConnection.ConnectToServer(target, targetPort, NetworkConnection.LastConnectPassword);
            }
            else if (stopped && MainSystem.NetworkState != ClientState.Running)
                MainSystem.Singleton.Status = "Automatic reconnect stopped. Connect normally to resume your saved fleet.";
        }
        public static void Draw()
        {
            lock (Gate)
            {
                if (!notice || !Policy.Active) return;
                GUI.Label(new Rect(12, 126, 540, 25), "Returning to menu and reconnecting to your saved session.");
                if (GUI.Button(new Rect(12, 153, 280, 30), "Cancel automatic reconnect"))
                { Policy.Cancel(); notice = false; address = null; }
            }
        }
    }
}
