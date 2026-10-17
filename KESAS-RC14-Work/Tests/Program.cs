using Aetherfront.Core;
using LmpClient;
using LmpClient.Aetherfront;
using LmpClient.Base;
using LmpClient.Systems.ShareTechnology;
using LmpCommon.Enums;

int checks = 0;
void Require(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
void Reset()
{
    MainSystem.NetworkState = ClientState.Disconnected;
    ProgressionMirror.Tick();
    MainSystem.NetworkState = ClientState.Running;
    AetherfrontClientSystem.State = new AetherfrontState();
    HighLogic.CurrentGame = new Game();
    HighLogic.LoadedScene = GameScenes.SpaceCentre;
    ResearchAndDevelopment.Instance = new ResearchAndDevelopment();
    ResearchAndDevelopment.TechWrites = ResearchAndDevelopment.TreeRefreshes = ResearchAndDevelopment.ScienceWrites = 0;
    ResearchAndDevelopment.Refreshed = null;
    AssetBase.RnDTechTree = new TechTree();
    System<ShareTechnologySystem>.Singleton.IgnoreEvents = false;
    Funding.Instance = new Funding();
    Funding.Writes = Reputation.Writes = 0;
    Reputation.Instance = new Reputation();
}
FactionRecord Join(string id = "faction")
{
    var faction = new FactionRecord { Id = id, Progression = new FactionProgression
        { Science = 25, TechnologyUnlocks = new List<string> { "start", "tech-1" } } };
    AetherfrontClientSystem.State.Faction = faction;
    return faction;
}

Reset();
ResearchAndDevelopment.Instance.Nodes["start"] = new ProtoTechNode { techID = "start", state = RDTech.State.Available };
for (int frame = 0; frame < 1000; frame++)
{
    RequireIfFalse(!ProgressionMirror.CanRequest("start"), "Factionless event requested a mutation");
    ProgressionMirror.Apply();
}
Require(ResearchAndDevelopment.TechWrites == 0 && ResearchAndDevelopment.TreeRefreshes == 0 &&
        ResearchAndDevelopment.ScienceWrites == 0, "1000 factionless frames preserve stock tree and science");

Reset();
var faction = Join();
bool callbacksSuppressed = false;
ResearchAndDevelopment.Refreshed = () =>
{
    callbacksSuppressed = System<ShareTechnologySystem>.Singleton.IgnoreEvents;
    if (ProgressionMirror.CanRequest("tech-2")) throw new Exception("Refresh scheduled another apply");
    ProgressionMirror.Apply(); // Reentrant plugin callbacks must not start a second pass.
};
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 1 && ResearchAndDevelopment.TechWrites == 180,
    "180-node initial faction state uses one batched refresh");
Require(callbacksSuppressed && !System<ShareTechnologySystem>.Singleton.IgnoreEvents,
    "Refresh callbacks are suppressed; previous sharing state restored");
for (int frame = 0; frame < 1000; frame++) ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 1 && ResearchAndDevelopment.ScienceWrites == 1,
    "1000 identical snapshots cause no extra writes or refreshes");
Require(!ProgressionMirror.CanRequest("start") && !ProgressionMirror.CanRequest("tech-1"),
    "Authoritative stock initialization events do not send purchase requests");

faction.Progression.Science = 30;
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.ScienceWrites == 2 && ResearchAndDevelopment.TreeRefreshes == 1,
    "Science-only change avoids rebuilding tech tree");
faction.Progression.TechnologyUnlocks.Add("tech-2");
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 2 && ResearchAndDevelopment.TechWrites == 181,
    "One technology unlock changes one node and refreshes once");

ResearchAndDevelopment.Instance.Nodes["tech-3"] = new ProtoTechNode { techID = "tech-3", state = RDTech.State.Available };
Require(ProgressionMirror.CanRequest("tech-3"), "New player research still reaches server");
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 3 &&
        ResearchAndDevelopment.Instance.Nodes["tech-3"].state == RDTech.State.Unavailable,
    "Unapproved stock unlock reconciles once until server accepts");
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 3, "Rejected research does not restart refresh loop");

faction.Progression.TechnologyUnlocks.Remove("tech-2");
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.Instance.Nodes["tech-2"].state == RDTech.State.Unavailable &&
        ResearchAndDevelopment.TreeRefreshes == 4, "Authoritative technology removal relocks once");

HighLogic.LoadedScene = GameScenes.Flight;
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 4, "Scene change without state delta avoids rebuild");
ResearchAndDevelopment.Instance = new ResearchAndDevelopment();
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 5, "New R&D instance receives progression once");

Join("another-faction");
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 6, "Faction change reapplies owned technology once");
AetherfrontClientSystem.State.Faction = null;
for (int frame = 0; frame < 1000; frame++) ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 6, "Leaving faction does not create empty-tree loop");

Reset();
faction = Join();
HighLogic.CurrentGame.Mode = 1;
faction.Progression.FundsMicro = 123456789;
faction.Progression.Reputation = 8;
for (int frame = 0; frame < 1000; frame++) ProgressionMirror.Apply();
Require(Funding.Writes == 1 && Reputation.Writes == 1 && Funding.Instance.Funds == 123.456789,
    "Career balances change once and retain microfund conversion");

Reset();
Join();
AssetBase.RnDTechTree = null;
ProgressionMirror.Apply();
AssetBase.RnDTechTree = new TechTree();
ProgressionMirror.Apply();
Require(ResearchAndDevelopment.TreeRefreshes == 1, "Delayed tree availability retries without caching incomplete pass");
System<ShareTechnologySystem>.Singleton.IgnoreEvents = true;
ProgressionMirror.CanRequest("tech-3");
ProgressionMirror.Apply();
Require(System<ShareTechnologySystem>.Singleton.IgnoreEvents, "Preexisting event suppression is preserved");

Console.WriteLine($"All {checks} progression regressions passed.");
static void RequireIfFalse(bool condition, string message) { if (!condition) throw new Exception(message); }
