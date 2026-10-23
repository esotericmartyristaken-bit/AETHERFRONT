using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using LmpCommon;
using Server.Aetherfront;

internal static class DeepFreezeNativeRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"Fixtures/DeepFreezeNative");
        Guid id=new Guid("ab7a5d6a-fd9d-4818-8750-69bd97035ad1");const string name="Jebediah Kerman";
        byte[] Read(string file)=>File.ReadAllBytes(Path.Combine(root,file));
        var currentBytes=Read("previous-scenario.txt");var incomingBytes=Read("Scenario.txt");
        DeepFreezeScenarioCodec.Snapshot Parse(byte[] bytes)=>DeepFreezeScenarioCodec.Parse(bytes,bytes.Length);
        var previous=new Dictionary<Guid,byte[]>{{id,Read("previous-vessel-"+id+".txt")}};
        var vessels=new Dictionary<Guid,byte[]>{{id,Read("vessel-"+id+".txt")}};
        var crew=new Dictionary<string,byte[]>{{name,Read("crew-Jebediah.txt")}};
        var result=AetherfrontDeepFreezeCheckpointValidator.Validate(Parse(currentBytes),Parse(incomingBytes),vessels,crew,previous);
        check(result.Kerbals[name].Type=="Unowned"&&result.Kerbals[name].Status=="Dead","captured actual native freeze tuple accepted unchanged");
        var old=Parse(incomingBytes);old.Kerbals[name].Status="Missing";
        var oldCrew=new Dictionary<string,byte[]>{{name,Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(crew[name]).Replace("state = Dead","state = Missing"))}};
        check(AetherfrontDeepFreezeCheckpointValidator.Validate(Parse(currentBytes),old,vessels,oldCrew,previous).Kerbals[name].Status=="Missing","legacy Missing frozen tuple remains readable without rewriting native statuses");
        void Refused(Action action,string reason){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}check(rejected,reason);}
        foreach(string type in new[]{"Crew","Applicant","Tourist"})
        {
            var bad=Parse(incomingBytes);bad.Kerbals[name].Type=type;
            Refused(()=>AetherfrontDeepFreezeCheckpointValidator.Validate(Parse(currentBytes),bad,vessels,crew,previous),"captured frozen roster cannot substitute type "+type);
        }
        var wrongRoster=new Dictionary<string,byte[]>{{name,Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(crew[name]).Replace("state = Dead","state = Available"))}};
        Refused(()=>AetherfrontDeepFreezeCheckpointValidator.Validate(Parse(currentBytes),Parse(incomingBytes),vessels,wrongRoster,previous),"captured freeze still requires matching roster status");
        string before=Encoding.UTF8.GetString(previous[id]);
        check(before.Contains("crew = "+name),"captured previous ship actually contains authorized awake crew");
        var unassigned=new Dictionary<Guid,byte[]>{{id,Encoding.UTF8.GetBytes(before.Replace("crew = "+name,"crew = Someone Else"))}};
        Refused(()=>AetherfrontDeepFreezeCheckpointValidator.Validate(Parse(currentBytes),Parse(incomingBytes),vessels,crew,unassigned),"new frozen identity still requires its previous vessel assignment");
        var foreign=Parse(incomingBytes);foreign.Kerbals[name].VesselGuid=Guid.NewGuid();
        Refused(()=>AetherfrontDeepFreezeCheckpointValidator.Validate(Parse(currentBytes),foreign,vessels,crew,previous),"captured freeze cannot claim a different vessel");
        Console.WriteLine("PASS native DeepFreeze captured-tuple and unchanged validation boundaries regression.");
    }
}
