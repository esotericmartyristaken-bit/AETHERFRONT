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
        static string address; static int port;
        static volatile bool leaveFlight;
        static bool notice;
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        public static void Install(Harmony harmony)
        {
            var type = Type.GetType("Aetherfront.Personal.Client.FlightCheckpointClient, AetherfrontPersonalClient", false)
                ?? AccessTools.TypeByName("Aetherfront.Personal.Client.FlightCheckpointClient");
            if (type == null) throw new TypeLoadException("Shipping personal checkpoint controls missing.");
            Pending = AccessTools.Field(type, "pending"); Status = AccessTools.Field(type, "status");
            if (Pending == null || Status == null) throw new MissingFieldException("Personal checkpoint request state changed.");
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
            }
        }
        static void Disconnecting(string reason)
        {
            lock (Gate)
                if (Policy.Disconnect((int)Generation.GetValue(null, null), reason, Now)) leaveFlight = true;
        }
        public static void Tick()
        {
            if (leaveFlight)
            {
                leaveFlight = false; notice = true;
                // Native StopGame exits flight and resets its systems. Do not thaw
                // a disconnected vessel or join the new timeline in the old scene.
                MainSystem.Singleton.ForceQuit = true;
                Debug.Log("[Aetherfront Stability] Checkpoint restore disconnect confirmed; leaving flight before automatic reconnect.");
            }
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
                MainSystem.Singleton.Status = "Reconnecting after personal checkpoint restore (attempt " + attempt + "/3).";
                Debug.Log("[Aetherfront Stability] Reconnecting after personal checkpoint restore; attempt=" + attempt);
                // Reuse the normal approved endpoint/identity/pack admission.
                // Password remains only in the existing native memory field.
                NetworkConnection.ConnectToServer(target, targetPort, NetworkConnection.LastConnectPassword);
            }
            else if (stopped && MainSystem.NetworkState != ClientState.Running)
                MainSystem.Singleton.Status = "Automatic checkpoint reconnect stopped. Connect normally to resume your saved fleet.";
        }
        public static void Draw()
        {
            lock (Gate)
            {
                if (!notice || !Policy.Active) return;
                GUI.Label(new Rect(12, 126, 540, 25), "Checkpoint restored: returning to menu and reconnecting automatically.");
                if (GUI.Button(new Rect(12, 153, 280, 30), "Cancel automatic reconnect"))
                { Policy.Cancel(); notice = false; address = null; }
            }
        }
    }
}
