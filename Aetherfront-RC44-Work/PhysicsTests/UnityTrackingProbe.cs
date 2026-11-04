using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

// Runs only in the private, minimal GameData player prepared by Run.ps1.
// LmpClient is loaded from a .bin AFTER addon discovery, never as an addon.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class UnityTrackingProbe : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance |
        BindingFlags.Public | BindingFlags.NonPublic;
    private const string ShippingHash = "e90c85067eefc8baf99589fd8625df22d373e0abcb37f4374e31a0ce14aa1b31";
    private readonly List<string> evidence = new List<string>();
    private readonly List<GameObject> fixtures = new List<GameObject>();
    private readonly Dictionary<string, Assembly> loadedInputs = new Dictionary<string, Assembly>(StringComparer.Ordinal);
    private Assembly client;
    private MethodInfo shippingTrack;
    private MethodInfo productionGuard, productionPolicy;
    private PrefixGuardProbe.Callback originalPrefix, guardedPrefix;
    private Type mainSystem, settingType, vesselCommon, lockDefinitionType, lockType;
    private object settings, controlLocks;
    private FlightGlobals globals;
    private GameScenes savedScene;
    private bool orbitGrafted;
    private Type queueSystem, queueType;
    private object queues;
    private string root;
    private string output;
    private int checks, cases;
    private bool groundPassed, trackingPassed;

    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("AF_PHYSICS_RUN_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        root = Path.GetFullPath(root);
        output = Path.GetFullPath(Environment.GetEnvironmentVariable("AF_PHYSICS_EVIDENCE"));
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        string game = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
        if (!game.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        DontDestroyOnLoad(gameObject);
        StartCoroutine(Run());
    }
    private IEnumerator Run()
    {
        yield return null;
        bool passed = false;
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Say("RUNTIME Unity=" + Application.unityVersion + " nativePhysics=true");
            GroundClearanceProbe.Bind(LoadInput("AetherfrontContinuityClient"), Need);
            Say("GROUND graftedAssemblySha256=" + Hash(Path.Combine(root, "inputs", "AetherfrontContinuityClient.bin")));
            GroundClearanceProbe.Run(Need, Say);
            cases += 3;
            groundPassed = true;
            File.WriteAllText(Path.Combine(output, "ground-result.json"), "{\"passed\":true,\"nativeUnity\":true,\"geometries\":3,\"unmodifiedStockLowestPoint\":true,\"testOnlySingleCallReplacement\":true,\"fullUnpack\":false}");
            string path = Path.Combine(root, "inputs", "LmpClient.bin");
            string expectedClient = Environment.GetEnvironmentVariable("AF_PHYSICS_CLIENT_SHA") ?? ShippingHash;
            Need(Hash(path) == expectedClient, "Exact selected LmpClient hash");
            client = LoadInput("LmpClient");
            shippingTrack = client.GetType("LmpClient.Harmony.OrbitDriver_TrackRigidbody", true)
                .GetMethod("TrackRigidbody", All);
            Need(shippingTrack != null, "Unmodified shipping TrackRigidbody resolved");
            PrepareQueue();
            PrepareClock();
            PrepareProductionGuard();
            Say("RUNTIME Unity=" + Application.unityVersion + " nativeRigidbody=true shippingMethodUnmodified=true");
            for (int craft = 0; craft < 3; craft++) RunCraft(craft);
            PredicateMatrix();
            trackingPassed = true;
            passed = true;
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Say("FAIL " + error);
        }
        finally { if (globals != null) HighLogic.LoadedScene = savedScene; }
        Say("RESULT passed=" + passed + " cases=" + cases + " checks=" + checks +
            " nativeUnity=true flightScene=false naturalUnpackReproduction=false fullModpack=false");
        File.WriteAllLines(Path.Combine(output, "unity-results.txt"), evidence.ToArray());
        File.WriteAllText(Path.Combine(output, "unity-result.json"),
            "{\"passed\":" + (passed ? "true" : "false") + ",\"cases\":" + cases + ",\"checks\":" + checks +
            ",\"groundPassed\":" + (groundPassed ? "true" : "false") + ",\"trackingPassed\":" + (trackingPassed ? "true" : "false") +
            ",\"nativeUnity\":true,\"shippingMethodUnmodified\":true,\"flightScene\":false," +
            "\"naturalUnpackReproduction\":false,\"fullModpack\":false,\"injectedStaleVelocity\":true}");
        Application.Quit(passed ? 0 : 1);
    }
    private Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        return File.Exists(Path.Combine(root, "inputs", name + ".bin")) ? LoadInput(name) : null;
    }
    private Assembly LoadInput(string name)
    {
        Assembly assembly;
        if (loadedInputs.TryGetValue(name, out assembly)) return assembly;
        assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(root, "inputs", name + ".bin")));
        loadedInputs.Add(name, assembly); return assembly;
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private void Need(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException(message);
    }
    private void Say(string value) { evidence.Add(value); Debug.Log("[PhysicsTests] " + value); }
    private GameObject Inactive(string name)
    {
        var go = new GameObject("PhysicsTests-" + name);
        go.SetActive(false); fixtures.Add(go); return go;
    }
    private void PrepareClock()
    {
        Planetarium clock = Inactive("Clock").AddComponent<Planetarium>();
        typeof(Planetarium).GetField("fetch", All).SetValue(null, clock);
        typeof(Planetarium).GetField("time", All).SetValue(clock, 12345.0);
        FieldInfo frameField = typeof(Planetarium).GetField("Zup", All);
        object frame = Activator.CreateInstance(frameField.FieldType);
        frameField.FieldType.GetField("X", All).SetValue(frame, new Vector3d(1, 0, 0));
        frameField.FieldType.GetField("Y", All).SetValue(frame, new Vector3d(0, 1, 0));
        frameField.FieldType.GetField("Z", All).SetValue(frame, new Vector3d(0, 0, 1));
        frameField.SetValue(null, frame);
        Need(Math.Abs(Planetarium.GetUniversalTime() - 12345) < 1e-12, "Actual clock fixture");
    }
    private void PrepareQueue()
    {
        queueSystem = client.GetType("LmpClient.Systems.VesselPositionSys.VesselPositionSystem", true);
        queueType = client.GetType("LmpClient.Systems.VesselPositionSys.PositionUpdateQueue", true);
        FieldInfo networkEvent = client.GetType("LmpClient.Events.NetworkEvent", true).GetField("onNetworkStatusChanged", All);
        networkEvent.SetValue(null, Activator.CreateInstance(networkEvent.FieldType, new object[] { "PhysicsTests-onNetworkStatusChanged" }));
        queues = queueSystem.GetProperty("TargetVesselUpdateQueue", All).GetValue(null, null);
        // The real singleton constructor installs event handlers but does not enable
        // the system. No peer, socket, NetworkMain or MainSystem is started.
        Type baseSystem = client.GetType("LmpClient.Base.System`1", true).MakeGenericType(queueSystem);
        object singleton = baseSystem.GetProperty("Singleton", All).GetValue(null, null);
        Need(singleton != null, "Real disabled queue singleton available");
    }
    private object SetQueue(Guid vesselId, bool queued)
    {
        object queue = Activator.CreateInstance(queueType);
        if (queued)
        {
            Type messageType = client.GetType("LmpCommon.Message.Data.Vessel.VesselPositionMsgData", true);
            object message = Activator.CreateInstance(messageType, true);
            messageType.GetField("VesselId", All).SetValue(message, vesselId);
            queueType.GetMethod("Enqueue", All).Invoke(queue, new[] { message });
            Need(Count(queue) == 1, "Actual shipping position-message enqueue retained one entry");
        }
        queues.GetType().GetProperty("Item").SetValue(queues, queue, new object[] { vesselId });
        return queue;
    }
    private static int Count(object queue) { return (int)queue.GetType().GetProperty("Count", All).GetValue(queue, null); }

    private void PrepareProductionGuard()
    {
        string helperPath = Path.Combine(root, "inputs", "AetherfrontLocalOrbitTracking.bin");
        Type policy = client.GetType("Aetherfront.Continuity.Client.LocalOrbitTracking", false);
        orbitGrafted = policy != null;
        if (policy == null) policy = LoadInput("AetherfrontLocalOrbitTracking").GetType("Aetherfront.Continuity.Client.LocalOrbitTracking", true);
        productionGuard = policy.GetMethod("UseStockKinematicTracking", All);
        productionPolicy = policy.GetMethod("IsEligible", All);
        MethodInfo prefix = client.GetType("LmpClient.Harmony.OrbitDriver_TrackRigidbody", true).GetMethod("PrefixTrackRigidbody", All);
        PrefixGuardProbe.Callback actualPrefix = (PrefixGuardProbe.Callback)prefix.CreateDelegate(typeof(PrefixGuardProbe.Callback));
        originalPrefix = orbitGrafted ? null : actualPrefix;
        guardedPrefix = orbitGrafted ? actualPrefix : PrefixGuardProbe.Insert(prefix, productionGuard, Need);
        mainSystem = client.GetType("LmpClient.MainSystem", true);
        mainSystem.GetField("<KspPath>k__BackingField", All).SetValue(null, KSPUtil.ApplicationRootPath.TrimEnd('\\', '/'));
        Type settingsSystem = client.GetType("LmpClient.Systems.SettingsSys.SettingsSystem", true);
        settings = settingsSystem.GetProperty("CurrentSettings", All).GetValue(null, null);
        settingType = settings.GetType();
        vesselCommon = client.GetType("LmpClient.VesselUtilities.VesselCommon", true);
        Type locks = client.GetType("LmpClient.Systems.Lock.LockSystem", true);
        object store = locks.GetProperty("LockStore", All).GetValue(null, null);
        controlLocks = store.GetType().GetProperty("ControlLocks", All).GetValue(store, null);
        lockDefinitionType = client.GetType("LmpCommon.Locks.LockDefinition", true);
        lockType = client.GetType("LmpCommon.Locks.LockType", true);
        globals = Inactive("FlightGlobals").AddComponent<FlightGlobals>();
        typeof(FlightGlobals).GetField("_fetch", All).SetValue(null, globals);
        savedScene = HighLogic.LoadedScene;
        HighLogic.LoadedScene = GameScenes.FLIGHT; // Static policy context only; no scene is loaded.
        Say("PRODUCTION helperSha256=" + (orbitGrafted ? Hash(Path.Combine(root, "inputs", "LmpClient.bin")) : Hash(helperPath)) +
            " actualGraftedPrefix=" + orbitGrafted + " guardedShippingPrefix=true actualSessionFields=true");
    }
    private void Context(Fixture fixture, string context, Vessel alternateActive)
    {
        FieldInfo network = mainSystem.GetField("_networkState", All);
        string state = context == "not-running" ? "Connected" : context == "disconnected" ? "Disconnected" : "Running";
        network.SetValue(null, Enum.Parse(network.FieldType, state));
        typeof(FlightGlobals).GetField("activeVessel", All).SetValue(globals, alternateActive ?? fixture.Vessel);
        vesselCommon.GetProperty("IsSpectating", All).SetValue(null, context == "spectating", null);
        string player = context == "missing-player" ? "" : context == "case-mismatch" ? "PhysicsTests-LOCAL" : "PhysicsTests-Local";
        settingType.GetProperty("PlayerName", All).SetValue(settings, player, null);
        controlLocks.GetType().GetMethod("Clear").Invoke(controlLocks, null);
        if (context != "no-owner")
        {
            string owner = context == "foreign-lock" ? "PhysicsTests-Other" : "PhysicsTests-Local";
            object definition = Activator.CreateInstance(lockDefinitionType, new object[] { Enum.Parse(lockType, "Control"), owner, fixture.Vessel.id });
            controlLocks.GetType().GetProperty("Item").SetValue(controlLocks, definition, new object[] { fixture.Vessel.id });
        }
    }
    private bool ActualGuard(Fixture fixture)
    {
        return (bool)productionGuard.Invoke(null, new object[] { fixture.Driver });
    }
    private void InvokePrefix(Fixture fixture, bool guarded)
    {
        if (!guarded && orbitGrafted)
        {
            // The candidate keeps the original Track method. Compare that exact
            // retained body, selecting stock only for the original prefix gates.
            int network = Convert.ToInt32(mainSystem.GetField("_networkState", All).GetValue(null));
            Invoke(fixture, network < 2 || fixture.Driver.vessel == null); return;
        }
        double update = 0;
        bool runStock = (guarded ? guardedPrefix : originalPrefix)(fixture.Driver, fixture.Body, -0.02, ref update);
        if (runStock) fixture.Driver.TrackRigidbody(fixture.Body, -0.02);
    }

    private sealed class Fixture
    {
        internal OrbitDriver Driver;
        internal Vessel Vessel;
        internal CelestialBody Body;
        internal object Queue;
        internal Vector3d CorrectSurfaceVelocity;
    }
    private Fixture MakeFixture(int craft, bool kinematic, bool queued, bool stale)
    {
        double longitude = new[] { 0.0, 0.7, -1.2 }[craft];
        double radius = new[] { 610000.0, 640000.0, 690000.0 }[craft];
        double speed = new[] { 2300.0, 2050.0, 1800.0 }[craft];
        var relative = new Vector3d(Math.Cos(longitude) * radius, 1000 * (craft + 1), Math.Sin(longitude) * radius);
        Vector3d east = Vector3d.Cross(new Vector3d(0, 1, 0), relative).normalized;
        Vector3d north = Vector3d.Cross(relative.normalized, east).normalized;
        Vector3d correct = east * speed + relative.normalized * 12 + north * 3;
        CelestialBody body = Inactive("Body-" + craft).AddComponent<CelestialBody>();
        body.gravParameter = 3.5316e12;
        body.Radius = 600000;
        body.rotates = true;
        body.inverseRotation = true;
        body.zUpAngularVelocity = new Vector3d(0, 0, -0.000291);
        typeof(CelestialBody).GetField("_position", All).SetValue(body, new Vector3d(17000, -23000, 31000));
        GameObject vesselObject = Inactive("Vessel-" + craft);
        Vessel vessel = vesselObject.AddComponent<Vessel>();
        OrbitDriver driver = vesselObject.AddComponent<OrbitDriver>();
        Part part = Inactive("RootPart-" + craft).AddComponent<Part>();
        Rigidbody rigidbody = part.gameObject.AddComponent<Rigidbody>();
        rigidbody.useGravity = false;
        rigidbody.isKinematic = kinematic;
        part.rb = rigidbody;
        vessel.rootPart = part;
        vessel.id = Guid.NewGuid();
        vessel.CoMD = body.position + relative;
        vessel.velocityD = stale ? north * speed + relative.normalized * 12 + east * 3 : correct;
        vessel.packed = false; // Critical transition: unpacked flag, native body still kinematic.
        driver.vessel = vessel;
        driver.orbit = new Orbit();
        driver.orbit.referenceBody = body;
        driver.updateMode = OrbitDriver.UpdateMode.TRACK_Phys;
        driver.pos = relative.xzy;
        driver.vel = correct.xzy + driver.orbit.GetRotFrameVelAtPos(body, driver.pos);
        driver.orbit.UpdateFromStateVectors(driver.pos, driver.vel, body, Planetarium.GetUniversalTime());
        return new Fixture { Driver = driver, Vessel = vessel, Body = body,
            Queue = SetQueue(vessel.id, queued), CorrectSurfaceVelocity = correct };
    }
    private void Invoke(Fixture fixture, bool stock)
    {
        if (stock) fixture.Driver.TrackRigidbody(fixture.Body, -0.02);
        else shippingTrack.Invoke(null, new object[] { fixture.Driver, fixture.Body, -0.02, 0.0 });
    }
    private static Vector3d Normal(Orbit orbit)
    {
        return ((Vector3d)typeof(Orbit).GetField("h", All).GetValue(orbit)).normalized;
    }
    private static double Angle(Vector3d first, Vector3d second)
    {
        return Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(first, second)))) * 180 / Math.PI;
    }
    private static double[] Snapshot(Fixture f)
    {
        Orbit o = f.Driver.orbit;
        return new[] { o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN,
            o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch,
            f.Driver.pos.x, f.Driver.pos.y, f.Driver.pos.z,
            f.Driver.vel.x, f.Driver.vel.y, f.Driver.vel.z, f.Driver.lastTrackUT };
    }
    private void Same(double[] first, double[] second, string message)
    {
        Need(first.Length == second.Length, message + " length");
        for (int i = 0; i < first.Length; i++)
            Need(!double.IsNaN(first[i]) && !double.IsNaN(second[i]) &&
                Math.Abs(first[i] - second[i]) <= 1e-9 * Math.Max(1, Math.Abs(first[i])), message + " field " + i);
    }
    private void RunCraft(int craft)
    {
        Fixture broken = MakeFixture(craft, true, false, true);
        Vector3d expected = Normal(broken.Driver.orbit);
        double initialInclination = broken.Driver.orbit.inclination;
        Context(broken, "local-kinematic", null);
        InvokePrefix(broken, false);
        double deviation = Angle(expected, Normal(broken.Driver.orbit));
        Need(deviation > 75 && deviation < 105, "Kinematic stale callback changes orbital plane");
        Fixture stock = MakeFixture(craft, true, false, true);
        Invoke(stock, true);
        Need(Angle(expected, Normal(stock.Driver.orbit)) < 1e-5, "Stock guard retains correct orbital plane");
        Fixture candidate = MakeFixture(craft, true, false, true);
        Context(candidate, "local-kinematic", null);
        bool eligible = ActualGuard(candidate);
        Need(eligible, "Local active owned kinematic callback eligible");
        InvokePrefix(candidate, true);
        Same(Snapshot(stock), Snapshot(candidate), "Candidate equals actual stock callback");
        cases++;
        Say("PASS craft=" + craft + " local-kinematic planeDeviationDegrees=" + deviation.ToString("R", CultureInfo.InvariantCulture) +
            " inclinationBefore=" + initialInclination.ToString("R", CultureInfo.InvariantCulture) +
            " brokenInclination=" + broken.Driver.orbit.inclination.ToString("R", CultureInfo.InvariantCulture) +
            " guardedInclination=" + candidate.Driver.orbit.inclination.ToString("R", CultureInfo.InvariantCulture));
        foreach (string context in new[] { "local-dynamic", "remote-kinematic", "remote-dynamic", "local-queued", "remote-queued", "spectating", "foreign-lock", "no-owner", "missing-player", "case-mismatch", "not-running", "disconnected" })
        {
            bool dynamic = context.EndsWith("dynamic", StringComparison.Ordinal);
            bool queued = context.EndsWith("queued", StringComparison.Ordinal);
            bool remote = context.StartsWith("remote", StringComparison.Ordinal);
            Fixture baseline = MakeFixture(craft, !dynamic, queued, !dynamic);
            Fixture trial = MakeFixture(craft, !dynamic, queued, !dynamic);
            double[] orbitBefore = Snapshot(trial).Take(7).ToArray();
            Context(trial, context, remote ? candidate.Vessel : null);
            bool useStock = ActualGuard(trial);
            Need(!useStock, context + " must preserve shipping eligibility");
            Context(baseline, context, remote ? candidate.Vessel : null); InvokePrefix(baseline, false);
            Context(trial, context, remote ? candidate.Vessel : null); InvokePrefix(trial, true);
            Same(Snapshot(baseline), Snapshot(trial), context + " actual shipping path unchanged");
            Need(Count(baseline.Queue) == (queued ? 1 : 0) && Count(trial.Queue) == (queued ? 1 : 0), context + " queue untouched");
            if (queued) Same(orbitBefore, Snapshot(trial).Take(7).ToArray(), context + " orbital elements untouched");
            cases++; Say("PASS craft=" + craft + " " + context);
        }
    }
    private void PredicateMatrix()
    {
        Fixture f = MakeFixture(0, true, false, true);
        Context(f, "local-kinematic", null);
        for (int mask = 0; mask < 128; mask++)
        {
            bool running = (mask & 1) != 0, active = (mask & 2) != 0, spectating = (mask & 4) != 0;
            bool hasRoot = (mask & 8) != 0, kinematic = (mask & 16) != 0, owns = (mask & 32) != 0, queued = (mask & 64) != 0;
            bool expected = running && active && !spectating && hasRoot && kinematic && owns && !queued;
            Need((bool)productionPolicy.Invoke(null, new object[] { running, active, spectating, hasRoot, kinematic, owns, queued }) == expected,
                "Actual production pure-policy combination " + mask);
        }
        Need(!TrackingEligibility.UseStock(null, f.Vessel, false, true, false), "Null driver excluded");
        Need(!TrackingEligibility.UseStock(f.Driver, null, false, true, false), "No active vessel excluded");
        Part saved = f.Vessel.rootPart; f.Vessel.rootPart = null;
        Need(!ActualGuard(f), "Production helper excludes missing root");
        Need(!TrackingEligibility.UseStock(f.Driver, f.Vessel, false, true, false), "Missing root part excluded");
        f.Vessel.rootPart = saved;
        Rigidbody rb = saved.rb; saved.rb = null;
        Need(!ActualGuard(f), "Production helper excludes missing rigidbody");
        Need(!TrackingEligibility.UseStock(f.Driver, f.Vessel, false, true, false), "Missing rigidbody excluded");
        saved.rb = rb;
        f.Vessel.packed = true;
        Need(ActualGuard(f), "Production helper selects stock for owned packed kinematic fixture");
        Need(TrackingEligibility.UseStock(f.Driver, f.Vessel, false, true, false), "Packed local kinematic retains stock selection");
        cases++; Say("PASS conservative predicate edge cases");
    }
}
