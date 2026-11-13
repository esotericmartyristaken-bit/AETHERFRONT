using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Aetherfront.Core;
using Server.Aetherfront;

namespace Aetherfront.Synchronization.Server
{
    // The primary snapshot and appended frame are already durable before Compact
    // runs. Validate historical recovery images once at bootstrap, then retain
    // the last eight known committed frames instead of rebuilding 64 object
    // graphs while holding the gameplay/store transaction locks.
    public static class RecoveryJournal
    {
        sealed class Cache
        {
            internal bool Initialized;
            internal readonly SortedDictionary<long,string> Frames = new SortedDictionary<long,string>();
        }
        sealed class Header
        {
            public int FormatVersion { get; set; }
            public bool Committed { get; set; }
            public long CommitSequence { get; set; }
            public long Revision { get; set; }
            public string StateJson { get; set; }
        }
        static readonly ConditionalWeakTable<AetherfrontServerStore,Cache> Caches = new ConditionalWeakTable<AetherfrontServerStore,Cache>();
        static readonly FieldInfo PathField=Field("_eventPath"), SequenceField=Field("_commitSequence"), StateField=Field("_state"), CounterField=Field("_journalAppendsSinceCompaction");
        static readonly MethodInfo Validate=typeof(AetherfrontServerStore).GetMethod("DeserializeAndValidate",BindingFlags.NonPublic|BindingFlags.Static)
            ?? throw new MissingMethodException("Recovery state validator unavailable.");
        public static long BootstrapValidations, Compactions;
        static FieldInfo Field(string name) => typeof(AetherfrontServerStore).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(AetherfrontServerStore).FullName,name);
        static bool Sequence(string json,long maximum,out long sequence)
        {
            sequence=0;
            try
            {
                var fields=JsonSlices.Object(json);
                return fields.TryGetValue("FormatVersion",out var version)&&version=="2"&&fields.TryGetValue("Committed",out var committed)&&committed=="true"&&
                    fields.TryGetValue("CommitSequence",out var number)&&long.TryParse(number,NumberStyles.None,CultureInfo.InvariantCulture,out sequence)&&sequence>0&&sequence<=maximum;
            }
            catch(InvalidDataException){return false;}
        }
        public static void Initialize(AetherfrontServerStore store)
        {
            var cache=Caches.GetValue(store,_=>new Cache());
            lock(cache)
            {
                if(cache.Initialized)return;
                long began=SyncTimings.Start();
                string path=(string)PathField.GetValue(store);
                long maximum=(long)SequenceField.GetValue(store);
                var candidates=new SortedDictionary<long,string>();
                long candidateBytes=0;
                if(File.Exists(path))
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                using(var reader=new StreamReader(stream,Encoding.UTF8))
                {
                    string line;
                    while((line=reader.ReadLine())!=null)
                    {
                        if(string.IsNullOrWhiteSpace(line)||!Sequence(line,maximum,out var number))continue;
                        if(candidates.TryGetValue(number,out var previous))candidateBytes-=previous.Length*2L;
                        candidates[number]=line;candidateBytes+=line.Length*2L;
                        while(candidates.Count>16||candidateBytes>256L*1024*1024)
                        {var first=candidates.First();candidateBytes-=first.Value.Length*2L;candidates.Remove(first.Key);}
                    }
                }
                string serverId=((AetherfrontState)StateField.GetValue(store)).ServerId;
                foreach(var item in candidates.Reverse())
                {
                    try
                    {
                        var header=AetherfrontJson.Deserialize<Header>(item.Value);
                        Interlocked.Increment(ref BootstrapValidations);
                        var state=(AetherfrontState)Validate.Invoke(null,new object[]{header.StateJson,serverId});
                        if(header.CommitSequence==item.Key&&state.Revision==header.Revision)cache.Frames[item.Key]=item.Value;
                        if(cache.Frames.Count==8)break;
                    }
                    catch(TargetInvocationException error) when((error.InnerException is InvalidDataException||!(error.InnerException is IOException))&&!(error.InnerException is UnauthorizedAccessException)&&!(error.InnerException is OutOfMemoryException)){}
                    catch(Exception error) when((error is InvalidDataException||!(error is IOException))&&!(error is UnauthorizedAccessException)&&!(error is TargetInvocationException)&&!(error is OutOfMemoryException)){}
                }
                // Never compact away older recovery evidence when the bounded
                // bootstrap window cannot establish the expected valid history.
                if(candidates.Count>=8&&cache.Frames.Count<8)global::Server.Log.LunaLog.Warning("Recovery history contains invalid recent frames; primary remains authoritative and existing journal is retained until eight trusted frames accumulate.");
                cache.Initialized=true;
                SyncTimings.Complete("journal.bootstrap.validate",began);
            }
        }
        public static void Compact(AetherfrontServerStore store,string currentFrame)
        {
            Initialize(store);
            var cache=Caches.GetValue(store,_=>new Cache());
            lock(cache)
            {
                long sequence=(long)SequenceField.GetValue(store);
                if(!Sequence(currentFrame,sequence,out var declared)||declared!=sequence)throw new InvalidDataException("Current recovery frame identity differs from committed store.");
                cache.Frames[sequence]=currentFrame;
                while(cache.Frames.Count>8)cache.Frames.Remove(cache.Frames.First().Key);
                if(cache.Frames.Count<8)return;
                string path=(string)PathField.GetValue(store);
                int bytes=Encoding.UTF8.GetByteCount(currentFrame);
                if((int)CounterField.GetValue(store)<64&&new FileInfo(path).Length<Math.Max(67108864L,32L*bytes))return;
                if((File.GetAttributes(path)&(FileAttributes.ReparsePoint|FileAttributes.Directory|FileAttributes.ReadOnly))!=0)
                    throw new InvalidDataException("Recovery journal must remain a writable regular file.");
                string temporary=path+".compact."+Guid.NewGuid().ToString("N")+".tmp";
                long began=SyncTimings.Start();
                try
                {
                    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    using(var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true))
                    {
                        foreach(var frame in cache.Frames.Values)writer.WriteLine(frame);
                        writer.Flush();SyncTimings.Flush(stream,true);
                    }
                    File.Move(temporary,path,true);
                    CounterField.SetValue(store,0);
                    Interlocked.Increment(ref Compactions);
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);SyncTimings.Complete("journal.compact.cached",began);}
            }
        }
    }
}
