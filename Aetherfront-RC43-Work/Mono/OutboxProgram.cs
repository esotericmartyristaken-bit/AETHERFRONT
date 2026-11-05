using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;
using Aetherfront.Personal.Client;
using Aetherfront.Personal.Transport;
using LmpClient;
using LmpClient.Apf;
using LmpClient.Aetherfront;

internal static class OutboxProgram
{
    private const BindingFlags Fields = BindingFlags.Static | BindingFlags.NonPublic;
    private static T Get<T>(string name) { return (T)typeof(ScienceOutbox).GetField(name, Fields).GetValue(null); }
    private static void Set(string name, object value) { typeof(ScienceOutbox).GetField(name, Fields).SetValue(null, value); }
    private static List<PendingScienceClaim> Claims { get { return Get<PendingScienceClaims>("pending").Claims; } }
    private static string PathName { get { return Path.Combine(MonoGate.CaseRoot, "APF", "PendingScience-v1.json"); } }
    private static AetherfrontWireRequest Last { get { return PersonalClient.Sends.Last(); } }
    private static PendingScienceClaim Claim(decimal amount = 10, int command = 40)
    {
        return new PendingScienceClaim { ServerId = "fixture-server", AccountId = "fixture-account", Command = command, SourceId = command == 42 ? "award:1234567890abcdef1234567890abcdef" : "science:legacy", Amount = amount, Reason = command == 42 ? 2 : 0 };
    }
    private static void Write(string text) { MonoGate.RequirePrivate(PathName); Directory.CreateDirectory(Path.GetDirectoryName(PathName)); File.WriteAllText(PathName, text); }
    private static void WriteClaims(params PendingScienceClaim[] claims) { Write(AetherfrontJson.Serialize(new PendingScienceClaims { Claims = claims.ToList() })); }
    private static PendingScienceClaims Read() { return AetherfrontJson.Deserialize<PendingScienceClaims>(File.ReadAllText(PathName)); }
    private static void Tick(float time) { UnityEngine.Time.realtimeSinceStartup = time; ScienceOutbox.Tick(); }
    private static void Restart()
    {
        Set("pending", null); Set("dirty", false); Set("failed", false); Set("cursor", 0); Set("nextSend", 0f); Set("nextSave", 0f); Set("path", null); Set("wantedRevision", 0L); Set("nextSnapshot", 0f); Set("<Warning>k__BackingField", null);
        Set("activeServer", null); Set("activeAccount", null);
        Get<IDictionary>("flights").Clear(); var q = Get<object>("receipts"); q.GetType().GetMethod("Clear").Invoke(q, null);
    }
    private static void Reset()
    {
        Restart(); KSPUtil.ApplicationRootPath = MonoGate.CaseRoot; HighLogic.CurrentGame = new Game { Mode = Game.Modes.CAREER };
        MainSystem.UniqueIdentifier = "fixture-account"; ApfClientSettings.ClientVersion = "fixture-rc43"; PersonalClient.Active = true;
        PersonalClient.Sends.Clear(); PersonalClient.OnSend = null; PersonalClient.ObserverError = null; KesasInstall.Installer.Failures = 0; KesasInstall.Installer.Saves = 0;
        AetherfrontClientSystem.State = new StubState { ServerId = "fixture-server", Revision = 0 }; AetherfrontClientSystem.Snapshots = 0; UnityEngine.Time.realtimeSinceStartup = 0; UnityEngine.Debug.Messages.Clear();
    }
    private static void Loaded(int count)
    {
        MonoGate.Check(!Get<bool>("failed"), "Actual source Load failed: " + string.Join(" | ", UnityEngine.Debug.Messages.ToArray()));
        MonoGate.Equal(count, Claims.Count, "Actual source loaded expected evidence count");
    }
    private static void Accept(AetherfrontWireRequest r = null)
    {
        r = r ?? Last; decimal amount = decimal.Parse(r.Body, CultureInfo.InvariantCulture);
        var e = new ScienceReceiptEnvelope { ProtocolVersion = 1, Operation = 3, Accepted = true, ReasonCode = "ok", Revision = 7, ServerId = "fixture-server", ScienceReceipt = new ScienceClaimReceipt { RequestId = r.CommandId, AccountId = "fixture-account", Command = (int)r.CommandType, SourceId = r.EntityId, RequestedAmount = amount, RecordedCumulative = amount, Disposition = "accepted" } };
        MonoGate.Check(ScienceOutbox.Capture(AetherfrontJson.Serialize(e)), "Actual optional receipt consumed");
    }
    private static void Case(string name, Action body)
    {
        MonoGate.Case("outbox", name, () =>
        {
            try { body(); if (PersonalClient.ObserverError != null) throw PersonalClient.ObserverError; }
            finally { File.WriteAllText(Path.Combine(MonoGate.CaseRoot, "observations.json"), AetherfrontJson.Serialize(new { logs = UnityEngine.Debug.Messages, sends = PersonalClient.Sends, saves = KesasInstall.Installer.Saves, dirty = Get<bool>("dirty"), pending = Get<PendingScienceClaims>("pending") })); }
        }, Reset);
    }
    public static int Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Case("JsonFx-nonempty-list-runtime-compatibility", () =>
        {
            var actual = AetherfrontJson.Deserialize<PendingScienceClaims>(AetherfrontJson.Serialize(new PendingScienceClaims { Claims = new List<PendingScienceClaim> { Claim() } }));
            MonoGate.Equal(1, actual.Claims.Count, "Actual JsonFx deserializes nonempty claim list"); MonoGate.Equal(10m, actual.Claims[0].Amount, "Amount preserved");
        });
        Case("old-v1-migration-preserves-evidence", () =>
        {
            Write("{\"Version\":1,\"Claims\":[{\"ServerId\":\"fixture-server\",\"AccountId\":\"fixture-account\",\"Command\":40,\"SourceId\":\"science:legacy\",\"Amount\":12.123456,\"Reason\":0},{\"ServerId\":\"fixture-server\",\"AccountId\":\"fixture-account\",\"Command\":42,\"SourceId\":\"award:1234567890ABCDEF1234567890ABCDEF\",\"Amount\":1000001,\"Reason\":2}]}");
            Tick(0); Loaded(2); MonoGate.Equal(12.123456m, Claims[0].Amount, "Legacy amount exact"); MonoGate.Equal(0L, Claims[1].ChunkIndex, "Missing chunk defaults zero");
            MonoGate.Equal("award:1234567890abcdef1234567890abcdef", Claims[1].SourceId, "Legacy source normalized"); MonoGate.Check(!Claims[1].Held && Claims[1].Attempts == 0, "Missing retry fields safe");
            MonoGate.Equal(1, Read().Claims[0].Attempts, "Migrated evidence saved before transport");
        });
        Case("actual-Core-decimal-six-place-roundtrip", () =>
        {
            WriteClaims(Claim(123456.123456m), Claim(1000001.000001m, 42)); Tick(0); Loaded(2);
            MonoGate.Equal(123456.123456m, Claims[0].Amount, "Subject decimal exact"); MonoGate.Equal(1000001.000001m, Claims[1].Amount, "Award decimal exact");
            MonoGate.Equal(1000001.000001m, Read().Claims[1].Amount, "Persisted decimal exact");
        });
        Case("attempt-save-failure-after-initial-persist", () =>
        {
            ScienceOutbox.Enqueue(Claim()); ScienceOutbox.Save(true); string old = File.ReadAllText(PathName); KesasInstall.Installer.Failures = 1; Tick(0);
            MonoGate.Equal(0, PersonalClient.Sends.Count, "Failed retry-state save blocks send"); MonoGate.Equal(old, File.ReadAllText(PathName), "Failed save preserves disk evidence");
            Restart(); Tick(0); Loaded(1); MonoGate.Equal(1, PersonalClient.Sends.Count, "Restart resumes preserved claim"); MonoGate.Equal(1, Read().Claims[0].Attempts, "Restart persisted retry attempt");
        });
        Case("receipt-retirement-save-failure-restart-recovers", () =>
        {
            ScienceOutbox.Enqueue(Claim()); Tick(0); var old = Last; KesasInstall.Installer.Failures = 1; Accept(); Tick(.1f);
            MonoGate.Equal(0, Claims.Count, "Accepted receipt retires in memory"); MonoGate.Equal(1, Read().Claims.Count, "Failed retirement save leaves old disk evidence");
            Restart(); Tick(0); Loaded(1); MonoGate.Equal(1, PersonalClient.Sends.Count, "Persisted deadline still blocks immediate resend");
            Claims[0].NextAttemptUtcTicks = 0; Tick(1); MonoGate.Equal(2, PersonalClient.Sends.Count, "Deadline seam enables safe restart retry"); MonoGate.Equal(old.EntityId, Last.EntityId, "Stable source after restart"); MonoGate.Check(old.CommandId != Last.CommandId, "Fresh nonce after restart");
        });
        Case("award-chunk-progression-and-restart", () =>
        {
            ScienceOutbox.Enqueue(Claim(2000000.123456m, 42)); Tick(0); string firstSource = Last.EntityId; MonoGate.Equal("1000000", Last.Body, "First chunk capped"); Accept(); Tick(.1f);
            MonoGate.Equal(1L, Read().Claims[0].ChunkIndex, "Acknowledged index persisted"); MonoGate.Equal(1000000.123456m, Read().Claims[0].Amount, "Remainder exact"); string secondSource = Claims[0].GetChunkSourceId();
            Restart(); Tick(0); Loaded(1); MonoGate.Equal(secondSource, Last.EntityId, "Derived chunk source stable through full outbox reload"); MonoGate.Check(firstSource != secondSource, "Chunk IDs distinct");
            Accept(); Tick(.1f); MonoGate.Equal(2L, Claims[0].ChunkIndex, "Second chunk advances once"); MonoGate.Equal(.123456m, Claims[0].Amount, "Final remainder exact");
            Tick(1); MonoGate.Equal("0.123456", Last.Body, "Final chunk amount exact"); Accept(); Tick(1.1f); MonoGate.Equal(0, Read().Claims.Count, "Final retirement durable");
        });
        Case("held-same-release-remains-held", () =>
        {
            var c = Claim(); c.Held = true; c.HeldVersion = ApfClientSettings.ClientVersion; c.LastRejection = "fixture-denied"; WriteClaims(c); Tick(0); Loaded(1);
            MonoGate.Equal(1, ScienceOutbox.HeldCount, "Same release remains held"); MonoGate.Equal(0, PersonalClient.Sends.Count, "Held never silently sent");
            Restart(); Tick(0); Loaded(1); MonoGate.Equal("fixture-denied", Claims[0].LastRejection, "Held evidence/reason survive reload"); MonoGate.Equal(1, ScienceOutbox.HeldCount, "Held survives restart");
        });
        Case("held-new-release-resumes-evidence", () =>
        {
            var c = Claim(); c.Held = true; c.HeldVersion = "fixture-old"; c.LastRejection = "fixture-denied"; c.Attempts = 30; c.NextAttemptUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks; WriteClaims(c);
            Tick(0); Loaded(1); MonoGate.Check(!Claims[0].Held, "New release unholds evidence"); MonoGate.Equal(1, Claims[0].Attempts, "Attempts reset before retry");
            MonoGate.Equal("", Claims[0].LastRejection, "Old rejection cleared"); MonoGate.Equal(1, PersonalClient.Sends.Count, "New release retries"); MonoGate.Check(!Read().Claims[0].Held, "Release reset persisted before send");
        });
        Case("load-future-retry-clamped-to-60-seconds", () =>
        {
            var c = Claim(); c.NextAttemptUtcTicks = DateTime.MaxValue.Ticks; WriteClaims(c); long now = DateTime.UtcNow.Ticks; Tick(0); Loaded(1);
            MonoGate.Check(Claims[0].NextAttemptUtcTicks >= now + 59 * TimeSpan.TicksPerSecond && Claims[0].NextAttemptUtcTicks <= DateTime.UtcNow.AddSeconds(60).Ticks, "Loaded future deadline clamped to 60 seconds");
            MonoGate.Equal(0, PersonalClient.Sends.Count, "Future retry not sent"); MonoGate.Equal(Claims[0].NextAttemptUtcTicks, Read().Claims[0].NextAttemptUtcTicks, "Clamped deadline persisted");
        });
        Case("persist-before-send-backoff-survives-reload", () =>
        {
            var c = Claim(); ScienceOutbox.Enqueue(c); bool observed = false;
            PersonalClient.OnSend = r => { var saved = Read().Claims.Single(); MonoGate.Equal(1, saved.Attempts, "Attempt persisted before send"); MonoGate.Check(saved.NextAttemptUtcTicks > DateTime.UtcNow.Ticks, "UTC deadline persisted before send"); observed = true; };
            Tick(0); MonoGate.Check(observed, "Send observer executed"); long deadline = c.NextAttemptUtcTicks; PersonalClient.OnSend = null;
            Restart(); Tick(0); Loaded(1); MonoGate.Equal(deadline, Claims[0].NextAttemptUtcTicks, "Exact backoff deadline survives reload"); MonoGate.Equal(1, PersonalClient.Sends.Count, "Reload cannot bypass individual backoff");
            Claims[0].NextAttemptUtcTicks = 0; Tick(1); MonoGate.Equal(2, Claims[0].Attempts, "Retry resumes attempt progression"); MonoGate.Equal(2, Read().Claims[0].Attempts, "Second attempt persisted");
        });
        return MonoGate.Finish("outbox");
    }
}
