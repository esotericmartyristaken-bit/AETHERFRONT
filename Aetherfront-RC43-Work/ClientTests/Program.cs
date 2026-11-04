using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Aetherfront.Core;
using Aetherfront.Personal.Client;
using Aetherfront.Personal.Transport;
using LmpClient;
using LmpClient.Aetherfront;
using LmpClient.Apf;

// No test framework packages, KSP assemblies, production client, or network transport.
// Only the four linked candidate sources and the shipped Core/JsonFx execute here.
internal static class Program
{
    private static readonly List<(string Name, Action Test)> Tests = new();
    private static readonly List<object> Results = new();
    private static int assertions;
    private static string Metadata(string key) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;

    private static int Main(string[] args)
    {
        if (args.Length > 1) throw new ArgumentException("Usage: ClientTests [case-name-substring]");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        TestEnvironment.RunRoot = Path.Combine(Metadata("FixtureRoot"), "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestEnvironment.RunRoot);
        Register();
        int passed = 0, failed = 0;
        Console.WriteLine("FIXTURES " + TestEnvironment.RunRoot);
        foreach (var (name, test) in Tests.Where(t => args.Length == 0 || t.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)))
        {
            int before = assertions;
            TestEnvironment.Reset(name);
            try
            {
                test();
                if (PersonalClient.ObserverError != null) throw new Exception("Send observer assertion failed", PersonalClient.ObserverError);
                passed++;
                Results.Add(new { name, passed = true, assertions = assertions - before });
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failed++;
                Results.Add(new { name, passed = false, assertions = assertions - before, error = error.ToString() });
                Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message);
            }
            finally
            {
                TestEnvironment.Write("observations.json", JsonSerializer.Serialize(new
                {
                    saves = KesasInstall.Installer.SaveAttempts,
                    successfulSaves = KesasInstall.Installer.SuccessfulSaves,
                    sends = PersonalClient.Sends.Select(s => new { s.Realtime, s.Json }),
                    snapshots = AetherfrontClientSystem.SnapshotTimes,
                    logs = UnityEngine.Debug.Messages
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        var sourceRoot = Path.GetFullPath(Metadata("SourceRoot"));
        var inputs = new[] { "Client/ScienceOutbox.cs", "Client/ScienceClaim.cs", "Client/ScienceRetryPolicy.cs", "Shared/ScienceReceipt.cs" }
            .Select(p => Path.Combine(sourceRoot, p)).Concat(new[] { "AetherfrontCore.dll", "JsonFx.dll" }.Select(p => Path.Combine(Metadata("ClientPlugins"), p)))
            .Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray();
        File.WriteAllText(Path.Combine(TestEnvironment.RunRoot, "results.json"), JsonSerializer.Serialize(new
        {
            passed, failed, assertions, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            inputs, results = Results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT passed={passed} failed={failed} assertions={assertions} registered={Tests.Count}");
        return failed == 0 && passed > 0 ? 0 : 1;
    }

    private static void Add(string name, Action test) => Tests.Add((name, test));
    internal static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), message + $"; expected={expected}; actual={actual}");
    private static void Throws<T>(Action action) where T : Exception
    {
        assertions++;
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static PendingScienceClaim Claim(string source = "science:test-source", decimal amount = 10m, int command = 40) => new()
    {
        ServerId = "fixture-server", AccountId = "fixture-account", SourceId = source, Amount = amount, Command = command,
        Reason = command == 42 ? PersonalProgression.ScienceRewardReasons & -PersonalProgression.ScienceRewardReasons : 0
    };
    private static PendingScienceClaim Award(decimal amount = 10m) => Claim("award:1234567890abcdef1234567890abcdef", amount, 42);
    private static List<PendingScienceClaim> Claims => TestEnvironment.Get<PendingScienceClaims>("pending")?.Claims ?? new();
    private static AetherfrontWireRequest Last => PersonalClient.Sends.Last().Request;
    private static void Tick(float realtime) { UnityEngine.Time.realtimeSinceStartup = realtime; ScienceOutbox.Tick(); }
    private static void Enqueue(PendingScienceClaim c) => ScienceOutbox.Enqueue(c);
    private static void Persist() => ScienceOutbox.Save(true);
    private static void WriteClaims(IEnumerable<PendingScienceClaim> claims, int version = 1) => TestEnvironment.WriteOutbox(AetherfrontJson.Serialize(new PendingScienceClaims { Version = version, Claims = claims.ToList() }));
    // Independent oracle for bytes written by actual Core/JsonFx. Production Load
    // still uses the unmodified Core reader; its runtime compatibility is tested separately.
    private static PendingScienceClaims ReadSaved() => JsonSerializer.Deserialize<PendingScienceClaims>(File.ReadAllText(TestEnvironment.OutboxPath),
        new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString });
    private static ScienceReceiptEnvelope Receipt(AetherfrontWireRequest request = null, string disposition = "accepted", decimal? recorded = null, long revision = 1)
    {
        request ??= Last;
        decimal amount = decimal.Parse(request.Body, CultureInfo.InvariantCulture);
        return new ScienceReceiptEnvelope
        {
            ProtocolVersion = 1, Operation = (int)AetherfrontWireOperation.SnapshotReply, Accepted = disposition == "accepted",
            ReasonCode = disposition == "accepted" ? "ok" : "fixture-rejection", ServerId = "fixture-server", Revision = revision,
            ScienceReceipt = new ScienceClaimReceipt
            {
                RequestId = request.CommandId, AccountId = "fixture-account", Command = (int)request.CommandType,
                SourceId = request.EntityId, RequestedAmount = amount, RecordedCumulative = recorded ?? amount, Disposition = disposition
            }
        };
    }
    private static bool Capture(ScienceReceiptEnvelope receipt) => ScienceOutbox.Capture(AetherfrontJson.Serialize(receipt));
    private static void Restart() => TestEnvironment.ResetStatics();
    private static void ExpireFlights()
    {
        foreach (var f in TestEnvironment.Flights.Values)
            TestEnvironment.SetFlightSent(f, DateTime.UtcNow.AddSeconds(-ScienceRetryPolicy.ReceiptTimeoutSeconds - 1).Ticks);
    }
    private static void Ready(PendingScienceClaim c) => c.NextAttemptUtcTicks = 0;

    private static void Register()
    {
        Add("protocol-optional-receipt-actual-Core-JsonFx", ProtocolCompatibility);
        Add("JsonFx-nonempty-list-runtime-compatibility", () =>
        {
            string json = AetherfrontJson.Serialize(new PendingScienceClaims { Claims = new() { Claim() } });
            var actual = AetherfrontJson.Deserialize<PendingScienceClaims>(json);
            Equal(1, actual.Claims.Count, "Actual Core reader must load a nonempty pending list");
        });
        Add("old-v1-migration-preserves-evidence", OldMigration);
        Add("actual-Core-decimal-six-place-roundtrip", () =>
        {
            WriteClaims(new[] { Claim(amount: 123456.123456m), Award(1000001.000001m) });
            Tick(0);
            Equal(2, Claims.Count, "Both old records loaded");
            Equal(123456.123456m, Claims[0].Amount, "Decimal precision survives actual JsonFx");
            Equal(1000001.000001m, Claims[1].Amount, "Award precision survives actual JsonFx");
        });
        foreach (decimal amount in new[] { .000001m, 1.123456m, PersonalProgression.MaxScience })
        {
            decimal value = amount;
            Add("valid-cap-precision-" + value.ToString(CultureInfo.InvariantCulture), () =>
            {
                Enqueue(Claim(amount: value)); Tick(0);
                Equal(1, PersonalClient.Sends.Count, "Boundary claim sent");
                Equal(value, decimal.Parse(Last.Body, CultureInfo.InvariantCulture), "Exact wire amount");
            });
        }
        var invalid = new List<(string Name, Action<PendingScienceClaim> Change)>
        {
            ("zero", c => c.Amount = 0), ("negative", c => c.Amount = -1),
            ("over-cap", c => c.Amount = PersonalProgression.MaxScience + .000001m),
            ("seven-places", c => c.Amount = 1.0000001m), ("sub-precision", c => c.Amount = .0000001m),
            ("unsupported-command", c => c.Command = 41), ("source-empty", c => c.SourceId = ""),
            ("source-control", c => c.SourceId = "bad\nsource"), ("source-long", c => c.SourceId = new string('x', 257)),
            ("server-empty", c => c.ServerId = ""), ("server-long", c => c.ServerId = new string('x', 257)),
            ("account-empty", c => c.AccountId = ""), ("account-long", c => c.AccountId = new string('x', 257)),
            ("chunk-negative", c => c.ChunkIndex = -1), ("chunk-over-cap", c => c.ChunkIndex = 1000001),
            ("attempt-negative", c => c.Attempts = -1), ("attempt-over-cap", c => c.Attempts = 31),
            ("retry-negative", c => c.NextAttemptUtcTicks = -1), ("retry-over-date", c => c.NextAttemptUtcTicks = long.MaxValue),
            ("rejection-control", c => c.LastRejection = "bad\n"), ("rejection-long", c => c.LastRejection = new string('x', 129)),
            ("version-control", c => c.HeldVersion = "bad\n"), ("version-long", c => c.HeldVersion = new string('x', 65)),
            ("award-not-guid", c => { c.Command = 42; c.SourceId = "award:bad"; }),
            ("award-empty-guid", c => { c.Command = 42; c.SourceId = "award:" + new string('0', 32); }),
            ("award-reason-negative", c => { c.Command = 42; c.SourceId = Award().SourceId; c.Reason = -1; }),
            ("award-reason-invalid", c => { c.Command = 42; c.SourceId = Award().SourceId; c.Reason = long.MaxValue; })
        };
        foreach (var row in invalid)
        {
            var item = row;
            Add("enqueue-reject-" + item.Name, () =>
            {
                var c = Claim(); item.Change(c); Enqueue(c); Tick(0); Persist();
                Equal(0, Claims.Count, "Invalid claim not queued"); Equal(0, PersonalClient.Sends.Count, "Invalid claim not sent");
            });
            Add("load-preserve-invalid-" + item.Name, () =>
            {
                var c = Claim(); item.Change(c); WriteClaims(new[] { c });
                byte[] bytes = File.ReadAllBytes(TestEnvironment.OutboxPath); Tick(0); Persist();
                Equal(0, PersonalClient.Sends.Count, "Invalid saved evidence not sent");
                Check(bytes.SequenceEqual(File.ReadAllBytes(TestEnvironment.OutboxPath)), "Invalid saved evidence unchanged");
                Check(!string.IsNullOrEmpty(ScienceOutbox.Warning), "Invalid saved evidence visible warning");
            });
        }
        foreach (string raw in new[] { "{", "null", "{\"Version\":2,\"Claims\":[]}", "{\"Version\":1,\"Claims\":null}", "{\"Version\":1,\"Claims\":[null]}" })
        {
            string json = raw;
            Add("load-malformed-" + Tests.Count, () =>
            {
                TestEnvironment.WriteOutbox(json); Tick(0); Persist();
                Equal(json, File.ReadAllText(TestEnvironment.OutboxPath), "Corrupt evidence not overwritten");
                Equal(0, PersonalClient.Sends.Count, "Corrupt evidence not sent"); Check(ScienceOutbox.Warning != null, "Corrupt evidence warning");
            });
        }
        Add("load-four-MiB-bound-preserves-file", () =>
        {
            string raw = new string(' ', 4 * 1024 * 1024 + 1); TestEnvironment.WriteOutbox(raw); Tick(0);
            Equal(0, PersonalClient.Sends.Count, "Oversized evidence not sent"); Equal(raw.Length, (int)new FileInfo(TestEnvironment.OutboxPath).Length, "Oversized evidence retained");
            Check(ScienceOutbox.Warning != null, "Oversized evidence warning");
        });
        Add("persist-before-Send-includes-retry-state", PersistBeforeSend);
        Add("disk-failure-never-sends-and-recovers", DiskFailure);
        Add("attempt-save-failure-after-initial-persist", AttemptSaveFailure);
        Add("receipt-retirement-save-failure-restart-recovers", RetirementSaveFailure);
        Add("global-one-Hz-and-two-in-flight", Pacing);
        Add("individual-backoff-2-4-8-16-32-60", Backoff);
        Add("realtime-and-UTC-ignore-TimeWarp", NoTimeWarp);
        Add("send-throw-preserves-evidence-fresh-retry", SendFailure);
        Add("fresh-nonce-and-stale-receipt-rejected", FreshNonce);
        var mismatches = new List<(string Name, Action<ScienceReceiptEnvelope> Change)>
        {
            ("server", e => e.ServerId = "other-server"), ("account", e => e.ScienceReceipt.AccountId = "other-account"),
            ("source", e => e.ScienceReceipt.SourceId = "other-source"), ("command", e => e.ScienceReceipt.Command = 42),
            ("amount", e => e.ScienceReceipt.RequestedAmount += .000001m),
            ("nonce", e => e.ScienceReceipt.RequestId = "science:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        };
        foreach (var row in mismatches)
        {
            var item = row;
            Add("receipt-correlation-reject-" + item.Name, () =>
            {
                Enqueue(Claim()); Tick(0); var e = Receipt(); item.Change(e);
                Check(Capture(e), "Structurally valid optional receipt consumed"); Tick(.5f);
                Equal(1, Claims.Count, "Mismatched receipt cannot retire evidence"); Equal(1, TestEnvironment.Flights.Count, "Mismatched receipt cannot consume flight");
                Equal(0, AetherfrontClientSystem.SnapshotTimes.Count, "Mismatched receipt no snapshot");
            });
        }
        RegisterMalformedReceipts();
        Add("accepted-retires-only-covered-source", CoveredOnly);
        Add("snapshot-cumulative-retires-only-covered-source", SnapshotCoveredOnly);
        Add("newer-amount-preserves-older-flight", NewerAmount);
        Add("award-immutable-duplicate-does-not-rewrite-chunk", ImmutableAward);
        Add("award-chunk-progression-and-restart", AwardProgression);
        Add("award-model-exact-chunks-actual-Core-record-restart", AwardModel);
        Add("terminal-held-persisted-does-not-starve", HeldDoesNotStarve);
        Add("held-retry-user-scoped-to-current-account", RetryUser);
        Add("held-same-release-remains-held", SameRelease);
        Add("held-new-release-resumes-evidence", NewRelease);
        Add("retry-disposition-keeps-individual-backoff", RetryDisposition);
        Add("reconnect-old-receipt-ignored", Reconnect);
        Add("cross-account-and-server-queued-evidence-isolated", ScopeIsolation);
        Add("sandbox-and-no-game-clear-flight-not-evidence", Sandbox);
        Add("receipt-queue-bound-64-keeps-newest", QueueBound);
        Add("receipt-queue-concurrent-capture-bound", ConcurrentCapture);
        Add("accepted-snapshot-coalesces-until-revision-catches-up", SnapshotCoalescing);
        Add("pending-cap-4096-retains-existing", PendingCap);
        Add("load-over-4096-preserves-evidence", () =>
        {
            WriteClaims(Enumerable.Range(0, 4097).Select(i => Claim("science:" + i))); var before = File.ReadAllBytes(TestEnvironment.OutboxPath); Tick(0);
            Equal(0, PersonalClient.Sends.Count, "Over-cap outbox blocked"); Check(before.SequenceEqual(File.ReadAllBytes(TestEnvironment.OutboxPath)), "Over-cap evidence unchanged");
        });
        Add("load-future-retry-clamped-to-60-seconds", () =>
        {
            var c = Claim(); c.NextAttemptUtcTicks = DateTime.MaxValue.Ticks; WriteClaims(new[] { c }); long before = DateTime.UtcNow.Ticks; Tick(0);
            Check(Claims.Single().NextAttemptUtcTicks >= before + 59 * TimeSpan.TicksPerSecond, "Future retry not made immediately due");
            Check(Claims.Single().NextAttemptUtcTicks <= DateTime.UtcNow.AddSeconds(60).Ticks, "Future retry clamped"); Equal(0, PersonalClient.Sends.Count, "Clamped retry not yet sent");
        });
        Add("policy-integer-overflow-and-DateTime-boundaries", PolicyBoundaries);
        Add("policy-coverage-tolerance-and-exact-awards", CoverageTolerance);
        Add("cursor-int-max-wrap-and-20000-ticks-fairness", CursorLongRun);
    }

    private static void ProtocolCompatibility()
    {
        string pluginRoot = Metadata("ClientPlugins");
        Check(SHA256.HashData(File.ReadAllBytes(typeof(AetherfrontProtocol).Assembly.Location)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(pluginRoot, "AetherfrontCore.dll")))), "Actual shipped Core bytes loaded");
        var jsonFx = Assembly.Load("JsonFx");
        Check(SHA256.HashData(File.ReadAllBytes(jsonFx.Location)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(pluginRoot, "JsonFx.dll")))), "Actual shipped JsonFx bytes loaded");
        var reply = new AetherfrontWireReply { ProtocolVersion = 1, Operation = AetherfrontWireOperation.SnapshotReply, Accepted = true, ReasonCode = "ok", Revision = 37, ServerId = "fixture-server", StateJson = "{}" };
        string basic = AetherfrontProtocol.Serialize(reply);
        var e = Receipt(new AetherfrontWireRequest { CommandId = "science:1234567890abcdef1234567890abcdef", EntityId = "science:test-source", CommandType = (AetherfrontCommandType)40, Body = "1.123456" });
        string dto = AetherfrontJson.Serialize(e.ScienceReceipt);
        foreach (string optional in new[] { dto, "null", "42", "\"unrecognized\"", "{\"FutureField\":[1,2,3]}" })
        {
            string augmented = basic.TrimEnd()[..^1] + ",\"ScienceReceipt\":" + optional + "}";
            var actual = AetherfrontProtocol.DeserializeReply(augmented);
            foreach (var p in typeof(AetherfrontWireReply).GetProperties()) Equal(p.GetValue(reply), p.GetValue(actual), "Optional DTO preserves Core field " + p.Name);
        }
        TestEnvironment.Write("optional-receipt.json", basic.TrimEnd()[..^1] + ",\"ScienceReceipt\":" + dto + "}");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => new[] { "LmpClient", "Assembly-CSharp", "UnityEngine", "Lidgren.Network", "KesasInstall" }.Contains(a.GetName().Name)), "No production client, KSP, installer or transport loaded");
    }

    private static void OldMigration()
    {
        string old = "{\"Version\":1,\"Claims\":[{\"ServerId\":\"fixture-server\",\"AccountId\":\"fixture-account\",\"Command\":40,\"SourceId\":\"science:legacy\",\"Amount\":12.123456,\"Reason\":0},{\"ServerId\":\"fixture-server\",\"AccountId\":\"fixture-account\",\"Command\":42,\"SourceId\":\"award:1234567890ABCDEF1234567890ABCDEF\",\"Amount\":1000001,\"Reason\":2}]}";
        TestEnvironment.WriteOutbox(old); Tick(0);
        Equal(2, Claims.Count, "Both legacy records remain pending until receipt");
        Equal(12.123456m, Claims[0].Amount, "Legacy amount preserved"); Equal(0L, Claims[1].ChunkIndex, "Missing chunk index defaults zero");
        Equal("award:1234567890abcdef1234567890abcdef", Claims[1].SourceId, "Award GUID normalized");
        Check(!Claims[1].Held && Claims[1].Attempts == 0, "Missing held/retry fields default safely");
        var saved = ReadSaved(); Equal(2, saved.Claims.Count, "Migration persisted before send"); Equal(1, saved.Claims[0].Attempts, "Retry state persisted");
        Equal(1, saved.Version, "Existing v1 path/schema retained");
    }

    private static void PersistBeforeSend()
    {
        Enqueue(Claim(amount: 17.123456m)); bool observed = false;
        PersonalClient.OnSend = request =>
        {
            var c = ReadSaved().Claims.Single();
            Equal(17.123456m, c.Amount, "Evidence already on disk at send"); Equal(1, c.Attempts, "Attempt saved before send");
            Check(c.NextAttemptUtcTicks > DateTime.UtcNow.Ticks, "Individual retry deadline saved before send");
            Check(KesasInstall.Installer.SuccessfulSaves >= 2, "Initial and retry evidence persisted"); observed = true;
        };
        Tick(0); Check(observed, "Send observer executed"); Equal(1, PersonalClient.Sends.Count, "Exactly one send");
        Equal(1, Claims.Count, "Sending is not confirmation");
    }
    private static void DiskFailure()
    {
        Enqueue(Claim()); KesasInstall.Installer.FailuresRemaining = 1; Tick(0);
        Equal(0, PersonalClient.Sends.Count, "Disk failure prevents send"); Check(TestEnvironment.Get<bool>("dirty"), "Unsaved evidence stays dirty");
        Check(ScienceOutbox.Warning != null, "Disk failure visible"); Equal(1, Claims.Count, "Unsaved evidence retained in memory");
        Tick(4.99f); Equal(0, PersonalClient.Sends.Count, "Save retry gate honored");
        Tick(5); Equal(1, PersonalClient.Sends.Count, "Recovered storage enables send"); Check(ScienceOutbox.Warning == null, "Recovery clears warning");
        Equal(1, ReadSaved().Claims.Count, "Recovered evidence persisted");
    }
    private static void AttemptSaveFailure()
    {
        Enqueue(Claim()); Persist(); var original = File.ReadAllBytes(TestEnvironment.OutboxPath);
        KesasInstall.Installer.FailuresRemaining = 1; Tick(0);
        Equal(0, PersonalClient.Sends.Count, "Failed forced retry-state save blocks transport");
        Equal(1, Claims.Single().Attempts, "Attempt retained for recovery"); Check(original.SequenceEqual(File.ReadAllBytes(TestEnvironment.OutboxPath)), "Failed atomic save leaves previous evidence intact");
        Restart(); Tick(0); Equal(1, PersonalClient.Sends.Count, "Restart safely resends preserved evidence");
    }
    private static void RetirementSaveFailure()
    {
        Enqueue(Claim()); Tick(0); var old = Last; KesasInstall.Installer.FailuresRemaining = 1; Check(Capture(Receipt()), "Receipt consumed"); Tick(.5f);
        Equal(0, Claims.Count, "Accepted receipt removes in-memory claim"); Equal(1, ReadSaved().Claims.Count, "Failed save retains disk evidence");
        Restart(); Tick(0); Equal(2, PersonalClient.Sends.Count, "Restart safely retries same cumulative source");
        Equal(old.EntityId, Last.EntityId, "Restart cannot invent another source"); Check(old.CommandId != Last.CommandId, "Restart uses fresh correlation nonce");
    }
    private static void Pacing()
    {
        for (int i = 0; i < 4; i++) Enqueue(Claim("science:" + i));
        Tick(0); Tick(0); Tick(.25f); Tick(.999f); Equal(1, PersonalClient.Sends.Count, "Global one Hz blocks intermediate ticks");
        Tick(1); Equal(2, PersonalClient.Sends.Count, "Second send at one second"); Equal(2, TestEnvironment.Flights.Count, "Two flights maximum");
        Tick(2); Tick(10); Equal(2, PersonalClient.Sends.Count, "Full flight window blocks sends");
        Capture(Receipt(PersonalClient.Sends[0].Request)); Tick(10); Equal(3, PersonalClient.Sends.Count, "Receipt opens one slot");
        Equal(2, TestEnvironment.Flights.Count, "Flight cap retained after receipt"); Tick(10.999f); Equal(3, PersonalClient.Sends.Count, "No burst after receipt");
        for (int i = 1; i < PersonalClient.Sends.Count; i++) Check(PersonalClient.Sends[i].Realtime - PersonalClient.Sends[i - 1].Realtime >= 1f, "Global send spacing >= 1 second");
    }
    private static void Backoff()
    {
        var a = Claim("science:a"); var b = Claim("science:b"); long now = DateTime.UtcNow.Ticks;
        foreach (int delay in new[] { 2, 4, 8, 16, 32, 60, 60, 60 })
        {
            Equal(now + delay * TimeSpan.TicksPerSecond, ScienceRetryPolicy.Schedule(a, now), "Exponential capped individual delay");
            Equal(0, b.Attempts, "Other claim attempt unaffected"); Equal(0L, b.NextAttemptUtcTicks, "Other claim deadline unaffected");
        }
        Enqueue(a); Enqueue(b); Tick(0); Equal("science:b", Last.EntityId, "Backed-off claim does not starve other source");
        for (int i = 0; i < 1000; i++) ScienceRetryPolicy.Schedule(a, now);
        Equal(ScienceRetryPolicy.MaximumAttempts, a.Attempts, "Attempt counter saturates"); Equal(now + 60 * TimeSpan.TicksPerSecond, a.NextAttemptUtcTicks, "Saturated counter still retries after 60 seconds");
    }
    private static void NoTimeWarp()
    {
        var a = Claim("science:a"); var b = Claim("science:b"); Enqueue(a); Enqueue(b);
        UnityEngine.Time.timeScale = 0; UnityEngine.Time.time = 1e20f; Tick(0); Tick(.99f); Equal(1, PersonalClient.Sends.Count, "Scaled time cannot advance send gate");
        UnityEngine.Time.timeScale = 100000; UnityEngine.Time.time = 0; Tick(1); Equal(2, PersonalClient.Sends.Count, "Realtime alone advances send gate");
        ExpireFlights(); UnityEngine.Time.time = 1e30f; Tick(1000);
        Equal(2, PersonalClient.Sends.Count, "TimeWarp/realtime cannot bypass UTC individual deadline");
        Ready(a); Tick(1001); Equal(3, PersonalClient.Sends.Count, "UTC seam enables retry without sleeping");
    }
    private static void SendFailure()
    {
        var c = Claim(); Enqueue(c); PersonalClient.ThrowOnSend = true; Tick(0);
        Equal(0, TestEnvironment.Flights.Count, "Thrown send releases flight"); Equal(1, ReadSaved().Claims.Count, "Thrown send preserves disk evidence");
        Equal(1, c.Attempts, "Thrown send keeps individual backoff"); string first = Last.CommandId;
        PersonalClient.ThrowOnSend = false; Ready(c); Tick(1); Equal(2, PersonalClient.Sends.Count, "Retry after throw"); Check(first != Last.CommandId, "Retry after throw gets fresh nonce");
    }
    private static void FreshNonce()
    {
        var c = Claim(); Enqueue(c); Tick(0); var old = Last; ExpireFlights(); Ready(c); Tick(1);
        Check(old.CommandId != Last.CommandId, "Retry generates fresh nonce"); Check(Guid.TryParseExact(Last.CommandId[8..], "N", out var id) && id != Guid.Empty, "Nonce GUID format");
        Capture(Receipt(old)); Tick(1.1f); Equal(1, Claims.Count, "Expired receipt ignored"); Equal(1, TestEnvironment.Flights.Count, "Old receipt cannot consume new flight");
        Capture(Receipt()); Tick(1.2f); Equal(0, Claims.Count, "Current receipt retires claim");
    }

    private static void RegisterMalformedReceipts()
    {
        var rows = new List<(string Name, Action<ScienceReceiptEnvelope> Change)>
        {
            ("protocol", e => e.ProtocolVersion = 2), ("operation", e => e.Operation = (int)AetherfrontWireOperation.CommandReply),
            ("server-null", e => e.ServerId = null), ("server-long", e => e.ServerId = new string('s', 257)),
            ("receipt-null", e => e.ScienceReceipt = null), ("nonce-null", e => e.ScienceReceipt.RequestId = null),
            ("nonce-empty-guid", e => e.ScienceReceipt.RequestId = "science:" + new string('0', 32)),
            ("nonce-invalid-guid", e => e.ScienceReceipt.RequestId = "science:" + new string('z', 32)),
            ("nonce-short", e => e.ScienceReceipt.RequestId = "science:1"), ("nonce-prefix", e => e.ScienceReceipt.RequestId = "invalid:" + new string('1', 32)),
            ("account-null", e => e.ScienceReceipt.AccountId = null), ("account-long", e => e.ScienceReceipt.AccountId = new string('a', 257)),
            ("source-null", e => e.ScienceReceipt.SourceId = null), ("source-control", e => e.ScienceReceipt.SourceId = "bad\n"),
            ("source-long", e => e.ScienceReceipt.SourceId = new string('s', 257)), ("command", e => e.ScienceReceipt.Command = 41),
            ("requested-zero", e => e.ScienceReceipt.RequestedAmount = 0), ("requested-negative", e => e.ScienceReceipt.RequestedAmount = -1),
            ("requested-over-cap", e => e.ScienceReceipt.RequestedAmount = PersonalProgression.MaxScience + 1),
            ("recorded-negative", e => e.ScienceReceipt.RecordedCumulative = -1),
            ("recorded-over-cap", e => e.ScienceReceipt.RecordedCumulative = PersonalProgression.MaxScience + 1),
            ("reason-null", e => e.ReasonCode = null), ("reason-control", e => e.ReasonCode = "bad\n"),
            ("reason-long", e => e.ReasonCode = new string('r', 129)), ("unknown-disposition", e => e.ScienceReceipt.Disposition = "future"),
            ("old-confirmed-disposition", e => e.ScienceReceipt.Disposition = "confirmed"),
            ("old-held-disposition", e => { e.ScienceReceipt.Disposition = "held"; e.Accepted = false; }),
            ("accepted-false", e => e.Accepted = false),
            ("terminal-accepted", e => e.ScienceReceipt.Disposition = "terminal"),
            ("retry-accepted", e => e.ScienceReceipt.Disposition = "retry")
        };
        foreach (var row in rows)
        {
            var item = row;
            Add("malformed-receipt-reject-" + item.Name, () =>
            {
                Enqueue(Claim()); Tick(0); var e = Receipt(); item.Change(e);
                Check(!Capture(e), "Malformed optional receipt not consumed"); Equal(0, TestEnvironment.ReceiptCount, "Malformed receipt not queued");
                Tick(.1f); Equal(1, Claims.Count, "Malformed receipt cannot retire claim"); Equal(1, TestEnvironment.Flights.Count, "Malformed receipt preserves flight");
            });
        }
        foreach (string raw in new[] { "", "{", "{}", "null", "{\"ScienceReceipt\":42}", "{\"ScienceReceipt\":[]}", "{\"ScienceReceipt\":{\"RequestedAmount\":\"NaN\"}}", "{\"ScienceReceipt\":{\"RequestedAmount\":1e1000}}", new string('x', 16385) })
        {
            string payload = raw;
            Add("malformed-json-reject-" + Tests.Count, () =>
            {
                Enqueue(Claim()); Tick(0); Check(!ScienceOutbox.Capture(payload), "Malformed JSON not consumed"); Tick(.1f);
                Equal(0, TestEnvironment.ReceiptCount, "Malformed JSON not queued"); Equal(1, Claims.Count, "Malformed JSON preserves evidence");
            });
        }
    }

    private static void CoveredOnly()
    {
        Enqueue(Claim("science:a", 10)); Enqueue(Claim("science:b", 20)); Tick(0); var first = Last; Tick(1);
        Capture(Receipt(first, recorded: 9)); Tick(1.1f);
        Equal(2, Claims.Count, "Insufficient cumulative receipt preserves both claims"); Equal(0, Claims.Single(c => c.SourceId == "science:a").Attempts, "Partial cumulative starts fresh attempt sequence");
        Tick(2); var retry = Last; Equal("science:a", retry.EntityId, "Partially covered source retried");
        Capture(Receipt(retry, recorded: 10)); Tick(2.1f);
        Equal("science:b", Claims.Single().SourceId, "Only covered source retired"); Equal(1, TestEnvironment.Flights.Count, "Unrelated flight retained");
        Capture(Receipt(PersonalClient.Sends[1].Request, recorded: 20)); Tick(2.2f); Equal(0, Claims.Count, "Matching cumulative second receipt retires second source");
    }
    private static void SnapshotCoveredOnly()
    {
        Enqueue(Claim("science:a", 10)); Enqueue(Claim("science:b", 20));
        PersonalClient.Credits["science:a"] = 10; PersonalClient.Credits["science:b"] = 19; Tick(0);
        Equal("science:b", Claims.Single().SourceId, "Snapshot only retires covered source"); Equal("science:b", Last.EntityId, "Uncovered source still sent");
        PersonalClient.Credits["science:b"] = 20; Tick(.1f); Equal(0, Claims.Count, "Updated cumulative snapshot retires remaining source"); Equal(0, TestEnvironment.Flights.Count, "Snapshot retirement cancels associated flight");
    }
    private static void NewerAmount()
    {
        var c = Claim(amount: 10); Enqueue(c); Tick(0); var first = Last;
        Enqueue(Claim(amount: 20)); Enqueue(Claim(amount: 15)); Tick(1);
        Equal(20m, Claims.Single().Amount, "Cumulative coalescing never decreases amount"); Equal(1, PersonalClient.Sends.Count, "Older in-flight claim not duplicated");
        Equal(10m, TestEnvironment.FlightAmount(TestEnvironment.Flights.Values.Cast<object>().Single()), "Flight immutable amount retained");
        Capture(Receipt(first, recorded: 10)); Tick(1.1f); Equal(20m, Claims.Single().Amount, "Older acceptance cannot erase newer amount");
        Tick(1.3f);
        Equal("20", Last.Body, "Newer cumulative amount sent after old receipt");
        Capture(Receipt(recorded: 20)); Tick(1.4f); Equal(0, Claims.Count, "New amount requires own coverage");
    }
    private static void ImmutableAward()
    {
        var c = Award(2000000.123456m); Enqueue(c); Tick(0); var first = Last;
        Enqueue(Award(3000000m)); Tick(1);
        Equal(2000000.123456m, c.Amount, "Duplicate award cannot rewrite immutable evidence"); Equal("1000000", first.Body, "Award first chunk bounded to Core MaxAward");
        Equal(1, PersonalClient.Sends.Count, "No duplicate award while in flight");
        Capture(Receipt(first, recorded: 1000000)); Tick(1.1f);
        Equal(1000000.123456m, c.Amount, "Only acknowledged chunk subtracted"); Equal(1L, c.ChunkIndex, "One index advancement"); Tick(1.3f); Check(first.EntityId != Last.EntityId, "New chunk distinct immutable source");
        Capture(Receipt(first, recorded: 1000000)); Tick(1.4f); Equal(1L, c.ChunkIndex, "Duplicate old acknowledgement cannot advance next chunk");
    }
    private static void AwardProgression()
    {
        Enqueue(Award(2000000.123456m)); Tick(0); var first = Last; Capture(Receipt(first, recorded: 999999.999999m)); Tick(.1f);
        Equal(0L, Claims.Single().ChunkIndex, "Awards require exact coverage, no subject tolerance");
        Ready(Claims.Single()); Tick(1); var repeat = Last; Equal(first.EntityId, repeat.EntityId, "Insufficient award keeps stable source");
        Capture(Receipt(repeat)); Tick(1.1f); var state = ReadSaved().Claims.Single(); Equal(1L, state.ChunkIndex, "Chunk index persisted");
        Equal(1000000.123456m, state.Amount, "Remainder persisted"); string secondSource = state.GetChunkSourceId(); Check(secondSource != first.EntityId, "Chunk source changes");
        Restart(); Tick(0); Equal(secondSource, Last.EntityId, "Restart reproduces stable derived chunk source"); Equal("1000000", Last.Body, "Second chunk still capped");
        Capture(Receipt()); Tick(.1f); Equal(2L, Claims.Single().ChunkIndex, "Second acknowledgement advances exactly once");
        Equal(.123456m, Claims.Single().Amount, "Fractional final remainder exact");
        Tick(1); Equal("0.123456", Last.Body, "Final chunk no rounding loss"); Check(Last.EntityId != secondSource, "Final chunk unique source");
        Capture(Receipt()); Tick(1.1f); Equal(0, Claims.Count, "Final exact receipt retires award"); Equal(0, ReadSaved().Claims.Count, "Final retirement durable");
    }
    private static void AwardModel()
    {
        var c = Award(2000000.123456m); decimal total = 0; var sources = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 3; i++)
        {
            decimal amount = c.GetChunkAmount(); string source = c.GetChunkSourceId();
            Check(sources.Add(source), "Every immutable chunk source distinct"); Check(amount > 0 && amount <= PersonalProgression.MaxAward, "Chunk bounded");
            if (i > 0)
            {
                byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.SourceId + ":" + i));
                Equal("award:" + new Guid(hash.AsSpan(0, 16)).ToString("N"), source, "Derived source independently matches SHA-256 contract");
            }
            var restarted = AetherfrontJson.Deserialize<PendingScienceClaim>(AetherfrontJson.Serialize(c));
            Equal(source, restarted.GetChunkSourceId(), "Actual Core single-record restart preserves source"); Equal(amount, restarted.GetChunkAmount(), "Actual Core single-record restart preserves amount");
            total += amount; Equal(i == 2, restarted.AcknowledgeChunk(), "Only final chunk completes award"); c = restarted;
        }
        Equal(2000000.123456m, total, "Chunk total exactly equals immutable award");
    }
    private static void HeldDoesNotStarve()
    {
        Enqueue(Claim("science:held")); Enqueue(Claim("science:other")); Tick(0); var heldRequest = Last;
        Capture(Receipt(heldRequest, "terminal", 0)); Tick(.1f);
        var held = Claims.Single(c => c.SourceId == "science:held"); Check(held.Held, "Terminal becomes held"); Equal("fixture-rejection", held.LastRejection, "Terminal reason retained");
        Equal(ApfClientSettings.ClientVersion, held.HeldVersion, "Held release retained"); Equal(1, ScienceOutbox.HeldCount, "Held count separate"); Equal(1, ScienceOutbox.PendingCount, "Other claim remains pending");
        Check(ReadSaved().Claims.Single(c => c.SourceId == held.SourceId).Held, "Held rejection persisted");
        Tick(1); Equal("science:other", Last.EntityId, "Held source does not starve other claim");
        Capture(Receipt()); Tick(1.1f); Tick(1000); Equal(2, PersonalClient.Sends.Count, "Held source never silently retried"); Equal(1, Claims.Count, "Held evidence not deleted");
    }
    private static void RetryUser()
    {
        var a = Claim("science:current"); var b = Claim("science:other-account"); b.AccountId = "other-account";
        foreach (var c in new[] { a, b }) { c.Held = true; c.HeldVersion = ApfClientSettings.ClientVersion; c.LastRejection = "denied"; c.Attempts = 5; c.NextAttemptUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks; }
        Enqueue(a); Enqueue(b); Persist(); Tick(0); ScienceOutbox.RetryHeld();
        var current = Claims.Single(c => c.AccountId == "fixture-account"); Check(!current.Held && current.Attempts == 0 && current.NextAttemptUtcTicks == 0, "User retry resets current account state");
        Equal("", current.LastRejection, "User retry clears rejection"); Check(Claims.Single(c => c.AccountId == "other-account").Held, "User retry leaves other account held");
        Check(!ReadSaved().Claims[0].Held, "User retry persisted"); Tick(1); Equal("science:current", Last.EntityId, "User retry enables sending");
    }
    private static void SameRelease()
    {
        var c = Award(); c.Held = true; c.HeldVersion = ApfClientSettings.ClientVersion; c.LastRejection = "denied"; WriteClaims(new[] { c });
        Tick(0); Equal(1, ScienceOutbox.HeldCount, "Same release preserves held status"); Equal(0, PersonalClient.Sends.Count, "Same release no retry");
        Restart(); Tick(0); Equal(1, ScienceOutbox.HeldCount, "Restart same release remains held"); Equal("denied", Claims.Single().LastRejection, "Held reason survives restart");
    }
    private static void NewRelease()
    {
        var c = Award(); c.Held = true; c.HeldVersion = "fixture-old-release"; c.LastRejection = "denied"; c.Attempts = 30; c.NextAttemptUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks; WriteClaims(new[] { c });
        Tick(0); Check(!Claims.Single().Held, "New release unholds evidence"); Equal(1, Claims.Single().Attempts, "New release resets attempts before send");
        Equal("", Claims.Single().LastRejection, "New release clears old rejection"); Equal(1, PersonalClient.Sends.Count, "New release retries once"); Check(!ReadSaved().Claims.Single().Held, "New-release retry persisted");
    }
    private static void RetryDisposition()
    {
        var c = Claim(); Enqueue(c); Tick(0); long deadline = c.NextAttemptUtcTicks; Capture(Receipt(disposition: "retry", recorded: 0)); Tick(.1f);
        Check(!c.Held, "Retry disposition not terminal"); Equal(1, Claims.Count, "Retry evidence preserved"); Equal(deadline, c.NextAttemptUtcTicks, "Retry keeps per-claim deadline");
        Equal(0, TestEnvironment.Flights.Count, "Retry releases flight"); Tick(100); Equal(1, PersonalClient.Sends.Count, "Realtime alone cannot bypass UTC backoff");
        Ready(c); Tick(101); Equal(2, c.Attempts, "Retry increments attempts");
    }
    private static void Reconnect()
    {
        var c = Claim(); Enqueue(c); Tick(0); var old = Last; Capture(Receipt(old)); PersonalClient.Active = false; Tick(.1f);
        Equal(0, TestEnvironment.Flights.Count, "Disconnect clears flights"); Equal(0, TestEnvironment.ReceiptCount, "Disconnect clears queued receipts"); Equal(1, Claims.Count, "Disconnect keeps evidence");
        Equal(0L, TestEnvironment.Get<long>("wantedRevision"), "Disconnect clears wanted revision");
        PersonalClient.Active = true; Ready(c); Tick(1); var fresh = Last; Check(old.CommandId != fresh.CommandId, "Reconnect fresh nonce");
        Capture(Receipt(old)); Tick(1.1f); Equal(1, Claims.Count, "Pre-reconnect receipt ignored"); Capture(Receipt(fresh)); Tick(1.2f); Equal(0, Claims.Count, "Fresh reconnect receipt accepted");
    }
    private static void ScopeIsolation()
    {
        var a = Claim("science:current"); var b = Claim("science:account"); b.AccountId = "other-account"; var c = Claim("science:server"); c.ServerId = "other-server";
        Enqueue(a); Enqueue(b); Enqueue(c); Tick(0); Equal("science:current", Last.EntityId, "Only matching identity sent"); Equal(1, ScienceOutbox.PendingCount, "Count current identity only");
        var e = Receipt(); MainSystem.UniqueIdentifier = "other-account"; Tick(1); Capture(e); Tick(1.1f); Check(Claims.Contains(a), "Changed account cannot retire old flight");
        AetherfrontClientSystem.State.ServerId = "other-server"; Tick(2); Check(Claims.Contains(c), "Other server evidence retained");
    }
    private static void Sandbox()
    {
        Enqueue(Claim()); Tick(0); HighLogic.CurrentGame.Mode = Game.Modes.SANDBOX; Tick(.1f); Equal(0, TestEnvironment.Flights.Count, "Sandbox clears flight"); Equal(1, Claims.Count, "Sandbox preserves evidence");
        HighLogic.CurrentGame = null; Tick(100); Equal(1, PersonalClient.Sends.Count, "Missing game never sends"); Equal(1, ReadSaved().Claims.Count, "No-game state keeps disk evidence");
    }
    private static void QueueBound()
    {
        Enqueue(Claim()); Tick(0); var stale = Receipt(); stale.ScienceReceipt.RequestId = "science:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        for (int i = 0; i < 1000; i++) Check(Capture(stale), "Well-formed stale envelope consumed");
        Equal(64, TestEnvironment.ReceiptCount, "Queue hard-bounded to 64"); Check(Capture(Receipt()), "Newest current receipt consumed"); Equal(64, TestEnvironment.ReceiptCount, "Queue remains bounded");
        Equal(1, Claims.Count, "Network capture cannot mutate evidence"); Tick(.1f); Equal(0, Claims.Count, "Newest current receipt survives queue pressure"); Equal(0, TestEnvironment.ReceiptCount, "Unity tick drains queue");
    }
    private static void ConcurrentCapture()
    {
        Enqueue(Claim()); Tick(0); var stale = Receipt(); stale.ScienceReceipt.RequestId = "science:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; string json = AetherfrontJson.Serialize(stale);
        int consumed = 0; Parallel.For(0, 1024, _ => { if (ScienceOutbox.Capture(json)) Interlocked.Increment(ref consumed); });
        Equal(1024, consumed, "Concurrent valid receipts all safely recognized"); Equal(64, TestEnvironment.ReceiptCount, "Concurrent captures cannot overrun bound");
        Equal(1, Claims.Count, "Receive path never mutates claims"); Capture(Receipt()); Tick(.1f); Equal(0, Claims.Count, "Real receipt processed after concurrent pressure");
    }
    private static void SnapshotCoalescing()
    {
        Enqueue(Claim("science:a")); Enqueue(Claim("science:b")); Tick(0); var first = Last; Tick(1); var second = Last;
        Capture(Receipt(first, revision: 5)); Tick(1.01f); Equal(1, AetherfrontClientSystem.SnapshotTimes.Count, "First accepted receipt requests snapshot");
        Capture(Receipt(second, revision: 9)); Tick(1.02f); Equal(1, AetherfrontClientSystem.SnapshotTimes.Count, "Second receipt coalesced"); Equal(9L, TestEnvironment.Get<long>("wantedRevision"), "Wanted revision is maximum accepted revision");
        Tick(3); Equal(1, AetherfrontClientSystem.SnapshotTimes.Count, "No snapshot before two seconds"); Tick(3.02f); Equal(2, AetherfrontClientSystem.SnapshotTimes.Count, "Unsatisfied revision retries after two seconds");
        AetherfrontClientSystem.State.Revision = 9; Tick(5.03f); Tick(10); Equal(2, AetherfrontClientSystem.SnapshotTimes.Count, "Caught-up revision stops snapshot requests");
        for (int i = 1; i < AetherfrontClientSystem.SnapshotTimes.Count; i++) Check(AetherfrontClientSystem.SnapshotTimes[i] - AetherfrontClientSystem.SnapshotTimes[i - 1] >= 2, "Snapshot requests >= 2 seconds apart");
    }
    private static void PendingCap()
    {
        foreach (var claim in Enumerable.Range(0, 4096).Select(i => Claim("science:" + i))) Enqueue(claim);
        Tick(0);
        Enqueue(Claim("science:overflow")); Persist(); Equal(4096, Claims.Count, "Pending queue remains 4096");
        Check(!Claims.Any(c => c.SourceId == "science:overflow"), "Overflow claim cannot evict existing evidence");
        Equal(4096, ReadSaved().Claims.Count, "Full saved queue retained"); Check(ScienceOutbox.Warning != null, "Capacity warning visible");
    }
    private static void PolicyBoundaries()
    {
        foreach (int attempts in new[] { int.MinValue, -1, 0, 29, 30, int.MaxValue })
        {
            var c = Claim(); c.Attempts = attempts; long now = DateTime.UtcNow.Ticks; ScienceRetryPolicy.Schedule(c, now);
            int expected = Math.Min(30, Math.Max(0, attempts) > 29 ? 30 : Math.Max(0, attempts) + 1);
            Equal(expected, c.Attempts, "Attempts saturate without integer overflow"); Check(c.NextAttemptUtcTicks >= now + 2 * TimeSpan.TicksPerSecond && c.NextAttemptUtcTicks <= now + 60 * TimeSpan.TicksPerSecond, "Retry stays within 2..60 seconds");
        }
        var edge = Claim(); edge.Attempts = int.MaxValue; Equal(DateTime.MaxValue.Ticks, ScienceRetryPolicy.Schedule(edge, DateTime.MaxValue.Ticks - 1), "DateTime addition saturates safely");
        Throws<ArgumentException>(() => ScienceRetryPolicy.Schedule(null, 0)); Throws<ArgumentException>(() => ScienceRetryPolicy.Schedule(Claim(), -1)); Throws<ArgumentException>(() => ScienceRetryPolicy.Schedule(Claim(), long.MaxValue));
        edge.Held = true; edge.HeldVersion = "old"; edge.LastRejection = "denied"; ScienceRetryPolicy.Reset(edge);
        Check(edge.Attempts == 0 && edge.NextAttemptUtcTicks == 0 && !edge.Held && edge.LastRejection == "" && edge.HeldVersion == "", "Reset fully clears retry/held state");
    }
    private static void CoverageTolerance()
    {
        Check(ScienceRetryPolicy.Covered(40, 10, 9.999999m), "Subject allows one-micro tolerance"); Check(!ScienceRetryPolicy.Covered(40, 10, 9.999998m), "Subject rejects beyond tolerance");
        Check(ScienceRetryPolicy.Covered(40, 1000000, 999999.9m), "Relative tolerance covers subject float conversion");
        Check(!ScienceRetryPolicy.Covered(42, 10, 9.999999m), "Awards do not use subject tolerance"); Check(ScienceRetryPolicy.Covered(42, 10, 10), "Awards exact coverage accepted");
        Check(!ScienceRetryPolicy.Covered(40, .000001m, 0), "Zero cumulative never confirms even smallest subject");
    }
    private static void CursorLongRun()
    {
        var all = Enumerable.Range(0, 7).Select(i => Claim("science:" + i)).ToArray(); foreach (var c in all) Enqueue(c);
        TestEnvironment.Set("cursor", int.MaxValue); Tick(0); Check(TestEnvironment.Get<int>("cursor") >= 0 && TestEnvironment.Get<int>("cursor") < 7, "int.MaxValue cursor normalized without overflow");
        foreach (var c in all) c.NextAttemptUtcTicks = DateTime.MaxValue.Ticks;
        ExpireFlights();
        for (int i = 1; i <= 20000; i++)
        {
            // Scan every future-due source each tick without 20000 filesystem flushes.
            Tick(i);
            Check(TestEnvironment.Get<int>("cursor") >= 0 && TestEnvironment.Get<int>("cursor") < 7, "Long-run cursor bounded");
            Check(TestEnvironment.Flights.Count <= 2, "Long-run flight cap");
        }
        Equal(1, PersonalClient.Sends.Count, "Long-run scheduler honors future UTC deadlines");
        for (int i = 1; i <= 70; i++) { foreach (var c in all) Ready(c); ExpireFlights(); Tick(20000 + i); }
        Equal(71, PersonalClient.Sends.Count, "Long-run scheduler resumes progress every eligible second");
        var groups = PersonalClient.Sends.GroupBy(s => s.Request.EntityId).Select(g => g.Count()).ToArray(); Equal(7, groups.Length, "Every source served");
        Check(groups.Max() - groups.Min() <= 1, "Round-robin fairness preserved over 20000 ticks"); Equal(7, Claims.Count, "No receipt means no evidence deleted");
    }
}

internal static class TestEnvironment
{
    internal static string RunRoot;
    internal static string CaseRoot;
    internal static string OutboxPath => Path.Combine(CaseRoot, "APF", "PendingScience-v1.json");
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    internal static T Get<T>(string name) => (T)typeof(ScienceOutbox).GetField(name, PrivateStatic).GetValue(null);
    internal static void Set(string name, object value) => (typeof(ScienceOutbox).GetField(name, PrivateStatic) ?? throw new MissingFieldException(typeof(ScienceOutbox).FullName, name)).SetValue(null, value);
    internal static IDictionary Flights => Get<IDictionary>("flights");
    internal static int ReceiptCount => (int)Get<object>("receipts").GetType().GetProperty("Count").GetValue(Get<object>("receipts"));
    internal static void SetFlightSent(object flight, long ticks) => flight.GetType().GetField("Sent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(flight, ticks);
    internal static decimal FlightAmount(object flight) => (decimal)flight.GetType().GetField("Amount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(flight);
    internal static void Reset(string name)
    {
        CaseRoot = Path.Combine(RunRoot, name); Directory.CreateDirectory(CaseRoot); KSPUtil.ApplicationRootPath = CaseRoot;
        ResetStatics();
        UnityEngine.Time.realtimeSinceStartup = 0; UnityEngine.Time.time = 0; UnityEngine.Time.timeScale = 1; UnityEngine.Debug.Messages.Clear();
        HighLogic.CurrentGame = new Game { Mode = Game.Modes.CAREER };
        MainSystem.UniqueIdentifier = "fixture-account"; ApfClientSettings.ClientVersion = "fixture-rc43";
        AetherfrontClientSystem.State = new StubState { ServerId = "fixture-server", Revision = 0 }; AetherfrontClientSystem.SnapshotTimes.Clear();
        PersonalClient.Active = true; PersonalClient.Sends.Clear(); PersonalClient.Credits.Clear(); PersonalClient.ThrowOnSend = false; PersonalClient.OnSend = null; PersonalClient.ObserverError = null;
        KesasInstall.Installer.FailuresRemaining = 0; KesasInstall.Installer.SaveAttempts = 0; KesasInstall.Installer.SuccessfulSaves = 0;
    }
    internal static void ResetStatics()
    {
        Set("pending", null); Set("dirty", false); Set("failed", false); Set("cursor", 0); Set("nextSend", 0f); Set("nextSave", 0f); Set("path", null);
        Set("wantedRevision", 0L); Set("nextSnapshot", 0f); Set("<Warning>k__BackingField", null);
        Flights.Clear(); Get<object>("receipts").GetType().GetMethod("Clear").Invoke(Get<object>("receipts"), null);
    }
    internal static void RequireSyntheticPath(string path)
    {
        string full = Path.GetFullPath(path), prefix = Path.GetFullPath(RunRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture path escaped synthetic run root: " + full);
        for (string parent = Path.GetDirectoryName(full); parent != null && parent.StartsWith(Path.GetFullPath(RunRoot), StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture reparse point forbidden");
    }
    internal static void WriteOutbox(string text) { RequireSyntheticPath(OutboxPath); Directory.CreateDirectory(Path.GetDirectoryName(OutboxPath)); File.WriteAllText(OutboxPath, text); }
    internal static void Write(string name, string text) { string path = Path.Combine(CaseRoot, name); RequireSyntheticPath(path); File.WriteAllText(path, text); }
}

namespace UnityEngine
{
    internal static class Time { public static float realtimeSinceStartup; public static float time; public static float timeScale = 1; }
    internal static class Debug
    {
        internal static readonly List<string> Messages = new();
        public static void LogError(object text) => Messages.Add("ERROR " + text);
        public static void LogWarning(object text) => Messages.Add("WARNING " + text);
    }
}
internal sealed class Game { internal enum Modes { SANDBOX, SCIENCE_SANDBOX, CAREER } internal Modes Mode; }
internal static class HighLogic { internal static Game CurrentGame; }
internal static class KSPUtil { internal static string ApplicationRootPath; }
namespace LmpClient { internal static class MainSystem { internal static string UniqueIdentifier; } }
namespace LmpClient.Apf { internal static class ApfClientSettings { internal static string ClientVersion; } }
namespace LmpClient.Aetherfront
{
    internal sealed class StubState { internal string ServerId; internal long Revision; }
    internal static class AetherfrontClientSystem
    {
        internal static StubState State;
        internal static readonly List<float> SnapshotTimes = new();
        internal static void RequestSnapshot() => SnapshotTimes.Add(UnityEngine.Time.realtimeSinceStartup);
    }
}
namespace ApfCore
{
    internal static class ApfPaths
    {
        internal static void EnsureNoReparsePoints(string root, string path)
        {
            TestEnvironment.RequireSyntheticPath(path);
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Outbox escaped fixture application root");
        }
    }
}
namespace KesasInstall
{
    internal static class Installer
    {
        internal static int FailuresRemaining, SaveAttempts, SuccessfulSaves;
        internal static void AtomicText(string path, string text)
        {
            TestEnvironment.RequireSyntheticPath(path); SaveAttempts++;
            if (FailuresRemaining > 0) { FailuresRemaining--; throw new IOException("Injected synthetic outbox save failure"); }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, text); File.Move(temporary, path, true); SuccessfulSaves++;
        }
    }
}
namespace Aetherfront.Personal.Client
{
    internal sealed record SentScience(float Realtime, AetherfrontWireRequest Request, string Json);
    internal static class PersonalClient
    {
        internal static bool Active, ThrowOnSend;
        internal static Action<AetherfrontWireRequest> OnSend;
        internal static Exception ObserverError;
        internal static readonly List<SentScience> Sends = new();
        internal static readonly Dictionary<string, decimal> Credits = new(StringComparer.Ordinal);
        internal static decimal CreditedScience(string source) => Credits.TryGetValue(source, out decimal amount) ? amount : 0;
        internal static void SendScienceClaim(AetherfrontWireRequest request)
        {
            request.ProtocolVersion = 1; request.Operation = AetherfrontWireOperation.CommandRequest;
            string json = AetherfrontProtocol.Serialize(request);
            Sends.Add(new SentScience(UnityEngine.Time.realtimeSinceStartup, AetherfrontProtocol.DeserializeRequest(json), json));
            try { OnSend?.Invoke(request); } catch (Exception error) { ObserverError = error; }
            if (ThrowOnSend) throw new IOException("Injected synthetic send failure; no socket exists");
        }
    }
}
