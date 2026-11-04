using System;
using System.Collections.Generic;
using System.IO;
using Aetherfront.Core;

namespace UnityEngine
{
    internal static class Time { internal static float realtimeSinceStartup; }
    internal static class Debug
    {
        internal static readonly List<string> Messages = new List<string>();
        public static void LogError(object text) { Messages.Add("ERROR " + text); }
        public static void LogWarning(object text) { Messages.Add("WARNING " + text); }
    }
}
internal sealed class Game { internal enum Modes { SANDBOX, CAREER } internal Modes Mode; }
internal static class HighLogic { internal static Game CurrentGame; }
internal static class KSPUtil { internal static string ApplicationRootPath; }
namespace LmpClient { internal static class MainSystem { internal static string UniqueIdentifier; } }
namespace LmpClient.Apf { internal static class ApfClientSettings { internal static string ClientVersion; } }
namespace LmpClient.Aetherfront
{
    internal sealed class StubState { internal string ServerId; internal long Revision; }
    internal static class AetherfrontClientSystem
    {
        internal static StubState State;
        internal static int Snapshots;
        internal static void RequestSnapshot() { Snapshots++; }
    }
}
namespace ApfCore
{
    internal static class ApfPaths
    {
        internal static void EnsureNoReparsePoints(string root, string path)
        {
            MonoGate.RequirePrivate(path);
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Outbox escaped synthetic application root");
        }
    }
}
namespace KesasInstall
{
    internal static class Installer
    {
        internal static int Failures, Saves;
        internal static void AtomicText(string path, string text)
        {
            MonoGate.RequirePrivate(path);
            if (Failures > 0) { Failures--; throw new IOException("Injected synthetic disk failure"); }
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            Saves++;
        }
    }
}
namespace Aetherfront.Personal.Client
{
    internal static class PersonalClient
    {
        internal static bool Active;
        internal static readonly List<AetherfrontWireRequest> Sends = new List<AetherfrontWireRequest>();
        internal static Action<AetherfrontWireRequest> OnSend;
        internal static Exception ObserverError;
        internal static decimal CreditedScience(string source) { return 0; }
        internal static void SendScienceClaim(AetherfrontWireRequest request)
        {
            request.ProtocolVersion = 1; request.Operation = AetherfrontWireOperation.CommandRequest;
            Sends.Add(AetherfrontProtocol.DeserializeRequest(AetherfrontProtocol.Serialize(request)));
            try { if (OnSend != null) OnSend(request); } catch (Exception error) { ObserverError = error; }
        }
    }
}
