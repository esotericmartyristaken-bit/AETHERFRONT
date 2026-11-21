using System;
using System.Collections.Generic;
using System.Reflection;
using Aetherfront.Core;
using HarmonyLib;
using LmpClient;
using LmpClient.Aetherfront;
using LmpClient.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Types;
using UnityEngine;

namespace Aetherfront.Tracking
{
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public sealed class TrackingOverlay : MonoBehaviour
    {
        private const string PatchId = "aetherfront.tracking.overlay";
        private static readonly object Gate = new object();
        private static string pending;
        private static AetherfrontWireReply received;
        private readonly List<Point> points = new List<Point>();
        private readonly HashSet<Guid> stock = new HashSet<Guid>();
        private string revision = "-";
        private string pendingId;
        private float nextPoll, deadline, expires, nextStockUpdate;
        private static MethodInfo handler;
        private GUIStyle label;

        private void Awake()
        {
            if (handler != null) return;
            var method = AccessTools.Method(typeof(AetherfrontClientSystem), "ApplyReply", new[] { typeof(AetherfrontWireReply) });
            if (method == null) { Debug.LogError("[Aetherfront Tracking] Reply handler not found"); enabled = false; return; }
            try
            {
                new Harmony(PatchId).Patch(method, prefix: new HarmonyMethod(typeof(TrackingOverlay), nameof(Receive)));
                handler = method;
            }
            catch (Exception e)
            {
                enabled = false;
                Debug.LogError("[Aetherfront Tracking] Reply hook unavailable: " + e.GetType().Name);
            }
        }

        // Keep the hook installed to consume late tracking replies after leaving the scene.
        private void OnDestroy() => Reset();

        private static bool Receive(AetherfrontWireReply __0)
        {
            if (__0.ReasonCode != Wire.Route || __0.Operation != AetherfrontWireOperation.SnapshotReply) return true;
            lock (Gate)
                if (pending != null && string.Equals(pending, __0.TradeId, StringComparison.Ordinal) && received == null)
                    received = __0;
            return false;
        }

        private void Update()
        {
            if (!AetherfrontClientSystem.Ready || HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            { Reset(); return; }

            float now = Time.realtimeSinceStartup;
            if (pendingId != null)
            {
                AetherfrontWireReply reply;
                lock (Gate) { reply = received; }
                if (reply != null || now >= deadline)
                {
                    CancelPending();
                    if (reply != null && now < deadline && Accept(reply)) expires = now + 8;
                }
            }
            if (now >= expires && revision != "-")
            { revision = "-"; points.Clear(); }

            if (pendingId == null && now >= nextPoll)
            {
                pendingId = Guid.NewGuid().ToString("N");
                lock (Gate) { pending = pendingId; received = null; }
                nextPoll = now + 3;
                deadline = now + 5;
                var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AetherfrontCommandMsgData>();
                data.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireRequest
                {
                    Operation = AetherfrontWireOperation.SnapshotRequest,
                    Name = Wire.Route, CommandId = pendingId, Description = revision
                });
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AetherfrontCliMsg>(data));
            }
            if (now >= nextStockUpdate)
            {
                nextStockUpdate = now + 0.5f;
                stock.Clear();
                if (FlightGlobals.Vessels != null)
                    foreach (var vessel in FlightGlobals.Vessels)
                        if (vessel != null) stock.Add(vessel.id);
            }
        }

        private bool Accept(AetherfrontWireReply reply)
        {
            if (!reply.Accepted || !Wire.IsRevision(reply.FactionId)) return false;
            string json = reply.InterestJson;
            if (string.IsNullOrEmpty(json)) return reply.FactionId == revision;
            if (json.Length > Wire.MaxPayload || FlightGlobals.Bodies == null) return false;

            List<Marker> rows;
            try { rows = AetherfrontJson.Deserialize<List<Marker>>(json); }
            catch (Exception e) { Debug.LogWarning("[Aetherfront Tracking] Invalid catalog: " + e.GetType().Name); return false; }
            if (rows == null || rows.Count > Wire.Limit) return false;

            points.Clear();
            var seen = new HashSet<Guid>();
            foreach (var row in rows)
            {
                if (row == null || !Guid.TryParseExact(row.Id, "N", out var id) || id == Guid.Empty ||
                    !row.HasValidPosition() || !seen.Add(id)) continue;
                var body = FlightGlobals.Bodies.Find(b => string.Equals(b.bodyName, row.Body, StringComparison.Ordinal));
                // REF is only a fallback for older saves without a body name.
                if (body == null && (string.IsNullOrEmpty(row.Body) || row.Body == "Unknown"))
                    body = FlightGlobals.Bodies.Find(b => b.flightGlobalsIndex == row.ReferenceBody);
                if (body == null) continue;
                var p = row.Position;
                var orbit = row.Surface ? null : new Orbit(p[0], p[1], p[2], p[3], p[4], p[5], p[6], body);
                points.Add(new Point { Id = id, Body = body, Position = p, Orbit = orbit,
                    Name = string.IsNullOrWhiteSpace(row.Name) ? "Foreign craft" : row.Name });
            }
            revision = reply.FactionId;
            return true;
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || points.Count == 0 || !AetherfrontClientSystem.Ready ||
                HighLogic.LoadedScene != GameScenes.TRACKSTATION || Time.realtimeSinceStartup >= expires) return;
            var camera = PlanetariumCamera.Camera;
            if (camera == null) return;
            if (label == null) label = new GUIStyle(GUI.skin.label) { normal = { textColor = Color.cyan }, richText = false };
            float nearest = 12 * 12;
            Point hovered = null;
            Vector2 hoverAt = default;
            var mouse = Event.current.mousePosition;
            double ut = Planetarium.GetUniversalTime();
            foreach (var point in points)
            {
                if (stock.Contains(point.Id) || point.Body == null) continue;
                var p = point.Position;
                Vector3d world = point.Orbit == null ? point.Body.GetWorldSurfacePosition(p[0], p[1], p[2]) : point.Orbit.getPositionAtUT(ut);
                if (!Wire.Finite(world.x) || !Wire.Finite(world.y) || !Wire.Finite(world.z)) continue;
                var screen = camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(world));
                if (!Wire.Finite(screen.x) || !Wire.Finite(screen.y) || !Wire.Finite(screen.z) ||
                    screen.z <= 0 || screen.x < 0 || screen.x >= Screen.width ||
                    screen.y < 0 || screen.y >= Screen.height) continue;
                float y = Screen.height - screen.y;
                GUI.Label(new Rect(screen.x - 4, y - 10, 14, 20), "•", label);
                float distance = (new Vector2(screen.x, y) - mouse).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance; hovered = point; hoverAt = new Vector2(screen.x, y);
            }
            if (hovered != null)
                GUI.Label(new Rect(Mathf.Clamp(hoverAt.x + 8, 0, Mathf.Max(0, Screen.width - 280)),
                    Mathf.Clamp(hoverAt.y - 11, 0, Mathf.Max(0, Screen.height - 22)), 280, 22),
                    hovered.Name + " (read only)", label);
        }

        private void Reset()
        {
            CancelPending();
            points.Clear();
            stock.Clear();
            revision = "-";
            expires = nextPoll = nextStockUpdate = 0;
        }

        private void CancelPending()
        {
            lock (Gate) { pending = null; received = null; }
            pendingId = null;
        }

        private sealed class Point
        {
            internal Guid Id;
            internal string Name;
            internal CelestialBody Body;
            internal Orbit Orbit;
            internal double[] Position;
        }
    }
}
