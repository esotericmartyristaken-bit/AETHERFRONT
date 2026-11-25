using System;
using System.Diagnostics;
using Aetherfront.Synchronization;

var watch = new TransportCapacity.Stall();
long At(int seconds) => seconds * Stopwatch.Frequency;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// The caller was idle for hours before it first saw a blocked queue.
Check(!watch.Blocked(At(10_000), 8_000_000, 9000, 3), "Idle time counted as a stall");
Check(!watch.Blocked(At(10_029), 8_000_000, 8000, 3), "Acknowledged fragments ignored");
Check(!watch.Blocked(At(10_058), 8_000_000, 7999, 3), "Slow native drain ignored");
Check(!watch.Blocked(At(10_060), 7_000_000, 7999, 2), "Released backing buffer ignored");
Check(!watch.Blocked(At(10_089), 8_000_000, 8200, 3), "Timed out early after actual progress");
Check(watch.Blocked(At(10_090), 7_000_000, 7999, 2), "Refill and drain to the same watermark extended the deadline");
watch.Reset();
Check(!watch.Blocked(At(20_000), 0, 0, 0), "New blocked period inherited the prior deadline");
Check(watch.Blocked(At(20_030), 0, 0, 0), "No native backlog masked a true stall");
watch.Reset();
Check(!watch.Blocked(At(30_000), long.MaxValue, int.MaxValue, int.MaxValue), "Unavailable transport immediately failed");
Check(watch.Blocked(At(30_030), long.MaxValue, int.MaxValue, int.MaxValue), "Unavailable transport bypassed timeout");
Console.WriteLine("PASS: idle, fragment drain, released buffers, churn, no-progress timeout and reset");
