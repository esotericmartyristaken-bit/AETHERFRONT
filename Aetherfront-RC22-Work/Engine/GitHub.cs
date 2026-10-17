using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text.RegularExpressions;

namespace KesasInstall
{
    public sealed class GitHubSettings
    {
        public bool Enabled { get; set; }
        public string Repository { get; set; } = "";
        public string MediaTag { get; set; } = "pack-media";
        public void Validate()
        {
            if(!Enabled)return;
            if(Repository==null || !Regex.IsMatch(Repository,@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") || Repository.EndsWith("/.",StringComparison.Ordinal) || Repository.EndsWith("/..",StringComparison.Ordinal))
                throw new InvalidDataException("GitHub repository must be owner/repository.");
            if(MediaTag==null || !Regex.IsMatch(MediaTag,@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") || MediaTag=="latest" || MediaTag.StartsWith("kesas-rc",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid GitHub media release tag.");
        }
        public Uri LatestFeed { get { Validate();RequireEnabled();return new Uri("https://github.com/"+Repository+"/releases/latest/download/APFClientUpdate.json"); } }
        public Uri Media(string asset){return Asset(MediaTag,asset);}
        public Uri Client(string version,string asset)
        {
            if(version==null || !Regex.IsMatch(version,@"^kesas-rc[1-9][0-9]*$"))throw new InvalidDataException("Invalid client release version.");
            return Asset(version,asset);
        }
        private Uri Asset(string tag,string asset)
        {
            Validate();RequireEnabled();
            if(asset==null || !Regex.IsMatch(asset,@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}\.zip$"))throw new InvalidDataException("Invalid GitHub archive filename.");
            return new Uri("https://github.com/"+Repository+"/releases/download/"+tag+"/"+asset);
        }
        private void RequireEnabled(){if(!Enabled)throw new InvalidDataException("GitHub downloads are not configured.");}
        private static bool Basic(Uri uri)=>uri!=null && uri.IsAbsoluteUri && uri.Scheme=="https" && uri.Port==443 && uri.UserInfo.Length==0 && uri.Fragment.Length==0 && uri.AbsoluteUri.Length<=16384;
        private bool RepositoryUri(Uri uri)=>Basic(uri) && uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase) && uri.Query.Length==0 && uri.AbsolutePath.StartsWith("/"+Repository+"/releases/",StringComparison.OrdinalIgnoreCase);
        private static bool AssetUri(Uri uri)=>Basic(uri) && (uri.Host.Equals("release-assets.githubusercontent.com",StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com",StringComparison.OrdinalIgnoreCase)) && Regex.IsMatch(uri.AbsolutePath,@"^/github-production-release-asset(?:-[A-Za-z0-9]+)?/");
        public bool AllowsTransport(Uri uri)=>Enabled && (RepositoryUri(uri)||AssetUri(uri));
        public bool AcceptsCertificate(Uri uri,SslPolicyErrors errors)=>AllowsTransport(uri) && errors==SslPolicyErrors.None;
        public void ValidateRedirect(Uri initial,Uri target)
        {
            Validate();
            if(!Enabled || !RepositoryUri(initial) || !AllowsTransport(target))throw new InvalidDataException("Download redirected outside the approved GitHub hosts.");
            if(target.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(target.AbsolutePath).Equals(Path.GetFileName(initial.AbsolutePath),StringComparison.Ordinal))
                throw new InvalidDataException("GitHub redirected to a different asset.");
        }
        public static bool CanFallBack(WebException error)
        {
            if(error.Status==WebExceptionStatus.RequestCanceled)return false;
            var response=error.Response as HttpWebResponse;
            if(response==null)return error.Status==WebExceptionStatus.Timeout || error.Status==WebExceptionStatus.NameResolutionFailure || error.Status==WebExceptionStatus.ConnectFailure || error.Status==WebExceptionStatus.ConnectionClosed || error.Status==WebExceptionStatus.ReceiveFailure || error.Status==WebExceptionStatus.SendFailure || error.Status==WebExceptionStatus.TrustFailure || error.Status==WebExceptionStatus.SecureChannelFailure;
            int code;
            if(error.Data[HttpRedirects.StatusCodeKey] is int saved)code=saved;
            else{try{code=(int)response.StatusCode;}catch(ObjectDisposedException){return false;}}
            return code==403 || code==404 || code==410 || code==429 || code==500 || code==502 || code==503 || code==504;
        }
    }

    public sealed class HttpResponseLease:IDisposable
    {
        public readonly HttpWebResponse Response;
        private readonly HttpWebRequest request;
        private readonly Action<HttpWebRequest> finish;
        public HttpResponseLease(HttpWebResponse response,HttpWebRequest request,Action<HttpWebRequest> finish){Response=response;this.request=request;this.finish=finish;}
        public void Dispose(){try{Response.Dispose();}finally{finish(request);}}
    }
    public static class HttpRedirects
    {
        public const int MaximumHops=5;
        public const string StatusCodeKey="Aetherfront.HttpStatusCode";
        public static HttpResponseLease Open(HttpWebRequest initial,Func<Uri,HttpWebRequest> create,Action<HttpWebRequest> finish,Action check,Action<Uri,Uri> validate)
        {
            var current=initial;
            try {
                for(int hops=0;;hops++) {
                    check();HttpWebResponse response;
                    try{response=(HttpWebResponse)current.GetResponse();}
                    catch(WebException error){var failed=error.Response as HttpWebResponse;if(failed!=null)error.Data[StatusCodeKey]=(int)failed.StatusCode;if(error.Response!=null)error.Response.Dispose();throw;}
                    int code=(int)response.StatusCode;
                    if(code!=301 && code!=302 && code!=303 && code!=307 && code!=308)return new HttpResponseLease(response,current,finish);
                    HttpWebRequest next=null;
                    try {
                        if(hops>=MaximumHops)throw new InvalidDataException("Too many download redirects.");
                        Uri target;
                        if(!Uri.TryCreate(current.RequestUri,response.Headers["Location"],out target) || validate==null)throw new InvalidDataException("Unexpected download redirect.");
                        validate(initial.RequestUri,target);check();
                        next=create(target);
                        string range=current.Headers[HttpRequestHeader.Range];
                        if(range!=null) {
                            var match=Regex.Match(range,@"^bytes=(\d+)-(\d*)$");
                            if(!match.Success)throw new InvalidDataException("Invalid redirected byte range.");
                            long start=long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
                            if(match.Groups[2].Length==0)next.AddRange(start);else next.AddRange(start,long.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture));
                        }
                    }catch{if(next!=null){finish(next);next.Abort();}throw;}
                    finally{response.Dispose();finish(current);}
                    current=next;
                }
            }catch{finish(current);current.Abort();throw;}
        }
    }
}
