using ApfCore;
using LmpClient.Network;
using LmpClient.Utilities;
using LmpClient.Windows.Apf;
using LmpCommon.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEngine;

namespace LmpClient.Apf
{
    public enum ApfClientUpdateState
    {
        Idle,
        Checking,
        Available,
        Downloading,
        Launching,
        Failed
    }

    public static class ApfClientUpdateCoordinator
    {
        private const long MaximumManifestBytes = 1024 * 1024;
        private const long MaximumArchiveBytes = 256L * 1024 * 1024;
        private static Task _task;
        private static bool _started;
        private static bool _quitRequested;

        public static ApfClientUpdateState State { get; private set; } = ApfClientUpdateState.Idle;
        public static ApfClientUpdateManifest Manifest { get; private set; }
        public static string Error { get; private set; } = string.Empty;
        public static string Status { get; private set; } = string.Empty;

        public static void BeginCheck()
        {
            if (_started || !ApfClientSettings.Enabled ||
                string.IsNullOrWhiteSpace(ApfClientSettings.ClientUpdateManifestUrl) ||
                string.IsNullOrWhiteSpace(ApfClientSettings.ClientUpdatePublicKeyXml)) return;
            _started = true;
            Error = string.Empty;
            Status = "Checking for APF client updates...";
            State = ApfClientUpdateState.Checking;
            _task = Task.Factory.StartNew(CheckForUpdate);
        }

        public static void Tick()
        {
            if (_task != null && _task.IsCompleted)
            {
                try { _task.GetAwaiter().GetResult(); }
                catch (Exception exception)
                {
                    Error = exception.GetBaseException().Message;
                    Status = "APF client update check failed";
                    State = ApfClientUpdateState.Failed;
                    LunaLog.LogError("[APF]: " + Error);
                }
                _task = null;
                if (State == ApfClientUpdateState.Available || State == ApfClientUpdateState.Failed)
                    ApfClientUpdateWindow.Singleton.Display = true;
            }

            if (State == ApfClientUpdateState.Launching && _quitRequested)
            {
                _quitRequested = false;
                Application.Quit();
            }
        }

        public static void Accept()
        {
            if (State != ApfClientUpdateState.Available || Manifest == null) return;
            State = ApfClientUpdateState.Downloading;
            Status = "Downloading APF client update...";
            Error = string.Empty;
            _task = Task.Factory.StartNew(DownloadAndStage);
        }

        public static void Cancel()
        {
            if (State == ApfClientUpdateState.Downloading || State == ApfClientUpdateState.Launching) return;
            var retryable = State == ApfClientUpdateState.Failed;
            Error = string.Empty;
            Status = string.Empty;
            State = ApfClientUpdateState.Idle;
            Manifest = null;
            if (retryable) _started = false;
            ApfClientUpdateWindow.Singleton.Display = false;
        }

        public static void Retry()
        {
            if (State != ApfClientUpdateState.Failed) return;
            Cancel();
            BeginCheck();
        }

        private static void CheckForUpdate()
        {
            if (!Uri.TryCreate(ApfClientSettings.ClientUpdateManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("APF client update manifest URL must use HTTPS");
            var manifest = ApfClientUpdateManifest.FromJson(DownloadText(uri));
            var validation = ApfClientUpdateValidator.Validate(manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));
            using (var publicKey = ApfClientUpdateSecurity.LoadXmlPublicKey(ApfClientSettings.ClientUpdatePublicKeyXml))
                if (!ApfClientUpdateSecurity.Verify(manifest, publicKey)) throw new InvalidDataException("APF client update signature verification failed");

            if (!string.IsNullOrWhiteSpace(manifest.KspVersion) &&
                !string.Equals(manifest.KspVersion, CompatibilityChecker.KspVersion.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("APF client update requires KSP " + manifest.KspVersion);

            if (ApfClientUpdateVersion.Compare(manifest.Version, ApfClientSettings.ClientVersion) <= 0)
            {
                Manifest = null;
                Status = "APF client is up to date";
                State = ApfClientUpdateState.Idle;
                return;
            }

            Manifest = manifest;
            Status = "APF client update available: " + manifest.Version;
            State = ApfClientUpdateState.Available;
        }

        private static void DownloadAndStage()
        {
            if (!Uri.TryCreate(Manifest.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("APF client update package URL must use HTTPS");

            var stageDirectory = Path.Combine(MainSystem.KspPath, "APF", ".client-update-staging", SafeName(Manifest.Version));
            ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, stageDirectory);
            Directory.CreateDirectory(stageDirectory);
            var archivePath = Path.Combine(stageDirectory, "APFClientUpdate.zip");
            ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, archivePath);
            DownloadArchive(uri, archivePath, Manifest.SizeBytes);
            ApfArchiveInstaller.ValidateClientUpdateArchive(archivePath, Manifest, MainSystem.KspPath);

            var helperPath = Path.Combine(MainSystem.KspPath, "GameData", "LunaMultiplayer", "APFUpdater.exe");
            if (!File.Exists(helperPath)) throw new FileNotFoundException("APFUpdater.exe is missing from GameData/LunaMultiplayer", helperPath);

            var transactionPath = Path.Combine(stageDirectory, "transaction.json");
            var transaction = new ApfClientUpdateTransaction
            {
                GameRoot = MainSystem.KspPath,
                GameDataPath = Path.Combine(MainSystem.KspPath, "GameData"),
                ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                KspExecutable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                KspArguments = Environment.GetCommandLineArgs().Skip(1).ToList(),
                ArchivePath = archivePath,
                Manifest = Manifest
            };
            ApfTransactionSecurity.Sign(transaction, ApfClientSettings.LoadOrCreateHelperKey());
            File.WriteAllText(transactionPath, ApfJson.Serialize(transaction));

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = MainSystem.KspPath,
                UseShellExecute = false,
                Arguments = ApfProcessArguments.Join(new[] { "--client-update", transactionPath })
            };
            System.Diagnostics.Process.Start(startInfo);
            Status = "APF client update staged; restarting KSP...";
            State = ApfClientUpdateState.Launching;
            _quitRequested = true;
        }

        private static string DownloadText(Uri uri)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            ApfPinnedHttps.Configure(request);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                EnsureResponse(response);
                if (response.ContentLength > MaximumManifestBytes) throw new InvalidDataException("APF client update manifest is too large");
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    CopyBounded(input, output, MaximumManifestBytes);
                    return System.Text.Encoding.UTF8.GetString(output.ToArray());
                }
            }
        }

        private static void DownloadArchive(Uri uri, string archivePath, long expectedSize)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            ApfPinnedHttps.Configure(request);
            request.Method = "GET";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                EnsureResponse(response);
                if (response.ContentLength > MaximumArchiveBytes || (expectedSize > 0 && response.ContentLength != expectedSize))
                    throw new InvalidDataException("APF client update archive size is invalid");
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    CopyBounded(input, output, MaximumArchiveBytes, expectedSize);
            }
        }

        private static void EnsureResponse(HttpWebResponse response)
        {
            var code = (int)response.StatusCode;
            if (code >= 300 && code < 400) throw new InvalidDataException("APF client update endpoint returned a redirect");
            if (response.ResponseUri == null || response.ResponseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("APF client update endpoint redirected away from HTTPS");
        }

        private static void CopyBounded(Stream input, Stream output, long maximum, long expectedSize = -1)
        {
            var buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maximum || (expectedSize >= 0 && total > expectedSize)) throw new InvalidDataException("APF client update download is too large");
                output.Write(buffer, 0, read);
            }
            if (expectedSize >= 0 && total != expectedSize) throw new InvalidDataException("APF client update download is truncated");
        }

        private static string SafeName(string value)
        {
            return new string((value ?? "update").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
        }
    }
}
