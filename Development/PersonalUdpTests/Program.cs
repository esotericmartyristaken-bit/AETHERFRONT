using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Aetherfront.Core;
using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Data.Apf;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;
using Newtonsoft.Json.Linq;

static class Program
{
    static int checks;
    static string clientVersion;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    static void Main(string[] args)
    {
        if(args.Length!=1)throw new Exception("Explicit isolated server root required");
        var root=Path.GetFullPath(args[0]);
        if(!root.EndsWith(Path.Combine("staging","PersonalServer"),StringComparison.OrdinalIgnoreCase) ||
           !File.ReadAllText(Path.Combine(root,"Config","ConnectionSettings.xml")).Contains("<ListenAddress>127.0.0.1</ListenAddress>"))throw new Exception("Local fixture required");
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(context,name)=>
        {
            string path=Path.Combine(root,name.Name+".dll");
            return File.Exists(path)?context.LoadFromAssemblyPath(path):null;
        };
        clientVersion=(string)JObject.Parse(File.ReadAllText(Path.Combine(root,"Config","APFPack.json")))["requiredClientVersion"];
        var subject=((JArray)JObject.Parse(File.ReadAllText(Path.Combine(root,"AetherfrontScienceSubjects.json")))["Subjects"]).First(s=>(decimal)s["Cap"]>=300);
        string source=(string)subject["Id"];
        using var alphaKey=RSA.Create(2048);using var betaKey=RSA.Create(2048);using var lateKey=RSA.Create(2048);
        using var alpha=new Peer("PTA"+Guid.NewGuid().ToString("N").Substring(0,6),alphaKey);
        using var beta=new Peer("PTB"+Guid.NewGuid().ToString("N").Substring(0,6),betaKey);
        decimal baseline=alpha.Player().SharedScienceCredited;
        Check(alpha.Claim(source,100).Accepted,"authenticated cumulative science claim over UDP");
        Check(alpha.Player().EarnedScience==100 && alpha.Player().TradableScience==100,"earner keeps full tradable award");
        Check(beta.Player().Science==baseline+50 && beta.Player().TradableScience==0,"other player receives research-only 50 percent");
        Check(!beta.Transfer(alpha.Account,1,1).Accepted,"free baseline transfer rejected over UDP");
        Check(alpha.Claim(source,100).Accepted && alpha.Player().EarnedScience==100,"fresh-command replay cannot double-credit");
        using var late=new Peer("PTL"+Guid.NewGuid().ToString("N").Substring(0,6),lateKey);
        Check(late.Player().Science==baseline+50,"new player receives historical baseline over UDP");
        string betaName=beta.Name;
        beta.Dispose();Thread.Sleep(500);
        Check(alpha.Claim(source,300).Accepted,"new science claim after offline disconnect");
        using var returned=new Peer(betaName,betaKey);
        Check(returned.Player().Science==baseline+150 && returned.Player().TradableScience==0,"offline shares survive authenticated reconnect");
        Check(alpha.Transfer(returned.Account,10,1).Accepted,"earned science transfer over UDP");
        Check(returned.Player().Science==baseline+160 && returned.Player().TradableScience==10,"incoming trade preserves earned provenance");
        Check(late.Player().Science==baseline+150,"transfer does not produce additional shared science");
        Check(returned.Unlock("basicRocketry",1).Accepted,"personal technology without faction over UDP");
        Check(returned.Player().TradableScience==10,"technology spends research-only credit first");
        Check(!alpha.Player().TechnologyUnlocks.Contains("basicRocketry"),"technology remains personal");
        string labReceipt="award:"+Guid.NewGuid().ToString("N");
        Check(alpha.Lab(labReceipt,500,0x400).Accepted,"real UDP lab receipt accepted");
        Check(alpha.Player().EarnedScience==800 && alpha.Player().TradableScience==790,"lab earner keeps full amount without transfer recursion");
        Check(returned.Player().SharedScienceCredited==baseline+400,"lab universal half-share over UDP");
        Check(alpha.Lab(labReceipt,500,0x400).Accepted && alpha.Player().EarnedScience==800,"lab UDP replay remains idempotent");
        Check(!alpha.Lab("award:"+Guid.NewGuid().ToString("N"),500,0x10000).Accepted,"UDP cheat reason rejected");
        Console.WriteLine("PASS: "+checks+" real UDP assertions; protocol-synthetic clients, not physical experiment collection.");
    }

    sealed class Peer:IDisposable
    {
        readonly NetClient client=new NetClient(new NetPeerConfiguration("LMP"));
        readonly ClientMessageFactory outgoing=new ClientMessageFactory();
        readonly ServerMessageFactory incoming=new ServerMessageFactory();
        bool disposed;
        public string Account{get;}
        public string Name{get;}
        public Peer(string name,RSA key)
        {
            Name=name;string publicKey=IdentityProof.ExportPublicKey(key);Account=IdentityProof.AccountId(publicKey);
            client.Start();var hail=client.CreateMessage(16);hail.Write("");client.Connect(new IPEndPoint(IPAddress.Loopback,28865),hail);
            var watch=Stopwatch.StartNew();
            while(client.ConnectionStatus!=NetConnectionStatus.Connected && watch.ElapsedMilliseconds<10000){DrainStatus();Thread.Sleep(10);}
            Check(client.ConnectionStatus==NetConnectionStatus.Connected,"UDP connection");
            var handshake=outgoing.CreateNewMessageData<HandshakeRequestMsgData>();
            handshake.PlayerName=Name;handshake.IdentityPublicKey=publicKey;handshake.UniqueIdentifier=Account;handshake.KspVersion="1.12.5";
            handshake.ApfEnabled=true;handshake.ApfClientVersion=clientVersion;handshake.ApfPackId="kesas";handshake.ApfPackVersion=clientVersion;
            Send(outgoing.CreateNew<HandshakeCliMsg>(handshake));
            var challenge=Receive<HandshakeIdentityChallengeMsgData>();
            var proof=outgoing.CreateNewMessageData<HandshakeIdentityProofMsgData>();proof.Signature=IdentityProof.Sign(key,challenge.Nonce,Account,Name);
            Send(outgoing.CreateNew<HandshakeCliMsg>(proof));
            var reply=Receive<HandshakeReplyMsgData>();
            if(reply.Response==HandshakeReply.ApfPackRequired)
            {
                var status=outgoing.CreateNewMessageData<PackStatusReportMsgData>();status.PackId=reply.ApfPackId;status.PackVersion=reply.ApfPackVersion;status.Status=1;status.Detail="isolated protocol code test";
                Send(outgoing.CreateNew<ApfPackCliMsg>(status));reply=Receive<HandshakeReplyMsgData>();
            }
            Check(reply.Response==HandshakeReply.HandshookSuccessfully,"RSA identity and APF admission: "+reply.Reason);
        }
        void DrainStatus(){NetIncomingMessage message;while((message=client.ReadMessage())!=null)client.Recycle(message);}
        void Send(IMessageBase message)
        {var buffer=client.CreateMessage(message.GetMessageSize());message.Serialize(buffer);client.SendMessage(buffer,message.NetDeliveryMethod,message.Channel);client.FlushSendQueue();message.Recycle();}
        T Receive<T>()where T:class
        {
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<15000)
            {
                var packet=client.ReadMessage();if(packet==null){Thread.Sleep(10);continue;}
                try
                {
                    if(packet.MessageType==NetIncomingMessageType.StatusChanged)
                    {var status=(NetConnectionStatus)packet.ReadByte();if(status==NetConnectionStatus.Disconnected)throw new Exception("Local peer disconnected: "+packet.ReadString());continue;}
                    if(packet.MessageType!=NetIncomingMessageType.Data)continue;
                    var message=incoming.Deserialize(packet,DateTime.UtcNow.Ticks) as IServerMessageBase;
                    if(message?.Data is T data)return data;
                    message?.Recycle();
                }
                finally{client.Recycle(packet);}
            }
            throw new TimeoutException("Awaiting "+typeof(T).Name);
        }
        AetherfrontWireReply Execute(AetherfrontWireRequest request)
        {
            request.ProtocolVersion=1;request.CommandId=Guid.NewGuid().ToString("N");
            var data=outgoing.CreateNewMessageData<AetherfrontCommandMsgData>();data.PayloadJson=AetherfrontProtocol.Serialize(request);Send(outgoing.CreateNew<AetherfrontCliMsg>(data));
            var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<15000)
            {
                var received=Receive<AetherfrontBaseMsgData>();
                string text=(received as AetherfrontCommandReplyMsgData)?.PayloadJson ?? (received as AetherfrontSnapshotMsgData)?.PayloadJson;
                if(string.IsNullOrEmpty(text))continue;
                var reply=AetherfrontProtocol.DeserializeReply(text);
                if(reply.Operation==AetherfrontWireOperation.SnapshotReply || reply.Operation==AetherfrontWireOperation.CommandReply)return reply;
            }
            throw new TimeoutException("Aetherfront reply");
        }
        public PlayerProgression Player()
        {var reply=Execute(new AetherfrontWireRequest{Operation=AetherfrontWireOperation.SnapshotRequest});Check(reply.Accepted,"personal snapshot");return PersonalProgression.Find(AetherfrontJson.Deserialize<AetherfrontState>(reply.StateJson),Account);}
        public AetherfrontWireReply Claim(string source,decimal value)=>Execute(new AetherfrontWireRequest{Operation=AetherfrontWireOperation.CommandRequest,CommandType=(AetherfrontCommandType)40,EntityId=source,Body=value.ToString(System.Globalization.CultureInfo.InvariantCulture)});
        public AetherfrontWireReply Lab(string id,decimal value,long reason)=>Execute(new AetherfrontWireRequest{Operation=AetherfrontWireOperation.CommandRequest,CommandType=(AetherfrontCommandType)42,EntityId=id,Body=value.ToString(System.Globalization.CultureInfo.InvariantCulture),Amount2=reason});
        public AetherfrontWireReply Transfer(string target,long amount,long sequence)=>Execute(new AetherfrontWireRequest{Operation=AetherfrontWireOperation.CommandRequest,CommandType=(AetherfrontCommandType)41,TargetAccountId=target,Amount=amount,Amount2=sequence});
        public AetherfrontWireReply Unlock(string technology,long sequence)=>Execute(new AetherfrontWireRequest{Operation=AetherfrontWireOperation.CommandRequest,CommandType=AetherfrontCommandType.UnlockTechnology,TechnologyId=technology,Amount2=sequence});
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            client.Disconnect("local progression test complete");client.FlushSendQueue();
            var watch=Stopwatch.StartNew();
            while(client.ConnectionStatus!=NetConnectionStatus.Disconnected && watch.ElapsedMilliseconds<1000){DrainStatus();Thread.Sleep(10);}
            client.Shutdown("local progression test complete");
        }
    }
}
