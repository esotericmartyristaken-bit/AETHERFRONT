using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aetherfront.Continuity;

internal static class Program
{
    static int checks;
    static void Require(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
        Require(rejected, message);
    }

    static int Main(string[] args)
    {
        if (args.Length == 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APF_CONTINUITY_EVIDENCE")))
            args = new[] { Environment.GetEnvironmentVariable("APF_CONTINUITY_EVIDENCE") };
        if (args.Length != 1) throw new ArgumentException("Explicit disposable evidence directory required");
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        string run = Path.Combine(root, "clock-" + Guid.NewGuid().ToString("N"));
        var store = new ClockStore(run);
        Require(store.Resume("alice", 1234.25) == 1234.25, "first migration time");
        store.Checkpoint("alice", 1300.5);
        Require(store.Resume("bob", 90000000) == 90000000, "independent account");
        store.Checkpoint("bob", 999000000);
        Require(new ClockStore(run).Resume("alice", 999000000) == 1300.5, "restart/offline/peer warp must not advance Alice");
        Require(store.Resume("Alice", 55) == 55, "ordinal account IDs");
        Require(new ClockStore(Path.Combine(run, "other-universe")).Resume("alice", 77) == 77, "universe isolation");
        Reject(() => store.Checkpoint("alice", 100), "backwards checkpoint");
        Reject(() => store.Checkpoint("alice", double.NaN), "NaN");
        Reject(() => store.Checkpoint("alice", double.PositiveInfinity), "infinity");
        Reject(() => store.Resume("alice", -1), "negative initial time");
        Reject(() => store.Checkpoint("alice", ClockStore.MaximumTime + 1), "time bound");
        Reject(() => store.Resume("", 1), "unauthenticated account");
        Reject(() => store.Resume(new string('a', 513), 1), "account bound");
        Require(store.Resume("../../outside", 16) == 16, "untrusted characters are hashed, never paths");
        foreach (var file in Directory.GetFiles(Path.Combine(run, "AetherfrontTime")))
            Require(Path.GetFileName(file).Length == 70 && Path.GetFileName(file).EndsWith(".clock", StringComparison.Ordinal), "fixed hashed name");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            store.Checkpoint("alice", 1350.125);
            Require(new ClockStore(run).Resume("alice", 0) == 1350.125, "culture independent");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Parallel.For(0, 128, i => { string account = "parallel-" + i; store.Resume(account, i); store.Checkpoint(account, i + 1); });
        for (int i = 0; i < 128; i++) Require(store.Resume("parallel-" + i, 999) == i + 1, "parallel independent accounts");
        Require(!Directory.GetFiles(Path.Combine(run, "AetherfrontTime"), "*.tmp").Any(), "no incomplete temporary files");
        string corruptRoot = Path.Combine(run, "corrupt");
        var corrupt = new ClockStore(corruptRoot);
        corrupt.Resume("alice", 100);
        string corruptFile = Directory.GetFiles(Path.Combine(corruptRoot, "AetherfrontTime")).Single();
        string original = File.ReadAllText(corruptFile);
        File.WriteAllText(corruptFile, original.Replace("100\n", "200\n"));
        Reject(() => corrupt.Resume("alice", 999), "corrupt clock must not silently join latest time");
        File.WriteAllText(corruptFile, new string('x', 513));
        Reject(() => corrupt.Resume("alice", 999), "oversized record bounded");
        File.WriteAllText(corruptFile, "");
        Reject(() => corrupt.Resume("alice", 999), "truncated record");
        Require(AuthorityPolicy.MaySimulate("alice", "alice", "bob", "bob", true, "alice"), "control lock wins during handoff");
        Require(!AuthorityPolicy.MaySimulate("bob", "alice", "bob", "bob", true, "alice"), "stale update owner cannot double simulate");
        Require(AuthorityPolicy.MaySimulate("alice", "", "alice", "bob", true, "alice"), "loaded update authority");
        Require(!AuthorityPolicy.MaySimulate("alice", "", "alice", "alice", false, "alice"), "loaded lock cannot authorize background processing");
        Require(AuthorityPolicy.MaySimulate("alice", "", "", "alice", false, "alice"), "unloaded update authority");
        Require(!AuthorityPolicy.MaySimulate("alice", "", "", "alice", true, "alice"), "unloaded lock cannot authorize loaded simulation");
        Require(!AuthorityPolicy.MaySimulate("alice", "", "", "", true, "alice"), "visible unowned vessel frozen");
        Require(!AuthorityPolicy.MaySimulate("", "", "", "", false, "alice"), "missing player frozen");
        Require(!AuthorityPolicy.MaySimulate("Alice", "alice", "", "", true,"alice"), "ordinal server lock identity");
        Require(!AuthorityPolicy.MaySimulate("bob","","","bob",false,"alice"),"automatic faction background lock does not advance paused creator's survival clock");
        Require(!AuthorityPolicy.MaySimulate("bob","","bob","",true,"alice"),"nearby shared vessel stays frozen without deliberate pilot handoff");
        Require(AuthorityPolicy.MaySimulate("bob","bob","alice","alice",true,"alice"),"deliberate faction pilot handoff can bind the survival clock");
        Require(!AuthorityPolicy.MaySimulate("bob","","","bob",false,""),"legacy unknown survival owner requires deliberate control before background simulation");
        foreach (string control in new[] { "", "alice", "bob", "carol" })
            foreach (string update in new[] { "", "alice", "bob", "carol" })
                foreach (string unloaded in new[] { "", "alice", "bob", "carol" })
                    foreach (bool loaded in new[] { false, true })
                    foreach (string survivalOwner in new[] { "", "alice", "bob", "carol" })
                    {
                        int authorities = new[] { "alice", "bob", "carol" }.Count(player =>
                            AuthorityPolicy.MaySimulate(player, control, update, unloaded, loaded, survivalOwner));
                        Require(authorities <= 1, "at most one authority for every lock handoff combination");
                    }
        var survival=new SurvivalEpoch("alice",100000000,90000000);
        Require(survival.Now("alice",100000100)==90000100,"own elapsed time advances survival");
        survival.Checkpoint("alice",100000100);
        survival.Bind("bob",100);
        Require(survival.Now("bob",100)==90000100,"transfer to early player preserves positive survival epoch");
        Require(survival.Now("bob",150)==90000150,"only new authority elapsed time counts");
        survival.Checkpoint("bob",150);
        survival.Bind("alice",100000200);
        Require(survival.Now("alice",100000200)==90000150,"return transfer does not consume time from other player's independent warp");
        var resumedSurvival=new SurvivalEpoch(survival.Owner,survival.OwnerUT,survival.SurvivalUT);
        Require(resumedSurvival.Now("alice",100000200)==90000150,"offline restart preserves epoch");
        resumedSurvival.Bind("alice",100000210);
        Require(resumedSurvival.Now("alice",100000210)==90000160,"same authority rebind retains elapsed time");
        Reject(()=>resumedSurvival.Now("alice",100000000),"backwards survival time refused");
        Reject(()=>resumedSurvival.Bind("bob",double.NaN),"invalid authority time refused");
        Require(survival.Now("alice",100000210)-89900000==100160,"habitation age remains meaningful across clocks");
        var recovered=new SurvivalEpoch("alice",350,1000350);
        Require(recovered.RecoverLoadedCheckpoint("alice",340),"snapshot ahead of crashed account checkpoint can recover");
        Require(recovered.Now("alice",340)==1000350,"crash recovery never refunds consumed survival time");
        Require(recovered.Now("alice",350)==1000360,"recovered clock advances without duplicate catch-up");
        Require(!recovered.RecoverLoadedCheckpoint("bob",100),"recovery cannot bind another authority");
        Require(!recovered.RecoverLoadedCheckpoint("alice",360),"ordinary load retains elapsed catch-up");
        var packet=new object();
        var bytes=System.Text.Encoding.UTF8.GetBytes("SurvivalClock\n{\n APF_SURVIVAL\n {\n owner = alice\n ownerUT = 1234.5\n }\n}\n");
        ResourceClockPayloads.Set(packet,bytes);bytes[0]=0;
        Require(ResourceClockPayloads.Get(packet)[0]==(byte)'S',"payload immutable after capture");
        var copy=ResourceClockPayloads.Get(packet);copy[0]=0;
        Require(ResourceClockPayloads.Get(packet)[0]==(byte)'S',"payload immutable across serializer callers");
        Require(ResourceClockPayloads.Size(packet)==ResourceClockPayloads.Get(packet).Length+4,"wire size includes length header");
        foreach(var invalid in new[]{"A\n{\nx = 1\n", "A\n{\nx = {}\n}\n", "A\n{\nx = a\u0000b\n}\n",new string('x',1025),"A\n{\nx = "+new string('x',513)+"\n}\n", "A\n{\nB\nC\n{\n}\n}\n"})
            Reject(()=>ResourceClockPayloads.Set(packet,System.Text.Encoding.UTF8.GetBytes(invalid)),"malformed clock text refused before parser");
        Reject(()=>ResourceClockPayloads.Set(packet,new byte[ResourceClockPayloads.MaximumBytes+1]),"oversized clock payload refused");
        Reject(()=>ResourceClockPayloads.Set(packet,new byte[]{0xff}),"invalid UTF-8 refused before parser");
        ResourceClockPayloads.Clear(packet);
        Require(ResourceClockPayloads.Size(packet)==4&&ResourceClockPayloads.Get(packet).Length==0,"pooled message metadata explicitly cleared");
        Console.WriteLine("PASS " + checks + " clock persistence assertions. Evidence: " + run);
        return 0;
    }
}
