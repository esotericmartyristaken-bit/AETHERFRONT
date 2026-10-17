using ApfCore;
using LmpClient.Network;
using LmpClient.Utilities;
using LmpClient.Windows.Apf;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Handshake;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace LmpClient.Apf
{
    public enum ApfSyncState
    {
        Idle,
        Checking,
        Ready,
        Downloading,
        Clean,
        Failed,
        Launching
    }

    public static class ApfSyncCoordinator
    {
        private const long MaximumManifestBytes = 4 * 1024 * 1024;
        private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
        private static Task _task;
        private static Action _continueHandshake;
        private static ApfPackState _state;
        private static List<ApfStagedPackage> _stagedPackages;
        private static string _stageDirectory;
        private static volatile bool _quitRequested;
        private static string _serverId = string.Empty;
        private static string _packId = string.Empty;
        private static string _packVersion = string.Empty;
        private static string _manifestUrl = string.Empty;
        private static string _requiredClientVersion = string.Empty;
        private static bool _reportCleanToServer;
        private static bool _cleanReported;
        private static volatile bool _cancelRequested;

        public static ApfSyncState State { get; private set; } = ApfSyncState.Idle;
        public static ApfSyncPlan Plan { get; private set; }
        public static ApfPackManifest Manifest { get; private set; }
        public static string Error { get; private set; } = string.Empty;
        public static string Status { get; private set; } = string.Empty;

        public static void Begin(HandshakeReplyMsgData reply, Action continueHandshake)
        {
            Begin(reply.ApfServerId, reply.ApfPackId, reply.ApfPackVersion, reply.ApfManifestUrl, reply.ApfRequiredClientVersion, continueHandshake, false);
        }

        public static bool PrepareForManifestRequest()
        {
            if (!ApfClientSettings.Enabled)
            {
                FailPrecondition("This server requires the APF client marker");
                return false;
            }
            if (!ApfClientSettings.PrivateMasterConfigured)
            {
                FailPrecondition("APFMasterServer.txt is missing or invalid; APF mode will not use the public master");
                return false;
            }

            Error = string.Empty;
            Plan = null;
            Manifest = null;
            _cancelRequested = false;
            State = ApfSyncState.Checking;
            Status = "Requesting server pack manifest...";
            ApfSyncWindow.Singleton.Display = true;
            return true;
        }

        public static void BeginFromProtocol(string serverId, string packId, string packVersion, string manifestUrl, string requiredClientVersion)
        {
            Begin(serverId, packId, packVersion, manifestUrl, requiredClientVersion, null, true);
        }

        private static void Begin(string serverId, string packId, string packVersion, string manifestUrl, string requiredClientVersion,
            Action continueHandshake, bool reportCleanToServer)
        {
            if (!ApfClientSettings.Enabled)
            {
                FailPrecondition("This server requires the APF client marker");
                return;
            }
            if (!ApfClientSettings.PrivateMasterConfigured)
            {
                FailPrecondition("APFMasterServer.txt is missing or invalid; APF mode will not use the public master");
                return;
            }

            if (_task != null && !_task.IsCompleted) return;
            _serverId = serverId ?? string.Empty;
            _packId = packId ?? string.Empty;
            _packVersion = packVersion ?? string.Empty;
            _manifestUrl = manifestUrl ?? string.Empty;
            _requiredClientVersion = requiredClientVersion ?? string.Empty;
            _continueHandshake = continueHandshake;
            _reportCleanToServer = reportCleanToServer;
            _cleanReported = false;
            _cancelRequested = false;
            Error = string.Empty;
            Plan = null;
            Manifest = null;
            State = ApfSyncState.Checking;
            Status = "Downloading server pack manifest...";
            ApfSyncWindow.Singleton.Display = true;
            _task = Task.Factory.StartNew(CheckManifestAndGameData);
        }

        public static void Tick()
        {
            if (_task != null && _task.IsCompleted)
            {
                if (_cancelRequested)
                {
                    Plan = null;
                    Manifest = null;
                    Error = string.Empty;
                    Status = string.Empty;
                    State = ApfSyncState.Idle;
                }
                else
                {
                    try { _task.GetAwaiter().GetResult(); }
                    catch (Exception exception) { Error = exception.GetBaseException().Message; State = ApfSyncState.Failed; }
                }
                _task = null;
            }

            if (State == ApfSyncState.Clean && _continueHandshake != null)
            {
                var callback = _continueHandshake;
                _continueHandshake = null;
                State = ApfSyncState.Idle;
                ApfSyncWindow.Singleton.Display = false;
                callback();
            }

            if (State == ApfSyncState.Clean && _reportCleanToServer && !_cleanReported && Manifest != null)
            {
                _cleanReported = true;
                ApfProtocolSystem.SendStatusReport(Manifest, ApfPackStatus.Clean, "GameData validation passed");
                Status = "APF pack clean; waiting for server handshake...";
            }

            if (State == ApfSyncState.Launching && _quitRequested)
            {
                _quitRequested = false;
                Application.Quit();
            }
        }

        public static void Accept()
        {
            if (State != ApfSyncState.Ready || Plan == null) return;
            if (Manifest != null) ApfProtocolSystem.SendSyncAccepted(Manifest);
            State = ApfSyncState.Downloading;
            Status = "Downloading approved packages...";
            _stagedPackages = new List<ApfStagedPackage>();
            _stageDirectory = Path.Combine(MainSystem.KspPath, "APF", ".staging", SafeName(Manifest.PackId + "-" + Manifest.PackVersion));
            ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, _stageDirectory);
            Directory.CreateDirectory(_stageDirectory);
            _task = Task.Factory.StartNew(DownloadPackages);
        }

        public static void Cancel()
        {
            if (State == ApfSyncState.Downloading || State == ApfSyncState.Launching) return;
            _cancelRequested = true;
            if (ApfClientSettings.Enabled && MainSystem.NetworkState >= ClientState.Connected)
                NetworkConnection.Disconnect("APF pack synchronization cancelled");
            State = ApfSyncState.Idle;
            Plan = null;
            Error = string.Empty;
            _reportCleanToServer = false;
            _cleanReported = false;
            ApfSyncWindow.Singleton.Display = false;
        }

        /// <summary>
        /// Re-run the current server-pack check after a recoverable failure.
        /// This is deliberately user initiated: a failed download or helper
        /// validation must never create an automatic restart loop.
        /// </summary>
        public static void Retry()
        {
            if (State != ApfSyncState.Failed || (_task != null && !_task.IsCompleted)) return;
            if (!ApfClientSettings.Enabled)
            {
                FailPrecondition("This server requires the APF client marker");
                return;
            }
            if (!ApfClientSettings.PrivateMasterConfigured)
            {
                FailPrecondition("APFMasterServer.txt is missing or invalid; APF mode will not use the public master");
                return;
            }

            _cancelRequested = false;
            _cleanReported = false;
            Error = string.Empty;
            Plan = null;
            Manifest = null;
            State = ApfSyncState.Checking;
            Status = "Retrying APF pack validation...";
            ApfSyncWindow.Singleton.Display = true;
            _task = Task.Factory.StartNew(CheckManifestAndGameData);
        }

        private static void CheckManifestAndGameData()
        {
            if (!Uri.TryCreate(_manifestUrl, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("APF server did not provide an HTTPS manifest URL");

            var manifestJson = DownloadText(manifestUri);
            if (!string.IsNullOrWhiteSpace(_requiredClientVersion) &&
                !string.Equals(_requiredClientVersion, ApfClientSettings.ClientVersion, StringComparison.Ordinal))
                throw new InvalidDataException("This APF server requires a different client version");
            Manifest = ApfPackManifest.FromJson(manifestJson);
            var validation = ApfManifestValidator.Validate(Manifest);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));
            if (!string.Equals(Manifest.ServerId, _serverId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Manifest.PackId, _packId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Manifest.PackVersion, _packVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Server manifest metadata does not match handshake metadata");
            if (!string.IsNullOrWhiteSpace(Manifest.RequiredClientVersion) &&
                !string.Equals(Manifest.RequiredClientVersion, ApfClientSettings.ClientVersion, StringComparison.Ordinal))
                throw new InvalidDataException("This APF pack requires a different client version");

            if (!string.IsNullOrWhiteSpace(Manifest.KspVersion) &&
                !string.Equals(Manifest.KspVersion, CompatibilityChecker.KspVersion.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This APF pack requires KSP " + Manifest.KspVersion + "; detected KSP " + CompatibilityChecker.KspVersion);

            var gameDataPath = Path.Combine(MainSystem.KspPath, "GameData");
            ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, MainSystem.KspPath);
            ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, gameDataPath);
            EnsureWritable(MainSystem.KspPath, "KSP clone");
            EnsureWritable(gameDataPath, "GameData");
            foreach (var dlc in Manifest.RequiredDlc ?? new List<string>())
            {
                var relative = ApfPaths.NormalizeGameDataRelative(dlc).Replace('/', Path.DirectorySeparatorChar);
                if (!Directory.Exists(Path.Combine(gameDataPath, relative)))
                    throw new InvalidDataException("Required DLC is missing: " + dlc);
            }

            _state = ApfClientSettings.LoadState();
            Plan = ApfPackScanner.Scan(gameDataPath, Manifest, _state);
            if (Plan.Errors.Count > 0) throw new InvalidDataException(string.Join("; ", Plan.Errors));
            State = Plan.IsClean ? ApfSyncState.Clean : ApfSyncState.Ready;
            Status = Plan.IsClean ? "APF pack is clean" : "APF pack changes require approval";
        }

        private static void DownloadPackages()
        {
            var failures = new List<string>();
            ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, _stageDirectory);
            foreach (var change in Plan.Install)
            {
                try
                {
                    if (!Uri.TryCreate(change.Package.ArchiveUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException("Package URL is not HTTPS");
                    var archivePath = Path.Combine(_stageDirectory, SafeName(change.Package.Id) + ".zip");
                    ApfPaths.EnsureNoReparsePoints(MainSystem.KspPath, archivePath);
                    DownloadArchive(uri, archivePath, change.Package.SizeBytes);
                    ApfArchiveInstaller.ValidateArchive(archivePath, change.Package, Path.Combine(MainSystem.KspPath, "GameData"));
                    _stagedPackages.Add(new ApfStagedPackage { Package = change.Package, ArchivePath = archivePath });
                }
                catch (Exception exception)
                {
                    try { var failedPath = Path.Combine(_stageDirectory, SafeName(change.Package.Id) + ".zip"); if (File.Exists(failedPath)) File.Delete(failedPath); } catch { }
                    failures.Add(change.Package.Id + ": " + exception.Message);
                }
            }

            if (failures.Count > 0) Error = "Some packages failed: " + string.Join(" | ", failures);
            if (_stagedPackages.Count == 0 && Plan.Install.Count > 0)
            {
                State = ApfSyncState.Failed;
                return;
            }

            var helperPath = Path.Combine(MainSystem.KspPath, "GameData", "LunaMultiplayer", "APFUpdater.exe");
            if (!File.Exists(helperPath))
            {
                Error = "APFUpdater.exe is missing from GameData/LunaMultiplayer";
                State = ApfSyncState.Failed;
                return;
            }

            try
            {
                var transactionPath = Path.Combine(_stageDirectory, "transaction.json");
                var transaction = new ApfSyncTransaction
                {
                    GameRoot = MainSystem.KspPath,
                    GameDataPath = Path.Combine(MainSystem.KspPath, "GameData"),
                    ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                    KspExecutable = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                    KspArguments = Environment.GetCommandLineArgs().Skip(1).ToList(),
                    StatePath = ApfClientSettings.StatePath,
                    Manifest = Manifest,
                    ExistingState = _state,
                    Packages = _stagedPackages,
                    Deletes = Plan.Delete
                };
                ApfTransactionSecurity.Sign(transaction, ApfClientSettings.LoadOrCreateHelperKey());
                File.WriteAllText(transactionPath, ApfJson.Serialize(transaction));

                var reconnect = new ApfReconnectInfo
                {
                    Address = NetworkConnection.LastConnectAddress,
                    Port = NetworkConnection.LastConnectPort,
                    Password = NetworkConnection.LastConnectPassword
                };
                if (!string.IsNullOrWhiteSpace(reconnect.Address) && reconnect.Port > 0)
                    File.WriteAllText(ApfClientSettings.ReconnectPath, ApfJson.Serialize(reconnect));

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = helperPath,
                    WorkingDirectory = MainSystem.KspPath,
                    UseShellExecute = false,
                    Arguments = ApfProcessArguments.Join(new[] { "--transaction", transactionPath })
                };
                System.Diagnostics.Process.Start(startInfo);
                State = ApfSyncState.Launching;
                Status = "APF sync staged; restarting KSP...";
                _quitRequested = true;
            }
            catch (Exception exception)
            {
                Error = exception.Message;
                State = ApfSyncState.Failed;
            }
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
                EnsureNoRedirect(response);
                EnsureHttps(response.ResponseUri);
                if (response.ContentLength > MaximumManifestBytes) throw new InvalidDataException("Manifest is too large");
                using (var stream = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaximumManifestBytes) throw new InvalidDataException("Manifest is too large");
                        memory.Write(buffer, 0, read);
                    }
                    return System.Text.Encoding.UTF8.GetString(memory.ToArray());
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
                EnsureNoRedirect(response);
                EnsureHttps(response.ResponseUri);
                if (response.ContentLength > MaximumArchiveBytes || (expectedSize > 0 && response.ContentLength > expectedSize))
                    throw new InvalidDataException("Package archive is larger than its manifest limit");
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    long total = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaximumArchiveBytes || (expectedSize > 0 && total > expectedSize))
                            throw new InvalidDataException("Package archive is larger than its manifest limit");
                        output.Write(buffer, 0, read);
                    }
                    if (expectedSize > 0 && total != expectedSize)
                        throw new InvalidDataException("Package archive size does not match manifest");
                }
            }
        }

        private static void EnsureHttps(Uri uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("APF download redirected away from HTTPS");
        }

        private static void EnsureNoRedirect(HttpWebResponse response)
        {
            var code = (int)response.StatusCode;
            if (code >= 300 && code < 400)
                throw new InvalidDataException("APF download returned a redirect; use the final HTTPS URL");
        }

        private static void EnsureWritable(string path, string label)
        {
            if (!Directory.Exists(path)) throw new InvalidDataException(label + " directory does not exist: " + path);
            var probe = Path.Combine(path, ".apf-write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception exception)
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                throw new InvalidDataException(label + " is not writable; use a writable cloned KSP install", exception);
            }
        }

        private static void FailPrecondition(string error)
        {
            Error = error;
            State = ApfSyncState.Failed;
            ApfSyncWindow.Singleton.Display = true;
            if (MainSystem.NetworkState >= ClientState.Connected)
                NetworkConnection.Disconnect(error);
        }

        private static string SafeName(string value)
        {
            var original = value ?? "pack";
            var readable = new string(original.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            if (string.IsNullOrWhiteSpace(readable)) readable = "pack";
            if (readable.Length > 48) readable = readable.Substring(0, 48);

            // Sanitization alone is not injective (for example, "a:b" and
            // "a?b" both become "a_b"). Keep the path readable while making
            // every logical package/pack name map to a distinct staging path.
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(original));
                return readable + "-" + ApfJson.ToHex(digest).Substring(0, 16);
            }
        }
    }
}
