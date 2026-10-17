using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KesasInstall;

internal static class Program
{
    private static readonly byte[] payload=new byte[40*1024*1024+113];
    private static readonly string root=Path.GetFullPath("Aetherfront-RC20-Work/TransferFixtures/"+Guid.NewGuid().ToString("N"));
    private static int checks;
    private static void Check(bool value,string description){if(!value)throw new Exception(description);checks++;Console.WriteLine("PASS "+description);}
    private static string Hash(string path){using(var sha=SHA256.Create())using(var input=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(input));}
    private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes));}
    private static int Main()
    {
        try{Directory.CreateDirectory(root);for(int i=0;i<payload.Length;i++)payload[i]=(byte)((i*31L+i/1024)%251);Run();Console.WriteLine("All "+checks+" transfer/ETA checks passed.");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Run()
    {
        Directory.CreateDirectory(Path.Combine(root,"GameData/Squad"));
        string data=Path.Combine(root,"GameData/LunaMultiplayer/Data");Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(root,"KSP_x64.exe"),"Fixture only; never launched");
        File.WriteAllText(Path.Combine(data,"APFTlsCertificatePins.txt"),new string('0',64));
        var installer=new Installer(root,new IPEndPoint(IPAddress.Loopback,18865),"kesas-rc20");
        var factory=typeof(Installer).GetMethod("CreateArchiveRequest",BindingFlags.Instance|BindingFlags.NonPublic);
        var request=(HttpWebRequest)factory.Invoke(installer,new object[]{new Uri("https://127.0.0.1:18866/test.zip")});
        Check(request.ServicePoint.ConnectionLimit==4 && request.ServerCertificateValidationCallback!=null && !request.AllowAutoRedirect,"Actual archive request factory initializes without unsupported APIs and retains TLS pinning/no redirects");
        var meter=new TransferMeter();double speed,eta;
        meter.ReadAt(0,0,100000000,out speed,out eta);meter.ReadAt(4,4194304,95805696,out speed,out eta);
        Check(speed==0&&eta<0,"ETA stays hidden during warm-up");
        meter.ReadAt(5,5242880,94757120,out speed,out eta);Check(Math.Abs(speed-1048576)<1&&eta>0,"Estimator measures actual transfer bytes against monotonic active time");
        double previous=eta;meter.ReadAt(6,5242880+10485760,84271360,out speed,out eta);
        Check(Math.Abs(eta-previous)/previous<0.15,"A one-second speed burst does not swing ETA wildly");
        double held=eta;meter.ReadAt(6.2,20000000,80000000,out speed,out eta);Check(eta==held,"ETA refreshes at most once per second");
        meter.Reset();meter.ReadAt(0,0,100,out speed,out eta);Check(eta<0&&speed==0,"New installation resets prior speed history");
        FileLockChecks();
        using(var server=new Server()) {
            string part=Path.Combine(root,"prefix.part");long prefix=2*1024*1024;
            using(var stream=File.Create(part))stream.Write(payload,0,(int)prefix);
            string cached=part+".range-"+prefix+"-"+(prefix+ResumableTransfer.ChunkBytes-1);
            using(var stream=File.Create(cached))stream.Write(payload,(int)prefix,1024*1024);
            var transfer=New(server);string downloaded=transfer.Download(server.Url,part,payload.Length,true);
            Check(Hash(downloaded)==Hash(payload),"Four ranged streams assemble the exact archive");
            Check(server.Peak>=2&&server.Peak<=4,"Parallelism remains bounded at four connections");
            Check(!server.Starts.Any(start=>start<prefix+1024*1024)&&server.BytesSent==payload.Length-prefix-1024*1024,"RC17 prefix and interrupted chunk bytes are reused, not redownloaded");
            transfer.Cleanup(part,payload.Length);Check(!Directory.GetFiles(root,"prefix.part*").Any(),"Only completed transfer temporary files are removed");
        }
        using(var server=new Server{InterruptOnce=true}) {
            string part=Path.Combine(root,"retry.part");var transfer=New(server);string downloaded=transfer.Download(server.Url,part,payload.Length,true);
            Check(Hash(downloaded)==Hash(payload)&&server.Starts.Any(start=>start%ResumableTransfer.ChunkBytes!=0),"Interrupted range retries resume from durable chunk length");
            transfer.Cleanup(part,payload.Length);
        }
        using(var server=new Server{WrongRange=true}) {
            bool rejected=false;try{New(server).Download(server.Url,Path.Combine(root,"bad.part"),payload.Length,true);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Incorrect Content-Range is rejected before merging");
        }
        using(var server=new Server{IgnoreRange=true}) {
            string part=Path.Combine(root,"fallback.part");var transfer=New(server);string downloaded=transfer.Download(server.Url,part,payload.Length,true);
            Check(Hash(downloaded)==Hash(payload),"Servers ignoring ranges fall back to one validated stream");transfer.Cleanup(part,payload.Length);
        }
        using(var server=new Server{Slow=true}) {
            string part=Path.Combine(root,"cancel.part");bool canceled=false;var transfer=New(server);transfer.IsCanceled=()=>Volatile.Read(ref canceled);
            transfer.Received=count=>{if(count>0){Volatile.Write(ref canceled,true);transfer.Cancel();}};
            bool stopped=false;try{transfer.Download(server.Url,part,payload.Length,true);}catch(OperationCanceledException){stopped=true;}
            Check(stopped&&Directory.GetFiles(root,"cancel.part.range-*").Any(),"Cancellation observes all workers and retains resumable chunks");
            server.Slow=false;transfer=New(server);string downloaded=transfer.Download(server.Url,part,payload.Length,true);
            Check(Hash(downloaded)==Hash(payload),"Canceled download resumes cleanly with a new transfer");transfer.Cleanup(part,payload.Length);
        }
        using(var server=new Server()) {
            string part=Path.Combine(root,"oversize.part");using(var output=File.Create(part))output.SetLength(payload.Length+1L);
            bool rejected=false;try{New(server).Download(server.Url,part,payload.Length,true);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&server.BytesSent==0,"Oversized partial files are rejected without network traffic");
        }
    }
    private static void FileLockChecks()
    {
        string owned=Path.Combine(root,"writer.chunk");
        using(var writer=DownloadFiles.OpenWriter(owned,FileMode.OpenOrCreate,()=>false)) {
            using(var reader=new FileStream(owned,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))Check(reader.CanRead,"Chunk writer permits read-only scanners");
            bool rejected=false;try{using(var second=new FileStream(owned,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){} }catch(IOException error){rejected=DownloadFiles.IsSharingViolation(error);}
            Check(rejected,"Chunk writer still excludes competing writers");
        }
        using(DownloadFiles.Acquire(root)) {
            bool rejected=false;try{using(DownloadFiles.Acquire(root)){} }catch(IOException error){rejected=error.Message.Contains("another download or process");}
            Check(rejected,"One game folder cannot run overlapping download sessions");
        }
        using(DownloadFiles.Acquire(root))Check(true,"Download lease releases after completion or failure");
        using(var server=new Server()) {
            string part=Path.Combine(root,"readlock.part");string chunk=part+".range-0-"+(ResumableTransfer.ChunkBytes-1);
            using(var output=File.Create(chunk))output.Write(payload,0,1048576);
            var reader=new FileStream(chunk,FileMode.Open,FileAccess.Read,FileShare.Read);
            var release=Task.Run(()=>{Thread.Sleep(300);reader.Dispose();});
            var transfer=New(server);string downloaded=transfer.Download(server.Url,part,payload.Length,true);release.Wait();
            Check(Hash(downloaded)==Hash(payload),"Transient chunk-file read lock retries and resumes the original bytes");
            Check(server.Starts.Count(start=>start==1048576)==1,"Locked output is acquired before HTTP, preventing wasted repeated range requests");
            transfer.Cleanup(part,payload.Length);
        }
        string cleanup=Path.Combine(root,"deferred.part");File.WriteAllBytes(cleanup,new byte[31]);
        string locked=cleanup+".range-31-"+(31+ResumableTransfer.ChunkBytes-1);File.WriteAllText(locked,"cached chunk");int warnings=0;
        var cleaner=new ResumableTransfer{Warning=message=>warnings++};
        using(var reader=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            cleaner.Cleanup(cleanup,payload.Length,true);
            Check(warnings>0&&File.Exists(locked)&&File.Exists(cleanup),"Locked optional cleanup does not abort success and preserves prefix metadata for retry");
            bool rejected=false;try{cleaner.Cleanup(cleanup,payload.Length);}catch(IOException error){rejected=DownloadFiles.IsSharingViolation(error);}
            Check(rejected,"Invalid-hash cleanup remains strict when a corrupt chunk cannot be removed");
        }
        cleaner.Cleanup(cleanup,payload.Length,true);
        Check(!File.Exists(cleanup)&&!File.Exists(locked),"Deferred cleanup completes after the reader releases its lock");
        bool securityRejected=false;cleaner.ValidatePath=path=>{throw new InvalidDataException("Fixture path guard");};
        try{cleaner.Cleanup(Path.Combine(root,"guard.part"),payload.Length,true);}catch(InvalidDataException){securityRejected=true;}
        Check(securityRejected,"Best-effort cleanup never suppresses path-security checks");
        string blocked=Path.Combine(root,"blocked.chunk");File.WriteAllText(blocked,"keep");
        using(var reader=new FileStream(blocked,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            bool canceled=false;var release=Task.Run(()=>{Thread.Sleep(100);Volatile.Write(ref canceled,true);});
            var timer=System.Diagnostics.Stopwatch.StartNew();bool stopped=false;
            try{using(var writer=DownloadFiles.OpenWriter(blocked,FileMode.Open,()=>Volatile.Read(ref canceled))){} }catch(OperationCanceledException){stopped=true;}
            release.Wait();Check(stopped&&timer.ElapsedMilliseconds<1000,"Cancellation interrupts sharing-lock backoff promptly");
        }
    }
    private static ResumableTransfer New(Server server)=>new ResumableTransfer{CreateRequest=uri=>{
        var request=(HttpWebRequest)WebRequest.Create(uri);request.Timeout=5000;request.ReadWriteTimeout=5000;request.AllowAutoRedirect=false;request.ServicePoint.ConnectionLimit=4;return request;
    }};
    private sealed class Server:IDisposable
    {
        private readonly HttpListener listener=new HttpListener();
        private readonly ConcurrentBag<Task> handlers=new ConcurrentBag<Task>();
        private readonly Task loop;
        private int active,peak,interrupted;
        private long sent;
        public bool IgnoreRange,WrongRange,InterruptOnce;
        public volatile bool Slow;
        public readonly ConcurrentBag<long> Starts=new ConcurrentBag<long>();
        public int Peak=>peak;public long BytesSent=>Interlocked.Read(ref sent);
        public Uri Url {get;}
        public Server(){var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();Url=new Uri("http://127.0.0.1:"+port+"/archive.zip");listener.Prefixes.Add("http://127.0.0.1:"+port+"/");listener.Start();loop=Task.Run(async()=>{try{while(listener.IsListening){var context=await listener.GetContextAsync();handlers.Add(Task.Run(()=>Serve(context)));}}catch(HttpListenerException){}catch(ObjectDisposedException){}});}
        private void Serve(HttpListenerContext context)
        {
            int count=Interlocked.Increment(ref active);int old;do{old=peak;if(old>=count)break;}while(Interlocked.CompareExchange(ref peak,count,old)!=old);
            try {
                long start=0,end=payload.Length-1;string range=context.Request.Headers["Range"];
                if(range!=null&&!IgnoreRange){var pieces=range.Substring(6).Split('-');start=long.Parse(pieces[0]);if(pieces[1].Length>0)end=long.Parse(pieces[1]);context.Response.StatusCode=206;context.Response.Headers["Content-Range"]="bytes "+(WrongRange?start+1:start)+"-"+end+"/"+payload.Length;}
                Starts.Add(start);context.Response.ContentLength64=end-start+1;
                long stop=end;if(InterruptOnce&&Interlocked.CompareExchange(ref interrupted,1,0)==0)stop=Math.Min(stop,start+1024*1024-1);
                for(long offset=start;offset<=stop;){int bytes=(int)Math.Min(65536,stop-offset+1);context.Response.OutputStream.Write(payload,(int)offset,bytes);Interlocked.Add(ref sent,bytes);offset+=bytes;if(Slow)Thread.Sleep(5);}
                if(stop!=end)context.Response.Abort();else context.Response.Close();
            }catch(HttpListenerException){}catch(IOException){}catch(ObjectDisposedException){}finally{Interlocked.Decrement(ref active);}
        }
        public void Dispose(){listener.Stop();listener.Close();loop.Wait();Task.WaitAll(handlers.ToArray());}
    }
}
