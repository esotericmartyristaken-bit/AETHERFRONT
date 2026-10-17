using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using KesasInstall;

internal static class Program
{
    private const int RequestTimeout=8000;
    private static readonly string root=Path.Combine(Path.GetTempPath(),"APF-GitHubTests-"+Guid.NewGuid().ToString("N"));
    private static int checks;
    private static void Check(bool value,string description)
    {
        if(!value)throw new Exception(description);
        checks++;Console.WriteLine("PASS "+description);
    }
    private static void Reject<T>(Action action,string description) where T:Exception
    {
        bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,description);
    }
    private static int Main()
    {
        // A stuck runtime must fail the standalone executable, not hang the test run.
        using(var deadline=new Timer(_=>{Console.Error.WriteLine("FAIL GitHub tests exceeded 90 seconds.");Environment.Exit(2);},null,90000,Timeout.Infinite)) {
            try {
                Directory.CreateDirectory(root);
                SettingsChecks();TransportChecks();FallbackChecks();
                byte[] small=Payload(5*1024*1024+113);
                RedirectChecks(small);TransferChecks(small,Payload(40*1024*1024+113));
                Console.WriteLine("All "+checks+" GitHub/redirect checks passed.");return 0;
            }catch(Exception error){Console.Error.WriteLine(error);return 1;}
            finally {
                // This directory has a fresh per-process identifier and contains fixture files only.
                try{if(Directory.Exists(root))Directory.Delete(root,true);}catch(Exception error){Console.Error.WriteLine("Fixture cleanup deferred: "+error.Message);}
            }
        }
    }
    private static GitHubSettings Settings()=>new GitHubSettings{Enabled=true,Repository="Example-Owner/APF.Release_22"};
    private static void SettingsChecks()
    {
        var disabled=new GitHubSettings();disabled.Validate();
        Check(!disabled.Enabled&&disabled.Repository==""&&disabled.MediaTag=="pack-media","GitHub defaults are disabled with a separate pack-media tag");
        Reject<InvalidDataException>(()=>{var ignored=disabled.LatestFeed;},"Disabled latest feed cannot be constructed");
        Reject<InvalidDataException>(()=>disabled.Media("media.zip"),"Disabled media cannot be constructed");
        Reject<InvalidDataException>(()=>disabled.Client("kesas-rc22","client.zip"),"Disabled client cannot be constructed");
        disabled.Repository=null;disabled.MediaTag=null;disabled.Validate();
        Check(!disabled.AllowsTransport(new Uri("https://github.com/owner/repo/releases/latest/download/APFClientUpdate.json")),"Disabled incomplete settings do not enable transport");
        var settings=Settings();settings.Validate();
        string origin="https://github.com/"+settings.Repository+"/releases/";
        Check(settings.LatestFeed.AbsoluteUri==origin+"latest/download/APFClientUpdate.json","Latest feed URI is pinned to the configured repository");
        Check(settings.Media("APF-Media_22.zip").AbsoluteUri==origin+"download/pack-media/APF-Media_22.zip","Media URI uses its independent release tag");
        foreach(string version in new[]{"kesas-rc1","kesas-rc22","kesas-rc123456"})
            Check(settings.Client(version,"APFClient-"+version+".zip").AbsoluteUri==origin+"download/"+version+"/APFClient-"+version+".zip","Client URI: "+version);
        foreach(string repository in new[]{"o/r","Owner-39/repo.name_22",new string('a',39)+"/"+new string('r',100)}) {
            var valid=new GitHubSettings{Enabled=true,Repository=repository};valid.Validate();
            Check(valid.LatestFeed.AbsolutePath=="/"+repository+"/releases/latest/download/APFClientUpdate.json","Valid repository: "+repository);
        }
        foreach(string repository in new[]{null,"","owner","/repo","owner/","owner/repo/extra","https://github.com/owner/repo","owner/repo?query","owner/repo#fragment","owner/repo%2fother","owner\\repo","owner name/repo","-owner/repo","owner/.","owner/..",new string('o',40)+"/repo","owner/"+new string('r',101)}) {
            var invalid=new GitHubSettings{Enabled=true,Repository=repository};
            Reject<InvalidDataException>(invalid.Validate,"Reject repository: "+(repository??"<null>"));
        }
        foreach(string version in new[]{null,"","kesas-rc0","kesas-rc01","KESAS-rc22","kesas-RC22","rc22","kesas-rc-1","kesas-rc22/other","kesas-rc22?x","kesas-rc22#x","kesas-rc22.zip"," kesas-rc22"})
            Reject<InvalidDataException>(()=>settings.Client(version,"client.zip"),"Reject version: "+(version??"<null>"));
        foreach(string tag in new[]{"pack-media","media.v2_3-4",new string('a',64)}) {
            settings.MediaTag=tag;settings.Validate();
            Check(settings.Media("media.zip").AbsolutePath.Contains("/download/"+tag+"/"),"Valid media tag: "+tag);
        }
        foreach(string tag in new[]{null,"","latest","kesas-rc22","KESAS-RC99","../media","media/next","media?x","media#x","media tag","_media",new string('a',65)}) {
            settings.MediaTag=tag;Reject<InvalidDataException>(settings.Validate,"Reject media tag: "+(tag??"<null>"));
        }
        settings.MediaTag="pack-media";
        foreach(string asset in new[]{null,"","media","media.exe","media.ZIP","../media.zip","folder/media.zip","folder\\media.zip","media.zip?x","media.zip#x","media%2fother.zip","media zip.zip",".hidden.zip",new string('a',201)+".zip"}) {
            Reject<InvalidDataException>(()=>settings.Media(asset),"Reject media asset: "+(asset??"<null>"));
            Reject<InvalidDataException>(()=>settings.Client("kesas-rc22",asset),"Reject client asset: "+(asset??"<null>"));
        }
    }
    private static void TransportChecks()
    {
        var settings=Settings();Uri initial=settings.Client("kesas-rc22","client.zip");
        var approved=new[]{initial,settings.LatestFeed,settings.Media("media.zip"),
            new Uri("https://GITHUB.COM:443/Example-Owner/APF.Release_22/releases/download/kesas-rc22/client.zip"),
            new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/123/opaque?sp=r&sig=signed%2Bvalue&se=2099-01-01"),
            new Uri("https://objects.githubusercontent.com/github-production-release-asset-2e65be/123/opaque?X-Amz-Signature=abc&X-Amz-Credential=key%2Fscope")};
        foreach(Uri uri in approved) {
            Check(settings.AllowsTransport(uri),"Approved exact host/path: "+uri);
            Check(settings.AcceptsCertificate(uri,SslPolicyErrors.None),"Clean TLS accepted only for approved URI: "+uri.Host);
            for(int errors=1;errors<=7;errors++)Check(!settings.AcceptsCertificate(uri,(SslPolicyErrors)errors),"TLS errors never bypassed: "+uri.Host+" "+(SslPolicyErrors)errors);
        }
        var denied=new List<Uri>{null,new Uri("relative.zip",UriKind.Relative),
            new Uri("https://github.com/Other/APF.Release_22/releases/download/kesas-rc22/client.zip"),
            new Uri("https://github.com/Example-Owner/Other/releases/download/kesas-rc22/client.zip"),
            new Uri("https://github.com/Example-Owner/APF.Release_22-extra/releases/download/kesas-rc22/client.zip"),
            new Uri("https://github.com/Example-Owner/APF.Release_22/releases-evil/client.zip"),
            new Uri("https://github.com/Example-Owner/APF.Release_22/blob/main/client.zip"),
            new Uri(initial+"?sig=not-a-CDN"),new Uri(initial+"#fragment")};
        foreach(string host in new[]{"github.com.evil.invalid","www.github.com","github.com.","api.github.com","raw.githubusercontent.com","githubusercontent.com","evil.release-assets.githubusercontent.com","objects.githubusercontent.com.evil.invalid","127.0.0.1"})
            denied.Add(new Uri("https://"+host+initial.AbsolutePath));
        foreach(string host in new[]{"github.com","release-assets.githubusercontent.com","objects.githubusercontent.com"}) {
            string path=host=="github.com"?initial.AbsolutePath:"/github-production-release-asset/123/opaque?sig=value";
            foreach(string authority in new[]{host+":80",host+":444","user@"+host,"user:password@"+host})denied.Add(new Uri("https://"+authority+path));
            denied.Add(new Uri("http://"+host+path));denied.Add(new Uri("https://"+host+path+"#fragment"));
        }
        foreach(string host in new[]{"release-assets.githubusercontent.com","objects.githubusercontent.com"})
            foreach(string path in new[]{"/","/other?sig=value","/github-production-release-asset?sig=value","/github-production-release-asset-/123?sig=value","/other/github-production-release-asset/123?sig=value"})
                denied.Add(new Uri("https://"+host+path));
        denied.Add(new Uri("https://objects.githubusercontent.com/github-production-release-asset/123?sig="+new string('a',16400)));
        foreach(Uri uri in denied) {
            string label=uri==null?"<null>":uri.OriginalString.Length>200?"overlong URI":uri.OriginalString;
            Check(!settings.AllowsTransport(uri),"Reject transport: "+label);
            Check(!settings.AcceptsCertificate(uri,SslPolicyErrors.None),"Clean TLS cannot authorize forbidden transport: "+label);
            Reject<InvalidDataException>(()=>settings.ValidateRedirect(initial,uri),"Reject redirect target: "+label);
        }
        foreach(Uri target in new[]{initial,new Uri("https://github.com/Example-Owner/APF.Release_22/releases/download/another-tag/client.zip"),approved[4],approved[5]}) {
            settings.ValidateRedirect(initial,target);Check(true,"Approved redirect: "+target.Host+target.AbsolutePath);
        }
        foreach(string asset in new[]{"other.zip","CLIENT.zip","client.zip.exe"})
            Reject<InvalidDataException>(()=>settings.ValidateRedirect(initial,new Uri(initial,"./"+asset)),"Reject GitHub asset filename jump: "+asset);
        foreach(Uri badInitial in new[]{approved[4],new Uri("https://github.com/Other/repo/releases/download/tag/client.zip"),new Uri("http://github.com/Example-Owner/APF.Release_22/releases/download/tag/client.zip"),new Uri("relative.zip",UriKind.Relative),null})
            Reject<InvalidDataException>(()=>settings.ValidateRedirect(badInitial,approved[4]),"Reject untrusted initial redirect URI");
        var disabled=new GitHubSettings();
        foreach(Uri uri in approved)Check(!disabled.AllowsTransport(uri)&&!disabled.AcceptsCertificate(uri,SslPolicyErrors.None),"Disabled policy denies approved host: "+uri.Host);
        Reject<InvalidDataException>(()=>disabled.ValidateRedirect(initial,approved[4]),"Disabled policy rejects redirects");
    }
    private static void FallbackChecks()
    {
        var recoverable=new[]{WebExceptionStatus.Timeout,WebExceptionStatus.NameResolutionFailure,WebExceptionStatus.ConnectFailure,WebExceptionStatus.ConnectionClosed,WebExceptionStatus.ReceiveFailure,WebExceptionStatus.SendFailure,WebExceptionStatus.TrustFailure,WebExceptionStatus.SecureChannelFailure};
        foreach(WebExceptionStatus status in Enum.GetValues(typeof(WebExceptionStatus)))
            Check(GitHubSettings.CanFallBack(new WebException("Fixture only",status))==recoverable.Contains(status),"Fallback classification: "+status);
        using(var server=new Server(new byte[0]))foreach(int status in new[]{400,401,403,404,410,418,429,500,501,502,503,504}) {
            var request=LocalRequest(server.Url("status/"+status));WebException failure=null;
            try{using(var response=request.GetResponse())throw new Exception("Expected HTTP error "+status);}
            catch(WebException error){failure=error;}
            try{Check(failure.Response is HttpWebResponse&&GitHubSettings.CanFallBack(failure)==new[]{403,404,410,429,500,502,503,504}.Contains(status),"HTTP fallback classification: "+status);}
            finally{if(failure.Response!=null)failure.Response.Dispose();request.Abort();}
            server.AssertHealthy();
        }
        using(var server=new Server(new byte[0]))foreach(int status in new[]{403,404,429,500,503,504})using(var tracking=new Tracking()) {
            WebException failure=null;
            try{using(var lease=HttpRedirects.Open(tracking.Create(server.Url("status/"+status)),tracking.Create,tracking.Finish,delegate {},null))throw new Exception("Expected HTTP failure");}
            catch(WebException error){failure=error;}
            Check(failure!=null&&GitHubSettings.CanFallBack(failure),"Disposed failed GitHub responses still permit pinned fallback: "+status);
            Check(tracking.ActiveCount==0,"HTTP failure drains redirected request tracking: "+status);
            server.AssertHealthy();
        }
    }
    private static void RedirectChecks(byte[] payload)
    {
        Check(HttpRedirects.MaximumHops==5,"Redirect limit remains exactly five hops");
        using(var server=new Server(payload))using(var tracking=new Tracking()) {
            Uri initial=server.Url("chain/0");int validations=0;bool original=true;
            using(var lease=HttpRedirects.Open(tracking.Create(initial),tracking.Create,tracking.Finish,delegate {},(source,target)=>{
                original&=source==initial;validations++;ValidateLocal(server,source,target);
            })) {
                Check(lease.Response.StatusCode==HttpStatusCode.OK&&Read(lease.Response).SequenceEqual(payload),"Five GET redirects (301/302/303/307/308) return exact bytes");
                Check(tracking.ActiveCount==1,"Response lease retains only the final request while open");
            }
            Check(original&&validations==5&&server.Seen.Count==6&&server.Seen.All(r=>r.Method=="GET"),"Redirect validation always receives the original URI; all hops remain GET");
            Check(tracking.ActiveCount==0,"Disposing response lease drains request tracking");server.AssertHealthy();
        }
        foreach(bool openEnded in new[]{false,true})using(var server=new Server(payload))using(var tracking=new Tracking()) {
            int start=1048576,end=openEnded?payload.Length-1:1048576+65535;
            string range="bytes="+start+"-"+(openEnded?"":end.ToString(CultureInfo.InvariantCulture));
            var initial=tracking.Create(server.Url("start"));if(openEnded)initial.AddRange(start);else initial.AddRange(start,end);
            using(var lease=HttpRedirects.Open(initial,tracking.Create,tracking.Finish,delegate {},(source,target)=>ValidateLocal(server,source,target))) {
                Check(lease.Response.StatusCode==HttpStatusCode.PartialContent&&lease.Response.Headers["Content-Range"]=="bytes "+start+"-"+end+"/"+payload.Length,"Redirected range returns exact 206 metadata: "+range);
                Check(Read(lease.Response).SequenceEqual(payload.Skip(start).Take(end-start+1)),"Redirected range returns exact bytes: "+range);
            }
            Check(server.Seen.Count==3&&server.Seen.All(r=>r.Method=="GET"&&r.Range==range),"Exact Range header survives both hops: "+range);
            Check(tracking.ActiveCount==0,"Redirected range lease drains tracking");server.AssertHealthy();
        }
        using(var sourceServer=new Server(payload))using(var targetServer=new Server(payload))using(var tracking=new Tracking()) {
            sourceServer.RejectedTarget=targetServer.Url("asset");Uri initial=sourceServer.Url("deny");int validations=0;
            Reject<InvalidDataException>(()=>{
                using(var lease=HttpRedirects.Open(tracking.Create(initial),tracking.Create,tracking.Finish,delegate {},(source,target)=>{
                    validations++;if(source!=initial)throw new Exception("Redirect origin changed.");ValidateLocal(sourceServer,source,target);
                }))throw new Exception("Off-policy redirect unexpectedly succeeded.");
            },"Off-policy redirect rejected before contacting a second loopback listener");
            Check(validations==1&&tracking.Created.Count==1&&targetServer.Seen.Count==0&&tracking.ActiveCount==0,"Rejected target is neither created nor hit; tracking drains");
            sourceServer.AssertHealthy();targetServer.AssertHealthy();
        }
        using(var server=new Server(payload))using(var tracking=new Tracking()) {
            var timer=Stopwatch.StartNew();int validations=0;
            Reject<InvalidDataException>(()=>{
                using(var lease=HttpRedirects.Open(tracking.Create(server.Url("loop")),tracking.Create,tracking.Finish,delegate {},(source,target)=>{validations++;ValidateLocal(server,source,target);})){}
            },"Redirect loop fails with InvalidDataException");
            Check(server.Seen.Count==6&&tracking.Created.Count==6&&validations==5&&timer.ElapsedMilliseconds<5000&&tracking.ActiveCount==0,"Loop stops after six requests/five validated hops; tracking drains");server.AssertHealthy();
        }
        using(var server=new Server(payload))using(var tracking=new Tracking()) {
            bool canceled=false;
            Reject<OperationCanceledException>(()=>{
                using(var lease=HttpRedirects.Open(tracking.Create(server.Url("start")),tracking.Create,tracking.Finish,()=>{if(canceled)throw new OperationCanceledException();},(source,target)=>{ValidateLocal(server,source,target);canceled=true;})){}
            },"Cancellation after validation stops before creating the next request");
            Check(server.Seen.Count==1&&tracking.Created.Count==1&&tracking.ActiveCount==0,"Canceled redirect creates no target and drains tracking");server.AssertHealthy();
        }
    }
    private static void TransferChecks(byte[] small,byte[] large)
    {
        foreach(bool parallel in new[]{false,true}) {
            byte[] payload=parallel?large:small;string mode=parallel?"parallel":"single";
            using(var server=new Server(payload)) {
                var transfer=NewTransfer();Check(transfer.ValidateRedirect==null,"Default "+mode+" redirect validator is absent");
                Reject<InvalidDataException>(()=>transfer.Download(server.Url("start"),Path.Combine(root,"default-"+mode+".part"),payload.Length,parallel),"Default "+mode+" transfer rejects redirects");
                Check(server.Seen.Count>=1&&server.Seen.Count<=ResumableTransfer.Connections&&server.Seen.All(r=>r.Path=="/start")&&Active(transfer).Length==0,"Default "+mode+" rejection never hits a redirect target; tracking drains");server.AssertHealthy();
            }
            using(var server=new Server(payload)) {
                string part=Path.Combine(root,"resume-"+mode+".part");int prefix=parallel?2*1024*1024:1024*1024,cached=parallel?1024*1024:0;
                using(var output=File.Create(part))output.Write(payload,0,prefix);
                if(parallel)using(var output=File.Create(part+".range-"+prefix+"-"+(prefix+ResumableTransfer.ChunkBytes-1)))output.Write(payload,prefix,cached);
                var transfer=NewTransfer();transfer.ValidateRedirect=(source,target)=>ValidateLocal(server,source,target);
                string result=transfer.Download(server.Url("start"),part,payload.Length,parallel);
                Check(FileEquals(result,payload),"Two-hop "+mode+" resume assembles every correct byte");
                Check(Hash(result)==Hash(payload),"Two-hop "+mode+" resume matches SHA-256");
                var expected=new List<string>();
                if(parallel)for(long start=prefix;start<payload.Length;start+=ResumableTransfer.ChunkBytes)
                    expected.Add("bytes="+(start+(start==prefix?cached:0))+"-"+Math.Min(payload.Length-1L,start+ResumableTransfer.ChunkBytes-1));
                else expected.Add("bytes="+prefix+"-");
                foreach(string path in new[]{"/start","/middle","/asset"}) {
                    string[] actual=server.Seen.Where(r=>r.Path==path).Select(r=>r.Range).OrderBy(r=>r,StringComparer.Ordinal).ToArray();
                    Check(actual.SequenceEqual(expected.OrderBy(r=>r,StringComparer.Ordinal)),"Exact resumed "+mode+" ranges at "+path);
                }
                Check(server.BytesSent==payload.Length-prefix-cached&&server.Seen.All(r=>r.Method=="GET")&&Active(transfer).Length==0,"Two-hop "+mode+" resume reuses cached bytes and drains tracking");
                transfer.Cleanup(part,payload.Length);Check(!Directory.GetFiles(root,Path.GetFileName(part)+"*").Any(),"Verified "+mode+" resume cache cleanup completes");server.AssertHealthy();
            }
            CancellationCheck(payload,parallel);
        }
    }
    private static void CancellationCheck(byte[] payload,bool parallel)
    {
        string mode=parallel?"parallel":"single";
        using(var server=new Server(payload){Hold=true}) {
            var transfer=NewTransfer();int canceled=0;transfer.IsCanceled=()=>Volatile.Read(ref canceled)!=0;
            transfer.ValidateRedirect=(source,target)=>ValidateLocal(server,source,target);
            Exception failure=null;string part=Path.Combine(root,"cancel-"+mode+".part");
            var download=Task.Run(()=>{try{transfer.Download(server.Url("start"),part,payload.Length,parallel);}catch(Exception error){failure=error;}});
            try {
                int expected=parallel?ResumableTransfer.Connections:1;
                Check(WaitUntil(()=>server.Seen.Count(r=>r.Path=="/hold")==expected,5000),"All "+mode+" redirected requests reach held final responses");
                var pending=Active(transfer);
                Check(pending.Length==expected&&pending.All(r=>r.RequestUri.AbsolutePath=="/hold"),"Only active final "+mode+" redirected requests remain tracked");
                var timer=Stopwatch.StartNew();Volatile.Write(ref canceled,1);transfer.Cancel();
                Check(download.Wait(3000)&&timer.ElapsedMilliseconds<3000,"Cancel aborts held "+mode+" redirected requests before request timeout");
                Check(failure is OperationCanceledException||(failure is WebException&&((WebException)failure).Status==WebExceptionStatus.RequestCanceled),"Canceled "+mode+" transfer reports cancellation, never success");
                Check(Active(transfer).Length==0,"Canceled "+mode+" redirected request tracking drains completely");
            }finally {
                Volatile.Write(ref canceled,1);transfer.Cancel();server.Release.Set();
                if(!download.Wait(5000))throw new TimeoutException("Canceled fixture did not stop.");
            }
            server.AssertHealthy();
        }
    }
    private static bool WaitUntil(Func<bool> condition,int milliseconds)
    {
        var timer=Stopwatch.StartNew();while(!condition()){if(timer.ElapsedMilliseconds>=milliseconds)return false;Thread.Sleep(10);}return true;
    }
    private static ResumableTransfer NewTransfer()=>new ResumableTransfer{CreateRequest=LocalRequest};
    private static HttpWebRequest[] Active(ResumableTransfer transfer)
    {
        var type=typeof(ResumableTransfer);var gate=type.GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic);
        var requests=type.GetField("requests",BindingFlags.Instance|BindingFlags.NonPublic);
        if(gate==null||requests==null)throw new Exception("Update active-request assertions for the transfer's tracking fields.");
        lock(gate.GetValue(transfer))return ((HashSet<HttpWebRequest>)requests.GetValue(transfer)).ToArray();
    }
    private static void ValidateLocal(Server server,Uri initial,Uri target)
    {
        if(initial.Scheme!="http"||initial.Host!="127.0.0.1"||initial.Port!=server.Origin.Port||
            target.Scheme!="http"||target.Host!="127.0.0.1"||target.Port!=server.Origin.Port||target.UserInfo.Length!=0||target.Fragment.Length!=0||
            !(target.AbsolutePath=="/middle"||target.AbsolutePath=="/asset"||target.AbsolutePath=="/hold"||target.AbsolutePath=="/loop"||target.AbsolutePath.StartsWith("/chain/",StringComparison.Ordinal)))
            throw new InvalidDataException("Fixture redirect policy rejected target.");
    }
    private static HttpWebRequest LocalRequest(Uri uri)
    {
        // Defense in depth: even a broken policy cannot cause external traffic.
        if(!uri.IsAbsoluteUri||uri.Scheme!="http"||uri.Host!="127.0.0.1"||uri.UserInfo.Length!=0)throw new InvalidDataException("Only IPv4 loopback fixtures may be requested.");
        var request=(HttpWebRequest)WebRequest.Create(uri);request.Proxy=null;request.Method="GET";
        request.Timeout=RequestTimeout;request.ReadWriteTimeout=RequestTimeout;request.AllowAutoRedirect=false;
        request.AutomaticDecompression=DecompressionMethods.None;request.ServicePoint.ConnectionLimit=ResumableTransfer.Connections;
        return request;
    }
    private static byte[] Payload(int size)
    {
        var bytes=new byte[size];for(int i=0;i<bytes.Length;i++)bytes[i]=(byte)((i*31L+i/1024)%251);return bytes;
    }
    private static byte[] Read(HttpWebResponse response)
    {
        using(var input=response.GetResponseStream())using(var output=new MemoryStream()){input.CopyTo(output);return output.ToArray();}
    }
    private static bool FileEquals(string path,byte[] expected)
    {
        using(var input=File.OpenRead(path)) {
            if(input.Length!=expected.Length)return false;var buffer=new byte[65536];int offset=0,read;
            while((read=input.Read(buffer,0,buffer.Length))>0){for(int i=0;i<read;i++)if(buffer[i]!=expected[offset+i])return false;offset+=read;}
            return offset==expected.Length;
        }
    }
    private static string Hash(string path){using(var sha=SHA256.Create())using(var input=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(input));}
    private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes));}
    private sealed class Tracking:IDisposable
    {
        private readonly object gate=new object();
        private readonly HashSet<HttpWebRequest> active=new HashSet<HttpWebRequest>();
        public readonly ConcurrentQueue<Uri> Created=new ConcurrentQueue<Uri>();
        public int ActiveCount{get{lock(gate)return active.Count;}}
        public HttpWebRequest Create(Uri uri){var request=LocalRequest(uri);lock(gate)active.Add(request);Created.Enqueue(uri);return request;}
        public void Finish(HttpWebRequest request){lock(gate)active.Remove(request);}
        public void Dispose(){HttpWebRequest[] pending;lock(gate){pending=active.ToArray();active.Clear();}foreach(var request in pending)request.Abort();}
    }
    private sealed class RequestRecord
    {
        public string Path,Range,Method;
    }
    private sealed class Server:IDisposable
    {
        private readonly HttpListener listener=new HttpListener();
        private readonly ConcurrentBag<Task> handlers=new ConcurrentBag<Task>();
        private readonly ConcurrentQueue<Exception> errors=new ConcurrentQueue<Exception>();
        private readonly byte[] payload;
        private readonly Task loop;
        private long sent;
        public readonly ConcurrentQueue<RequestRecord> Seen=new ConcurrentQueue<RequestRecord>();
        public readonly ManualResetEventSlim Release=new ManualResetEventSlim(false);
        public readonly Uri Origin;
        public bool Hold;
        public Uri RejectedTarget;
        public long BytesSent=>Interlocked.Read(ref sent);
        public Uri Url(string path)=>new Uri(Origin,path);
        public Server(byte[] payload)
        {
            this.payload=payload;
            var port=new TcpListener(IPAddress.Loopback,0);port.Start();int number=((IPEndPoint)port.LocalEndpoint).Port;port.Stop();
            Origin=new Uri("http://127.0.0.1:"+number+"/");listener.Prefixes.Add(Origin.AbsoluteUri);listener.Start();
            loop=Task.Factory.StartNew(()=>{
                while(listener.IsListening) {
                    HttpListenerContext context;
                    try{context=listener.GetContext();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}
                    // Held responses must not starve the transfer's ThreadPool workers.
                    handlers.Add(Task.Factory.StartNew(()=>Handle(context),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default));
                }
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        public void AssertHealthy(){Exception error;if(errors.TryPeek(out error))throw new Exception("Loopback fixture failed.",error);}
        private static void Redirect(HttpListenerResponse response,int status,string location)
        {response.StatusCode=status;response.RedirectLocation=location;response.ContentLength64=0;}
        private void Handle(HttpListenerContext context)
        {
            bool held=false,redirect=false;
            try {
                var request=context.Request;var response=context.Response;string path=request.Url.AbsolutePath;
                redirect=path=="/start"||path=="/middle"||path=="/loop"||path=="/deny"||path.StartsWith("/chain/",StringComparison.Ordinal);
                Seen.Enqueue(new RequestRecord{Path=path,Range=request.Headers["Range"],Method=request.HttpMethod});
                if(path=="/start"){Redirect(response,302,"/middle");return;}
                if(path=="/middle"){Redirect(response,307,Hold?"/hold":"/asset");return;}
                if(path=="/loop"){Redirect(response,302,"/loop");return;}
                if(path=="/deny"){Redirect(response,302,(RejectedTarget??Url("asset")).AbsoluteUri);return;}
                if(path.StartsWith("/status/",StringComparison.Ordinal)){response.StatusCode=int.Parse(path.Substring(8),CultureInfo.InvariantCulture);response.ContentLength64=0;return;}
                if(path.StartsWith("/chain/",StringComparison.Ordinal)) {
                    int hop=int.Parse(path.Substring(7),CultureInfo.InvariantCulture);int[] codes={301,302,303,307,308};
                    if(hop<codes.Length){Redirect(response,codes[hop],"/chain/"+(hop+1));return;}
                }else if(path!="/asset"&&path!="/hold")throw new InvalidDataException("Unknown fixture path: "+path);
                if(path=="/hold") {
                    held=true;
                    if(!Release.Wait(15000))throw new TimeoutException("Held fixture was not released.");
                    // Cancellation fixtures intentionally close without writing a response body.
                    response.ContentLength64=0;return;
                }
                long start=0,end=payload.Length-1;string range=request.Headers["Range"];
                if(range!=null) {
                    if(!range.StartsWith("bytes=",StringComparison.Ordinal))throw new InvalidDataException("Invalid fixture range.");
                    string[] parts=range.Substring(6).Split('-');start=long.Parse(parts[0],CultureInfo.InvariantCulture);
                    if(parts.Length!=2)throw new InvalidDataException("Invalid fixture range bounds.");
                    if(parts[1].Length!=0)end=long.Parse(parts[1],CultureInfo.InvariantCulture);
                    if(start<0||end<start||end>=payload.Length)throw new InvalidDataException("Out-of-bounds fixture range.");
                    response.StatusCode=206;response.Headers["Content-Range"]="bytes "+start+"-"+end+"/"+payload.Length;
                }
                response.ContentLength64=end-start+1;
                for(long offset=start;offset<=end;) {
                    int count=(int)Math.Min(65536,end-offset+1);response.OutputStream.Write(payload,(int)offset,count);
                    Interlocked.Add(ref sent,count);offset+=count;
                }
            }catch(Exception error){RecordFailure(error,held||redirect||context.Response.StatusCode>=300);}
            finally{try{context.Response.Close();}catch(Exception error){RecordFailure(error,held||redirect||context.Response.StatusCode>=300);}}
        }
        private void RecordFailure(Exception error,bool held)
        {
            // Cancellation, rejected redirects and rejected HTTP statuses can
            // close the client socket before HttpListener finishes closing it.
            // Keep ordinary successful-payload fixture failures observable.
            if(held&&(error is HttpListenerException||error is IOException||error is ObjectDisposedException))return;
            if(held&&error is InvalidOperationException&&error.Message=="Cannot be changed after headers are sent.")return;
            errors.Enqueue(error);
        }
        public void Dispose()
        {
            Release.Set();listener.Close();
            if(!loop.Wait(2000))throw new TimeoutException("Loopback listener did not stop.");
            if(!Task.WaitAll(handlers.ToArray(),3000))throw new TimeoutException("Loopback handlers did not stop.");
            Release.Dispose();AssertHealthy();
        }
    }
}
