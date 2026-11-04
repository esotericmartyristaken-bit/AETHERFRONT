using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Aetherfront.Core;
using Aetherfront.Personal.Server;
using LmpCommon.Enums;
using LmpCommon;
using LmpCommon.Message;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;

static class Program
{
    static string Metadata(string key) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;
    static readonly string Runtime = Metadata("Runtime");
    static readonly string Evidence = Metadata("EvidenceRoot");
    static string Actor;
    static readonly List<object> Results = new();
    static int checks;
    static object noticeEvidence;
    static void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
    static object Case(ClientStructure client, string name, int command, string source, string amount, long reason, string expected)
    {
        while (client.SendMessageQueue.TryDequeue(out _)) { }
        var before = AetherfrontServerSystem.Snapshot();
        string requestId = "fixture:" + Guid.NewGuid().ToString("N");
        Check(PersonalServer.TryHandle(client, new AetherfrontWireRequest {
            Operation = AetherfrontWireOperation.CommandRequest, CommandType = (AetherfrontCommandType)command,
            EntityId = source, Body = amount, Amount2 = reason, CommandId = requestId, AccountId = "forged-account"
        }), name + ": unhandled");
        Check(client.SendMessageQueue.TryDequeue(out var message), name + ": no direct reply");
        var payload = ((AetherfrontCommandReplyMsgData)message.Data).PayloadJson;
        using var parsed = JsonDocument.Parse(payload);
        JsonElement Field(string key) => parsed.RootElement.EnumerateObject().Single(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        string code = Field("ReasonCode").GetString();
        Check(code == expected, name + ": " + code);
        Check(!parsed.RootElement.TryGetProperty("StateJson", out _), name + ": full snapshot leaked");
        Check(!parsed.RootElement.TryGetProperty("InterestJson", out _), name + ": interest projection leaked");
        Check(Encoding.UTF8.GetByteCount(payload) < 4096, name + ": receipt not compact");
        var receipt = Field("ScienceReceipt");
        Check(receipt.GetProperty("RequestId").GetString() == requestId, name + ": nonce mismatch");
        Check(receipt.GetProperty("AccountId").GetString() == Actor, name + ": actor spoofed");
        Check(receipt.GetProperty("SourceId").GetString() == source, name + ": source correlation changed");
        Check(receipt.GetProperty("Command").GetInt32() == command, name + ": command mismatch");
        Check(receipt.GetProperty("RequestedAmount").GetDecimal() == decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), name + ": amount mismatch");
        var legacy = AetherfrontProtocol.DeserializeReply(payload);
        Check(legacy != null && legacy.Operation == AetherfrontWireOperation.SnapshotReply && string.IsNullOrEmpty(legacy.StateJson), name + ": legacy parse incompatible");
        var after = AetherfrontServerSystem.Snapshot();
        var player = PersonalProgression.Find(after, Actor);
        string disposition = expected == "Accepted" ? "accepted" : new[] { "ScienceSubjectNotRegistered", "InvalidScienceEvidence", "InvalidScienceSource", "InvalidScienceRewardReceipt", "ScienceRewardReceiptConflict" }.Contains(expected) ? "terminal" : "retry";
        Check(receipt.GetProperty("Disposition").GetString() == disposition, name + ": incorrect disposition");
        decimal recorded = player.ScienceSources.FirstOrDefault(s => string.Equals(s.Id, source, StringComparison.OrdinalIgnoreCase))?.CumulativeScience ?? 0m;
        Check(receipt.GetProperty("RecordedCumulative").GetDecimal() == recorded, name + ": wrong authoritative high-water");
        var row = new { name, command, source, amount, reason, code, accepted = Field("Accepted").GetBoolean(),
            operation = Field("Operation").ToString(), beforeRevision = before.Revision, afterRevision = after.Revision,
            sourceCumulative = player.ScienceSources.FirstOrDefault(s => string.Equals(s.Id, source, StringComparison.OrdinalIgnoreCase))?.CumulativeScience ?? 0m, player.EarnedScience, stateBytes = 0, disposition = receipt.GetProperty("Disposition").GetString(), recordedCumulative = receipt.GetProperty("RecordedCumulative").GetDecimal(),
            replyBytes = Encoding.UTF8.GetByteCount(payload) };
        Results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row)); return row;
    }
    static void Until(Func<bool> condition, int milliseconds, string reason)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds >= milliseconds) throw new Exception(reason);
            Thread.Sleep(10);
        }
        Check(true, reason);
    }

    static void NoticeCoalescing(ClientStructure client)
    {
        var notices = typeof(PersonalServer).GetNestedType("ScienceNotices", BindingFlags.NonPublic);
        var gate = notices.GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var pending = notices.GetField("pending", BindingFlags.NonPublic | BindingFlags.Static);
        var active = notices.GetField("activeCallbacks", BindingFlags.NonPublic | BindingFlags.Static);
        var clock = (Timer)notices.GetField("Clock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        bool IsPending() { lock (gate) return (bool)pending.GetValue(null); }
        int Active() { lock (gate) return (int)active.GetValue(null); }
        bool IsQuiet() { lock (gate) return !(bool)pending.GetValue(null) && (int)active.GetValue(null) == 0; }
        Until(IsQuiet, 7000, "initial science timer did not become idle");
        var observer = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
        observer.ConnectionStatus = ConnectionStatus.Connected; observer.Authenticated = true;
        typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(observer, new ConcurrentQueue<IServerMessageBase>());
        var budget = typeof(ClientStructure).GetProperty("OutboundBudget", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(observer);
        var budgetGate = budget.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(budget);
        var dequeue = budget.GetType().GetMethod("TryDequeue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 43210);
        Check(ServerContext.Clients.TryAdd(endpoint, observer), "fixture observer registration");
        List<AetherfrontWireReply> Drain()
        {
            var rows = new List<AetherfrontWireReply>();
            var arguments = new object[] { null };
            while ((bool)dequeue.Invoke(budget, arguments))
            {
                var message = (IServerMessageBase)arguments[0];
                Check(message.Data is AetherfrontEventNoticeMsgData, "science notice sent a full message");
                var json = ((AetherfrontEventNoticeMsgData)message.Data).PayloadJson;
                var reply = AetherfrontProtocol.DeserializeReply(json);
                Check(reply.Operation == AetherfrontWireOperation.EventNotice && string.IsNullOrEmpty(reply.StateJson) && Encoding.UTF8.GetByteCount(json) < 512, "notice not compact");
                rows.Add(reply);
            }
            return rows;
        }
        void Award(string label) => Case(client, label, 42, "award:" + Guid.NewGuid().ToString("N"), "0.01", 0x400, "Accepted");
        void Transmit(string label, decimal cumulative) => Case(client, label, 40, "temperatureScan@KerbinSrfLandedLaunchPad", cumulative.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, "Accepted");
        long burstMilliseconds = 0;
        long tailMilliseconds = 0;
        int callbackRaceNotices = 0;
        int loggedErrors = 0;
        bool disposed = false;
        try
        {
            // An accepted ordinary command retains its immediate original notice.
            AetherfrontServerSystem.Execute(s => { s.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "coalescer-control", ScienceCost = 0 }); return AetherfrontResult.Success(s.Revision); });
            while (client.SendMessageQueue.TryDequeue(out _)) { }
            Check(PersonalServer.TryHandle(client, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = AetherfrontCommandType.UnlockTechnology, TechnologyId = "coalescer-control", Amount2 = PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), Actor).NextCommandSequence }), "non-science control not handled");
            Check(client.SendMessageQueue.TryDequeue(out var controlMessage), "non-science control reply missing");
            var control = AetherfrontProtocol.DeserializeReply(((AetherfrontCommandReplyMsgData)controlMessage.Data).PayloadJson);
            Check(control.Accepted && control.Operation == AetherfrontWireOperation.CommandReply && !string.IsNullOrEmpty(control.StateJson), "non-science control reply changed");
            var immediate = Drain();
            Check(immediate.Count == 1 && immediate[0].Revision == AetherfrontServerSystem.Snapshot().Revision && IsQuiet(), "non-science notice was delayed or coalesced");

            var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < 8; i++) Transmit("positive_transmission_burst_" + i, (i + 1) / 100m);
            long burstRevision = AetherfrontServerSystem.Snapshot().Revision;
            Check(observer.SendMessageQueue.IsEmpty, "science burst notified immediately");
            Until(() => !observer.SendMessageQueue.IsEmpty, 7000, "science burst lost its notice");
            burstMilliseconds = elapsed.ElapsedMilliseconds;
            var first = Drain();
            Check(first.Count == 1 && first[0].Revision == burstRevision && burstMilliseconds >= 1500, "burst was not coalesced to latest revision in one two-second window");
            Until(IsQuiet, 2000, "burst callback did not finish");
            elapsed.Restart(); Transmit("positive_transmission_tail", .09m);
            long tailRevision = AetherfrontServerSystem.Snapshot().Revision;
            Until(() => !observer.SendMessageQueue.IsEmpty, 7000, "tail award never notified");
            tailMilliseconds = elapsed.ElapsedMilliseconds;
            var tail = Drain();
            Check(tail.Count == 1 && tail[0].Revision == tailRevision && tailMilliseconds >= 1500, "tail notice missing latest revision or emitted immediately");
            Until(IsQuiet, 2000, "tail callback did not finish");

            // Hold only the fixture observer's enqueue gate. This keeps a real
            // timer callback in flight while another authoritative award arrives.
            Monitor.Enter(budgetGate);
            try
            {
                Award("callback_race_first");
                Until(() => Active() > 0 && !IsPending(), 7000, "callback did not clear pending before sending");
                Transmit("callback_race_transmission_during_send", .10m);
                Check(IsPending(), "award during callback failed to schedule a trailing window");
            }
            finally { Monitor.Exit(budgetGate); }
            var racing = new List<AetherfrontWireReply>();
            Until(() => { racing.AddRange(Drain()); return racing.Count >= 2 && IsQuiet(); }, 7000, "callback race lost its trailing notice");
            callbackRaceNotices = racing.Count;
            Check(racing.Count == 2 && racing.Last().Revision == AetherfrontServerSystem.Snapshot().Revision, "callback race tail did not include latest state");

            // Deliberately malformed fixture observer makes enqueue throw. The
            // callback contains both failures and logs only the first occurrence.
            var broken = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
            broken.ConnectionStatus = ConnectionStatus.Connected;
            var brokenEndpoint = new IPEndPoint(IPAddress.Loopback, 43211);
            Check(ServerContext.Clients.TryAdd(brokenEndpoint, broken), "broken observer fixture registration");
            var originalOutput = Console.Out;
            using var logCapture = new StringWriter();
            try
            {
                Console.SetOut(logCapture);
                var flush = notices.GetMethod("Flush", BindingFlags.NonPublic | BindingFlags.Static);
                flush.Invoke(null, new object[] { null });
                flush.Invoke(null, new object[] { null });
            }
            finally
            {
                Console.SetOut(originalOutput);
                ServerContext.Clients.TryRemove(brokenEndpoint, out _);
            }
            loggedErrors = logCapture.ToString().Split("Coalesced science notice failed:").Length - 1;
            Check(loggedErrors == 1 && (bool)notices.GetField("errorLogged", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null), "timer failures were not contained/logged once");
            Drain();
            Award("after_callback_error");
            Until(() => !observer.SendMessageQueue.IsEmpty && IsQuiet(), 7000, "timer failed to recover after a callback error");
            var recovered = Drain();
            Check(recovered.Count == 1 && recovered[0].Revision == AetherfrontServerSystem.Snapshot().Revision, "recovered timer missed latest state");

            // No-pending awards and shutdown must leave no active callback or timer.
            Award("shutdown_pending_notice");
            Check(IsPending(), "shutdown fixture did not schedule notice");
            AetherfrontServerSystem.Shutdown();
            Until(IsQuiet, 7000, "disabled server timer did not drain safely");
            Check(Drain().Count == 0, "disabled server emitted a stale notice");
        }
        finally
        {
            ServerContext.Clients.TryRemove(endpoint, out _);
            using var completed = new ManualResetEvent(false);
            disposed = clock.Dispose(completed) && completed.WaitOne(7000);
            Check(disposed && Active() == 0 && !IsPending(), "fixture left a timer or callback active");
        }
        noticeEvidence = new { positiveTransmissionAwards = 8, transmissionCommand = 40, burstNotices = 1, burstMilliseconds, tailNotices = 1, tailMilliseconds, callbackRaceNotices, immediateNonScienceNotice = true, loggedErrors, callbackErrorRecovery = true, shutdownSafe = true, timerDisposed = disposed };
        Console.WriteLine(JsonSerializer.Serialize(noticeEvidence));
    }

    static void Main()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) => {
            string path = Path.Combine(Runtime, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        string root = Path.Combine(Evidence, "fixture-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ServerContext.ConfigDirectory = Path.Combine(root, "Config"); Directory.CreateDirectory(ServerContext.ConfigDirectory);
        ServerContext.UniverseDirectory = Path.Combine(root, "Universe"); Directory.CreateDirectory(ServerContext.UniverseDirectory);
        File.Copy(Path.Combine(Runtime, "AetherfrontScienceSubjects.json"), Path.Combine(AppContext.BaseDirectory, "AetherfrontScienceSubjects.json"), true);
        GeneralSettings.SettingsStore.GameMode = GameMode.Science;
        GameplaySettings.SettingsStore.StartingScience = 0; GameplaySettings.SettingsStore.ScienceGainMultiplier = 1;
        typeof(AetherfrontServerSystem).GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null)
            .Invoke(null, new object[] { root, "transmit-fixture" });
        var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
        client.PlayerName = "Fixture"; client.Subspace = int.MinValue; client.ConnectionStatus = ConnectionStatus.Connected;
        typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(client, new ConcurrentQueue<IServerMessageBase>());
        using var fixtureKey = RSA.Create(2048);
        var handshake = new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
        handshake.IdentityPublicKey = IdentityProof.ExportPublicKey(fixtureKey);
        Actor = handshake.UniqueIdentifier = IdentityProof.AccountId(handshake.IdentityPublicKey);
        handshake.PlayerName = client.PlayerName; handshake.KspVersion = "1.12.5";
        handshake.ApfEnabled = true; handshake.ApfClientVersion = "kesas-rc42";
        client.UniqueIdentifier = Actor;
        Check(Server.System.IdentityAuthentication.Begin(client, handshake, out var nonce, out var identityReason), "fixture identity begin: " + identityReason);
        Check(Server.System.IdentityAuthentication.Complete(client, IdentityProof.Sign(fixtureKey, nonce, Actor, client.PlayerName), out _, out identityReason), "fixture identity proof: " + identityReason);
        client.Authenticated = true;
        Check(AetherfrontServerSystem.BindSession(client), "fixture account binding");
        Case(client, "unknown_subject", 40, "not-registered@KerbinInSpaceLow", "4.02", 0, "ScienceSubjectNotRegistered");
        Case(client, "unknown_subject_retry", 40, "not-registered@KerbinInSpaceLow", "4.02", 0, "ScienceSubjectNotRegistered");
        Case(client, "new_valid_pad", 40, "crewReport@KerbinSrfLandedLaunchPad", "1.5", 0, "Accepted");
        Case(client, "valid_pad_replay", 40, "crewReport@KerbinSrfLandedLaunchPad", "1.5", 0, "Accepted");
        Case(client, "overcap_subject", 40, "crewReport@KerbinSrfLandedLaunchPad", "100", 0, "InvalidScienceEvidence");
        Case(client, "cap_ulp", 40, "crewReport@KerbinSrfLandedLaunchPad", "1.500001", 0, "Accepted");
        string receipt = "award:1234567890abcdef1234567890abcdef";
        Case(client, "receipt_bad_reason", 42, receipt, "4.02", 0x10000, "InvalidScienceRewardReceipt");
        Case(client, "receipt_valid", 42, receipt, "4.02", 0x400, "Accepted");
        Case(client, "receipt_same_replay", 42, receipt, "4.02", 0x400, "Accepted");
        Case(client, "receipt_immutable_conflict", 42, receipt, "4.03", 0x400, "ScienceRewardReceiptConflict");
        Case(client, "receipt_immutable_conflict_retry", 42, receipt, "4.03", 0x400, "ScienceRewardReceiptConflict");
        Case(client, "receipt_case_replay", 42, receipt.ToUpperInvariant().Replace("AWARD:", "award:"), "4.02", 0x400, "Accepted");
        for (int retry = 0; retry < 20; retry++)
            Case(client, "bounded_rejection_retry_" + retry, 40, "not-registered@KerbinInSpaceLow", "4.02", 0, "ScienceSubjectNotRegistered");
        GameplaySettings.SettingsStore.ScienceGainMultiplier = 0;
        Case(client, "transient_configuration_exception", 40, "crewReport@KerbinSrfLandedLaunchPad", "1.5", 0, "PersonalProgressionUnavailable");
        GameplaySettings.SettingsStore.ScienceGainMultiplier = 1;
        GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        Case(client, "sandbox_policy", 40, "crewReport@KerbinSrfLandedLaunchPad", "1.5", 0, "ProgressionUnavailableInSandbox");
        GeneralSettings.SettingsStore.GameMode = GameMode.Science;
        decimal oldBalance = PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), Actor).Science;
        AetherfrontServerSystem.Execute(s => { PersonalProgression.Find(s, Actor).Science = PersonalProgression.MaxScience; return AetherfrontResult.Success(s.Revision); });
        Case(client, "wallet_limit_retry", 40, "barometerScan@KerbinSrfLandedLaunchPad", "1", 0, "ScienceLimit");
        AetherfrontServerSystem.Execute(s => { PersonalProgression.Find(s, Actor).Science = oldBalance; return AetherfrontResult.Success(s.Revision); });
        while (client.SendMessageQueue.TryDequeue(out _)) { }
        Check(!PersonalServer.TryHandle(client, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest }), "ordinary snapshot must continue");
        Check(client.SendMessageQueue.IsEmpty, "ordinary snapshot unexpectedly handled");
        Check(PersonalServer.TryHandle(client, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = AetherfrontCommandType.TransferScience }), "retired command not handled");
        Check(client.SendMessageQueue.TryDequeue(out var normalMessage), "non-science reply missing");
        var normal = AetherfrontProtocol.DeserializeReply(((AetherfrontCommandReplyMsgData)normalMessage.Data).PayloadJson);
        Check(normal.Operation == AetherfrontWireOperation.CommandReply && normal.ReasonCode == "FactionEconomyRetired" && !string.IsNullOrEmpty(normal.StateJson), "non-science reply changed");
        foreach (string badNonce in new[] { "", new string('x', 257) })
        {
            Check(PersonalServer.TryHandle(client, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = (AetherfrontCommandType)40, CommandId = badNonce, EntityId = "not-registered@KerbinInSpaceLow", Body = "4.02" }), "bad nonce claim not handled");
            Check(client.SendMessageQueue.TryDequeue(out var invalidMessage), "bad nonce response missing");
            string invalidPayload = ((AetherfrontCommandReplyMsgData)invalidMessage.Data).PayloadJson;
            using var invalidDocument = JsonDocument.Parse(invalidPayload);
            Check(invalidDocument.RootElement.GetProperty("ScienceReceipt").ValueKind == JsonValueKind.Null && !invalidDocument.RootElement.TryGetProperty("StateJson", out _) && Encoding.UTF8.GetByteCount(invalidPayload) < 512, "invalid nonce echoed or produced snapshot");
        }
        NoticeCoalescing(client);
        AetherfrontServerSystem.Shutdown();
        typeof(AetherfrontServerSystem).GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null).Invoke(null, new object[] { root, "transmit-fixture" });
        Case(client, "receipt_replay_after_restart", 42, receipt, "4.02", 0x400, "Accepted");
        AetherfrontServerSystem.Shutdown();
        File.WriteAllText(Path.Combine(Evidence, "server-fixture-results.json"), JsonSerializer.Serialize(new { checks, root, results = Results, noticeEvidence }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
