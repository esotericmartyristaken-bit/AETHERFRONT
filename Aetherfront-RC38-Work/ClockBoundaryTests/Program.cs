using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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

static class Program
{
    static int checks;
    static void Check(bool value,string text) { checks++; if(!value) throw new Exception(text); }
    static ClientStructure Client(string name)
    {
#pragma warning disable SYSLIB0050
        var client=(ClientStructure)FormatterServices.GetUninitializedObject(typeof(ClientStructure));
#pragma warning restore SYSLIB0050
        client.Subspace=int.MinValue; client.ConnectionStatus=ConnectionStatus.Connected;
        typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(client,new ConcurrentQueue<IServerMessageBase>());
        using var key=RSA.Create(2048);
        var request=new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
        request.IdentityPublicKey=IdentityProof.ExportPublicKey(key);
        request.UniqueIdentifier=IdentityProof.AccountId(request.IdentityPublicKey);
        request.PlayerName=name;request.KspVersion="1.12.5";request.ApfEnabled=true;request.ApfClientVersion="kesas-rc37";
        client.UniqueIdentifier=request.UniqueIdentifier;client.PlayerName=name;
        Check(Server.System.IdentityAuthentication.Begin(client,request,out var nonce,out var reason),"identity begin "+reason);
        Check(Server.System.IdentityAuthentication.Complete(client,IdentityProof.Sign(key,nonce,request.UniqueIdentifier,name),out _,out reason),"identity proof "+reason);
        client.Authenticated=true;
        Check(AetherfrontServerSystem.BindSession(client),"authenticated account binding");
        return client;
    }

    static void Main(string[] args)
    {
        string runtime=Path.GetFullPath(args[1]);
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(context,name)=> {
            string file=Path.Combine(runtime,name.Name+".dll");return File.Exists(file)?context.LoadFromAssemblyPath(file):null;
        };
        string root=Path.GetFullPath(args[0]);
        if(Directory.Exists(root))throw new IOException("Preserve prior evidence");
        Directory.CreateDirectory(root);
        ServerContext.ConfigDirectory=Path.Combine(root,"Config");Directory.CreateDirectory(ServerContext.ConfigDirectory);
        ServerContext.UniverseDirectory=Path.Combine(root,"Universe");Directory.CreateDirectory(ServerContext.UniverseDirectory);
        GeneralSettings.SettingsStore.GameMode=GameMode.Sandbox;GeneralSettings.SettingsStore.ModControl=false;
        typeof(AetherfrontServerSystem).GetMethod("Initialize",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(string),typeof(string)},null).Invoke(null,new object[]{root,"clock-boundary-fixture"});
        TimeContext.StartTime=DateTime.UtcNow;
        WarpContext.Subspaces.Clear();WarpContext.Subspaces.TryAdd(0,new Subspace(0,1000));WarpContext.NextSubspaceId=1;
        bool reproduce=args.Length>2&&args[2]=="reproduce";
        var receiver=new Server.System.WarpSystemReceiver();
        foreach(double lag in reproduce?new[]{0.05}:new[]{0.0005,0.05,0.25,1.0,3.0,3.5})
        {
            var client=Client("Boundary"+checks);
            Server.System.WarpSystemSender.SendAllSubspaces(client);
            int old=client.Subspace;
            double oldOffset=WarpContext.Subspaces[old].Time;
            PersonalClockServer.Checkpoint(client);
            var store=new ClockStore(ServerContext.UniverseDirectory);
            double before=store.Resume(client.AetherfrontAccountId,0);
            // Sender's UT remains monotonic, but its 1x clock can trail the
            // server by the existing timesync skew window when warp settles.
            int next=WarpContext.NextSubspaceId;
            var request=new ClientMessageFactory().CreateNewMessageData<WarpNewSubspaceMsgData>();
            request.PlayerCreator=client.PlayerName;request.ServerTimeDifference=oldOffset-lag;
            receiver.HandleNewSubspace(client,request);
            Check(WarpContext.Subspaces.ContainsKey(next),"new personal subspace created");
            var change=new ClientMessageFactory().CreateNewMessageData<WarpChangeSubspaceMsgData>();
            change.PlayerName=client.PlayerName;change.Subspace=next;
            bool disconnectPath=false;
            try { receiver.HandleChangeSubspace(client,change); }
            catch(NullReferenceException error) when(reproduce && error.StackTrace.Contains("ClientConnectionHandler.DisconnectClient"))
            {
                // This fixture has no live transport endpoint. The production
                // disconnect path reaches endpoint cleanup after rejecting time.
                disconnectPath=true;
            }
            if(reproduce)
            {
                var sessions=(System.Collections.IDictionary)typeof(PersonalClockServer).GetField("Sessions",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
                Check(disconnectPath && !sessions.Contains(client),"baseline checkpoint rejected and entered disconnect at 50 ms skew");
                Console.WriteLine("REPRODUCED: actual RC37 server disconnects at a 50 ms backwards offset transition.");
                return;
            }
            Check(!client.DisconnectClient,"normal boundary skew must not disconnect "+lag);
            Check(client.Subspace==next,"client changes to canonical subspace");
            Check(WarpContext.Subspaces[next].Time>=oldOffset,"canonical offset never decreases");
            Check(request.ServerTimeDifference==WarpContext.Subspaces[next].Time,"broadcast uses canonical request offset");
            Check(store.Resume(client.AetherfrontAccountId,0)>=before,"durable clock never rolls back");
            PersonalClockServer.BeforeDisconnect(client);
        }
        Security(receiver);
        Console.WriteLine("PASS "+checks+" authenticated boundary assertions; no Unity/UDP claim.");
    }

    static bool DisconnectAttempt(Action action)
    {
        try { action(); return false; }
        catch(NullReferenceException error) when(error.StackTrace.Contains("ClientConnectionHandler.DisconnectClient")) { return true; }
    }

    static void Security(Server.System.WarpSystemReceiver receiver)
    {
        var client=Client("ClockSecurity");
        Server.System.WarpSystemSender.SendAllSubspaces(client);
        int old=client.Subspace;
        double offset=WarpContext.Subspaces[old].Time;
        int next=WarpContext.NextSubspaceId;
        var request=new ClientMessageFactory().CreateNewMessageData<WarpNewSubspaceMsgData>();
        request.PlayerCreator="AnotherPlayer";request.ServerTimeDifference=offset+10;
        receiver.HandleNewSubspace(client,request);
        Check(WarpContext.NextSubspaceId==next,"forged creator cannot allocate subspace");
        request.PlayerCreator=client.PlayerName;
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            request.ServerTimeDifference=bad;receiver.HandleNewSubspace(client,request);
            Check(WarpContext.NextSubspaceId==next,"non-finite clock ignored without allocation");
        }
        // Forward warp is not reduced or charged to a peer's clock.
        request.ServerTimeDifference=offset+100000;
        receiver.HandleNewSubspace(client,request);
        Check(WarpContext.Subspaces[next].Time==offset+100000,"forward warp remains exact");
        Check(WarpContext.Subspaces[old].Time==offset,"original/shared timeline untouched");
        var store=new ClockStore(ServerContext.UniverseDirectory);
        PersonalClockServer.Checkpoint(client);
        double before=store.Resume(client.AetherfrontAccountId,0);
        bool strict=false;
        try { store.Checkpoint(client.AetherfrontAccountId,before-0.01); } catch(InvalidDataException) { strict=true; }
        Check(strict,"original 1 ms durable rollback guard unchanged");
        next=WarpContext.NextSubspaceId;
        request.ServerTimeDifference=offset-3.5001;
        Check(DisconnectAttempt(()=>receiver.HandleNewSubspace(client,request)),"larger backward proposal takes native disconnect path");
        Check(WarpContext.NextSubspaceId==next&&!WarpContext.Subspaces.ContainsKey(next),"rejected rollback never allocated/broadcast");
        Check(store.Resume(client.AetherfrontAccountId,0)>=before,"rejected proposal never reduces saved clock");

        client=Client("ClockPastSync");
        Server.System.WarpSystemSender.SendAllSubspaces(client);
        offset=WarpContext.Subspaces[client.Subspace].Time;
        int past=WarpContext.NextSubspaceId++;
        WarpContext.Subspaces.TryAdd(past,new Subspace(past,offset-10,"AnotherPlayer"));
        before=store.Resume(client.AetherfrontAccountId,0);
        var change=new ClientMessageFactory().CreateNewMessageData<WarpChangeSubspaceMsgData>();
        change.PlayerName=client.PlayerName;change.Subspace=past;
        Check(DisconnectAttempt(()=>receiver.HandleChangeSubspace(client,change)),"arbitrary existing past-subspace change still refused");
        Check(store.Resume(client.AetherfrontAccountId,0)>=before,"existing-timeline rollback cannot reduce saved clock");
    }
}
