using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

var work=Path.GetFullPath("KESAS-RC16-Work");
var source=Path.Combine(work,"ModpackSource","GameData");
var output=Path.Combine(work,"HostedPackages");
if(Directory.Exists(output))throw new IOException("Package output already exists; preserve immutable artifacts.");
Directory.CreateDirectory(output);
var visual=new HashSet<string>(File.ReadAllLines(Path.Combine(work,"visual-roots.txt")).Where(s=>s.Length>0),StringComparer.OrdinalIgnoreCase);
var excluded=new HashSet<string>(new[]{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony","ModuleManager.ConfigCache","ModuleManager.ConfigSHA","ModuleManager.ConfigSHA.dmp","ModuleManager.Physics","ModuleManager.TechTree","toolbar-settings.dat","finalfrontier.dat","ScattererAtmosphereCache","LunaMultiplayer.zip"},StringComparer.OrdinalIgnoreCase);
var blocked=new HashSet<string>(new[]{".exe",".msi",".bat",".cmd",".com",".ps1",".psm1",".psd1",".vbs",".vbe",".js",".jse",".wsf",".wsh",".hta",".scr",".lnk",".url",".cpl",".reg",".sh"},StringComparer.OrdinalIgnoreCase);
var full=new List<object>();var potato=new List<object>();var sizes=new List<object>();var audit=new List<object>();
foreach(string root in Directory.GetFileSystemEntries(source).OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase))
{
    string name=Path.GetFileName(root);if(excluded.Contains(name))continue;
    if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("Source link: "+root);
    var files=(Directory.Exists(root)?Directory.GetFiles(root,"*",SearchOption.AllDirectories):new[]{root}).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).ToArray();
    var allowed=new List<string>();
    foreach(string file in files)
    {
        string relative=Path.GetRelativePath(source,file).Replace('\\','/');
        if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Source link: "+file);
        if(blocked.Contains(Path.GetExtension(file))){audit.Add(new{path=relative,action="excluded-nonruntime-script"});continue;}
        if(relative.Split('/').Any(s=>s=="."||s==".."||s.EndsWith('.')||s.EndsWith(' ')||s.Contains(':')))throw new IOException("Unsafe source path: "+relative);
        allowed.Add(file);
    }
    if(allowed.Count==0)continue;
    string slug=Regex.Replace(name.ToLowerInvariant(),"[^a-z0-9]+","-").Trim('-');
    string id="kesas-"+slug+"-"+Hash(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()))[..8];
    string temporary=Path.Combine(output,id+".building");long expanded=0;
    using(var archive=ZipFile.Open(temporary,ZipArchiveMode.Create))
    foreach(string file in allowed)
    {
        string relative=Path.GetRelativePath(source,file).Replace('\\','/');
        var entry=archive.CreateEntry(relative,CompressionLevel.Fastest);entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
        using var input=File.OpenRead(file);using var target=entry.Open();input.CopyTo(target);expanded=checked(expanded+input.Length);
    }
    string sha=HashFile(temporary);string filename=id+"-"+sha[..16]+".zip";string final=Path.Combine(output,filename);File.Move(temporary,final);
    long bytes=new FileInfo(final).Length;
    if(bytes>34359738368L || expanded>34359738368L || allowed.Count>100000)throw new IOException("Package exceeds bounded limits: "+name);
    var package=new{id,version="sha-"+sha[..16],archiveUrl="https://217.216.49.250:18866/"+filename,sha256=sha,sizeBytes=bytes,destination="GameData",stripPrefix=name};
    full.Add(package);if(!visual.Contains(name))potato.Add(package);sizes.Add(new{id,expandedBytes=expanded});
    audit.Add(new{path=name,profile=visual.Contains(name)?"full-only":"shared",files=allowed.Count,compressedBytes=bytes,expandedBytes=expanded,sha256=sha});
    Console.WriteLine(name+": "+allowed.Count+" files, "+(bytes/1048576)+" MiB"+(visual.Contains(name)?" [Full only]":" [Shared]"));
}
object Manifest(List<object> packages)=>new{schemaVersion=1,serverId="kesas-b6286df413b9426f",packId="kesas",packVersion="kesas-rc16",kspVersion="1.12.5",requiredClientVersion="kesas-rc16",sharedDeepFreeze=false,requiredDlc=Array.Empty<string>(),protectedRoots=new[]{"Squad","SquadExpansion","LunaMultiplayer","000_Harmony"},managedPackages=packages,clientOnly=excluded.Where(n=>!new[]{"Squad","SquadExpansion","LunaMultiplayer","LunaMultiplayer.zip","000_Harmony"}.Contains(n)).Select(n=>new{id="local-"+Regex.Replace(n,"[^a-zA-Z0-9]","-"),pathRoots=new[]{"GameData/"+n},dlls=Array.Empty<object>()})};
var catalog=new{version="kesas-rc16",full=Manifest(full),potato=Manifest(potato),sizes};
File.WriteAllText(Path.Combine(work,"install-catalog.json"),JsonSerializer.Serialize(catalog,new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllText(Path.Combine(work,"package-audit.json"),JsonSerializer.Serialize(audit,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Built "+full.Count+" Full packages; "+potato.Count+" shared Potato packages. All shared package records are identical.");
static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static string HashFile(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();}
