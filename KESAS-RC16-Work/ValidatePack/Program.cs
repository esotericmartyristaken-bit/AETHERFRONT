using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ApfCore;
using KesasLauncher;

var work=Path.GetFullPath("KESAS-RC16-Work");
var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(work,"install-catalog.json")));
var visual=new HashSet<string>(File.ReadAllLines(Path.Combine(work,"visual-roots.txt")),StringComparer.OrdinalIgnoreCase);
foreach(var manifest in new[]{catalog.Full,catalog.Potato}) {
    var validation=ApfManifestValidator.Validate(manifest);
    if(!validation.IsValid)throw new Exception(string.Join("; ",validation.Errors));
}
var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int count=0;
foreach(var package in catalog.Full.ManagedPackages.Concat(catalog.Potato.ManagedPackages).GroupBy(p=>p.Id).Select(g=>g.First()).Where(p=>!args.Contains("--profile-only") || p.Id.StartsWith("kesas-potato-",StringComparison.Ordinal)))
{
    string root=ApfPaths.PackageRoot(package);roots.Add(root);
    string archive=Path.Combine(work,"HostedPackages",Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath));
    if(package.SizeBytes>=3L*1024*1024*1024)throw new Exception("Split this package before publishing; ZIP64 is not accepted: "+root);
    ApfArchiveInstaller.ValidateArchive(archive,package,Path.Combine(work,"ArchiveValidation","GameData"));
    Console.WriteLine("Verified "+(++count)+": "+root);
}
foreach(var package in catalog.Potato.ManagedPackages)
{
    if(package.Id.EndsWith("-rules",StringComparison.Ordinal)) {
        string ruleRoot=ApfPaths.PackageRoot(package);
        using(var archive=System.IO.Compression.ZipFile.OpenRead(Path.Combine(work,"HostedPackages",Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath)))) {
            foreach(var entry in archive.Entries) {
                if(!entry.FullName.EndsWith(".cfg",StringComparison.OrdinalIgnoreCase) && entry.FullName.IndexOf("license",StringComparison.OrdinalIgnoreCase)<0 && entry.FullName.IndexOf("copying",StringComparison.OrdinalIgnoreCase)<0)throw new Exception("Rendering asset in configuration-only package: "+entry.FullName);
                using(var stream=entry.Open())using(var sha=System.Security.Cryptography.SHA256.Create())if(!string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""),ApfJson.Sha256File(Path.Combine(work,"ModpackSource/GameData",entry.FullName)),StringComparison.OrdinalIgnoreCase))throw new Exception("Engine configuration rules differ: "+entry.FullName);
            }
            foreach(string source in Directory.GetFiles(Path.Combine(work,"ModpackSource/GameData",ruleRoot),"*.cfg",SearchOption.AllDirectories)) {
                string relative=source.Substring(Path.Combine(work,"ModpackSource/GameData").Length+1).Replace('\\','/');
                if(!archive.Entries.Any(e=>e.FullName.Equals(relative,StringComparison.OrdinalIgnoreCase)))throw new Exception("Missing engine rule: "+relative);
            }
        }
    }
    if(package.Id=="kesas-potato-waterfall-api" || package.Id=="kesas-potato-policy" || package.Id=="kesas-potato-realplume-rules" || package.Id=="kesas-potato-realplume-stock-rules" || package.Id=="kesas-potato-stockwaterfalleffects-rules" || package.Id=="kesas-potato-waterfallrestock-rules" || package.Id=="kesas-potato-waterfallextensions-rules")continue;
    if(visual.Contains(ApfPaths.PackageRoot(package).Split('/')[0]))throw new Exception("Visual package in Potato: "+package.Id);
    var full=catalog.Full.ManagedPackages.Single(p=>p.Id==package.Id);
    if(ApfJson.Serialize(full)!=ApfJson.Serialize(package))throw new Exception("Shared package differs: "+package.Id);
}
var api=catalog.Potato.ManagedPackages.Single(p=>p.Id=="kesas-potato-waterfall-api");
using(var zip=System.IO.Compression.ZipFile.OpenRead(Path.Combine(work,"HostedPackages",Path.GetFileName(new Uri(api.ArchiveUrl).AbsolutePath)))) {
    if(zip.Entries.Any(e=>e.FullName.StartsWith("Waterfall/FX/") || e.FullName.StartsWith("Waterfall/Templates/") || e.FullName.StartsWith("Waterfall/Sounds/")))throw new Exception("Plume assets in Potato API package");
    using(var stream=zip.GetEntry("Waterfall/Plugins/Waterfall.dll").Open())using(var sha=System.Security.Cryptography.SHA256.Create()) {
        string actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
        if(!string.Equals(actual,ApfJson.Sha256File(Path.Combine(work,"ModpackSource/GameData/Waterfall/Plugins/Waterfall.dll")),StringComparison.OrdinalIgnoreCase))throw new Exception("Waterfall API binary changed");
    }
}
foreach(var required in new[]{"Kopernicus","KcalbelohSystem","KcalbelohTextures","OPM","WarpPlugin","ReStock","ReStockPlus","000_TexturesUnlimited"})
    if(!catalog.Potato.ManagedPackages.Any(p=>string.Equals(ApfPaths.PackageRoot(p),required,StringComparison.OrdinalIgnoreCase)))throw new Exception("Missing gameplay/content dependency: "+required);
Console.WriteLine("PASS "+count+" actual archives, both manifests, shared hashes, Potato visual exclusion and required content.");
File.WriteAllText(Path.Combine(work,"archives-verified.txt"),DateTime.UtcNow.ToString("O"));
