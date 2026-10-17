using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KesasInstall
{
    // Active transfer time only: archive verification and installation are not
    // network samples. One shared meter measures all four connections together.
    public sealed class TransferMeter
    {
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Queue<Tuple<double,long>> samples = new Queue<Tuple<double,long>>();
        private long bytes;
        private double lastSample, speed, estimate = -1;
        public void Reset() { lock(this) { clock.Reset(); samples.Clear(); bytes=0; lastSample=0; speed=0; estimate=-1; } }
        public void Start() { lock(this) clock.Start(); }
        public void Pause() { lock(this) clock.Stop(); }
        public void Add(int count) { lock(this) bytes+=count; }
        public void Read(long remaining, out double rate, out double seconds)
        {
            lock(this) ReadAt(clock.Elapsed.TotalSeconds, bytes, remaining, out rate, out seconds);
        }
        // Explicit monotonic time also permits deterministic estimator tests.
        public void ReadAt(double now, long received, long remaining, out double rate, out double seconds)
        {
            lock(this)
            {
                if(samples.Count==0) samples.Enqueue(Tuple.Create(now,received));
                if(now-lastSample>=1) {
                    lastSample=now;
                    samples.Enqueue(Tuple.Create(now,received));
                    while(samples.Count>2 && now-samples.Peek().Item1>20) samples.Dequeue();
                    var first=samples.Peek(); double elapsed=now-first.Item1;
                    if(elapsed>=5 && received-first.Item2>=131072) {
                        double measured=(received-first.Item2)/elapsed;
                        speed=speed==0?measured:speed*0.8+measured*0.2;
                        double next=remaining/Math.Max(1,speed);
                        estimate=estimate<0?next:estimate*0.8+next*0.2;
                    }
                }
                rate=speed; seconds=remaining<=0?0:estimate;
            }
        }
    }

    // Only the caller supplies trusted, same-origin requests. Fixed-size chunk
    // files survive retries; the RC17 contiguous .part prefix is retained too.
    public sealed class ResumableTransfer
    {
        public const int Connections=4;
        public const long ChunkBytes=8L*1024*1024;
        public Func<Uri,HttpWebRequest> CreateRequest;
        public Func<bool> IsCanceled=()=>false;
        public Action<long> Progress=delegate {};
        public Action<int> Received=delegate {};
        public Action<string> ValidatePath=delegate {};
        private readonly object gate=new object();
        private readonly HashSet<HttpWebRequest> requests=new HashSet<HttpWebRequest>();
        private bool failed;
        private long downloaded;
        private sealed class RangeUnsupported : IOException { }
        private sealed class Chunk { public long Start,End,Length; public string Path; }
        public void Cancel() { lock(gate) foreach(var request in requests.ToArray()) request.Abort(); }
        private void Check() { if(IsCanceled())throw new OperationCanceledException("Download canceled. Installed files were kept."); if(Volatile.Read(ref failed))throw new IOException("Another download stream failed; retry to resume."); }
        private HttpWebRequest Request(Uri uri) { Check();var request=CreateRequest(uri);lock(gate){Check();requests.Add(request);}return request; }
        private void Forget(HttpWebRequest request) { lock(gate) requests.Remove(request); }
        public string Download(Uri uri,string part,long size,bool parallel)
        {
            Check(); ValidatePath(part);
            long prefix=File.Exists(part)?new FileInfo(part).Length:0;
            if(prefix>size)throw new InvalidDataException("Partial download exceeds its declared size.");
            if(prefix==size){Progress(size);return part;}
            if(!parallel || size-prefix<2*ChunkBytes) { Single(uri,part,size,prefix);return part; }
            var chunks=new List<Chunk>();
            for(long start=prefix;start<size;start+=ChunkBytes) {
                long end=Math.Min(size-1,start+ChunkBytes-1);
                string path=part+".range-"+start+"-"+end;ValidatePath(path);
                long length=File.Exists(path)?new FileInfo(path).Length:0;
                if(length>end-start+1)throw new InvalidDataException("Partial range exceeds its declared size.");
                chunks.Add(new Chunk{Start=start,End=end,Length=length,Path=path});
            }
            downloaded=prefix+chunks.Sum(c=>c.Length);Progress(downloaded);
            int next=-1;Exception cause=null;
            var workers=Enumerable.Range(0,Math.Min(Connections,chunks.Count)).Select(_=>Task.Run(()=>{
                try {
                    int index;while((index=Interlocked.Increment(ref next))<chunks.Count) {
                        Check();var chunk=chunks[index];
                        for(int attempt=0;;attempt++) {
                            try{ReadChunk(uri,chunk,size);break;}
                            catch(Exception error) when((error is WebException || error is IOException) && !(error is InvalidDataException) && !(error is RangeUnsupported) && attempt<2 && !IsCanceled() && !Volatile.Read(ref failed)) { }
                        }
                    }
                }catch(Exception error){lock(gate){if(cause==null){cause=error;Volatile.Write(ref failed,true);}}Cancel();}
            })).ToArray();
            Task.WaitAll(workers); // Every worker exception is observed before merge/retry.
            if(IsCanceled())throw new OperationCanceledException("Download canceled. Installed files were kept.");
            if(cause is RangeUnsupported) { Volatile.Write(ref failed,false);Single(uri,part,size,prefix);return part; }
            if(cause!=null)throw cause;
            Check();string assembled=part+".assembled";ValidatePath(assembled);
            using(var output=new FileStream(assembled,FileMode.Create,FileAccess.Write,FileShare.None,262144)) {
                var buffer=new byte[262144];
                if(prefix>0)Copy(part,output,buffer);
                foreach(var chunk in chunks){Check();Copy(chunk.Path,output,buffer);}
                if(output.Length!=size)throw new IOException("Assembled download has an invalid size.");
                output.Flush(true);
            }
            // Keep chunks and the RC17 prefix until the caller verifies the final
            // archive hash. No loaded mod or identity file is changed here.
            return assembled;
        }
        private void Copy(string path,Stream output,byte[] buffer) { ValidatePath(path);using(var input=File.OpenRead(path)){int read;while((read=input.Read(buffer,0,buffer.Length))>0){Check();output.Write(buffer,0,read);}} }
        private void ReadChunk(Uri uri,Chunk chunk,long total)
        {
            ValidatePath(chunk.Path);long length=File.Exists(chunk.Path)?new FileInfo(chunk.Path).Length:0;
            if(length==chunk.End-chunk.Start+1)return;
            long start=chunk.Start+length;var request=Request(uri);request.AddRange(start,chunk.End);
            try { using(var response=(HttpWebResponse)request.GetResponse()) {
                if(response.StatusCode==HttpStatusCode.OK)throw new RangeUnsupported();
                string expected="bytes "+start.ToString(CultureInfo.InvariantCulture)+"-"+chunk.End.ToString(CultureInfo.InvariantCulture)+"/"+total.ToString(CultureInfo.InvariantCulture);
                if(response.StatusCode!=HttpStatusCode.PartialContent || response.Headers["Content-Range"]!=expected || response.ContentLength!=chunk.End-start+1)throw new InvalidDataException("Invalid download range response.");
                using(var input=response.GetResponseStream())using(var output=new FileStream(chunk.Path,FileMode.Append,FileAccess.Write,FileShare.None,262144)) {
                    byte[] buffer=new byte[262144];int read;long remaining=chunk.End-start+1;
                    while((read=input.Read(buffer,0,(int)Math.Min(buffer.Length,remaining+1)))>0) {
                        Check();if(read>remaining)throw new InvalidDataException("Download range exceeded its declared size.");
                        output.Write(buffer,0,read);remaining-=read;Received(read);Progress(Interlocked.Add(ref downloaded,read));
                    }
                    if(remaining!=0)throw new IOException("Download ended early; retry to resume.");
                }
            }}finally{Forget(request);}
        }
        private void Single(Uri uri,string part,long size,long prefix)
        {
            var request=Request(uri);if(prefix>0)request.AddRange(prefix);
            try {using(var response=(HttpWebResponse)request.GetResponse()) {
                if(response.StatusCode==HttpStatusCode.OK)prefix=0;
                else if(response.StatusCode!=HttpStatusCode.PartialContent || prefix==0 || response.Headers["Content-Range"]!="bytes "+prefix+"-"+(size-1)+"/"+size)throw new InvalidDataException("Invalid download range response.");
                if(response.ContentLength!=size-prefix)throw new InvalidDataException("Download size differs from its declared size.");
                using(var input=response.GetResponseStream())using(var output=new FileStream(part,prefix>0?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,262144)) {
                    var buffer=new byte[262144];long count=prefix;Progress(count);int read;
                    while((read=input.Read(buffer,0,buffer.Length))>0) { Check();count=checked(count+read);if(count>size)throw new InvalidDataException("Download exceeded its declared size.");output.Write(buffer,0,read);Received(read);Progress(count); }
                    if(count!=size)throw new IOException("Download ended early; retry to resume.");
                }
            }}finally{Forget(request);}
        }
        public void Cleanup(string part,long size)
        {
            // Exact hash-derived paths only. No recursive/glob deletion.
            long prefix=File.Exists(part)?new FileInfo(part).Length:0;
            if(prefix<size)for(long start=prefix;start<size;start+=ChunkBytes){string path=part+".range-"+start+"-"+Math.Min(size-1,start+ChunkBytes-1);ValidatePath(path);if(File.Exists(path))File.Delete(path);}
            foreach(string path in new[]{part,part+".assembled"}){ValidatePath(path);if(File.Exists(path))File.Delete(path);}
        }
    }
}
