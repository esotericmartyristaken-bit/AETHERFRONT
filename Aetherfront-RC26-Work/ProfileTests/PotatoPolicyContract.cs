using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApfCore;
using KesasInstall;

// Separate from the currently running, hash-pinned RC26 fixture. Link only after it exits.
internal sealed class PotatoPolicyContract
{
    internal const string PackageId="kesas-potato-policy";
    internal const string EntryPath="ZZZ_KESASPotato/DisableExhaust.cfg";
    public sealed class FileRecord
    {
        public string Path{get;set;}public string Sha256{get;set;}public long SizeBytes{get;set;}
    }
    public sealed class ChangeRecord
    {
        public string Path{get;set;}public string OldSha256{get;set;}public long OldSizeBytes{get;set;}
        public string Sha256{get;set;}public long SizeBytes{get;set;}
    }
    public sealed class Plan
    {
        public int SchemaVersion{get;set;}public string Version{get;set;}public string Profile{get;set;}
        public string SourceCatalogSha256{get;set;}public ApfPackage OldDeclaration{get;set;}public ApfPackage Declaration{get;set;}
        public FileRecord Archive{get;set;}public FileRecord Source{get;set;}public ChangeRecord ChangedEntry{get;set;}public FileRecord TestEvidence{get;set;}
    }
    public sealed class Evidence
    {
        public int SchemaVersion{get;set;}public string Version{get;set;}public string Profile{get;set;}public bool Passed{get;set;}
        public string SourceCatalogSha256{get;set;}public string OldArchiveSha256{get;set;}public string NewArchiveSha256{get;set;}public string SourceSha256{get;set;}
        public bool UnchangedOtherEntries{get;set;}public bool FullProfileUnchanged{get;set;}public bool FixturesOnly{get;set;}
    }
    internal sealed class Input
    {
        public string Path{get;set;}public string Sha256{get;set;}
    }
    private sealed class ZipItem
    {
        internal string Hash,Timestamp;internal long Length;internal int Attributes;internal bool Directory;
        internal bool Same(ZipItem other){return Hash==other.Hash&&Timestamp==other.Timestamp&&Length==other.Length&&Attributes==other.Attributes&&Directory==other.Directory;}
    }
    internal string PlanPath{get;private set;}internal string PlanSha256{get;private set;}
    internal string ArchivePath{get;private set;}internal string SourcePath{get;private set;}internal string EvidencePath{get;private set;}
    internal Plan DeclarationPlan{get;private set;}internal Evidence TestEvidence{get;private set;}
    internal ApfPackage Declaration{get{return DeclarationPlan.Declaration;}}
    internal Input[] Inputs{get;private set;}

    private static void Require(bool condition,string message){if(!condition)throw new InvalidDataException("Potato policy: "+message);}
    private static bool Hash(string value){return value!=null&&Regex.IsMatch(value,"^[0-9a-f]{64}$");}
    private static string Digest(Stream stream){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    private static byte[] ReadBytes(string path,string hash,long? length=null)
    {
        Require(Hash(hash),"missing or invalid reviewed SHA256.");
        byte[] bytes=File.ReadAllBytes(path);
        Require(!length.HasValue||bytes.LongLength==length.Value,"source/evidence size mismatch.");
        using(var stream=new MemoryStream(bytes,false))Require(Digest(stream)==hash,"reviewed source/evidence hash mismatch.");
        return bytes;
    }
    private static T ReadJson<T>(string path,string hash)
    {
        Require(new FileInfo(path).Length<=2*1024*1024,"oversized plan/evidence JSON.");
        using(var stream=new MemoryStream(ReadBytes(path,hash),false))
        using(var reader=new StreamReader(stream,new UTF8Encoding(false,true),true))return ApfJson.Deserialize<T>(reader.ReadToEnd());
    }
    private static string Child(string directory,string relative,string work)
    {
        Require(!string.IsNullOrEmpty(relative)&&relative.IndexOf('\\')<0&&ApfPaths.IsSafeRelative(relative),"unsafe evidence/source/archive relative path.");
        string path=System.IO.Path.GetFullPath(System.IO.Path.Combine(directory,relative));
        Require(ApfPaths.IsUnder(path,directory)&&path!=directory,"input escapes the explicitly reviewed stage.");
        ApfPaths.EnsureNoReparsePoints(work,path);return path;
    }
    private static Dictionary<string,ZipItem> ReadZip(string path,string hash,long size)
    {
        Require(Hash(hash)&&size>0,"invalid archive hash/size.");
        var result=new Dictionary<string,ZipItem>(StringComparer.Ordinal);
        var names=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);long expanded=0;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
            Require(held.Length==size&&Digest(held)==hash,"archive differs from the exact signed declaration.");held.Position=0;
            using(var zip=new ZipArchive(held,ZipArchiveMode.Read,true)){
                Require(zip.Entries.Count<=20000,"unexpected archive entry count.");
                foreach(var entry in zip.Entries){
                    bool directory=entry.FullName.EndsWith("/",StringComparison.Ordinal);
                    string name=directory?entry.FullName.Substring(0,entry.FullName.Length-1):entry.FullName;
                    Require(name.IndexOf('\\')<0&&ApfPaths.IsSafeRelative(name)&&!names.ContainsKey(name),"unsafe or duplicate ZIP entry.");
                    Require(((entry.ExternalAttributes>>16)&0xF000)!=0xA000&&(entry.ExternalAttributes&0x400)==0,"linked ZIP entry.");
                    expanded=checked(expanded+entry.Length);Require(expanded<=64*1024*1024&&(!directory||entry.Length==0),"unexpected expanded ZIP size.");
                    string entryHash;using(var input=entry.Open())entryHash=Digest(input);
                    names.Add(name,directory);result.Add(entry.FullName,new ZipItem{Hash=entryHash,Length=entry.Length,Timestamp=entry.LastWriteTime.ToString("o"),Attributes=entry.ExternalAttributes,Directory=directory});
                }
            }
        }
        foreach(string name in names.Keys){
            string parent=name;
            while(parent.Contains("/")){
                parent=parent.Substring(0,parent.LastIndexOf('/'));
                Require(!names.TryGetValue(parent,out bool directory)||directory,"ZIP file/directory collision.");
            }
        }
        return result;
    }
    internal static PotatoPolicyContract Load(string planPath,string planSha256,InstallCatalog baseline,string catalogSha256,string oldArchive,string work)
    {
        Require(System.IO.Path.IsPathRooted(planPath)&&Hash(planSha256),"explicit absolute -PotatoPolicyPlanPath and SHA256 required.");
        string modFixes=System.IO.Path.GetFullPath(System.IO.Path.Combine(work,"ModFixes"));
        string full=System.IO.Path.GetFullPath(planPath),directory=System.IO.Path.GetDirectoryName(full);
        Require(string.Equals(System.IO.Path.GetDirectoryName(directory),modFixes,StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(System.IO.Path.GetFileName(directory),"^Staged-RC26(?:-[A-Za-z0-9-]+)?$"),"plan must be in an immutable owned ModFixes/Staged-RC26 stage.");
        ApfPaths.EnsureNoReparsePoints(work,full);
        var plan=ReadJson<Plan>(full,planSha256);
        Require(plan!=null&&plan.SchemaVersion==1&&plan.Version=="kesas-rc26"&&plan.Profile=="potato"&&plan.SourceCatalogSha256==catalogSha256,"schema, revision, profile or signed source catalog mismatch.");
        Require(baseline.Potato.ManagedPackages.Count(p=>p.Id==PackageId)==1&&!baseline.Full.ManagedPackages.Any(p=>p.Id==PackageId),"baseline policy must occur exactly once in Potato, never Full.");
        var old=baseline.Potato.ManagedPackages.Single(p=>p.Id==PackageId);var package=plan.Declaration;
        Require(package!=null&&plan.OldDeclaration!=null&&ApfJson.Serialize(plan.OldDeclaration)==ApfJson.Serialize(old)&&package.Id==PackageId&&Hash(package.Sha256)&&package.Sha256!=old.Sha256&&package.Version!=old.Version&&Regex.IsMatch(package.Version??"","^[A-Za-z0-9._-]{1,64}$")&&package.SizeBytes>0&&package.SizeBytes<=1024*1024,"unexpected baseline/replacement declaration.");
        var expected=ApfJson.Deserialize<ApfPackage>(ApfJson.Serialize(old));
        expected.Version=package.Version;expected.ArchiveUrl=package.ArchiveUrl;expected.Sha256=package.Sha256;expected.SizeBytes=package.SizeBytes;
        Require(ApfJson.Serialize(expected)==ApfJson.Serialize(package),"replacement changed identity, destination, prefix or policy.");
        var oldUri=new Uri(old.ArchiveUrl);var uri=new Uri(package.ArchiveUrl);
        string filename=Uri.UnescapeDataString(System.IO.Path.GetFileName(uri.AbsolutePath));
        string origin=oldUri.GetLeftPart(UriPartial.Authority)+oldUri.AbsolutePath.Substring(0,oldUri.AbsolutePath.LastIndexOf('/')+1);
        Require(uri.IsAbsoluteUri&&uri.Scheme=="https"&&string.IsNullOrEmpty(uri.UserInfo)&&string.IsNullOrEmpty(uri.Query)&&string.IsNullOrEmpty(uri.Fragment)&&Regex.IsMatch(filename,"^kesas-rc26-[a-z0-9-]+-"+Regex.Escape(package.Sha256)+"\\.zip$")&&package.ArchiveUrl==origin+filename,"URL is not the exact signed-origin, full-SHA256 archive tuple.");
        Require(plan.Archive!=null&&plan.Source!=null&&plan.ChangedEntry!=null&&plan.TestEvidence!=null,"source, archive, changed-entry and test-evidence records required.");
        var change=plan.ChangedEntry;
        Require(plan.Archive.Path=="Media/"+filename&&plan.Archive.Sha256==package.Sha256&&plan.Archive.SizeBytes==package.SizeBytes,"archive record differs from the replacement declaration.");
        Require(Regex.IsMatch(plan.Source.Path??"","^Source/(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_.-]+\\.cfg$")&&change.Path==EntryPath&&Hash(change.OldSha256)&&Hash(change.Sha256)&&change.OldSha256!=change.Sha256&&change.OldSizeBytes>0&&change.SizeBytes>0&&change.SizeBytes<=65536&&plan.Source.Sha256==change.Sha256&&plan.Source.SizeBytes==change.SizeBytes,"only the reviewed existing DisableExhaust.cfg may change and source bytes must match it.");
        string archive=Child(directory,plan.Archive.Path,work),source=Child(directory,plan.Source.Path,work),evidencePath=Child(directory,plan.TestEvidence.Path,work);
        Require(new HashSet<string>(new[]{full,archive,source,evidencePath},StringComparer.OrdinalIgnoreCase).Count==4,"plan/source/archive/evidence paths must be distinct.");
        ReadBytes(source,plan.Source.Sha256,plan.Source.SizeBytes);
        var proof=ReadJson<Evidence>(evidencePath,plan.TestEvidence.Sha256);
        Require(proof!=null&&proof.SchemaVersion==1&&proof.Version=="kesas-rc26"&&proof.Profile=="potato"&&proof.Passed&&!proof.FixturesOnly&&proof.SourceCatalogSha256==catalogSha256&&proof.OldArchiveSha256==old.Sha256&&proof.NewArchiveSha256==package.Sha256&&proof.SourceSha256==plan.Source.Sha256&&proof.UnchangedOtherEntries&&proof.FullProfileUnchanged,"passing non-synthetic MM/policy evidence must bind exact old/new archive, source and unchanged Full.");
        var before=ReadZip(oldArchive,old.Sha256,old.SizeBytes);var after=ReadZip(archive,package.Sha256,package.SizeBytes);
        Require(before.ContainsKey(EntryPath)&&after.ContainsKey(EntryPath)&&new HashSet<string>(before.Keys,StringComparer.Ordinal).SetEquals(after.Keys),"archive entry addition/removal is forbidden.");
        foreach(var pair in before)if(pair.Key!=EntryPath)Require(pair.Value.Same(after[pair.Key]),"unchanged ZIP entry bytes or metadata changed.");
        Require(!before[EntryPath].Directory&&!after[EntryPath].Directory&&before[EntryPath].Hash==change.OldSha256&&before[EntryPath].Length==change.OldSizeBytes&&after[EntryPath].Hash==change.Sha256&&after[EntryPath].Length==change.SizeBytes,"actual changed-entry bytes do not match the explicit old/new proof.");
        return new PotatoPolicyContract{PlanPath=full,PlanSha256=planSha256,ArchivePath=archive,SourcePath=source,EvidencePath=evidencePath,DeclarationPlan=plan,TestEvidence=proof,Inputs=new[]{new Input{Path=full,Sha256=planSha256},new Input{Path=archive,Sha256=package.Sha256},new Input{Path=source,Sha256=plan.Source.Sha256},new Input{Path=evidencePath,Sha256=plan.TestEvidence.Sha256}}};
    }
    internal string ExactArchive(ApfPackage package)
    {
        Require(ApfJson.Serialize(package)==ApfJson.Serialize(Declaration),"catalog package differs from the reviewed policy declaration.");
        Require(new FileInfo(ArchivePath).Length==Declaration.SizeBytes&&ApfJson.Sha256File(ArchivePath)==Declaration.Sha256,"policy archive changed before preparation.");
        return ArchivePath;
    }
}
