using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Globalization;
using System.Linq;
using Aetherfront.Core;

namespace UnityEngine {
 public class MonoBehaviour { protected static void DontDestroyOnLoad(object value) {} protected static void Destroy(object value){} }
 public sealed class GameObject {public static int Created;public GameObject(string name){Created++;}public T AddComponent<T>() where T:new(){var value=new T();typeof(T).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,null);return value;}}
 public static class Time { public static int frameCount; public static float realtimeSinceStartup; }
 public static class Debug { public static readonly List<string> Messages=new List<string>(); public static void Log(object v){Messages.Add("LOG "+v);}public static void LogError(object v){Messages.Add("ERROR "+v);}public static void LogWarning(object v){Messages.Add("WARNING "+v);} }
}
public sealed class KSPAddon:Attribute {public enum Startup{Instantly}public KSPAddon(Startup value,bool once){} }
public sealed class Game {public enum Modes{SANDBOX,SCIENCE_SANDBOX,CAREER}public Modes Mode=Modes.SCIENCE_SANDBOX;public Parameters Parameters=new Parameters();}
public sealed class Parameters {public Career Career=new Career();}
public sealed class Career {public float ScienceGainMultiplier=1;}
public static class HighLogic {public static Game CurrentGame=new Game();}
public static class KSPUtil {public static string ApplicationRootPath;}
public enum TransactionReasons {VesselRecovery=32,ScienceTransmission=1024,Cheating=65536}
public sealed class ScienceSubject {
 public string id;public float science;public float scienceCap=100;public float scientificValue=1;public bool applyScienceScale=true;public float dataScale=1;
 public ScienceSubject(){}
 public ScienceSubject(ScienceExperiment experiment,ExperimentSituations situation,CelestialBody body,string biome,string displayBiome){id=experiment.id+"@"+body.name+situation+biome.Replace(" ","");scienceCap=experiment.scienceCap;dataScale=experiment.dataScale;}
 public void Save(ConfigNode node){node.AddValue("id",id);node.AddValue("sci",science.ToString(CultureInfo.InvariantCulture));node.AddValue("cap",scienceCap.ToString(CultureInfo.InvariantCulture));}
}
public enum ExperimentSituations{SrfLanded=1,SrfSplashed=2,FlyingLow=4,FlyingHigh=8,InSpaceLow=16,InSpaceHigh=32}
public sealed class CelestialBody{public string name;}
public sealed class ScienceExperiment{public string id;public float scienceCap=100;public float dataScale=1;}
public static class FlightGlobals{public static List<CelestialBody> Bodies=new List<CelestialBody>{new CelestialBody{name="Mun"},new CelestialBody{name="Kerbin"}};}
public sealed class ConfigNode {
 public string name;private readonly Dictionary<string,string> values=new Dictionary<string,string>();private readonly List<ConfigNode> nodes=new List<ConfigNode>();
 public ConfigNode(string value=""){name=value;}public void AddValue(string key,string value){values[key]=value;}public string GetValue(string key){string value;return values.TryGetValue(key,out value)?value:null;}
 public ConfigNode AddNode(string key){var n=new ConfigNode(key);nodes.Add(n);return n;}public ConfigNode[] GetNodes(string key){return nodes.FindAll(n=>n.name==key).ToArray();}
 public override string ToString(){return name+" { "+string.Join(";",values.Select(p=>p.Key+"="+p.Value).ToArray())+" }";}
}
public sealed class ProtoPartModuleSnapshot{public ConfigNode moduleValues=new ConfigNode();}
public sealed class ProtoPartSnapshot{public List<ProtoPartModuleSnapshot> modules=new List<ProtoPartModuleSnapshot>();}
public sealed class ProtoVessel {public Guid vesselID=Guid.NewGuid();public List<ProtoPartSnapshot> parts=new List<ProtoPartSnapshot>();public List<ProtoPartSnapshot> GetAllProtoPartsIncludingCargo(){return parts;}}
public sealed class MissionRecoveryDialog{}
public sealed class ScienceEvent {
 private Action<float,ScienceSubject,ProtoVessel,bool> callbacks;
 public void Add(Action<float,ScienceSubject,ProtoVessel,bool> a){callbacks+=a;}
 public void Remove(Action<float,ScienceSubject,ProtoVessel,bool> a){callbacks-=a;}
 public void Clear(){callbacks=null;}
 public void Fire(float a,ScienceSubject b,ProtoVessel c,bool d){if(callbacks!=null)callbacks(a,b,c,d);}
}
public static class GameEvents {public static readonly ScienceEvent OnScienceRecieved=new ScienceEvent();}
public sealed class ResearchAndDevelopment {
 private readonly Dictionary<string,ScienceSubject> scienceSubjects=new Dictionary<string,ScienceSubject>();
 public static ScienceExperiment GetExperiment(string id){return id=="test"?new ScienceExperiment{id=id}:null;}
 public float LabAward;public Action AfterRecovery;
 public void Recover(ProtoVessel vessel){onVesselRecoveryProcessing(vessel,null,1);}
 [MethodImpl(MethodImplOptions.NoInlining)] private void onVesselRecoveryProcessing(ProtoVessel vessel,MissionRecoveryDialog dialog,float score){
  foreach(var part in vessel.GetAllProtoPartsIncludingCargo())foreach(var module in part.modules)foreach(var data in module.moduleValues.GetNodes("ScienceData")){
   ScienceSubject subject;if(!scienceSubjects.TryGetValue(data.GetValue("subjectID"),out subject))continue;
   SubmitScienceData(float.Parse(data.GetValue("data"),CultureInfo.InvariantCulture),1,subject,1,vessel,false);
  }
  if(LabAward>0)AddScience(LabAward,TransactionReasons.ScienceTransmission);if(AfterRecovery!=null)AfterRecovery();
 }
 public float Science;
 public bool ThrowBeforeAward,ThrowAfterAward,ThrowInsideCurrencyEvent,ThrowBeforeWallet;
 public Action AfterAward;
 [MethodImpl(MethodImplOptions.NoInlining)] private ScienceSubject getScienceSubject(ScienceSubject subject){return subject;}
 public static float GetSubjectValue(float science,ScienceSubject subject){return Math.Max(0,1-science/subject.scienceCap);}
 [MethodImpl(MethodImplOptions.NoInlining)] public void AddScience(float value,TransactionReasons reason){if(ThrowBeforeWallet)throw new InvalidOperationException("before wallet mutation");Science+=value;if(ThrowInsideCurrencyEvent)throw new InvalidOperationException("currency event after wallet mutation");}
 // Stock order verified from the actual licensed KSP assembly: subject cache
 // read by recovery, cumulative change, AddScience, then OnScienceRecieved.
 [MethodImpl(MethodImplOptions.NoInlining)] public float SubmitScienceData(float dataAmount,float scienceValueRatio,ScienceSubject subject,float xmitScalar,ProtoVessel source,bool reverseEngineered){
  float value=subject.applyScienceScale?Math.Min(dataAmount*subject.scientificValue,subject.scienceCap-subject.science):dataAmount;
  subject.science+=value;if(subject.applyScienceScale)subject.scientificValue=GetSubjectValue(subject.science,subject);
  if(ThrowBeforeAward)throw new InvalidOperationException("before wallet award");
  value*=HighLogic.CurrentGame.Parameters.Career.ScienceGainMultiplier;
  AddScience(value,xmitScalar==1?TransactionReasons.VesselRecovery:TransactionReasons.ScienceTransmission);
  if(AfterAward!=null)AfterAward();
  if(ThrowAfterAward)throw new InvalidOperationException("after wallet award");
  GameEvents.OnScienceRecieved.Fire(value,subject,source,reverseEngineered);return value;
 }
}
namespace LmpClient {public static class MainSystem {public static string UniqueIdentifier="fixture-account";}}
namespace LmpClient.Apf {public static class ApfClientSettings {public static string ClientVersion="fixture-rc48";}}
namespace LmpClient.Aetherfront {public static class AetherfrontClientSystem {
 public static AetherfrontState State=new AetherfrontState{ServerId="fixture-server"};
 public static void RequestSnapshot(){}
}}
namespace ApfCore {public static class ApfPaths {public static void EnsureNoReparsePoints(string root,string path){MonoGate.RequirePrivate(path);}}}
namespace KesasInstall {public static class Installer {public static int Saves;public static void AtomicText(string path,string text){MonoGate.RequirePrivate(path);Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";File.WriteAllText(temp,text);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);Saves++;}}}
namespace Aetherfront.Personal.Client {public static class PersonalClient {
 public static bool Active=true;
 public static readonly Dictionary<string,decimal> Credits=new Dictionary<string,decimal>();
 public static readonly List<AetherfrontWireRequest> Sends=new List<AetherfrontWireRequest>();
 internal static decimal CreditedScience(string id){decimal value;return Credits.TryGetValue(id,out value)?value:0;}
 public static void RestoreSubject(ScienceSubject __result){SubjectHistory.Restore(__result);}
 internal static void SendScienceClaim(AetherfrontWireRequest value){Sends.Add(value);}
}}
