# In-process probe: do not launch a second CoreCLR from protected legacy tooling.
function Initialize-UpdatedHealth {
 if('Aetherfront.UpdatedHealth' -as [type]){return}
 Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace Aetherfront {
 public static class UpdatedHealth {
  public static string Fetch(int port,string pin) {
   if(port<1||port>65535||pin==null||pin.Length!=64)throw new IOException("Invalid health endpoint/pin");
   using(var handler=new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false}) {
    handler.ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>certificate!=null&&Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(pin,StringComparison.OrdinalIgnoreCase);
    using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    using(var client=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(10)})
    using(var response=client.GetAsync("https://127.0.0.1:"+port+"/health",HttpCompletionOption.ResponseHeadersRead,deadline.Token).GetAwaiter().GetResult()) {
     response.EnsureSuccessStatusCode();
     using(var stream=response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
     using(var output=new MemoryStream()) {
      byte[] buffer=new byte[4096];int read;
      while((read=stream.ReadAsync(buffer,0,buffer.Length,deadline.Token).GetAwaiter().GetResult())>0){if(output.Length+read>65536)throw new IOException("Health body exceeds limit");output.Write(buffer,0,read);}
      return Encoding.UTF8.GetString(output.ToArray());
     }
    }
   }
  }
 }
}
'@
}
function Invoke-UpdatedHealthFetch([int]$Port,[string]$Pin){Initialize-UpdatedHealth;[Aetherfront.UpdatedHealth]::Fetch($Port,$Pin)}
function Assert-UpdatedHealth([int]$Port,[string]$Pin){
 if($Pin -cnotmatch '^[a-fA-F0-9]{64}$' -or $Port -lt 1 -or $Port -gt 65535){throw 'Invalid pinned health request'}
 $last=$null
 for($attempt=1;$attempt -le 3;$attempt++){
  try{
   $text=Invoke-UpdatedHealthFetch $Port $Pin;$body=$text|ConvertFrom-Json
   if($body.ok -ne $true -or $body.service -cne 'apf-pack-host'){throw 'Unexpected pinned health response'}
   return @{verified=$true;attempt=$attempt;certificateSha256=$Pin.ToLowerInvariant();observedAtUtc=[DateTime]::UtcNow.ToString('o')}
  }catch{$last=$_.Exception.Message;if($attempt -lt 3){Start-Sleep -Milliseconds 250}}
 }
 throw ('Updated HTTPS service failed its pinned health check: '+$last)
}
