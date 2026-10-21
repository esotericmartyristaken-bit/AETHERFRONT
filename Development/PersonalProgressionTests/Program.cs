using System;
using System.Linq;
using Aetherfront.Core;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static AetherfrontState New(params string[] accounts)
    {
        var state = new AetherfrontState();
        foreach (var account in accounts) state.Accounts.Add(new AetherfrontAccountRecord { AccountId = account });
        state.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "start", Starter = true, ScienceCost = 0 });
        state.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "basic", ScienceCost = 30, PrerequisiteIds = new System.Collections.Generic.List<string> { "start" } });
        return state;
    }
    static void Earn(AetherfrontState s, string account, string source, decimal amount, decimal cap = 1000)
    { Check(PersonalProgression.RecordEarnedScience(s, account, source, amount, cap).Accepted, "earn rejected"); }
    public static void Main()
    {
        var s = New("A", "B", "offline", "newcomer");
        PersonalProgression.Ensure(s, "A", 1000);
        PersonalProgression.Ensure(s, "B", 1000);
        Earn(s, "A", "crewReport@KerbinSrfLanded", 100);
        Check(PersonalProgression.Ensure(s, "A").Science == 100, "earner must receive exactly 100");
        Check(PersonalProgression.Ensure(s, "B").Science == 50, "other player must receive 50");
        Check(PersonalProgression.Ensure(s, "offline").Science == 50, "offline catchup");
        Check(PersonalProgression.UnlockTechnology(s, "B", 1, "basic").Accepted, "personal purchase");
        Check(PersonalProgression.Ensure(s, "B").Science == 20, "spending stays spent");
        Check(!PersonalProgression.Ensure(s, "A").TechnologyUnlocks.Contains("basic"), "technology must be personal");
        Earn(s, "A", "crewReport@KerbinSrfLanded", 100);
        Check(PersonalProgression.Ensure(s, "B").Science == 20, "replay must not grant again");
        Earn(s, "B", "crewReport@KerbinSrfLanded", 100);
        Check(PersonalProgression.Ensure(s, "A").Science == 150, "cross-player sharing");
        Check(PersonalProgression.Ensure(s, "B").Science == 120, "no self-sharing");
        Check(PersonalProgression.Ensure(s, "newcomer").Science == 100, "new player receives historical baseline");
        Check(PersonalProgression.Ensure(s, "B").FundsMicro == 1000, "funds must not be shared");
        Check(!PersonalProgression.UnlockTechnology(s, "B", 1, "basic").Accepted, "replayed purchase");
        Check(PersonalProgression.Transfer(s, "A", 1, "B", 10, 100).Accepted, "personal transfer");
        Check(!PersonalProgression.Transfer(s, "A", 1, "B", 10, 100).Accepted, "replayed transfer");
        Check(PersonalProgression.Ensure(s, "newcomer").Science == 100, "transfers never shared");
        Check(PersonalProgression.Ensure(s, "A").FundsMicro == 900 && PersonalProgression.Ensure(s, "B").FundsMicro == 1100, "personal funds transfer");
        var json = AetherfrontJson.Serialize(s);
        s = AetherfrontJson.Deserialize<AetherfrontState>(json);
        Earn(s, "A", "crewReport@KerbinSrfLanded", 100);
        Check(PersonalProgression.Ensure(s, "B").Science == 130, "reconnect/restart replay");
        Earn(s, "A", "crewReport@KerbinSrfLanded", 101);
        Check(PersonalProgression.Ensure(s, "B").Science == 130.5m, "odd science half-point preserved");
        Earn(s, "A", "fractional", 0.000001m);
        Check(PersonalProgression.Ensure(s, "B").Science == 130.5000005m, "fractional sharing preserved");
        var before = s.PersonalProgression.TotalEarnedScience;
        Check(!PersonalProgression.RecordEarnedScience(s, "A", "bad", 1001, 1000).Accepted, "cap validation");
        Check(!PersonalProgression.RecordEarnedScience(s, "A", "bad", -1, 1000).Accepted, "negative award");
        Check(!PersonalProgression.RecordEarnedScience(s, "A", "bad", 0.00000001m, 1000).Accepted, "precision validation");
        Check(s.PersonalProgression.TotalEarnedScience == before, "invalid evidence cannot mutate total");
        bool denied = false;
        try { PersonalProgression.Ensure(s, "unauthenticated"); } catch (InvalidOperationException) { denied = true; }
        Check(denied, "unknown account rejected");
        s.FindAccount("offline").Suspended = true;
        denied = false;
        try { PersonalProgression.Ensure(s, "offline"); } catch (InvalidOperationException) { denied = true; }
        Check(denied, "suspended account rejected");
        var faction = new FactionRecord { Id = "f", Progression = new FactionProgression { Science = 900000, FundsMicro = 999999 } };
        faction.Members.Add(new FactionMember { AccountId = "A" });
        s.Factions.Add(faction);
        var balance = PersonalProgression.Ensure(s, "A").Science;
        faction.Members.Clear();
        Check(PersonalProgression.Ensure(s, "A").Science == balance, "faction changes do not change personal progression");
        Check(faction.Progression.Science == 900000, "historical faction evidence preserved");
        var configured = New("starter", "recipient");
        configured.PersonalProgression.StartingScience = 5;
        configured.PersonalProgression.StartingFundsMicro = 25000000000L;
        Check(PersonalProgression.Ensure(configured, "starter").Science == 5, "configured starting science");
        Earn(configured, "starter", "first", 100);
        Check(PersonalProgression.Ensure(configured, "recipient").Science == 55, "starting science must not be shared");
        Check(PersonalProgression.Ensure(configured, "recipient").FundsMicro == 25000000000L, "offline recipient receives configured starting funds");
        Check(PersonalProgression.Ensure(configured, "starter").TradableScience == 100, "only earned science is tradable");
        Check(PersonalProgression.Ensure(configured, "recipient").TradableScience == 0, "starting and historical shares are research-only");
        Check(!PersonalProgression.Transfer(configured, "recipient", 1, "starter", 1, 0).Accepted, "new-account baseline cannot be funnelled");
        Check(PersonalProgression.Ensure(configured, "recipient").NextCommandSequence == 1, "rejected baseline transfer does not consume sequence");
        Check(PersonalProgression.RecordEarnedScience(configured, "starter", "first", 100, 1000, 2).Accepted, "multiplier replay handled");
        Check(PersonalProgression.Ensure(configured, "starter").Science == 105, "multiplier changes must not reprice history");
        Check(PersonalProgression.RecordEarnedScience(configured, "starter", "first", 101, 1000, 2).Accepted, "new multiplied award");
        Check(PersonalProgression.Ensure(configured, "starter").Science == 107 && PersonalProgression.Ensure(configured, "recipient").Science == 56, "only new science uses changed multiplier");
        configured.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "or-node", ScienceCost = 1, AnyPrerequisite = true, PrerequisiteIds = new System.Collections.Generic.List<string> { "start", "missing" } });
        Check(PersonalProgression.UnlockTechnology(configured, "recipient", 1, "or-node").Accepted, "OR prerequisite semantics");
        Check(PersonalProgression.Ensure(configured, "recipient").TradableScience == 0, "research-only science remains usable for research");
        var restricted = New("earner", "receiver", "late");
        restricted.PersonalProgression.StartingScience = 20;
        Earn(restricted, "earner", "award", 100);
        Check(!PersonalProgression.Transfer(restricted, "earner", 1, "receiver", 101, 0).Accepted, "starting science cannot supplement a trade");
        Check(PersonalProgression.Transfer(restricted, "earner", 1, "receiver", 40, 0).Accepted, "earned science transfer");
        Check(PersonalProgression.Ensure(restricted, "receiver").Science == 110 && PersonalProgression.Ensure(restricted, "receiver").TradableScience == 40, "incoming earned trade retains provenance");
        Check(PersonalProgression.UnlockTechnology(restricted, "receiver", 1, "basic").Accepted, "research spends free credit first");
        Check(PersonalProgression.Ensure(restricted, "receiver").Science == 80 && PersonalProgression.Ensure(restricted, "receiver").TradableScience == 40, "research preserves earned balance while free credit remains");
        restricted.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "costly", ScienceCost = 50 });
        Check(PersonalProgression.UnlockTechnology(restricted, "receiver", 2, "costly").Accepted, "research can spend earned science too");
        Check(PersonalProgression.Ensure(restricted, "receiver").Science == 30 && PersonalProgression.Ensure(restricted, "receiver").TradableScience == 30, "research consumes earned balance after free credit");
        restricted = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(restricted));
        Check(PersonalProgression.Transfer(restricted, "receiver", 3, "earner", 30, 0).Accepted, "incoming science can be traded after restart");
        Check(!PersonalProgression.Transfer(restricted, "late", 1, "earner", 1, 0).Accepted, "newcomer cannot launder baseline after restart");
        Check(restricted.PersonalProgression.TotalEarnedScience == 100, "trades and research never generate shared credit");
        var labs = New("researcher", "peer", "late");
        string receipt = "award:" + Guid.NewGuid().ToString("N");
        Check(PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", receipt, 500, 0x400).Accepted, "lab science receipt");
        Check(PersonalProgression.Ensure(labs, "researcher").Science == 500 && PersonalProgression.Ensure(labs, "researcher").TradableScience == 500, "lab earner keeps full earned reward");
        Check(PersonalProgression.Ensure(labs, "peer").Science == 250 && PersonalProgression.Ensure(labs, "peer").TradableScience == 0, "lab reward shared research-only");
        labs = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(labs));
        Check(PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", receipt, 500, 0x400).Accepted && labs.PersonalProgression.TotalEarnedScience == 500, "lab receipt replay after restart");
        Check(PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", "award:"+receipt.Substring(6).ToUpperInvariant(), 500, 0x400).Accepted && labs.PersonalProgression.TotalEarnedScience == 500, "GUID casing cannot replay a receipt");
        Check(!PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", receipt, 501, 0x400).Accepted, "lab receipt amount cannot change");
        Check(!PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", "award:"+Guid.NewGuid().ToString("N"), 10, 0x4000).Accepted, "technology refund excluded");
        Check(!PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", "award:"+Guid.NewGuid().ToString("N"), 10, 0x10000).Accepted, "cheat award excluded");
        Check(!PersonalProgression.RecordGameplayScienceReceipt(labs, "researcher", "award:"+Guid.NewGuid().ToString("N"), 10, -1).Accepted, "unknown reason excluded");
        Check(PersonalProgression.Ensure(labs, "late").Science == 250, "newcomer receives lab historical baseline");
        var chunks = New("earner", "peer");
        var claim = new Aetherfront.Personal.Client.PendingScienceClaim { ServerId = "test", AccountId = "earner", Command = 42, SourceId = "award:"+Guid.NewGuid().ToString("N"), Amount = 2500000.5m, Reason = 0x400 };
        string firstChunkId = claim.GetChunkSourceId();
        Check(PersonalProgression.RecordGameplayScienceReceipt(chunks, "earner", firstChunkId, claim.GetChunkAmount(), claim.Reason).Accepted, "large reward first bounded chunk");
        Check(!claim.AcknowledgeChunk() && claim.Amount == 1500000.5m, "large reward retains remaining amount in one queue entry");
        string secondChunkId = claim.GetChunkSourceId();
        claim = AetherfrontJson.Deserialize<Aetherfront.Personal.Client.PendingScienceClaim>(AetherfrontJson.Serialize(claim));
        Check(claim.GetChunkSourceId() == secondChunkId && secondChunkId != firstChunkId, "chunk receipt remains deterministic after restart");
        Check(PersonalProgression.RecordGameplayScienceReceipt(chunks, "earner", firstChunkId, 1000000, 0x400).Accepted && chunks.PersonalProgression.TotalEarnedScience == 1000000, "restarting cannot replay an acknowledged chunk");
        Check(PersonalProgression.RecordGameplayScienceReceipt(chunks, "earner", secondChunkId, claim.GetChunkAmount(), claim.Reason).Accepted, "large reward second chunk");
        Check(!claim.AcknowledgeChunk(), "fractional final chunk retained");
        Check(PersonalProgression.RecordGameplayScienceReceipt(chunks, "earner", claim.GetChunkSourceId(), claim.GetChunkAmount(), claim.Reason).Accepted && claim.AcknowledgeChunk(), "large reward fractional completion");
        Check(PersonalProgression.Ensure(chunks, "earner").Science == 2500000.5m && PersonalProgression.Ensure(chunks, "peer").Science == 1250000.25m, "large rewards never lose progress or half-point shares");
        Check(!PersonalProgression.RecordEarnedScience(configured, "unknown", "first", 0, 1000).Accepted, "even duplicate/zero claims require authentication");
        var repeated = New("R", "S");
        Check(PersonalProgression.RecordEarnedScience(repeated,"R","repeat@KerbinSrfLanded",100,10,1,true).Accepted,"unscaled subject can exceed nominal cap");
        Check(PersonalProgression.RecordEarnedScience(repeated,"R","repeat@KerbinSrfLanded",120,10,1,true).Accepted,"unscaled subsequent increment");
        Check(PersonalProgression.Ensure(repeated,"S").Science==60,"unscaled awards shared exactly once");
        Check(PersonalProgression.RecordEarnedScience(repeated,"R","repeat@KerbinSrfLanded",100,10,1,true).Accepted && PersonalProgression.Ensure(repeated,"S").Science==60,"unscaled replay stays at high water");
        Check(!PersonalProgression.RecordEarnedScience(repeated,"R","ordinary",100,10).Accepted,"ordinary subject still bounded by cap");
        var rng = new Random(42);
        var stress = New("P0", "P1", "P2", "P3", "P4");
        decimal[] earned = new decimal[5];
        for (int n = 0; n < 2000; n++)
        {
            int p = rng.Next(5);
            decimal delta = rng.Next(1, 10000) / 1000m;
            earned[p] += delta;
            Earn(stress, "P" + p, "source", earned[p], 10000);
            if (n % 19 == 0) stress = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(stress));
            for (int i = 0; i < 5; i++)
            {
                Check(PersonalProgression.Ensure(stress, "P" + i).Science == earned[i] + (earned.Sum() - earned[i]) / 2, "ledger invariant at event " + n);
                Check(PersonalProgression.Ensure(stress, "P" + i).TradableScience == earned[i], "tradable provenance invariant at event " + n);
            }
        }
        Console.WriteLine("PASS: " + checks + " personal progression assertions; shipping integration not yet tested.");
    }
}
