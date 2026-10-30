using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

internal static class Program
{
    static int checks;static string output;static readonly Action<string> Log=s=>Console.WriteLine(s);
    static void Check(bool good,string why){checks++;if(!good)throw new Exception(why);}
    static string Hash(string file){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-","").ToLowerInvariant();}
    static string At(string root,string relative)=>Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
    static void Put(string root,string path,string data){string file=At(root,path);Directory.CreateDirectory(Path.GetDirectoryName(file));File.WriteAllText(file,data);}
    static string Fixture(string label,string name="MakingHistory",bool existing=true,string extra=null)
    {
        string root=Path.Combine(output,label);Directory.CreateDirectory(root);
        string archive=ExpansionRepair.Payload(root,name);Directory.CreateDirectory(Path.GetDirectoryName(archive));
        using(var z=ZipFile.Open(archive,ZipArchiveMode.Create)){
            using(var s=new StreamWriter(z.CreateEntry(name+"/"+(name=="MakingHistory"?"makinghistory.kspexpansion":"serenity.kspexpansion")).Open()))s.Write("metadata");
            using(var s=new StreamWriter(z.CreateEntry(name+"/Parts/part.cfg").Open()))s.Write("CANONICAL");
            if(extra!=null)using(var s=new StreamWriter(z.CreateEntry(name+"/"+extra).Open()))s.Write("bad");
        }
        if(existing){Put(root,"GameData/SquadExpansion/"+name+"/Parts/part.cfg","OLD-STATE");Put(root,"GameData/SquadExpansion/"+name+"/obsolete.txt","old");}
        Put(root,"saves/preserve.txt","SAVE-CANARY");Put(root,"APF/Identity/preserve.txt","IDENTITY-CANARY");return root;
    }
    static void Install(string root,string name="MakingHistory")=>ExpansionRepair.Install(root,name,Hash(ExpansionRepair.Payload(root,name)),Log);
    static void Verified(string root,string name="MakingHistory")
    {
        Check(File.ReadAllText(At(root,"GameData/SquadExpansion/"+name+"/Parts/part.cfg"))=="CANONICAL","canonical asset");
        Check(!File.Exists(At(root,"GameData/SquadExpansion/"+name+"/obsolete.txt")),"obsolete DLC asset removed automatically");
        Check(File.ReadAllText(At(root,"saves/preserve.txt"))=="SAVE-CANARY","save preserved");
        Check(File.ReadAllText(At(root,"APF/Identity/preserve.txt"))=="IDENTITY-CANARY","identity preserved");
        Check(!File.Exists(At(root,"APF/ExpansionReceipts/"+name+".repair")),"transaction completed without extra launch");
        Check(Directory.GetDirectories(At(root,"APF/e")).Length==0,"temporary DLC backups removed after verified commit");
    }
    static void Copy(string source,string target)
    {
        Directory.CreateDirectory(target);
        foreach(string file in Directory.GetFiles(source)){if(Path.GetFileName(file)==".expansion.lock")continue;File.Copy(file,Path.Combine(target,Path.GetFileName(file)));}
        foreach(string dir in Directory.GetDirectories(source))Copy(dir,Path.Combine(target,Path.GetFileName(dir)));
    }
    static void Main()
    {
        output=Environment.GetEnvironmentVariable("APF_EXPANSION_TEST_OUTPUT");if(String.IsNullOrEmpty(output)||Directory.Exists(output))throw new Exception("New explicit fixture output required");Directory.CreateDirectory(output);
        foreach(string name in new[]{"MakingHistory","Serenity"})foreach(bool existing in new[]{false,true}){
            string root=Fixture(name+"-"+existing,name,existing);Install(root,name);Verified(root,name);
            long ticks=File.GetLastWriteTimeUtc(At(root,"GameData/SquadExpansion/"+name+"/Parts/part.cfg")).Ticks;bool fast=false;
            ExpansionRepair.TestCheckpoint=s=>{if(s=="fast-receipt")fast=true;};Install(root,name);ExpansionRepair.TestCheckpoint=null;
            Check(fast,"unchanged installation uses receipt; no archive content rehash/extraction");
            Check(ticks==File.GetLastWriteTimeUtc(At(root,"GameData/SquadExpansion/"+name+"/Parts/part.cfg")).Ticks,"unchanged file not rewritten");
            Put(root,"GameData/SquadExpansion/"+name+"/Parts/part.cfg","BROKENxxx");Install(root,name);Verified(root,name);
        }
        foreach(string phase in new[]{"prepared","old-moved","activated","committed"}){
            string root=Fixture("interrupt-"+phase),snapshot=Path.Combine(output,"snapshot-"+phase);bool reached=false;
            ExpansionRepair.TestCheckpoint=s=>{if(s==phase){reached=true;Copy(root,snapshot);throw new IOException("simulated interruption after snapshot");}};
            try{Install(root);}catch(IOException){}finally{ExpansionRepair.TestCheckpoint=null;}
            Check(reached,"interruption checkpoint "+phase+" reached");
            if(phase!="committed")Check(File.ReadAllText(At(root,"GameData/SquadExpansion/MakingHistory/Parts/part.cfg"))=="OLD-STATE","ordinary failure restores original DLC");
            Install(snapshot);Verified(snapshot); // Exact crash-state snapshot, recovered in this first invocation.
            Install(root);Verified(root);
        }
        foreach(string invalid in new[]{"../escape.txt","nested/../../escape.txt","bad.dll","CON","name.","name ","a:b","a\\b"}){
            string root=Fixture("unsafe-"+checks,extra:invalid);bool rejected=false;try{Install(root);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"malformed signed archive rejected: "+invalid);
            Check(File.ReadAllText(At(root,"GameData/SquadExpansion/MakingHistory/Parts/part.cfg"))=="OLD-STATE","unsafe archive leaves existing DLC intact");
        }
        {
            string root=Fixture("bad-hash");bool rejected=false;
            try{ExpansionRepair.Install(root,"MakingHistory",new string('0',64),Log);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"wrong signed payload hash rejected");Check(File.ReadAllText(At(root,"GameData/SquadExpansion/MakingHistory/Parts/part.cfg"))=="OLD-STATE","untrusted payload cannot replace original");
        }
        {
            string root=Fixture("source-lock");bool writeDenied=false,deleteDenied=false;
            ExpansionRepair.TestCheckpoint=s=>{if(s=="prepared"){
                string archive=ExpansionRepair.Payload(root,"MakingHistory");
                try{using(var stream=new FileStream(archive,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){} }catch(IOException){writeDenied=true;}
                try{File.Delete(archive);}catch(IOException){deleteDenied=true;}
            }};
            try{Install(root);}finally{ExpansionRepair.TestCheckpoint=null;}
            Check(writeDenied,"verified archive cannot be rewritten during activation");
            Check(deleteDenied,"verified archive cannot be replaced after signature hash check");Verified(root);
        }
        {
            string root=Fixture("concurrent");Put(root,"APF/.expansion.lock","");bool rejected=false;
            using(var lease=new FileStream(At(root,"APF/.expansion.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))try{Install(root);}catch(IOException){rejected=true;}
            Check(rejected,"concurrent preparation refused");
        }
        Console.WriteLine("PASS "+checks+" expansion repair assertions: first-call install/repair, both DLCs, crash snapshots, atomic rollback, safe paths, signed hashes, cache reuse and preservation.");
    }
}
