using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static readonly string[] Hashes = {
        "4b57bce899c7ab814165cc9d46737122bb953bfc6f2bd353a03013458dc22bf3",
        "b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3",
        "b1508b4051942377168a4f1bb18708145c1054441ddb5b5b7c3ca213ee3fa778",
        "99288eb5872e7a9608dc830295eb353b15db9b5fa26b33ceab94846201e329fa"
    };
    public static int Main(string[] args)
    {
        if (args.Length != 5) { Console.Error.WriteLine("ORIGINAL_CLIENT PATCHED_CLIENT ORIGINAL_SERVER PATCHED_SERVER SUMMARY.json"); return 2; }
        var results = new List<object>(); int passed = 0, failed = 0, assertions = 0;
        for (int i = 0; i < 4; i++)
        {
            string path = Path.GetFullPath(args[i]);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (hash != Hashes[i]) { Console.Error.WriteLine("Unreviewed input: " + path); return 1; }
            var context = new AssemblyLoadContext("rc42-" + i, true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(path);
                var run = ReflectionSuite.Run(assembly, (i & 1) != 0, false);
                results.Add(new { path, hash, patched = (i & 1) != 0, run });
                passed += run.Passed; failed += run.Failed; assertions += run.Assertions;
            }
            catch (Exception error) { failed++; Console.Error.WriteLine(error); }
            finally { context.Unload(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        }
        string output = Path.GetFullPath(args[4]);
        foreach (string input in args[..4]) if (string.Equals(Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Summary cannot overwrite input");
        if (!output.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Summary extension must be .json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed, failed, assertions, sockets = 0, sequential = true, targets = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SUMMARY " + passed + "/" + (passed + failed) + " cases; " + assertions + " assertions; sockets=0");
        return failed == 0 ? 0 : 1;
    }
}
