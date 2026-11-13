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
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
FieldInfo Field(string name)=>typeof(ConnectionInstall).GetField(name,BindingFlags.Static|BindingFlags.NonPublic)??throw new Exception(name);
void Set(string name,object value)=>Field(name).SetValue(null,value);
T Get<T>(string name)=>(T)Field(name).GetValue(null);
void Reset(){
 Check(Get<Task<InstallResult>>("task")==null,"Previous worker settled");
 foreach(string name in new[]{"launching","canceled","expanded","bypass","repairRequested"})Set(name,false);
 foreach(string name in new[]{"pending","progress","endpoint","password","installer","reconnectWritten"})Set(name,null);
 Set("loaded",true);Set("mode","full");HighLogic.LoadedScene=GameScenes.MAINMENU;MainSystem.NetworkState=ClientState.Disconnected;
 ApfPostExitLauncher.Launches=0;ApfPostExitLauncher.Denied=false;ApfPostExitLauncher.Entered.Reset();ApfPostExitLauncher.Ready.Reset();
 Installer.Constructions=Installer.Writes=Installer.Cancels=0;Application.Quits=NetworkConnection.Connects=0;NetworkConnection.BypassAccepted=false;GUI.enabled=true;
}
void Complete(InstallResult result){Set("endpoint",new IPEndPoint(IPAddress.Loopback,18865));Set("password","fixture password");Set("task",Task.FromResult(result));ConnectionInstall.Tick();}
InstallResult Changed()=>new(){Changed=true,TransactionPath="prepared-transaction.json"};
void Finish(){Check(SpinWait.SpinUntil(()=>Get<Task<InstallResult>>("task").IsCompleted,5000),"Helper completion bounded");ConnectionInstall.Tick();}
string[] Draw(EventType type){Event.current.type=type;GUILayout.Controls.Clear();ConnectionInstall.DrawProfile();return GUILayout.Controls.ToArray();}

Reset();Complete(Changed());
Check(ApfPostExitLauncher.Entered.Wait(5000),"Changed result automatically starts helper");
Check(Installer.Writes==1 && Application.Quits==0,"Reconnect saved; no quit before authenticated readiness");
Check(ConnectionInstall.Busy && ConnectionInstall.LayoutDisconnected==false,"Flow remains busy while helper secures changes");
Check(!ConnectionInstall.ShouldConnect(new[]{new IPEndPoint(IPAddress.Loopback,18865)},""),"Duplicate connect rejected");
var task=Get<Task<InstallResult>>("task");ConnectionInstall.RestartAndApply();ConnectionInstall.Cancel();ConnectionInstall.Tick();
Check(ApfPostExitLauncher.Launches==1 && ReferenceEquals(task,Get<Task<InstallResult>>("task")),"Automatic launch remains once-only");
var controls=Draw(EventType.Layout);Check(!controls.Any(s=>s.Contains("Restart & apply")||s.Contains("Approve listed changes")),"No routine confirmation controls");
ApfPostExitLauncher.Ready.Set();Finish();Check(Application.Quits==1,"Automatic quit only after readiness");
ConnectionInstall.Tick();Check(Application.Quits==1,"Quit issued once");

Reset();ApfPostExitLauncher.Denied=true;Complete(Changed());ApfPostExitLauncher.Ready.Set();Finish();
Check(Application.Quits==0 && !ConnectionInstall.Busy,"Helper rejection never quits or automatically retries");
Check(MainSystem.Singleton.Status=="authorization denied","Helper failure preserved");
Check(Get<string>("reconnectWritten")==null,"Failed helper clears owned reconnect intent");

Reset();Complete(new InstallResult());
Check(NetworkConnection.Connects==1 && NetworkConnection.BypassAccepted,"Current installation connects automatically");
Check(!ConnectionInstall.Busy && ApfPostExitLauncher.Launches==0 && Application.Quits==0,"Current installation needs no restart");

Reset();var canceled=new TaskCompletionSource<InstallResult>();Set("task",canceled.Task);ConnectionInstall.Cancel();
Check(ConnectionInstall.Busy,"Cancel waits for download worker");canceled.SetResult(Changed());ConnectionInstall.Tick();
Check(!ConnectionInstall.Busy && ApfPostExitLauncher.Launches==0 && Application.Quits==0,"Canceled download cannot auto-apply");

Reset();HighLogic.LoadedScene=GameScenes.FLIGHT;Complete(Changed());
Check(!ConnectionInstall.Busy && ApfPostExitLauncher.Launches==0 && Application.Quits==0,"No automatic restart from flight");
Reset();Complete(Changed());Check(ApfPostExitLauncher.Entered.Wait(5000),"Scene race helper entered");
HighLogic.LoadedScene=GameScenes.FLIGHT;ApfPostExitLauncher.Ready.Set();Finish();
Check(Application.Quits==0 && !ConnectionInstall.Busy && Installer.Writes==2,"Scene change disarms staged helper instead of closing gameplay");

Reset();var confirm=typeof(ConnectionInstall).GetMethod("Confirm",BindingFlags.Static|BindingFlags.NonPublic);
Check((bool)confirm.Invoke(null,new object[]{"fixture managed change"}),"Connect authorizes managed update without waiting for a prompt");
Set("canceled",true);bool rejected=false;try{confirm.Invoke(null,new object[]{"fixture"});}catch(TargetInvocationException e)when(e.InnerException is OperationCanceledException){rejected=true;}
Check(rejected,"Cancellation still vetoes automatic approval");

Reset();Set("task",new TaskCompletionSource<InstallResult>().Task);
var progress=new ProgressInfo{Status="Downloading",Total=100,Downloaded=25,Package="fixture",PackageIndex=1,PackageCount=2,PackageTotal=50,PackageDownloaded=25,Transferring=true,BytesPerSecond=5,EstimatedSeconds=15};
Set("progress",progress);var before=Draw(EventType.Layout);progress.Total=0;progress.Transferring=false;progress.PackageTotal=0;
Check(before.SequenceEqual(Draw(EventType.Repaint)),"Progress remains stable across Layout/Repaint");Set("task",null);

Reset();var idle=Draw(EventType.Layout);Complete(Changed());
Check(idle.SequenceEqual(Draw(EventType.Repaint)),"Automatic completion cannot break a GUI layout cycle");
ApfPostExitLauncher.Ready.Set();Finish();
Console.WriteLine($"PASS {checks} automatic connection flow assertions; no real helper, network, game, or install mutation.");
