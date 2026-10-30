using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LmpClient;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.TimeSync;
using LmpClient.Systems.Warp;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Interface;
using UnityEngine;

namespace Aetherfront.Continuity.Client
{
    // HandleMessage runs off the Unity thread. Only capture the acknowledgement
    // there; the actual forward-only clock alignment is performed in Update.
    public sealed class ClockTransitionCompatibility : MonoBehaviour
    {
        private static readonly ConcurrentQueue<int> Acknowledged = new ConcurrentQueue<int>();
        private sealed class Floor { internal double Offset; internal bool Valid; }
        private static readonly ConditionalWeakTable<WarpSystem, Floor> Floors = new ConditionalWeakTable<WarpSystem, Floor>();
        internal static void Install(Harmony harmony)
        {
            var handler = AccessTools.DeclaredMethod(typeof(WarpMessageHandler), nameof(WarpMessageHandler.HandleMessage), new[] { typeof(IServerMessageBase) });
            if (handler == null) throw new MissingMethodException("WARP acknowledgement handler");
            harmony.Patch(handler, postfix: new HarmonyMethod(typeof(ClockTransitionCompatibility), nameof(AfterMessage)));
            var rate = AccessTools.DeclaredMethod(typeof(WarpEvents), nameof(WarpEvents.OnTimeWarpChanged), Type.EmptyTypes);
            if (rate == null) throw new MissingMethodException("WARP rate event handler");
            harmony.Patch(rate, postfix: new HarmonyMethod(typeof(ClockTransitionCompatibility), nameof(RecordWarpBoundary)));
            var setter = AccessTools.PropertySetter(typeof(WarpSystem), nameof(WarpSystem.CurrentSubspace));
            var disabled = AccessTools.DeclaredMethod(typeof(WarpSystem), "OnDisabled", Type.EmptyTypes);
            var sender = AccessTools.DeclaredMethod(typeof(WarpMessageSender), nameof(WarpMessageSender.SendNewSubspace), Type.EmptyTypes);
            if (setter == null || disabled == null || sender == null) throw new MissingMethodException("WARP boundary lifecycle");
            harmony.Patch(setter, prefix: new HarmonyMethod(typeof(ClockTransitionCompatibility), nameof(CaptureFloor)));
            harmony.Patch(disabled, prefix: new HarmonyMethod(typeof(ClockTransitionCompatibility), nameof(ClearFloor)));
            harmony.Patch(sender, transpiler: new HarmonyMethod(typeof(ClockTransitionCompatibility), nameof(RewriteProposal)));
            var host = new GameObject("Aetherfront clock boundary alignment");
            DontDestroyOnLoad(host);
            host.AddComponent<ClockTransitionCompatibility>();
        }

        private static void CaptureFloor(WarpSystem __instance, int value)
        {
            if (value == int.MinValue) { Floors.Remove(__instance); return; }
            int stable = value >= 0 ? value : __instance.CurrentSubspace;
            double offset;
            if (stable < 0 || !__instance.Subspaces.TryGetValue(stable, out offset)) return;
            var floor = Floors.GetValue(__instance, _ => new Floor());
            lock (floor) { floor.Offset = offset; floor.Valid = true; }
        }

        private static void ClearFloor(WarpSystem __instance) => Floors.Remove(__instance);

        public static double ForwardOffset(double proposed, double stableOffset, bool hasFloor)
            => hasFloor && !double.IsNaN(proposed) && !double.IsInfinity(proposed) &&
                !double.IsNaN(stableOffset) && !double.IsInfinity(stableOffset) ? Math.Max(proposed, stableOffset) : proposed;

        public static IEnumerable<CodeInstruction> RewriteProposal(IEnumerable<CodeInstruction> source)
        {
            var codes = source.ToList();
            int count = 0;
            var clamp = AccessTools.Method(typeof(ClockTransitionCompatibility), nameof(ClampProposal));
            for (int i = 0; i < codes.Count; i++)
                if (codes[i].opcode == OpCodes.Stfld && codes[i].operand is FieldInfo field &&
                    field.DeclaringType == typeof(WarpNewSubspaceMsgData) && field.Name == nameof(WarpNewSubspaceMsgData.ServerTimeDifference))
                {
                    var injected = new CodeInstruction(OpCodes.Call, clamp);
                    injected.labels.AddRange(codes[i].labels); codes[i].labels.Clear();
                    injected.blocks.AddRange(codes[i].blocks); codes[i].blocks.Clear();
                    codes.Insert(i++, injected); count++;
                }
            if (count != 1) throw new InvalidOperationException("Unreviewed WARP proposal producer: " + count);
            return codes;
        }

        private static double ClampProposal(double proposed)
        {
            var system = WarpSystem.Singleton;
            Floor floor;
            if (MainSystem.NetworkState != ClientState.Running || system.CurrentSubspace != -1 || !Floors.TryGetValue(system, out floor)) return proposed;
            double canonical;
            lock (floor) canonical = ForwardOffset(proposed, floor.Offset, floor.Valid);
            if (canonical > proposed)
                Debug.Log("[Aetherfront Clock] Warp request held at own stable offset after a " +
                    (canonical-proposed).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " second simulation lag.");
            return canonical;
        }

        private static void AfterMessage(IServerMessageBase msg)
        {
            var data = msg?.Data as WarpNewSubspaceMsgData;
            if (data != null && data.SubspaceKey >= 0 && data.PlayerCreator == SettingsSystem.CurrentSettings.PlayerName)
                Acknowledged.Enqueue(data.SubspaceKey);
        }

        private static void RecordWarpBoundary()
        {
            if (MainSystem.NetworkState != ClientState.Running || !MainSystem.IsUnityThread) return;
            var system = WarpSystem.Singleton;
            Debug.Log("[Aetherfront Clock] Warp rate changed: index=" + TimeWarp.CurrentRateIndex +
                ", rate=" + TimeWarp.CurrentRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                ", menu=" + (PauseMenu.exists && PauseMenu.isOpen) + ", paused=" + FlightDriver.Pause +
                ", subspace=" + system.CurrentSubspace + ", awaiting=" + system.WaitingSubspaceIdFromServer +
                ", UT=" + Planetarium.GetUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        public static bool ShouldAlign(int acknowledged, int current, bool running, bool warping, double target, double now)
            => running && !warping && acknowledged == current && acknowledged >= 0 &&
                !double.IsNaN(target) && !double.IsInfinity(target) && !double.IsNaN(now) && !double.IsInfinity(now) &&
                target > now + 0.001d && target <= 1e12 && now >= 0;

        private void Update()
        {
            int id;
            while (Acknowledged.TryDequeue(out id))
            {
                var system = WarpSystem.Singleton;
                if (MainSystem.NetworkState != ClientState.Running || system.CurrentSubspace != id || system.WaitingSubspaceIdFromServer ||
                    TimeWarp.CurrentRateIndex > 0 || Math.Abs(TimeWarp.CurrentRate - 1f) >= 0.1f) continue;
                double offset;
                if (!system.Subspaces.TryGetValue(id, out offset)) continue;
                double target = system.GetSubspaceTime(id);
                double now = Planetarium.GetUniversalTime();
                if (!ShouldAlign(id, system.CurrentSubspace, true, false, target, now)) continue;
                // Use the existing vessel/UT alignment without firing a second
                // warp-stopped event for a boundary already acknowledged.
                TimeSyncSystem.Singleton.SetGameTime(target);
                Debug.Log("[Aetherfront] Personal warp acknowledgement aligned forward by " + (target-now).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
            }
        }
    }
}
