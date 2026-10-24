using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ApfCore;
using KesasInstall;

internal static class Program
{
    private static string Hash(string file)
    {
        using(var stream=File.OpenRead(file))using(var sha=SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==0)args=(Environment.GetEnvironmentVariable("AETHERFRONT_CATALOG_TEST_ARGS")??string.Empty).Split('|');
            if(args.Length!=4)throw new ArgumentException("catalog server-manifest output --valid|--expect-invalid");
            var core=typeof(ApfManifestValidator).Assembly.Location;
            if(Hash(core)!="0a4b6bd7c25ee4e18aba88ec8d41cb66b8c0034da3ba02d2d090ddb798bdc6c0")
                throw new InvalidDataException("Use the exact shipped ApfCore validator");
            var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(args[0]));
            var server=ApfPackManifest.FromJson(File.ReadAllText(args[1]));
            var errors=new[]{catalog.Full,catalog.Potato,server}.Select(ApfManifestValidator.Validate).ToArray();
            Console.WriteLine("Actual shipped validator: Regular="+errors[0].Errors.Count+", Potato="+errors[1].Errors.Count+", Server="+errors[2].Errors.Count);
            if(args[3]=="--expect-invalid")
            {
                if(errors[0].IsValid||errors[1].IsValid||!errors[2].IsValid||
                   errors.Take(2).Any(e=>e.Errors.Any(x=>!x.StartsWith("Client-only rule collides with a managed package: ",StringComparison.Ordinal))))
                    throw new InvalidDataException("Expected both shipped RC36 profiles to reproduce only the reported collisions");
                Console.WriteLine("PASS: reproduced RC36 catalog collisions with the shipped validator.");return 0;
            }
            if(args[3]!="--valid"||errors.Any(e=>!e.IsValid))throw new InvalidDataException(string.Join("; ",errors.SelectMany(e=>e.Errors)));
            foreach(var profile in new[]{catalog.Full,catalog.Potato,server})
            {
                if(profile.PackVersion!=catalog.Version||profile.RequiredClientVersion!=catalog.Version)
                    throw new InvalidDataException("Catalog/server version mismatch");
            }
            // Keep the security check: a real overlapping exception must still
            // fail, rather than fixing this by weakening manifest validation.
            var injected=ApfPackManifest.FromJson(catalog.Full.ToJson());
            injected.ClientOnly.Add(new ApfClientOnlyRule {Id="regression-overlap",PathRoots=new System.Collections.Generic.List<string>{"GameData/"+ApfPaths.PackageRoot(injected.ManagedPackages[0])}});
            if(ApfManifestValidator.Validate(injected).IsValid)throw new InvalidDataException("Collision protection was weakened");
            var result=new {version=catalog.Version,valid=true,catalogSha256=Hash(args[0]),serverManifestSha256=Hash(args[1]),validatorSha256=Hash(core),regularPackages=catalog.Full.ManagedPackages.Count,potatoPackages=catalog.Potato.ManagedPackages.Count,injectedCollisionRejected=true};
            File.WriteAllText(args[2],ApfJson.Serialize(result));
            Console.WriteLine("PASS: both install profiles and the server manifest validate; collision protection remains enforced.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
