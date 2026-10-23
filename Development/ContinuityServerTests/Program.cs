using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using Aetherfront.Continuity;
using Aetherfront.Continuity.Server;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Warp;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;

internal static class Program
{
    static int checks, port = 31000;
    static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    static ClientStructure Client(string name)
    {
#pragma warning disable SYSLIB0050
        var client = (ClientStructure)FormatterServices.GetUninitializedObject(typeof(ClientStructure));
#pragma warning restore SYSLIB0050
        client.Subspace = int.MinValue;
        client.ConnectionStatus = ConnectionStatus.Connected;
        typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(client, new ConcurrentQueue<IServerMessageBase>());
        using var key = RSA.Create(2048);
        var request = new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
        request.IdentityPublicKey = IdentityProof.ExportPublicKey(key);
        request.UniqueIdentifier = IdentityProof.AccountId(request.IdentityPublicKey);
        request.PlayerName = name; request.KspVersion = "1.12.5"; request.ApfEnabled = true; request.ApfClientVersion = "kesas-rc34";
        client.UniqueIdentifier = request.UniqueIdentifier; client.PlayerName = name;
        Check(Server.System.IdentityAuthentication.Begin(client, request, out var nonce, out var reason), "identity begin " + reason);
        Check(Server.System.IdentityAuthentication.Complete(client, IdentityProof.Sign(key, nonce, request.UniqueIdentifier, name), out _, out reason), "identity proof " + reason);
        client.Authenticated = true;
        Check(AetherfrontServerSystem.BindSession(client), "bound account");
        ServerContext.Clients.TryAdd(new IPEndPoint(IPAddress.Loopback, port++), client);
        return client;
    }
    static void Ensure(ClientStructure client) => typeof(PersonalClockServer).GetMethod("EnsureSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { client });
    static void Reject(ClientStructure client, string name)
    {
        bool rejected = false;
        try { Ensure(client); } catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException || error.InnerException is InvalidDataException) { rejected = true; }
        Check(rejected, name);
    }
    static double Time(ClientStructure client) => PersonalClockServer.ServerSeconds + WarpContext.Subspaces[client.Subspace].Time;
    static void Main(string[] args)
    {
        if(args.Length==4 && args[0]=="--diagnose-df")
        {
            string dependencyRoot=Path.GetFullPath(args[3]);
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(context,name)=>
            {
                string path=Path.Combine(dependencyRoot,name.Name+".dll");return File.Exists(path)?context.LoadFromAssemblyPath(path):null;
            };
            DeepFreezeDiagnostics.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));return;
        }
        if(args.Length==1 && args[0]=="--native-df-regression"){DeepFreezeNativeRegression.Run(Check);return;}
        if (args.Length != 2 || !Path.IsPathRooted(args[0]) || !Path.IsPathRooted(args[1])) throw new ArgumentException("New disposable root and read-only runtime required");
        string runtime = args[1];
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(runtime, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        string root = args[0];
        if (Directory.Exists(root)) throw new IOException("Do not overwrite evidence");
        Directory.CreateDirectory(root);
        ServerContext.ConfigDirectory = Path.Combine(root, "Config"); Directory.CreateDirectory(ServerContext.ConfigDirectory);
        ServerContext.UniverseDirectory = Path.Combine(root, "Universe"); Directory.CreateDirectory(ServerContext.UniverseDirectory);
        GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        GeneralSettings.SettingsStore.ModControl = false; // This synthetic protocol fixture has no KSP part database.
        typeof(AetherfrontServerSystem).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(string) }, null).Invoke(null, new object[] { root, "continuity-integration" });
        TimeContext.StartTime = DateTime.UtcNow;
        WarpContext.Subspaces.Clear(); WarpContext.Subspaces.TryAdd(0, new Subspace(0, 1000)); WarpContext.NextSubspaceId = 1;
        var alice = Client("ClockAlice"); var bob = Client("ClockBob");
        // Call the actual patched server entry point, not just the helper.
        Server.System.WarpSystemSender.SendAllSubspaces(alice);
        Check(alice.Subspace == 1, "personal subspace allocated");
        var reply = alice.SendMessageQueue.Select(m => m.Data).OfType<WarpSubspacesReplyMsgData>().Single();
        Check(reply.Subspaces.Single(s => s.SubspaceKey == alice.Subspace).Players.Contains(alice.PlayerName), "own authenticated selector transmitted");
        Check(Math.Abs(Time(alice) - 1000) < 5, "first migration retains current timeline");
        int aliceFirstSpace = alice.Subspace;
        Server.System.WarpSystemSender.SendAllSubspaces(alice);
        Check(alice.Subspace == aliceFirstSpace && WarpContext.Subspaces.Count == 2, "repeat startup cannot allocate another clock");
        Server.System.WarpSystemSender.SendAllSubspaces(bob);
        Check(alice.Subspace != bob.Subspace, "different personal subspaces");
        double before = Time(alice);
        WarpContext.Subspaces[bob.Subspace].Time += 100000000;
        Check(alice.Subspace != WarpContext.LatestSubspace.Id && PersonalClockServer.MayPersistPosition(alice), "authorized past-timeline position remains persistable");
        PersonalClockServer.Checkpoint(alice);
        var clock = new ClockStore(ServerContext.UniverseDirectory);
        double saved = clock.Resume(alice.AetherfrontAccountId, 999000000);
        Check(Math.Abs(saved - before) < 1, "peer warp did not advance saved time");
        PersonalClockServer.BeforeDisconnect(alice);
        saved = clock.Resume(alice.AetherfrontAccountId, 999000000);
        alice.Subspace = int.MinValue;
        // Simulated wall-clock/server restart gap without sleeping or changing OS time.
        TimeContext.StartTime = TimeContext.StartTime.AddDays(-90);
        Server.System.WarpSystemSender.SendAllSubspaces(alice);
        Check(Math.Abs(Time(alice) - saved) < 1, "rejoin restores paused UT after 90 days and peer warp");
        Check(alice.Subspace != aliceFirstSpace, "new live subspace uses restored time");
        var impostor = Client("ClockImpostor");
        impostor.UniqueIdentifier = alice.UniqueIdentifier; impostor.AetherfrontAccountId = alice.AetherfrontAccountId;
        Reject(impostor, "forged account properties cannot steal a saved clock");
        Check(!PersonalClockServer.MayPersistPosition(impostor), "unbound session cannot persist position");
        int previous = alice.Subspace;
        var unknown = new ClientMessageFactory().CreateNewMessageData<WarpChangeSubspaceMsgData>();
        unknown.PlayerName = alice.PlayerName; unknown.Subspace = int.MaxValue;
        new Server.System.WarpSystemReceiver().HandleChangeSubspace(alice, unknown);
        Check(alice.Subspace == previous, "hardened unknown-subspace rejection retained");
        unknown.PlayerName = bob.PlayerName; unknown.Subspace = bob.Subspace;
        new Server.System.WarpSystemReceiver().HandleChangeSubspace(alice, unknown);
        Check(alice.Subspace == previous, "hardened sender binding retained");
        // These fixtures deliberately have no Lidgren NetConnection. The
        // original relay compares its real transport endpoints; keep this
        // receiver/clock test free of fake endpoint equality. UDP tests cover relay.
        ServerContext.Clients.Clear();
        var warp = new ClientMessageFactory().CreateNewMessageData<WarpChangeSubspaceMsgData>();
        warp.PlayerName=alice.PlayerName; warp.Subspace=-1;
        new Server.System.WarpSystemReceiver().HandleChangeSubspace(alice,warp);
        Check(alice.Subspace==previous, "deployed protocol retains last stable subspace during warp");
        double beforeWarp=clock.Resume(alice.AetherfrontAccountId,999000000);
        // Defensive recovery for an explicitly unstable internal clock as well.
        alice.Subspace=-1;
        PersonalClockServer.Checkpoint(alice);
        Check(!alice.DisconnectClient, "periodic backup during warp does not disconnect player");
        PersonalClockServer.BeforeDisconnect(alice);
        Check(Math.Abs(clock.Resume(alice.AetherfrontAccountId,999000000)-beforeWarp)<0.01, "mid-warp disconnect preserves pre-warp checkpoint");
        alice.Subspace=int.MinValue;
        Server.System.WarpSystemSender.SendAllSubspaces(alice);
        Check(Math.Abs(Time(alice)-beforeWarp)<1, "mid-warp reconnect restores safe personal checkpoint");
        PersonalClockServer.CheckpointAll();
        Check(!Directory.GetFiles(Path.Combine(ServerContext.UniverseDirectory, "AetherfrontTime"), "*.tmp").Any(), "durable checkpoints completed");
        ResourceTests.Run(alice,bob,Check);
        VisibilityScaleTests.Run(alice,bob,root,Check);
        DeepFreezeCapabilities(root);
        DeepFreezeNativeRegression.Run(Check);
        Console.WriteLine("PASS " + checks + " authenticated runtime integration assertions. This is not a UDP or Unity test.");
    }
    static void DeepFreezeCapabilities(string evidenceRoot)
    {
        var root=Path.Combine(evidenceRoot,"deepfreeze-capabilities");
        Directory.CreateDirectory(Path.Combine(root,"Vessels"));
        Directory.CreateDirectory(Path.Combine(root,"Kerbals"));
        var type=typeof(ClientStructure).Assembly.GetType("Server.Aetherfront.AetherfrontDeepFreezeSystem",true);
        var flags=BindingFlags.Static|BindingFlags.NonPublic;
        var initialize=type.GetMethod("Initialize",flags);
        var connect=type.GetMethod("CanConnect",flags);
        initialize.Invoke(null,new object[]{root,true});
        try
        {
            var legacy=new object[]{0,null};
            Check(!(bool)connect.Invoke(null,legacy),"enabled native shared-world server rejects legacy capability");
            Check(!string.IsNullOrWhiteSpace((string)legacy[1]),"legacy capability has actionable rejection");
            Check((bool)connect.Invoke(null,new object[]{1,null}),"enabled native shared-world server accepts supported capability");
            Check(!(bool)connect.Invoke(null,new object[]{-1,null}),"negative capability rejected");
            Check(!(bool)connect.Invoke(null,new object[]{2,null}),"unknown future capability rejected");
        }
        finally{initialize.Invoke(null,new object[]{root,false});}
    }
}
