using Aetherfront.Stability;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Interface;
using Aetherfront.Stability.Server;
using Server.Client;
using Server.Message.Base;
using LmpCommon.Enums;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Diagnostics;

string runtime = Path.GetFullPath(args[0]);
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
{ string path = Path.Combine(runtime, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null; };
int count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
var policy = new WarpRecoveryPolicy();
policy.Begin(5, 1000, 50);
Check(policy.ShouldSend(50) && !policy.ShouldSend(50.1), "Initial request has bounded retry cadence");
Check(!policy.CanAccept(4, 20, 1000), "Old connection acknowledgement rejected");
Check(!policy.CanAccept(5, 20, 999) && !policy.CanAccept(5, 20, 1004), "Previous warp and implausible future acknowledgements rejected");
Check(!policy.CanAccept(5, 20, double.NaN), "Nonfinite acknowledgement rejected");
Check(policy.CanAccept(5, 20, 1003.5), "Bounded server clock normalization accepted");
Check(policy.ShouldSend(52) && !policy.Expired(64.999) && policy.Expired(65), "Delayed ACK reaches finite 15 second deadline");
Check(!policy.ShouldSend(65), "No retry after recovery timeout");
policy.Accepted(20); policy.Begin(5, 1100, 70);
Check(!policy.CanAccept(5, 19, 1100), "Retired subspace cannot replace a newer boundary");
policy.Reset(); Check(!policy.Pending && !policy.CanAccept(5, 30, 1100), "Session reset retires outstanding acknowledgements");

var factory = new ClientMessageFactory();
IMessageBase Position(Guid id)
{
 var data = factory.CreateNewMessageData<VesselPositionMsgData>(); data.VesselId = id;
 return factory.CreateNew<VesselCliMsg>(data);
}
var recycled = new HashSet<IMessageBase>(); double clock = 0;
var queue = new TrafficScheduler(m => { if (!recycled.Add(m)) throw new Exception("Double recycle"); }, () => clock);
var vessel = Guid.NewGuid();
for (int i = 0; i < 500; i++) queue.Add(Position(vessel));
Check(queue.Count == 1 && queue.Coalesced == 499 && recycled.Count == 499, "500 superseded positions coalesce into one owned message");
for (int i = 0; i < 100; i++) queue.Add(Position(Guid.NewGuid()));
Check(queue.Count == 101, "Different vessels retain independent latest snapshots");
int drained = 0; while (queue.TryTake((_, _) => true, out _)) drained++;
Check(drained == 64 && queue.Count == 37, "Application burst bounded before receiver queues fill");
clock += 1; while (queue.TryTake((_, _) => true, out _)) drained++;
Check(drained == 101 && queue.Count == 0, "Paced state drains after token refill");
var fx = factory.CreateNewMessageData<VesselPartSyncFieldMsgData>(); fx.VesselId = vessel; fx.ModuleName = "FXModuleThrottleEffects"; fx.FieldName = "state";
queue.Add(factory.CreateNew<VesselCliMsg>(fx));
Check(queue.Count == 0 && queue.CosmeticsFiltered == 1, "Known renderer-only throttle effect never occupies reliable transport");
var resource = factory.CreateNewMessageData<VesselPartSyncFieldMsgData>(); resource.VesselId = vessel; resource.ModuleName = "ModuleEnginesFX"; resource.FieldName = "currentThrottle";
Check(!TrafficScheduler.Cosmetic(resource), "Engine control is not confused with cosmetic throttle effect");
var proto = factory.CreateNewMessageData<VesselProtoMsgData>(); proto.VesselId = vessel;
var first = factory.CreateNew<VesselCliMsg>(proto);
var remove = factory.CreateNewMessageData<VesselRemoveMsgData>(); remove.VesselId = vessel;
var second = factory.CreateNew<VesselCliMsg>(remove);
queue.Add(first); queue.Add(second);
var warp = factory.CreateNewMessageData<WarpNewSubspaceMsgData>(); warp.PlayerCreator = "fixture"; warp.ServerTimeDifference = 500;
var control = factory.CreateNew<WarpCliMsg>(warp); queue.Add(control);
Check(queue.TryTake((m, _) => !ReferenceEquals(m, first), out var sent) && ReferenceEquals(sent, control), "Clock acknowledgement bypasses blocked vessel-transfer lane");
Check(!queue.TryTake((m, _) => !ReferenceEquals(m, first), out _), "Vessel removal cannot overtake its proto on the same reliable channel");
Check(queue.TryTake((_, _) => true, out sent) && ReferenceEquals(sent, first), "Original reliable lane order retained");
Check(queue.TryTake((_, _) => true, out sent) && ReferenceEquals(sent, second), "Gameplay removal remains queued and delivered");
queue.Clear();
Check(queue.Count == 0 && queue.Bytes == 0, "Cancellation clears accounting and owned references");
var actor = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure)); actor.ConnectionStatus = ConnectionStatus.Connected;
var blocked = new ManualResetEventSlim(); var release = new ManualResetEventSlim(); var done = new ManualResetEventSlim();
var observed = new ConcurrentQueue<string>(); var threads = new ConcurrentDictionary<int, bool>();
var reader = new FixtureReader((_, message) =>
{
 threads[Environment.CurrentManagedThreadId] = true;
 if (ReferenceEquals(message, first)) { blocked.Set(); if (!release.Wait(5000)) throw new Exception("Fixture release timeout"); }
 else { observed.Enqueue(message.Data.GetType().Name); if (observed.Count >= 2) done.Set(); }
});
ReceivePump.Enqueue(reader, actor, (IClientMessageBase)first);
Check(blocked.Wait(5000), "Native dispatch worker starts independently of packet intake");
var latency = Stopwatch.StartNew();
for (int i = 0; i < 500; i++) ReceivePump.Enqueue(reader, actor, (IClientMessageBase)Position(vessel));
ReceivePump.Enqueue(reader, actor, (IClientMessageBase)control);
Check(latency.ElapsedMilliseconds < 500, "Packet intake remains responsive while a native gameplay handler is blocked");
release.Set(); Check(done.Wait(5000), "Queued control and latest position drain after handler resumes");
Check(observed.TryDequeue(out string name) && name == "WarpNewSubspaceMsgData", "Clock boundary takes priority over coalesced positions in real receive pump");
Check(threads.Count == 1, "All native gameplay callbacks remain serial on one worker");
Check(ReceivePump.Coalesced >= 499, "Incoming superseded positions removed before native lock/disk work");
Console.WriteLine("PASS " + count + " shipping-graft scheduling/recovery assertions");

sealed class FixtureReader(Action<ClientStructure, IClientMessageBase> action) : ReaderBase
{ public override void HandleMessage(ClientStructure client, IClientMessageBase message) => action(client, message); }
