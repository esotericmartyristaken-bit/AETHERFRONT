using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Aetherfront.Core;
using Aetherfront.Synchronization;
using Aetherfront.Stability.Client;
using HarmonyLib;
using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.Warp;

class Program
{
 static string game, fixture; static int checks;
 static Assembly Resolve(object sender, ResolveEventArgs args)
 {
  string name = new AssemblyName(args.Name).Name + ".dll";
  foreach (string directory in new[] { fixture, Path.Combine(game, "KSP_x64_Data/Managed") })
  { string path = Path.Combine(directory, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
  return null;
 }
 static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS Mono " + name); }
 static void Main()
 {
  game = Environment.GetEnvironmentVariable("RC56_KSP_REFERENCE"); fixture = Environment.GetEnvironmentVariable("RC56_MONO_ROOT");
  AppDomain.CurrentDomain.AssemblyResolve += Resolve; Run();
 }
 [MethodImpl(MethodImplOptions.NoInlining)] static void Run()
 {
  Assembly.LoadFrom(Path.Combine(fixture, "AetherfrontContinuityClient.dll"));
  int methods = 0; var helper = typeof(FlightSafety).Assembly;
  foreach (var type in helper.GetTypes()) foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
   if (!method.ContainsGenericParameters && method.GetMethodBody() != null) { RuntimeHelpers.PrepareMethod(method.MethodHandle); methods++; }
  Check(methods > 100, "shipping helper methods JIT against KSP's actual Mono");
  var listener = typeof(FlightSafety).GetField("Listener", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
  Check(listener != null && listener.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Count(m => m.DeclaringType == listener.GetType()) == 4,
   "KSP lifecycle callbacks retain non-null instance targets");
  FlightSafety.InstallHooks(new Harmony("aetherfront.rc56.isolated-mono-validation"));
  Check(true, "complete Harmony hook installation against shipping KSP and patched LMP");
  var factory = new ClientMessageFactory();
  var source = factory.CreateNewMessageData<WarpNewSubspaceMsgData>(); source.PlayerCreator = "MonoFixture"; source.SubspaceKey = 12; source.ServerTimeDifference = 500;
  BoundaryWire.Set(source, 12345, 98765);
  var write = (NetOutgoingMessage)Activator.CreateInstance(typeof(NetOutgoingMessage), true);
  var data = typeof(NetBuffer).GetField("m_data", BindingFlags.Instance | BindingFlags.NonPublic); data.SetValue(write, new byte[256]);
  typeof(WarpNewSubspaceMsgData).GetMethod("InternalSerialize", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, new object[] { write });
  var read = (NetIncomingMessage)Activator.CreateInstance(typeof(NetIncomingMessage), true); data.SetValue(read, (byte[])write.Data.Clone()); read.LengthBits = write.LengthBits;
  var target = factory.CreateNewMessageData<WarpNewSubspaceMsgData>();
  typeof(WarpNewSubspaceMsgData).GetMethod("InternalDeserialize", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { read });
  Check(BoundaryWire.Session(target) == 12345 && BoundaryWire.Request(target) == 98765 && target.SubspaceKey == 12, "identified warp wire framing round trip");
  var before = new AetherfrontState { ServerId = "mono-fixture", Revision = 1 };
  before.Crafts.Add(new CraftRecord { Id = "craft", Name = "Before", Revision = 1 });
  var after = AetherfrontJson.Deserialize<AetherfrontState>(AetherfrontJson.Serialize(before)); after.Revision = 2; after.Crafts[0].Name = "After"; after.Crafts[0].Revision++;
  var delta = StateDelta.Between(before, after); var decoded = AetherfrontJson.Deserialize<StateDelta>(AetherfrontJson.Serialize(delta));
  Check(AetherfrontJson.Serialize(decoded.Apply(before)) == AetherfrontJson.Serialize(after), "client JsonFx delta decoding reconstructs exact state");
  var policy = new WarpRecoveryPolicy(); policy.Begin(1, 2, 3, 100, 0);
  Check(policy.Expired(30) && policy.CanAccept(1, 2, 3, 1, 100, 1), "timely ACK survives a delayed Unity frame");
  policy.Cancel(); Check(!policy.Expired(300), "cancelled scene/termination boundary cannot expire later");
  File.WriteAllText(Path.Combine(fixture, "mono-results.txt"), DateTime.UtcNow.ToString("O") + " checks=" + checks + "; preparedMethods=" + methods + "; no Unity flight scene was launched\n");
  Console.WriteLine("PASS " + checks + " actual KSP Mono checks; " + methods + " methods prepared. This is not a rendered flight test.");
 }
}
