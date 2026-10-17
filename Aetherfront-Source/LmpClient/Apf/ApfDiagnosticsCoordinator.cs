using ApfCore;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpClient.Windows.Apf;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace LmpClient.Apf
{
    public enum ApfDiagnosticsState
    {
        Idle,
        Preparing,
        Ready,
        Uploading,
        Submitted,
        Failed
    }

    public static class ApfDiagnosticsCoordinator
    {
        private const long MaximumReplyBytes = 64 * 1024;
        private static Task _task;
        private static ApfDiagnosticContext _pendingContext;

        public static string SessionId { get; } = Guid.NewGuid().ToString("N");
        public static ApfDiagnosticsState State { get; private set; } = ApfDiagnosticsState.Idle;
        public static ApfDiagnosticBundleResult Bundle { get; private set; }
        public static string Status { get; private set; } = string.Empty;
        public static string Error { get; private set; } = string.Empty;
        public static string CaseId { get; private set; } = string.Empty;
        public static bool UploadConfigured => IsHttps(ApfClientSettings.DiagnosticsEndpoint);

        public static void Show()
        {
            ApfDiagnosticsWindow.Singleton.Display = true;
        }

        public static void Prepare(string description)
        {
            if (_task != null || State == ApfDiagnosticsState.Preparing || State == ApfDiagnosticsState.Uploading) return;
            description = (description ?? string.Empty).Trim();
            if (description.Length > 2000)
            {
                Fail("Description is limited to 2,000 characters");
                return;
            }

            _pendingContext = new ApfDiagnosticContext
            {
                ClientVersion = ApfClientSettings.ClientVersion,
                KspVersion = CompatibilityChecker.KspVersion.ToString(),
                Scene = HighLogic.LoadedScene.ToString(),
                NetworkState = MainSystem.NetworkState.ToString(),
                ServerAddress = NetworkConnection.LastConnectAddress ?? string.Empty,
                ServerPort = NetworkConnection.LastConnectPort,
                PlayerName = SettingsSystem.CurrentSettings.PlayerName ?? string.Empty,
                SessionId = SessionId,
                Description = ApfDiagnosticBundleBuilder.Redact(description, MainSystem.KspPath)
            };
            Bundle = null;
            CaseId = string.Empty;
            Error = string.Empty;
            Status = "Preparing redacted diagnostic preview...";
            State = ApfDiagnosticsState.Preparing;
            _task = Task.Factory.StartNew(BuildBundle);
        }

        public static void Submit()
        {
            if (_task != null || State != ApfDiagnosticsState.Ready || Bundle == null) return;
            if (!UploadConfigured)
            {
                Fail("Diagnostic upload is not configured. The local archive remains in the APF diagnostics outbox.");
                return;
            }
            Error = string.Empty;
            Status = "Uploading diagnostic archive...";
            State = ApfDiagnosticsState.Uploading;
            _task = Task.Factory.StartNew(UploadBundle);
        }

        public static void Tick()
        {
            if (_task == null || !_task.IsCompleted) return;
            try { _task.GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                Error = exception.GetBaseException().Message;
                Status = "Diagnostic operation failed";
                State = ApfDiagnosticsState.Failed;
                LunaLog.LogError("[APF Diagnostics]: " + Error);
            }
            _task = null;
        }

        public static void Reset()
        {
            if (_task != null) return;
            Bundle = null;
            CaseId = string.Empty;
            Error = string.Empty;
            Status = string.Empty;
            State = ApfDiagnosticsState.Idle;
        }

        public static void OpenOutbox()
        {
            try
            {
                Directory.CreateDirectory(ApfClientSettings.DiagnosticsOutboxPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = ApfProcessArguments.Quote(ApfClientSettings.DiagnosticsOutboxPath),
                    UseShellExecute = true
                });
            }
            catch (Exception exception) { Fail("Could not open diagnostics outbox: " + exception.Message); }
        }

        private static void BuildBundle()
        {
            Bundle = ApfDiagnosticBundleBuilder.Build(MainSystem.KspPath, ApfClientSettings.DiagnosticsOutboxPath, _pendingContext);
            Status = "Preview ready. Review the included files before submitting.";
            State = ApfDiagnosticsState.Ready;
            LunaLog.Log("[APF Diagnostics]: Prepared " + Bundle.ReportId + " for session " + SessionId);
        }

        private static void UploadBundle()
        {
            var endpoint = new Uri(ApfClientSettings.DiagnosticsEndpoint, UriKind.Absolute);
            if (endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Diagnostic endpoint must use HTTPS");
            if (!File.Exists(Bundle.ArchivePath)) throw new FileNotFoundException("Prepared diagnostic archive is missing", Bundle.ArchivePath);
            var info = new FileInfo(Bundle.ArchivePath);
            if (info.Length <= 0 || info.Length > ApfDiagnosticBundleBuilder.MaximumArchiveBytes)
                throw new InvalidDataException("Diagnostic archive size is invalid");

            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            ApfPinnedHttps.Configure(request);
            request.Method = "POST";
            request.ContentType = "application/zip";
            request.ContentLength = info.Length;
            request.Timeout = 120000;
            request.ReadWriteTimeout = 120000;
            request.AllowAutoRedirect = false;
            request.Headers["X-Aetherfront-Report-Id"] = Bundle.ReportId;
            request.Headers["X-Aetherfront-Client-Version"] = ApfClientSettings.ClientVersion;
            if (!string.IsNullOrWhiteSpace(ApfClientSettings.DiagnosticsUploadToken))
                request.Headers["X-Aetherfront-Upload-Token"] = ApfClientSettings.DiagnosticsUploadToken;

            using (var input = new FileStream(Bundle.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = request.GetRequestStream())
                input.CopyTo(output);

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                EnsureHttpsResponse(response);
                using (var input = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    long total = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaximumReplyBytes) throw new InvalidDataException("Diagnostic endpoint reply is too large");
                        memory.Write(buffer, 0, read);
                    }
                    var reply = ApfJson.Deserialize<ApfDiagnosticUploadReply>(Encoding.UTF8.GetString(memory.ToArray()));
                    if (reply == null || string.IsNullOrWhiteSpace(reply.CaseId)) throw new InvalidDataException("Diagnostic endpoint did not return a case ID");
                    CaseId = reply.CaseId;
                }
            }
            Status = "Diagnostics submitted. Case ID: " + CaseId;
            State = ApfDiagnosticsState.Submitted;
            LunaLog.Log("[APF Diagnostics]: Submitted " + Bundle.ReportId + " as " + CaseId);
        }

        private static void EnsureHttpsResponse(HttpWebResponse response)
        {
            var code = (int)response.StatusCode;
            if (code >= 300 && code < 400) throw new InvalidDataException("Diagnostic endpoint returned a redirect");
            if (response.ResponseUri == null || response.ResponseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Diagnostic endpoint redirected away from HTTPS");
        }

        private static bool IsHttps(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
        }

        private static void Fail(string error)
        {
            Error = error;
            Status = "Diagnostic operation failed";
            State = ApfDiagnosticsState.Failed;
        }
    }
}
