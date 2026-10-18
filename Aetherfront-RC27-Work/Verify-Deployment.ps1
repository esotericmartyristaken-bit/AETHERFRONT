#requires -Version 7.0
<#
READ-ONLY deployment/public verification. Run only after main confirms native
activation AND GitHub completion. This script never publishes or repairs.

  ./Verify-Deployment.ps1 -BuildResultPath <BUILD-RESULT.json> `
      -PreservationSnapshotPath <private-snapshot.json>

Snapshot: { files:[{relativePath,sha256}], vanillaPid:2320,
vanillaStartedUtc:<UTC timestamp>, mediaAssets:[{name,size,digest}],
historicalVersions:[<tag>] }. The nine relativePath values resolve beneath
KESAS-VPS-Server, not the workspace. Exactly 228 existing media assets required.
Private snapshot contents, protected-file names/hashes and bridge player data
are NEVER copied to public evidence or included in errors.

Only writes: a NEW RC27 BuildAttempts/Verification-<GUID> directory containing
three anonymous public downloads and immutable VERIFICATION-RESULT.json.
No secret/config/Universe/VPN/native journal/service/GitHub changes. No media
downloads. Metadata uses local gh GET authentication; downloads use a separate
credential-free HttpClient, normal certificate validation and HTTPS redirects.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildResultPath,
    [Parameter(Mandatory)][string]$PreservationSnapshotPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$root = [IO.Path]::GetFullPath((Join-Path $work '../KESAS-VPS-Server'))
$version = 'kesas-rc27'; $baselineVersion = 'kesas-rc26'
$catalogRelative = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
$repository = 'esotericmartyristaken-bit/AETHERFRONT'
$utf8 = [Text.UTF8Encoding]::new($false,$true)
$jsonDateStrings = (Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')
$bindings = [Collections.Generic.List[object]]::new()
$stage = 'preflight'; $resultPath = $null

function Require([bool]$Condition,[string]$Code) {
    if (!$Condition) { throw [IO.InvalidDataException]::new("CHECK:$Code") }
}
function Relative([string]$Name) {
    Require (![string]::IsNullOrWhiteSpace($Name) -and !$Name.Contains('\') -and ![IO.Path]::IsPathRooted($Name)) 'unsafe-relative-path'
    foreach ($segment in $Name.Split('/')) {
        Require ($segment -and $segment -notin @('.','..') -and $segment -notmatch '[<>:"|?*\x00-\x1f]|[. ]$' -and
            $segment -notmatch '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])(?:\.|$)') 'unsafe-relative-path'
    }
    $Name
}
function Plain([string]$Path) {
    Require (![string]::IsNullOrWhiteSpace($Path) -and !$Path.StartsWith('\\')) 'unsafe-local-path'
    $full = [IO.Path]::GetFullPath($Path)
    $tail = $full.Substring([IO.Path]::GetPathRoot($full).Length).Replace('\','/').TrimEnd('/')
    if ($tail) { $null = Relative $tail }
    for ($cursor=$full; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        try { $attributes=[IO.File]::GetAttributes($cursor) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        Require (!( $attributes -band [IO.FileAttributes]::ReparsePoint)) 'local-reparse-point'
    }
    $full
}
function Child([string]$Directory,[string]$Name) {
    $null = Relative $Name
    $base = (Plain $Directory).TrimEnd('\')
    $path = Plain ([IO.Path]::Combine($base,$Name.Replace('/','\')))
    Require ($path.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) 'local-path-escape'
    $path
}
function Owned([string]$Path) {
    $full=Plain $Path
    Require ($full.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)) 'artifact-outside-rc27'
    $full
}
function StreamHash([IO.Stream]$Stream) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Hash([string]$Path) {
    $inputStream=[IO.FileStream]::new((Plain $Path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,1MB,[IO.FileOptions]::SequentialScan)
    try { StreamHash $inputStream } finally { $inputStream.Dispose() }
}
function Text([string]$Path,[long]$MaximumBytes=16MB) {
    $inputStream=[IO.FileStream]::new((Plain $Path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        Require ($inputStream.Length -le $MaximumBytes) 'json-size-limit'
        $reader=[IO.StreamReader]::new($inputStream,$utf8,$true)
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $inputStream.Dispose() }
}
function ParseJson([string]$Json) {
    $options=@{InputObject=$Json;NoEnumerate=$true}
    if ($jsonDateStrings) { $options.DateKind='String' }
    ConvertFrom-Json @options
}
function ReadJson([string]$Path,[long]$MaximumBytes=16MB) {
    ParseJson (Text $Path $MaximumBytes)
}
function Instant($Value) {
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value.ToUniversalTime()) }
    [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture)
}
function Canonical($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary] -or $Value -is [pscustomobject]) {
        $keys=[Collections.Generic.List[string]]::new()
        if ($Value -is [Collections.IDictionary]) { foreach ($key in $Value.Keys) { $keys.Add([string]$key) } }
        else { foreach ($property in $Value.PSObject.Properties) { $keys.Add($property.Name) } }
        $keys.Sort([StringComparer]::Ordinal)
        $pairs=@(foreach ($key in $keys) {
            $item=if ($Value -is [Collections.IDictionary]) { $Value[$key] } else { $Value.PSObject.Properties[$key].Value }
            (ConvertTo-Json -InputObject $key -Compress)+':'+(Canonical $item)
        })
        return '{'+($pairs -join ',')+'}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        return '['+(@(foreach ($item in $Value) { Canonical $item }) -join ',')+']'
    }
    ConvertTo-Json -InputObject $Value -Compress -Depth 100
}
function Bind([string]$Path,[string]$Expected,[long]$Size=-1) {
    Require ($Expected -cmatch '^[a-f0-9]{64}$') 'invalid-public-sha256'
    $full=Plain $Path
    Require ((Hash $full) -ceq $Expected) 'public-file-hash-mismatch'
    if ($Size -ge 0) { Require (([IO.FileInfo]$full).Length -eq $Size) 'public-file-size-mismatch' }
    $bindings.Add([pscustomobject]@{path=$full;sha256=$Expected;size=([IO.FileInfo]$full).Length})
}
function Files([string]$Directory,[int]$Maximum,[int]$Depth=0) {
    $pending=[Collections.Generic.Stack[object]]::new()
    $pending.Push(@{path=(Plain $Directory);depth=$Depth});$scanned=0;$count=0
    while ($pending.Count) {
        $current=$pending.Pop();Require ($current.depth -lt 32) 'artifact-tree-depth'
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current.path)) {
            $scanned++;Require ($scanned -le ($Maximum*4+128)) 'artifact-tree-entry-limit'
            $full=Plain $entry
            if ([IO.Directory]::Exists($full)) { $pending.Push(@{path=$full;depth=($current.depth+1)}) }
            else { $count++;Require ($count -le $Maximum) 'artifact-tree-file-limit';$full }
        }
    }
}
function Inventory($Records) {
    Require (@($Records).Count -eq 464) 'signed-inventory-count'
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $items=@(foreach ($record in $Records) {
        $path=Relative ([string]$record.path)
        Require ($seen.Add($path) -and [string]$record.sha256 -cmatch '^[a-f0-9]{64}$' -and [long]$record.sizeBytes -ge 0) 'invalid-signed-file-record'
        [pscustomobject][ordered]@{path=$path;sha256=[string]$record.sha256;sizeBytes=[long]$record.sizeBytes}
    })
    Canonical @($items | Sort-Object -Property path -CaseSensitive)
}
function ZipInventory([string]$Path,$Records,[Collections.IDictionary]$ExtraText=@{}) {
    $null=Inventory $Records
    Require ($ExtraText.Count -in @(0,4)) 'bootstrap-settings-count'
    $zip=[IO.Compression.ZipFile]::OpenRead((Plain $Path))
    try {
        Require ($zip.Entries.Count -le 2048) 'zip-entry-count'
        $entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name=[string]$entry.FullName
            if ($name.EndsWith('/')) { $null=Relative $name.TrimEnd('/'); continue }
            $null=Relative $name
            Require ($seen.Add($name)) 'duplicate-zip-file'
            $entries.Add($name,$entry)
        }
        Require ($entries.Count -eq (464+$ExtraText.Count)) 'zip-file-inventory-count'
        foreach ($record in $Records) {
            Require ($entries.ContainsKey([string]$record.path)) 'zip-canonical-path-mismatch'
            $entry=$entries[[string]$record.path]
            Require ($entry.Length -eq [long]$record.sizeBytes) 'zip-file-size-mismatch'
            $inputStream=$entry.Open()
            try { Require ((StreamHash $inputStream) -ceq [string]$record.sha256) 'zip-file-hash-mismatch' } finally { $inputStream.Dispose() }
        }
        foreach ($name in $ExtraText.Keys) {
            $null=Relative ([string]$name)
            Require ($entries.ContainsKey([string]$name)) 'bootstrap-setting-path'
            $expectedBytes=$utf8.GetBytes([string]$ExtraText[$name])
            $entry=$entries[[string]$name]
            Require ($entry.Length -eq $expectedBytes.Length -and $entry.Length -le 32768) 'bootstrap-setting-size'
            $expectedStream=[IO.MemoryStream]::new($expectedBytes,$false)
            $inputStream=$entry.Open()
            try { Require ((StreamHash $inputStream) -ceq (StreamHash $expectedStream)) 'bootstrap-setting-content' }
            finally { $inputStream.Dispose();$expectedStream.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function ZipJson([string]$Path,$Record,[long]$Maximum=4MB) {
    $zip=[IO.Compression.ZipFile]::OpenRead((Plain $Path))
    try {
        $matches=@($zip.Entries | Where-Object FullName -CEQ ([string]$Record.path))
        Require ($matches.Count -eq 1 -and $matches[0].Length -eq [long]$Record.sizeBytes -and $matches[0].Length -le $Maximum) 'zip-json-record'
        $inputStream=$matches[0].Open(); $memory=[IO.MemoryStream]::new()
        try {
            $inputStream.CopyTo($memory)
            Require ($memory.Length -le $Maximum) 'zip-json-size'
            $memory.Position=0
            Require ((StreamHash $memory) -ceq [string]$Record.sha256) 'zip-json-hash'
            ParseJson $utf8.GetString($memory.ToArray())
        } finally { $inputStream.Dispose();$memory.Dispose() }
    } finally { $zip.Dispose() }
}
function RunReadOnly([string]$Executable,[string[]]$Arguments,[string]$Code) {
    $start=[Diagnostics.ProcessStartInfo]::new((Plain $Executable))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw [IO.IOException]::new("CHECK:$Code-timeout") }
        $output=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult()
        Require ($process.ExitCode -eq 0) $Code
        $output
    } finally { $process.Dispose() }
}
function SignedFeed([string]$FeedPath,[string]$ArchivePath,[string]$ExpectedVersion) {
    $null=RunReadOnly $verifier @('--verify','--manifest',$FeedPath,'--archive',$ArchivePath,'--public-key-xml',$publicKey) 'native-signature-verification'
    $feed=ReadJson $FeedPath 4MB
    Require ($feed.version -ceq $ExpectedVersion -and $feed.packageUrl -ceq "$($deployment.packOrigin)/updates/$ExpectedVersion/APFClient-$ExpectedVersion.zip") 'signed-version-url'
    $null=Inventory $feed.files
    Bind $ArchivePath ([string]$feed.sha256) ([long]$feed.sizeBytes)
    Bind $FeedPath (Hash $FeedPath)
    $feed
}
function PrivatePreserved {
    Require (@($snapshot.files).Count -eq 9) 'private-preservation-contract'
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $snapshot.files) {
        $relative=([string]$file.relativePath).Replace('\','/')
        Require ($seen.Add($relative) -and [string]$file.sha256 -cmatch '^[a-fA-F0-9]{64}$') 'private-preservation-contract'
        Require ((Hash (Child $root $relative)) -ieq [string]$file.sha256) 'protected-preservation'
    }
}
function VanillaPreserved {
    Require ($snapshot.vanillaPid -eq 2320) 'vanilla-snapshot-pid'
    $vanilla=Get-Process -Id 2320 -ErrorAction Stop
    $expected=Instant $snapshot.vanillaStartedUtc
    Require ($vanilla.StartTime.ToUniversalTime().Ticks -eq $expected.UtcDateTime.Ticks) 'vanilla-start-time'
    $udp=@(Get-NetUDPEndpoint -LocalPort 2252 -ErrorAction Stop)
    Require ($udp.Count -gt 0 -and @($udp | Where-Object OwningProcess -NE 2320).Count -eq 0) 'vanilla-udp-ownership'
}
function BridgeJson([string]$Path,[switch]$Ready) {
    for ($attempt=0;$attempt -lt 20;$attempt++) {
        $inputStream=$null;$reader=$null
        try {
            $inputStream=[IO.FileStream]::new((Plain $Path),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            Require ($inputStream.Length -le 4MB) 'bridge-json-size'
            $reader=[IO.StreamReader]::new($inputStream,$utf8,$true)
            return (ParseJson $reader.ReadToEnd())
        } catch {
            $cause=$_.Exception.GetBaseException();$nativeCode=$cause.HResult -band 65535
            $retry=($cause -is [IO.IOException] -and $nativeCode -in @(32,33)) -or ($Ready -and $cause -is [IO.IOException] -and $nativeCode -in @(2,3))
            if (!$retry -or $attempt -eq 19) { throw }
            Start-Sleep -Milliseconds 100
        } finally { if ($reader) {$reader.Dispose()};if ($inputStream) {$inputStream.Dispose()} }
    }
    throw [IO.IOException]::new('CHECK:bridge-read-retry-limit')
}
function Healthy {
    $readyPath=Child $root 'Server/APFDiscordBridge/ready.json'
    $statusPath=Child $root 'Server/APFDiscordBridge/status.json'
    $ready=BridgeJson $readyPath -Ready
    $status=BridgeJson $statusPath
    $readyAgain=BridgeJson $readyPath -Ready
    Require ($ready.pid -eq $readyAgain.pid -and $ready.startedAt -ceq $readyAgain.startedAt -and $ready.pid -eq $status.serverPid -and [string]$status.serverPid -cmatch '^[1-9][0-9]*$') 'bridge-ready-pid'
    $updated=Instant $status.updatedAt
    $readyAt=Instant $ready.startedAt
    $age=([DateTimeOffset]::UtcNow-$updated.ToUniversalTime()).TotalSeconds
    Require ($age -ge -2 -and $age -le 10) 'bridge-status-freshness-10-seconds'
    $game=Get-Process -Id ([int]$status.serverPid) -ErrorAction Stop
    Require ([string]::Equals($game.Path,(Child $root 'Server/Server.exe'),[StringComparison]::OrdinalIgnoreCase)) 'game-process-path'
    Require ($readyAt.UtcDateTime -ge $game.StartTime.ToUniversalTime().AddSeconds(-2) -and $readyAt -le [DateTimeOffset]::UtcNow.AddSeconds(2) -and $updated -ge $readyAt.AddSeconds(-2)) 'bridge-ready-process-start'
    $udp=@(Get-NetUDPEndpoint -LocalPort 18865 -ErrorAction Stop)
    Require ($udp.Count -gt 0 -and @($udp | Where-Object OwningProcess -NE $game.Id).Count -eq 0) 'game-udp-18865-ownership'
    $tcp=@(Get-NetTCPConnection -LocalPort 18866 -State Listen -ErrorAction Stop)
    $owners=@($tcp | Select-Object -ExpandProperty OwningProcess -Unique)
    Require ($tcp.Count -gt 0 -and $owners.Count -eq 1) 'https-18866-listener'
    $https=Get-Process -Id $owners[0] -ErrorAction Stop
    Require ([string]::Equals($https.Path,(Child $root 'PackHost/ApfPackHost.exe'),[StringComparison]::OrdinalIgnoreCase)) 'https-process-path'
    $null=RunReadOnly $probe @('--probe-server','https://127.0.0.1:18866/health',[string]$deployment.certificateSha256) 'pinned-local-https-health'
    # Probe/listener queries may take time. Re-read now; never apply the 10-second
    # allowance to a status timestamp sampled before a slow diagnostic.
    $status=BridgeJson $statusPath;$finalReady=BridgeJson $readyPath -Ready
    Require ($status.serverPid -eq $game.Id -and $finalReady.pid -eq $game.Id -and $finalReady.startedAt -ceq $ready.startedAt) 'bridge-changed-during-health-check'
    $updated=Instant $status.updatedAt
    $age=([DateTimeOffset]::UtcNow-$updated.ToUniversalTime()).TotalSeconds
    Require ($age -ge -2 -and $age -le 10) 'bridge-status-freshness-10-seconds'
    [pscustomobject]@{gamePid=$game.Id;gameStartedUtc=$game.StartTime.ToUniversalTime().ToString('o');httpsPid=$https.Id;httpsStartedUtc=$https.StartTime.ToUniversalTime().ToString('o');statusAgeSeconds=[Math]::Round($age,3)}
}
function GhGet([string]$Suffix) {
    Require (($Suffix -ceq '' -or $Suffix.StartsWith('/')) -and $Suffix -notmatch '[\x00-\x20#]') 'github-get-route'
    $json=RunReadOnly $gh @('api',"repos/$repository$Suffix",'--hostname','github.com','--method','GET','--header','Accept: application/vnd.github+json','--header','X-GitHub-Api-Version: 2022-11-28') 'github-read-only-metadata'
    $value=ParseJson $json
    Write-Output -NoEnumerate $value
}
function GhList([string]$Suffix) {
    for ($page=1;$page -le 10;$page++) {
        $items=GhGet "$($Suffix)?per_page=100&page=$page"
        Require ($items -is [array] -and $items.Count -le 100) 'github-pagination-shape'
        foreach ($item in $items) { $item }
        if ($items.Count -lt 100) { return }
    }
    throw [IO.InvalidDataException]::new('CHECK:github-pagination-limit')
}
function AnonymousDownload([string]$Name,[string]$Expected,[long]$Size) {
    $null=Relative $Name
    Require (!$Name.Contains('/') -and $Size -gt 0 -and $Size -lt 2GB) 'public-download-contract'
    $handler=[Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect=$false;$handler.UseCookies=$false;$handler.UseDefaultCredentials=$false;$handler.PreAuthenticate=$false;$handler.Credentials=$null
    # No custom certificate callback, Authorization header, gh token, cookies,
    # default credentials, or TLS bypass is ever installed on this client.
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(60)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Aetherfront-Deployment-Verification/27')
    $cancel=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
    $uri=[Uri]"https://github.com/$repository/releases/download/$version/$([Uri]::EscapeDataString($Name))"
    try {
        for ($redirect=0;$redirect -le 5;$redirect++) {
            Require ($uri.Scheme -ceq 'https' -and !$uri.UserInfo -and !$uri.Fragment -and ($uri.Host -ceq 'github.com' -or $uri.Host.EndsWith('.githubusercontent.com',[StringComparison]::OrdinalIgnoreCase))) 'anonymous-https-redirect-policy'
            $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
                    Require ($redirect -lt 5 -and $null -ne $response.Headers.Location) 'anonymous-redirect-limit'
                    $uri=[Uri]::new($uri,$response.Headers.Location);continue
                }
                Require ([int]$response.StatusCode -eq 200) 'anonymous-http-status'
                if ($null -ne $response.Content.Headers.ContentLength) { Require ($response.Content.Headers.ContentLength -eq $Size) 'anonymous-content-length' }
                $inputStream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $output=[IO.FileStream]::new((Child $attempt $Name),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,1MB)
                $sha=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try {
                    $buffer=[byte[]]::new(1MB);$copied=[long]0
                    while (($count=$inputStream.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0) {
                        $copied+=$count;Require ($copied -le $Size) 'anonymous-download-size-limit'
                        $sha.AppendData($buffer,0,$count);$output.Write($buffer,0,$count)
                    }
                    $digest=[BitConverter]::ToString($sha.GetHashAndReset()).Replace('-','').ToLowerInvariant();$output.Flush($true)
                    Require ($copied -eq $Size -and $digest -ceq $Expected) 'anonymous-native-byte-mismatch'
                } finally { $sha.Dispose();$output.Dispose();$inputStream.Dispose() }
                return [pscustomobject]@{name=$Name;sizeBytes=$Size;sha256=$Expected;anonymous=$true;normalTlsValidation=$true}
            } finally { $response.Dispose() }
        }
    } finally { $cancel.Dispose();$client.Dispose();$handler.Dispose() }
}
function WriteResult($Value) {
    $output=[IO.FileStream]::new((Owned $resultPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=$utf8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 30)+"`n");$output.Write($bytes,0,$bytes.Length);$output.Flush($true) } finally { $output.Dispose() }
}

try {
    $BuildResultPath=Owned $BuildResultPath;$PreservationSnapshotPath=Plain $PreservationSnapshotPath
    $attempt=Owned (Child $work ('BuildAttempts/Verification-'+[Guid]::NewGuid().ToString('N')))
    Require (![IO.Directory]::Exists($attempt) -and ![IO.File]::Exists($attempt)) 'immutable-verification-attempt'
    $null=[IO.Directory]::CreateDirectory($attempt);$resultPath=Child $attempt 'VERIFICATION-RESULT.json'
    $buildHash=Hash $BuildResultPath;$snapshotHash=Hash $PreservationSnapshotPath
    $report=ReadJson $BuildResultPath;$snapshot=ReadJson $PreservationSnapshotPath 4MB
    Require ($report.schemaVersion -eq 1 -and $report.version -ceq $version -and $report.baselineVersion -ceq $baselineVersion -and $report.completed -is [bool] -and $report.completed) 'reviewed-build-contract'
    Require ($report.signedFiles -eq 464 -and $report.fullPackages -eq 211 -and $report.potatoPackages -eq 177 -and $report.newMediaCount -eq 0) 'reviewed-build-counts'
    Require (@($snapshot.mediaAssets).Count -eq 228 -and @($snapshot.historicalVersions).Count -gt 0) 'preservation-media-history-contract'
    PrivatePreserved;VanillaPreserved
    $deploymentPath=Child $root 'deployment.json';$deploymentHash=Hash $deploymentPath
    $deployment=ReadJson $deploymentPath 32768
    $address=$null
    Require ([Net.IPAddress]::TryParse([string]$deployment.address,[ref]$address) -and $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        $deployment.gamePort -eq 18865 -and $deployment.packPort -eq 18866 -and $deployment.packOrigin -ceq "https://$($deployment.address):18866" -and
        [string]$deployment.certificateSha256 -cmatch '^[a-fA-F0-9]{64}$') 'deployment-routing-contract'
    $publicKey=Child $root 'secrets/update-signing-public.xml'
    $publicKeyHash=Hash $publicKey
    $verifier=Child $root 'UpdateTool/ApfUpdateTool.exe';$probe=Child $root 'CertificateTool/ApfCertificateTool.exe'
    Require ((Hash $verifier) -ceq '215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30') 'reviewed-verifier-binary'
    Require ((Hash $probe) -ceq '1322e7e70d20e0f8160c39ff1209b9db5e8e693dc5a77964e14f813ce5a822a1') 'reviewed-probe-binary'
    $gh=Plain 'C:/Program Files/GitHub CLI/gh.exe'
    Require (![IO.File]::Exists((Child $root '.update-transaction.json'))) 'native-completion-journal-present'

    $stage='reviewed-artifacts'
    $directory=[IO.Path]::GetDirectoryName($BuildResultPath)
    Require ([IO.Path]::GetFileName($directory) -cmatch '^Release-[a-f0-9]{32}$') 'completed-release-attempt'
    $outputNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Require (@($report.outputFiles).Count -gt 0 -and @($report.outputFiles).Count -le 2048) 'reviewed-output-count'
    foreach ($file in $report.outputFiles) {
        Require ($outputNames.Add([string]$file.path)) 'reviewed-output-duplicate'
        Bind (Child $directory ([string]$file.path)) ([string]$file.sha256) ([long]$file.sizeBytes)
    }
    $releaseZip=Owned ([string]$report.releaseZip)
    Require ($releaseZip -ceq (Child $directory "Aetherfront-Release-$version.zip") -and (Owned ([string]$report.candidatePath)) -ceq (Child $directory 'Candidate')) 'reviewed-release-routing'
    Bind $releaseZip ([string]$report.releaseSha256)
    Bind (Child $root 'ClientTemplate.zip') (Hash (Child $directory 'Release/ClientTemplate.zip'))
    $codePath=Child $directory 'Candidate/Evidence/CODE-RESULT.json'
    Bind $codePath ([string]$report.codeResultSha256)
    $code=ReadJson $codePath
    Require ($code.schemaVersion -eq 1 -and $code.version -ceq $version -and $code.completed -is [bool] -and $code.completed -and $code.baselineVersion -ceq $baselineVersion) 'reviewed-code-contract'
    Bind (Child $directory 'Candidate/Evidence/audit.json') ([string]$code.auditSha256)
    Require ($report.auditSha256 -ceq $code.auditSha256 -and $report.baselineFeedSha256 -ceq $code.baselineFeedSha256 -and $report.baselineArchiveSha256 -ceq $code.baselineArchiveSha256) 'reviewed-code-build-binding'
    $previewFeedPath=Child $directory 'Candidate/Published/APFClientUpdate.json'
    $previewArchive=Child $directory "Candidate/Published/APFClient-$version.zip"
    $preview=SignedFeed $previewFeedPath $previewArchive $version
    $previewInventory=Inventory $preview.files
    foreach ($record in $preview.files) { Bind (Child (Child $directory 'Candidate/Client') ([string]$record.path)) ([string]$record.sha256) ([long]$record.sizeBytes) }
    Require (@(Files (Child $directory 'Candidate/Client') 465 | Select-Object -First 465).Count -eq 464) 'reviewed-client-exact-file-count'

    $stage='native-signed-inventory'
    $feedPath=Child $root 'Updates/APFClientUpdate.json'
    $archive=Child $root "Updates/$version/APFClient-$version.zip"
    $feed=SignedFeed $feedPath $archive $version
    Require ((Inventory $feed.files) -ceq $previewInventory) 'native-reviewed-canonical-inventory'
    ZipInventory $archive $feed.files
    $codeMappings=@(@('engine','GameData/LunaMultiplayer/Plugins/KesasInstall.dll'),@('client','GameData/LunaMultiplayer/Plugins/LmpClient.dll'),@('core','GameData/LunaMultiplayer/Plugins/ApfCore.dll'))
    foreach ($mapping in $codeMappings) {
        $record=@($feed.files | Where-Object path -CEQ $mapping[1])
        Require ($record.Count -eq 1 -and $record[0].sha256 -ceq [string]$code."$($mapping[0])Sha256") 'native-code-hash-binding'
    }
    $catalogRecord=@($feed.files | Where-Object path -CEQ $catalogRelative)
    Require ($catalogRecord.Count -eq 1 -and $catalogRecord[0].sha256 -ceq $report.catalogSha256) 'native-reviewed-catalog-hash'
    $catalog=ZipJson $archive $catalogRecord[0]
    Require ($catalog.version -ceq $version -and @($catalog.full.managedPackages).Count -eq 211 -and @($catalog.potato.managedPackages).Count -eq 177) 'native-catalog-profile-counts'

    $stage='catalog-policy-preservation'
    $oldFeedPath=Child $root "Updates/$baselineVersion/APFClientUpdate.json"
    $oldArchive=Child $root "Updates/$baselineVersion/APFClient-$baselineVersion.zip"
    Bind $oldFeedPath ([string]$report.baselineFeedSha256);Bind $oldArchive ([string]$report.baselineArchiveSha256)
    $oldFeed=SignedFeed $oldFeedPath $oldArchive $baselineVersion
    $oldCatalogRecord=@($oldFeed.files | Where-Object path -CEQ $catalogRelative)
    Require ($oldCatalogRecord.Count -eq 1) 'baseline-catalog-record'
    $oldCatalog=ZipJson $oldArchive $oldCatalogRecord[0]
    $reverse=ParseJson (ConvertTo-Json -InputObject $catalog -Depth 100)
    $reverse.version=$baselineVersion;$hettnIds=@()
    foreach ($profile in @('full','potato')) {
        Require ($catalog.$profile.packVersion -ceq $version -and $catalog.$profile.requiredClientVersion -ceq $version -and
            $oldCatalog.$profile.packVersion -ceq $baselineVersion -and $oldCatalog.$profile.requiredClientVersion -ceq $baselineVersion) 'catalog-version-routing'
        $old=@($oldCatalog.$profile.managedPackages | Where-Object stripPrefix -CEQ 'HideEmptyTechTreeNodes')
        $current=@($reverse.$profile.managedPackages | Where-Object stripPrefix -CEQ 'HideEmptyTechTreeNodes')
        Require ($old.Count -eq 1 -and $current.Count -eq 1 -and $current[0].id -ceq $old[0].id -and $current[0].version -ceq ($old[0].version+'-repair27')) 'hettn-only-repair27-version'
        $hettnIds+=([string]$old[0].id);$current[0].version=$old[0].version
        $reverse.$profile.packVersion=$baselineVersion;$reverse.$profile.requiredClientVersion=$baselineVersion
    }
    Require ($oldCatalog.version -ceq $baselineVersion -and $hettnIds[0] -ceq $hettnIds[1] -and (Canonical $reverse) -ceq (Canonical $oldCatalog)) 'catalog-only-version-and-hettn-exception'
    $oldCore=@($oldFeed.files | Where-Object path -CEQ 'GameData/LunaMultiplayer/Plugins/ApfCore.dll')
    Require ($oldCore.Count -eq 1 -and $oldCore[0].sha256 -ceq $code.coreSha256) 'core-unchanged-from-authenticated-26'
    $packPath=Child $root 'Server/Config/APFPack.json'
    $pack=ReadJson $packPath 4MB;$template=ReadJson (Child $directory 'Release/APFPack-template.json') 4MB
    Require ((Canonical $pack) -ceq (Canonical $template) -and @($pack.managedPackages).Count -eq 0 -and $pack.packVersion -ceq $version -and $pack.requiredClientVersion -ceq $version) 'native-pack-semantic-template-empty-managed'
    Bind $packPath (Hash $packPath)
    $technology=Child $root 'Server/Config/AetherfrontTechnology.json'
    $technologyHash=Hash (Child $directory 'Release/AetherfrontTechnology.json')
    $technologyBefore=@($report.inputFiles | Where-Object { [string]::Equals([string]$_.path,$technology,[StringComparison]::OrdinalIgnoreCase) })
    Require ($technologyBefore.Count -eq 1 -and $technologyBefore[0].sha256 -ceq $technologyHash) 'technology-build-preservation'
    Bind $technology $technologyHash

    $stage='native-prepared-byte-identity'
    $prepared=Child $root ".github-prepared/$version"
    $preparedPath=Child $prepared 'prepared.json';$preparedHash=Hash $preparedPath
    $record=ReadJson $preparedPath
    Require ($record.schema -eq 1 -and $record.version -ceq $version -and $record.fingerprint.releaseZipSha256 -ceq $report.releaseSha256 -and
        $record.fingerprint.notes -ceq $report.releaseNotes -and $report.notes -ceq $report.releaseNotes) 'prepared-reviewed-release-fingerprint'
    Require ($record.fingerprint.packOrigin -ceq $deployment.packOrigin -and $record.fingerprint.address -ceq $deployment.address -and
        $record.fingerprint.gamePort -eq 18865 -and $record.fingerprint.certificateSha256 -ceq ([string]$deployment.certificateSha256).ToLowerInvariant() -and
        $record.fingerprint.publicKeySha256 -ceq (Hash $publicKey)) 'prepared-deployment-fingerprint'
    $preparedNames=@('Published/APFClientUpdate.json',"Published/APFClient-$version.zip",'KESAS-Client-Bootstrap.zip','KESAS-Client-Bootstrap.zip.sha256',"Aetherfront-Starter-$version.zip",'install-catalog.json')
    Require (@($record.files).Count -eq 6 -and @(Files $prepared 8 | Select-Object -First 8).Count -eq 7) 'prepared-exact-file-count'
    foreach ($name in $preparedNames) {
        $declared=@($record.files | Where-Object path -CEQ $name)
        Require ($declared.Count -eq 1) 'prepared-canonical-file-record'
        Bind (Child $prepared $name) ([string]$declared[0].sha256) ([long]$declared[0].sizeBytes)
    }
    Bind $preparedPath $preparedHash
    Bind (Child $prepared 'install-catalog.json') ([string]$report.catalogSha256)
    Bind $feedPath (Hash (Child $prepared 'Published/APFClientUpdate.json'))
    Bind $archive (Hash (Child $prepared "Published/APFClient-$version.zip"))
    Bind (Child $root "Updates/$version/APFClientUpdate.json") (Hash $feedPath)
    $bootstrap=Child $root 'KESAS-Client-Bootstrap.zip'
    $bootstrapHash=Hash (Child $prepared "Aetherfront-Starter-$version.zip")
    Bind $bootstrap $bootstrapHash
    Bind (Child $prepared 'KESAS-Client-Bootstrap.zip') $bootstrapHash
    $checksumPath=Child $prepared 'KESAS-Client-Bootstrap.zip.sha256'
    Require ((Text $checksumPath 1024).TrimEnd("`r","`n") -ceq "$bootstrapHash  KESAS-Client-Bootstrap.zip") 'prepared-bootstrap-checksum'
    Bind (Child $root 'KESAS-Client-Bootstrap.zip.sha256') (Hash $checksumPath)
    # Export-Client adds exactly four public server-trust settings to the
    # signed 464-file template. They are not files in the generic update ZIP.
    $rsa=[Security.Cryptography.RSACryptoServiceProvider]::new()
    try {
        $rsa.PersistKeyInCsp=$false;$rsa.FromXmlString((Text $publicKey 32768))
        $bootstrapPublicKey=$rsa.ToXmlString($false)
    } finally { $rsa.Dispose() }
    $bootstrapSettings=[ordered]@{
        'GameData/LunaMultiplayer/Data/APFServer.txt'="$($deployment.address):$($deployment.gamePort)`n"
        'GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt'="$($deployment.certificateSha256)`n"
        'GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt'="$($deployment.packOrigin)/updates/APFClientUpdate.json`n"
        'GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml'=$bootstrapPublicKey
    }
    ZipInventory $bootstrap $feed.files $bootstrapSettings
    $initialHealth=Healthy;VanillaPreserved;PrivatePreserved

    $stage='public-release-metadata'
    Require ($catalog.gitHub.enabled -is [bool] -and $catalog.gitHub.enabled -and $catalog.gitHub.repository -ceq $repository -and $catalog.gitHub.mediaTag -ceq 'pack-media') 'signed-github-routing'
    $repo=GhGet ''
    Require ($repo.full_name -ceq $repository -and $repo.private -is [bool] -and !$repo.private -and $repo.visibility -ceq 'public') 'github-public-repository'
    $latest=GhGet '/releases/latest'
    Require ($latest.tag_name -ceq $version -and $latest.draft -is [bool] -and !$latest.draft -and $latest.prerelease -is [bool] -and !$latest.prerelease -and [string]$latest.id -cmatch '^[1-9][0-9]*$') 'github-public-latest-27'
    $assets=@(GhList "/releases/$($latest.id)/assets")
    Require ($assets.Count -eq 3) 'github-exact-three-version-assets'
    $native=[ordered]@{'APFClientUpdate.json'=$feedPath;"APFClient-$version.zip"=$archive;"Aetherfront-Starter-$version.zip"=$bootstrap}
    $downloads=@(foreach ($name in $native.Keys) {
        $asset=@($assets | Where-Object name -CEQ $name)
        $source=$native[$name];$hash=Hash $source;$size=([IO.FileInfo]$source).Length
        Require ($asset.Count -eq 1 -and $asset[0].state -ceq 'uploaded' -and $asset[0].size -eq $size -and $asset[0].digest -ceq "sha256:$hash" -and
            $asset[0].browser_download_url -ceq "https://github.com/$repository/releases/download/$version/$name") 'github-native-asset-name-size-digest'
        AnonymousDownload $name $hash $size
    })
    $null=SignedFeed (Child $attempt 'APFClientUpdate.json') (Child $attempt "APFClient-$version.zip") $version

    $stage='public-media-and-history-metadata'
    $mediaRelease=GhGet '/releases/tags/pack-media'
    Require ($mediaRelease.tag_name -ceq 'pack-media' -and $mediaRelease.draft -is [bool] -and !$mediaRelease.draft -and [string]$mediaRelease.id -cmatch '^[1-9][0-9]*$') 'github-public-media-release'
    $media=@(GhList "/releases/$($mediaRelease.id)/assets")
    Require ($media.Count -eq 228) 'public-media-exact-228'
    $mediaNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $snapshot.mediaAssets) {
        Require ($mediaNames.Add([string]$asset.name) -and [string]$asset.digest -cmatch '^sha256:[a-f0-9]{64}$' -and [long]$asset.size -gt 0) 'media-preservation-snapshot'
        $current=@($media | Where-Object name -CEQ ([string]$asset.name))
        Require ($current.Count -eq 1 -and $current[0].state -ceq 'uploaded' -and $current[0].size -eq [long]$asset.size -and $current[0].digest -ceq $asset.digest) 'public-historical-media-preservation'
    }
    foreach ($profile in @('full','potato')) {
        foreach ($package in $catalog.$profile.managedPackages) {
            $uri=[Uri]([string]$package.archiveUrl)
            Require ($uri.IsAbsoluteUri -and $uri.Scheme -ceq 'https' -and !$uri.UserInfo) 'catalog-media-url'
            $name=[Uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
            $declared=@($media | Where-Object name -CEQ $name)
            Require ($declared.Count -eq 1 -and $declared[0].size -eq [long]$package.sizeBytes -and $declared[0].digest -ceq "sha256:$($package.sha256)") 'catalog-public-media-binding'
        }
    }
    $releases=@(GhList '/releases');$tags=@(GhList '/tags')
    $historical=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($tag in $snapshot.historicalVersions) {
        Require ([string]$tag -cmatch '^(?:kesas-rc[1-9][0-9]*|pack-media)$' -and $historical.Add([string]$tag)) 'historical-tag-snapshot'
        $release=@($releases | Where-Object tag_name -CEQ ([string]$tag));$gitTag=@($tags | Where-Object name -CEQ ([string]$tag))
        Require ($release.Count -eq 1 -and $release[0].draft -is [bool] -and !$release[0].draft -and $gitTag.Count -eq 1) 'historical-public-release-and-tag-preservation'
    }
    Require ($historical.Contains($baselineVersion)) 'historical-26-tag-required'

    $stage='final-stability'
    foreach ($binding in $bindings) { Require ((Hash $binding.path) -ceq $binding.sha256 -and ([IO.FileInfo]$binding.path).Length -eq $binding.size) 'public-input-changed-during-verification' }
    Require ((Hash $BuildResultPath) -ceq $buildHash -and (Hash $PreservationSnapshotPath) -ceq $snapshotHash) 'verification-input-changed'
    Require ((Hash $deploymentPath) -ceq $deploymentHash -and (Hash $publicKey) -ceq $publicKeyHash -and
        (Hash $verifier) -ceq '215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30' -and
        (Hash $probe) -ceq '1322e7e70d20e0f8160c39ff1209b9db5e8e693dc5a77964e14f813ce5a822a1') 'verification-trust-input-changed'
    PrivatePreserved;VanillaPreserved
    Require (![IO.File]::Exists((Child $root '.update-transaction.json'))) 'native-completion-journal-present'
    $finalLatest=GhGet '/releases/latest'
    Require ($finalLatest.id -eq $latest.id -and $finalLatest.tag_name -ceq $version -and $finalLatest.draft -is [bool] -and !$finalLatest.draft -and
        $finalLatest.prerelease -is [bool] -and !$finalLatest.prerelease) 'public-latest-changed-during-verification'
    $finalHealth=Healthy
    Require ($finalHealth.gamePid -eq $initialHealth.gamePid -and $finalHealth.gameStartedUtc -ceq $initialHealth.gameStartedUtc -and
        $finalHealth.httpsPid -eq $initialHealth.httpsPid -and $finalHealth.httpsStartedUtc -ceq $initialHealth.httpsStartedUtc) 'owned-services-changed-during-verification'
    Require (![IO.File]::Exists((Child $root '.update-transaction.json'))) 'native-completion-journal-present'
    WriteResult ([ordered]@{schemaVersion=1;version=$version;completed=$true;passed=$true;verifiedAtUtc=[DateTime]::UtcNow.ToString('o')
        buildResultSha256=$buildHash;signedFiles=464;fullPackages=211;potatoPackages=177;catalogSha256=$report.catalogSha256
        codeHashes=[ordered]@{engine=$code.engineSha256;client=$code.clientSha256;core=$code.coreSha256};coreByteIdenticalToRC26=$true
        catalogOnlyVersionAndHETTNRepair27=$true;activePackSemanticallyMatchesTemplate=$true;serverManagedPackagesEmpty=$true;technologyUnchanged=$true
        protectedFilesVerified=9;protectedFilesUnchanged=$true;vanillaPid=2320;vanillaUnchanged=$true
        preparedFilesVerified=6;preparedFingerprintMatchesReviewedBuild=$true;nativePreparedBytesIdentical=$true;nativeCompletionJournalAbsent=$true
        gamePid=$finalHealth.gamePid;httpsPid=$finalHealth.httpsPid;gameUdpPort=18865;httpsPort=18866;bridgeFreshnessLimitSeconds=10
        bridgeFresh=$true;localHttpsPinned=$true;repository=$repository;latestPublicVersion=$version;anonymousAssets=$downloads
        anonymousAuthorization=$false;anonymousTlsBypass=$false;nativePublicBytesIdentical=$true
        mediaAssets=228;mediaMetadataOnly=$true;historicalMediaRetained=$true;historicalVersions=@($snapshot.historicalVersions);historicalTagsRetained=$true
        privateEvidenceIncluded=$false;productionWrites=$false;githubWrites=$false;serviceChanges=$false})
    Write-Output "VERIFICATION-RESULT: $resultPath"
} catch {
    # Never emit arbitrary tool output, parser context, snapshot values, private
    # paths or an exception stack that could contain protected-file hashes.
    $failure='unexpected-error';$message=$_.Exception.GetBaseException().Message
    if ($message -cmatch '^CHECK:([a-z0-9-]+)$') { $failure=$Matches[1] }
    if ($resultPath -and ![IO.File]::Exists($resultPath)) {
        WriteResult ([ordered]@{schemaVersion=1;version=$version;completed=$false;passed=$false;verifiedAtUtc=[DateTime]::UtcNow.ToString('o');stage=$stage;failureCode=$failure;privateEvidenceIncluded=$false;productionWrites=$false;githubWrites=$false;serviceChanges=$false})
    }
    throw "Deployment verification failed ($stage/$failure). No deployment changes requested."
}
