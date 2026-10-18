using System;
using System.Net;
using System.Diagnostics;
using System.Threading;
using Lidgren.Network;
internal static class FuzzIngress {
 public static void Run() {
  Console.WriteLine("Bounded malformed-message ingress: local staging UDP28865 only, no authentication or production traffic");
  byte[][] samples={new byte[0],new byte[]{0},new byte[]{255},new byte[]{255,255,255,255},new byte[8],new byte[32],new byte[128],new byte[1024]};
  int connected=0;
  foreach(var data in samples){
   var peer=new NetClient(new NetPeerConfiguration("LMP"));peer.Start();
   try{
    var hail=peer.CreateMessage();hail.Write("");peer.Connect(new IPEndPoint(IPAddress.Loopback,28865),hail);
    var timer=Stopwatch.StartNew();bool ready=false;
    while(timer.ElapsedMilliseconds<3000&&!ready){var incoming=peer.ReadMessage();if(incoming==null){Thread.Sleep(10);continue;}try{if(incoming.MessageType==NetIncomingMessageType.StatusChanged)ready=(NetConnectionStatus)incoming.ReadByte()==NetConnectionStatus.Connected;}finally{peer.Recycle(incoming);}}
    if(!ready)throw new Exception("Local server did not accept transport for malformed fixture");
    connected++;
    var message=peer.CreateMessage(Math.Max(1,data.Length));message.Write(data);peer.SendMessage(message,NetDeliveryMethod.ReliableOrdered,0);peer.FlushSendQueue();
    Thread.Sleep(200);Console.WriteLine("SENT bounded malformed payload bytes="+data.Length);
   }finally{peer.Disconnect("owned malformed ingress fixture complete");peer.Shutdown("owned fixture complete");}
   Thread.Sleep(150);
  }
  Console.WriteLine("Malformed ingress fixtures sent="+connected+"; successful authenticated scenario must follow before this is counted passed");
 }
}
