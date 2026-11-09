using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;
using LmpClient;
using LmpClient.Aetherfront;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    public sealed class FlightCheckpointClient : MonoBehaviour
    {
        public sealed class CheckpointInfo
        {
            public string RequestId { get; set; }
            public string CheckpointId { get; set; }
            public string SavedUtc { get; set; }
            public double MissionTime { get; set; }
            public int VesselCount { get; set; }
            public long Generation { get; set; }
            public string LastRestoreId { get; set; }
            public List<RestorePoint> Points { get; set; }
            public string AutomaticStatus { get; set; }
        }

        public sealed class RestorePoint
        {
            public string CheckpointId { get; set; }
            public string Label { get; set; }
            public string SavedUtc { get; set; }
            public double MissionTime { get; set; }
            public int VesselCount { get; set; }
        }

        public sealed class Envelope
        {
            public int ProtocolVersion { get; set; }
            public string ServerId { get; set; }
            public bool Accepted { get; set; }
            public string ReasonCode { get; set; }
            public CheckpointInfo FlightCheckpoint { get; set; }
        }

        private static FlightCheckpointClient live;
        private static readonly object ReceiptGate = new object();
        private static readonly Queue<Envelope> Receipts = new Queue<Envelope>();
        private CheckpointInfo info;
        private string server, account, status = "Loading checkpoint information...";
        private string selectedPoint;
        private AetherfrontWireRequest pending;
        private float sentAt;
        private float nextRefresh;
        private int attempts;
        private bool visible, confirm, hooked;
        private Rect window = new Rect(420, 100, 380, 260);
        private const int WindowId = 1886553;

        public static void EnsureStarted()
        {
            if (!PersonalClient.Active || HighLogic.CurrentGame == null) return;
            if (live == null) new GameObject("Aetherfront personal checkpoints").AddComponent<FlightCheckpointClient>();
        }

        private void Awake()
        {
            if (live != null && live != this) { Destroy(this); return; }
            live = this;
            DontDestroyOnLoad(this);
            try
            {
                var harmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", true);
                var methodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", true);
                object harmony = Activator.CreateInstance(harmonyType, "aetherfront.personal.checkpoints");
                var patch = harmonyType.GetMethods().Single(method => method.Name == "Patch" && method.GetParameters().Length >= 5 && method.GetParameters()[0].ParameterType == typeof(MethodBase));
                var prefix = Activator.CreateInstance(methodType, typeof(FlightCheckpointClient).GetMethod(nameof(AllowStockCheckpoint)));
                var methods = typeof(QuickSaveLoad).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == "QuickSave" || method.Name == "QuickLoad").ToArray();
                if (methods.Length == 0) throw new MissingMethodException("KSP checkpoint methods are unavailable.");
                foreach (var method in methods)
                {
                    var arguments = new object[patch.GetParameters().Length];
                    arguments[0] = method; arguments[1] = prefix;
                    patch.Invoke(harmony, arguments);
                }
                hooked = true;
            }
            catch (Exception error)
            {
                status = "Personal checkpoints could not initialize. See KSP.log.";
                Debug.LogError("[Aetherfront] Checkpoint controls unavailable: " + (error.InnerException ?? error));
            }
        }

        public static bool AllowStockCheckpoint() => live == null || !live.hooked || !PersonalClient.Active;
        private void OnDestroy() { if (live == this) live = null; }

        public static bool Capture(string payload)
        {
            if (string.IsNullOrEmpty(payload) || payload.Length > 16384 || payload.IndexOf("\"FlightCheckpoint\"", StringComparison.Ordinal) < 0) return false;
            try
            {
                var envelope = AetherfrontJson.Deserialize<Envelope>(payload);
                var checkpoint = envelope?.FlightCheckpoint;
                if (checkpoint == null || envelope.ProtocolVersion != 1 || string.IsNullOrEmpty(envelope.ServerId) || envelope.ServerId.Length > 256 ||
                    !Guid.TryParseExact(checkpoint.RequestId, "N", out var request) || request == Guid.Empty ||
                    checkpoint.Generation < 0 || checkpoint.VesselCount < 0 || checkpoint.VesselCount > 512 ||
                    double.IsNaN(checkpoint.MissionTime) || double.IsInfinity(checkpoint.MissionTime) || checkpoint.MissionTime < 0 || checkpoint.MissionTime > 1e12 ||
                    envelope.ReasonCode == null || envelope.ReasonCode.Length > 1024 ||
                    (!string.IsNullOrEmpty(checkpoint.CheckpointId) && !Guid.TryParseExact(checkpoint.CheckpointId, "N", out _)) ||
                    checkpoint.Points == null || checkpoint.Points.Count > 5 || checkpoint.Points.Any(point => point == null ||
                        !Guid.TryParseExact(point.CheckpointId, "N", out _) || string.IsNullOrEmpty(point.Label) || point.Label.Length > 128 ||
                        !DateTimeOffset.TryParse(point.SavedUtc, out _) || point.VesselCount < 0 || point.VesselCount > 512 ||
                        double.IsNaN(point.MissionTime) || double.IsInfinity(point.MissionTime) || point.MissionTime < 0 || point.MissionTime > 1e12)) return false;
                lock (ReceiptGate)
                {
                    if (Receipts.Count >= 16) Receipts.Dequeue();
                    Receipts.Enqueue(envelope);
                }
                return true;
            }
            catch { return false; }
        }

        private void Update()
        {
            if (!PersonalClient.Active)
            {
                server = account = selectedPoint = null; info = null; pending = null; confirm = false;
                lock (ReceiptGate) Receipts.Clear();
                return;
            }
            if (server != AetherfrontClientSystem.State.ServerId || account != MainSystem.UniqueIdentifier)
            {
                server = AetherfrontClientSystem.State.ServerId; account = MainSystem.UniqueIdentifier;
                info = null; pending = null; selectedPoint = null; confirm = false;
                Request(81);
            }
            for (;;)
            {
                Envelope envelope;
                lock (ReceiptGate) { if (Receipts.Count == 0) break; envelope = Receipts.Dequeue(); }
                if (pending == null || envelope.ServerId != server || envelope.FlightCheckpoint.RequestId != pending.CommandId) continue;
                int command = (int)pending.CommandType;
                pending = null;
                if (!envelope.Accepted) { status = envelope.ReasonCode; continue; }
                info = envelope.FlightCheckpoint;
                nextRefresh = Time.realtimeSinceStartup + 30;
                if (info.Points == null) info.Points = new List<RestorePoint>();
                if (!info.Points.Any(point => point.CheckpointId == selectedPoint)) selectedPoint = info.Points.FirstOrDefault()?.CheckpointId;
                status = command == 80 ? "Personal fleet checkpoint saved." :
                    info.Points.Count == 0 ? "You have no personal checkpoint yet." : "Personal checkpoint ready.";
            }
            if (pending != null && Time.realtimeSinceStartup - sentAt > 15)
            {
                if (++attempts < 3) SendPending();
                else { pending = null; status = "The server has not confirmed this request. Refresh before trying again."; }
            }
            if (!hooked || !(HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION)) return;
            if (Input.GetKeyDown(KeyCode.F5)) { visible = true; Request(80); }
            if (Input.GetKeyDown(KeyCode.F9)) { visible = true; confirm = true; }
            if (visible && pending == null && Time.realtimeSinceStartup >= nextRefresh) Request(81);
        }

        private void Request(int command)
        {
            if (pending != null || !PersonalClient.Active) return;
            if (command != 81 && info == null) { status = "Refresh checkpoint information first."; return; }
            if (command == 82 && string.IsNullOrEmpty(selectedPoint)) { status = "Choose an available checkpoint first."; return; }
            pending = new AetherfrontWireRequest
            {
                CommandType = (AetherfrontCommandType)command, CommandId = Guid.NewGuid().ToString("N"),
                EntityId = command == 81 ? string.Empty : command == 82 ? selectedPoint : info.CheckpointId,
                Amount = info?.Generation ?? 0
            };
            attempts = 0;
            status = command == 80 ? "Saving your personal fleet..." : command == 82 ? "Loading your checkpoint. Reconnect after the server finishes." : "Refreshing checkpoint information...";
            SendPending();
        }

        private void SendPending()
        {
            sentAt = Time.realtimeSinceStartup;
            AetherfrontClientSystem.SendCommand(pending);
        }

        private void OnGUI()
        {
            if (!PersonalClient.Active || !(HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION)) return;
            if (GUI.Button(new Rect(Screen.width - 175, 55, 165, 28), "Personal checkpoints")) visible = !visible;
            if (visible) window = GUILayout.Window(WindowId, window, DrawWindow, "Personal checkpoints", GUILayout.Width(380));
        }

        private void DrawWindow(int ignored)
        {
            GUILayout.Label("F5 saves your personal fleet. F9 opens checkpoint loading.");
            if (info?.Points != null)
            {
                foreach (var point in info.Points)
                {
                    string label = point.Label + " | " + point.VesselCount + " craft";
                    if (DateTimeOffset.TryParse(point.SavedUtc, out var saved)) label += " | " + saved.ToLocalTime().ToString("g");
                    if (GUILayout.Toggle(selectedPoint == point.CheckpointId, label)) selectedPoint = point.CheckpointId;
                }
                var selected = info.Points.FirstOrDefault(point => point.CheckpointId == selectedPoint);
                if (selected != null) GUILayout.Label("Saved mission time " + selected.MissionTime.ToString("0.0", CultureInfo.InvariantCulture) + " UT");
            }
            GUILayout.Label(status ?? string.Empty);
            if (!string.IsNullOrEmpty(info?.AutomaticStatus)) GUILayout.Label(info.AutomaticStatus);
            bool wasEnabled = GUI.enabled;
            try
            {
                GUI.enabled = wasEnabled && pending == null && hooked;
                if (GUILayout.Button("Save personal checkpoint")) Request(80);
                if (GUILayout.Button("Refresh")) Request(81);
                if (confirm)
                {
                    GUILayout.Label("Load all your personal craft and saved mission time? Reconnect afterward. Banked science, research, funds and other players' progress stay current.");
                    if (GUILayout.Button("Load checkpoint")) { confirm = false; Request(82); }
                    if (GUILayout.Button("Keep playing")) confirm = false;
                }
                else if (GUILayout.Button("Load personal checkpoint...")) confirm = true;
            }
            finally { GUI.enabled = wasEnabled; }
            if (GUILayout.Button("Close")) visible = false;
            GUI.DragWindow();
        }
    }
}
