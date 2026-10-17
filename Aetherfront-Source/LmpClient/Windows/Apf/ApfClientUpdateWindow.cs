using LmpClient.Apf;
using LmpClient.Base;
using UnityEngine;

namespace LmpClient.Windows.Apf
{
    public class ApfClientUpdateWindow : Window<ApfClientUpdateWindow>
    {
        private const float WindowWidth = 600;
        private const float WindowHeight = 360;
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
            WindowRect = FixWindowPos(GUILayout.Window(6727 + MainSystem.WindowOffset, WindowRect, DrawContent, "APF Client Update"));
        }

        protected override void DrawWindowContent(int windowId)
        {
            GUI.DragWindow(MoveRect);
            GUILayout.BeginVertical();
            GUILayout.Label("APF client self-update", BoldGreenLabelStyle);
            GUILayout.Label(ApfClientUpdateCoordinator.Status);
            if (ApfClientUpdateCoordinator.Manifest != null)
            {
                var manifest = ApfClientUpdateCoordinator.Manifest;
                GUILayout.Label("Version: " + manifest.Version);
                GUILayout.Label("Download: " + FormatBytes(manifest.SizeBytes));
                if (!string.IsNullOrWhiteSpace(manifest.KspVersion)) GUILayout.Label("KSP: " + manifest.KspVersion);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(150));
                GUILayout.Label(string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? "No release notes." : manifest.ReleaseNotes);
                GUILayout.EndScrollView();
            }
            if (!string.IsNullOrEmpty(ApfClientUpdateCoordinator.Error))
                GUILayout.Label(ApfClientUpdateCoordinator.Error, BoldRedLabelStyle);

            GUI.enabled = ApfClientUpdateCoordinator.State == ApfClientUpdateState.Available;
            if (GUILayout.Button("Install & Restart KSP")) ApfClientUpdateCoordinator.Accept();
            GUI.enabled = true;
            if (ApfClientUpdateCoordinator.State == ApfClientUpdateState.Failed)
            {
                if (GUILayout.Button("Retry")) ApfClientUpdateCoordinator.Retry();
            }
            else if (ApfClientUpdateCoordinator.State != ApfClientUpdateState.Downloading &&
                     ApfClientUpdateCoordinator.State != ApfClientUpdateState.Launching &&
                     GUILayout.Button("Later")) ApfClientUpdateCoordinator.Cancel();
            GUILayout.EndVertical();
        }

        public override void Update()
        {
            base.Update();
            ApfClientUpdateCoordinator.Tick();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KiB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + " MiB";
        }
    }
}
