namespace Aetherfront.Core
{
    public class FactionProgression
    {
        public long Science, FundsMicro, Reputation;
        public List<string> TechnologyUnlocks = new();
    }
    public class FactionRecord { public string Id; public FactionProgression Progression; }
    public class AetherfrontState
    {
        public string ServerId = "server";
        public FactionRecord Faction;
        public FactionRecord FindFactionForAccount(string id) => Faction;
    }
}
namespace LmpCommon.Enums { public enum ClientState { Disconnected, Running } }
namespace LmpClient
{
    public static class MainSystem
    {
        public static LmpCommon.Enums.ClientState NetworkState = LmpCommon.Enums.ClientState.Running;
        public static string UniqueIdentifier = "alice";
    }
    public static class LunaLog { public static void LogError(string error) => throw new Exception(error); }
}
namespace LmpClient.Apf { public static class ApfClientSettings { public static bool Enabled = true; } }
namespace LmpClient.Aetherfront
{
    public static class AetherfrontClientSystem
    {
        public static global::Aetherfront.Core.AetherfrontState State = new();
        public static bool Ready => true;
    }
}
namespace LmpClient.Base
{
    public class ShareSystem
    {
        public bool IgnoreEvents;
        public void StartIgnoringEvents() { IgnoreEvents = true; }
        public void StopIgnoringEvents(bool restoreOldValue = false) { IgnoreEvents = false; }
    }
    public static class System<T> where T : new() { public static T Singleton = new(); }
}
namespace LmpClient.Systems.ShareScience { public class ShareScienceSystem : LmpClient.Base.ShareSystem { } }
namespace LmpClient.Systems.ShareTechnology { public class ShareTechnologySystem : LmpClient.Base.ShareSystem { } }
namespace LmpClient.Systems.ShareFunds { public class ShareFundsSystem : LmpClient.Base.ShareSystem { } }
namespace LmpClient.Systems.ShareReputation { public class ShareReputationSystem : LmpClient.Base.ShareSystem { } }
public enum GameScenes { SpaceCentre, Flight }
public class Game { public int Mode = 2; }
public static class HighLogic { public static Game CurrentGame = new(); public static GameScenes LoadedScene; }
public enum TransactionReasons { None }
public class AvailablePart { }
public class RDTech { public enum State { Unavailable, Available } }
public class ProtoTechNode
{
    public string techID;
    public int scienceCost;
    public RDTech.State state;
    public List<AvailablePart> partsPurchased = new();
}
public class ResearchAndDevelopment
{
    public static ResearchAndDevelopment Instance = new();
    public float Science;
    public static int ScienceWrites, TechWrites, TreeRefreshes;
    public static Action Refreshed;
    public Dictionary<string, ProtoTechNode> Nodes = new();
    public ProtoTechNode GetTechState(string id) => Nodes.TryGetValue(id, out var node) ? node : null;
    public void SetTechState(string id, ProtoTechNode node) { TechWrites++; Nodes[id] = node; }
    public void UnlockProtoTechNode(ProtoTechNode node)
    {
        SetTechState(node.techID, new ProtoTechNode { techID = node.techID, state = RDTech.State.Available });
    }
    public void SetScience(float value, TransactionReasons reason) { ScienceWrites++; Science = value; }
    public static void RefreshTechTreeUI() { TreeRefreshes++; Refreshed?.Invoke(); }
}
public class TechTree
{
    public ProtoTechNode[] Nodes = Enumerable.Range(0, 180)
        .Select(index => new ProtoTechNode { techID = index == 0 ? "start" : "tech-" + index }).ToArray();
    public ProtoTechNode[] GetTreeTechs() => Nodes;
}
public static class AssetBase { public static TechTree RnDTechTree = new(); }
public class Funding
{
    public static Funding Instance = new();
    public double Funds;
    public static int Writes;
    public void SetFunds(double value, TransactionReasons reason) { Writes++; Funds = value; }
}
public class Reputation
{
    public static Reputation Instance = new();
    public float reputation;
    public static int Writes;
    public void SetReputation(float value, TransactionReasons reason) { Writes++; reputation = value; }
}
namespace KSP.UI.Screens
{
    public class EditorPartList
    {
        public static EditorPartList Instance;
        public static int Refreshes;
        public void Refresh() { Refreshes++; }
    }
}
