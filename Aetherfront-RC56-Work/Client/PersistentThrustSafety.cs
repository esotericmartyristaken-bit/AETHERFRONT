using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Aetherfront.Synchronization;
using HarmonyLib;
using LmpClient;
using LmpClient.Apf;
using LmpCommon.Enums;
using PersistentThrust;
using UnityEngine;

namespace Aetherfront.Stability.Client
{
    // Compatibility for the exact PT build in the signed pack. Keep native fuel,
    // engine and effects behavior; guard the packed burn before requesting fuel.
    public static class PersistentThrustSafety
    {
        internal sealed class State
        {
            internal readonly PersistentBurnPolicy Intent = new PersistentBurnPolicy();
            internal Vessel Vessel;
            internal bool Faulted;
            internal string Ticket;
        }
        internal sealed class BurnTicket { internal State State; internal Guid Vessel; internal uint Part; internal int Generation; }
        public sealed class BackgroundScope { internal BackgroundScope Previous; internal List<KeyValuePair<string, double>> Changes; internal int Count; internal double Dt; internal Vessel Vessel; internal bool Applied; internal BurnTicket Ticket; internal Orbit Backup; internal double UT; }
        public sealed class Scope { internal PersistentEngine Previous; }
        static readonly ConditionalWeakTable<PersistentEngine, State> States = new ConditionalWeakTable<PersistentEngine, State>();
        static readonly FieldInfo ThrottleQueue = AccessTools.Field(typeof(PersistentEngine), "_mainThrottleQueue");
        static readonly FieldInfo ResourceChanges = AccessTools.Field(typeof(PersistentEngine), "_kerbalismResourceChangeRequest");
        static Func<Vessel, bool> authority;
        static double nextNotice;
        static bool installed;
        static readonly ConcurrentDictionary<string, BurnTicket> Tickets = new ConcurrentDictionary<string, BurnTicket>(StringComparer.Ordinal);
        [ThreadStatic] static PersistentEngine executing;
        [ThreadStatic] static BackgroundScope background;
        static bool Multiplayer => ApfClientSettings.Enabled && MainSystem.NetworkState >= ClientState.Starting;
        static bool Protected => FlightSafety.SimulationProtected || (Multiplayer && MainSystem.NetworkState != ClientState.Running);
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

        public static void Install(Harmony harmony)
        {
            if (installed) return;
            if (typeof(PersistentEngine).Module.ModuleVersionId.ToString("N") != "2fe691b0050441e29beaf318f8f9b290" ||
                ThrottleQueue == null || ResourceChanges == null)
                throw new InvalidOperationException("Unreviewed PersistentThrust build; compatibility must be reviewed before connection.");
            var authorityType = AccessTools.TypeByName("Aetherfront.Continuity.Client.SimulationAuthority");
            var maySimulate = AccessTools.Method(authorityType, "MaySimulate", new[] { typeof(Vessel) });
            if (maySimulate == null) throw new MissingMethodException("Vessel simulation authority unavailable.");
            authority = (Func<Vessel, bool>)Delegate.CreateDelegate(typeof(Func<Vessel, bool>), maySimulate);
            harmony.Patch(AccessTools.Method(typeof(PersistentThrust.VesselExtension), "GetRequestedDirection"),
                prefix: Hook(nameof(DirectionReady)));
            harmony.Patch(AccessTools.Method(typeof(PersistentEngine), "OnSave"), prefix: Hook(nameof(SaveEngine)));
            harmony.Patch(AccessTools.Method(typeof(PersistentEngine), "FixedUpdate"), prefix: Hook(nameof(BeforePhysics)),
                transpiler: Hook(nameof(GuardPackedBurn)), finalizer: Hook(nameof(AfterPhysics)));
            harmony.Patch(AccessTools.Method(typeof(PersistentEngine), "OnUpdate"), transpiler: Hook(nameof(GuardControlWrites)));
            harmony.Patch(AccessTools.Method(typeof(PersistentEngine), "SetThrottle"), transpiler: Hook(nameof(AvoidRepeatedRateEvent)), postfix: Hook(nameof(ThrottleChanged)));
            harmony.Patch(AccessTools.Method(typeof(PersistentEngine), "UpdatePersistentThrottle"), prefix: Hook(nameof(RefreshThrottle)));
            harmony.Patch(AccessTools.Method(typeof(PersistentEngine), "BackgroundUpdate"), prefix: Hook(nameof(BackgroundReady)),
                transpiler: Hook(nameof(GuardBackgroundBurn)), finalizer: Hook(nameof(AfterBackground)));
            harmony.Patch(AccessTools.Method(typeof(TimeWarp), "SetRate", new[] { typeof(int), typeof(bool), typeof(bool) }),
                prefix: Hook(nameof(LimitWarpRate)));
            harmony.Patch(AccessTools.Method(typeof(Part), "Couple", new[] { typeof(Part) }),
                prefix: Hook(nameof(BeforeCouple)), postfix: Hook(nameof(AfterCouple)));
            foreach (var method in new[] { AccessTools.Method(typeof(Part), "decouple", new[] { typeof(float) }),
                AccessTools.Method(typeof(Part), "Undock", new[] { typeof(DockedVesselInfo) }) })
                harmony.Patch(method, prefix: Hook(nameof(BeforeSplit)), postfix: Hook(nameof(AfterSplit)));
            installed = true;
        }
        static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(PersistentThrustSafety), name);
        static bool Owns(Vessel vessel) => !ReferenceEquals(vessel, null) && (!Multiplayer || (authority != null && authority(vessel)));
        static bool WorldReady() => FlightGlobals.ready;
        static Vessel PilotVessel() => HighLogic.LoadedSceneIsFlight && WorldReady() ? FlightGlobals.ActiveVessel : null;
        static float PilotThrottle() => ReferenceEquals(FlightInputHandler.state, null) ? 0 : FlightInputHandler.state.mainThrottle;
        static bool Active(Vessel vessel) => ReferenceEquals(PilotVessel(), vessel);
        static bool Finite(Vector3d vector) => PersistentBurnPolicy.Finite(vector.x) && PersistentBurnPolicy.Finite(vector.y) && PersistentBurnPolicy.Finite(vector.z);
        static long Topology(Vessel vessel)
        {
            unchecked { return ((long)vessel.id.GetHashCode() << 32) ^ (vessel.parts == null ? 0 : vessel.parts.Count) ^
                (ReferenceEquals(vessel.rootPart, null) ? 0 : ((long)vessel.rootPart.persistentId << 1)); }
        }
        static State Get(PersistentEngine engine)
        {
            var state = States.GetValue(engine, _ => new State());
            var vessel = engine.vessel;
            if (!ReferenceEquals(state.Vessel, vessel)) { state.Vessel = vessel; state.Intent.Invalidate(); state.Faulted = false; }
            float input = Active(vessel) ? PilotThrottle() : 0;
            state.Intent.Observe(Topology(vessel), FlightSafety.ProtectionGeneration, Protected, Active(vessel) && !vessel.packed, input);
            if (!state.Intent.Armed && state.Ticket != null) { Tickets.TryRemove(state.Ticket, out _); state.Ticket = null; }
            return state;
        }
        static void ClearCache(PersistentEngine engine)
        {
            engine.persistentThrottle = 0; engine.persistentThrust = 0;
            engine.warpToReal = false; engine.warpToRealCountDown = 0;
            (ThrottleQueue.GetValue(engine) as Queue<float>)?.Clear();
            // Do not change native controls, engine ignition, resources, or another guard's hold.
        }
        public static bool HeadingAvailable(Vessel vessel)
        {
            if (ReferenceEquals(vessel, null) || ReferenceEquals(vessel.orbitDriver, null) || vessel.orbit == null || vessel.Autopilot == null) return false;
            var mode = vessel.Autopilot.Mode;
            if (mode == VesselAutopilot.AutopilotMode.Target || mode == VesselAutopilot.AutopilotMode.AntiTarget)
            {
                try
                {
                    var target = vessel.targetObject;
                    return target != null && !ReferenceEquals(target, vessel) && target.GetOrbit() != null && target.GetOrbitDriver() != null;
                }
                catch (NullReferenceException) { return false; }
                catch (MissingReferenceException) { return false; }
            }
            if (mode == VesselAutopilot.AutopilotMode.Maneuver)
                return vessel.patchedConicSolver != null && vessel.patchedConicSolver.maneuverNodes != null &&
                    vessel.patchedConicSolver.maneuverNodes.Count > 0 && vessel.patchedConicSolver.maneuverNodes[0] != null;
            return !ReferenceEquals(vessel.mainBody, null);
        }
        public static bool DirectionReady(Vessel __0, ref Vector3d __result)
        {
            if (HeadingAvailable(__0)) return true;
            __result = Vector3d.zero; return false; // Coasting is fine; packed burn authorization separately requires a heading.
        }
        public static bool SaveEngine(PersistentEngine __instance, ConfigNode __0)
        {
            var engine = __instance; var node = __0;
            if (!HighLogic.LoadedSceneIsFlight || ReferenceEquals(engine, null) || ReferenceEquals(engine.part, null)) return false;
            var vessel = engine.vessel;
            if (ReferenceEquals(vessel, null)) return false;
            if (node == null || vessel.parts == null) throw new InvalidDataException("Persistent engine save lacks required vessel data.");
            node.SetValue("vesselDryMass", vessel.GetDryMass().ToString("R", CultureInfo.InvariantCulture), true);
            var modules = engine.moduleEngines ?? new PersistentEngineModule[0];
            double average = engine.persistentAverageDensity;
            if (engine.isMultiMode && engine.currentEngine != null) average = engine.currentEngine.averageDensity;
            else if (engine.persistentThrust > 0 && modules.Length > 0)
            {
                double denominator = modules.Where(m => m != null && !ReferenceEquals(m.engine, null)).Sum(m => (double)m.engine.maxThrust);
                if (denominator > 0) average = modules.Where(m => m != null).Sum(m => (double)m.finalThrust * m.averageDensity) / denominator;
            }
            if (PersistentBurnPolicy.Finite(average)) node.SetValue("persistentAverageDensity", average.ToString("R", CultureInfo.InvariantCulture), true);
            var changes = ResourceChanges.GetValue(engine) as Dictionary<string, double>;
            if (changes != null && changes.Count > 0)
                node.SetValue("persistentResourceChange", string.Join(";", changes.Select(p => p.Key + "=" + p.Value.ToString("R", CultureInfo.InvariantCulture)).ToArray()), true);
            // A ticket is meaningful only in this process/connection/topology.
            // It permits a genuinely armed ship to continue in background, but
            // cannot rearm a ship after game restart or receipt from another pilot.
            State savedState = null;
            if (Owns(vessel) && Active(vessel) && !vessel.packed && !Protected) Get(engine);
            node.SetValue("HasPersistentThrust", engine.HasPersistentThrust.ToString(), true);
            bool armed = engine.HasPersistentThrust && engine.isEnabled && modules.Any(m => m != null && !ReferenceEquals(m.engine, null) && m.engine.getIgnitionState) &&
                States.TryGetValue(engine, out savedState) && savedState.Intent.Armed &&
                savedState.Intent.Generation == FlightSafety.ProtectionGeneration && Owns(vessel) && HeadingAvailable(vessel) && !savedState.Faulted;
            if (armed)
            {
                if (savedState.Ticket == null)
                {
                    if (Tickets.Count >= 4096) throw new InvalidDataException("Too many live persistent-burn tickets.");
                    savedState.Ticket = Guid.NewGuid().ToString("N");
                    if (!Tickets.TryAdd(savedState.Ticket, new BurnTicket { State = savedState, Vessel = vessel.id, Part = engine.part.persistentId, Generation = FlightSafety.ProtectionGeneration }))
                        throw new InvalidDataException("Duplicate persistent-burn ticket.");
                }
                node.SetValue("apfBurnTicket", savedState.Ticket, true);
                node.SetValue("persistentThrottle", savedState.Intent.Demand.ToString("R", CultureInfo.InvariantCulture), true);
            }
            else
            { node.RemoveValue("apfBurnTicket"); node.SetValue("persistentThrottle", "0", true); node.SetValue("persistentThrust", "0", true); }
            node.SetValue("persistentVesselTargetId", Guid.Empty.ToString(), true);
            node.SetValue("persistentVesselTargetBodyName", "", true);
            if (vessel.Autopilot == null) return false;
            var mode = vessel.Autopilot.Mode;
            if (mode == VesselAutopilot.AutopilotMode.Target || mode == VesselAutopilot.AutopilotMode.AntiTarget)
            {
                if (!HeadingAvailable(vessel))
                { node.SetValue("persistentAutopilotMode", VesselAutopilot.AutopilotMode.StabilityAssist.ToString(), true); return false; }
                var driver = vessel.targetObject.GetOrbitDriver();
                if (!ReferenceEquals(driver.vessel, null)) node.SetValue("persistentVesselTargetId", driver.vessel.id.ToString(), true);
                else if (!ReferenceEquals(driver.celestialBody, null)) node.SetValue("persistentVesselTargetBodyName", driver.celestialBody.bodyName, true);
            }
            else if (mode == VesselAutopilot.AutopilotMode.Maneuver)
            {
                var nodes = vessel.patchedConicSolver == null ? null : vessel.patchedConicSolver.maneuverNodes;
                var first = nodes == null || nodes.Count == 0 ? null : nodes[0];
                if (first != null && first.nextPatch != null && first.patch != null)
                {
                    node.SetValue("persistentManeuverUT", first.UT.ToString("R", CultureInfo.InvariantCulture), true);
                    node.SetValue("persistentManeuverNextPatch", first.nextPatch.Serialize(), true);
                    node.SetValue("persistentManeuverPatch", first.patch.Serialize(), true);
                }
                else
                { node.RemoveValue("persistentManeuverUT"); node.RemoveValue("persistentManeuverNextPatch"); node.RemoveValue("persistentManeuverPatch"); }
            }
            return false;
        }
        public static bool BeforePhysics(PersistentEngine __instance, out Scope __state)
        {
            __state = new Scope { Previous = executing }; executing = __instance;
            var vessel = __instance.vessel;
            if (ReferenceEquals(vessel, null) || !Owns(vessel)) return false;
            var state = Get(__instance);
            if (state.Faulted || Protected) return false;
            if (Active(vessel) && !state.Intent.Armed) ClearCache(__instance);
            if (!HeadingAvailable(vessel)) { ClearCache(__instance); return false; }
            if (vessel.packed && !AllowPackedBurn(__instance)) return false;
            if (vessel.packed) __instance.persistentThrottle = state.Intent.Demand;
            return true;
        }
        public static Exception AfterPhysics(Exception __exception, Scope __state)
        {
            var engine = executing; executing = __state == null ? null : __state.Previous;
            if (__exception != null && !ReferenceEquals(engine, null))
            {
                States.GetValue(engine, _ => new State()).Faulted = true;
                ClearCache(engine); Notice("Persistent engine paused after a module error; native flight remains available.");
            }
            return __exception; // Do not conceal unrelated errors or claim a successful simulation.
        }
        public static bool AllowPackedBurn(PersistentEngine engine)
        {
            if (ReferenceEquals(engine, null) || ReferenceEquals(engine.vessel, null)) return false;
            var state = Get(engine); var module = engine.currentEngine;
            return !state.Faulted && engine.isEnabled && engine.HasPersistentThrust && module != null && !ReferenceEquals(module.engine, null) &&
                state.Intent.Allows(Owns(engine.vessel), Protected, TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH,
                    module.engine.getIgnitionState, HeadingAvailable(engine.vessel));
        }
        public static bool AllowControlWrites(PersistentEngine engine)
        {
            if (ReferenceEquals(engine, null) || ReferenceEquals(engine.vessel, null) || !engine.HasPersistentThrust || !Owns(engine.vessel) || Protected) return false;
            var state = Get(engine);
            return !state.Faulted && state.Intent.Armed && Active(engine.vessel) && HeadingAvailable(engine.vessel);
        }
        public static bool RefreshThrottle(PersistentEngine __instance)
        {
            var vessel = __instance.vessel;
            if (ReferenceEquals(vessel, null) || !Owns(vessel) || Protected) return false;
            float command = vessel.ctrlState == null ? 0 : vessel.ctrlState.mainThrottle;
            (ThrottleQueue.GetValue(__instance) as Queue<float>)?.Clear();
            __instance.persistentThrottle = !float.IsNaN(command) && !float.IsInfinity(command) ? Mathf.Clamp01(command) : 0;
            return false;
        }
        public static void ThrottleChanged(PersistentEngine __instance, float __0)
        {
            if (ReferenceEquals(__instance.vessel, null) || !Owns(__instance.vessel) || Protected) return;
            var state = Get(__instance);
            state.Intent.Observe(Topology(__instance.vessel), FlightSafety.ProtectionGeneration, false, Active(__instance.vessel), __0);
            if (__0 <= 0) { ClearCache(__instance); if (state.Ticket != null) { Tickets.TryRemove(state.Ticket, out _); state.Ticket = null; } }
        }
        public static bool BackgroundReady(Vessel __0, ProtoPartSnapshot __1, ProtoPartModuleSnapshot __2, Part __4,
            List<KeyValuePair<string, double>> __6, double __7, ref string __result, out BackgroundScope __state)
        {
            __state = null;
            if (!Multiplayer) return true;
            __result = ReferenceEquals(__4, null) || __4.partInfo == null ? "Persistent engine" : __4.partInfo.title;
            BurnTicket ticket;
            string token = __2 == null || __2.moduleValues == null ? null : __2.moduleValues.GetValue("apfBurnTicket");
            if (token == null || !string.Equals(__2.moduleValues.GetValue("HasPersistentThrust"), "True", StringComparison.OrdinalIgnoreCase) ||
                !Tickets.TryGetValue(token, out ticket) || ReferenceEquals(__0, null) || __1 == null ||
                __1.modules == null || ticket.Vessel != __0.id || ticket.Part != __1.persistentId || ticket.Generation != FlightSafety.ProtectionGeneration ||
                !ticket.State.Intent.Armed || ticket.State.Faulted || Protected || !Owns(__0) ||
                !PersistentBurnPolicy.Finite(__7) || __7 <= 0 || __6 == null) return false;
            bool ignited = __1.modules.Any(m => m != null && m.moduleValues != null && (m.moduleName == "ModuleEngines" || m.moduleName == "ModuleEnginesFX") &&
                string.Equals(m.moduleValues.GetValue("EngineIgnited"), "True", StringComparison.OrdinalIgnoreCase));
            if (!ignited) return false;
            __state = new BackgroundScope { Previous = background, Changes = __6, Count = __6.Count, Dt = __7, Vessel = __0, Ticket = ticket,
                Backup = new Orbit(__0.orbit), UT = Planetarium.GetUniversalTime() };
            background = __state; return true;
        }
        public static Exception AfterBackground(Exception __exception, BackgroundScope __state)
        {
            if (__state != null)
            {
                if (__exception != null || !__state.Applied)
                    if (__state.Changes.Count > __state.Count) __state.Changes.RemoveRange(__state.Count, __state.Changes.Count - __state.Count);
                if (__exception != null && __state.Applied)
                {
                    var old = __state.Backup; var orbit = __state.Vessel.orbit;
                    orbit.UpdateFromStateVectors(old.getRelativePositionAtUT(__state.UT), old.getOrbitalVelocityAtUT(__state.UT), old.referenceBody, __state.UT);
                    orbit.Init(); orbit.UpdateFromUT(__state.UT);
                }
                background = __state.Previous;
            }
            return __exception;
        }
        public static IEnumerable<CodeInstruction> GuardBackgroundBurn(IEnumerable<CodeInstruction> source)
        {
            int changed = 0;
            foreach (var code in source)
            {
                if (code.operand is MethodInfo call && call.DeclaringType == typeof(OrbitExtensions) && call.Name == "Perturb")
                { code.operand = AccessTools.Method(typeof(PersistentThrustSafety), nameof(SafeBackgroundPerturb)); changed++; }
                yield return code;
            }
            if (changed != 1) throw new InvalidOperationException("Unreviewed persistent background orbit writer.");
        }
        public static void SafeBackgroundPerturb(Orbit orbit, Vector3d delta, double now)
        {
            if (!Multiplayer) { OrbitExtensions.Perturb(orbit, delta, now); return; }
            var scope = background;
            if (scope == null || !scope.Ticket.State.Intent.Armed || scope.Ticket.Generation != FlightSafety.ProtectionGeneration ||
                Protected || !Owns(scope.Vessel) || !ReferenceEquals(scope.Vessel.orbit, orbit) || !Finite(delta) || delta.sqrMagnitude <= 0 ||
                delta.magnitude > PersistentBurnPolicy.DeltaVBudget(orbit.getOrbitalVelocityAtUT(now).magnitude) ||
                !SafeTrajectory(orbit, delta, now, scope.Dt)) return;
            scope.Applied = true; OrbitExtensions.Perturb(orbit, delta, now);
        }
        public static IEnumerable<CodeInstruction> GuardPackedBurn(IEnumerable<CodeInstruction> source)
        {
            int flags = 0, vectors = 0, perturbations = 0;
            foreach (var code in source)
            {
                if (code.opcode == OpCodes.Ldfld && code.operand is FieldInfo flag && flag.DeclaringType == typeof(PersistentEngine) && flag.Name == "HasPersistentThrust")
                { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(PersistentThrustSafety), nameof(AllowPackedBurn)); flags++; }
                if (code.operand is MethodInfo call && call.DeclaringType == typeof(Utils) && call.Name == "CalculateDeltaVVector" && call.GetParameters().Length == 7)
                { code.operand = AccessTools.Method(typeof(PersistentThrustSafety), nameof(SafeDeltaV)); vectors++; }
                if (code.operand is MethodInfo perturb && perturb.DeclaringType == typeof(OrbitExtensions) && perturb.Name == "Perturb")
                { code.operand = AccessTools.Method(typeof(PersistentThrustSafety), nameof(SafePerturb)); perturbations++; }
                yield return code;
            }
            if (flags != 1 || vectors != 1 || perturbations != 1) throw new InvalidOperationException("Unreviewed persistent packed-burn pipeline.");
        }
        public static IEnumerable<CodeInstruction> GuardControlWrites(IEnumerable<CodeInstruction> source)
        {
            int changed = 0;
            foreach (var code in source)
            {
                if (code.opcode == OpCodes.Ldfld && code.operand is FieldInfo flag && flag.DeclaringType == typeof(PersistentEngine) && flag.Name == "HasPersistentThrust")
                { code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(PersistentThrustSafety), nameof(AllowControlWrites)); changed++; }
                yield return code;
            }
            if (changed != 1) throw new InvalidOperationException("Unreviewed persistent control pipeline.");
        }
        public static IEnumerable<CodeInstruction> AvoidRepeatedRateEvent(IEnumerable<CodeInstruction> source)
        {
            int changed = 0;
            foreach (var code in source)
            {
                if (code.operand is MethodInfo call && call.DeclaringType == typeof(TimeWarp) && call.Name == "SetRate")
                { code.operand = AccessTools.Method(typeof(PersistentThrustSafety), nameof(SetRateIfChanged)); changed++; }
                yield return code;
            }
            if (changed != 1) throw new InvalidOperationException("Unreviewed persistent throttle-rate pipeline.");
        }
        public static void SetRateIfChanged(int index, bool instant, bool physics)
        { if (TimeWarp.CurrentRateIndex != index) TimeWarp.SetRate(index, instant, physics); }
        public static Vector3d SafeDeltaV(double density, double mass, double dt, double thrust, float isp, Vector3d direction, out double demand)
        {
            demand = 0;
            var engine = executing;
            if (ReferenceEquals(engine, null) || !AllowPackedBurn(engine) || !Finite(direction) || direction.sqrMagnitude < 1e-12 ||
                !PersistentBurnPolicy.Finite(density) || density < 0 || !PersistentBurnPolicy.Finite(thrust) || thrust < 0) return Vector3d.zero;
            var vessel = engine.vessel; var orbit = vessel.orbit;
            if (orbit == null || ReferenceEquals(orbit.referenceBody, null) || !PersistentBurnPolicy.Finite(mass) || mass <= 0 ||
                !PersistentBurnPolicy.Finite(dt) || dt <= 0 || !PersistentBurnPolicy.Finite(isp) || isp <= 0)
            { StopUnsafeStep(engine, "Invalid persistent-burn input"); return Vector3d.zero; }
            double requested;
            var vector = Utils.CalculateDeltaVVector(density, mass, dt, thrust, isp, direction, out requested);
            double now = Planetarium.GetUniversalTime(), speed = orbit.getOrbitalVelocityAtUT(now).magnitude;
            if (!Finite(vector) || !PersistentBurnPolicy.Finite(requested) || requested < 0 ||
                dt > MaximumStep(orbit, now) || !PersistentBurnPolicy.ValidStep(mass, dt, thrust, isp, speed, vector.magnitude) || !SafeTrajectory(orbit, vector, now, dt))
            { StopUnsafeStep(engine, "Warp reduced before an unsafe persistent-burn step"); return Vector3d.zero; }
            demand = requested; return vector;
        }
        public static bool SafeTrajectory(Orbit orbit, Vector3d delta, double now, double dt)
        {
            if (orbit == null || ReferenceEquals(orbit.referenceBody, null) || !Finite(delta) || !PersistentBurnPolicy.Finite(now) || !PersistentBurnPolicy.Finite(dt) || dt <= 0) return false;
            if (dt > MaximumStep(orbit, now)) return false;
            var position = orbit.getRelativePositionAtUT(now); var velocity = orbit.getOrbitalVelocityAtUT(now) + delta.xzy;
            if (!Finite(position) || !Finite(velocity) || position.sqrMagnitude <= 0) return false;
            var next = new Orbit(); next.UpdateFromStateVectors(position, velocity, orbit.referenceBody, now); next.Init(); next.UpdateFromUT(now);
            double floor = orbit.referenceBody.Radius + Math.Max(0, orbit.referenceBody.atmosphereDepth) + 1000;
            // Bound the proposed trajectory before any resource request. Do not
            // rewrite the live orbit, restore progress, or suppress stock crashes.
            for (int i = 0; i <= 8; i++)
            {
                var sample = next.getRelativePositionAtUT(now + dt * i / 8);
                if (!Finite(sample) || sample.magnitude <= floor) return false;
            }
            return true;
        }
        public static double MaximumStep(Orbit orbit, double now)
        {
            if (orbit == null || ReferenceEquals(orbit.referenceBody, null)) return 0;
            double radius = orbit.getRelativePositionAtUT(now).magnitude, mu = orbit.referenceBody.gravParameter;
            if (!PersistentBurnPolicy.Finite(radius) || radius <= 0 || !PersistentBurnPolicy.Finite(mu) || mu <= 0) return 0;
            // A local dynamical timescale also bounds hyperbolic trajectories,
            // whose formal orbital period is not finite. Eight lookahead samples
            // now cover at most 1/32 of a local orbit, not arbitrary many periods.
            return 2 * Math.PI * Math.Sqrt(radius * radius * radius / mu) / 32;
        }
        public static void SafePerturb(Orbit orbit, Vector3d delta, double now)
        {
            if (ReferenceEquals(executing, null) || !AllowPackedBurn(executing) || !Finite(delta) || !ReferenceEquals(orbit, executing.vessel.orbit)) return;
            OrbitExtensions.Perturb(orbit, delta, now);
        }
        static void StopUnsafeStep(PersistentEngine engine, string reason)
        { ClearCache(engine); States.GetValue(engine, _ => new State()).Intent.Invalidate(); SetRateIfChanged(0, true, false); Notice(reason + "; coast or use a lower warp rate."); }
        static void Notice(string text)
        {
            if (Now < nextNotice) return; nextNotice = Now + 10;
            Debug.LogWarning("[Aetherfront Persistent Thrust] " + text);
            if (HighLogic.LoadedSceneIsFlight) ScreenMessages.PostScreenMessage(text, 5, ScreenMessageStyle.UPPER_CENTER);
        }
        public static void LimitWarpRate(ref int __0, ref bool __1, bool __2)
        {
            if (__0 <= 0 || __2 || Protected || ReferenceEquals(PilotVessel(), null) || ReferenceEquals(TimeWarp.fetch, null)) return;
            var vessel = PilotVessel();
            if (!Owns(vessel) || vessel.parts == null || vessel.orbit == null) return;
            double force = 0; var engines = vessel.parts.SelectMany(p => p.Modules.OfType<PersistentEngine>());
            foreach (var engine in engines)
            {
                var state = Get(engine);
                if (!engine.isEnabled || !engine.isPersistentEngine || !engine.HasPersistentThrust || !state.Intent.Armed || !HeadingAvailable(vessel)) continue;
                foreach (var module in engine.isMultiMode ? new[] { engine.currentEngine } : engine.moduleEngines ?? new PersistentEngineModule[0])
                    if (module != null && !ReferenceEquals(module.engine, null) && module.engine.getIgnitionState)
                        force += module.engine.maxThrust * module.engine.thrustPercentage * 0.01 * state.Intent.Demand;
            }
            double mass = vessel.GetTotalMass();
            if (force <= 0 || mass <= 0 || !PersistentBurnPolicy.Finite(force)) return;
            double budget = PersistentBurnPolicy.DeltaVBudget(vessel.orbit.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).magnitude);
            double maxRate = Math.Min(budget * mass / force, MaximumStep(vessel.orbit, Planetarium.GetUniversalTime())) / Math.Max(0.02, Time.fixedDeltaTime);
            var rates = TimeWarp.fetch.warpRates;
            int allowed = Math.Min(__0, rates.Length - 1);
            while (allowed > 0 && rates[allowed] > maxRate) allowed--;
            if (allowed != __0) { __0 = allowed; __1 = true; Notice("Persistent-burn warp limited to keep orbital steps bounded."); }
        }
        static void InvalidateVessel(Vessel vessel)
        {
            if (ReferenceEquals(vessel, null) || vessel.parts == null || !Owns(vessel)) return;
            foreach (var engine in vessel.parts.SelectMany(p => p.Modules.OfType<PersistentEngine>()))
            { var state = States.GetValue(engine, _ => new State()); state.Intent.Invalidate(); if (state.Ticket != null) { Tickets.TryRemove(state.Ticket, out _); state.Ticket = null; } ClearCache(engine); }
        }
        public static void BeforeCouple(Part __instance, Part __0)
        {
            if (ReferenceEquals(__instance, null) || ReferenceEquals(__0, null)) return;
            FlightSafety.CaptureTopologyRecovery("pre-docking");
            InvalidateVessel(__instance.vessel); InvalidateVessel(__0.vessel);
        }
        public static void AfterCouple(Part __instance)
        { if (!ReferenceEquals(__instance, null)) InvalidateVessel(__instance.vessel); FlightSafety.RequestTopologyRecovery(); }
        public static void BeforeSplit(Part __instance)
        { if (!ReferenceEquals(__instance, null)) { FlightSafety.CaptureTopologyRecovery("pre-separation"); InvalidateVessel(__instance.vessel); } }
        public static void AfterSplit(Part __instance)
        { if (!ReferenceEquals(__instance, null)) InvalidateVessel(__instance.vessel); FlightSafety.RequestTopologyRecovery(); }
    }
}
