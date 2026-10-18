using System;
using System.Linq;
using System.Reflection;
internal static class ShippingDomainRunner {
 public static int Main() {
  var type=typeof(AetherfrontCoreTests.Program);
  int passed=0,failed=0;
  var probeState=new Aetherfront.Core.AetherfrontState {ServerId="test-server"};
  probeState.ModeratorAccountIds.Add("dm");
  var probe=Aetherfront.Core.AetherfrontDomain.UpsertObjective(probeState,"probe","dm","weekly-launch","season-1","Launch week","Complete one launch",Aetherfront.Core.AetherfrontEventTypes.VesselLaunched,1,25,1000,2,true,null,null,7,2,DateTime.UtcNow);
  if(probe.Accepted || probe.ReasonCode!="ObjectiveCatchUpRequiresEnd")throw new InvalidOperationException("Catch-up objective must have an end date");
  Console.WriteLine("PASS catch-up objective without an end date rejected");

  foreach(var method in type.GetMethods(BindingFlags.NonPublic|BindingFlags.Static).Where(m=>m.ReturnType==typeof(void)&&m.GetParameters().Length==0)) {
   try {method.Invoke(null,null); Console.WriteLine("PASS "+method.Name); passed++;}
   catch(Exception error) {Console.WriteLine("FAIL "+method.Name+": "+(error.InnerException??error).Message);failed++;}
  }
  failed+=SecurityCases.Run();
  Console.WriteLine("RESULT historical-scenarios-passed="+passed+" total-failed="+failed);
  return failed==0?0:1;
 }
}
