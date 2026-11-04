# Reliable sender pacing

## Integration contract

The public hook is:

```csharp
bool Aetherfront.Pacing.ClientPacing.WaitForCapacity(
    LmpCommon.Message.Interface.IMessageBase message,
    System.Threading.CancellationToken cancellationToken,
    int generation)
```

Call it inside the shipping `NetworkSender.SendNetworkMessage(IMessageBase, CancellationToken, int)` original try region, before SentTime mutation, serialization, or CreateMessage. A false result must branch to the existing false-return block while preserving the original finally. The helper never calls Recycle, dequeues, requeues, reserves transport storage, or retries a Dropped send. The original finally remains the sole application-message owner on both success and failure.

The main release builder owns the Cecil graft and its exception-handler audit. This directory does not change that builder or any shipping transport guard.

## Policy

Only reliable connected application messages are paced. Master-server and unreliable paths return immediately, leaving their original checks unchanged. The existing sender background thread keeps one pending application message on its stack while waiting; no producer or Unity update thread is made to wait. A foreground caller that would need to wait fails closed instead.

Every poll reads the actual connection and peer aggregate retained bytes, distinct backing arrays, and reservation counts under the existing connection-then-peer gates. All transport locks are released before FlushSendQueue or the 10 ms sleep. The helper does not treat a flush or Sent/Queued result as an ACK.

The soft window leaves 8 MiB, 512 arrays, and 16,384 reservations below each applicable hard cap, including the next message's conservative cost. Its backing-array bound is max(request, min(64 MiB, max(request + 1024, 2 * request))), using 64-bit arithmetic. Fragment reservations conservatively mirror RC42 GetBestChunkSize at the largest fragment-group identifier. A single valid message larger than a soft window can use the hard budget once its entire cost fits; it does not wait forever for an impossible soft-window condition. Invalid or intrinsically impossible size estimates follow the original serializer/guard path rather than waiting.

A blocked pending job has a 30 second no-budget-progress deadline, independent of heartbeat activity. New low-water marks in relevant blocked connection/peer budgets reset this deadline; enqueue increases, churn above an old low-water mark, and unrelated peer drain do not. The original first-wait timestamp remains available for total-wait diagnostics. On expiry the existing HandleDisconnectException path runs under the reflected existing TryUseCurrentSession generation gate, then the helper returns false. Original finally recycles that pending job once. A cancelled token, reset, old generation, replaced peer/connection, disconnected state, or pending transport disconnect aborts without disconnecting a replacement session.

Counter reflection requires exact field types and the RC42 Lidgren.Network SHA-256 `b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3`. Missing/changed contracts fail closed, never disable the original hard guards. The runtime IsCurrentSession and TryUseCurrentSession calls use reflection because they are internal to LmpClient. Diagnostic lines include all six counters, next-message bounds, and MTU; logging is globally limited to one line per five seconds.

## Tests and artifacts

PacingTests uses the actual RC42 Lidgren.Network.dll and actual LmpClient message interfaces. Only client session coordination and logging are substituted. Every synthetic peer remains unstarted; assertions require no socket or network thread. A 16-message channel window keeps all simulated encoding below the actual MTU, with real channel ACK processing and sequence wrap.

Coverage includes allocation/fragment bounds, all six soft and hard budgets, large valid work, fail-closed contracts, 4,097-message FIFO delivery with ACK drain, shared-array accounting, cancellation/reset/reconnect, no mutation while blocked, sole original-finally recycling, rate-limited diagnostics, and the real 30 second blocked deadline. Hard byte limits are exercised by synthetic counters, not large heap allocations. Tests run sequentially with garbage collection between cases.

Build `PacingTests.csproj` with .NET 8. Build `PacingHelper.csproj` for the production net472 helper. Both accept ClientPlugins pointing to the RC42 plugins directory. Send BaseIntermediateOutputPath and OutputPath outside Git when building. Current artifacts are under `UpdateBuilds/ScienceTransmit-20261104/pacing/`, relative to the APF workspace. `--skip-deadline` omits only the real-time deadline case for quick reruns.

## Shipping producer findings

Shipping NetworkSender has one LongRunning send task, an unbounded ConcurrentQueue of IMessageBase, and a generation dictionary. QueueOutgoingMessage has public current-generation and internal explicit-generation overloads. It admits and resets under QueueLock, but SendMain dequeues outside that lock, removes generation metadata, serializes on the sender task, and batches 128 before flushing. It has no pending-application queue byte/count cap or per-entry expiry. Stock defaults are SendReceiveMsInterval 3 ms, heartbeat 2,000 ms, and connection timeout 15 seconds; the idle sender sleep clamps the configured interval to 1–100 ms. These are defaults, not evidence of Spacedout's local settings.

Lock and many message senders enqueue via SystemBase.TaskFactory.StartNew. Vessel senders, AetherfrontClientSystem.Send, approved-scenario capture, and DeepFreeze also call QueueOutgoingMessage directly on their respective caller threads. Application serialization and transport SendMessage remain on the single sender task. A useful bounded producer query is all call references to NetworkSender.QueueOutgoingMessage plus GetMessageSize and delivery/channel values, grouped by caller/data type; snapshot the aggregate budgets while stalled. The earlier legacy ShareScience paths remain unchanged.
