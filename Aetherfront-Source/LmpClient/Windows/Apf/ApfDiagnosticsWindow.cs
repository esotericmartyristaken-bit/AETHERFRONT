using LmpClient.Apf;
using LmpClient.Base;
using UnityEngine;

namespace LmpClient.Windows.Apf
{
    public sealed class ApfDiagnosticsWindow : Window<ApfDiagnosticsWindow>
    {
        private const float WindowWidth = 680;
        private const float WindowHeight = 540;
        private Vector2 _scroll;
        private string _description = string.Empty;
        private bool _consent;

        public override void SetStyles()
        {
            WindowRect = new Rect(Screen.width / 2f - WindowWidth / 2f, Screen.height / 2f - WindowHeight / 2f, WindowWidth, WindowHeight);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);
        }

        protected override void DrawGui()
        {
            WindowRect = FixWindowPos(GUILayout.Window(6728 + MainSystem.WindowOffset, WindowRect, DrawContent, "Aetherfront Diagnostics"));
        }

        protected override void DrawWindowContent(int windowId)
        {
            GUI.DragWindow(MoveRect);
            GUILayout.BeginVertical();
            GUILayout.Label("Submit a diagnostic report", BoldGreenLabelStyle);
            GUILayout.Label("Creates a local preview containing recent redacted logs, network context, and a DLL inventory. Saves, craft files, screenshots, and arbitrary files are never included.");

            if (ApfDiagnosticsCoordinator.State == ApfDiagnosticsState.Idle || ApfDiagnosticsCoordinator.State == ApfDiagnosticsState.Failed)
            {
                GUILayout.Label("What happened? Optional; 2,000 characters maximum.");
                _description = GUILayout.TextArea(_description, 2000, GUILayout.Height(90));
                GUI.enabled = ApfDiagnosticsCoordinator.State != ApfDiagnosticsState.Preparing &&
                              ApfDiagnosticsCoordinator.State != ApfDiagnosticsState.Uploading;
                if (GUILayout.Button("Prepare Diagnostic Preview"))
                {
                    _consent = false;
                    ApfDiagnosticsCoordinator.Prepare(_description);
                }
                GUI.enabled = true;
            }

            GUILayout.Label(ApfDiagnosticsCoordinator.Status);
            if (!string.IsNullOrWhiteSpace(ApfDiagnosticsCoordinator.Error))
                GUILayout.Label(ApfDiagnosticsCoordinator.Error, BoldRedLabelStyle);

            var bundle = ApfDiagnosticsCoordinator.Bundle;
            if (bundle != null)
            {
                GUILayout.Label("Report: " + bundle.ReportId);
                GUILayout.Label("Archive: " + FormatBytes(bundle.SizeBytes));
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(190));
                foreach (var entry in bundle.Entries)
                    GUILayout.Label(entry.Name + " - " + FormatBytes(entry.IncludedBytes) + (entry.Truncated ? " (latest section only)" : string.Empty));
                GUILayout.EndScrollView();
                _consent = GUILayout.Toggle(_consent, "I reviewed this file list and consent to upload this diagnostic archive.");

                GUI.enabled = _consent && ApfDiagnosticsCoordinator.UploadConfigured &&
                              ApfDiagnosticsCoordinator.State == ApfDiagnosticsState.Ready;
                if (GUILayout.Button("Submit Diagnostics")) ApfDiagnosticsCoordinator.Submit();
                GUI.enabled = true;

                if (!ApfDiagnosticsCoordinator.UploadConfigured)
                    GUILayout.Label("HTTPS upload is not configured in this build. Use Open Local Archive for manual transfer.");
                if (GUILayout.Button("Open Local Archive Folder")) ApfDiagnosticsCoordinator.OpenOutbox();
            }

            if (ApfDiagnosticsCoordinator.State == ApfDiagnosticsState.Submitted && !string.IsNullOrWhiteSpace(ApfDiagnosticsCoordinator.CaseId))
                GUILayout.Label("Keep this case ID: " + ApfDiagnosticsCoordinator.CaseId, BoldGreenLabelStyle);
            if (ApfDiagnosticsCoordinator.State != ApfDiagnosticsState.Preparing &&
                ApfDiagnosticsCoordinator.State != ApfDiagnosticsState.Uploading &&
                ApfDiagnosticsCoordinator.State != ApfDiagnosticsState.Idle &&
                GUILayout.Button("New Report"))
            {
                _consent = false;
                _description = string.Empty;
                ApfDiagnosticsCoordinator.Reset();
            }
            GUILayout.EndVertical();
        }

        public override void Update()
        {
            base.Update();
            ApfDiagnosticsCoordinator.Tick();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KiB";
            return (bytes / (1024f * 1024f)).ToString("0.0") + " MiB";
        }
    }
}
