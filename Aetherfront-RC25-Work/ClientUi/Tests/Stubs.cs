using System.Net;
using LmpClient.Apf;

public enum GameScenes { MAINMENU, FLIGHT }
public static class HighLogic { public static GameScenes LoadedScene=GameScenes.MAINMENU; }
namespace LmpCommon.Enums { public enum ClientState { Disconnected,Connecting,Running } }
namespace LmpClient.Base { public static class TestNamespace { } }
namespace LmpClient {
    public sealed class MainSystem {
        public static MainSystem Singleton=new MainSystem();
        public static string KspPath="Z:/nonexistent-flow-test";
        public static LmpCommon.Enums.ClientState NetworkState;
        public string Status;
    }
}
namespace LmpClient.Apf {
    public static class ApfClientSettings {
        public static bool Enabled=true;
        public static string ClientVersion="kesas-rc25";
        public static string ReconnectPath="Z:/nonexistent-flow-test/reconnect.json";
    }
}
namespace LmpClient.Network {
    public static class NetworkConnection {
        public static int Connects;
        public static bool BypassAccepted;
        public static void ConnectToServer(IPEndPoint[] endpoints,string password){Connects++;BypassAccepted=ConnectionInstall.ShouldConnect(endpoints,password);}
    }
}
namespace ApfCore {
    public static class ApfPaths { public static void EnsureNoReparsePoints(string root,string path){} }
    public static class ApfJson { public static string Serialize(object value)=>"reconnect-test"; }
    public sealed class ApfReconnectInfo { public string Address;public int Port;public string Password; }
    public static class ApfPostExitLauncher {
        public static int Launches;
        public static bool Denied;
        public static readonly ManualResetEventSlim Entered=new(false),Ready=new(false);
        public static void Launch(string root,string path,bool clientUpdate) {
            Interlocked.Increment(ref Launches);Entered.Set();
            if(!Ready.Wait(5000))throw new TimeoutException("Fixture helper gate timed out");
            if(Denied)throw new IOException("authorization denied");
        }
    }
}
namespace KesasInstall {
    public sealed class InstallResult { public bool Changed {get;set;}public bool ClientUpdate {get;set;}public string TransactionPath {get;set;} }
    public sealed class ProgressInfo {
        public string Status {get;set;}public string Package {get;set;}public int PackageIndex {get;set;}public int PackageCount {get;set;}
        public long Downloaded {get;set;}public long Total {get;set;}public long PackageDownloaded {get;set;}public long PackageTotal {get;set;}
        public double BytesPerSecond {get;set;}public double EstimatedSeconds {get;set;}=-1;public bool Transferring {get;set;}
    }
    public sealed class Installer {
        public static int Constructions,Writes,Cancels;
        public Func<bool> IsCanceled;public Action<ProgressInfo> Report;public Func<string,bool> Confirm;
        public Installer(string root,IPEndPoint endpoint,string version){Constructions++;}
        public InstallResult Run(string mode)=>new();
        public void Cancel(){Cancels++;}
        public static void AtomicText(string path,string value){Writes++;}
        public static string Format(long value)=>value+" B";
    }
}
namespace UnityEngine {
    public enum EventType { Layout,Repaint }
    public sealed class Event { public static Event current=new();public EventType type; }
    public class GUIStyle { public bool wordWrap,richText;public GUIStyle(){}public GUIStyle(GUIStyle other){} }
    public sealed class GUISkin { public GUIStyle label=new(); }
    public struct Vector2 { }
    public struct Color { public Color(float r,float g,float b,float a){} }
    public struct Rect { public float x,y,width,height;public Rect(float x,float y,float width,float height){this.x=x;this.y=y;this.width=width;this.height=height;} }
    public static class Texture2D { public static object whiteTexture=new(); }
    public sealed class GUILayoutOption { }
    public static class Application { public static int Quits;public static void Quit(){Quits++;} }
    public static class GUI {
        public static bool enabled=true;public static Color color;public static GUISkin skin=new();
        public static void Box(Rect rect,string value){}
        public static void DrawTexture(Rect rect,object texture){}
        public static void Label(Rect rect,string value,GUIStyle style){GUILayout.Controls.Add("bar:"+value);}
    }
    public static class GUILayoutUtility { public static Rect GetRect(float w,float h)=>new(0,0,w,h); }
    public static class GUILayout {
        public static readonly List<string> Controls=new();public static string Press;
        public static void Label(string text){Controls.Add("label:"+text);}
        public static void Label(string text,GUIStyle style){Label(text);}
        public static bool Button(string text){Controls.Add("button:"+text);if(GUI.enabled && Press==text){Press=null;return true;}return false;}
        public static Vector2 BeginScrollView(Vector2 current,params GUILayoutOption[] options){Controls.Add("scroll");return current;}
        public static void EndScrollView(){Controls.Add("end-scroll");}
        public static GUILayoutOption Height(float height)=>new();
    }
}
