using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Data-only, local-operator compensation. This is not a gameplay grant API.
public static class ScienceCompensation
{
    static void Require(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
    static void Unique(JsonElement value){
        if(value.ValueKind==JsonValueKind.Object){
            var names=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in value.EnumerateObject()){Require(names.Add(p.Name),"Duplicate JSON field");Unique(p.Value);}
        }else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item);
    }
    static JsonNode Parse(string text){using(var doc=JsonDocument.Parse(text))Unique(doc.RootElement);return JsonNode.Parse(text);}
    public static string ValidateIntent(string text,string version){
        Require(System.Text.Encoding.UTF8.GetByteCount(text)<=4096,"Compensation plan too large");
        var p=Parse(text).AsObject();
        var fields=new[]{"schemaVersion","applicationVersion","serverId","transactionId","accountId","playerName","amount"};
        Require(p.Count==fields.Length && p.All(x=>fields.Contains(x.Key)),"Unexpected compensation fields");
        Require(p["schemaVersion"].GetValue<int>()==1 && p["applicationVersion"].GetValue<string>()==version && Regex.IsMatch(version,@"^kesas-rc[1-9][0-9]{0,14}$"),"Compensation version differs");
        Require(Regex.IsMatch(p["serverId"].GetValue<string>(),@"^[a-z0-9-]{1,64}$") && Regex.IsMatch(p["accountId"].GetValue<string>(),@"^key-[a-f0-9]{64}$") && Regex.IsMatch(p["playerName"].GetValue<string>(),@"^[A-Za-z0-9_-]{1,64}$"),"Invalid compensation identity");
        Require(Regex.IsMatch(p["transactionId"].GetValue<string>(),@"^[a-z0-9][a-z0-9-]{0,79}$"),"Invalid compensation transaction");
        decimal amount=p["amount"].GetValue<decimal>();Require(amount>0 && amount<=10000,"Compensation amount outside bound");
        return p.ToJsonString();
    }
    public static string Build(string stateText,string bindingText,string intentText,string version){
        var p=Parse(ValidateIntent(intentText,version));var state=Parse(stateText);var binding=Parse(bindingText);
        string id=p["accountId"].GetValue<string>(),name=p["playerName"].GetValue<string>();
        Require(state["ServerId"].GetValue<string>()==p["serverId"].GetValue<string>(),"Compensation world differs");
        Require(binding["version"].GetValue<int>()==1,"Identity schema differs");
        var bindings=binding["accounts"].AsArray().Where(x=>x["accountId"].GetValue<string>()==id || x["playerNames"].AsArray().Any(n=>string.Equals(n.GetValue<string>(),name,StringComparison.OrdinalIgnoreCase))).ToArray();
        Require(bindings.Length==1 && bindings[0]["accountId"].GetValue<string>()==id && bindings[0]["playerNames"].AsArray().Any(n=>string.Equals(n.GetValue<string>(),name,StringComparison.OrdinalIgnoreCase)),"Compensation identity binding changed");
        var accounts=state["Accounts"].AsArray().Where(x=>x["AccountId"].GetValue<string>()==id || string.Equals(x["DisplayName"].GetValue<string>(),name,StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(accounts.Length==1 && accounts[0]["AccountId"].GetValue<string>()==id && accounts[0]["DisplayName"].GetValue<string>()==name && !accounts[0]["Suspended"].GetValue<bool>(),"Compensation account changed");
        var progression=state["PersonalProgression"];Require(progression["Version"].GetValue<int>()==1,"Ledger schema differs");
        var players=progression["Players"].AsArray();
        Require(players.Select(x=>x["AccountId"].GetValue<string>()).Distinct(StringComparer.Ordinal).Count()==players.Count,"Duplicate player ledger");
        var matches=players.Where(x=>x["AccountId"].GetValue<string>()==id).ToArray();Require(matches.Length==1,"Compensation ledger missing");var player=matches[0];
        decimal before=player["Science"].GetValue<decimal>(),tradable=player["TradableScience"].GetValue<decimal>(),earned=player["EarnedScience"].GetValue<decimal>(),shared=player["SharedScienceCredited"].GetValue<decimal>(),total=progression["TotalEarnedScience"].GetValue<decimal>();
        decimal pending=decimal.Round((total-earned)/2m,28,MidpointRounding.ToEven)-shared,after=checked(before+p["amount"].GetValue<decimal>());
        Require(before>=0 && tradable>=0 && tradable<=before && earned>=0 && total>=earned && shared>=0 && pending>=0 && after+pending<=1000000000000m,"Invalid balance or entitlement");
        long revision=state["Revision"].GetValue<long>(),sequence=state["StoreCommitSequence"].GetValue<long>();Require(revision>=0 && sequence>=0,"Invalid durable state counters");
        var next=state.DeepClone();var nextPlayer=next["PersonalProgression"]["Players"].AsArray().Single(x=>x["AccountId"].GetValue<string>()==id);
        nextPlayer["Science"]=after;next["Revision"]=checked(revision+1);next["StoreCommitSequence"]=checked(sequence+1);
        var restored=next.DeepClone();restored["Revision"]=state["Revision"].DeepClone();restored["StoreCommitSequence"]=state["StoreCommitSequence"].DeepClone();
        restored["PersonalProgression"]["Players"].AsArray().Single(x=>x["AccountId"].GetValue<string>()==id)["Science"]=player["Science"].DeepClone();
        Require(JsonNode.DeepEquals(restored,state),"Unexpected compensation difference");
        string result=next.ToJsonString();Require(JsonNode.DeepEquals(next,Parse(result)),"Compensation serialization changed state");return result;
    }
}
