using System;
internal static class Program {
 static int Main(string[] args) {
  Console.WriteLine("Scope: disposable local staging, IPAddress.Loopback UDP28865 only; no production endpoint or master probe");
  try {
   if(args.Length==3&&args[0]=="--seed-local-review")ApfProtocolTests.DeepProtocolScenario.SeedLocalReview(args[1],args[2]);
   else if(args.Length==1&&args[0]=="--malformed-ingress"){FuzzIngress.Run();ApfProtocolTests.DeepProtocolScenario.Run(0,28865,"");}
   else if(args.Length==0)ApfProtocolTests.DeepProtocolScenario.Run(0,28865,"");
   else throw new ArgumentException("Use no arguments, --malformed-ingress, or --seed-local-review <private-key-path> <player-name>");
   return 0;
  }catch(Exception error){Console.WriteLine(error);return 1;}
 }
}
