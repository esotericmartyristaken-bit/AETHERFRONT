#requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseRoot,
 [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RequestId,
 [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SourceCommit,
 [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ArchiveSha256)
$ErrorActionPreference='Stop'
$root='C:\Users\Administrator\Desktop\APF\KESAS-VPS-Server'
$request=$RequestId
$commit=$SourceCommit
$archiveHash=$ArchiveSha256
function Read-Shared([string]$Path){$s=[IO.FileStream]::new($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));try{$r=[IO.StreamReader]::new($s);try{$r.ReadToEnd()}finally{$r.Dispose()}}finally{$s.Dispose()}}
function Hash-Shared([string]$Path){$s=[IO.FileStream]::new($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));try{[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($s)).ToLowerInvariant()}finally{$s.Dispose()}}
function Json-Equal($a,$b){($a|ConvertTo-Json -Depth 100 -Compress) -ceq ($b|ConvertTo-Json -Depth 100 -Compress)}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
public static class Rc65PublicEvidence {
 public static string Fetch(string path,string destination,long limit) {
  var uri=new Uri("https://217.216.49.250:18866"+path);
  if(uri.Scheme!="https"||uri.Host!="217.216.49.250"||uri.Port!=18866||uri.UserInfo!="")throw new IOException("Unexpected origin");
  using(var handler=new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false}) {
   handler.ServerCertificateCustomValidationCallback=(request,cert,chain,errors)=>cert!=null&&Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals("34b4bf414b4923fdf8150d0c56a541d393a45bf76dd50ae10e93afc5d1358d09",StringComparison.OrdinalIgnoreCase);
   using(var client=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(50)})
   using(var response=client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()) {
    response.EnsureSuccessStatusCode();
    using(var input=response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
    using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read)) {
     var buffer=new byte[65536];long total=0;int read;
     while((read=input.Read(buffer,0,buffer.Length))>0){total+=read;if(total>limit)throw new IOException("Evidence size limit");output.Write(buffer,0,read);}
     output.Flush(true);output.Position=0;return Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
    }
   }
  }
 }
}
'@
$result=[ordered]@{startedAtUtc=[DateTime]::UtcNow.ToString('o');productionChangedByCollector=$false;checksPassed=$false}
try{
 $receipt=(Read-Shared ('C:\ProgramData\Aetherfront-VPSLocalPublisher\Status-v3\'+$request+'.json'))|ConvertFrom-Json
 if($receipt.status -cne 'healthy' -or $receipt.version -cne 'kesas-rc65' -or $receipt.sourceCommit -cne $commit -or $receipt.archiveSha256 -cne $archiveHash){throw 'Exact guarded publication has not reported healthy'}
 $result.receipt=$receipt
 $archive=Join-Path $ReleaseRoot 'Aetherfront-Release-kesas-rc65.zip'
 if((Hash-Shared $archive) -cne $archiveHash){throw 'Staged release changed'}
 $public=Join-Path $ReleaseRoot ('PublicEvidence-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'));$null=New-Item -ItemType Directory -Path $public
 $result.publicEvidence=$public
 $result.httpChecks=@(foreach($row in @(@{url='/health';file='health.json';limit=64KB},@{url='/APFPack.json';file='APFPack.json';limit=16MB},@{url='/updates/APFClientUpdate.json';file='APFClientUpdate.json';limit=16MB},@{url='/updates/kesas-rc65/APFClientUpdate.json';file='version-feed.json';limit=16MB},@{url='/updates/kesas-rc65/APFClient-kesas-rc65.zip';file='APFClient-kesas-rc65.zip';limit=32MB})){
  @{path=$row.url;status=200;sha256=[Rc65PublicEvidence]::Fetch($row.url,(Join-Path $public $row.file),$row.limit);observedAtUtc=[DateTime]::UtcNow.ToString('o')}
 })
 $pack=Get-Content -LiteralPath (Join-Path $public 'APFPack.json') -Raw|ConvertFrom-Json
 $feed=Get-Content -LiteralPath (Join-Path $public 'APFClientUpdate.json') -Raw|ConvertFrom-Json
 $health=Get-Content -LiteralPath (Join-Path $public 'health.json') -Raw|ConvertFrom-Json
 if(-not $health.ok -or $health.service -cne 'apf-pack-host' -or $pack.packVersion -cne 'kesas-rc65' -or $pack.requiredClientVersion -cne 'kesas-rc65' -or $feed.version -cne 'kesas-rc65'){throw 'Public health or versions differ'}
 if((Hash-Shared (Join-Path $public 'APFClientUpdate.json')) -cne (Hash-Shared (Join-Path $public 'version-feed.json'))){throw 'Global/versioned public feeds differ'}
 $tool=Join-Path $root 'UpdateTool'
 foreach($row in @(@{file='JsonFx.dll';sha='8ff95ee45b3e9167f8fd36622eb6e3ebce915f8fee4fc2bd6ccb5ce66749d54f'},@{file='ApfCore.dll';sha='cecd5a0f062cb2a87111218854a0a7fc61281f473e9bcf27f5b7f301b670c5a7'})){
  $file=Join-Path $tool $row.file;if((Hash-Shared $file) -cne $row.sha){throw 'Existing verifier identity changed'};$null=[Reflection.Assembly]::LoadFrom($file)
 }
 $publicKey=Join-Path $root 'secrets\update-signing-public.xml'
 if((Hash-Shared $publicKey) -cne '5d6a4b75d1af78a888c87b8c6203e00972fecbcfc6f604f351e68d77fc248ea1'){throw 'Signing public key pin differs'}
 $manifest=[ApfCore.ApfClientUpdateManifest]::FromJson([IO.File]::ReadAllText((Join-Path $public 'APFClientUpdate.json')))
 $validation=[ApfCore.ApfClientUpdateValidator]::Validate($manifest,$true);if(-not $validation.IsValid){throw 'Signed client schema rejected'}
 $key=[ApfCore.ApfClientUpdateSecurity]::LoadXmlPublicKey([IO.File]::ReadAllText($publicKey));try{if(-not [ApfCore.ApfClientUpdateSecurity]::Verify($manifest,$key)){throw 'RSA-SHA256 signature rejected'}}finally{$key.Dispose()}
 $clientZip=Join-Path $public 'APFClient-kesas-rc65.zip'
 if((Hash-Shared $clientZip) -cne $manifest.Sha256.ToLowerInvariant() -or (Get-Item -LiteralPath $clientZip).Length -ne $manifest.SizeBytes){throw 'Signed client ZIP size/hash differs'}
 $expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
 foreach($file in $manifest.Files){$expected.Add([ApfCore.ApfPaths]::Normalize($file.Path),$file)}
 $hashes=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
 $zip=[IO.Compression.ZipFile]::OpenRead($clientZip)
 try{foreach($entry in $zip.Entries){if([string]::IsNullOrEmpty($entry.Name)){continue};$name=[ApfCore.ApfPaths]::Normalize($entry.FullName)
  if(-not [ApfCore.ApfPaths]::IsSafeRelative($entry.FullName) -or -not $expected.ContainsKey($name) -or $hashes.ContainsKey($name)){throw 'Unexpected/duplicate signed client file'}
  $s=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($s)).ToLowerInvariant()}finally{$s.Dispose()}
  if($entry.Length -ne $expected[$name].SizeBytes -or $hash -cne $expected[$name].Sha256.ToLowerInvariant()){throw 'Signed file inventory differs'};$hashes.Add($name,$hash)
 }}finally{$zip.Dispose()}
 if($hashes.Count -ne $expected.Count){throw 'Missing signed files'}
 $releaseZip=[IO.Compression.ZipFile]::OpenRead($archive)
 try{
  $s=$releaseZip.GetEntry('ClientTemplate.zip').Open();$memory=[IO.MemoryStream]::new();try{$s.CopyTo($memory)}finally{$s.Dispose()};$memory.Position=0
  $template=[IO.Compression.ZipArchive]::new($memory,'Read')
  try{foreach($entry in $template.Entries){$s=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($s)).ToLowerInvariant()}finally{$s.Dispose()};if(-not $hashes.ContainsKey($entry.FullName) -or $hashes[$entry.FullName] -cne $hash){throw ('Public client differs from tested release template: '+$entry.FullName)}}}finally{$template.Dispose();$memory.Dispose()}
  $r=[IO.StreamReader]::new($releaseZip.GetEntry('RELEASE.json').Open());try{$release=$r.ReadToEnd()|ConvertFrom-Json}finally{$r.Dispose()}
  $result.serverPayload=@(foreach($row in $release.files|Where-Object path -CLike 'ServerPayload/*'){$path=Join-Path $root ('Server/'+$row.path.Substring(14));try{@{relative=$row.path;matches=((Hash-Shared $path) -ceq $row.sha256);readable=$true}}catch [UnauthorizedAccessException]{@{relative=$row.path;readable=$false;verification='Guarded candidate and activation; independent file read denied'}}})
 }finally{$releaseZip.Dispose()}
 if(@($result.serverPayload|Where-Object {$_.readable -and -not $_.matches}).Count){throw 'Readable running native payload files differ'}
 $result.signedClient=@{verified=$true;version=$manifest.Version;files=$hashes.Count;sha256=$manifest.Sha256;bytes=$manifest.SizeBytes;allTemplateFilesMatch=$true}
 $baseline=Get-Content -LiteralPath (Join-Path $ReleaseRoot 'Baseline.json') -Raw|ConvertFrom-Json
 $bridge=(Read-Shared (Join-Path $root 'Server\APFDiscordBridge\status.json'))|ConvertFrom-Json
 $udp=@(Get-NetUDPEndpoint -LocalPort 18865|Select-Object -ExpandProperty OwningProcess -Unique)
 $https=@(Get-NetTCPConnection -LocalPort 18866 -State Listen|Select-Object -ExpandProperty OwningProcess -Unique)
 if($bridge.serverPid -eq $baseline.bridge.serverPid -or ([DateTimeOffset]::UtcNow-[DateTimeOffset]$bridge.updatedAt).TotalSeconds -gt 15 -or $udp -notcontains $bridge.serverPid -or $https.Count -lt 1){throw 'Fresh server process/heartbeat/listener check failed'}
 $result.bridge=$bridge|Select-Object serverPid,updatedAt,playerCount,connectedCount
 $result.udpOwners=$udp;$result.httpsOwners=$https
 if(Test-Path -LiteralPath (Join-Path $root '.update-transaction.json')){throw 'Activation transaction still present'}
 $preservationPath=Join-Path 'C:\ProgramData\Aetherfront-VPSLocalPublisher\Status-v3' ($archiveHash+'.preservation.json')
 $result.preservation=(Read-Shared $preservationPath)|ConvertFrom-Json
 $result.preservationVerificationIdentity='Approved publisher; protected state is not readable by this independent collector'
 if(-not $result.preservation.checksPassed -or $result.preservation.version -cne 'kesas-rc65' -or [DateTimeOffset]$result.preservation.observedAtUtc -lt ([DateTimeOffset]$receipt.observedAtUtc).AddMinutes(-30)){throw 'Protected preservation proof missing or inconsistent'}
 $result.rollbackBackup=$result.preservation.backup
 $result.checksPassed=$true
}catch{$result.error=$_.Exception.Message}
$result.finishedAtUtc=[DateTime]::UtcNow.ToString('o')
$output=Join-Path $ReleaseRoot ('After-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'.json')
[IO.File]::WriteAllText($output,($result|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
if($result.checksPassed -and -not(Test-Path -LiteralPath (Join-Path $ReleaseRoot 'After.json'))){[IO.File]::Copy($output,(Join-Path $ReleaseRoot 'After.json'),$false)}
[pscustomobject]$result|Select-Object checksPassed,error,finishedAtUtc,receipt,bridge,signedClient,preservation,rollbackBackup|ConvertTo-Json -Depth 8
if(-not $result.checksPassed){throw 'Independent RC65 verification incomplete; see retained evidence'}
