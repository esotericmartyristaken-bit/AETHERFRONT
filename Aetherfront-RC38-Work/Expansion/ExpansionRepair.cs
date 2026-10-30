using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// File-system-only core: runs before the stock expansion/database loaders.
internal static class ExpansionRepair
{
    internal static Action<string> TestCheckpoint = null;
    private sealed class Record { internal string Path, Hash; internal long Bytes; }
    private sealed class Pending { internal string Work, Journal, Phase; }
    private static readonly HashSet<string> Executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".ps1", ".bat", ".cmd", ".vbs", ".js", ".msi", ".com", ".scr" };
    private static void Require(bool good,string reason){if(!good)throw new InvalidDataException(reason);}
    private static void Name(string name){Require(name=="MakingHistory"||name=="Serenity","Unknown expansion");}
    private static bool SafeRelative(string relative)
    {
        if(String.IsNullOrWhiteSpace(relative)||relative.Contains('\\')||relative.Contains(':')||relative.StartsWith("/",StringComparison.Ordinal))return false;
        foreach(string part in relative.Split('/')){
            if(part.Length==0||part=="."||part==".."||part.TrimEnd(' ','.')!=part||part.Any(c=>c<32||"<>\"|?*".Contains(c)))return false;
            string stem=part.Split('.')[0].ToUpperInvariant();
            if(new[]{"CON","PRN","AUX","NUL"}.Contains(stem)||stem.Length==4&&(stem.StartsWith("COM")||stem.StartsWith("LPT"))&&stem[3]>='1'&&stem[3]<='9')return false;
        }
        return true;
    }
    internal static string Payload(string root,string name)
    {
        Name(name);return Under(root,"GameData/"+(name=="MakingHistory"?"AetherfrontMakingHistory":"AetherfrontBreakingGround")+"/PluginData/"+name+".zip");
    }
    private static string Under(string root,string relative)
    {
        Require(SafeRelative(relative),"Unsafe expansion path");
        root=Path.GetFullPath(root).TrimEnd('\\','/');
        string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        Require(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Expansion path escaped game");
        for(string cursor=path;cursor!=null;cursor=Path.GetDirectoryName(cursor))
            if(File.Exists(cursor)||Directory.Exists(cursor))Require((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)==0,"Expansion link refused");
        return path;
    }
    private static IEnumerable<string> Files(string root)
    {
        if(!Directory.Exists(root))yield break;
        foreach(string file in Directory.GetFileSystemEntries(root))
        {
            Require((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0,"Expansion tree link refused");
            if(Directory.Exists(file)){foreach(string child in Files(file))yield return child;}
            else yield return file;
        }
    }
    private static string Hash(Stream input){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
    private static string HashFile(string file){using(var input=File.OpenRead(file))return Hash(input);}
    private static void Atomic(string file,IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string temp=file+".new-"+Guid.NewGuid().ToString("N");
        byte[] bytes=new UTF8Encoding(false).GetBytes(String.Join("\n",lines)+"\n");
        using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
        if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
    }
    private static void DeleteTree(string root)
    {
        if(!Directory.Exists(root))return;
        Require((File.GetAttributes(root)&FileAttributes.ReparsePoint)==0,"Expansion cleanup root link refused");
        Files(root).ToArray(); // Validate every descendant before a recursive delete.
        Directory.Delete(root,true);
    }
    private static Pending ReadPending(string root,string name,string expected)
    {
        string journal=Under(root,"APF/ExpansionReceipts/"+name+".repair");
        if(!File.Exists(journal))return null;
        Require(new FileInfo(journal).Length<1024,"Oversized expansion repair journal");
        string[] rows=File.ReadAllLines(journal);Guid id;
        Require(rows.Length==4&&rows[0]=="APF-DLC-REPAIR-38"&&Guid.TryParseExact(rows[1],"N",out id)&&
            rows[2]==expected&&(rows[3]=="prepared"||rows[3]=="complete"),"Invalid expansion repair journal");
        return new Pending{Journal=journal,Work=Under(root,"APF/e/"+rows[1]),Phase=rows[3]};
    }
    private static void WritePending(Pending p,string expected,string phase)
    {
        string id=Path.GetFileName(p.Work).Substring(Path.GetFileName(p.Work).Length-32);
        Atomic(p.Journal,new[]{"APF-DLC-REPAIR-38",id,expected,phase});p.Phase=phase;
    }
    private static void Rollback(string root,string destination,Pending pending,Action<string> log)
    {
        if(pending==null||pending.Phase!="prepared")return;
        string old=Under(root,Relative(root,pending.Work)+"/o");
        string discarded=Under(root,Relative(root,pending.Work)+"/d");
        if(Directory.Exists(old))
        {
            Files(old).ToArray();Files(destination).ToArray();
            if(Directory.Exists(destination))
            {
                Require(!Directory.Exists(discarded),"Ambiguous interrupted expansion rollback; all copies preserved");
                Directory.Move(destination,discarded);
            }
            Directory.Move(old,destination);
        }
        DeleteTree(pending.Work);if(File.Exists(pending.Journal))File.Delete(pending.Journal);
        log("Recovered interrupted "+Path.GetFileName(destination)+" transaction before stock loading.");
    }
    private static string Relative(string root,string path)=>path.Substring(Path.GetFullPath(root).TrimEnd('\\','/').Length+1).Replace('\\','/');
    private static void Cleanup(Pending pending,Action<string> log)
    {
        if(pending==null)return;
        try {DeleteTree(pending.Work);File.Delete(pending.Journal);}
        catch(Exception e){log("Verified expansion is active; deferred recovery cleanup: "+e.Message);}
    }
    private static bool Fast(string root,string destination,string receipt,string expected,FileInfo source)
    {
        if(!File.Exists(receipt)||!Directory.Exists(destination)||new FileInfo(receipt).Length>1024*1024)return false;
        try {
            var rows=File.ReadAllLines(receipt);
            if(rows.Length<5||rows.Length>2004||rows[0]!="APF-DLC-RECEIPT-38"||rows[1]!=expected||
               rows[2]!=source.Length.ToString(CultureInfo.InvariantCulture)||rows[3]!=source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))return false;
            if(Files(destination).Count()!=rows.Length-4)return false;
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string row in rows.Skip(4)){
                var fields=row.Split('|');if(fields.Length!=3)return false;
                string relative=Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                if(!seen.Add(relative))return false;
                var file=new FileInfo(Under(root,Relative(root,destination)+"/"+relative));
                if(!file.Exists||fields[1]!=file.Length.ToString(CultureInfo.InvariantCulture)||fields[2]!=file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))return false;
            }
            return true;
        }catch{return false;}
    }
    private static List<Record> Records(ZipArchive zip,string name)
    {
        Require(zip.Entries.Count>0&&zip.Entries.Count<=2000,"Invalid expansion entry count");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var records=new List<Record>();long expanded=0;
        foreach(var entry in zip.Entries){
            string prefix=name+"/";
            Require(entry.FullName.StartsWith(prefix,StringComparison.Ordinal)&&!entry.FullName.Contains('\\')&&
                ((entry.ExternalAttributes>>16)&0xF000)!=0xA000&&(entry.ExternalAttributes&0x400)==0,"Unsafe expansion entry");
            string relative=entry.FullName.Substring(prefix.Length);
            Require(entry.Name.Length>0&&seen.Add(relative)&&!Executables.Contains(Path.GetExtension(relative))&&
                SafeRelative(relative),"Unsafe or duplicate expansion entry");
            Require(entry.Length>=0&&entry.Length<=1024L*1024*1024-expanded,"Expansion exceeds size bound");expanded+=entry.Length;
            using(var input=entry.Open())records.Add(new Record{Path=relative,Bytes=entry.Length,Hash=Hash(input)});
        }
        Require(seen.Contains(name=="MakingHistory"?"makinghistory.kspexpansion":"serenity.kspexpansion"),"Expansion metadata absent");
        return records;
    }
    private static bool Matches(string root,string destination,List<Record> records)
    {
        if(!Directory.Exists(destination)||Files(destination).Count()!=records.Count)return false;
        foreach(var record in records){
            string path=Under(root,Relative(root,destination)+"/"+record.Path);
            if(!File.Exists(path)||new FileInfo(path).Length!=record.Bytes||HashFile(path)!=record.Hash)return false;
        }
        return true;
    }
    private static void Receipt(string root,string destination,string receipt,string expected,FileInfo archive,List<Record> records)
    {
        var rows=new List<string>{"APF-DLC-RECEIPT-38",expected,archive.Length.ToString(CultureInfo.InvariantCulture),archive.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)};
        foreach(var record in records.OrderBy(r=>r.Path,StringComparer.Ordinal)){
            var file=new FileInfo(Under(root,Relative(root,destination)+"/"+record.Path));
            rows.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Path))+"|"+file.Length.ToString(CultureInfo.InvariantCulture)+"|"+file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        Atomic(receipt,rows);
    }
    internal static void Install(string root,string name,string expected,Action<string> log)
    {
        Name(name);Require(expected!=null&&expected.Length==64&&expected.All(x=>"0123456789abcdef".Contains(x)),"Invalid pinned expansion hash");
        root=Path.GetFullPath(root).TrimEnd('\\','/');
        string archive=Payload(root,name),destination=Under(root,"GameData/SquadExpansion/"+name),receipt=Under(root,"APF/ExpansionReceipts/"+name+".receipt38");
        if(!File.Exists(archive))throw new FileNotFoundException("Signed expansion payload is missing: "+name);
        string lockFile=Under(root,"APF/.expansion.lock");Directory.CreateDirectory(Path.GetDirectoryName(lockFile));
        using(var lease=new FileStream(lockFile,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            Pending pending=ReadPending(root,name,expected);
            if(pending!=null&&pending.Phase=="prepared"){Rollback(root,destination,pending,log);pending=null;}
            var source=new FileInfo(archive);
            if(Fast(root,destination,receipt,expected,source)){Cleanup(pending,log);TestCheckpoint?.Invoke("fast-receipt");log(name+" unchanged; cached receipt accepted.");return;}
            // Keep the verified source open without write/delete sharing through
            // extraction and activation; it cannot be swapped after hashing.
            using(var payload=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read)){
            Require(payload.Length<=512L*1024*1024&&Hash(payload)==expected,"Expansion payload fingerprint does not match signed client: "+name);
            source.Refresh();payload.Position=0;
            using(var zip=new ZipArchive(payload,ZipArchiveMode.Read,true)){
                var records=Records(zip,name);
                if(Matches(root,destination,records)){Receipt(root,destination,receipt,expected,source,records);Cleanup(pending,log);log(name+" existing assets verified.");return;}
                Cleanup(pending,log);
                string id=Guid.NewGuid().ToString("N");
                // Compact staging also avoids the shipped Mono's MAX_PATH limit.
                pending=new Pending{Work=Under(root,"APF/e/"+id),Journal=Under(root,"APF/ExpansionReceipts/"+name+".repair"),Phase="prepared"};
                string candidate=Under(root,Relative(root,pending.Work)+"/n"),old=Under(root,Relative(root,pending.Work)+"/o");
                Directory.CreateDirectory(candidate);
                try {
                    foreach(var record in records){
                        string file=Under(root,Relative(root,candidate)+"/"+record.Path);Directory.CreateDirectory(Path.GetDirectoryName(file));
                        using(var input=zip.GetEntry(name+"/"+record.Path).Open())using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
                    }
                    Require(Matches(root,candidate,records),"Incomplete staged expansion; existing DLC preserved");
                    WritePending(pending,expected,"prepared");TestCheckpoint?.Invoke("prepared");
                    bool replaced=Directory.Exists(destination);
                    if(replaced){Files(destination).ToArray();Directory.Move(destination,old);}
                    TestCheckpoint?.Invoke("old-moved");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));Directory.Move(candidate,destination);
                    TestCheckpoint?.Invoke("activated");
                    Receipt(root,destination,receipt,expected,source,records);
                    WritePending(pending,expected,"complete");TestCheckpoint?.Invoke("committed");
                    Cleanup(pending,log);
                    log(name+(replaced?" repaired atomically before stock loading.":" installed atomically before stock loading."));
                }catch(Exception error) {
                    try{if(pending.Phase=="prepared")Rollback(root,destination,pending,log);}
                    catch(Exception recovery){throw new AggregateException("Expansion repair and rollback failed; recovery copies retained",error,recovery);}
                    throw;
                }
            }
            }
        }
    }
}
