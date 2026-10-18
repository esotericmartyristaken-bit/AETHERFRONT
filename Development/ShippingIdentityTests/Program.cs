using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LmpCommon;
using LmpCommon.Message;
using LmpCommon.Message.Data.Handshake;
internal static class Program {
 static int count,failed;
 static DateTime now=DateTime.UtcNow;
 static Type sessionType=typeof(IdentityProof).Assembly.GetType("Server.System.IdentityChallengeSession",true);
 static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Check(string name,Action test){count++;try{test();Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+(e.InnerException??e).Message);}}
 static HandshakeRequestMsgData Request(RSA key) {
  var r=new ClientMessageFactory().CreateNewMessageData<HandshakeRequestMsgData>();
  r.IdentityPublicKey=IdentityProof.ExportPublicKey(key);r.UniqueIdentifier=IdentityProof.AccountId(r.IdentityPublicKey);r.PlayerName="SecurityProbe";r.KspVersion="1.12.5";r.ApfEnabled=true;r.ApfClientVersion="kesas-rc29";return r;
 }
 static object Session(HandshakeRequestMsgData request){return Activator.CreateInstance(sessionType,new object[]{request,now});}
 static string Nonce(object s){return (string)sessionType.GetProperty("Nonce").GetValue(s);}
 static bool Verify(object s,string signature,DateTime at){return (bool)sessionType.GetMethod("VerifyOnce").Invoke(s,new object[]{signature,at});}
 static string Sign(RSA key,object s,HandshakeRequestMsgData r){return IdentityProof.Sign(key,Nonce(s),r.UniqueIdentifier,r.PlayerName);}
 static int Main() {
  using var key=RSA.Create(2048);using var wrongKey=RSA.Create(2048);
  Check("valid identity challenge succeeds only once",()=>{var r=Request(key);var s=Session(r);var sig=Sign(key,s,r);Require(Verify(s,sig,now),"valid rejected");Require(!Verify(s,sig,now),"replay accepted");});
  Check("wrong private key cannot prove account ownership",()=>{var r=Request(key);var s=Session(r);Require(!Verify(s,Sign(wrongKey,s,r),now),"wrong key accepted");Require(!Verify(s,Sign(key,s,r),now),"failed challenge reused");});
  Check("challenge proof is bound to player name",()=>{var r=Request(key);var s=Session(r);Require(!Verify(s,IdentityProof.Sign(key,Nonce(s),r.UniqueIdentifier,"OtherName"),now),"wrong name accepted");});
  Check("challenge proof is bound to account id",()=>{var r=Request(key);var s=Session(r);Require(!Verify(s,IdentityProof.Sign(key,Nonce(s),"another-account",r.PlayerName),now),"wrong account accepted");});
  Check("challenge from another session cannot be replayed",()=>{var r=Request(key);var a=Session(r);var b=Session(r);Require(Nonce(a)!=Nonce(b),"duplicate nonce");Require(!Verify(b,Sign(key,a,r),now),"cross-session replay accepted");});
  Check("expired challenge rejects without verification",()=>{var r=Request(key);var s=Session(r);Require(!Verify(s,Sign(key,s,r),now.AddSeconds(61)),"expired accepted");});
  Check("challenge rejects backwards clock",()=>{var r=Request(key);var s=Session(r);Require(!Verify(s,Sign(key,s,r),now.AddTicks(-1)),"backdated accepted");});
  Check("caller cannot mutate original request after challenge creation",()=>{var r=Request(key);var s=Session(r);string sig=Sign(key,s,r);r.PlayerName="Changed";r.UniqueIdentifier="Changed";r.IdentityPublicKey=IdentityProof.ExportPublicKey(wrongKey);Require(Verify(s,sig,now),"request was not snapshotted");});
  Check("parallel proofs consume the challenge exactly once",()=>{var r=Request(key);var s=Session(r);string sig=Sign(key,s,r);int accepted=0;Parallel.For(0,32,_=>{if(Verify(s,sig,now))System.Threading.Interlocked.Increment(ref accepted);});Require(accepted==1,"accepted="+accepted);});
  Check("malformed signatures fail safely",()=>{foreach(string sig in new[]{"", "bad",new string('A',100000)}){var r=Request(key);var s=Session(r);Require(!Verify(s,sig,now),"malformed accepted");}});
  Check("public key round trip retains account identity",()=>{string text=IdentityProof.ExportPublicKey(key);using(var copy=IdentityProof.ImportPublicKey(text)){Require(IdentityProof.AccountId(IdentityProof.ExportPublicKey(copy))==IdentityProof.AccountId(text),"identity changed");}});
  Console.WriteLine("RESULT cases="+count+" failed="+failed);return failed==0?0:1;
 }
}
