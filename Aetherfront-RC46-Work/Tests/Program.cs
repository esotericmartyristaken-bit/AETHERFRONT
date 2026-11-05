using System;
using Aetherfront.Core;
using Aetherfront.Personal.Client;

public static class ResearchTests
{
    private const string Actor = "isolated-research-account";
    private static int checks, cases;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static AetherfrontState State(decimal science = 100)
    {
        var s = new AetherfrontState { ServerId = "isolated-research-server" };
        s.PersonalProgression.StartingScience = science;
        s.Accounts.Add(new AetherfrontAccountRecord { AccountId = Actor });
        s.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "start", Starter = true });
        s.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "a", ScienceCost = 10 });
        s.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "b", ScienceCost = 20 });
        s.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "c", ScienceCost = 30, PrerequisiteIds = new System.Collections.Generic.List<string> { "a", "b" } });
        PersonalProgression.Ensure(s, Actor); return s;
    }
    private static PlayerProgression Player(AetherfrontState s) { return PersonalProgression.Find(s, Actor); }
    private static ResearchQueue Queue(AetherfrontState s) { var q = new ResearchQueue(); q.Context(s.ServerId, Actor, true); return q; }
    private static AetherfrontResult Buy(AetherfrontState s, AetherfrontWireRequest r) { return PersonalProgression.UnlockTechnology(s, Actor, r.Amount2, r.TechnologyId); }
    private static void Case(string name, Action test) { test(); cases++; Console.WriteLine("PASS " + name); }
    public static int Main()
    {
        Case("rapid-purchases-serialize-and-charge-once", () => {
            var s = State(); var q = Queue(s); q.Enqueue("a"); q.Enqueue("b"); q.Enqueue("c");
            var a = q.Step(s, Actor, 0, 0); Check(a.TechnologyId == "a" && a.Amount2 == 1, "first counter");
            Check(q.Step(s, Actor, 1, 0) == null && q.Count == 3, "overlapping send");
            Check(Buy(s, a).Accepted, "first rejected");
            var b = q.Step(s, Actor, 2, 1); Check(b.TechnologyId == "b" && b.Amount2 == 2 && Buy(s, b).Accepted, "second counter");
            var c = q.Step(s, Actor, 3, 2); Check(c.TechnologyId == "c" && c.Amount2 == 3 && Buy(s, c).Accepted, "dependent node");
            Check(q.Step(s, Actor, 4, 3) == null && q.Count == 0 && Player(s).Science == 40 && Player(s).NextCommandSequence == 4, "charges or queue");
        });
        Case("same-node-spam-deduplicated", () => {
            var s = State(); var q = Queue(s); for (int i = 0; i < 100; i++) q.Enqueue("a");
            Check(q.Count == 1, "duplicate intents"); var r = q.Step(s, Actor, 0, 0); Buy(s, r);
            Check(q.Step(s, Actor, 1, 1) == null && q.Count == 0 && Player(s).Science == 90, "duplicate charge");
            Check(!Buy(s, r).Accepted && Player(s).Science == 90, "server replay protection removed");
        });
        Case("stale-sequence-and-transfer-race-resynchronize", () => {
            var client = State(); var server = State(); var q = Queue(client); q.Enqueue("b"); var stale = q.Step(client, Actor, 0, 0);
            Check(PersonalProgression.UnlockTechnology(server, Actor, 1, "a").Accepted, "race setup");
            Check(Buy(server, stale).ReasonCode == "PersonalSequenceMismatch", "shipping rejection");
            Check(q.Step(server, Actor, 1, 1) == null, "must yield before retry");
            var retry = q.Step(server, Actor, 2, 1); Check(retry.Amount2 == 2 && retry.TechnologyId == "b" && Buy(server, retry).Accepted, "fresh counter retry");
            Check(q.Step(server, Actor, 3, 2) == null && q.Count == 0 && Player(server).Science == 70, "race double charge");
        });
        Case("missing-reply-refresh-before-retry", () => {
            var s = State(); var q = Queue(s); q.Enqueue("a"); var first = q.Step(s, Actor, 0, 0);
            Check(q.Step(s, Actor, 10, 0) == null && q.SnapshotNeeded, "refresh missing");
            Check(q.Step(s, Actor, 11, 0) == null && !q.SnapshotNeeded, "refresh flood");
            Check(q.Step(s, Actor, 12, 1) == null, "retry without settling refresh");
            var retry = q.Step(s, Actor, 13, 1); Check(retry.Amount2 == 1 && retry.CommandId != first.CommandId, "retry correlation");
            Check(Buy(s, first).Accepted && !Buy(s, retry).Accepted && Player(s).Science == 90, "late delivery double charged");
            Check(q.Step(s, Actor, 14, 2) == null && q.Count == 0, "late acceptance not recognized");
        });
        Case("disconnected-or-identity-changed-intents-never-migrate", () => {
            var s = State(); var q = Queue(s); q.Enqueue("a"); q.Step(s, Actor, 0, 0);
            q.Context(s.ServerId, Actor, false); Check(q.Count == 0, "disconnect retained intent");
            q.Context(s.ServerId, Actor, true); Check(q.Step(s, Actor, 1, 1) == null, "reconnect replay");
            q.Enqueue("a"); q.Context("different-server", Actor, true); Check(q.Count == 0, "cross-server purchase");
            q.Enqueue("b"); q.Context("different-server", "other-account", true); Check(q.Count == 0, "cross-account purchase");
        });
        Case("insufficient-science-and-prerequisite-rejections-terminal", () => {
            var s = State(5); var q = Queue(s); q.Enqueue("a");
            Check(q.Step(s, Actor, 0, 0) == null && q.Count == 0 && q.Warning.Contains("Not enough") && q.SnapshotNeeded, "unaffordable retry loop");
            s = State(); q = Queue(s); q.Enqueue("c");
            Check(q.Step(s, Actor, 0, 0) == null && q.Count == 0 && q.Warning.Contains("prerequisite"), "prerequisite bypass");
            q.Enqueue("missing"); Check(q.Step(s, Actor, 1, 1) == null && q.Count == 0, "unknown node");
        });
        Case("bounded-retries-and-explicit-manual-retry", () => {
            var s = State(); var q = Queue(s); q.Enqueue("a");
            for (int i = 0; i < 3; i++) {
                Check(q.Step(s, Actor, i * 13, i) != null, "attempt missing");
                q.Step(s, Actor, i * 13 + 10, i); Check(q.SnapshotNeeded, "snapshot missing");
                q.Step(s, Actor, i * 13 + 11, i + 1);
            }
            Check(q.Step(s, Actor, 39, 3) == null && q.Warning.Contains("3 attempts") && q.Count == 1, "unbounded retries");
            Check(q.Step(s, Actor, 500, 4) == null, "paused send");
            q.Enqueue("a"); Check(q.Step(s, Actor, 501, 5) != null, "manual retry unavailable");
        });
        Case("dead-connection-pauses-without-request-flood", () => {
            var s = State(); var q = Queue(s); q.Enqueue("a"); q.Step(s, Actor, 0, 0); q.Step(s, Actor, 10, 0);
            Check(q.Step(s, Actor, 30, 0) == null && q.Warning.Contains("timed out"), "dead connection did not pause");
            Check(q.Step(s, Actor, 600, 0) == null && !q.SnapshotNeeded, "dead connection flood");
        });
        Case("queue-capacity-and-null-input", () => {
            var s = State(); var q = Queue(s); Check(!q.Enqueue(null) && !q.Enqueue(new string('x', 257)), "invalid id");
            for (int i = 0; i < 40; i++) q.Enqueue("node-" + i);
            Check(q.Count == 32 && q.Warning.Contains("full"), "unbounded queue");
        });
        Case("pending-overlay-preserves-nodes-and-reserves-cost-without-changing-ledger", () => {
            var s = State(); var q = Queue(s); q.Enqueue("a"); q.Enqueue("b"); q.Enqueue("c");
            var desired=q.Project(s,Player(s),out var visible);
            Check(desired.Contains("a") && desired.Contains("b") && desired.Contains("c") && visible==40,"pending projection");
            Check(Player(s).Science==100 && Player(s).TechnologyUnlocks.Count==1 && Player(s).NextCommandSequence==1,"projection changed authoritative state");
            var a=q.Step(s,Actor,0,0); Buy(s,a);
            desired=q.Project(s,Player(s),out visible);
            Check(desired.Contains("c") && visible==40,"accepted first purchase relocked later pending nodes or double-reserved cost");
            q.Context(s.ServerId,Actor,false); desired=q.Project(s,Player(s),out visible);
            Check(!desired.Contains("b") && visible==90,"disconnect must reconcile only saved purchases");
        });
        Case("overlay-cannot-bypass-prerequisites-budget-or-paused-state", () => {
            var s=State(25); var q=Queue(s); q.Enqueue("a"); q.Enqueue("b");
            var desired=q.Project(s,Player(s),out var visible); Check(desired.Contains("a")&&!desired.Contains("b")&&visible==15,"budget bypass");
            q=Queue(s);q.Enqueue("c");desired=q.Project(s,Player(s),out visible);Check(!desired.Contains("c")&&visible==25,"prerequisite bypass");
            q=Queue(s);q.Enqueue("a");q.Step(s,Actor,0,0);q.Step(s,Actor,10,0);q.Step(s,Actor,30,0);
            desired=q.Project(s,Player(s),out visible);Check(!desired.Contains("a")&&visible==25,"paused purchase must reconcile with visible error");
        });
        Console.WriteLine("RESULT " + cases + " cases / " + checks + " checks"); return 0;
    }
}
