using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Aetherfront.Core;
using Server.Aetherfront;
using Server.Client;

internal static class VisibilityScaleTests
{
    internal static void Run(ClientStructure owner,ClientStructure outsider,string evidence,Action<bool,string> check)
    {
        // Cloned metadata only: no persisted state or repeated vessel payloads.
        var state=AetherfrontServerSystem.Snapshot();
        string account=AetherfrontServerSystem.GetBoundAccountId(owner);
        var ids=new Guid[5000];
        for(int index=0;index<ids.Length;index++)
        {
            ids[index]=Guid.NewGuid();
            state.Crafts.Add(new CraftRecord{Id=ids[index].ToString(),Name="Visibility fixture "+index,OwnerAccountId=account,OwnerFactionId=string.Empty});
        }
        var method=typeof(Server.Message.VesselMsgReader).GetMethod("CanViewVessel",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(ClientStructure),typeof(Guid),typeof(AetherfrontState)},null);
        var allows=method.CreateDelegate<Func<ClientStructure,Guid,AetherfrontState,bool>>();
        int outsiderVisible=0,ownerVisible=0;
        var timer=Stopwatch.StartNew();foreach(var id in ids)if(allows(outsider,id,state))outsiderVisible++;timer.Stop();double outsiderMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();foreach(var id in ids)if(allows(owner,id,state))ownerVisible++;timer.Stop();double ownerMs=timer.Elapsed.TotalMilliseconds;
        check(outsiderVisible==0,"5000 private ships cannot leak full vessel synchronization to outsider");
        check(ownerVisible==ids.Length,"5000 owned ships preserve authorized visibility");
        File.WriteAllText(Path.Combine(evidence,"visibility-scale.json"),JsonSerializer.Serialize(new{count=ids.Length,outsiderVisible,ownerVisible,outsiderMs,ownerMs,scope="actual native visibility predicate over cloned metadata; no UDP/Unity/frame-time claim"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Visibility metadata benchmark: outsider "+outsiderMs.ToString("F1")+" ms, owner "+ownerMs.ToString("F1")+" ms for "+ids.Length+" records.");
    }
}
