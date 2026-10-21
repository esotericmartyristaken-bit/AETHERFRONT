using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Collections.Generic;
using LmpCommon;
using LmpCommon.Message;
using LmpCommon.Message.Data.Handshake;
using Aetherfront.Core;
using Aetherfront.Personal.Server;
using LmpCommon.Enums;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;

static class Program
{
    static int checks;
    static readonly Dictionary<string, ClientStructure> clients = new Dictionary<string, ClientStructure>();
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static ClientStructure Client(string account)
    {
        if (clients.TryGetValue(account, out var existing)) return existing;
#pragma warning disable SYSLIB0050
        var client = (ClientStructure)FormatterServices.GetUninitializedObject(typeof(ClientStructure));
#pragma warning restore SYSLIB0050
        client.Subspace = int.MinValue;
        typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, new ConcurrentQueue<IServerMessageBase>());
        using var key = RSA.Create(2048);
        var request = new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
        request.IdentityPublicKey = IdentityProof.ExportPublicKey(key); request.UniqueIdentifier = IdentityProof.AccountId(request.IdentityPublicKey);
        request.PlayerName = "PT_" + account; request.KspVersion = "1.12.5"; request.ApfEnabled = true; request.ApfClientVersion = "kesas-rc31";
        client.UniqueIdentifier = request.UniqueIdentifier; client.PlayerName = request.PlayerName;
        Check(Server.System.IdentityAuthentication.Begin(client, request, out var nonce, out var reason), "identity begin: " + reason);
        Check(Server.System.IdentityAuthentication.Complete(client, IdentityProof.Sign(key, nonce, request.UniqueIdentifier, request.PlayerName), out _, out reason), "identity proof: " + reason);
        client.Authenticated = true;
        Check(AetherfrontServerSystem.BindSession(client), "authenticated account binding");
        clients.Add(account, client);
        return client;
    }
    static void Init(string root) => typeof(AetherfrontServerSystem).GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null).Invoke(null, new object[] { root, "personal-integration" });
    static PlayerProgression Player(string name) => PersonalProgression.Ensure(AetherfrontServerSystem.Snapshot(), Client(name).UniqueIdentifier);
    static AetherfrontWireRequest Claim(string subject, string amount) => new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = (AetherfrontCommandType)40, EntityId = subject, Body = amount, AccountId = "forged-account", CommandId = Guid.NewGuid().ToString("N") };
    static AetherfrontResult Apply(ClientStructure client, AetherfrontWireRequest request)
    {
        request.CommandId = string.IsNullOrEmpty(request.CommandId) ? Guid.NewGuid().ToString("N") : request.CommandId;
        var apply = typeof(Server.Message.AetherfrontMsgReader).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic);
        return AetherfrontServerSystem.Execute(s => (AetherfrontResult)apply.Invoke(null, new object[] { s, request, client.UniqueIdentifier }));
    }
    static void Main(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathRooted(args[0]) || !Path.IsPathRooted(args[1])) throw new Exception("Explicit new disposable workspace root and read-only runtime required");
        string runtime = Path.GetFullPath(args[1]);
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(runtime, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new Exception("Do not overwrite existing test evidence");
        Directory.CreateDirectory(root);
        ServerContext.ConfigDirectory = Path.Combine(root, "Config"); Directory.CreateDirectory(ServerContext.ConfigDirectory);
        ServerContext.UniverseDirectory = Path.Combine(root, "Universe"); Directory.CreateDirectory(ServerContext.UniverseDirectory);
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AetherfrontScienceSubjects.json"), "{\"Version\":2,\"Subjects\":[{\"Id\":\"crewReport@KerbinSrfLandedKSC\",\"Cap\":1000},{\"Id\":\"evaReport@KerbinSrfLandedKSC\",\"Cap\":50},{\"Id\":\"repeat@KerbinSrfLanded\",\"Cap\":10}],\"Experiments\":[{\"Id\":\"crewReport\",\"Cap\":5,\"ApplyScienceScale\":true},{\"Id\":\"repeat\",\"Cap\":10,\"ApplyScienceScale\":false}]}");
        GeneralSettings.SettingsStore.GameMode = GameMode.Science;
        GameplaySettings.SettingsStore.StartingFunds = 25000; GameplaySettings.SettingsStore.StartingScience = 5; GameplaySettings.SettingsStore.ScienceGainMultiplier = 1;
        try
        {
            Init(root);
            AetherfrontServerSystem.Execute(s => { s.TechnologyDefinitions.Add(new TechnologyDefinition { Id = "personal-test-node", ScienceCost = 30 }); return AetherfrontResult.Success(s.Revision); });
            var a = Client("alice"); var b = Client("bob");
            Check(!PersonalServer.TryHandle(a, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest }), "snapshot must continue through original handler");
            Check(Player("alice").FundsMicro == 25000000000L && Player("alice").Science == 5, "server starting balances");
            Check(PersonalServer.TryHandle(a, Claim("crewReport@KerbinSrfLandedKSC", "100")), "claim handled");
            Check(Player("alice").Science == 105, "earner retains full award");
            Check(PersonalProgression.Find(AetherfrontServerSystem.Snapshot(), "forged-account") == null, "wire account must be ignored");
            PersonalServer.TryHandle(b, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest });
            Check(Player("bob").Science == 55, "other player baseline");
            PersonalServer.TryHandle(a, Claim("crewReport@KerbinSrfLandedKSC", "100"));
            Check(Player("alice").Science == 105, "claim replay cannot mint");
            PersonalServer.TryHandle(a, Claim("crewReport@KerbinSrfLandedKSC", "1000000"));
            Check(Player("alice").Science == 105, "cap reject");
            PersonalServer.TryHandle(a, Claim("not-a-subject", "40"));
            Check(Player("alice").Science == 105, "unknown subject reject");
            PersonalServer.TryHandle(b, Claim("crewReport@KerbinSrfLandedKSC", "100"));
            PersonalServer.TryHandle(a, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest });
            Check(Player("alice").Science == 155 && Player("bob").Science == 155, "universal science without factions");
            PersonalServer.TryHandle(b, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = AetherfrontCommandType.UnlockTechnology, TechnologyId = "personal-test-node", Amount2 = 1 });
            Check(Player("bob").Science == 125, "personal technology cost");
            Check(!Player("alice").TechnologyUnlocks.Contains("personal-test-node"), "no shared faction technology");
            PersonalServer.TryHandle(b, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = AetherfrontCommandType.UnlockTechnology, TechnologyId = "personal-test-node", Amount2 = 1 });
            Check(Player("bob").Science == 125, "technology retry cannot debit twice");
            var projected = PersonalServer.Filter(AetherfrontServerSystem.Snapshot(), b.UniqueIdentifier);
            Check(projected.PersonalProgression.Players.Count == 1 && projected.PersonalProgression.Players[0].AccountId == b.UniqueIdentifier, "personal privacy");
            Check(PersonalServer.TryHandle(a, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = AetherfrontCommandType.TransferFunds, Amount = 1, TargetFactionId = "other" }), "retired faction economy intercepted");
            Check(Player("alice").FundsMicro == 25000000000L, "retired path cannot debit personal funds");
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            PersonalServer.TryHandle(a, Claim("evaReport@KerbinSrfLandedKSC", "50"));
            Check(Player("alice").Science == 155, "Sandbox cannot generate science");
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            AetherfrontServerSystem.Shutdown(); Init(root);
            PersonalServer.TryHandle(a, Claim("crewReport@KerbinSrfLandedKSC", "100"));
            Check(Player("alice").Science == 155, "server restart replay");
            PersonalServer.TryHandle(Client("newcomer"), new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest });
            Check(Player("newcomer").Science == 105, "newcomer receives all historical shares plus starting science");
            string factionA = Apply(a, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateFaction, Name = "PersonalAlpha", Abbreviation = "PAL", ColorHex = "#AAAAAA", RecruitmentPolicy = RecruitmentPolicies.Open }).FactionId;
            string factionB = Apply(b, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateFaction, Name = "PersonalBeta", Abbreviation = "PBE", ColorHex = "#BBBBBB", RecruitmentPolicy = RecruitmentPolicies.Open }).FactionId;
            Check(!string.IsNullOrEmpty(factionA) && !string.IsNullOrEmpty(factionB), "personal trade factions");
            var newcomer = Client("newcomer");
            string factionC = Apply(newcomer, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateFaction, Name = "PersonalGamma", Abbreviation = "PGA", ColorHex = "#CCCCCC", RecruitmentPolicy = RecruitmentPolicies.Open }).FactionId;
            var freeOffer = Apply(newcomer, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateTradeOffer, FactionId = factionC, TargetFactionId = factionA, AssetJson = AetherfrontJson.Serialize(new[] { new TradeAsset { Type = TradeAssetTypes.Science, Amount = 1 } }), CounterAssetJson = "[]" });
            Check(!freeOffer.Accepted, "new-account baseline cannot fund an actual trade");
            Check(Player("newcomer").Science == 105 && Player("newcomer").TradableScience == 0, "rejected free-science trade preserves research balance");
            var legacy = AetherfrontServerSystem.Snapshot();
            long legacyAFunds = legacy.FindFaction(factionA).Progression.FundsMicro;
            long legacyAScience = legacy.FindFaction(factionA).Progression.Science;
            var give = new[] { new TradeAsset { Type = TradeAssetTypes.FundsMicro, Amount = 1000000 }, new TradeAsset { Type = TradeAssetTypes.Science, Amount = 10 } };
            var proposal = Apply(a, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateTradeOffer, FactionId = factionA, TargetFactionId = factionB, AssetJson = AetherfrontJson.Serialize(give), CounterAssetJson = "[]" });
            Check(proposal.Accepted, "personal funded trade proposal: " + proposal.ReasonCode);
            Check(Apply(a, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.ConfirmTrade, TradeId = proposal.TradeId }).Accepted, "proposer confirmation");
            var outsider = Client("charlie");
            Check(Apply(outsider, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.JoinFaction, FactionId = factionA, DisplayName = "Charlie" }).Accepted, "officer joins");
            Check(Apply(a, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.SetMemberRole, FactionId = factionA, TargetAccountId = outsider.UniqueIdentifier, Role = FactionRoles.Officer }).Accepted, "officer role");
            var hijack = Apply(outsider, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.ConfirmTrade, TradeId = proposal.TradeId });
            Check(!hijack.Accepted && hijack.ReasonCode == "OnlyPersonalSponsorCanConfirm", "officer cannot spend another player's balance");
            var confirm = new AetherfrontWireRequest { CommandType = AetherfrontCommandType.ConfirmTrade, TradeId = proposal.TradeId, CommandId = "personal-settle" };
            var settled = Apply(b, confirm);
            Check(settled.Accepted, "recipient confirmation: " + settled.ReasonCode);
            Check(Player("alice").Science == 145 && Player("bob").Science == 135, "personal science settlement");
            Check(Player("alice").TradableScience == 90 && Player("bob").TradableScience == 110, "trade moves only earned-science provenance");
            Check(Player("alice").FundsMicro == 24999000000L && Player("bob").FundsMicro == 25001000000L, "personal funds settlement");
            Check(Player("newcomer").Science == 105, "trade must never produce shared science");
            Apply(b, confirm);
            Check(Player("alice").Science == 145 && Player("bob").Science == 135, "trade replay cannot settle twice");
            var afterTrade = AetherfrontServerSystem.Snapshot();
            Check(afterTrade.FindFaction(factionA).Progression.FundsMicro == legacyAFunds && afterTrade.FindFaction(factionA).Progression.Science == legacyAScience, "compatibility view must restore historical faction balances before commit");
            var mixedOffer = Apply(b, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateTradeOffer, FactionId = factionB, TargetFactionId = factionA, AssetJson = AetherfrontJson.Serialize(new[] { new TradeAsset { Type = TradeAssetTypes.Science, Amount = 111 } }), CounterAssetJson = "[]" });
            Check(!mixedOffer.Accepted, "actual trade cannot supplement earned credit with shared credit");
            AetherfrontServerSystem.Shutdown(); Init(root);
            Check(Player("alice").TradableScience == 90 && Player("bob").TradableScience == 110 && Player("newcomer").TradableScience == 0, "tradable provenance survives actual server restart");
            AetherfrontServerSystem.Execute(s => { PersonalProgression.Ensure(s, b.UniqueIdentifier).Science = PersonalProgression.MaxScience; return AetherfrontResult.Success(s.Revision); });
            var overflowing = Apply(a, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.CreateTradeOffer, FactionId = factionA, TargetFactionId = factionB, AssetJson = AetherfrontJson.Serialize(new[] { new TradeAsset { Type = TradeAssetTypes.Science, Amount = 1 } }), CounterAssetJson = "[]" });
            Check(overflowing.Accepted, "bounded gift proposed before recipient consent");
            Check(Apply(a, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.ConfirmTrade, TradeId = overflowing.TradeId }).Accepted, "bounded gift proposer confirms");
            long beforeOverflowRevision = AetherfrontServerSystem.Snapshot().Revision;
            bool overflowRejected = false;
            try { Apply(b, new AetherfrontWireRequest { CommandType = AetherfrontCommandType.ConfirmTrade, TradeId = overflowing.TradeId }); }
            catch (TargetInvocationException error) { overflowRejected = error.GetBaseException().Message.Contains("Personal trade balance limit"); }
            Check(overflowRejected, "recipient overflow aborts entire settlement");
            var rolledBack = AetherfrontServerSystem.Snapshot();
            Check(rolledBack.Revision == beforeOverflowRevision && rolledBack.FindTrade(overflowing.TradeId).State != TradeStates.Committed, "overflow cannot persist revision or vessel-trade commit");
            Check(Player("alice").Science == 145 && Player("alice").TradableScience == 90 && Player("bob").Science == PersonalProgression.MaxScience && Player("bob").TradableScience == 110, "overflow leaves both personal balances intact");
            Check(rolledBack.FindFaction(factionA).Progression.Science == legacyAScience && rolledBack.FindFaction(factionA).Progression.FundsMicro == legacyAFunds, "overflow leaves historical faction balance intact");
            AetherfrontServerSystem.Shutdown(); Init(root);
            Check(AetherfrontServerSystem.Snapshot().FindTrade(overflowing.TradeId).State != TradeStates.Committed && Player("alice").Science == 145, "aborted settlement remains rolled back after restart");
            AetherfrontServerSystem.Execute(s => { PersonalProgression.Ensure(s, b.UniqueIdentifier).Science = 135; return AetherfrontResult.Success(s.Revision); });
            var labReceipt = new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, CommandType = (AetherfrontCommandType)42, EntityId = "award:" + Guid.NewGuid().ToString("N"), Body = "500", Amount2 = 0x400, AccountId = "forged-account" };
            Check(PersonalServer.TryHandle(a, labReceipt), "lab reward handled on authenticated transport");
            Check(Player("alice").Science == 645 && Player("alice").TradableScience == 590, "full lab reward is personal and tradable");
            Check(Player("bob").Science == 385 && Player("newcomer").Science == 355, "lab rewards shared across factions and new-player baseline");
            AetherfrontServerSystem.Shutdown(); Init(root);
            PersonalServer.TryHandle(a, labReceipt);
            Check(Player("alice").Science == 645 && Player("bob").Science == 385, "lab replay after server restart cannot grant twice");
            labReceipt.Body = "501"; PersonalServer.TryHandle(a, labReceipt);
            Check(Player("alice").Science == 645, "lab receipt amount cannot be edited");
            labReceipt.EntityId = "award:" + Guid.NewGuid().ToString("N"); labReceipt.Amount2 = 0x10000; PersonalServer.TryHandle(a, labReceipt);
            Check(Player("alice").Science == 645, "cheat reason rejected at server boundary");
            string vesselId = Guid.NewGuid().ToString(), leaseVesselId = Guid.NewGuid().ToString();
            string vesselHash = new string('a',64);
            AetherfrontServerSystem.Execute(s => {
                foreach(string id in new[]{vesselId,leaseVesselId}) s.Crafts.Add(new CraftRecord { Id=id,Name="Paid fixture vessel",OwnerFactionId=factionA,CreatorAccountId=a.UniqueIdentifier,Revision=1,ContentSha256=vesselHash });
                return AetherfrontResult.Success(s.Revision);
            });
            var purchase = new AetherfrontWireRequest { Operation=AetherfrontWireOperation.CommandRequest,CommandType=AetherfrontCommandType.CreateTradeOffer,FactionId=factionB,TargetFactionId=factionA,ExpectedRevision=0,
                AssetJson=AetherfrontJson.Serialize(new[]{new TradeAsset{Type=TradeAssetTypes.Science,Amount=100},new TradeAsset{Type=TradeAssetTypes.FundsMicro,Amount=3000000}}),
                CounterAssetJson=AetherfrontJson.Serialize(new[]{new TradeAsset{Type=TradeAssetTypes.Craft,EntityId=vesselId,CraftRevision=1,ContentSha256=vesselHash}}) };
            Check(!PersonalServer.TryHandle(b,purchase) && purchase.ExpectedRevision==-1,"unrelated science revisions do not invalidate pinned trades");
            var vesselPurchase=Apply(b,purchase);
            Check(vesselPurchase.Accepted,"paid vessel proposal: "+vesselPurchase.ReasonCode);
            Check(Apply(b,new AetherfrontWireRequest{CommandType=AetherfrontCommandType.ConfirmTrade,TradeId=vesselPurchase.TradeId}).Accepted,"buyer vessel confirmation");
            var controlLock=new LmpCommon.Locks.LockDefinition(LmpCommon.Locks.LockType.Control,a.PlayerName,Guid.Parse(vesselId));
            Check(Server.System.LockSystem.AcquireLock(controlLock,false,out _),"real vessel control lock acquired");
            var busySale=Apply(a,new AetherfrontWireRequest{CommandType=AetherfrontCommandType.ConfirmTrade,TradeId=vesselPurchase.TradeId});
            Check(!busySale.Accepted && busySale.ReasonCode=="VesselInUseParkBeforeChangingRights","in-use vessel rejects paid settlement");
            Check(AetherfrontServerSystem.Snapshot().FindCraft(vesselId).OwnerFactionId==factionA && Player("alice").Science==645 && Player("bob").Science==385,"in-use refusal preserves ownership and personal balances");
            Check(Server.System.LockSystem.ReleaseLock(controlLock),"fixture control lock released");
            var sale=Apply(a,new AetherfrontWireRequest{CommandType=AetherfrontCommandType.ConfirmTrade,TradeId=vesselPurchase.TradeId});
            Check(sale.Accepted,"seller vessel confirmation: "+sale.ReasonCode);
            Check(AetherfrontServerSystem.Snapshot().FindCraft(vesselId).OwnerFactionId==factionB,"paid vessel ownership transfers");
            Check(Player("alice").Science==745 && Player("alice").TradableScience==690 && Player("bob").Science==285 && Player("bob").TradableScience==10,"paid vessel settlement uses personal earned balances");
            Check(Player("newcomer").Science==355,"paid vessel sale does not create shared science");
            var lease=Apply(a,new AetherfrontWireRequest{CommandType=AetherfrontCommandType.CreateTradeOffer,FactionId=factionA,TargetFactionId=factionB,
                AssetJson=AetherfrontJson.Serialize(new[]{new TradeAsset{Type="VesselLease",EntityId=leaseVesselId,CraftRevision=1,ContentSha256=vesselHash,LeaseHours=1,Permissions=new List<string>{"Use"}}}),
                CounterAssetJson=AetherfrontJson.Serialize(new[]{new TradeAsset{Type=TradeAssetTypes.Science,Amount=5}})});
            Check(lease.Accepted,"paid lease proposal: "+lease.ReasonCode);
            Check(Apply(a,new AetherfrontWireRequest{CommandType=AetherfrontCommandType.ConfirmTrade,TradeId=lease.TradeId}).Accepted,"lease owner confirms");
            var leased=Apply(b,new AetherfrontWireRequest{CommandType=AetherfrontCommandType.ConfirmTrade,TradeId=lease.TradeId});
            Check(leased.Accepted,"lease renter confirms: "+leased.ReasonCode);
            var leasedState=AetherfrontServerSystem.Snapshot();
            Check(leasedState.FindCraft(leaseVesselId).OwnerFactionId==factionA && AetherfrontDomain.CanControlCraft(leasedState,b.UniqueIdentifier,factionB,leaseVesselId,"Use",DateTime.UtcNow),"paid lease grants use without transferring ownership");
            Check(Player("alice").Science==750 && Player("bob").Science==280 && Player("bob").TradableScience==5,"paid lease debits personal tradable science once");
            AetherfrontServerSystem.Execute(s => {foreach(var grant in s.FindCraft(leaseVesselId).AccessGrants)grant.ExpiresAtUtc=DateTime.UtcNow.AddSeconds(-1);return AetherfrontResult.Success(s.Revision);});
            Check(!AetherfrontDomain.CanControlCraft(AetherfrontServerSystem.Snapshot(),b.UniqueIdentifier,factionB,leaseVesselId,"Use",DateTime.UtcNow),"expired paid lease no longer authorizes control");
            PersonalServer.TryHandle(a,Claim("crewReport@KerbinSrfLandedKSC_AsteroidOne","5"));
            Check(Player("alice").Science==755 && Player("bob").Science==282.5m,"dynamic source uses registered experiment base cap");
            PersonalServer.TryHandle(a,Claim("crewReport@KerbinSrfLandedKSC_AsteroidOne","6"));
            Check(Player("alice").Science==755,"dynamic source cannot use body cap instead of base cap");
            PersonalServer.TryHandle(a,Claim("crewReport@MadeUpSrfLanded_AsteroidOne","1"));
            Check(Player("alice").Science==755,"unknown dynamic body rejected");
            PersonalServer.TryHandle(a,Claim("repeat@KerbinSrfLanded","100"));
            Check(Player("alice").Science==855 && Player("bob").Science==332.5m,"server-known unscaled experiment can continue beyond nominal cap");
            PersonalServer.TryHandle(a,Claim("repeat@KerbinSrfLanded","100"));
            Check(Player("alice").Science==855,"unscaled cumulative replay cannot mint");
            Console.WriteLine("PASS: " + checks + " actual server integration assertions. Unity and UDP tests are separate.");
        }
        finally { AetherfrontServerSystem.Shutdown(); }
    }
}
