using System;
using ApfCore;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Apf
{
    public static class FirstInstall
    {
        public static void Tick()
        {
            if(HighLogic.LoadedScene!=GameScenes.MAINMENU || MainSystem.NetworkState>=ClientState.Connected) return;
            try { if(KesasInstallPolicy.LaunchPending(MainSystem.KspPath)) Application.Quit(); }
            catch(Exception error) { Debug.LogError("[KESAS] Open Launch KESAS.cmd to finish installation: "+error.Message); }
        }
    }
}
