using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LunaConfigNode.CfgNode;
using Aetherfront.Core;
using Aetherfront.Synchronization;
using Aetherfront.Synchronization.Server;
using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Message.Base;
using Server.Settings.Structures;
using Server.System;
using Server.System.Vessel;

internal static class Program
{
 static int checks, port = 32500;
 static readonly List<string> Passed = new();
 static readonly Dictionary<string, object> Measurements = new();
 static string root = "";
 static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
 static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Passed.Add(name); Console.WriteLine("PASS " + name); }
 static void Reject(Action action, string name)
 { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, name); }
 static void Main(string[] args)
 {
  if (args.Length != 3) throw new ArgumentException("NEW_FIXTURE_ROOT READ_ONLY_RUNTIME READ_ONLY_CREW_SEED");
  root = Path.GetFullPath(args[0]);
  if (!root.StartsWith(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\", StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
   throw new IOException("A fresh RC56 isolated fixture root is required; production is never a test target.");
  Directory.CreateDirectory(root); Environment.CurrentDirectory = root;
  string runtime = Path.GetFullPath(args[1]);
  crewSeed = Path.GetFullPath(args[2]);
  if (!crewSeed.StartsWith(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(crewSeed)) throw new IOException("Isolated crew seed required.");
  System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
  { string path = Path.Combine(runtime, name.Name + ".dll"); return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null; };
  Run();
  File.WriteAllText(Path.Combine(root, "results.json"), System.Text.Json.JsonSerializer.Serialize(new { utc = DateTime.UtcNow, checks, passed = Passed, measurements = Measurements, phases = SyncTimings.Summary() }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
  Console.WriteLine("PASS " + checks + " RC56 isolated assertions; no production process or files changed.");
 }
 [MethodImpl(MethodImplOptions.NoInlining)] static void Run()
 {
  Policy(); Reconnect(); Ownership(); Deltas(); Wire(); Scheduling(); Dispatcher(); NativeFixture(); CrewRepair();
 }
 static string crewSeed = "";
 static void CrewRepair()
 {
  string world = Path.Combine(root, "CrewRepair"); Directory.CreateDirectory(world);
  foreach (string path in Directory.GetFiles(crewSeed, "*", SearchOption.AllDirectories))
  {
   string target = Path.Combine(world, Path.GetRelativePath(crewSeed, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
  }
  var before = Directory.GetFiles(world, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(world, p), File.ReadAllBytes);
  var frozen = new HashSet<string>(StringComparer.Ordinal);
  string scenario = Path.Combine(world, "DeepFreeze", "Scenario.txt");
  IEnumerable<ConfigNode> Find(ConfigNode node, string name)
  { if (node.Name == name) yield return node; foreach (var child in node.GetAllNodes()) foreach (var result in Find(child, name)) yield return result; }
  if (File.Exists(scenario)) foreach (var node in Find(new ConfigNode(File.ReadAllText(scenario)), "KerbalInfo"))
   foreach (var name in node.GetValues("kerbalName")) frozen.Add(name.Value);
  var repaired = SurvivalRosterRepair.Repair(world, frozen);
  Check(repaired.Files == 7 && repaired.Removed == 9 && repaired.Unresolved == 0, "Copied production fleet reconciles exactly nine stale records in seven vessels");
  int changed = 0;
  foreach (var pair in before)
  {
   string path = Path.Combine(world, pair.Key); byte[] current = File.ReadAllBytes(path);
   if (current.SequenceEqual(pair.Value)) continue;
   Check(pair.Key.StartsWith("Vessels" + Path.DirectorySeparatorChar), "Reconciliation changes only vessel membership records"); changed++;
   string hash = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant();
   string archive = Path.Combine(world, "Survival-Recovery", Path.GetFileNameWithoutExtension(path) + "-" + hash + ".txt");
   Check(File.ReadAllBytes(archive).SequenceEqual(pair.Value), "Exact original vessel and survival history retained before replacement");
   var old = new ConfigNode(Encoding.UTF8.GetString(pair.Value)); var next = new ConfigNode(Encoding.UTF8.GetString(current));
   foreach (var survival in Find(old, "APF_SURVIVAL")) foreach (var crew in survival.GetNodes("CREW").ToArray())
   {
    string name = crew.Value.GetValue("KerbalName")!.Value;
    if (!Find(next, "APF_SURVIVAL").Any(n => n.GetNodes("CREW").Any(c => c.Value.GetValue("KerbalName")?.Value == name))) survival.Nodes.Remove(crew);
   }
   Check(old.ToString() == next.ToString(), "Every epoch, supply value, seat, resource and remaining survival record is unchanged");
  }
  Check(changed == 7, "Exactly the audited seven vessel files change");
  var again = SurvivalRosterRepair.Repair(world, frozen);
  Check(again.Files == 0 && again.Removed == 0, "Survival membership repair is idempotent");
  Measurements["staleSurvivalRecordsReconciled"] = repaired.Removed;
  string protectedWorld = Path.Combine(root, "ProtectedCrewRepair"); Directory.CreateDirectory(protectedWorld);
  foreach (var pair in before) { string path = Path.Combine(protectedWorld, pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, pair.Value); }
  frozen.Add("Tripond Kerman");
  var protectedRepair = SurvivalRosterRepair.Repair(protectedWorld, frozen);
  Check(protectedRepair.Removed == 8 && protectedRepair.Unresolved == 1, "Frozen crew references are retained for explicit review, not silently removed");
  var protectedShip = new ConfigNode(File.ReadAllText(Path.Combine(protectedWorld, "Vessels", "01d0137e-47ce-48e0-b11f-36a29398c3ed.txt")));
  Check(Find(protectedShip, "APF_SURVIVAL").Any(n => n.GetNodes("CREW").Any(c => c.Value.GetValue("KerbalName")?.Value == "Tripond Kerman")),
   "Protected survival history remains attached and unchanged");
 }
 static void Reconnect()
 {
  const string reason = "Loading your personal checkpoint. Connect again to resume your saved fleet and mission time.";
  var p = new CheckpointReconnectPolicy();
  Check(!p.Disconnect(1, reason, 0), "Unsolicited disconnect cannot start checkpoint reconnect");
  p.Arm(1, "fixture-request", 0);
  Check(!p.Disconnect(2, reason, 1) && !p.Disconnect(1, "timeout", 1), "Different session and ordinary timeout cannot authorize reconnect");
  Check(p.Disconnect(1, reason, 2), "Matching restore disconnect authorizes bounded reconnect");
  Check(!p.Tick(10, false, true, false, false) && !p.Tick(10, true, true, true, false), "Reconnect waits for main menu and complete native reset");
  Check(p.Tick(10, true, true, false, false) && !p.Tick(11, true, true, false, false), "Reconnect attempts are paced using unscaled time");
  Check(p.Tick(26, true, true, false, false) && p.Tick(42, true, true, false, false) && !p.Tick(58, true, true, false, false) && !p.Active,
   "Three unsuccessful attempts cannot form an infinite reconnect loop");
  p.Arm(1, "other", 0); p.Disconnect(1, reason, 1); p.Cancel();
  Check(!p.Tick(10, true, true, false, false), "Player cancellation prevents future automatic reconnect");
  p.Arm(1, "stale", 0); Check(!p.Disconnect(1, reason, 181), "Expired restore authorization cannot reconnect later");
  p.Arm(1, "deadline", 0); Check(!p.Disconnect(1, reason, 180), "Restore authorization expires at its exact deadline");
  p.Arm(1, "receive-thread-race", 0); p.Disconnect(1, reason, 1);
  Check(!p.Tick(1.01, false, false, false, true) && p.Active,
   "Old Running state after receive-thread disconnect cannot cancel pending menu exit");
  Check(p.Tick(5, true, true, false, false) && !p.Tick(6, false, false, false, true) && !p.Active,
   "Only a successful new connection completes checkpoint reconnect");
  Reject(() => p.Arm(1, "", 0), "Missing restore request cannot authorize automatic reconnect");
  Reject(() => p.Arm(1, "invalid-time", double.NaN), "Nonfinite restore clock cannot authorize reconnect");
 }
 static void Policy()
 {
  var p = new WarpRecoveryPolicy(); p.Begin(3, 17, 1, 1000, 50);
  Check(p.ShouldSend(50) && !p.ShouldSend(50.1) && p.ShouldSend(52), "Bounded two-second retry cadence");
  Check(!p.CanAccept(2, 17, 1, 1, 1000, 51), "Previous connection ACK cannot unlock flight");
  Check(!p.CanAccept(3, 18, 1, 1, 1000, 51) && !p.CanAccept(3, 17, 2, 1, 1000, 51), "Session and request identifiers both required");
  Check(!p.CanAccept(3, 17, 1, 1, 999, 51) && !p.CanAccept(3, 17, 1, 1, 1004, 51), "Clock rollback and excessive normalization refused");
  Check(p.Expired(90) && p.CanAccept(3, 17, 1, 1, 1000, 51), "Timely queued ACK remains valid after a 40-second Unity stall");
  Check(!p.CanAccept(3, 17, 1, 1, 1000, 65) && !p.CanAccept(3, 17, 1, 1, 1000, double.NaN), "Late or nonfinite ACK cannot bypass the deadline");
  p.Accepted(1); p.Begin(3, 17, 2, 1000, 100);
  Check(!p.CanAccept(3, 17, 1, 1, 1000, 101), "Equal clock offsets do not conflate different requests");
  p.Cancel(); Check(!p.Pending && !p.CanAccept(3, 17, 2, 2, 1000, 101), "Termination and scene cancellation retire the pending ACK");
  p.Begin(3, 17, 3, 1000, 140); p.Cancel();
  Check(!p.Expired(500), "Scene loading longer than 15 seconds does not expire a cancelled request");
  p.Begin(3, 17, 4, 1000, 500); Check(p.Expired(838.193), "Multi-minute server stall remains bounded by safe failure");
  p.Reset(); Check(!p.Pending && p.LastAccepted == -1, "Connection reset retires the entire boundary session");
  Check(WarpRecoveryPolicy.ForwardBudget(100, 120, true) == 40 && WarpRecoveryPolicy.ForwardBudget(100, 120, false) == 20,
   "No additional scene-debt skip while burning or otherwise unsafe");
 }
 static void Ownership()
 {
  var vessel = Guid.NewGuid(); var lease = new HoldLease();
  lease.Begin(vessel, false, 1, .8f); var done = lease.End(true, vessel, true, 0, 0);
  Check(done.Unpause && done.RestoreScale && done.Scale == 1 && done.RestoreThrottle && done.Throttle == .8f, "Successful same-vessel sync releases only its own hold");
  lease.Begin(vessel, true, 0, .8f); done = lease.End(true, vessel, true, 0, 0);
  Check(!done.Unpause && !done.RestoreScale && !done.RestoreThrottle, "A pre-existing player/mod pause remains owned by its creator");
  lease.Begin(vessel, false, 0, .8f); done = lease.End(false, vessel, true, 0, 0);
  Check(done.RestoreScale && done.Scale == 0 && !done.RestoreThrottle, "An independently frozen time scale is not accidentally resumed");
  lease.Begin(vessel, false, 1, .8f); lease.ObserveExternalPause(true); done = lease.End(true, vessel, true, 0, 0);
  Check(!done.Unpause && !done.RestoreScale && !done.RestoreThrottle, "Pause requested during synchronization remains in force");
  lease.Begin(vessel, false, 1, .8f); done = lease.End(false, vessel, true, 0, 0);
  Check(!done.RestoreThrottle && !lease.Active, "Termination, disconnect and scene exit never restart an engine");
  lease.Begin(vessel, false, 1, .8f); done = lease.End(true, Guid.NewGuid(), true, 0, 0);
  Check(!done.RestoreThrottle, "A replacement vessel cannot inherit the old flight throttle");
 }
 static void Deltas()
 {
  var before = new AetherfrontState { ServerId = "delta-fixture", Revision = 10 };
  for (int i = 0; i < 2000; i++) before.Crafts.Add(new CraftRecord { Id = "craft-" + i, Name = "Fixture " + i, Revision = 1, OwnerAccountId = "fixture-owner" });
  var next = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(before)); next.Revision++;
  next.Crafts[100].Name = "Changed craft \"quoted\" \\ [ { \n \u03a9"; next.Crafts[100].Revision++; next.Crafts.RemoveAt(200);
  next.Crafts.Add(new CraftRecord { Id = "new-craft", Name = "New fixture", Revision = 1, OwnerAccountId = "fixture-owner" });
  long began = Stopwatch.GetTimestamp(); var delta = StateDelta.Between(before, next); string encoded = AetherfrontJson.Serialize(delta);
  var decoded = AetherfrontJson.Deserialize<StateDelta>(encoded); var applied = decoded.Apply(before);
  Check(AetherfrontJson.Serialize(applied) == AetherfrontJson.Serialize(next), "Craft updates, removals, insertions and ordering reconstruct the exact state");
  Check(before.Crafts[100].Name != next.Crafts[100].Name && before.Crafts.Count == 2000, "Applying a delta does not mutate its base snapshot");
  int full = Encoding.UTF8.GetByteCount(AetherfrontJson.Serialize(next)), patch = Encoding.UTF8.GetByteCount(encoded);
  Check(patch < full / 5, "Changed-craft delta removes at least 80 percent of full snapshot bytes");
  Measurements["snapshotFullBytes"] = full; Measurements["snapshotDeltaBytes"] = patch;
  Measurements["snapshotDeltaRoundTripMilliseconds"] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
  Console.WriteLine("MEASURE snapshot fullBytes=" + full + "; deltaBytes=" + patch + "; roundTripMs=" + Measurements["snapshotDeltaRoundTripMilliseconds"]);
  var same = StateDelta.Between(next, next); Check(same.Sections.Count == 0 && same.CraftOrder == null, "Unchanged revision poll needs no state payload");
  var expiry = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(next)); expiry.Crafts.RemoveAt(0);
  var expired = StateDelta.Between(next, expiry); Check(expired.CraftOrder != null && expired.Apply(next).Crafts.Count == next.Crafts.Count - 1,
   "Permission projection expiry still applies at the same ledger revision");
  Reject(() => decoded.Apply(new AetherfrontState { ServerId = "other", Revision = 10 }), "Cross-server delta rejected");
  Reject(() => decoded.Apply(new AetherfrontState { ServerId = before.ServerId, Revision = 9 }), "Missing-base delta requires full resynchronization");
  decoded.Sections.Add(new StateSection { Name = "NotAStateProperty", Json = "1" }); Reject(() => decoded.Apply(before), "Unknown patch property rejected before publication");
  Check(JsonSlices.Array("[\"a,]\\\"\",{\"nested\":[1,2]},null]").Count == 3, "Canonical slices preserve nested and escaped delimiters");
  Reject(() => JsonSlices.Array("[{]}]"), "Mismatched canonical nesting rejected");
 }
 static NetOutgoingMessage Outgoing()
 {
  var message = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true)!;
  typeof(NetBuffer).GetField("m_data", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(message, new byte[256]); return message;
 }
 static NetIncomingMessage Incoming(NetOutgoingMessage source)
 {
  var message = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true)!;
  typeof(NetBuffer).GetField("m_data", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(message, (byte[])source.Data.Clone()); message.LengthBits = source.LengthBits; return message;
 }
 static void Wire()
 {
  var factory = new ClientMessageFactory();
  foreach (int alignment in Enumerable.Range(0, 8))
  {
   var source = factory.CreateNewMessageData<WarpNewSubspaceMsgData>(); source.PlayerCreator = "WireFixture"; source.SubspaceKey = 7; source.ServerTimeDifference = 123.5;
   BoundaryWire.Set(source, long.MinValue + 123, long.MaxValue - 100);
   var write = Outgoing(); for (int i = 0; i < alignment; i++) write.Write(true);
   typeof(WarpNewSubspaceMsgData).GetMethod("InternalSerialize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, new object[] { write });
   var read = Incoming(write); for (int i = 0; i < alignment; i++) read.ReadBoolean();
   var target = factory.CreateNewMessageData<WarpNewSubspaceMsgData>();
   typeof(WarpNewSubspaceMsgData).GetMethod("InternalDeserialize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, new object[] { read });
   Check(target.SubspaceKey == 7 && target.ServerTimeDifference == 123.5 && BoundaryWire.Session(target) == BoundaryWire.Session(source) &&
    BoundaryWire.Request(target) == BoundaryWire.Request(source) && read.Position == write.LengthBits, "Native identified warp wire round trip at bit alignment " + alignment);
  }
 }
 static void Scheduling()
 {
  var factory = new ClientMessageFactory(); double seconds = 0; var recycled = new HashSet<IMessageBase>();
  var queue = new TrafficScheduler(m => { if (!recycled.Add(m)) throw new Exception("Double recycle"); }, () => seconds);
  IMessageBase Sync(Guid[] ids)
  { var data = factory.CreateNewMessageData<VesselSyncMsgData>(); data.VesselIds = ids; data.VesselsCount = ids.Length; return factory.CreateNew<VesselCliMsg>(data); }
  var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }; for (int i = 0; i < 100; i++) queue.Add(Sync(ids));
  Check(queue.Count == 1 && queue.DuplicatesFiltered == 99, "One outstanding identical fleet query replaces 100 queued requests");
  var remove = factory.CreateNewMessageData<VesselRemoveMsgData>(); remove.VesselId = ids[0]; queue.Add(factory.CreateNew<VesselCliMsg>(remove)); queue.Add(Sync(ids));
  Check(queue.Count == 3, "A reliable removal barrier prevents cross-transaction snapshot suppression"); queue.Clear();
  IMessageBase Proto(Guid id, byte value)
  { var data = factory.CreateNewMessageData<VesselProtoMsgData>(); data.VesselId = id; data.Data = Enumerable.Repeat(value, 65536).ToArray(); data.NumBytes = data.Data.Length; return factory.CreateNew<VesselCliMsg>(data); }
  var id = Guid.NewGuid(); queue.Add(Proto(id, 1)); queue.Add(Proto(id, 1));
  Check(queue.Count == 1, "Identical pending vessel definition is encoded only once");
  queue.Add(Proto(id, 2)); Check(queue.Count == 2, "Different vessel revision bytes cannot be deduplicated");
  queue.Add(Proto(id, 1)); Check(queue.Count == 3, "A,B,A vessel transactions retain the final A"); queue.Clear();
  queue = new TrafficScheduler(_ => { }, () => seconds);
  for (int i = 0; i < 100; i++) queue.Add(Proto(Guid.NewGuid(), 1));
  int sent = 0; while (queue.TryTake((_, _) => true, out _)) sent++;
  for (int tick = 0; tick < 100; tick++) { seconds += .01; while (queue.TryTake((_, _) => true, out _)) sent++; }
  Check(sent >= 70 && sent <= 80, "Faster byte pacing stays bounded by burst and message budgets");
  Measurements["syntheticBulkBytesFirstSecond"] = sent * 65536L; queue.Clear();
  var blocked = Proto(id, 1); queue.Add(blocked);
  var warp = factory.CreateNewMessageData<WarpNewSubspaceMsgData>(); warp.PlayerCreator = "fixture";
  var control = factory.CreateNew<WarpCliMsg>(warp); queue.Add(control);
  Check(queue.TryTake((m, _) => !ReferenceEquals(m, blocked), out var taken) && ReferenceEquals(taken, control), "Clock control bypasses a backpressured bulk lane"); queue.Clear();
 }
 static void Dispatcher()
 {
  var actor = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure)); actor.ConnectionStatus = ConnectionStatus.Connected;
  var blocked = new ManualResetEventSlim(); var release = new ManualResetEventSlim(); var done = new ManualResetEventSlim(); var factory = new ClientMessageFactory();
  var first = factory.CreateNew<VesselCliMsg>(factory.CreateNewMessageData<VesselRemoveMsgData>());
  var ids = new ConcurrentDictionary<int, bool>();
  var reader = new FixtureReader((_, message) => { ids[Environment.CurrentManagedThreadId] = true; if (ReferenceEquals(message, first)) { blocked.Set(); if (!release.Wait(5000)) throw new Exception("Fixture timeout"); } else done.Set(); });
  ReceivePump.Enqueue(reader, actor, first); Check(blocked.Wait(5000), "Serial gameplay dispatcher begins independently of intake");
  var latency = Stopwatch.StartNew();
  var data = factory.CreateNewMessageData<WarpNewSubspaceMsgData>(); data.PlayerCreator = "fixture"; ReceivePump.Enqueue(reader, actor, factory.CreateNew<WarpCliMsg>(data));
  Check(latency.ElapsedMilliseconds < 1000, "Blocked gameplay does not block packet intake"); release.Set();
  Check(done.Wait(5000) && ids.Count == 1, "Native mutations remain serial; no unsafe parallel gameplay handlers introduced");
 }
 static void NativeFixture()
 {
  ServerContext.ConfigDirectory = Path.Combine(root, "Config"); Directory.CreateDirectory(ServerContext.ConfigDirectory);
  ServerContext.UniverseDirectory = Path.Combine(root, "Universe"); Directory.CreateDirectory(ServerContext.UniverseDirectory);
  GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox; GeneralSettings.SettingsStore.ModControl = false;
  typeof(AetherfrontServerSystem).GetMethod("Initialize", PrivateStatic, null, new[] { typeof(string), typeof(string) }, null)!.Invoke(null, new object[] { root, "rc56-isolated" });
  TimeContext.StartTime = DateTime.UtcNow; WarpContext.Subspaces.Clear(); WarpContext.Subspaces.TryAdd(0, new Subspace(0, 1000)); WarpContext.NextSubspaceId = 1;
  var client = Client("SyncFixture"); Server.System.WarpSystemSender.SendAllSubspaces(client);
  var request = new ClientMessageFactory().CreateNewMessageData<WarpNewSubspaceMsgData>(); request.PlayerCreator = client.PlayerName;
  request.ServerTimeDifference = WarpContext.Subspaces[client.Subspace].Time + 5; BoundaryWire.Set(request, 1234567, 1);
  int before = WarpContext.NextSubspaceId; var receiver = new Server.System.WarpSystemReceiver(); receiver.HandleNewSubspace(client, request);
  Check(WarpContext.NextSubspaceId == before + 1 && client.Subspace == before, "Native clock transition commits one authenticated personal timeline");
  var responses = Drain(client); var ack = responses.OfType<WarpNewSubspaceMsgData>().Last();
  Check(BoundaryWire.Session(ack) == 1234567 && BoundaryWire.Request(ack) == 1, "Native server reply echoes the exact session/request identity");
  receiver.HandleNewSubspace(client, request); Check(WarpContext.NextSubspaceId == before + 1, "Lost-ACK retry cannot create duplicate subspaces");
  Check(Drain(client).OfType<WarpNewSubspaceMsgData>().Any(a => a.SubspaceKey == before), "Retry replays the already committed ACK");
  Store(); Backups(); SnapshotPipeline(client);
 }
 static void SnapshotPipeline(ClientStructure client)
 {
  var factory = new ClientMessageFactory();
  AetherfrontWireReply Poll(long revision)
  {
   var data = factory.CreateNewMessageData<AetherfrontCommandMsgData>();
   data.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest,
    ProtocolVersion = 1, ExpectedRevision = revision, Body = "apf-state-delta-1" });
   var clock = Stopwatch.StartNew();
   new global::Server.Message.AetherfrontMsgReader().HandleMessage(client, factory.CreateNew<AetherfrontCliMsg>(data));
   Check(clock.ElapsedMilliseconds < 3000, "Native snapshot admission does not await background encoding");
   var replies = new List<AetherfrontWireReply>();
   Check(SpinWait.SpinUntil(() =>
   {
    foreach (var reply in Drain(client).OfType<AetherfrontCommandReplyMsgData>())
     replies.Add(AetherfrontProtocol.DeserializeReply(reply.PayloadJson));
    return replies.Any(r => r.Operation == AetherfrontWireOperation.SnapshotReply);
   }, 15000), "Native asynchronous snapshot reply arrives within the fixture bound");
   return replies.Last(r => r.Operation == AetherfrontWireOperation.SnapshotReply);
  }
  string account = client.UniqueIdentifier, craftId = Guid.NewGuid().ToString();
  AetherfrontServerSystem.Store.Execute(s =>
  {
   s.Crafts.Add(new CraftRecord { Id = craftId, Name = "Visible fixture craft", OwnerAccountId = account, Revision = 1 });
   for (int i = 0; i < 1000; i++) s.Crafts.Add(new CraftRecord { Id = Guid.NewGuid().ToString(), Name = "Fleet fixture " + i,
    OwnerAccountId = account, Revision = 1 });
   s.Revision++; return AetherfrontResult.Success(s.Revision);
  });
  var initial = Poll(-1); var baseline = AetherfrontJson.Deserialize<AetherfrontState>(initial.StateJson);
  Check(initial.Accepted && !initial.StateJson.StartsWith(StateDelta.Prefix) && baseline.FindCraft(craftId) != null,
   "Initial native poll returns a complete authorized state");
  Check(baseline.Accounts.All(a => a.AccountId == account) && baseline.CommandReceipts.Count == 0 &&
   baseline.PersonalProgression.Players.All(p => p.AccountId == account), "Background projection retains native account and personal-science privacy filters");
  AetherfrontServerSystem.Store.Execute(s => { s.FindCraft(craftId).Name = "Changed through native store"; s.Revision++; return AetherfrontResult.Success(s.Revision); });
  var update = Poll(baseline.Revision);
  Check(update.StateJson.StartsWith(StateDelta.Prefix), "Native advertised-base poll sends a state difference");
  Measurements["nativeFullReplyStateBytes"] = Encoding.UTF8.GetByteCount(initial.StateJson);
  Measurements["nativeDeltaReplyStateBytes"] = Encoding.UTF8.GetByteCount(update.StateJson);
  Check(Encoding.UTF8.GetByteCount(update.StateJson) < Encoding.UTF8.GetByteCount(initial.StateJson) / 5,
   "Native projected 1001-craft pipeline reduces changed-state bytes by at least 80 percent");
  var delta = AetherfrontJson.Deserialize<StateDelta>(update.StateJson.Substring(StateDelta.Prefix.Length));
  var updated = delta.Apply(baseline);
  Check(updated.FindCraft(craftId).Name == "Changed through native store" && updated.Revision == update.Revision,
   "Native projected difference reconstructs current craft and revision");
  var unchanged = Poll(updated.Revision);
  Check(unchanged.Accepted && string.IsNullOrEmpty(unchanged.StateJson) && !string.IsNullOrEmpty(unchanged.InterestJson),
   "Unchanged native poll refreshes interest without retransmitting full state");
  // A changing world must not defeat suppression while a client still advertises
  // the same old base. Otherwise full replies accumulate behind its first transfer.
  long fullReplies = SnapshotTransport.FullReplies, deltaReplies = SnapshotTransport.DeltaReplies, coalesced = SnapshotTransport.Coalesced;
  AetherfrontServerSystem.Store.Execute(s => { s.Revision++; return AetherfrontResult.Success(s.Revision); });
  SnapshotTransport.Enqueue(client, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest,
   ExpectedRevision = updated.Revision - 1, Body = "apf-state-delta-1" });
  // LastExpected is the acknowledged current base after the unchanged poll. Send
  // that same base once, then advance again before repeating it unacknowledged.
  Check(SpinWait.SpinUntil(() => Drain(client).OfType<AetherfrontCommandReplyMsgData>().Any(), 15000),
   "A new advertised base can initiate the next state transfer");
  using (SyncTimings.FleetScope()) { } // Wait until publication and peer-cache advancement both complete.
  fullReplies = SnapshotTransport.FullReplies; deltaReplies = SnapshotTransport.DeltaReplies; coalesced = SnapshotTransport.Coalesced;
  AetherfrontServerSystem.Store.Execute(s => { s.Revision++; return AetherfrontResult.Success(s.Revision); });
  SnapshotTransport.Enqueue(client, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest,
   ExpectedRevision = updated.Revision - 1, Body = "apf-state-delta-1" });
  Check(SpinWait.SpinUntil(() => SnapshotTransport.Coalesced > coalesced, 15000) && SnapshotTransport.FullReplies == fullReplies && SnapshotTransport.DeltaReplies == deltaReplies,
   "World revision changes do not accumulate another unacknowledged full snapshot");
 }
 static ClientStructure Client(string name)
 {
  var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure)); client.Subspace = int.MinValue; client.ConnectionStatus = ConnectionStatus.Connected;
  using var key = RSA.Create(2048); var request = new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
  request.IdentityPublicKey = IdentityProof.ExportPublicKey(key); request.UniqueIdentifier = IdentityProof.AccountId(request.IdentityPublicKey);
  request.PlayerName = name; request.KspVersion = "1.12.5"; request.ApfEnabled = true; request.ApfClientVersion = "kesas-rc58";
  client.UniqueIdentifier = request.UniqueIdentifier; client.PlayerName = name;
  Check(Server.System.IdentityAuthentication.Begin(client, request, out var nonce, out var reason), "Disposable native authentication challenge: " + reason);
  Check(Server.System.IdentityAuthentication.Complete(client, IdentityProof.Sign(key, nonce, request.UniqueIdentifier, name), out _, out reason), "Disposable native authentication proof: " + reason);
  client.Authenticated = true; Check(AetherfrontServerSystem.BindSession(client), "Disposable account bound without production identities");
  ServerContext.Clients.TryAdd(new IPEndPoint(IPAddress.Loopback, port++), client); return client;
 }
 static List<IMessageData> Drain(ClientStructure client)
 {
  var table = typeof(ServerTraffic).GetField("States", PrivateStatic)!.GetValue(null)!; object?[] args = { client, null };
  if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!) return new();
  var queue = (TrafficScheduler)args[1]!.GetType().GetField("Queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(args[1])!;
  var result = new List<IMessageData>(); while (queue.TryTake((_, _) => true, out var message)) result.Add(message.Data); return result;
 }
 static void Store()
 {
  var store = AetherfrontServerSystem.Store; var before = store.ReadSnapshot();
  var change = store.Execute(s => { s.Revision++; return AetherfrontResult.Success(s.Revision, "", "", new List<AetherfrontEvent>()); });
  var after = store.ReadSnapshot(); Check(change.Accepted && after.Revision == before.Revision + 1, "Durable store commit precedes authoritative state advancement");
  string path = Path.Combine(store.RootPath, "state.json");
  Check(AetherfrontJson.Deserialize<AetherfrontState>(File.ReadAllText(path)).Revision == after.Revision, "Acknowledged revision is present in the canonical durable file");
  after.Crafts.Clear(); Check(store.ReadSnapshot().Revision == before.Revision + 1, "Snapshot copies remain isolated from authoritative memory");
  using (var denied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
   Reject(() => store.Execute(s => { s.Revision++; return AetherfrontResult.Success(s.Revision, "", "", new List<AetherfrontEvent>()); }), "Denied canonical replacement cannot acknowledge a mutation");
  Check(store.ReadSnapshot().Revision == before.Revision + 1, "Failed disk commit cannot advance memory");
 }
 static object? CallBackup(string method, params object[] args) => typeof(VesselBackup).GetMethod(method, PrivateStatic)!.Invoke(null, args);
 static void Backups()
 {
  string directory = Path.Combine(ServerContext.UniverseDirectory, "Vessels"); Directory.CreateDirectory(directory); VesselStoreSystem.VesselsPath = directory;
  var id = Guid.NewGuid(); string vesselText = "pid = " + id + "\n name = RC56 fixture\n type = Probe\n root = 0\n" +
   string.Join("\n", new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(name => name + "\n{\n}\n"));
  var vessel = new Server.System.Vessel.Classes.Vessel(vesselText); VesselStoreSystem.CurrentVessels[id] = vessel;
  var plan = CallBackup("Capture", id) ?? throw new Exception("Backup capture failed"); Check(true, "Backup captures a stable vessel identity before I/O");
  object fleet = typeof(AetherfrontServerSystem).Assembly.GetType("Server.Aetherfront.AetherfrontFleetGuard", true)!.GetField("Gate", PrivateStatic)!.GetValue(null)!;
  var locked = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
  var thread = new Thread(() => { lock (fleet) { locked.Set(); release.Wait(5000); } }); thread.Start(); Check(locked.Wait(5000), "Independent gameplay can own fleet lock during backup staging");
  var stage = Task.Run(() => CallBackup("Stage", plan)); bool staged = stage.Wait(3000); release.Set(); thread.Join();
  Check(staged, "Backup disk write and durable flush finish without acquiring fleet lock");
  VesselContext.RemovedVessels[id] = 0; VesselStoreSystem.CurrentVessels.TryRemove(id, out _);
  Check(!(bool)CallBackup("Publish", plan)!, "Deletion tombstone invalidates an already flushed backup"); CallBackup("Discard", plan);
  Check(!File.Exists(Path.Combine(directory, id + ".txt")), "Stale backup cannot resurrect a deleted vessel file");
  VesselContext.RemovedVessels.TryRemove(id, out _); VesselStoreSystem.CurrentVessels[id] = vessel;
  var old = CallBackup("Capture", id)!; var newer = CallBackup("Capture", id)!; CallBackup("Stage", old); CallBackup("Stage", newer);
  Check((bool)CallBackup("Publish", newer)! && !(bool)CallBackup("Publish", old)!, "A later capture prevents out-of-order disk publication");
  CallBackup("Discard", old); CallBackup("Discard", newer);
  Check(Directory.GetFiles(directory, "*.pending").Length == 0, "Completed and cancelled staging files are cleaned up");
  string canonical = Path.Combine(directory, id + ".txt");
  byte[] committed = File.ReadAllBytes(canonical); DateTime written = File.GetLastWriteTimeUtc(canonical);
  long durableStages = VesselBackup.DurableStages, skipped = VesselBackup.UnchangedSkipped;
  Check(VesselBackup.Save(id) && VesselBackup.DurableStages == durableStages && VesselBackup.UnchangedSkipped == skipped + 1,
   "Identical canonical vessel skips temporary write and durable flush");
  Check(File.GetLastWriteTimeUtc(canonical) == written && File.ReadAllBytes(canonical).SequenceEqual(committed),
   "No-op backup leaves canonical bytes and write timestamp untouched");
  var unchangedPlan = CallBackup("Capture", id)!; CallBackup("Stage", unchangedPlan);
  string replacement = canonical + ".replacement"; File.WriteAllBytes(replacement, committed);
  File.SetCreationTimeUtc(replacement, File.GetCreationTimeUtc(canonical)); File.SetLastWriteTimeUtc(replacement, written);
  File.Move(replacement, canonical, true);
  Check(!(bool)CallBackup("Publish", unchangedPlan)!, "Same bytes and timestamps cannot hide replacement of a checked canonical file identity");
  CallBackup("Discard", unchangedPlan);
  File.WriteAllText(canonical, "changed canonical contents");
  Check(VesselBackup.Save(id) && VesselBackup.DurableStages == durableStages + 1 && File.ReadAllBytes(canonical).SequenceEqual(committed),
   "Changed canonical contents still require a fully flushed atomic backup");
  unchangedPlan = CallBackup("Capture", id)!; CallBackup("Stage", unchangedPlan);
  VesselContext.RemovedVessels[id] = 0;
  Check(!(bool)CallBackup("Publish", unchangedPlan)!, "Unchanged-backup optimization preserves deletion tombstones");
  VesselContext.RemovedVessels.TryRemove(id, out _); CallBackup("Discard", unchangedPlan);
  unchangedPlan = CallBackup("Capture", id)!; CallBackup("Stage", unchangedPlan);
  object pathLock = typeof(FileHandler).GetMethod("GetLockSemaphore", PrivateStatic | BindingFlags.Public)!.Invoke(null, new object[] { canonical })!;
  locked.Reset(); release.Reset();
  thread = new Thread(() => { lock (pathLock) { locked.Set(); release.Wait(5000); } }); thread.Start();
  Check(locked.Wait(5000), "Native per-path writer lock contention is exercised");
  var publish = Task.Run(() => CallBackup("Publish", unchangedPlan)); bool returned = publish.Wait(1000);
  release.Set(); thread.Join();
  Check(returned && !(bool)publish.Result!, "Backup never waits for per-path I/O while holding gameplay locks");
  CallBackup("Discard", unchangedPlan);
  Check(VesselBackup.Save(id), "A fresh capture retries safely after per-path contention");
 }
 sealed class FixtureReader(Action<ClientStructure, IClientMessageBase> action) : ReaderBase
 { public override void HandleMessage(ClientStructure client, IClientMessageBase message) => action(client, message); }
}
