using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetherfront.Continuity;
using Aetherfront.Continuity.Server;
using Aetherfront.Core;
using Aetherfront.Personal.Server;
using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using StoredVessel = Server.System.Vessel.Classes.Vessel;

if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
    throw new ArgumentException("A new disposable test root and read-only DeepFreeze fixture directory are required.");
string root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Preserve previous test evidence.");
Directory.CreateDirectory(root);
int checks = 0, caseIndex = 0, port = 37000;
const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
var results = new List<object>();
var failures = new List<string>();
void Check(bool value, string name)
{
    checks++;
    if (!value) throw new InvalidOperationException(name);
}
void StopTimer()
{
    foreach (string name in new[] { "recoveryTimer", "automaticTimer" })
    {
        var timer = typeof(FlightCheckpointServer).GetField(name, Private);
        using var stopped = new ManualResetEvent(false);
        if (timer.GetValue(null) is Timer active && active.Dispose(stopped))
            Check(stopped.WaitOne(TimeSpan.FromSeconds(5)), "Background checkpoint worker stopped");
        timer.SetValue(null, null);
    }
    ((IDictionary)typeof(FlightCheckpointServer).GetField("Automatic", Private).GetValue(null)).Clear();
}
string Actor(ClientStructure client) => client.AetherfrontAccountId;
double Durable(ClientStructure client) => new ClockStore(ServerContext.UniverseDirectory).Resume(Actor(client), 999999);
ClientStructure Client(string name, RSA key)
{
    var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
    var connection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
    var endpoint = new IPEndPoint(IPAddress.Loopback, port++);
    typeof(NetConnection).GetField("m_remoteEndPoint", Instance).SetValue(connection, endpoint);
    typeof(ClientStructure).GetField("<Connection>k__BackingField", Instance).SetValue(client, connection);
    typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", Instance).SetValue(client, new ConcurrentQueue<IServerMessageBase>());
    var startup = typeof(ClientStructure).GetField("<StartupReplies>k__BackingField", Instance);
    startup?.SetValue(client, Activator.CreateInstance(startup.FieldType, true));
    client.ConnectionStatus = ConnectionStatus.Connected; client.Subspace = int.MinValue;
    var request = new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
    request.IdentityPublicKey = IdentityProof.ExportPublicKey(key);
    request.UniqueIdentifier = IdentityProof.AccountId(request.IdentityPublicKey);
    request.PlayerName = name; request.KspVersion = "1.12.5"; request.ApfEnabled = true; request.ApfClientVersion = "kesas-rc53";
    client.PlayerName = name; client.UniqueIdentifier = request.UniqueIdentifier;
    Check(IdentityAuthentication.Begin(client, request, out var nonce, out var reason), "Identity begin " + reason);
    Check(IdentityAuthentication.Complete(client, IdentityProof.Sign(key, nonce, request.UniqueIdentifier, name), out _, out reason), "Identity complete " + reason);
    client.Authenticated = true;
    Check(AetherfrontServerSystem.BindSession(client), "Account bound");
    ServerContext.Clients[endpoint] = client;
    AetherfrontServerSystem.Execute(state => { PersonalProgression.Ensure(state, Actor(client)); return AetherfrontResult.Success(state.Revision); });
    return client;
}
void Resume(ClientStructure client)
{
    PersonalClockServer.ResumeAndSend(client);
    Check(!client.DisconnectClient && client.Subspace >= 0, "Personal clock resumed");
}
JsonElement Request(ClientStructure client, int command, string entity = "", long generation = 0, string nonce = null)
{
    while (client.SendMessageQueue.TryDequeue(out var old)) old.Recycle();
    var request = new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest,
        CommandType = (AetherfrontCommandType)command, CommandId = nonce ?? Guid.NewGuid().ToString("N"), EntityId = entity, Amount = generation };
    Check(PersonalServer.TryHandle(client, request), "Native personal dispatch handled checkpoint");
    JsonElement result = default;
    while (client.SendMessageQueue.TryDequeue(out var message))
    {
        if (message.Data is AetherfrontCommandReplyMsgData reply && reply.PayloadJson.Contains("\"FlightCheckpoint\""))
            result = JsonDocument.Parse(reply.PayloadJson).RootElement.Clone();
        message.Recycle();
    }
    return result;
}
string Proto(Guid id, uint part, string name, string crew = "") =>
    $"pid = {id:N}\nname = {name}\ntype = Ship\nsit = ORBITING\nlastUT = 1000\nORBIT\n{{\nSMA = 700000\nECC = 0\nREF = 1\nbody = Kerbin\n}}\n" +
    $"PART\n{{\nname = mk1pod\nuid = {part}\n" + (crew.Length == 0 ? "" : "crew = " + crew + "\n") +
    "RESOURCE\n{\nname = Supplies\namount = 5\nmaxAmount = 10\nflowState = True\n}\nMODULE\n{\nname = ModuleScienceContainer\nScienceData\n{\nsubjectID = crewReport@MunInSpaceLow\ndataAmount = 20\n}\n}\n}\n" +
    "ACTIONGROUPS\n{\n}\nDISCOVERY\n{\n}\nFLIGHTPLAN\n{\n}\nCTRLSTATE\n{\n}\nVESSELMODULES\n{\n}\n";
void Vessel(ClientStructure owner, Guid id, string proto)
{
    VesselStoreSystem.CurrentVessels[id] = new StoredVessel(proto);
    File.WriteAllText(Path.Combine(VesselStoreSystem.VesselsPath, id + ".txt"), proto);
    AetherfrontServerSystem.Execute(state =>
    {
        state.Crafts.Add(new CraftRecord { Id = id.ToString(), Name = "Checkpoint fixture", OwnerAccountId = Actor(owner), CreatorAccountId = Actor(owner),
            ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proto))).ToLowerInvariant(), Revision = 1 });
        state.Revision++;
        return AetherfrontResult.Success(state.Revision);
    });
}
foreach (string phase in new[] { "normal", "world", "registry", "clock", "receipt", "own-freeze", "other-freeze" })
{
    string caseRoot = Path.Combine(root, (caseIndex++).ToString("D2") + "-" + phase);
    Directory.CreateDirectory(caseRoot);
    try
    {
        StopTimer();
        typeof(FlightCheckpointServer).GetProperty("BeforeRestorePhase", Private).SetValue(null, null);
        ServerContext.Clients.Clear();
        ((IDictionary)typeof(PersonalClockServer).GetField("Sessions", Private).GetValue(null)).Clear();
        ((IDictionary)typeof(PersonalClockServer).GetField("Stores", Private).GetValue(null)).Clear();
        ServerContext.ConfigDirectory = Path.Combine(caseRoot, "Config"); Directory.CreateDirectory(ServerContext.ConfigDirectory);
        ServerContext.UniverseDirectory = Path.Combine(caseRoot, "Universe"); Directory.CreateDirectory(ServerContext.UniverseDirectory);
        VesselStoreSystem.VesselsPath = Path.Combine(ServerContext.UniverseDirectory, "Vessels"); Directory.CreateDirectory(VesselStoreSystem.VesselsPath);
        Directory.CreateDirectory(KerbalSystem.KerbalsPath);
        VesselStoreSystem.CurrentVessels.Clear(); VesselContext.RemovedVessels.Clear();
        GeneralSettings.SettingsStore.GameMode = GameMode.Science;
        GeneralSettings.SettingsStore.ModControl = false;
        typeof(AetherfrontServerSystem).GetMethod("Initialize", Private, null, new[] { typeof(string), typeof(string) }, null)
            .Invoke(null, new object[] { caseRoot, "checkpoint-fixture" });
        var freeze = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontDeepFreezeSystem");
        freeze.GetMethod("Initialize", Private).Invoke(null, new object[] { ServerContext.UniverseDirectory, true });
        TimeContext.StartTime = DateTime.UtcNow;
        WarpContext.Subspaces.Clear(); WarpContext.Subspaces[0] = new Subspace(0, 1000); WarpContext.NextSubspaceId = 1;
        using var keyA = RSA.Create(2048); using var keyB = RSA.Create(2048);
        var alice = Client("CP_Alice" + caseIndex, keyA);
        var bob = Client("CP_Bob" + caseIndex, keyB);
        Resume(alice); Resume(bob);
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), b1 = Guid.NewGuid(), debris = Guid.NewGuid();
        string pilot = phase == "own-freeze" ? "Jebediah Kerman" : "Checkpoint Pilot";
        if (phase == "own-freeze") a1 = Guid.Parse("ab7a5d6a-fd9d-4818-8750-69bd97035ad1");
        if (phase == "other-freeze") b1 = Guid.Parse("ab7a5d6a-fd9d-4818-8750-69bd97035ad1");
        string cryo = phase.EndsWith("freeze") ? File.ReadAllText(Path.Combine(args[1], "vessel-ab7a5d6a-fd9d-4818-8750-69bd97035ad1.txt")) : string.Empty;
        string original1 = phase == "own-freeze" ? cryo : Proto(a1, 101, "Lander", pilot);
        string original2 = Proto(a2, 102, "Station");
        string originalB = phase == "other-freeze" ? cryo : Proto(b1, 201, "Other player");
        Vessel(alice, a1, original1); Vessel(alice, a2, original2); Vessel(bob, b1, originalB);
        string crewPath = Path.Combine(KerbalSystem.KerbalsPath, pilot + ".txt");
        string crew = phase == "own-freeze" ? File.ReadAllText(Path.Combine(args[1], "crew-Jebediah.txt")) :
            "name = Checkpoint Pilot\ntype = Crew\nstate = Assigned\ntrait = Pilot\n";
        File.WriteAllText(crewPath, crew);
        if (phase.EndsWith("freeze"))
        {
            if (phase == "other-freeze")
                File.WriteAllText(Path.Combine(KerbalSystem.KerbalsPath, "Jebediah Kerman.txt"), File.ReadAllText(Path.Combine(args[1], "crew-Jebediah.txt")));
            var store = new AetherfrontDeepFreezeCheckpointStore(ServerContext.UniverseDirectory);
            store.Commit(store.Recover().Revision, File.ReadAllBytes(Path.Combine(args[1], "Scenario.txt")),
                new Dictionary<Guid, byte[]>(), new Dictionary<string, byte[]>(), Array.Empty<Guid>());
            freeze.GetMethod("Initialize", Private).Invoke(null, new object[] { ServerContext.UniverseDirectory, true });
        }
        var launch = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
        launch.VesselId = a1; launch.Reason = "VAB Launch";
        FlightCheckpointServer.ObserveProto(alice, launch);
        var launchInfo = Request(alice, 81);
        Check(launchInfo.GetProperty("FlightCheckpoint").GetProperty("Points").EnumerateArray().Any(point => point.GetProperty("Label").GetString() == "Latest launch"),
            "Accepted launch captures a fleet checkpoint");
        var saved = Request(alice, 80);
        Check(saved.ValueKind != JsonValueKind.Undefined && saved.GetProperty("Accepted").GetBoolean(), "Whole fleet save acknowledged " + saved);
        string checkpoint = saved.GetProperty("FlightCheckpoint").GetProperty("CheckpointId").GetString();
        double savedTime = saved.GetProperty("FlightCheckpoint").GetProperty("MissionTime").GetDouble();
        Check(saved.GetProperty("FlightCheckpoint").GetProperty("VesselCount").GetInt32() == 2, "Both personal craft captured");
        byte[] foreignFreeze = null;
        if (phase == "other-freeze")
        {
            var store = new AetherfrontDeepFreezeCheckpointStore(ServerContext.UniverseDirectory);
            var current = store.Recover();
            var frozen = DeepFreezeScenarioCodec.Parse(current.ScenarioData, current.ScenarioData.Length);
            frozen.Vessels[b1].StoredEc += 5;
            foreignFreeze = DeepFreezeScenarioCodec.Serialize(frozen);
            store.Commit(current.Revision, foreignFreeze, new Dictionary<Guid, byte[]>(), new Dictionary<string, byte[]>(), Array.Empty<Guid>());
        }
        if (phase == "own-freeze")
        {
            var store = new AetherfrontDeepFreezeCheckpointStore(ServerContext.UniverseDirectory);
            var empty = new DeepFreezeScenarioCodec.Snapshot("DeepFreeze", new[] { "5", "6", "7", "8" }, true, null, null, null, null);
            store.Commit(store.Recover().Revision, DeepFreezeScenarioCodec.Serialize(empty),
                new Dictionary<Guid, byte[]>(), new Dictionary<string, byte[]>(), Array.Empty<Guid>());
            freeze.GetMethod("Initialize", Private).Invoke(null, new object[] { ServerContext.UniverseDirectory, true });
        }
        WarpContext.Subspaces[alice.Subspace].Time += 100;
        PersonalClockServer.Checkpoint(alice); PersonalClockServer.Checkpoint(bob);
        double bobTime = Durable(bob);
        VesselStoreSystem.CurrentVessels.TryRemove(a1, out _); File.Delete(Path.Combine(VesselStoreSystem.VesselsPath, a1 + ".txt"));
        VesselContext.RemovedVessels.TryAdd(a1, 0);
        AetherfrontServerSystem.Execute(state => { state.FindCraft(a1.ToString()).Archived = true; return AetherfrontResult.Success(state.Revision); });
        VesselStoreSystem.CurrentVessels[a2] = new StoredVessel(original2.Replace("amount = 5", "amount = 1"));
        Vessel(alice, debris, Proto(debris, 103, "New debris"));
        File.WriteAllText(crewPath, crew.Replace("Assigned", "Dead"));
        AetherfrontServerSystem.Execute(state =>
        {
            PersonalProgression.Ensure(state, Actor(alice)).Science += 20;
            PersonalProgression.Ensure(state, Actor(bob)).Science += 10;
            state.Revision++;
            return AetherfrontResult.Success(state.Revision);
        });
        var bank = PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), Actor(alice)).Science;
        var otherBank = PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), Actor(bob)).Science;
        if (phase == "world" || phase == "registry" || phase == "clock" || phase == "receipt")
            typeof(FlightCheckpointServer).GetProperty("BeforeRestorePhase", Private).SetValue(null,
                (Action<string>)(current => { if (current == phase) throw new IOException("Synthetic interrupted restore at " + phase); }));
        string restoreId = Guid.NewGuid().ToString("N");
        Request(alice, 82, checkpoint, 0, restoreId);
        StopTimer();
        typeof(FlightCheckpointServer).GetProperty("BeforeRestorePhase", Private).SetValue(null, null);
        Check(!alice.Authenticated && alice.ConnectionStatus == ConnectionStatus.Disconnected, "Loading ends the previous native session");
        var resumed = Client(alice.PlayerName, keyA);
        Resume(resumed);
        Check(Math.Abs(Durable(resumed) - savedTime) < 0.1, "Mission time restored to saved checkpoint");
        Check(VesselStoreSystem.CurrentVessels[a1].ToString() == new StoredVessel(original1).ToString(), "Destroyed craft and carried science restored");
        Check(VesselStoreSystem.CurrentVessels[a2].ToString() == new StoredVessel(original2).ToString(), "Other personal craft resources restored");
        Check(!VesselStoreSystem.VesselExists(debris), "Post-checkpoint personal debris removed");
        Check(File.ReadAllText(crewPath) == crew, "Original crew restored");
        Check(VesselStoreSystem.CurrentVessels[b1].ToString() == new StoredVessel(originalB).ToString(), "Other player's craft preserved");
        Check(Durable(bob) == bobTime && bob.Authenticated, "Other player's clock and connection preserved");
        if (phase == "other-freeze")
            Check(new AetherfrontDeepFreezeCheckpointStore(ServerContext.UniverseDirectory).Recover().ScenarioData.SequenceEqual(foreignFreeze),
                "Other player's newer cryopod state preserved byte for byte");
        if (phase == "own-freeze")
        {
            var frozen = new AetherfrontDeepFreezeCheckpointStore(ServerContext.UniverseDirectory).Recover().ScenarioData;
            Check(DeepFreezeScenarioCodec.Parse(frozen, frozen.Length).Kerbals["Jebediah Kerman"].VesselGuid == a1,
                "Personal frozen crew restored with their original ship");
        }
        Check(PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), Actor(resumed)).Science == bank, "Banked personal science preserved");
        Check(PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), Actor(bob)).Science == otherBank, "Other player's shared science preserved");
        var info = Request(resumed, 81);
        Check(info.GetProperty("FlightCheckpoint").GetProperty("Generation").GetInt64() == 1, "One durable restore receipt");
        WarpContext.Subspaces[resumed.Subspace].Time += 50; PersonalClockServer.Checkpoint(resumed);
        double afterPlay = Durable(resumed);
        var replay = Request(resumed, 82, checkpoint, 0, restoreId);
        Check(replay.GetProperty("Accepted").GetBoolean() && resumed.Authenticated && Durable(resumed) == afterPlay, "Repeated restore request does not rewind again");
        Check(!Directory.EnumerateFiles(Path.Combine(ServerContext.UniverseDirectory, "FlightCheckpoints"), "*.restore").Any(), "Recovery journal completed");
        if (phase == "normal")
        {
            string index = Directory.EnumerateFiles(Path.Combine(ServerContext.UniverseDirectory, "FlightCheckpoints"), "*.history-index").Single();
            byte[] valid = File.ReadAllBytes(index), damaged = (byte[])valid.Clone();
            damaged[12] ^= 1; File.WriteAllBytes(index, damaged);
            var unavailable = Request(resumed, 81);
            Check(!unavailable.GetProperty("Accepted").GetBoolean() && resumed.Authenticated && !resumed.DisconnectClient,
                "Unavailable optional checkpoint history returns an error without disconnecting the player");
            File.WriteAllBytes(index, valid);
            Check(Request(resumed, 81).GetProperty("Accepted").GetBoolean(), "Unchanged fleet and manual checkpoint remain readable");
        }
        results.Add(new { phase, passed = true }); Console.WriteLine("PASS " + phase);
    }
    catch (Exception error)
    {
        failures.Add(phase + ": " + error);
        results.Add(new { phase, passed = false, error = error.ToString() });
        Console.WriteLine("FAIL " + phase + ": " + error.GetBaseException().Message);
    }
}
StopTimer();
File.WriteAllText(Path.Combine(root, "RESULTS.json"), JsonSerializer.Serialize(new { checks, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{checks} assertions, {failures.Count} failed cases");
if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
