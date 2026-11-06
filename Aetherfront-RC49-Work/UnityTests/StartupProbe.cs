using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

// The candidate assembly is installed normally in an independent minimal KSP.
// KSP's own addon discovery creates ScienceEvents; this probe never creates it.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class RecoveryStartupProbe : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private string root;
    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("AF_RECOVERY_STARTUP_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        root = Path.GetFullPath(root);
        string game = Path.GetFullPath(KSPUtil.ApplicationRootPath);
        if (!game.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        DontDestroyOnLoad(gameObject); Application.runInBackground = true; StartCoroutine(Run());
    }
    private IEnumerator Run()
    {
        Type type = null;
        for (int i = 0; i < 240; i++)
        {
            yield return null;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AetherfrontPersonalClient");
            if (assembly != null) type = assembly.GetType("Aetherfront.Personal.Client.ScienceEvents");
            if (type != null)
            {
                var instances = UnityEngine.Object.FindObjectsOfType(type);
                var ready = type.GetField("subjectHookInstalled", All);
                if (instances.Length == 1 && ready != null && (bool)ready.GetValue(ready.IsStatic ? null : instances[0])) break;
            }
        }
        bool passed = false; string failure = null; int count = 0;
        try
        {
            if (type == null) throw new Exception("Candidate science collector assembly not loaded");
            count = UnityEngine.Object.FindObjectsOfType(type).Length;
            if (count != 1) throw new Exception("KSP did not automatically start exactly one collector: " + count);
            foreach (string flag in new[] { "subjectHookInstalled", "historyHookInstalled", "awardHookInstalled", "submissionHookInstalled", "recoveryHookInstalled" })
                if (!(bool)type.GetField(flag, All).GetValue(null)) throw new Exception("Native KSP method hook missing: " + flag);
            type.GetMethod("EnsureStarted").Invoke(null, null);
            type.GetMethod("EnsureStarted").Invoke(null, null);
            if (UnityEngine.Object.FindObjectsOfType(type).Length != 1) throw new Exception("Mirror fallback duplicated collector");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); Debug.LogError("RECOVERY STARTUP PROBE " + error); }
        string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/RecoveryProbe/Plugins/AetherfrontPersonalClient.dll");
        string hash = BitConverter.ToString(SHA256.Create().ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        string json = "{\"passed\":" + (passed ? "true" : "false") + ",\"automaticKspAddonDiscovery\":true,\"collectorCount\":" + count +
            ",\"nativeKspHooks\":" + (passed ? "true" : "false") + ",\"candidateSha256\":\"" + hash + "\",\"unity\":\"" + Application.unityVersion +
            "\",\"scope\":\"Actual candidate DLL with real KSP addon discovery and native Unity lifecycle; dependency addons disabled in private fixture; no server connection or flight recovery\"}";
        File.WriteAllText(Path.Combine(root, "startup-result.json"), json);
        if (failure != null) File.WriteAllText(Path.Combine(root, "startup-error.txt"), failure);
        Application.Quit();
    }
}
