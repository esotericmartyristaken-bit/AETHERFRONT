using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("ORIGINAL PATCHED SERVER_DEPENDENCIES");
int passed = 0;
foreach (var binary in args.Take(2))
{
    var context = new AssemblyLoadContext("fixture-" + Guid.NewGuid(), true);
    context.Resolving += (_, name) => {
        string path = Path.Combine(Path.GetFullPath(args[2]), name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    };
    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(binary));
    Type proof = assembly.GetType("LmpCommon.IdentityProof", true)!, storeType = assembly.GetType("Server.System.IdentityBindingStore", true)!;
    var authorize = storeType.GetMethod("AuthorizeAndPersist")!;
    using var key = RSA.Create(2048); using var otherKey = RSA.Create(2048);
    string Public(RSA rsa) => (string)proof.GetMethod("ExportPublicKey")!.Invoke(null, [rsa])!;
    string Id(string pub) => (string)proof.GetMethod("AccountId")!.Invoke(null, [pub])!;
    string pub = Public(key), otherPub = Public(otherKey), id = Id(pub), otherId = Id(otherPub);
    string root = Path.Combine(Path.GetTempPath(), "Aetherfront-IdentityFixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "Universe")); Directory.CreateDirectory(Path.Combine(root, "Config"));
    object store = Activator.CreateInstance(storeType, Path.Combine(root, "Universe"), Path.Combine(root, "Config"))!;
    string binding = Path.Combine(root, "Universe", "IdentityBindings.json");
    string oldReason = "Identity account or player name belongs to another key; ask the server owner about account enrollment";
    bool patched = binary == args[1];
    void Check(string name, string publicKey, string accountId, string playerName, bool legacy, bool expected, string reason)
    {
        byte[]? before = File.Exists(binding) ? File.ReadAllBytes(binding) : null;
        object?[] parameters = [publicKey, accountId, playerName, new Func<string, bool>(_ => legacy), null];
        bool actual = (bool)authorize.Invoke(store, parameters)!;
        if (actual != expected || (string)parameters[4]! != (expected ? "" : patched ? reason : oldReason)) throw new Exception(name + ": outcome or reason differs");
        if (!expected && before is not null && !before.SequenceEqual(File.ReadAllBytes(binding))) throw new Exception(name + ": rejection mutated identity bindings");
        passed++; Console.WriteLine("PASS " + (patched ? "patched " : "baseline ") + name);
    }
    Check("fresh key claims name", pub, id, "FixtureOwner", false, true, "");
    Check("existing key reconnects", pub, id, "FixtureOwner", true, true, "");
    Check("other key cannot take name", otherPub, otherId, "fixtureowner", false, false, "Name already in use. Please choose another player name.");
    Check("wrong key cannot take account", otherPub, id, "AnotherName", false, false, "Account key changed. Restore your saved key or contact the server owner.");
    Check("blank account rejected", pub, "", "AnotherName", false, false, "Account details are invalid. Please update your client.");
    Check("whitespace account rejected", pub, " " + id, "AnotherName", false, false, "Account details are invalid. Please update your client.");
    Check("legacy reservation protected", otherPub, otherId, "LegacyName", true, false, "Please choose another player name, or contact the server owner.");
    Check("unenrolled account protected", otherPub, "legacy-account", "LegacyName", false, false, "Please choose another player name, or contact the server owner.");
    File.WriteAllText(Path.Combine(root, "Config", "IdentityEnrollments.json"), JsonSerializer.Serialize(new { version = 1, accounts = new[] { new { accountId = "legacy-account", publicKey = otherPub, playerNames = new[] { "EnrolledName" } } } }));
    Check("explicit operator enrollment retained", otherPub, "legacy-account", "EnrolledName", true, true, "");
    Check("one key may register another free name", pub, id, "FixtureAlias", false, true, "");
    // Temporary fixtures contain generated keys, never production identities.
    Directory.Delete(root, true); context.Unload();
}
Console.WriteLine($"RESULT passed={passed} failed=0; original/patched acceptance identical");
