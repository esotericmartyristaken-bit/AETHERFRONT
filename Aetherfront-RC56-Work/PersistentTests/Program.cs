using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Reflection.Emit;
using Aetherfront.Stability.Client;
using Aetherfront.Synchronization;
using HarmonyLib;
using PersistentThrust;
using UnityEngine;

internal static class Program
{
    static string game, fixture;
    static int checks, rate, rateChanges;
    static Vessel active;
    static float pilotThrottle;
    static bool multiplayer, physicsHeld;
    static void Main()
    {
        game = Environment.GetEnvironmentVariable("RC56_KSP_REFERENCE");
        fixture = Environment.GetEnvironmentVariable("RC56_MONO_ROOT");
        if (game != @"C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke" ||
            !fixture.StartsWith(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Isolated fixture required.");
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Run();
        Console.WriteLine("PASS " + checks + " persistent-thrust policy, actual-binary patch, save and trajectory checks. No rendered flight or production writes.");
    }
    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string root in new[] { fixture, Path.Combine(game, "KSP_x64_Data/Managed"), Path.Combine(game, "GameData/PersistentThrust/Plugin"), Path.Combine(game, "GameData/000_Harmony"), Path.Combine(game, "GameData/LunaMultiplayer/Plugins"), Path.Combine(game, "GameData/UmbraSpaceIndustries/LifeSupport"), Path.Combine(game, "GameData/UmbraSpaceIndustries/USITools") })
        { string path = Path.Combine(root, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
        return null;
    }
    static void Check(bool value, string text) { checks++; if (!value) throw new Exception("FAILED: " + text); Console.WriteLine("PASS: " + text); }
    static T Blank<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    static void Set(object instance, Type type, string name, object value) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).SetValue(instance, value);
    static bool Rate(ref int __result) { __result = rate; return false; }
    static bool Mode(ref TimeWarp.Modes __result) { __result = TimeWarp.Modes.HIGH; return false; }
    static bool Active(ref Vessel __result) { __result = active; return false; }
    static Vessel FixturePilot() => active;
    static IEnumerable<CodeInstruction> PilotBoundary(IEnumerable<CodeInstruction> ignored)
    { yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Program), nameof(FixturePilot))); yield return new CodeInstruction(OpCodes.Ret); }
    static bool Offline(ref bool __result) { __result = multiplayer; return false; }
    static bool Protected(ref bool __result) { __result = physicsHeld; return false; }
    static bool PilotInput(ref float __result) { __result = pilotThrottle; return false; }
    static bool UniversalTime(ref double __result) { __result = 1000; return false; }
    static bool Generation(ref int __result) { __result = 1; return false; }
    static bool SetRate(int __0) { rate = __0; rateChanges++; return false; }
    static bool Mass(ref float __result) { __result = 50; return false; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        Debug.unityLogger.logEnabled = false;
        Assembly.LoadFrom(Path.Combine(fixture, "AetherfrontContinuityClient.dll"));
        string expected = Environment.GetEnvironmentVariable("RC61_CLIENT_VERSION");
        if (!string.IsNullOrEmpty(expected))
        {
            var settings = typeof(FlightSafety).Assembly.GetReferencedAssemblies().Single(a => a.Name == "LmpClient");
            var client = Assembly.Load(settings);
            Check((string)client.GetType("LmpClient.Apf.ApfClientSettings").GetProperty("ClientVersion").GetValue(null, null) == expected, "Combined client runtime advertises the intended release version");
            Check((string)client.GetType("LmpClient.Apf.ApfClientBuildVersion").GetField("Value").GetRawConstantValue() == expected, "Compiled client version constant matches its runtime getter");
        }
        PolicyTests();
        var test = new Harmony("aetherfront.rc61.isolated-native-fixture");
        test.Patch(AccessTools.PropertyGetter(typeof(TimeWarp), "CurrentRateIndex"), prefix: new HarmonyMethod(typeof(Program), nameof(Rate)));
        test.Patch(AccessTools.PropertyGetter(typeof(TimeWarp), "WarpMode"), prefix: new HarmonyMethod(typeof(Program), nameof(Mode)));
        test.Patch(AccessTools.Method(typeof(PersistentThrustSafety), "PilotVessel"), transpiler: new HarmonyMethod(typeof(Program), nameof(PilotBoundary)));
        test.Patch(AccessTools.PropertyGetter(typeof(PersistentThrustSafety), "Multiplayer"), prefix: new HarmonyMethod(typeof(Program), nameof(Offline)));
        test.Patch(AccessTools.PropertyGetter(typeof(PersistentThrustSafety), "Protected"), prefix: new HarmonyMethod(typeof(Program), nameof(Protected)));
        test.Patch(AccessTools.Method(typeof(PersistentThrustSafety), "PilotThrottle"), prefix: new HarmonyMethod(typeof(Program), nameof(PilotInput)));
        test.Patch(AccessTools.Method(typeof(Planetarium), "GetUniversalTime"), prefix: new HarmonyMethod(typeof(Program), nameof(UniversalTime)));
        test.Patch(AccessTools.PropertyGetter(typeof(FlightSafety), "ProtectionGeneration"), prefix: new HarmonyMethod(typeof(Program), nameof(Generation)));
        test.Patch(AccessTools.Method(typeof(TimeWarp), "SetRate", new[] { typeof(int), typeof(bool), typeof(bool) }), prefix: new HarmonyMethod(typeof(Program), nameof(SetRate)) { priority = Priority.First });
        test.Patch(AccessTools.Method(typeof(Vessel), "GetTotalMass"), prefix: new HarmonyMethod(typeof(Program), nameof(Mass)));
        var harmony = new Harmony("aetherfront.rc61.isolated-persistent-fix");
        PersistentThrustSafety.Install(harmony);
        foreach (string name in new[] { "OnSave", "FixedUpdate", "OnUpdate", "BackgroundUpdate", "SetThrottle", "UpdatePersistentThrottle" })
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(PersistentEngine), name)).Owners.Contains(harmony.Id), "Actual mod method patched: " + name);
        HighLogic.LoadedSceneIsFlight = false;
        Blank<PersistentEngine>().OnSave(new ConfigNode("MODULE"));
        Check(true, "Unattached editor/prefab module save does not dereference a missing part");
        HighLogic.LoadedSceneIsFlight = true;
        // A rendered KSP scene initializes this orthonormal frame. The embedded
        // Mono fixture must do so explicitly before exercising real Orbit math.
        Planetarium.Zup = new Planetarium.CelestialFrame { X = Vector3d.right, Y = Vector3d.up, Z = Vector3d.forward };
        var vessel = Blank<Vessel>(); active = vessel;
        Set(vessel, typeof(Vessel), "id", Guid.NewGuid());
        Set(vessel, typeof(Vessel), "parts", new List<Part>());
        var autopilot = Blank<VesselAutopilot>(); Set(vessel, typeof(Vessel), "autopilot", autopilot);
        var driver = Blank<OrbitDriver>(); Set(vessel, typeof(Vessel), "orbitDriver", driver);
        var body = Blank<CelestialBody>(); body.Radius = 600000; body.gravParameter = 3.5316e12; body.atmosphereDepth = 70000;
        // Orbit's constructor tests Unity object's cached pointer before using
        // body parameters. This non-dereferenced sentinel enables pure managed
        // orbital math; no native Unity API is invoked by these tests.
        Set(body, typeof(UnityEngine.Object), "m_CachedPtr", new IntPtr(1));
        var orbit = new Orbit(0, 0, 700000, 0, 0, 0, 1000, body); orbit.Init(); orbit.UpdateFromUT(1000); driver.orbit = orbit;
        var part = Blank<Part>(); Set(part, typeof(Part), "vessel", vessel);
        var engine = Blank<PersistentEngine>(); Set(engine, typeof(PartModule), "_part", part);
        Set(engine, typeof(PersistentEngine), "_kerbalismResourceChangeRequest", new Dictionary<string, double>());
        Set(engine, typeof(PersistentEngine), "_mainThrottleQueue", new Queue<float>(new[] { 1f, 0.5f }));
        engine.HasPersistentThrust = true; engine.isEnabled = true;
        foreach (var mode in new[] { VesselAutopilot.AutopilotMode.Target, VesselAutopilot.AutopilotMode.AntiTarget })
        {
            Set(autopilot, typeof(VesselAutopilot), "mode", mode);
            var node = new ConfigNode("MODULE"); node.AddValue("unrelatedScience", "750"); node.AddValue("persistentThrottle", "1");
            engine.OnSave(node);
            Check(node.GetValue("unrelatedScience") == "750", "Patched actual save preserves unrelated fields for " + mode);
            Check(node.GetValue("persistentThrottle") == "0" && node.GetValue("persistentAutopilotMode") == "StabilityAssist", "Missing target persists safe metadata without mutating live SAS");
            Check(vessel.Autopilot.Mode == mode, "Saving does not change live pilot SAS mode");
            Check(!PersistentThrustSafety.HeadingAvailable(vessel), "Missing target is not burn-capable");
            Check(PersistentThrust.VesselExtension.GetRequestedDirection(vessel, 1000) == Vector3d.zero, "Patched real direction lookup coasts without target exception");
        }
        Set(autopilot, typeof(VesselAutopilot), "mode", VesselAutopilot.AutopilotMode.StabilityAssist);
        Check(PersistentThrustSafety.HeadingAvailable(vessel), "Ordinary attitude hold remains valid");
        Set(vessel, typeof(Vessel), "ctrlState", Blank<FlightCtrlState>());
        engine.persistentThrottle = 1;
        PersistentThrustSafety.RefreshThrottle(engine);
        Check(engine.persistentThrottle == 0 && ((Queue<float>)AccessTools.Field(typeof(PersistentEngine), "_mainThrottleQueue").GetValue(engine)).Count == 0, "Explicit cutoff clears positive rolling maximum immediately");
        vessel.ctrlState.mainThrottle = 0.2f; PersistentThrustSafety.RefreshThrottle(engine);
        Check(engine.persistentThrottle == 0.2f, "Lower positive throttle is not replaced by older queue maximum");
        PersistentThrustSafety.SetRateIfChanged(0, true, false);
        Check(rateChanges == 0, "Repeated 1x request does not emit another warp event");
        rate = 2; PersistentThrustSafety.SetRateIfChanged(0, true, false);
        Check(rateChanges == 1 && rate == 0, "Real warp stop still reaches native rate setter");
        Console.WriteLine("ORBIT INPUT radius=" + orbit.getRelativePositionAtUT(1000).magnitude + " speed=" + orbit.getOrbitalVelocityAtUT(1000).magnitude + " mu=" + body.gravParameter + " floor=" + (body.Radius + body.atmosphereDepth + 1000));
        var planned = new Orbit(); planned.UpdateFromStateVectors(orbit.getRelativePositionAtUT(1000), orbit.getOrbitalVelocityAtUT(1000), body, 1000); planned.Init(); planned.UpdateFromUT(1000);
        Console.WriteLine("ORBIT PLAN radius=" + planned.getRelativePositionAtUT(1000).magnitude + " future=" + planned.getRelativePositionAtUT(1020).magnitude + " ecc=" + planned.eccentricity + " sma=" + planned.semiMajorAxis);
        Check(PersistentThrustSafety.SafeTrajectory(orbit, new Vector3d(0, 0, 0), 1000, 20), "Actual KSP orbit supports finite coasting trajectory");
        Check(!PersistentThrustSafety.SafeTrajectory(orbit, new Vector3d(double.NaN, 0, 0), 1000, 20), "Invalid orbit proposal rejected before resource requests");
        Check(!PersistentThrustSafety.SafeTrajectory(orbit, Vector3d.zero, 1000, double.NaN), "Invalid step duration rejected");
        Check(!PersistentThrustSafety.SafeTrajectory(orbit, Vector3d.zero, 1000, 100000), "High-warp sample interval cannot alias multiple complete low-orbit revolutions");
        PoweredTests(engine, vessel, part);
        foreach (var method in typeof(PersistentThrustSafety).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            if (method.GetMethodBody() != null && !method.ContainsGenericParameters) RuntimeHelpers.PrepareMethod(method.MethodHandle);
        Check(true, "All compatibility methods prepared under current runtime");
    }
    static void PoweredTests(PersistentEngine engine, Vessel vessel, Part part)
    {
        var native = Blank<ModuleEngines>(); Set(native, typeof(PartModule), "_part", part);
        native.EngineIgnited = true; native.isEnabled = true; native.maxThrust = 1; native.thrustPercentage = 100;
        var module = Blank<PersistentEngineModule>(); module.engine = native; module.persistentIsp = 300; module.averageDensity = 0.005;
        engine.currentEngine = module; engine.moduleEngines = new[] { module }; engine.isPersistentEngine = true;
        PersistentThrustSafety.Scope scope;
        pilotThrottle = 0; vessel.packed = false;
        Check(PersistentThrustSafety.BeforePhysics(engine, out scope), "Valid unpacked native engine remains available"); PersistentThrustSafety.AfterPhysics(null, scope);
        pilotThrottle = 0.6f;
        Check(PersistentThrustSafety.BeforePhysics(engine, out scope), "Fresh pilot input reaches unpacked engine path"); PersistentThrustSafety.AfterPhysics(null, scope);
        vessel.packed = true; rate = 2;
        Check(PersistentThrustSafety.AllowPackedBurn(engine), "Actual engine, ignition and live intent authorize powered warp");
        native.EngineIgnited = false;
        Check(!PersistentThrustSafety.AllowPackedBurn(engine), "Actual shut-down native engine cannot burn stale cached throttle"); native.EngineIgnited = true;
        rate = 0;
        Check(!PersistentThrustSafety.BeforePhysics(engine, out scope), "Actual compatibility skips packed physics during 1x clock correction"); PersistentThrustSafety.AfterPhysics(null, scope);
        rate = 2; physicsHeld = true;
        Check(!PersistentThrustSafety.BeforePhysics(engine, out scope), "Actual compatibility respects synchronization hold"); PersistentThrustSafety.AfterPhysics(null, scope); physicsHeld = false;
        Check(PersistentThrustSafety.BeforePhysics(engine, out scope), "Legitimate burn resumes after same-vessel hold");
        double demand;
        var delta = PersistentThrustSafety.SafeDeltaV(0.005, 50, 1, 1, 300, Vector3d.up, out demand);
        Check(delta.magnitude > 0 && demand > 0 && delta.magnitude < 1, "Original fuel/delta-V calculation retained for a valid bounded burn");
        PersistentThrustSafety.AfterPhysics(null, scope);
        var saved = new ConfigNode("MODULE"); engine.OnSave(saved);
        Check(!string.IsNullOrEmpty(saved.GetValue("apfBurnTicket")), "Armed current-session background ticket captured without pilot-control mutation");
        var snapshot = Blank<ProtoPartSnapshot>(); snapshot.persistentId = part.persistentId;
        var nativeSnapshot = Blank<ProtoPartModuleSnapshot>(); nativeSnapshot.moduleName = "ModuleEngines"; nativeSnapshot.moduleValues = new ConfigNode("MODULE"); nativeSnapshot.moduleValues.AddValue("EngineIgnited", "True");
        snapshot.modules = new List<ProtoPartModuleSnapshot> { nativeSnapshot };
        var persistentSnapshot = Blank<ProtoPartModuleSnapshot>(); persistentSnapshot.moduleName = "PersistentEngine"; persistentSnapshot.moduleValues = saved;
        var changes = new List<KeyValuePair<string, double>>(); string result = null;
        multiplayer = true;
        Set(null, typeof(PersistentThrustSafety), "authority", (Func<Vessel, bool>)(_ => true));
        PersistentThrustSafety.BackgroundScope backgroundScope;
        Check(PersistentThrustSafety.BackgroundReady(vessel, snapshot, persistentSnapshot, part, changes, 1, ref result, out backgroundScope), "Intentional locally-owned background burn remains supported");
        changes.Add(new KeyValuePair<string, double>("LiquidFuel", -1));
        PersistentThrustSafety.SafeBackgroundPerturb(vessel.orbit, new Vector3d(double.NaN, 0, 0), 1000);
        PersistentThrustSafety.AfterBackground(null, backgroundScope);
        Check(changes.Count == 0, "Rejected background orbit proposal rolls back only its resource-change proposals");
        Check(PersistentThrustSafety.BackgroundReady(vessel, snapshot, persistentSnapshot, part, changes, 1, ref result, out backgroundScope), "Valid background ticket survives rejected numerical proposal");
        changes.Add(new KeyValuePair<string, double>("LiquidFuel", -1));
        PersistentThrustSafety.SafeBackgroundPerturb(vessel.orbit, new Vector3d(0.001, 0, 0), 1000);
        PersistentThrustSafety.AfterBackground(null, backgroundScope);
        Check(changes.Count == 1, "Successful bounded background orbit update retains its fuel proposal");
        saved.SetValue("apfBurnTicket", Guid.NewGuid().ToString("N"));
        Check(!PersistentThrustSafety.BackgroundReady(vessel, snapshot, persistentSnapshot, part, changes, 1, ref result, out backgroundScope), "Forged or prior-process burn ticket cannot reactivate saved thrust");
        multiplayer = false; pilotThrottle = 0; vessel.packed = false;
        PersistentThrustSafety.BeforePhysics(engine, out scope); PersistentThrustSafety.AfterPhysics(null, scope);
        Check(engine.persistentThrottle == 0, "Actual pilot cutoff disarms cached engine state after warp");
    }
    static void PolicyTests()
    {
        var policy = new PersistentBurnPolicy();
        policy.Observe(1, 1, false, true, 1);
        Check(!policy.Armed, "Stored positive throttle cannot arm a newly loaded vessel");
        policy.Observe(1, 1, false, true, 0); policy.Observe(1, 1, false, true, 0.7f);
        Check(policy.Armed && policy.Demand == 0.7f, "Fresh cutoff then pilot command arms deliberate burn");
        Check(policy.Allows(true, false, true, true, true), "Intentional powered on-rails warp remains available");
        Check(!policy.Allows(true, false, false, true, true), "Synchronization-only 1x packing cannot authorize thrust");
        Check(!policy.Allows(true, true, true, true, true), "Synchronization hold suppresses packed simulation");
        policy.Observe(1, 1, true, true, 0);
        Check(policy.Armed && policy.Demand == 0.7f, "Lease-owned temporary zero preserves the pilot's prior intent");
        Check(!policy.Allows(false, false, true, true, true), "Remote pilot's vessel cannot be simulated");
        Check(!policy.Allows(true, false, true, false, true), "Shutdown engine cannot use cached throttle");
        Check(!policy.Allows(true, false, true, true, false), "Missing heading target cannot authorize thrust");
        policy.Observe(2, 1, false, true, 0.7f);
        Check(!policy.Armed, "Docking/part topology change invalidates previous burn");
        policy.Observe(2, 1, false, true, 0); policy.Observe(2, 1, false, true, 0.3f);
        Check(policy.Armed, "Fresh command rearms valid merged vessel");
        policy.Observe(2, 2, false, true, 0.3f);
        Check(!policy.Armed, "Reconnect/session generation rejects stale burn authorization");
        policy.Observe(2, 2, false, true, 0); policy.Observe(2, 2, false, true, 0.3f); policy.Observe(2, 2, false, true, float.NaN);
        Check(!policy.Armed, "Invalid command fails closed");
        Check(PersistentBurnPolicy.ValidStep(50, 1, 1, 300, 2200, 0.2), "Bounded legitimate low-thrust integration accepted");
        Check(!PersistentBurnPolicy.ValidStep(50, 400, 100, 300, 2200, 800), "Large high-warp impulse rejected");
        Check(!PersistentBurnPolicy.ValidStep(0, 1, 1, 300, 2200, 0.2), "Invalid vessel mass rejected");
        Check(!PersistentBurnPolicy.ValidStep(50, double.PositiveInfinity, 1, 300, 2200, 0.2), "Non-finite integration duration rejected");
        Check(!PersistentBurnPolicy.ValidStep(50, 1, 1, 0, 2200, 0.2), "Invalid specific impulse rejected");
    }
}
