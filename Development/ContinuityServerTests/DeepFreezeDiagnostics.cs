using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using LmpCommon;
using Server.Aetherfront;

// Read-only reproduction against the actual validator. Never starts a server,
// commits a world, changes a roster or bypasses the production rejection.
internal static class DeepFreezeDiagnostics
{
    internal static void Run(string capture,string runtime)
    {
        var pack=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(runtime,"Config/APFPack.json")));
        if(pack.RootElement.GetProperty("serverId").GetString()!="aetherfront-continuity-df-test")throw new IOException("Only isolated fixture permitted");
        string evidence=Path.Combine(capture,"validation-diagnostic.json");
        if(File.Exists(evidence))throw new IOException("Preserve diagnostic evidence");
        var candidate=Directory.GetFiles(capture,"vessel-*.txt").ToDictionary(p=>Guid.Parse(Path.GetFileNameWithoutExtension(p).Substring(7)),File.ReadAllBytes);
        var crew=new Dictionary<string,byte[]>{{"Jebediah Kerman",File.ReadAllBytes(Path.Combine(capture,"crew-Jebediah.txt"))}};
        byte[] currentBytes=File.ReadAllBytes(Path.Combine(runtime,"Universe/DeepFreeze/Scenario.txt")),incomingBytes=File.ReadAllBytes(Path.Combine(capture,"Scenario.txt"));
        var current=DeepFreezeScenarioCodec.Parse(currentBytes,currentBytes.Length);var incoming=DeepFreezeScenarioCodec.Parse(incomingBytes,incomingBytes.Length);
        var read=typeof(AetherfrontDeepFreezeCheckpointValidator).Assembly.GetType("Server.Aetherfront.AetherfrontDeepFreezeCheckpointCoordinator",true).GetMethod("ReadAssignments",BindingFlags.Static|BindingFlags.NonPublic);
        var previous=new Dictionary<Guid,AetherfrontDeepFreezeCheckpointValidator.VesselAssignments>();
        foreach(var file in Directory.GetFiles(Path.Combine(runtime,"Universe/Vessels"),"*.txt"))
        {
            var id=Guid.Parse(Path.GetFileNameWithoutExtension(file));var text=File.ReadAllText(file);
            previous.Add(id,(AetherfrontDeepFreezeCheckpointValidator.VesselAssignments)read.Invoke(null,new object[]{id,new Server.System.Vessel.Classes.Vessel(text)}));
            if(candidate.ContainsKey(id))File.WriteAllText(Path.Combine(capture,"previous-vessel-"+id+".txt"),text);
        }
        File.WriteAllBytes(Path.Combine(capture,"previous-scenario.txt"),currentBytes);
        bool accepted=false;string error=null;
        try{AetherfrontDeepFreezeCheckpointValidator.ValidateWithAssignments(current,incoming,candidate,crew,previous);accepted=true;}
        catch(InvalidDataException e){error=e.Message;Console.WriteLine(e.ToString());}
        File.WriteAllText(evidence,System.Text.Json.JsonSerializer.Serialize(new{utc=DateTime.UtcNow,accepted,error,scope="native validator only, canonical world unchanged"},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Native validator accepted="+accepted+"; reason="+error);
    }
}
