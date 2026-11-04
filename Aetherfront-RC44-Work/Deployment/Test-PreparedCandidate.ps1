#requires -Version 7.4
<##
Preparation-only gate. Does NOT acquire the live server lock, stop/start a live
service, invoke a scheduled task, sign an update, or change a production file.
Run from an already elevated Windows operator/publisher; never requests UAC.
ResultPath must be NEW, outside all input trees, in a new or Admin/SYSTEM-only
directory. Scratch, world snapshots, certificate, logs and receipt remain there
for review. Caller must recheck receipt freshness AND every bound input before
the production stop; this is evidence, not an activation permit/signature.

Snapshots use shared read handles (including Delete). They are per-file live
snapshots, NOT a transactional/VSS world backup. A separate writable copy is
booted; the immutable snapshot is retained. Large media and live bridge queues
are never copied. This gate verifies native startup, not a KSP client join.
Dot-source to load functions without executing anything (used by fixtures).

Receipt v1 contract (all booleans must be true before activation): success,
startPassed, shutdownPassed, productionPidUnchanged, productionUnchanged.
version is unchanged. createdAt is UTC after cleanup; startedAt/finishedAt
bound the run. releasePayloadHashes is the exact native payload inventory;
releaseFiles also binds metadata/client/media inputs, releaseManifestSha256
binds RELEASE.json. liveBaselineHashes paths are relative to deploymentRoot.
signedFeedSha256/signedArchiveSha256 bind exact already-signed prepared bytes.
production contains frozen PID/path/start-time and UDP/TCP ownership tuples.
Caller rechecks this private receipt and ALL inputs after any warning countdown.
##>
[CmdletBinding()]
param([string]$DeploymentRoot, [string]$ReleaseDirectory,
      [string]$PublishedDirectory, [string]$ExpectedVersion, [string]$ResultPath)

function Initialize-PCNative {
    if ('PreparedCandidate.ProcessLog' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
namespace PreparedCandidate {
    public sealed class ProcessLog : IDisposable {
        public Process Process { get; private set; }
        public string Executable { get; private set; }
        public DateTime StartedUtc { get; private set; }
        readonly FileStream output, error;
        public Task OutputTask { get; private set; }
        public Task ErrorTask { get; private set; }
        public ProcessLog(ProcessStartInfo start, string stdout, string stderr) {
            Executable = Path.GetFullPath(start.FileName);
            output = new FileStream(stdout, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            try {
                error = new FileStream(stderr, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                Process = new Process { StartInfo = start };
                if (!Process.Start()) throw new IOException("Candidate process did not start");
                // Retain the OS handle: cleanup never resolves a name or a reused PID.
                var handle = Process.SafeHandle;
                StartedUtc = Process.StartTime.ToUniversalTime();
                OutputTask = Pump(Process.StandardOutput.BaseStream, output);
                ErrorTask = Pump(Process.StandardError.BaseStream, error);
            } catch {
                if (Process != null) {
                    try { if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(5000); } } catch { }
                }
                output.Dispose(); if (error != null) error.Dispose(); throw;
            }
        }
        static async Task Pump(Stream input, Stream destination) {
            var buffer = new byte[8192]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0) {
                total += count;
                if (total > 64L * 1024 * 1024) throw new IOException("Candidate log limit exceeded");
                await destination.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
        }
        public void AssertOwned() {
            if (Process.HasExited) return;
            var module = Process.MainModule;
            if (Process.HasExited) return; // exit can race module enumeration
            if (module == null || !String.Equals(module.FileName, Executable, StringComparison.OrdinalIgnoreCase) ||
                Process.StartTime.ToUniversalTime() != StartedUtc)
                throw new IOException("Candidate process identity changed; refusing termination");
        }
        public void Drain() {
            if (!Task.WaitAll(new [] { OutputTask, ErrorTask }, 5000)) throw new IOException("Candidate log drain timeout");
        }
        public void Dispose() {
            if (Process != null && !Process.HasExited) throw new IOException("Candidate still alive");
            output.Dispose(); error.Dispose(); if (Process != null) Process.Dispose();
        }
    }
    public static class Https {
        public static string Fetch(string url, string pin, string destination, long limit) {
            var uri = new Uri(url);
            if (uri.Scheme != "https" || uri.Host != "127.0.0.1" || uri.UserInfo != "")
                throw new IOException("Only candidate loopback HTTPS is permitted");
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = false }) {
                handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                    cert != null && String.Equals(Convert.ToHexString(SHA256.HashData(cert.RawData)), pin, StringComparison.OrdinalIgnoreCase);
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                using (var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()) {
                    if ((int)response.StatusCode != 200 || response.Content.Headers.ContentLength > limit)
                        throw new IOException("Candidate HTTPS GET rejected");
                    using (var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    using (var source = response.Content.ReadAsStreamAsync(cancellation.Token).GetAwaiter().GetResult())
                    using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
                        var buffer = new byte[81920]; long total = 0; int count;
                        while ((count = source.ReadAsync(buffer, 0, buffer.Length, cancellation.Token).GetAwaiter().GetResult()) > 0) {
                            total += count; if (total > limit) throw new IOException("Candidate HTTPS size limit");
                            target.Write(buffer, 0, count); hash.AppendData(buffer, 0, count);
                        }
                        target.Flush(true); return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    }
                }
            }
        }
    }
}
'@
}

function Assert-PCPlatform {
    if (-not $IsWindows) { throw 'Candidate gate requires Windows' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($identity.User.Value -ne 'S-1-5-18' -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Candidate gate requires an already elevated Administrator or SYSTEM; no UAC requested'
    }
}

function Assert-PCPrivate([string]$Path) {
    $null = Assert-UpdatePath $Path
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) { throw 'Candidate evidence directory must have a protected private ACL' }
    $allowed = @('S-1-5-18', 'S-1-5-32-544')
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) {
        throw 'Candidate evidence owner must be Administrators or SYSTEM'
    }
    $seen = @()
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow') {
            if ($rule.IdentityReference.Value -notin $allowed) { throw 'Candidate evidence ACL grants another principal access' }
            if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl) { $seen += $rule.IdentityReference.Value }
        }
    }
    foreach ($sid in $allowed) { if ($sid -notin $seen) { throw 'Candidate evidence ACL lacks Admin/SYSTEM access' } }
}

function New-PCPrivateDirectory([string]$Path) {
    $full = Assert-UpdatePath $Path
    if (Test-Path -LiteralPath $full) { throw 'Candidate directory must be NEW' }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($full))) { throw 'Candidate directory parent must exist' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    # Set DACL at creation, before even an empty directory is exposed.
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($full), $acl)
    Assert-PCPrivate $full
    return $full
}

function Test-PCWithin([string]$Path, [string]$Root) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $r = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    return $p.Equals($r, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($r + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Read-PCText([string]$Path, [long]$Limit = 16MB) {
    $null = Assert-UpdatePath $Path
    $stream = [IO.File]::Open($Path, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($stream.Length -gt $Limit) { throw 'Candidate text limit exceeded' }
        $reader = [IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-PCHash([string]$Path) {
    $null = Assert-UpdatePath $Path
    $stream = [IO.File]::Open($Path, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Write-PCJson([string]$Path, $Value) {
    $null = Assert-UpdatePath $Path
    $text = $Value | ConvertTo-Json -Depth 80
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

function Get-PCFiles([string]$Root) {
    $null = Assert-UpdatePath $Root
    if (-not [IO.Directory]::Exists($Root)) { throw 'Required candidate input directory missing' }
    $stack = [Collections.Generic.Stack[string]]::new(); $stack.Push($Root)
    $count = 0
    while ($stack.Count) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($stack.Pop()) | Sort-Object) {
            $null = Assert-UpdatePath $entry
            if ([IO.Directory]::Exists($entry)) { $stack.Push($entry) }
            else { if (++$count -gt 30000) { throw 'Candidate inventory file limit exceeded' }; $entry }
        }
    }
}

function Copy-PCFile([string]$Source, [string]$Destination, [long]$Limit = 512MB) {
    $null = Assert-UpdatePath $Source; $null = Assert-UpdatePath $Destination
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    $inputStream = [IO.File]::Open($Source, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($inputStream.Length -gt $Limit) { throw 'Candidate copy limit exceeded' }
        $outputStream = [IO.File]::Open($Destination, 'CreateNew', 'Write', 'None')
        try {
            $buffer = [byte[]]::new(81920); [long]$total = 0
            while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $total += $count; if ($total -gt $Limit) { throw 'Candidate copy grew beyond its limit' }
                $outputStream.Write($buffer, 0, $count)
            }
            $outputStream.Flush($true)
        } finally { $outputStream.Dispose() }
        # An open handle tolerates atomic production replacements. Detect in-place
        # writes while copying; no handle ever denies production File.Replace.
        $inputStream.Position = 0
        $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)).ToLowerInvariant()
        if ((Get-PCHash $Destination) -cne $sourceHash) { throw 'Candidate source changed during shared snapshot' }
        return [pscustomobject]@{ sha256 = $sourceHash; sizeBytes = $total }
    } finally { $inputStream.Dispose() }
}

function Get-PCStaticFiles([string]$Root) {
    foreach ($name in @('deployment.json','Update.Common.ps1','secrets/update-signing-public.xml','secrets/pack.pfx')) {
        Get-UpdateChildPath $Root $name
    }
    foreach ($family in @('Server','PackHost','CertificateTool','UpdateTool')) {
        $base = Get-UpdateChildPath $Root $family
        foreach ($file in Get-ChildItem -LiteralPath $base -File -Force | Sort-Object Name) {
            if ($file.Name -match '\.(dll|exe|config)$|\.(deps|runtimeconfig)\.json$' -or $file.Name -ceq 'AetherfrontScienceSubjects.json') {
                Assert-UpdatePath $file.FullName
            }
        }
        # Only dependency subtrees; NEVER recurse Server itself (world/logs/media).
        foreach ($name in @('Plugins','cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant')) {
            $directory = Get-UpdateChildPath $base $name
            if (-not [IO.Directory]::Exists($directory)) { continue }
            foreach ($file in Get-PCFiles $directory) {
                $relative = [IO.Path]::GetRelativePath($base, $file).Replace('\','/')
                if ($relative -match '(?i)(^|/)(secrets|logs?)(/|$)' -or $relative -match '\.(pfx|p12|key|log)$') { throw 'Secrets/logs found in dependency subtree' }
                if ($file -match '\.(dll|exe|json|config)$') { $file }
            }
        }
    }
    $config = Get-UpdateChildPath $Root 'Server/Config'
    foreach ($file in Get-ChildItem -LiteralPath $config -File -Force | Sort-Object Name) {
        if ($file.Name -match '\.(xml|json|txt)$') {
            if ($file.Name -match '(?i)(secret|credential|token|private|password)') { throw 'Unexpected secret configuration file' }
            Assert-UpdatePath $file.FullName
        }
    }
}

function Get-PCInventory([string]$Root, [string[]]$Paths) {
    foreach ($path in $Paths | Sort-Object -Unique) {
        $item = Get-Item -LiteralPath $path -Force
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($Root, $path).Replace('\','/'); sha256 = Get-PCHash $path; sizeBytes = $item.Length }
    }
}

function Assert-PCNativeBytes($Context) {
    foreach ($row in $Context.Receipt.candidateFiles) {
        # Native settings loaders can normalize their private XML. Runtime,
        # payload data, PFX, public key and signed update bytes must not change.
        if ($row.path -cmatch '^Server/Config/') { continue }
        if ((Get-PCHash (Get-UpdateChildPath $Context.Candidate $row.path)) -cne $row.sha256) {
            throw 'Exact candidate runtime/prepared bytes changed'
        }
    }
}

function Assert-PCSame($Expected, $Actual, [string]$Message) {
    if (($Expected | ConvertTo-Json -Depth 80 -Compress) -cne ($Actual | ConvertTo-Json -Depth 80 -Compress)) { throw $Message }
}

function Get-PCProduction($Context) {
    $root = $Context.Root; $config = $Context.Config
    $processes = @(Get-UpdateServiceProcesses $root $config | Sort-Object Id)
    $game = @($processes | Where-Object Path -IEQ "$root\Server\Server.exe")
    $pack = @($processes | Where-Object Path -IEQ "$root\PackHost\ApfPackHost.exe")
    if ($game.Count -ne 1 -or $pack.Count -ne 1) { throw 'Preparation requires exactly one live native game and pack host' }
    $udp = @(Get-NetUDPEndpoint -LocalPort $config.gamePort -ErrorAction Stop)
    $tcp = @(Get-NetTCPConnection -LocalPort $config.packPort -State Listen -ErrorAction Stop)
    if (-not $udp.Count -or -not $tcp.Count) { throw 'Production listener missing' }
    foreach ($row in $udp) { if ($row.OwningProcess -ne $game[0].Id) { throw 'Production game ownership changed' } }
    foreach ($row in $tcp) { if ($row.OwningProcess -ne $pack[0].Id) { throw 'Production HTTPS ownership changed' } }
    [pscustomobject]@{
        processes = @($processes | ForEach-Object { [pscustomobject]@{ pid = $_.Id; path = $_.Path; startedAt = $_.StartTime.ToUniversalTime().ToString('o') } })
        udp = @($udp | Sort-Object LocalAddress,LocalPort | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)/$($_.OwningProcess)" })
        tcp = @($tcp | Sort-Object LocalAddress,LocalPort | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)/$($_.OwningProcess)" })
        packSha256 = Get-PCHash "$root/Server/Config/APFPack.json"
    }
}

function Assert-PCProduction($Context, [switch]$Full) {
    Assert-PCSame $Context.Production (Get-PCProduction $Context) 'Production PID/ports/pack changed during candidate gate'
    if ($Full) {
        $paths = @(Get-PCStaticFiles $Context.Root)
        Assert-PCSame $Context.Baseline @(Get-PCInventory $Context.Root $paths) 'Production configuration/binary baseline changed'
        $Context.Receipt.baselineChecks += [DateTime]::UtcNow.ToString('o')
    }
    $Context.Receipt.productionSamples++
}

function Get-PCRelease([string]$Directory, [string]$Version) {
    $text = Read-PCText "$Directory/RELEASE.json"; Assert-ReleaseJsonUnique $text
    $release = $text | ConvertFrom-Json
    if ($release.version -cne $Version -or $release.files -isnot [array]) { throw 'Release version/inventory mismatch' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $release.files) {
        $relative = Get-UpdateRelativePath $file.path
        if (-not $seen.Add($relative) -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid/duplicate release entry' }
        if ($relative -cmatch '^(ServerPayload|PackHostPayload|CertificateToolPayload|UpdateToolPayload)/') {
            $tail = $relative.Substring($relative.IndexOf('/') + 1)
            if ($tail -match '(?i)(^|/)(Config|Universe|Aetherfront|APFDiscordBridge|secrets|logs?)(/|$)' -and $tail -notmatch '^Plugins/APFDiscordBridge/[^/]+\.(dll|json)$') { throw 'Release payload contains protected state' }
            if ($tail -notmatch '\.(dll|exe|json|config)$' -or $tail -match '(?i)(^|/)(secrets?|passwords?|credentials?|private)([-_.]|$)') { throw 'Unsupported native payload file' }
        } elseif ($relative -cnotin @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json','MediaReferences.json','AccountReset.json') -and $relative -cnotmatch '^MediaPayload/kesas-[a-z0-9-]+-[a-f0-9]{64}\.zip$') { throw 'Unsupported release entry' }
        if ((Get-PCHash (Get-UpdateChildPath $Directory $relative)) -cne $file.sha256) { throw 'Release payload hash mismatch' }
    }
    foreach ($required in @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')) {
        if (-not $seen.Contains($required)) { throw 'Release required file missing' }
    }
    if (@(Get-PCFiles $Directory).Count -ne $seen.Count + 1) { throw 'Undeclared release file' }
    return $release
}

function Get-PCPublished([string]$Directory, [string]$Version, $Config) {
    $paths = @(Get-PCFiles $Directory)
    $names = @($paths | ForEach-Object { [IO.Path]::GetRelativePath($Directory, $_).Replace('\','/') } | Sort-Object)
    Assert-PCSame @('APFClientUpdate.json', "APFClient-$Version.zip" | Sort-Object) $names 'Published directory must contain exactly the prepared signed feed and ZIP'
    $text = Read-PCText "$Directory/APFClientUpdate.json"; Assert-ReleaseJsonUnique $text
    $feed = $text | ConvertFrom-Json
    $zip = "$Directory/APFClient-$Version.zip"
    if ($feed.version -cne $Version -or $feed.packageUrl -cne "$($Config.packOrigin)/updates/$Version/APFClient-$Version.zip" -or
        $feed.sha256 -cne (Get-PCHash $zip) -or $feed.sizeBytes -ne (Get-Item -LiteralPath $zip).Length -or
        [string]::IsNullOrWhiteSpace($feed.signature) -or @($feed.files).Count -eq 0) { throw 'Prepared signed feed/ZIP mismatch' }
    return @(Get-PCInventory $Directory $paths)
}

function Copy-PCWorld($Context) {
    $rows = @(); [long]$bytes = 0
    foreach ($world in @('Universe','Aetherfront')) {
        $source = "$($Context.Root)/Server/$world"
        $files = @(Get-PCFiles $source)
        if (-not $files.Count) { throw 'Production world snapshot must not be empty' }
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($source, $file).Replace('\','/')
            if ($relative -match '(?i)(^|/)(secrets|logs?)(/|$)|\.(pfx|p12|key|log|zip)$') { throw 'World contains excluded secret/log/archive data' }
            $snapshot = Get-UpdateChildPath $Context.Scratch "Snapshots/$world/$relative"
            $record = Copy-PCFile $file $snapshot
            $bytes += $record.sizeBytes; if ($bytes -gt 2GB) { throw 'World snapshot exceeds 2 GB; explicit design review required' }
            $null = Copy-PCFile $snapshot (Get-UpdateChildPath $Context.Candidate "Server/$world/$relative")
            [IO.File]::SetAttributes($snapshot, [IO.FileAttributes]::ReadOnly)
            $rows += [pscustomobject]@{path = "$world/$relative"; sha256 = $record.sha256; sizeBytes = $record.sizeBytes}
        }
    }
    $Context.Receipt.worldSnapshot = $rows
}

function Set-PCXml([string]$Path, [string]$RootName, [hashtable]$Values) {
    $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null
    if ([IO.File]::Exists($Path)) {
        $text = Read-PCText $Path
        if ($text -match '<!DOCTYPE|<!ENTITY') { throw 'DTD/entity forbidden in candidate configuration' }
        $document.LoadXml($text)
    } else { $null = $document.AppendChild($document.CreateElement($RootName)) }
    if ($document.DocumentElement.Name -cne $RootName) { throw 'Unexpected candidate XML root' }
    foreach ($key in $Values.Keys) {
        $nodes = @($document.DocumentElement.SelectNodes($key))
        if ($nodes.Count -gt 1) { throw 'Duplicate candidate XML setting' }
        $node = if ($nodes.Count) { $nodes[0] } else { $document.DocumentElement.AppendChild($document.CreateElement($key)) }
        $node.InnerText = [string]$Values[$key]
    }
    $document.Save($Path)
}

function Set-PCConfiguration($Context, $Release) {
    $candidate = $Context.Candidate; $config = "$candidate/Server/Config"
    $origin = "https://127.0.0.1:$($Context.PackPort)"
    $general = [xml](Read-PCText "$config/GeneralSettings.xml")
    if ($general.GeneralSettingsDefinition.GameMode -cne 'Science') { throw 'Candidate requires the actual production Science configuration' }
    Set-PCXml "$config/ConnectionSettings.xml" 'ConnectionSettingsDefinition' @{ListenAddress='127.0.0.1';Port=$Context.GamePort;Upnp='false'}
    Set-PCXml "$config/MasterServerSettings.xml" 'MasterServerSettingsDefinition' @{RegisterWithMasterServer='false'}
    Set-PCXml "$config/DebugSettings.xml" 'DebugSettingsDefinition' @{CustomMasterServer="127.0.0.1:$($Context.GamePort)"}
    Set-PCXml "$config/WebsiteSettings.xml" 'WebsiteSettingsDefinition' @{EnableWebsite='false';ListenAddress='127.0.0.1';Port=$Context.PackPort}
    foreach ($entry in @{
        'APFDirectServer.txt'="true`n"; 'APFManifestUrl.txt'="$origin/APFPack.json`n";
        'APFServer.txt'="127.0.0.1:$($Context.GamePort)`n";
        'APFClientUpdateUrl.txt'="$origin/updates/APFClientUpdate.json`n"
    }.GetEnumerator()) { [IO.File]::WriteAllText("$config/$($entry.Key)", $entry.Value, [Text.UTF8Encoding]::new($false)) }
    $old = Read-PCText "$($Context.Root)/Server/Config/APFPack.json" | ConvertFrom-Json
    $next = Read-PCText "$($Context.Release)/APFPack-template.json" | ConvertFrom-Json
    if ($next.packVersion -cne $Context.Version -or $next.requiredClientVersion -cne $Context.Version -or
        -not $old.serverId -or -not $old.packId) { throw 'Candidate pack version/identity mismatch' }
    $next.serverId = $old.serverId; $next.packId = $old.packId
    # Rewrite only the private candidate metadata. Signed feed remains byte exact.
    $text = ($next | ConvertTo-Json -Depth 80).Replace($Context.Config.packOrigin, $origin)
    [IO.File]::WriteAllText("$config/APFPack.json", $text, [Text.UTF8Encoding]::new($false))
    $technology = "$config/AetherfrontTechnology.json"
    if ([IO.File]::Exists($technology)) { [IO.File]::Delete($technology) }
    $null = Copy-PCFile "$($Context.Release)/AetherfrontTechnology.json" $technology
    foreach ($file in Get-ChildItem -LiteralPath $config -File) {
        if ($file.Name -notmatch '\.(xml|json|txt)$') { continue }
        $configurationText=Read-PCText $file.FullName
        if ($configurationText -match '(?i)(?<![a-z0-9])[a-z]:[\\/]|\\\\[a-z0-9]|\.\.[\\/]') {
            throw 'Candidate configuration contains an absolute/escaping filesystem path'
        }
        if ($configurationText.Contains($Context.Root) -or $configurationText.Contains($Context.Config.packOrigin)) {
            throw 'Candidate configuration still references production'
        }
    }
    $Context.Receipt.candidatePackSha256 = Get-PCHash "$config/APFPack.json"
    $Context.Receipt.packIdentity = @{serverId=$old.serverId;packId=$old.packId;packVersion=$Context.Version}
}

function Get-PCPorts($Context) {
    # Ephemeral per-run choices near 28865/28866; never production ports.
    foreach ($offset in (0..199 | Sort-Object { Get-Random })) {
        $gamePort = 28865 + 2 * $offset; $packPort = $gamePort + 1
        if ($gamePort -in @($Context.Config.gamePort,$Context.Config.packPort) -or $packPort -in @($Context.Config.gamePort,$Context.Config.packPort)) { continue }
        if (@(Get-NetUDPEndpoint -LocalPort $gamePort -ErrorAction SilentlyContinue).Count -or @(Get-NetTCPConnection -LocalPort $packPort -State Listen -ErrorAction SilentlyContinue).Count) { continue }
        $Context.GamePort = $gamePort; $Context.PackPort = $packPort; return
    }
    throw 'No isolated candidate ports available'
}

function Enable-PCIsolation($Context) {
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
    if ($profiles.Count -ne 3 -or @($profiles | Where-Object Enabled -ne $true).Count) { throw 'All Windows Firewall profiles must already be enabled' }
    $programs = @(Get-PCFiles $Context.Candidate | Where-Object { $_ -match '\.exe$' })
    foreach ($program in $programs) {
        # Block all nonloopback destinations, plus loopback services other than
        # this candidate's pair. No production-address exception is installed.
        $specifications = @(
            @{Protocol='Any'; RemoteAddress=@('0.0.0.0-127.0.0.0','127.0.0.2-255.255.255.255','::/0')},
            @{Protocol='TCP'; RemoteAddress='127.0.0.1';RemotePort=@("1-$($Context.GamePort-1)","$($Context.PackPort+1)-65535")},
            @{Protocol='UDP'; RemoteAddress='127.0.0.1';RemotePort=@("1-$($Context.GamePort-1)","$($Context.PackPort+1)-65535")}
        )
        foreach ($specification in $specifications) {
            $name = 'APF-PreparedCandidate-' + [Guid]::NewGuid().ToString('N')
            $Context.Firewall.Add($name)
            $null = New-NetFirewallRule -Name $name -DisplayName $name -Program $program -Direction Outbound -Action Block -Enabled True -Profile Any @specification -ErrorAction Stop
            $effective = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name $name -ErrorAction Stop)
            if ($effective.Count -ne 1 -or $effective[0].Enabled -ne 'True' -or $effective[0].Direction -ne 'Outbound' -or $effective[0].Action -ne 'Block') { throw 'Candidate outbound firewall rule not effective' }
            $filter = $effective[0] | Get-NetFirewallApplicationFilter -ErrorAction Stop
            if ($filter.Program -ine $program) { throw 'Candidate firewall program mismatch' }
        }
    }
    $Context.Receipt.isolation = @{loopback='127.0.0.1';gamePort=$Context.GamePort;packPort=$Context.PackPort;outboundRules=@($Context.Firewall.ToArray());inheritedOperatorEnvironment=$false}
}

function New-PCStartInfo($Context, [ValidateSet('Game','Pack','Verify')][string]$Role) {
    $candidate = $Context.Candidate
    $start = [Diagnostics.ProcessStartInfo]::new()
    $arguments = @()
    switch ($Role) {
        Game { $start.FileName="$candidate/Server/Server.exe"; $start.WorkingDirectory="$candidate/Server" }
        Pack {
            $start.FileName="$candidate/PackHost/ApfPackHost.exe"; $start.WorkingDirectory="$candidate/PackHost"
            $arguments=@('--root',"$candidate/Server/Config",'--listen-url',"https://127.0.0.1:$($Context.PackPort)",'--certificate-path',"$candidate/secrets/pack.pfx",'--client-update-root',"$candidate/Updates")
        }
        Verify {
            $start.FileName="$candidate/UpdateTool/ApfUpdateTool.exe"; $start.WorkingDirectory="$candidate/UpdateTool"
            $arguments=@('--verify','--manifest',"$candidate/Updates/APFClientUpdate.json",'--archive',"$candidate/Updates/$($Context.Version)/APFClient-$($Context.Version).zip",'--public-key-xml',"$candidate/secrets/update-signing-public.xml")
        }
    }
    $start.FileName = Assert-UpdatePath $start.FileName
    if (-not (Test-PCWithin $start.FileName $candidate)) { throw 'Executable escapes candidate' }
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Clear()
    foreach ($name in @('SystemRoot','WINDIR','SystemDrive')) { $start.Environment[$name]=[Environment]::GetEnvironmentVariable($name) }
    $start.Environment['PATH']=[IO.Path]::Combine($env:SystemRoot,'System32')
    foreach ($name in @('TEMP','TMP','USERPROFILE','APPDATA','LOCALAPPDATA')) { $start.Environment[$name]="$($Context.Scratch)/Profile" }
    $start.Environment['DOTNET_EnableDiagnostics']='0'
    $start.Environment['KESAS_LOCAL_ONLY']='1'
    return $start
}

function Start-PCOwned($Context, [ValidateSet('Game','Pack','Verify')][string]$Role) {
    Initialize-PCNative
    $start=New-PCStartInfo $Context $Role
    $tag = $Role + '-' + [Guid]::NewGuid().ToString('N')
    return [PreparedCandidate.ProcessLog]::new($start, "$($Context.Scratch)/Logs/$tag.stdout.log", "$($Context.Scratch)/Logs/$tag.stderr.log")
}

function Invoke-PCVerifySigned($Context) {
    $owned = $null
    try {
        $owned = Start-PCOwned $Context Verify
        $Context.Owned.Add($owned)
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        while (-not $owned.Process.HasExited) {
            Assert-PCProduction $Context
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Signed feed verification timeout' }
            Start-Sleep -Milliseconds 500
        }
        $owned.Drain()
        if ($owned.Process.ExitCode -ne 0) { throw 'Prepared signed update signature/inventory verification failed' }
    } finally { if ($owned -and $owned.Process.HasExited) { $owned.Dispose(); $null=$Context.Owned.Remove($owned) } }
}

function Assert-PCListeners($Context, [switch]$Require) {
    if (-not $Context.Game -or -not $Context.Pack) { return $false }
    $gameId=$Context.Game.Process.Id; $packId=$Context.Pack.Process.Id
    $udp=@(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object { $_.OwningProcess -in @($gameId,$packId) -or $_.LocalPort -eq $Context.GamePort })
    $tcp=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -in @($gameId,$packId) -or $_.LocalPort -eq $Context.PackPort })
    foreach ($row in $udp) { if ($row.OwningProcess -ne $gameId -or $row.LocalAddress -cne '127.0.0.1' -or $row.LocalPort -ne $Context.GamePort) { throw 'Candidate UDP not exclusively owned/loopback' } }
    foreach ($row in $tcp) { if ($row.OwningProcess -ne $packId -or $row.LocalAddress -cne '127.0.0.1' -or $row.LocalPort -ne $Context.PackPort) { throw 'Candidate HTTPS not exclusively owned/loopback' } }
    $connections=@(Get-NetTCPConnection -ErrorAction Stop | Where-Object { $_.OwningProcess -in @($gameId,$packId) -and $_.State -ne 'Listen' -and $_.RemotePort -gt 0 })
    foreach ($row in $connections) {
        # Accepted HTTPS sockets have the probe client's ephemeral RemotePort,
        # including CloseWait after a GET. They are inbound, not outbound.
        $acceptedHttps=$row.OwningProcess -eq $packId -and $row.LocalAddress -ceq '127.0.0.1' -and $row.LocalPort -eq $Context.PackPort -and $row.RemoteAddress -ceq '127.0.0.1'
        if (-not $acceptedHttps -and ($row.RemoteAddress -cne '127.0.0.1' -or $row.RemotePort -notin @($Context.GamePort,$Context.PackPort))) { throw 'Candidate opened a forbidden outbound connection' }
    }
    if ($Require -and ($udp.Count -ne 1 -or $tcp.Count -ne 1)) { throw 'Candidate owned loopback listeners missing' }
    return $udp.Count -eq 1 -and $tcp.Count -eq 1
}

function Read-PCStartup($Context) {
    $text = ''
    foreach ($directory in @("$($Context.Scratch)/Logs","$($Context.Candidate)/Server/Logs")) {
        if (-not [IO.Directory]::Exists($directory)) { continue }
        foreach ($file in Get-PCFiles $directory) { $text += Read-PCText $file 64MB }
    }
    if ($text -match '(?im)\bFATAL\b|Unhandled (process )?exception|Error in main server thread|\[APF\].*configuration invalid|\[APF Discord\].*(Startup failed|poll failed)|Failed to (load|initialize|start)') { throw 'Fatal/invalid candidate startup log' }
    return $text.Contains('All systems up and running.')
}

function Read-PCBridge($Context) {
    $directory="$($Context.Candidate)/Server/APFDiscordBridge"
    if (-not [IO.File]::Exists("$directory/ready.json") -or -not [IO.File]::Exists("$directory/status.json")) { return $null }
    # Only transient sharing conflicts are retryable by the outer readiness loop.
    try { $ready=Read-PCText "$directory/ready.json" 64KB | ConvertFrom-Json; $status=Read-PCText "$directory/status.json" 4MB | ConvertFrom-Json }
    catch [IO.IOException] { if (($_.Exception.GetBaseException().HResult -band 65535) -in @(32,33)) { return $null }; throw }
    $start=[DateTimeOffset]$ready.startedAt; $updated=[DateTimeOffset]$status.updatedAt
    $now=[DateTimeOffset]::UtcNow; $age=($now-$updated).TotalSeconds
    if ($ready.pid -ne $Context.Game.Process.Id -or $status.serverPid -ne $Context.Game.Process.Id -or
        $start.UtcDateTime -lt $Context.Game.StartedUtc.AddSeconds(-1) -or $start -gt $now.AddSeconds(2) -or
        $updated -lt $start -or $age -lt -2 -or $age -gt 5 -or $status.playerCount -ne 0 -or $status.connectedCount -ne 0) { throw 'Candidate bridge identity/heartbeat invalid' }
    return $updated.ToUniversalTime().ToString('o')
}

function Invoke-PCHttps($Context) {
    $base="https://127.0.0.1:$($Context.PackPort)"; $probes="$($Context.Scratch)/Probes"
    $null=[IO.Directory]::CreateDirectory($probes)
    $pin=$Context.Config.certificateSha256
    $null=[PreparedCandidate.Https]::Fetch("$base/health",$pin,"$probes/health.json",64KB)
    $health=Read-PCText "$probes/health.json" | ConvertFrom-Json
    if ($health.ok -ne $true -or $health.service -cne 'apf-pack-host') { throw 'Candidate pinned health body invalid' }
    $packHash=[PreparedCandidate.Https]::Fetch("$base/APFPack.json",$pin,"$probes/APFPack.json",16MB)
    if ($packHash -cne $Context.Receipt.candidatePackSha256) { throw 'Candidate served wrong APFPack metadata' }
    $feedHash=[PreparedCandidate.Https]::Fetch("$base/updates/APFClientUpdate.json",$pin,"$probes/APFClientUpdate.json",16MB)
    $versionFeedHash=[PreparedCandidate.Https]::Fetch("$base/updates/$($Context.Version)/APFClientUpdate.json",$pin,"$probes/version-feed.json",16MB)
    $archiveHash=[PreparedCandidate.Https]::Fetch("$base/updates/$($Context.Version)/APFClient-$($Context.Version).zip",$pin,"$probes/client.zip",2GB)
    $feed=@($Context.PublishedFiles | Where-Object path -CEQ 'APFClientUpdate.json')[0]
    $archive=@($Context.PublishedFiles | Where-Object path -CEQ "APFClient-$($Context.Version).zip")[0]
    if ($feedHash -cne $feed.sha256 -or $versionFeedHash -cne $feed.sha256 -or $archiveHash -cne $archive.sha256) { throw 'Candidate HTTPS served different prepared signed bytes' }
    $Context.Receipt.https=@{health=$true;certificateSha256=$pin;packSha256=$packHash;feedSha256=$feedHash;zipSha256=$archiveHash;checkedAt=[DateTime]::UtcNow.ToString('o')}
}

function Wait-PCReady($Context) {
    $deadline=[DateTime]::UtcNow.AddSeconds(180); $heartbeat=$null
    do {
        Assert-PCProduction $Context
        foreach ($owned in @($Context.Game,$Context.Pack)) {
            $owned.AssertOwned()
            if ($owned.Process.HasExited -or $owned.OutputTask.IsFaulted -or $owned.ErrorTask.IsFaulted) { throw 'Candidate service exited or logging failed during startup' }
        }
        $listening=Assert-PCListeners $Context
        $started=Read-PCStartup $Context
        $fresh=Read-PCBridge $Context
        if ($listening -and $started -and $fresh) {
            if ($heartbeat -and $fresh -cne $heartbeat) {
                Invoke-PCHttps $Context
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Candidate readiness deadline exceeded during HTTPS verification' }
                $null=Assert-PCListeners $Context -Require
                $null=Read-PCStartup $Context
                if (-not (Read-PCBridge $Context)) { throw 'Candidate bridge vanished during HTTPS checks' }
                $Context.Receipt.bridge=@{pid=$Context.Game.Process.Id;firstHeartbeat=$heartbeat;secondHeartbeat=$fresh}
                return
            }
            if (-not $heartbeat) { $heartbeat=$fresh }
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Candidate readiness timeout' }
        Start-Sleep -Milliseconds 500
    } while ($true)
}

function Stop-PCOwned($Context) {
    $clean=$true; $records=@()
    foreach ($owned in @($Context.Owned.ToArray() | Sort-Object { if ($_ -eq $Context.Game) { 0 } else { 1 } })) {
        $isGame=$owned -eq $Context.Game
        $requested=$false; $forced=$false; $verified=$false; $exited=$false
        $record=@{pid=$null;executable=$owned.Executable;exited=$false;exitCode=$null;gameGraceful=$false;loggingPassed=$false;disposed=$false}
        try {
            if (-not (Test-PCWithin $owned.Executable $Context.Candidate)) { throw 'Refusing cleanup outside exact candidate tree' }
            $owned.AssertOwned()
            $verified=$true
            $record.pid=$owned.Process.Id
            if ($Context.Receipt.startPassed -and $owned.Process.HasExited) { $clean=$false }
            if (-not $owned.Process.HasExited) {
                if ($isGame) {
                    $owned.Process.StandardInput.WriteLine('/shutdown'); $owned.Process.StandardInput.Flush()
                    $requested=$true
                    $deadline=[DateTime]::UtcNow.AddSeconds(30)
                    while (-not $owned.Process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                        # A production drift must not prevent candidate cleanup.
                        try { Assert-PCProduction $Context } catch { $clean=$false }
                        Start-Sleep -Milliseconds 250
                    }
                } else { $null=$owned.Process.CloseMainWindow() }
            }
        } catch { $clean=$false }
        finally {
            # Termination is independent of stdin, readiness and log failures.
            # Only verified, retained candidate handles can reach Kill.
            if ($verified) {
                try {
                    if (-not $owned.Process.HasExited) {
                        $owned.AssertOwned(); $forced=$true
                        if ($isGame) { $clean=$false }
                        $owned.Process.Kill() # exact owned PID only, never a process tree/name
                        if (-not $owned.Process.WaitForExit(5000)) { throw 'Candidate cleanup timeout' }
                    }
                    $exited=$owned.Process.HasExited; $record.exited=$exited
                    if ($exited) {
                        $record.exitCode=$owned.Process.ExitCode
                        $record.gameGraceful=($isGame -and $requested -and -not $forced -and $record.exitCode -eq 0)
                        if ($isGame -and $record.exitCode -ne 0) { $clean=$false }
                    }
                } catch { $clean=$false }
            }
            if ($exited) {
                # Logging failures still fail the receipt, but cannot suppress
                # disposal or leave an already-exited process registered alive.
                try { $owned.Drain(); $record.loggingPassed=$true } catch { $clean=$false }
                try { $owned.Dispose(); $record.disposed=$true } catch { $clean=$false }
                finally { $null=$Context.Owned.Remove($owned) }
            } else { $clean=$false } # retain ownership AND firewall until exit is confirmed
            $records+=,$record
        }
    }
    $Context.Receipt.shutdown=@{passed=($clean -and $Context.Owned.Count -eq 0);processes=$records;finishedAt=[DateTime]::UtcNow.ToString('o');packTermination='owned process only (PackHost has no shutdown API)'}
    if (-not $Context.Receipt.shutdown.passed) { throw 'Candidate clean shutdown failed; production untouched' }
}

function Disable-PCIsolation($Context) {
    if ($Context.Owned.Count) { throw 'Candidate still alive; outbound rules retained' }
    foreach ($name in $Context.Firewall.ToArray()) {
        # Only names generated by this invocation; never a broad rule group.
        Remove-NetFirewallRule -Name $name -ErrorAction Stop
        if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) { throw 'Candidate firewall cleanup incomplete' }
        $null=$Context.Firewall.Remove($name)
    }
}

function Invoke-PreparedCandidate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$DeploymentRoot,
          [Parameter(Mandatory)][string]$ReleaseDirectory,
          [Parameter(Mandatory)][string]$PublishedDirectory,
          [Parameter(Mandatory)][string]$ExpectedVersion,
          [Parameter(Mandatory)][string]$ResultPath)
    $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
    Assert-PCPlatform
    if ($ExpectedVersion -cnotmatch '^kesas-rc[1-9][0-9]{0,14}$') { throw 'Invalid candidate expected version' }
    # Check ancestors before importing the installed fixed helper file; do not
    # execute a helper reached through a junction/symlink. No executable option.
    $helper=[IO.Path]::Combine([IO.Path]::GetFullPath($DeploymentRoot),'Update.Common.ps1')
    $cursor=$helper
    while ($cursor) {
        if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in deployment helper path' }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    . $helper
    $root=Assert-UpdatePath $DeploymentRoot; $release=Assert-UpdatePath $ReleaseDirectory
    $published=Assert-UpdatePath $PublishedDirectory; $result=Assert-UpdatePath $ResultPath
    $parent=[IO.Path]::GetDirectoryName($result)
    if ([IO.Path]::GetExtension($result) -cne '.json' -or (Test-Path -LiteralPath $result)) { throw 'ResultPath must be a NEW JSON receipt' }
    foreach ($inputRoot in @($root,$release,$published)) {
        if ((Test-PCWithin $parent $inputRoot) -or (Test-PCWithin $inputRoot $parent)) { throw 'Receipt/scratch must be disjoint from deployment/release/published trees' }
    }
    if ((Test-PCWithin $release $published) -or (Test-PCWithin $published $release)) { throw 'Release and prepared signed directory must be disjoint' }
    if ([IO.Directory]::Exists($parent)) { Assert-PCPrivate $parent } else { $null=New-PCPrivateDirectory $parent }
    $scratch=New-PCPrivateDirectory ([IO.Path]::Combine($parent,'candidate-'+[Guid]::NewGuid().ToString('N')))
    $receipt=[ordered]@{
        schema='apf-prepared-candidate/v1';id=[Guid]::NewGuid().ToString('N');version=$ExpectedVersion;
        startedAt=[DateTime]::UtcNow.ToString('o');finishedAt=$null;createdAt=$null;deploymentRoot=$root;releaseDirectory=$release;publishedDirectory=$published;scratchDirectory=$scratch;
        gateSha256=Get-PCHash $PSCommandPath;startPassed=$false;shutdownPassed=$false;productionUnchanged=$false;productionPidUnchanged=$false;success=$false;
        releaseManifestSha256=$null;releaseFiles=@();releasePayloadHashes=@();liveBaselineHashes=@();publishedFiles=@();signedFeedSha256=$null;signedArchiveSha256=$null;production=$null;
        baselineChecks=@();productionSamples=0;worldSnapshot=@();candidateFiles=@();candidatePackSha256=$null;packIdentity=$null;
        isolation=$null;https=$null;bridge=$null;shutdown=$null;failureStage=$null;failureDetail=$null
    }
    $context=@{Root=$root;Release=$release;Published=$published;Version=$ExpectedVersion;Scratch=$scratch;Candidate="$scratch/Native";
        Config=$null;Production=$null;Baseline=@();PublishedFiles=@();Receipt=$receipt;GamePort=0;PackPort=0;Game=$null;Pack=$null;
        Owned=[Collections.Generic.List[object]]::new();Firewall=[Collections.Generic.List[string]]::new()}
    $stage='inputs'; $failure=$false; $cleanupFailed=$false
    try {
        $context.Config=Get-UpdateDeployment $root
        $releaseDocument=Get-PCRelease $release $ExpectedVersion
        $receipt.releaseManifestSha256=Get-PCHash "$release/RELEASE.json"
        $receipt.releaseFiles=@($releaseDocument.files | Sort-Object path)
        $receipt.releasePayloadHashes=@($releaseDocument.files | Where-Object path -CMatch '^(ServerPayload|PackHostPayload|CertificateToolPayload|UpdateToolPayload)/' | Sort-Object path)
        $context.PublishedFiles=@(Get-PCPublished $published $ExpectedVersion $context.Config); $receipt.publishedFiles=$context.PublishedFiles
        $receipt.signedFeedSha256=@($context.PublishedFiles | Where-Object path -CEQ 'APFClientUpdate.json')[0].sha256
        $receipt.signedArchiveSha256=@($context.PublishedFiles | Where-Object path -CEQ "APFClient-$ExpectedVersion.zip")[0].sha256
        $context.Baseline=@(Get-PCInventory $root @(Get-PCStaticFiles $root)); $receipt.liveBaselineHashes=$context.Baseline
        $context.Production=Get-PCProduction $context; $receipt.production=$context.Production
        $stage='snapshot'
        foreach ($directory in @($context.Candidate,"$scratch/Logs","$scratch/Profile")) { $null=[IO.Directory]::CreateDirectory($directory) }
        foreach ($row in $context.Baseline) {
            # Exactly one private PFX is needed by PackHost. Never copy signing
            # private keys, entire secrets/, logs/, queues, or static media ZIPs.
            if ($row.path -in @('deployment.json','Update.Common.ps1')) { continue }
            $record=Copy-PCFile (Get-UpdateChildPath $root $row.path) (Get-UpdateChildPath $context.Candidate $row.path)
            if ($record.sha256 -cne $row.sha256) { throw 'Production baseline changed during candidate copy' }
        }
        Copy-PCWorld $context
        $stage='overlay'
        $families=@{ServerPayload='Server';PackHostPayload='PackHost';CertificateToolPayload='CertificateTool';UpdateToolPayload='UpdateTool'}
        foreach ($row in $releaseDocument.files) {
            $prefix=$row.path.Split('/')[0]
            if (-not $families.ContainsKey($prefix)) { continue }
            $target=Get-UpdateChildPath $context.Candidate ($families[$prefix]+'/'+$row.path.Substring($prefix.Length+1))
            if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
            $copied=Copy-PCFile (Get-UpdateChildPath $release $row.path) $target
            if ($copied.sha256 -cne $row.sha256) { throw 'Native overlay differs from exact release payload' }
        }
        Get-PCPorts $context
        Set-PCConfiguration $context $releaseDocument
        foreach ($row in $context.PublishedFiles) {
            $target=Get-UpdateChildPath $context.Candidate ("Updates/$ExpectedVersion/"+$row.path)
            $copied=Copy-PCFile (Get-UpdateChildPath $published $row.path) $target 2GB
            if ($copied.sha256 -cne $row.sha256) { throw 'Prepared signed bytes changed during candidate copy' }
        }
        $null=Copy-PCFile "$($context.Candidate)/Updates/$ExpectedVersion/APFClientUpdate.json" "$($context.Candidate)/Updates/APFClientUpdate.json"
        $certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new("$($context.Candidate)/secrets/pack.pfx",'', [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            if (-not $certificate.HasPrivateKey -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData)) -ine $context.Config.certificateSha256) { throw 'Candidate PFX does not match production pin' }
        } finally { $certificate.Dispose() }
        $receipt.candidateFiles=@(Get-PCInventory $context.Candidate @(Get-PCFiles $context.Candidate | Where-Object { $_ -notmatch '[\\/]Server[\\/](Universe|Aetherfront)[\\/]' }))
        Assert-PCProduction $context -Full
        $stage='isolation'; Enable-PCIsolation $context
        $stage='signature'; Invoke-PCVerifySigned $context
        Assert-PCNativeBytes $context
        $stage='startup'
        $context.Pack=Start-PCOwned $context Pack; $context.Owned.Add($context.Pack)
        $context.Game=Start-PCOwned $context Game; $context.Owned.Add($context.Game)
        Wait-PCReady $context
        Assert-PCNativeBytes $context
        Assert-PCProduction $context -Full
        $receipt.startPassed=$true
    } catch { $failure=$true; $receipt.failureStage=$stage; $receipt.failureDetail=$_.Exception.Message }
    finally {
        try {
            Stop-PCOwned $context; $receipt.shutdownPassed=$true
            if ($receipt.startPassed -and -not (Read-PCStartup $context)) { throw 'Candidate startup evidence missing after shutdown' }
            if ($receipt.startPassed) { Assert-PCNativeBytes $context }
        } catch { $cleanupFailed=$true; if (-not $receipt.failureStage) { $receipt.failureStage='shutdown'; $receipt.failureDetail=$_.Exception.Message } }
        try { Disable-PCIsolation $context } catch { $cleanupFailed=$true; if (-not $receipt.failureStage) { $receipt.failureStage='isolation-cleanup' } }
        try {
            if ($context.Production) {
                Assert-PCSame $context.Production (Get-PCProduction $context) 'Production process/ports changed'
                $receipt.productionPidUnchanged=$true
                Assert-PCProduction $context -Full; $receipt.productionUnchanged=$true
            }
            if ($receipt.releaseManifestSha256) {
                if ((Get-PCHash "$release/RELEASE.json") -cne $receipt.releaseManifestSha256) { throw 'Release manifest changed' }
                $finalRelease=Get-PCRelease $release $ExpectedVersion
                Assert-PCSame $receipt.releaseFiles @($finalRelease.files | Sort-Object path) 'Release changed'
                Assert-PCSame $context.PublishedFiles @(Get-PCPublished $published $ExpectedVersion $context.Config) 'Prepared signed bytes changed'
            }
        } catch { $failure=$true; $receipt.productionUnchanged=$false; if (-not $receipt.failureStage) { $receipt.failureStage='final-input-baseline' } }
        $receipt.finishedAt=[DateTime]::UtcNow.ToString('o')
        $receipt.createdAt=$receipt.finishedAt
        $receipt.success=(-not $failure -and -not $cleanupFailed -and $receipt.startPassed -and $receipt.shutdownPassed -and $receipt.productionUnchanged -and $receipt.productionPidUnchanged)
        Assert-PCPrivate $parent
        # CreateNew: a previous receipt is never overwritten, even on failure.
        $stream=[IO.File]::Open($result,'CreateNew','Write','None')
        try { $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 80)); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    if (-not $receipt.success) { throw "Prepared candidate gate failed ($($receipt.failureStage)); inspect private receipt/logs: $result" }
    return [pscustomobject]@{Success=$true;Version=$ExpectedVersion;ResultPath=$result;ReceiptSha256=Get-PCHash $result}
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-PreparedCandidate -DeploymentRoot $DeploymentRoot -ReleaseDirectory $ReleaseDirectory -PublishedDirectory $PublishedDirectory -ExpectedVersion $ExpectedVersion -ResultPath $ResultPath
}
