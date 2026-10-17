using System.Net;
using System.Reflection;
using ApfCore;
using KesasInstall;
using LmpClient;
using LmpClient.Apf;
using LmpClient.Network;
using LmpCommon.Enums;
using UnityEngine;

int checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
FieldInfo Field(string name)=>typeof(ConnectionInstall).GetField(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new Exception("Missing field "+name);
void Set(string name,object value)=>Field(name).SetValue(null,value);
T Get<T>(string name)=>(T)Field(name).GetValue(null);
void Reset() {
    Check(Get<Task<InstallResult>>("task")==null,"Previous task must complete");
    foreach(string name in new[]{"launching","canceled","approved","expanded","bypass"})Set(name,false);
    foreach(string name in new[]{"pending","progress","confirmation","endpoint","password","installer"})Set(name,null);
    Set("loaded",true);Set("mode","full");
    HighLogic.LoadedScene=GameScenes.MAINMENU;MainSystem.NetworkState=ClientState.Disconnected;
    ApfPostExitLauncher.Launches=0;ApfPostExitLauncher.Denied=false;ApfPostExitLauncher.Entered.Reset();ApfPostExitLauncher.Ready.Reset();
    Installer.Constructions=Installer.Writes=Installer.Cancels=0;Application.Quits=NetworkConnection.Connects=0;NetworkConnection.BypassAccepted=false;GUI.enabled=true;
}
InstallResult Result()=>new(){Changed=true,TransactionPath="prepared-transaction.json"};
void Complete(InstallResult result) {
    Set("endpoint",new IPEndPoint(IPAddress.Loopback,18865));Set("password","fixture password");
    Set("task",Task.FromResult(result));ConnectionInstall.Tick();
}
string[] Draw(EventType type) { Event.current.type=type;GUILayout.Controls.Clear();ConnectionInstall.DrawProfile();return GUILayout.Controls.ToArray(); }
void FinishLaunch(){Check(SpinWait.SpinUntil(()=>Get<Task<InstallResult>>("task").IsCompleted,5000),"Helper completion");ConnectionInstall.Tick();}

Reset();Check(ConnectionInstall.Title=="Aetherfront RC25","Dynamic RC25 title");
Complete(Result());
Check(ConnectionInstall.Busy && Get<InstallResult>("pending")!=null,"Changed result remains pending and busy");
Check(ApfPostExitLauncher.Launches==0 && Application.Quits==0 && Installer.Writes==0,"Pending does not launch, quit, or write reconnect");
Check(!ConnectionInstall.ShouldConnect(new[]{new IPEndPoint(IPAddress.Loopback,18865)},""),"Pending blocks duplicate connection");
Check(Installer.Constructions==0,"Pending blocks duplicate installer");
var pendingLayout=Draw(EventType.Layout);Check(pendingLayout.Contains("button:Restart & apply"),"Explicit in-game restart button");
ConnectionInstall.Cancel();
Check(!ConnectionInstall.Busy && Get<InstallResult>("pending")==null,"Cancel clears pending");
Check(Installer.Writes==0 && Application.Quits==0,"Pending cancel keeps installed files and reconnect unchanged");
Check(pendingLayout.SequenceEqual(Draw(EventType.Repaint)),"Cancel cannot change controls inside a Layout cycle");
Check(!Draw(EventType.Layout).Contains("button:Restart & apply"),"Next Layout removes pending controls");
ConnectionInstall.RestartAndApply();Check(ApfPostExitLauncher.Launches==0,"Canceled restart is inert");

Reset();Complete(Result());ConnectionInstall.RestartAndApply();
Check(ApfPostExitLauncher.Entered.Wait(5000),"Helper launched on explicit action");
Check(Get<ProgressInfo>("progress").Status=="Securing prepared files...","Readiness progress");
var launchTask=Get<Task<InstallResult>>("task");
ConnectionInstall.RestartAndApply();ConnectionInstall.Cancel();ConnectionInstall.Tick();
Check(ApfPostExitLauncher.Launches==1 && ReferenceEquals(launchTask,Get<Task<InstallResult>>("task")),"No duplicate helper");
Check(ConnectionInstall.Busy && Application.Quits==0,"No quit before authenticated readiness");
Draw(EventType.Layout);Check(ConnectionInstall.LayoutLaunching && !GUILayout.Controls.Contains("button:Restart & apply"),"No restart action while launching");
Check(Installer.Writes==1,"Reconnect written only on explicit restart");
ApfPostExitLauncher.Ready.Set();FinishLaunch();Check(Application.Quits==1,"Quit after readiness only");
ConnectionInstall.Tick();Check(Application.Quits==1 && ConnectionInstall.Busy,"Quit issued once; remains busy until process exits");

Reset();Complete(Result());ApfPostExitLauncher.Denied=true;ApfPostExitLauncher.Ready.Set();ConnectionInstall.RestartAndApply();FinishLaunch();
Check(Application.Quits==0 && !ConnectionInstall.Busy && Get<InstallResult>("pending")==null,"Authorization failure never quits; fresh validation required");
Check(MainSystem.Singleton.Status=="authorization denied","Authorization error preserved");

Reset();Complete(new InstallResult{Changed=false});
Check(NetworkConnection.Connects==1 && NetworkConnection.BypassAccepted,"Unchanged files connect with existing bypass");
Check(!ConnectionInstall.Busy && ApfPostExitLauncher.Launches==0 && Application.Quits==0,"Unchanged files do not restart");

Reset();var canceledDownload=new TaskCompletionSource<InstallResult>();Set("task",canceledDownload.Task);ConnectionInstall.Cancel();
Check(ConnectionInstall.Busy,"Cancel waits for active task to settle");canceledDownload.SetResult(Result());ConnectionInstall.Tick();
Check(!ConnectionInstall.Busy && Get<InstallResult>("pending")==null && ApfPostExitLauncher.Launches==0,"Canceled completion cannot become pending");

Reset();Complete(Result());HighLogic.LoadedScene=GameScenes.FLIGHT;ConnectionInstall.RestartAndApply();
Check(ConnectionInstall.Busy && ApfPostExitLauncher.Launches==0 && Application.Quits==0,"Restart rejected outside main menu; pending retained");
ConnectionInstall.Cancel();
Reset();HighLogic.LoadedScene=GameScenes.FLIGHT;Complete(Result());
Check(!ConnectionInstall.Busy && Get<InstallResult>("pending")==null,"Completion outside main menu rejected");

Reset();Set("task",new TaskCompletionSource<InstallResult>().Task);
var progress=new ProgressInfo{Status="Downloading",Total=100,Downloaded=25,Package="fixture",PackageIndex=1,PackageCount=2,PackageTotal=50,PackageDownloaded=25,Transferring=true,BytesPerSecond=5,EstimatedSeconds=15};
Set("progress",progress);Set("confirmation","Approve fixture changes");var stable=Draw(EventType.Layout);
Check(stable.Any(s=>s.Contains("Estimated remaining: 00:00:15")),"Existing ETA retained");
progress.Total=0;progress.Transferring=false;progress.PackageTotal=0;Set("confirmation",null);
Check(stable.SequenceEqual(Draw(EventType.Repaint)),"Mutable progress and confirmation frozen for Layout cycle");
Check(!stable.SequenceEqual(Draw(EventType.Layout)),"Next Layout sees progress changes");Set("task",null);

Reset();var idle=Draw(EventType.Layout);Complete(Result());
Check(idle.SequenceEqual(Draw(EventType.Repaint)),"Async completion cannot change Layout control shape");
Check(Draw(EventType.Layout).Contains("button:Restart & apply"),"Next Layout shows completed download");ConnectionInstall.Cancel();

Console.WriteLine($"PASS {checks} flow assertions; source linked directly; no real helper, network, game, or filesystem mutation.");
