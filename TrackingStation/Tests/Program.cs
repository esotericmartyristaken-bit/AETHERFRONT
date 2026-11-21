using Aetherfront.Core;
using Aetherfront.Tracking;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Server.Client;
using Server.Message;
using Server.System;
using Server.System.Vessel.Classes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};

int assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    assertions++;
}

var state = new AetherfrontState();
state.Factions.Add(new FactionRecord { Id = "visible", PublicProfile = true });
state.Factions.Add(new FactionRecord { Id = "private", PublicProfile = false });
var vessels = new Dictionary<Guid, Vessel>();
var removed = new Dictionary<Guid, byte>();
Guid Add(string faction, bool surface = false, bool archived = false, string owner = "other", string sma = "700000")
{
    var id = Guid.NewGuid();
    state.Crafts.Add(new CraftRecord { Id = id.ToString("N"), Name = "Marker " + faction, Archived = archived,
        OwnerAccountId = owner, OwnerFactionId = faction });
    vessels.Add(id, Save(surface, sma));
    return id;
}

var orbit = Add("visible");
var landed = Add("visible", true);
var hidden = Add("private");
var archived = Add("visible", archived: true);
var own = Add("visible", owner: "viewer");
var destroyed = Add("visible");
removed[destroyed] = 1;
var invalid = Add("visible", sma: "NaN");
var absent = Add("visible");
vessels.Remove(absent);
DateTime now = DateTime.UtcNow;
var rows = TrackingCatalog.Build(state, "viewer", 4, now, vessels, removed);
Check(rows.Select(m => Guid.Parse(m.Id)).ToHashSet().SetEquals(new[] { orbit, landed }), "public offline only; no private, archived, own, destroyed, malformed or absent vessels");
Check(rows.Single(m => m.Id == orbit.ToString("N")).Position[2] == 700000, "orbital elements projected");
Check(rows.Single(m => m.Id == landed.ToString("N")).Position.SequenceEqual(new[] { 12.5, -45.0, 105.0 }), "landed position projected");
Check(!TrackingCatalog.TryReply(null, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest }), "ordinary snapshots still use native dispatch");
Check(!TrackingCatalog.TryReply(null, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.CommandRequest, Name = Wire.Route }), "commands cannot enter read-only route");
Check(TrackingCatalog.TryReply(null, new AetherfrontWireRequest { Operation = AetherfrontWireOperation.SnapshotRequest, Name = Wire.Route }), "malformed tracking poll is consumed without fallback");

var privateCraft = state.Crafts.Single(c => c.Id == hidden.ToString("N"));
privateCraft.AccessGrants.Add(new CraftAccessGrant { GranteeAccountId = "viewer", Permissions = new List<string> { CraftAccess.ViewMetadata }, ExpiresAtUtc = now.AddMinutes(1) });
var subspaces = (IDictionary<Guid, int>)typeof(VesselMsgReader).GetField("VesselSubspaces", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
subspaces[hidden] = 4;
Check(TrackingCatalog.Build(state, "viewer", 4, now, vessels, removed).Any(m => m.Id == hidden.ToString("N")), "explicit metadata grant in matching subspace");
Check(!TrackingCatalog.Build(state, "viewer", 5, now, vessels, removed).Any(m => m.Id == hidden.ToString("N")), "private grant does not expose other subspace");
Check(!TrackingCatalog.Build(state, "viewer", 4, now.AddMinutes(2), vessels, removed).Any(m => m.Id == hidden.ToString("N")), "expired grant revoked");
subspaces.Remove(hidden);
Check(!TrackingCatalog.Build(state, "viewer", 4, now, vessels, removed).Any(m => m.Id == hidden.ToString("N")), "unknown private timeline fails closed");
var json = AetherfrontJson.Serialize(rows);
Check(AetherfrontJson.Deserialize<List<Marker>>(json).Count == 2, "wire roundtrip");
Check(!json.Contains("PART") && !json.Contains("FLIGHTPLAN") && !json.Contains("owner@example"), "marker wire contains no vessel payload");
Check(TrackingCatalog.Revision(json) == TrackingCatalog.Revision(AetherfrontJson.Serialize(TrackingCatalog.Build(state, "viewer", 4, now, vessels, removed))), "stable revision suppresses unchanged catalogs");
state.Factions.Single(f => f.Id == "visible").PublicProfile = false;
Check(TrackingCatalog.Build(state, "viewer", 4, now, vessels, removed).Count == 0, "public profile revocation removes markers");
state.Factions.Single(f => f.Id == "visible").PublicProfile = true;

var store = (AetherfrontServerStore)RuntimeHelpers.GetUninitializedObject(typeof(AetherfrontServerStore));
typeof(AetherfrontServerStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(store, new object());
typeof(AetherfrontServerStore).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(store, state);
VesselStoreSystem.CurrentVessels[orbit] = vessels[orbit];
Check(store.ReadTrackingCatalog("viewer", 4).Single().Id == orbit.ToString("N"), "grafted store projection reads existing state and vessel store");
VesselContext.RemovedVessels[orbit] = 1;
Check(store.ReadTrackingCatalog("viewer", 4).Count == 0, "grafted store respects tombstones");
VesselContext.RemovedVessels.TryRemove(orbit, out _);

var isolatedRoot = Path.Combine(AppContext.BaseDirectory, "world-" + Guid.NewGuid().ToString("N"));
var realStore = new AetherfrontServerStore(isolatedRoot, "tracking-test");
typeof(AetherfrontServerSystem).GetField("_store", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, realStore);
typeof(AetherfrontServerStore).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(realStore, state);
var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
client.PlayerName = "Track" + Guid.NewGuid().ToString("N").Substring(0, 9);
client.ConnectionStatus = ConnectionStatus.Connected;
client.Subspace = 4;
var queue = new ConcurrentQueue<IServerMessageBase>();
typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, queue);
var factory = new ClientMessageFactory();
using var key = RSA.Create(2048);
var hello = factory.CreateNewMessageData<HandshakeRequestMsgData>();
hello.IdentityPublicKey = IdentityProof.ExportPublicKey(key);
hello.UniqueIdentifier = client.UniqueIdentifier = IdentityProof.AccountId(hello.IdentityPublicKey);
hello.PlayerName = client.PlayerName;
hello.KspVersion = "1.12.5";
hello.ApfEnabled = true;
hello.ApfClientVersion = "kesas-rc64";
_ = key.SignData(new byte[32], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
Check(IdentityAuthentication.Begin(client, hello, out var nonce, out var beginReason), "isolated identity challenge: " + beginReason);
Check(IdentityAuthentication.Complete(client, IdentityProof.Sign(key, nonce, hello.UniqueIdentifier, client.PlayerName), out _, out var proofReason), "isolated identity proof: " + proofReason);
client.Authenticated = true;
Check(AetherfrontServerSystem.BindSession(client), "isolated session bound");
var reader = new AetherfrontMsgReader();
var traffic = typeof(AetherfrontMsgReader).Assembly.GetType("Aetherfront.Synchronization.Server.ServerTraffic", true);
var states = traffic.GetField("States", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
var getState = states.GetType().GetMethod("TryGetValue");
bool Take(out IServerMessageBase reply)
{
    reply = null;
    var lookup = new object[] { client, null };
    if (!(bool)getState.Invoke(states, lookup)) return false;
    var peer = lookup[1];
    var scheduler = peer.GetType().GetField("Queue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(peer);
    var arguments = new object[] { new Func<IMessageBase, int, bool>((_, _) => true), null };
    // No socket is opened; inspect the actual scheduled packet before transport budgeting.
    if (!(bool)scheduler.GetType().GetMethod("TryTake").Invoke(scheduler, arguments)) return false;
    reply = (IServerMessageBase)arguments[1];
    return true;
}
void Request(string revision, string token)
{
    var message = factory.CreateNewMessageData<AetherfrontCommandMsgData>();
    message.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireRequest {
        Operation = AetherfrontWireOperation.SnapshotRequest, Name = Wire.Route,
        Description = revision, CommandId = token, AccountId = "untrusted-request-account"
    });
    reader.HandleMessage(client, factory.CreateNew<AetherfrontCliMsg>(message));
}
AetherfrontWireReply Dequeue()
{
    Check(Take(out var reply), "native server queued response; disconnect=" + client.DisconnectClient);
    var result = AetherfrontProtocol.DeserializeReply(((AetherfrontCommandReplyMsgData)reply.Data).PayloadJson);
    reply.Recycle();
    return result;
}
var token = Guid.NewGuid().ToString("N");
Request("-", token);
var full = Dequeue();
Check(full.ReasonCode == Wire.Route && full.TradeId == token && Wire.IsRevision(full.FactionId) &&
    string.IsNullOrEmpty(full.StateJson) && AetherfrontJson.Deserialize<List<Marker>>(full.InterestJson).Count == 1,
    "authenticated native request returns only the marker catalog");
Request("-", Guid.NewGuid().ToString("N"));
Check(!Take(out _), "duplicate polling rate-limited");
Thread.Sleep(2200);
Request(full.FactionId, Guid.NewGuid().ToString("N"));
var unchanged = Dequeue();
Check(unchanged.FactionId == full.FactionId && string.IsNullOrEmpty(unchanged.InterestJson), "unchanged revision sends no catalog");
VesselContext.RemovedVessels[orbit] = 1;
Thread.Sleep(2200);
Request(full.FactionId, Guid.NewGuid().ToString("N"));
var deleted = Dequeue();
Check(deleted.FactionId != full.FactionId && AetherfrontJson.Deserialize<List<Marker>>(deleted.InterestJson).Count == 0,
    "deleted marker clears from the next complete revision");

var fixture = vessels[landed];
var shared = new string('x', 64);
for (int i = 0; i < 5000; i++)
{
    Guid id = Guid.NewGuid();
    state.Crafts.Add(new CraftRecord { Id = id.ToString("N"), Name = shared, OwnerFactionId = i % 2 == 0 ? "visible" : "private" });
    vessels.Add(id, fixture);
}
var timer = Stopwatch.StartNew();
var large = TrackingCatalog.Build(state, "viewer", 4, now, vessels, removed);
timer.Stop();
Check(large.Count == Wire.Limit, "large fleet bounded");
Check(AetherfrontJson.Serialize(large).Length < Wire.MaxPayload, "large catalog fits transport bound");
Console.WriteLine($"{assertions} checks passed. 5,000 extra craft projected in {timer.Elapsed.TotalMilliseconds:F1} ms (isolated, not a live benchmark).");

static Vessel Save(bool surface, string sma) => new Vessel($@"
landed = {surface}
splashed = False
lat = 12.5
lon = -45
alt = 105
ORBIT
{{
 INC = 2.5
 ECC = 0.02
 SMA = {sma}
 LAN = 0
 LPE = 0
 MNA = 0.4
 EPH = 50000
 REF = 1
 IDENT = Squad/Kerbin
}}
ACTIONGROUPS
{{
}}
DISCOVERY
{{
}}
FLIGHTPLAN
{{
}}
CTRLSTATE
{{
}}
VESSELMODULES
{{
}}
");
