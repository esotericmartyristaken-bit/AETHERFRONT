using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using ApfCore;
using KesasInstall;

try {
    string work=Path.GetFullPath("Aetherfront-RC22-Work");
    string root=Path.Combine(work,"PublicTransferFixture-"+Guid.NewGuid().ToString("N").Substring(0,10));
    ZipFile.ExtractToDirectory(Path.GetFullPath("client-artifacts/Aetherfront-RC22/Aetherfront-Starter-rc22.zip"),root);
    Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
    File.Copy(Path.Combine(work,"GameStub/bin/Release/net472/GameStub.exe"),Path.Combine(root,"KSP_x64.exe"));
    var catalog=ApfJson.Deserialize<InstallCatalog>(File.ReadAllText(Path.Combine(root,"GameData/LunaMultiplayer/Launcher/install-catalog.json")));
    var package=catalog.Full.ManagedPackages.Single(p=>p.Id=="kesas-000-kspburst-de64ef39");
    string archive=Path.Combine(root,"APF/probe.zip");Directory.CreateDirectory(Path.GetDirectoryName(archive));
    string hosted=Path.GetFullPath(Path.Combine("KESAS-VPS-Server/Server/Config",Path.GetFileName(new Uri(package.ArchiveUrl).AbsolutePath)));
    // Reproduce the operator's exact RC19 cache layout without reading their
    // large files over RDP. Media bytes come from the immutable server archive.
    long prefix=205197312,badStart=221974528,excess=217088;
    using(var input=File.OpenRead(hosted)) {
        byte[] buffer=new byte[262144];
        void CopyRange(string path,long start,long length) {
            input.Position=start;using(var output=File.Create(path)) {
                while(length>0){int read=input.Read(buffer,0,(int)Math.Min(buffer.Length,length));if(read==0)throw new IOException("Fixture source truncated");output.Write(buffer,0,read);length-=read;}
                if(start==badStart)output.Write(new byte[excess],0,(int)excess);
            }
        }
        CopyRange(archive+".part",0,prefix);
        for(long start=prefix;start<package.SizeBytes;start+=ResumableTransfer.ChunkBytes){long end=Math.Min(package.SizeBytes-1,start+ResumableTransfer.ChunkBytes-1);CopyRange(archive+".part.range-"+start+"-"+end,start,end-start+1);}
    }
    using(var output=File.Create(archive+".part.assembled"))output.SetLength(package.SizeBytes+excess);
    var installer=new Installer(root,new IPEndPoint(IPAddress.Parse("217.216.49.250"),18865),"kesas-rc22");
    bool github=args.Contains("--github");
    if(github) {
        catalog.GitHub.Validate();
        if(!catalog.GitHub.Enabled || catalog.GitHub.Repository!="esotericmartyristaken-bit/AETHERFRONT")throw new InvalidDataException("Configured public repository required.");
        // Transport-only probe: seed the known local cache and test the real
        // GitHub range mirror. Full signed Connect flow is tested separately.
        typeof(Installer).GetField("github",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(installer,catalog.GitHub);
        typeof(Installer).GetField("githubReady",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(installer,true);
    }
    var timer=Stopwatch.StartNew();
    try {typeof(Installer).GetMethod("Download",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(installer,new object[]{new Uri(package.ArchiveUrl),archive,package.SizeBytes,package.Sha256});}
    catch(TargetInvocationException error){throw error.InnerException;}
    if(github && !(bool)typeof(Installer).GetField("githubReady",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(installer))throw new IOException("GitHub fallback occurred; this is not a successful GitHub probe.");
    if(new FileInfo(archive).Length!=package.SizeBytes || ApfJson.Sha256File(archive)!=package.Sha256)throw new IOException("Public transfer checksum mismatch");
    var meter=typeof(Installer).GetField("meter",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(installer);
    long received=(long)typeof(TransferMeter).GetField("bytes",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(meter);
    string[] rejected=Directory.GetFiles(Path.Combine(root,"APF"),"bad-*.cache");
    if(received!=ResumableTransfer.ChunkBytes||rejected.Length!=1||new FileInfo(rejected[0]).Length!=ResumableTransfer.ChunkBytes+excess)throw new IOException("Cache recovery did not isolate the one damaged range");
    Console.WriteLine("PASS exact user cache layout: 205197312-byte prefix, 25 ranges, one +217088-byte malformed range and oversized assembly. "+(github?"Real GitHub CDN (no origin fallback)":"Pinned HTTPS")+" fetched only "+received+" bytes; exact "+package.SizeBytes+"-byte catalog hash verified; malformed original quarantined. "+timer.Elapsed.TotalSeconds.ToString("0.0")+" seconds; transport/cache probe, not a full-install or end-user speed test.");
    if(Directory.GetFiles(Path.Combine(root,"APF"),"*.part*").Length!=0)throw new IOException("Temporary chunks remain");
    File.Delete(archive);
}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
