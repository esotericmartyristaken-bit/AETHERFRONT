using System;
using System.Collections.Generic;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Tracking;
using HarmonyLib;
using LmpClient.Aetherfront;

internal static class Program
{
    private static int Main()
    {
        var hook = AccessTools.Method(typeof(TrackingOverlay), "Receive");
        var reply = AccessTools.Method(typeof(AetherfrontClientSystem), "ApplyReply", new[] { typeof(AetherfrontWireReply) });
        var patches = new Harmony("aetherfront.tracking.isolated-test");
        patches.Patch(reply, prefix: new HarmonyMethod(hook));
        var pending = AccessTools.Field(typeof(TrackingOverlay), "pending");
        var received = AccessTools.Field(typeof(TrackingOverlay), "received");
        var expected = Guid.NewGuid().ToString("N");
        pending.SetValue(null, expected);
        var normal = new AetherfrontWireReply { Operation = AetherfrontWireOperation.SnapshotReply, Accepted = true };
        reply.Invoke(null, new object[] { normal });
        Check(ReferenceEquals(AetherfrontClientSystem.LastReply, normal), "normal snapshot still runs");
        var tracking = new AetherfrontWireReply { Operation = AetherfrontWireOperation.SnapshotReply, Accepted = true,
            ReasonCode = Wire.Route, TradeId = Guid.NewGuid().ToString("N") };
        reply.Invoke(null, new object[] { tracking });
        Check(received.GetValue(null) == null && ReferenceEquals(AetherfrontClientSystem.LastReply, normal), "unmatched reply discarded");
        tracking.TradeId = expected;
        reply.Invoke(null, new object[] { tracking });
        Check(ReferenceEquals(received.GetValue(null), tracking) && ReferenceEquals(AetherfrontClientSystem.LastReply, normal), "matching reply isolated from gameplay state");
        pending.SetValue(null, null);
        received.SetValue(null, null);
        reply.Invoke(null, new object[] { tracking });
        Check(received.GetValue(null) == null && ReferenceEquals(AetherfrontClientSystem.LastReply, normal), "late reply after exit discarded");

        var json = "[{\"Id\":\"0123456789abcdef0123456789abcdef\",\"Name\":\"Test\",\"Body\":\"Kerbin\",\"ReferenceBody\":1,\"Surface\":false,\"Position\":[0,0.02,700000,0,0,0.4,50000]}]";
        var markers = AetherfrontJson.Deserialize<List<Marker>>(json);
        Check(markers.Count == 1 && markers[0].HasValidPosition() && markers[0].Position[5] == 0.4, "shipping client parser accepts compact wire");
        markers[0].Position[2] = double.NaN;
        Check(!markers[0].HasValidPosition(), "non-finite marker rejected");
        Console.WriteLine("6 KSP Mono client checks passed.");
        return 0;
    }

    private static void Check(bool condition, string test)
    {
        if (!condition) throw new InvalidOperationException(test);
    }
}
