using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ApfCore;
using KesasInstall;
using LmpClient.Network;
using LmpClient.Base;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Apf
{
    public static class ConnectionInstall
    {
        private static Task<InstallResult> task;
        private static Installer installer;
        private static IPEndPoint endpoint;
        private static string password;
        private static string mode="full";
        private static bool loaded,expanded,bypass;
        private static volatile bool launching;
        private static InstallResult pending;
        private static volatile bool canceled;
        private static volatile ProgressInfo progress;
        private static volatile string confirmation;
        private static readonly ManualResetEventSlim confirmed=new ManualResetEventSlim(false);
        private static volatile bool approved;
        private static bool layoutBusy,layoutExpanded,layoutPending,layoutLaunching;
        private static ProgressInfo layoutProgress;
        private static string layoutConfirmation;
        private static GUIStyle textStyle;
        private static Vector2 detailsScroll;
        public static bool Busy {get {return task!=null || launching || pending!=null;}}
        public static bool LayoutBusy {get {return layoutBusy;}}
        public static bool LayoutLaunching {get {return layoutLaunching;}}
        public static bool LayoutDisconnected {get;private set;}
        public static string Title {get {return "Aetherfront "+ApfClientSettings.ClientVersion.Replace("kesas-","").ToUpperInvariant();}}
        public static bool ShouldConnect(IPEndPoint[] endpoints,string pass)
        {
            if(bypass || !ApfClientSettings.Enabled)return true;
            if(Busy || MainSystem.NetworkState>ClientState.Disconnected)return false;
            if(HighLogic.LoadedScene!=GameScenes.MAINMENU){MainSystem.Singleton.Status="Disconnect to the main menu before changing the install.";return false;}
            if(endpoints==null || endpoints.Length!=1){MainSystem.Singleton.Status="Enter one server IP and port.";return false;}
            try {
                LoadSelection();
                endpoint=endpoints[0];password=pass??"";canceled=false;confirmation=null;confirmed.Reset();
                installer=new Installer(MainSystem.KspPath,endpoint,ApfClientSettings.ClientVersion);
                installer.IsCanceled=IsCanceled;installer.Report=Report;installer.Confirm=Confirm;
                progress=new ProgressInfo{Status="Checking server requirements..."};
                task=Task.Factory.StartNew(Work);
            }catch(Exception error){MainSystem.Singleton.Status=error.GetBaseException().Message;}
            return false;
        }
        private static InstallResult Work(){return installer.Run(mode);}
        private static bool IsCanceled(){return canceled;}
        private static void Report(ProgressInfo value){progress=value;}
        private static bool Confirm(string message)
        {
            approved=false;confirmed.Reset();confirmation=message;
            try{while(!confirmed.Wait(100)){if(canceled)throw new OperationCanceledException();}return approved;}
            finally{confirmation=null;}
        }
        public static void Cancel()
        {
            if(launching)return;
            canceled=true;approved=false;confirmation=null;confirmed.Set();
            if(pending!=null) {
                pending=null;progress=null;endpoint=null;password=null;
                MainSystem.Singleton.Status="Prepared install canceled. Installed files were kept.";
            }else if(task!=null) {
                progress=new ProgressInfo{Status="Canceling; installed files will be kept..."};
                installer?.Cancel();
            }
        }
        public static void RestartAndApply()
        {
            if(pending==null || task!=null || launching)return;
            try {
                if(HighLogic.LoadedScene!=GameScenes.MAINMENU)throw new InvalidOperationException("Return to the main menu before applying prepared files.");
                string reconnect=ApfClientSettings.ReconnectPath;
                ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath,reconnect);
                Installer.AtomicText(reconnect,ApfJson.Serialize(new ApfReconnectInfo{Address=endpoint.Address.ToString(),Port=endpoint.Port,Password=password}));
                progress=new ProgressInfo{Status="Securing prepared files..."};
                launching=true;task=Task.Factory.StartNew(Launch);
            }catch(Exception error){launching=false;MainSystem.Singleton.Status=error.GetBaseException().Message;}
        }
        private static InstallResult Launch(){ApfPostExitLauncher.Launch(MainSystem.KspPath,pending.TransactionPath,pending.ClientUpdate);return pending;}
        public static void Tick()
        {
            if(task==null || !task.IsCompleted)return;
            try {
                var result=task.GetAwaiter().GetResult();
                task=null;
                if(canceled){MainSystem.Singleton.Status="Download canceled. Installed files were kept.";return;}
                // Launch returns only after the authenticated helper publishes .ready.
                if(launching){Application.Quit();return;}
                if(HighLogic.LoadedScene!=GameScenes.MAINMENU)throw new InvalidOperationException("Return to the main menu and reconnect before installing or joining.");
                if(!result.Changed) {
                    MainSystem.Singleton.Status="Files match; connecting...";
                    bypass=true;try{NetworkConnection.ConnectToServer(new[]{endpoint},password);}finally{bypass=false;}
                }else{
                    pending=result;
                    progress=new ProgressInfo{Status="Prepared files verified. Choose Restart & apply to install and relaunch KSP, or Cancel to keep installed files."};
                    MainSystem.Singleton.Status="Ready to apply. Restart & apply is required; KSP will remain open until you choose it.";
                }
            }catch(Exception error){task=null;launching=false;pending=null;MainSystem.Singleton.Status=canceled?"Download canceled. Installed files were kept.":error.GetBaseException().Message;progress=null;}
            finally{if(task==null){installer=null;confirmation=null;if(pending==null && !launching)progress=null;}}
        }
        private static void LoadSelection()
        {
            if(loaded)return;
            string data=Path.Combine(MainSystem.KspPath,"GameData","LunaMultiplayer","Data");
            string selected=Path.Combine(data,"KesasInstallSelection.txt");
            string installed=Path.Combine(data,"KesasInstallProfile.json");
            if(File.Exists(selected))mode=File.ReadAllText(selected).Trim();
            else if(File.Exists(installed) && File.ReadAllText(installed).Contains("potato"))mode="potato";
            if(mode!="potato")mode="full";
            loaded=true;
        }
        public static void DrawProfile()
        {
            if(textStyle==null)textStyle=new GUIStyle(GUI.skin.label){wordWrap=true,richText=false};
            try{LoadSelection();}catch(Exception error){GUILayout.Label(error.Message);return;}
            CaptureLayout();
            bool enabled=GUI.enabled;
            GUI.enabled=enabled && !layoutBusy && LayoutDisconnected;
            GUILayout.Label("Install profile");
            if(GUILayout.Button((mode=="potato"?"Potato":"Regular")+"  ▼"))expanded=!expanded;
            if(layoutExpanded) {
                if(GUILayout.Button("Regular"))Select("full");
                if(GUILayout.Button("Potato"))Select("potato");
            }
            GUI.enabled=enabled;
            if(layoutBusy && layoutProgress!=null) {
                var p=layoutProgress;
                detailsScroll=GUILayout.BeginScrollView(detailsScroll,GUILayout.Height(200));
                GUILayout.Label(p.Status,textStyle);
                if(p.Total>0) {
                    double ratio=Math.Min(1,Math.Max(0,(double)p.Downloaded/p.Total));
                    Rect bar=GUILayoutUtility.GetRect(340,20);
                    GUI.Box(bar,"");
                    Color previousColor=GUI.color;
                    GUI.color=new Color(0.2f,0.7f,0.3f,0.85f);
                    GUI.DrawTexture(new Rect(bar.x+1,bar.y+1,(float)((bar.width-2)*ratio),bar.height-2),Texture2D.whiteTexture);
                    GUI.color=previousColor;
                    GUI.Label(bar,(ratio*100).ToString("0.0")+"%",GUI.skin.label);
                    GUILayout.Label(Installer.Format(p.Downloaded)+" / "+Installer.Format(p.Total)+(p.Transferring && p.BytesPerSecond>0?" · "+Installer.Format((long)p.BytesPerSecond)+"/s":""));
                    if(p.Transferring && p.Downloaded<p.Total)GUILayout.Label(p.EstimatedSeconds<0?"Estimated remaining: estimating...":"Estimated remaining: "+FormatRemaining(p.EstimatedSeconds));
                    if(p.PackageCount>0)GUILayout.Label("Package "+p.PackageIndex+" / "+p.PackageCount+": "+p.Package);
                    if(p.PackageTotal>0)GUILayout.Label("This package: "+Installer.Format(p.PackageDownloaded)+" / "+Installer.Format(p.PackageTotal));
                }
                if(layoutConfirmation!=null) {
                    GUILayout.Label(layoutConfirmation,textStyle);
                    if(GUILayout.Button("Approve listed changes")){approved=true;confirmed.Set();}
                    if(GUILayout.Button("Keep existing mods; cancel"))Cancel();
                }
                GUI.enabled=enabled && !layoutLaunching;
                if(layoutPending && !layoutLaunching && GUILayout.Button("Restart & apply"))RestartAndApply();
                if(GUILayout.Button(layoutPending?"Cancel":"Cancel download"))Cancel();
                GUI.enabled=enabled;
                GUILayout.EndScrollView();
            }
        }
        public static void CaptureLayout()
        {
            if(Event.current.type!=EventType.Layout)return;
            layoutBusy=Busy;layoutExpanded=expanded;layoutPending=pending!=null;layoutLaunching=launching;
            LayoutDisconnected=MainSystem.NetworkState<=ClientState.Disconnected;
            layoutConfirmation=confirmation;
            // Reporters may reuse a mutable ProgressInfo; freeze every branch-driving value.
            var p=progress;
            layoutProgress=p==null?null:new ProgressInfo{Status=p.Status,Package=p.Package,PackageIndex=p.PackageIndex,PackageCount=p.PackageCount,
                Downloaded=p.Downloaded,Total=p.Total,PackageDownloaded=p.PackageDownloaded,PackageTotal=p.PackageTotal,
                BytesPerSecond=p.BytesPerSecond,EstimatedSeconds=p.EstimatedSeconds,Transferring=p.Transferring};
        }
        private static string FormatRemaining(double seconds)
        {
            var time=TimeSpan.FromSeconds(Math.Min(86399999,Math.Max(0,seconds)));
            return ((int)time.TotalHours).ToString("00")+":"+time.Minutes.ToString("00")+":"+time.Seconds.ToString("00");
        }
        private static void Select(string value)
        {
            string path=Path.Combine(MainSystem.KspPath,"GameData","LunaMultiplayer","Data","KesasInstallSelection.txt");
            try{ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath,path);Installer.AtomicText(path,value+Environment.NewLine);mode=value;expanded=false;}
            catch(Exception error){MainSystem.Singleton.Status="Could not save install profile: "+error.Message;}
        }
    }
}
