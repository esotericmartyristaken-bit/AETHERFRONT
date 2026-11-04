using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Aetherfront.Core;

internal static class MonoGate
{
    internal static readonly string Root = Path.GetFullPath(Environment.GetEnvironmentVariable("AF_SCIENCE_MONO_ROOT") ?? throw new InvalidOperationException("Private fixture root required"));
    internal static string CaseRoot;
    internal static int Assertions;
    internal static readonly List<CaseResult> Results = new List<CaseResult>();
    internal sealed class CaseResult { public string Name { get; set; } public bool Passed { get; set; } public int Assertions { get; set; } public string Error { get; set; } }
    internal static void Check(bool condition, string text) { Assertions++; if (!condition) throw new InvalidOperationException(text); }
    internal static void Equal<T>(T expected, T actual, string text) { Check(EqualityComparer<T>.Default.Equals(expected, actual), text + "; expected=" + expected + "; actual=" + actual); }
    internal static void Case(string phase, string name, Action test, Action reset = null)
    {
        CaseRoot = Path.Combine(Root, phase + "-fixtures", Results.Count.ToString("D2") + "-" + name.Substring(0, Math.Min(20, name.Length))); RequirePrivate(CaseRoot); Directory.CreateDirectory(CaseRoot);
        int before = Assertions;
        try { if (reset != null) reset(); test(); Results.Add(new CaseResult { Name = name, Passed = true, Assertions = Assertions - before }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Results.Add(new CaseResult { Name = name, Passed = false, Assertions = Assertions - before, Error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    }
    internal static int Finish(string phase)
    {
        int passed = 0; foreach (var r in Results) if (r.Passed) passed++;
        Type mono = Type.GetType("Mono.Runtime"); Check(mono != null, "Actual embedded Mono runtime required");
        var version = mono.GetMethod("GetDisplayName", BindingFlags.Static | BindingFlags.NonPublic);
        string runtime = version == null ? "Mono.Runtime" : (string)version.Invoke(null, null);
        Console.WriteLine("MONO " + runtime);
        string path = Path.Combine(Root, phase + "-results.json"); RequirePrivate(path);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location)).Select(a => new { name = a.GetName().Name, path = a.Location, sha256 = Hash(a.Location) }).ToArray();
        File.WriteAllText(path, AetherfrontJson.Serialize(new { phase = phase, passed = passed, failed = Results.Count - passed, assertions = Assertions, runtime = runtime, cases = Results, loadedAssemblies = assemblies, sockets = 0, unityNativeCalls = 0 }));
        Console.WriteLine("RESULT " + phase + " passed=" + passed + " failed=" + (Results.Count - passed) + " assertions=" + Assertions);
        return passed == Results.Count ? 0 : 1;
    }
    private static string Hash(string path)
    {
        using (var h = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    internal static void RequirePrivate(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path outside private Mono fixture root: " + full);
        for (string p = Path.GetDirectoryName(full); p != null && p.StartsWith(Root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private fixture reparse point forbidden");
    }
}
