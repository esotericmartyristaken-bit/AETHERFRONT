using System;
using System.Linq;
using Aetherfront.Core;
internal static class SecurityCases {
 static int count,failed;
 static DateTime now=DateTime.UtcNow;
 static void Require(bool valid,string message) {if(!valid)throw new Exception(message);}
 static void Check(string name,Action test) {count++;try{test();Console.WriteLine("PASS SECURITY "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL SECURITY "+name+": "+e.Message);}}
 static AetherfrontState Fixture(out FactionRecord a,out FactionRecord b) {
  var s=new AetherfrontState {ServerId="security-fixture"};
  var x=AetherfrontDomain.CreateFaction(s,"create-a","alice","Alpha","ALP","","#AAAAAA","","",now,RecruitmentPolicies.Open);
  var y=AetherfrontDomain.CreateFaction(s,"create-b","bob","Beta","BET","","#BBBBBB","","",now,RecruitmentPolicies.Open);
  Require(x.Accepted&&y.Accepted,"fixture faction creation");a=s.FindFaction(x.FactionId);b=s.FindFaction(y.FactionId);return s;
 }
 public static int Run() {
  Check("funds recipient overflow rejects without changing balances",()=>{
   FactionRecord a,b;var s=Fixture(out a,out b);a.Progression.FundsMicro=100;b.Progression.FundsMicro=long.MaxValue;
   var r=AetherfrontDomain.TransferFunds(s,"overflow-funds","alice",a.Id,b.Id,1,now);
   Require(!r.Accepted&&a.Progression.FundsMicro==100&&b.Progression.FundsMicro==long.MaxValue,"result="+r.ReasonCode+" source="+a.Progression.FundsMicro+" target="+b.Progression.FundsMicro);
  });
  Check("science recipient overflow rejects without changing balances",()=>{
   FactionRecord a,b;var s=Fixture(out a,out b);a.Progression.Science=100;b.Progression.Science=long.MaxValue;
   var r=AetherfrontDomain.TransferScience(s,"overflow-science","alice",a.Id,b.Id,1,now);
   Require(!r.Accepted&&a.Progression.Science==100&&b.Progression.Science==long.MaxValue,"result="+r.ReasonCode+" source="+a.Progression.Science+" target="+b.Progression.Science);
  });
  Check("outsider cannot transfer either faction currency",()=>{
   FactionRecord a,b;var s=Fixture(out a,out b);a.Progression.FundsMicro=100;a.Progression.Science=100;
   Require(!AetherfrontDomain.TransferFunds(s,"rogue-funds","mallory",a.Id,b.Id,1,now).Accepted,"outsider funds accepted");
   Require(!AetherfrontDomain.TransferScience(s,"rogue-science","mallory",a.Id,b.Id,1,now).Accepted,"outsider science accepted");
   Require(a.Progression.FundsMicro==100&&a.Progression.Science==100,"outsider mutated balances");
  });
  Check("invalid currency amounts cannot mutate funds",()=>{
   foreach(long amount in new[]{long.MinValue,-1L,0L,long.MaxValue}) {
    FactionRecord a,b;var s=Fixture(out a,out b);a.Progression.FundsMicro=100;long before=b.Progression.FundsMicro;
    var r=AetherfrontDomain.TransferFunds(s,"invalid-funds","alice",a.Id,b.Id,amount,now);
    Require(!r.Accepted&&a.Progression.FundsMicro==100&&b.Progression.FundsMicro==before,"invalid amount accepted/mutated: "+amount);
   }
  });
  Check("replayed successful currency transfer never applies twice",()=>{
   FactionRecord a,b;var s=Fixture(out a,out b);a.Progression.FundsMicro=100;b.Progression.FundsMicro=0;
   var first=AetherfrontDomain.TransferFunds(s,"same-command","alice",a.Id,b.Id,10,now);
   var replay=AetherfrontDomain.TransferFunds(s,"same-command","alice",a.Id,b.Id,10,now);
   Require(first.Accepted&&replay.Accepted&&a.Progression.FundsMicro==90&&b.Progression.FundsMicro==10,"duplicate mutation");
  });
  Check("ordinary faction member cannot grant self founder role",()=>{
   FactionRecord a,b;var s=Fixture(out a,out b);
   Require(AetherfrontDomain.JoinFaction(s,"join-member","mallory",a.Id,"Mallory",now).Accepted,"join fixture");
   Require(!AetherfrontDomain.SetMemberRole(s,"escalate","mallory",a.Id,"mallory",FactionRoles.Founder,now).Accepted,"self escalation accepted");
   Require(a.FounderAccountId=="alice","founder changed");
  });
  Check("unaffordable trade cannot partially settle currencies",()=>{
   FactionRecord a,b;var s=Fixture(out a,out b);a.Progression.FundsMicro=100;b.Progression.Science=100;
   var offer=AetherfrontDomain.CreateTradeOffer(s,"trade","alice",a.Id,b.Id,new[]{new TradeAsset{Type=TradeAssetTypes.FundsMicro,Amount=10}},new[]{new TradeAsset{Type=TradeAssetTypes.Science,Amount=20}},now);
   Require(offer.Accepted,"offer creation");
   Require(AetherfrontDomain.ConfirmTrade(s,"confirm-a","alice",offer.TradeId,now).Accepted,"first confirmation");
   b.Progression.Science=0;
   var r=AetherfrontDomain.ConfirmTrade(s,"confirm-b","bob",offer.TradeId,now);
   Require(!r.Accepted&&a.Progression.FundsMicro==100&&b.Progression.Science==0&&s.FindTrade(offer.TradeId).State!=TradeStates.Committed,"partial trade settlement");
  });
  Check("vessel grant expires and never grants omitted permissions",()=>{
   FactionRecord a,b;var state=Fixture(out a,out b);
   var registered=AetherfrontDomain.RegisterCraft(state,"register","alice",a.Id,"vessel-grant","Test Vessel",new string('a',64),now);
   Require(registered.Accepted,"register fixture");
   var result=AetherfrontDomain.SetVesselGrant(state,"grant","alice","vessel-grant",b.Id,"",new[]{CraftAccess.Launch},now.AddHours(1),false,now);
   Require(result.Accepted,"grant failed: "+result.ReasonCode);
   Require(AetherfrontDomain.CanControlCraft(state,"bob",b.Id,"vessel-grant",CraftAccess.Launch,now),"grant not usable");
   Require(!AetherfrontDomain.CanControlCraft(state,"bob",b.Id,"vessel-grant",CraftAccess.Modify,now),"omitted permission allowed");
   Require(!AetherfrontDomain.CanControlCraft(state,"bob",b.Id,"vessel-grant",CraftAccess.Launch,now.AddHours(2)),"expired grant allowed");
   var revoked=AetherfrontDomain.SetVesselGrant(state,"revoke","alice","vessel-grant",b.Id,"",new[]{CraftAccess.Launch},null,true,now);
   Require(revoked.Accepted&&!AetherfrontDomain.CanControlCraft(state,"bob",b.Id,"vessel-grant",CraftAccess.Launch,now),"revoked grant usable");
  });
  Check("outsider cannot change vessel member policy",()=>{
   FactionRecord a,b;var state=Fixture(out a,out b);
   Require(AetherfrontDomain.RegisterCraft(state,"register","alice",a.Id,"vessel-policy","Test Vessel",new string('a',64),now).Accepted,"register fixture");
   var result=AetherfrontDomain.SetVesselPolicy(state,"rogue-policy","bob","vessel-policy",new[]{CraftAccess.Transfer},now);
   Require(!result.Accepted,"outsider changed vessel policy");
   Require(!AetherfrontDomain.ValidVesselPermissions(new[]{"Administrator"}),"unknown permission accepted");
  });
  Check("vessel gift atomically transfers ownership and control",()=>{
   FactionRecord a,b;var state=Fixture(out a,out b);
   Require(AetherfrontDomain.RegisterCraft(state,"register","alice",a.Id,"vessel-gift","Gift Vessel",new string('b',64),now).Accepted,"register fixture");
   var craft=state.Crafts.Single();
   var trade=AetherfrontDomain.CreateTradeOffer(state,"gift","alice",a.Id,b.Id,new[]{new TradeAsset{Type=TradeAssetTypes.Craft,EntityId=craft.Id,ContentSha256=craft.ContentSha256,CraftRevision=craft.Revision}},new TradeAsset[0],now);
   Require(trade.Accepted,"gift offer rejected: "+trade.ReasonCode);
   Require(AetherfrontDomain.ConfirmTrade(state,"gift-a","alice",trade.TradeId,now).Accepted,"gift proposer confirmation");
   var accepted=AetherfrontDomain.ConfirmTrade(state,"gift-b","bob",trade.TradeId,now);
   Require(accepted.Accepted&&craft.OwnerFactionId==b.Id,"gift settlement failed: "+accepted.ReasonCode);
   Require(AetherfrontDomain.CanControlCraft(state,"bob",b.Id,craft.Id,CraftAccess.Launch,now),"recipient lacks control");
   Require(!AetherfrontDomain.CanControlCraft(state,"alice",a.Id,craft.Id,CraftAccess.Launch,now),"prior owner retains control");
  });
  Check("vessel lease retains ownership and expires on schedule",()=>{
   FactionRecord a,b;var state=Fixture(out a,out b);
   Require(AetherfrontDomain.RegisterCraft(state,"register","alice",a.Id,"vessel-lease","Lease Vessel",new string('c',64),now).Accepted,"register fixture");
   var craft=state.Crafts.Single();
   var asset=new TradeAsset{Type=TradeAssetTypes.VesselLease,EntityId=craft.Id,ContentSha256=craft.ContentSha256,CraftRevision=craft.Revision,LeaseHours=1,Permissions=new System.Collections.Generic.List<string>{CraftAccess.Use,CraftAccess.Launch}};
   var trade=AetherfrontDomain.CreateTradeOffer(state,"lease","alice",a.Id,b.Id,new[]{asset},new TradeAsset[0],now);
   Require(trade.Accepted,"lease offer rejected: "+trade.ReasonCode);
   Require(AetherfrontDomain.ConfirmTrade(state,"lease-a","alice",trade.TradeId,now).Accepted,"lease proposer confirmation");
   var result=AetherfrontDomain.ConfirmTrade(state,"lease-b","bob",trade.TradeId,now);
   Require(result.Accepted&&craft.OwnerFactionId==a.Id,"lease transferred ownership: "+result.ReasonCode);
   Require(craft.AccessGrants.Any(g=>g.IsLease),"lease grant missing");
   Require(AetherfrontDomain.CanControlCraft(state,"bob",b.Id,craft.Id,CraftAccess.Launch,now),"lessee lacks control");
   Require(!AetherfrontDomain.CanControlCraft(state,"bob",b.Id,craft.Id,CraftAccess.Transfer,now),"lessee can transfer ownership");
   Require(!AetherfrontDomain.CanControlCraft(state,"bob",b.Id,craft.Id,CraftAccess.Launch,now.AddHours(2)),"lease remains usable after expiry");
  });
  Check("personal vessel donation preserves creator and changes owner faction",()=>{
   FactionRecord a,b;var state=Fixture(out a,out b);
   var craft=new CraftRecord{Id="personal-vessel",Name="Personal Vessel",OwnerAccountId="bob",CreatorAccountId="bob",ContentSha256=new string('d',64)};state.Crafts.Add(craft);
   Require(!AetherfrontDomain.DonatePersonalVessel(state,"rogue-donation","alice",craft.Id,now).Accepted,"outsider donated vessel");
   var result=AetherfrontDomain.DonatePersonalVessel(state,"donation","bob",craft.Id,now);
   Require(result.Accepted&&craft.OwnerFactionId==b.Id&&craft.CreatorAccountId=="bob","donation failed: "+result.ReasonCode);
  });
  Check("faction chat cannot target a different faction",()=>{
   FactionRecord a,b;var state=Fixture(out a,out b);
   var rejected=AetherfrontDomain.PostFactionChat(state,"wrong-chat","alice","private message",now,b.Id);
   Require(!rejected.Accepted,"cross-faction chat accepted");
   var accepted=AetherfrontDomain.PostFactionChat(state,"valid-chat","alice","hello",now,a.Id);
   Require(accepted.Accepted,"own faction chat rejected: "+accepted.ReasonCode);
  });
  Console.WriteLine("SECURITY RESULT cases="+count+" failed="+failed);
  return failed;
 }
}
