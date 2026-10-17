using ApfCore;
using LmpClient.Apf;
using LmpClient.Base;
using UnityEngine;

namespace LmpClient.Windows.Apf
{
    public class ApfSyncWindow : Window<ApfSyncWindow>
    {
        private const float WindowWidth = 700;
        private const float WindowHeight = 600;
        private Vector2 _scroll;
        private bool _display;

        public override bool Display
        {
            get => base.Display && _display && HighLogic.LoadedScene == GameScenes.MAINMENU;
            set => base.Display = _display = value;
        }

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width / 2f - WindowWidth / 2f, Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);
        }

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6726 + MainSystem.WindowOffset, WindowRect, DrawContent, "APF Server Pack"));
        }

        protected override void DrawWindowContent(int windowId)
        {
            GUI.DragWindow(MoveRect);
            GUILayout.BeginVertical();
            GUILayout.Label("APF managed pack synchronization", BoldGreenLabelStyle);
            if (ApfSyncCoordinator.Manifest != null)
                GUILayout.Label($"Server {ApfSyncCoordinator.Manifest.ServerId}  |  Pack {ApfSyncCoordinator.Manifest.PackId} {ApfSyncCoordinator.Manifest.PackVersion}");
            GUILayout.Label(ApfSyncCoordinator.Status);
            if (!string.IsNullOrEmpty(ApfSyncCoordinator.Error)) GUILayout.Label(ApfSyncCoordinator.Error, BoldRedLabelStyle);

            if (ApfSyncCoordinator.Plan != null)
            {
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(420));
                foreach (var install in ApfSyncCoordinator.Plan.Install)
                    GUILayout.Label("INSTALL  " + install.Package.Id + " " + install.Package.Version +
                        "  " + FormatBytes(install.Package.SizeBytes) + "  -> GameData/" + ApfPaths.PackageRoot(install.Package) +
                        "  (" + install.Reason + ")", BoldGreenLabelStyle);
                foreach (var deletion in ApfSyncCoordinator.Plan.Delete)
                    GUILayout.Label("DELETE   " + deletion.RelativePath + "  (PERMANENT)  (" + deletion.Reason + ")", BoldRedLabelStyle);
                GUILayout.EndScrollView();
                if (ApfSyncCoordinator.Plan.Delete.Count > 0)
                    GUILayout.Label("Warning: approved deletions are permanent. APF does not create save backups.", BoldRedLabelStyle);
            }

            GUI.enabled = ApfSyncCoordinator.State == ApfSyncState.Ready;
            if (GUILayout.Button("Accept & Sync")) ApfSyncCoordinator.Accept();
            GUI.enabled = true;
            if (ApfSyncCoordinator.State == ApfSyncState.Failed && GUILayout.Button("Retry"))
                ApfSyncCoordinator.Retry();
            if (ApfSyncCoordinator.State != ApfSyncState.Downloading && ApfSyncCoordinator.State != ApfSyncState.Launching && GUILayout.Button("Cancel"))
                ApfSyncCoordinator.Cancel();
            GUILayout.EndVertical();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KiB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + " MiB";
        }

        public override void Update()
        {
            base.Update();
            ApfSyncCoordinator.Tick();
        }
    }
}
