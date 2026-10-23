using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Aetherfront.Continuity;
using Aetherfront.Continuity.Server;
using Lidgren.Network;
using LmpCommon.Locks;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using Server.Client;
using Server.Context;
using Server.System;

internal static class ResourceTests
{
    static NetOutgoingMessage Write()
    {
        var value=(NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage),true);
        typeof(NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,new byte[16]);return value;
    }
    static NetIncomingMessage Read(NetOutgoingMessage from)
    {
        var value=(NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage),true);
        typeof(NetBuffer).GetField("m_data",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,(byte[])from.Data.Clone());value.LengthBits=from.LengthBytes*8;return value;
    }
    internal static void Run(ClientStructure alice,ClientStructure bob,Action<bool,string> check)
    {
        var factory=new ClientMessageFactory();var id=Guid.NewGuid();
        var proto=factory.CreateNewMessageData<VesselProtoMsgData>();proto.VesselId=id;proto.GameTime=PersonalClockServer.ServerSeconds+WarpContext.Subspaces[alice.Subspace].Time;
        string template=@"pid = ID
name = Resource fixture
type = Ship
situation = PRELAUNCH
sit = PRELAUNCH
landed = True
lat = 0
lon = 0
alt = 75
PART
{
 name = mk1pod.v2
 uid = 1
 parent = 0
 MODULE
 {
  name = ModuleResourceConverter
  lastUpdateTime = 0
 }
 RESOURCE
 {
  name = Supplies
  amount = 100
  maxAmount = 100
  flowState = True
 }
}
ORBIT
{
 SMA = 700000
 ECC = 0
 INC = 0
 LPE = 0
 LAN = 0
 MNA = 0
 EPH = 0
 REF = 1
}
ACTIONGROUPS
{
}
DISCOVERY
{
}
FLIGHTPLAN
{
}
CTRLSTATE
{
}
VESSELMODULES
{
 SurvivalClock
 {
 }
 ModuleLifeSupportSystem
 {
  LastUpdateTime = 0
 }
}
".Replace("ID",id.ToString("N"));
        proto.Data=Encoding.UTF8.GetBytes(template);proto.NumBytes=proto.Data.Length;
        new Server.Message.VesselMsgReader().HandleMessage(alice,factory.CreateNew<VesselCliMsg>(proto));
        check(VesselStoreSystem.CurrentVessels.ContainsKey(id),"normal authenticated proto creates synthetic resource fixture");
        check(LockSystem.AcquireLock(new LockDefinition(LockType.Control,alice.PlayerName,id),false,out _),"resource fixture control lock acquired");
        var message=factory.CreateNewMessageData<VesselResourceMsgData>();message.VesselId=id;message.GameTime=proto.GameTime+0.1;
        message.ResourcesCount=1;message.Resources=new[]{new VesselResourceInfo{PartFlightId=1,ResourceName="Supplies",Amount=99.25,FlowState=true}};
        string time=message.GameTime.ToString("R",CultureInfo.InvariantCulture),survival=(message.GameTime+500).ToString("R",CultureInfo.InvariantCulture);
        string body=@"APF_RESOURCE_CLOCK
{
 schema = 1
 vesselId = @ID@
 gameTime = @TIME@
 SurvivalClock
 {
  APF_SURVIVAL
  {
   schema = 1
   owner = @OWNER@
   ownerUT = @TIME@
   survivalUT = @VIRTUAL_TIME@
  }
 }
 ModuleLifeSupportSystem
 {
  LastUpdateTime = @VIRTUAL_TIME@
 }
 PART_TIMER
 {
  uid = 1
  index = 0
  module = ModuleResourceConverter
  lastUpdateTime = @VIRTUAL_TIME@
 }
}
".Replace("@ID@",id.ToString()).Replace("@VIRTUAL_TIME@",survival).Replace("@TIME@",time).Replace("@OWNER@",alice.PlayerName);
        var payload=Encoding.UTF8.GetBytes(body);ResourceClockPayloads.Set(message,payload);
        var wire=Write();message.Serialize(wire);check(message.GetMessageSize()>=wire.LengthBytes,"patched resource message size budgets extension");
        var received=factory.CreateNewMessageData<VesselResourceMsgData>();received.Deserialize(Read(wire));
        check(ResourceClockPayloads.Get(received).SequenceEqual(payload),"actual patched server resource serializer carries metadata");
        check(received.ResourcesCount==1&&received.Resources[0].Amount==99.25,"existing resource fields survive extended framing");
        foreach(long revision in new[]{0L,17L,long.MaxValue})
        {
            message.SharedWorldRevision=revision;wire=Write();message.Serialize(wire);var input=Read(wire);received.Deserialize(input);
            check(received.SharedWorldRevision==revision && input.Position==input.LengthBits,"actual server outer shared revision reader preserved");
            check(ResourceClockPayloads.Get(received).SequenceEqual(payload),"actual server resource clock coexists with native shared revision");
        }
        message.SharedWorldRevision=received.SharedWorldRevision=-1;
        new Server.Message.VesselMsgReader().HandleMessage(alice,factory.CreateNew<VesselCliMsg>(received));
        var saved=VesselStoreSystem.CurrentVessels[id];
        check(saved.GetPart(1).Resources.GetSingle("Supplies").Value.GetValue("amount").Value=="99.25","original authorized reader persists resource amount");
        check(saved.VesselModules.GetNode("SurvivalClock").Value.GetNode("APF_SURVIVAL").Value.GetValue("survivalUT").Value==survival,"clock committed with same resource packet");
        check(saved.VesselModules.GetNode("ModuleLifeSupportSystem").Value.GetValue("LastUpdateTime").Value==survival,"life support timestamp paired with resources");
        check(saved.GetPart(1).Modules.GetAll()[0].Value.GetValue("lastUpdateTime").Value==survival,"converter timestamp paired with resources");
        string second=body.Replace("   schema = 1","   schema = 2\n   launchUT = 0");
        ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(second));
        check(ResourceClockServer.CommitResourceClock(alice,message),"authenticated legacy survival snapshot migrates to persisted launch anchor");
        check(saved.VesselModules.GetNode("SurvivalClock").Value.GetNode("APF_SURVIVAL").Value.GetValue("launchUT").Value=="0","virtual launch anchor persisted with resource transaction");
        string unchanged=saved.ToString();
        foreach(string anchor in new[]{"NaN","-1","1e99","99999999"})
        {
            ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(second.Replace("launchUT = 0","launchUT = "+anchor)));
            check(!ResourceClockServer.CommitResourceClock(alice,message),"invalid launch anchor refused before mutation");
            check(saved.ToString()==unchanged,"invalid launch anchor preserves entire snapshot");
        }
        ResourceClockPayloads.Set(message,payload);
        check(!ResourceClockServer.CommitResourceClock(alice,message),"clock schema cannot downgrade to remove launch protection");
        string third=second.Replace("   schema = 2","   schema = 3\n   freezeOffset = 100");
        ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(third));
        check(ResourceClockServer.CommitResourceClock(alice,message),"authenticated legacy snapshot migrates to persisted cryopod origin");
        check(saved.VesselModules.GetNode("SurvivalClock").Value.GetNode("APF_SURVIVAL").Value.GetValue("freezeOffset").Value=="100","cryopod origin committed atomically with resources");
        unchanged=saved.ToString();
        foreach(string invalid in new[]{"NaN","Infinity","-1e99","1e99","-1","99"})
        {
            ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(third.Replace("freezeOffset = 100","freezeOffset = "+invalid)));
            check(!ResourceClockServer.CommitResourceClock(alice,message),"invalid or backwards cryopod origin refused before mutation");
            check(saved.ToString()==unchanged,"rejected cryopod origin cannot partially change resources");
        }
        ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(second));
        check(!ResourceClockServer.CommitResourceClock(alice,message),"clock schema cannot downgrade to remove cryopod origin protection");
        body=third;payload=Encoding.UTF8.GetBytes(third);
        foreach(var bad in new[]{body.Replace(alice.PlayerName,bob.PlayerName),body.Replace("index = 0","index = 999"),body.Replace("survivalUT = "+survival,"survivalUT = NaN"),body.Replace("vesselId = "+id,"vesselId = "+Guid.NewGuid())})
        {
            message.Resources[0].Amount=1;ResourceClockPayloads.Set(message,Encoding.UTF8.GetBytes(bad));
            check(!ResourceClockServer.CommitResourceClock(alice,message),"malformed/forged clock refused");
            check(VesselStoreSystem.CurrentVessels[id].ToString()==unchanged,"rejected transaction cannot partially consume resources");
        }
        ResourceClockPayloads.Set(message,payload);
        check(!ResourceClockServer.CommitResourceClock(bob,message),"another bound player cannot write through somebody else's control lock");
        check(VesselStoreSystem.CurrentVessels[id].ToString()==unchanged,"foreign control rejection preserves entire snapshot");
        ResourceClockPayloads.Clear(message);var empty=Write();message.Serialize(empty);received.Deserialize(Read(empty));
        check(ResourceClockPayloads.Get(received).Length==0,"actual pooled resource message clears old envelope on deserialize");
        check(!ResourceClockServer.CommitResourceClock(alice,received),"clocked vessel refuses unpaired legacy resource packet");
        Directory.CreateDirectory(Path.Combine(ServerContext.UniverseDirectory,"Vessels"));VesselStoreSystem.BackupVessels();
        string disk=File.ReadAllText(Path.Combine(ServerContext.UniverseDirectory,"Vessels",id+".txt"));
        check(disk.Contains("amount = 99.25")&&disk.Contains("survivalUT = "+survival),"normal atomic backup includes matching resource/clock checkpoint");
        BackgroundCheckpointAuthority(id,alice,bob,check);
    }
    static void BackgroundCheckpointAuthority(Guid id,ClientStructure alice,ClientStructure bob,Action<bool,string> check)
    {
        var query=LockSystem.LockQuery;
        bool Allowed(string player)=>DeepFreezeCheckpointAuthority.MayCheckpointFreeze(query,id,player);
        check(Allowed(alice.PlayerName)&&!Allowed(bob.PlayerName),"native control lock retains exclusive frozen checkpoint authority");
        LockSystem.ReleaseLock(new LockDefinition(LockType.Control,alice.PlayerName,id));
        check(!Allowed(alice.PlayerName),"saved owner alone cannot write without background lock");
        check(LockSystem.AcquireLock(new LockDefinition(LockType.UnloadedUpdate,alice.PlayerName,id),false,out _),"background test lock acquired");
        check(Allowed(alice.PlayerName),"unloaded lock and matching persisted clock owner permit checkpoint");
        check(!Allowed(bob.PlayerName),"peer cannot write another owner's unloaded cryopod");
        var owner=VesselStoreSystem.CurrentVessels[id].VesselModules.GetNode("SurvivalClock").Value.GetNode("APF_SURVIVAL").Value.GetValue("owner");
        owner.Value=bob.PlayerName;
        check(!Allowed(alice.PlayerName),"automatic background lock reassignment cannot adopt another saved timeline");
        owner.Value=alice.PlayerName;
        check(LockSystem.AcquireLock(new LockDefinition(LockType.Update,bob.PlayerName,id),false,out _),"competing live update lock acquired");
        check(!Allowed(alice.PlayerName),"live update authority excludes stale unloaded writer");
        LockSystem.ReleaseLock(new LockDefinition(LockType.Update,bob.PlayerName,id));
        check(LockSystem.AcquireLock(new LockDefinition(LockType.Control,bob.PlayerName,id),false,out _),"new pilot control lock acquired");
        check(Allowed(bob.PlayerName)&&!Allowed(alice.PlayerName),"new pilot control wins over stale background lock and saved owner");
        LockSystem.ReleaseLock(new LockDefinition(LockType.Control,bob.PlayerName,id));
        LockSystem.ReleaseLock(new LockDefinition(LockType.UnloadedUpdate,alice.PlayerName,id));
        check(!Allowed(alice.PlayerName)&&!Allowed(bob.PlayerName),"unlocked cryopod is not writable by either pilot");
        check(!DeepFreezeCheckpointAuthority.MayCheckpointFreeze(null,id,alice.PlayerName),"missing lock store fails closed");
        check(!DeepFreezeCheckpointAuthority.MayCheckpointFreeze(query,id,string.Empty),"empty actor fails closed");
        LockSystem.AcquireLock(new LockDefinition(LockType.Control,alice.PlayerName,id),false,out _);
    }
}
