# Dot-source only. The native caller MUST verify the feed signature before planning.
# Only gh's existing local authentication is used. Public download URLs need no token.

function Invoke-GitHubCli {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$Context, [switch]$AllowNotFound)
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $output = & 'C:\Program Files\GitHub CLI\gh.exe' @Arguments 2>&1
        $code = $LASTEXITCODE
    } catch { throw "GitHub CLI failed ($Context). Check local gh installation and authentication." }
    # Never include CLI output, arguments, environment, or credentials in an error.
    if ($code -ne 0) {
        # Only an explicit included HTTP status can establish absence. A CLI,
        # authentication, or network error must never masquerade as no latest.
        if ($AllowNotFound -and $Arguments -contains '--include' -and
            ($output -join "`n") -match '\AHTTP/[0-9.]+ 404(?:[ \r\n]|$)') { return ($output -join "`n") }
        throw "GitHub CLI failed ($Context). Check local gh access and retry."
    }
    return ($output -join "`n")
}

function Assert-GhpPath {
    param([string]$Path, [switch]$Directory)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[\x00-\x1f*?\[\]#]' -or
        $Path -match '(^|[\\/])\.\.?([\\/]|$)' -or $Path.StartsWith('\\')) {
        throw 'Unsafe publication path.'
    }
    try {
        $full = [IO.Path]::GetFullPath($Path)
        if ($full.Substring([IO.Path]::GetPathRoot($full).Length).Contains(':')) { throw 'Alternate stream.' }
        $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
        if ([bool]$item.PSIsContainer -ne [bool]$Directory) { throw 'Wrong path type.' }
        $cursor = $item
        while ($null -ne $cursor) {
            if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point.' }
            if ($cursor -is [IO.DirectoryInfo]) { $cursor = $cursor.Parent } else { $cursor = $cursor.Directory }
        }
        return $item.FullName
    } catch { throw 'Publication path is missing, invalid, or contains a reparse point.' }
}

function Assert-GhpPublicName {
    param([string]$Name)
    if ($Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9 ._()+-]*$' -or $Name.EndsWith('.') -or
        $Name.EndsWith(' ') -or $Name -match '(?i)(^|[ ._()-])(secrets?|private(?:key)?|credentials?|universe|state|deployment)([ ._()-]|$)' -or
        $Name -match '(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
        throw 'Unsafe public asset name.'
    }
}

function Get-GhpUrlName {
    param([string]$Url)
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $Url -match '(?i)\\|%2f|%5c|(?:^|/)(?:\.|%2e){1,2}(?:/|$)') { throw 'Unsafe catalog or feed URL.' }
    $name = [Uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
    Assert-GhpPublicName $name
    if ($name -notmatch '(?i)\.zip$') { throw 'Only ZIP media and client archives are public assets.' }
    return $name
}

function Get-GhpSha256 {
    param([string]$Path)
    # Large sequential reads avoid thousands of 4 KiB operations per second on
    # IOPS-limited hosts. Every byte is still SHA256-verified; no metadata cache.
    $inputStream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,1MB,[IO.FileOptions]::SequentialScan)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose();$inputStream.Dispose() }
}

function Get-GhpAsset {
    param([string]$Path, [string]$Kind, [string]$Tag, [string]$Repository)
    $full = Assert-GhpPath $Path
    $item = Get-Item -LiteralPath $full -ErrorAction Stop
    Assert-GhpPublicName $item.Name
    if ($item.Length -le 0 -or $item.Length -ge 2GB) { throw 'Public asset must be nonempty and smaller than 2 GiB.' }
    [pscustomobject]@{
        Path = $full; Name = $item.Name; Size = [long]$item.Length
        Sha256 = Get-GhpSha256 $full
        Kind = $Kind; ReleaseTag = $Tag
        Url = "https://github.com/$Repository/releases/download/$Tag/$([Uri]::EscapeDataString($item.Name))"
    }
}

function Assert-GhpDeclaredAsset {
    param($Asset, $Declaration)
    $size = [long]0
    if ([string]$Declaration.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        -not [long]::TryParse([string]$Declaration.sizeBytes, [ref]$size) -or $size -le 0 -or $size -ge 2GB -or
        $size -ne $Asset.Size -or $Declaration.sha256 -ine $Asset.Sha256) {
        throw 'Declared asset SHA256 or size does not match the local file.'
    }
}

function Assert-GhpPublicZip {
    param([string]$Path, [ValidateRange(1L,8589934592L)][long]$MaximumExpandedBytes = 512MB)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    $zip = $null; $checking=''
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        if ($zip.Entries.Count -eq 0 -or $zip.Entries.Count -gt 10000) { throw 'Invalid entry count.' }
        $seen = @{}; $total = [long]0
        # A bounded guard, not a substitute for building from a clean public template.
        $secret = '(?i)-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----|<D>\s*[A-Za-z0-9+/=]{16}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|["''](?:serverpassword|password|token|secret|api[_-]?key)["'']\s*:\s*["''][^"''\s][^"'']{2,}["'']|\b(?:serverpassword|password|token|secret|api[_-]?key)\s*=\s*["''][^"''\s][^"'']{2,}["'']|<(?:serverpassword|password|token|secret)>\s*[^<\s][^<]{2,}</'
        $strongSecret='(?i)-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----|<D>\s*[A-Za-z0-9+/=]{16}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}'
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            $checking=$name
            $normalized = $name.Replace('\', '/')
            if ($name -ne $normalized -or $normalized.StartsWith('/') -or $normalized -match '[:\x00-\x1f]' -or
                $normalized -match '(^|/)\.\.?(/|$)' -or $seen.ContainsKey($normalized.TrimEnd('/')) -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                ($entry.ExternalAttributes -band 0x400)) { throw 'Unsafe ZIP entry.' }
            foreach ($component in $normalized.TrimEnd('/').Split('/')) {
                if ([string]::IsNullOrWhiteSpace($component) -or $component -match '[<>"|?*]' -or
                    $component.EndsWith('.') -or $component.EndsWith(' ') -or
                    $component -match '(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Ambiguous Windows ZIP entry.' }
            }
            if ($normalized.EndsWith('/') -and $entry.Length -ne 0) { throw 'Unsafe ZIP entry.' }
            $seen[$normalized.TrimEnd('/')] = $normalized.EndsWith('/')
            if ($normalized -match '(?i)(^|/)(secrets?|state|universe|saves|\.git|\.ssh|\.aws|\.azure)(/|$)' -or
                $normalized -match '(?i)(^|/)(private[^/]*|credentials?[^/]*|deployment\.json|\.env(?:\.[^/]*)?|[^/]*\.(?:key|pem|pfx|p12|kdbx))$' -or
                $normalized -match '(?i)(^|[/._ -])(?:privatekey|private-key|signing-private|credentials)(?:[./_ -]|$)' -or
                $normalized -match '(?i)(^|/)(?:APFState|server-state|gh-token)(?:[./_-]|$)') { throw 'Private ZIP entry.' }
            $total += $entry.Length
            if ($total -gt $MaximumExpandedBytes) {
                if ($MaximumExpandedBytes -eq 512MB) { throw 'Public ZIP exceeds the 512 MiB expanded safety bound.' }
                throw 'Public ZIP exceeds the media expanded safety bound.'
            }
            if ($normalized.EndsWith('/')) { continue }
            $stream = $entry.Open()
            try {
                $buffer = New-Object byte[] 65536
                $tail = ''; $readTotal = [long]0
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $readTotal += $read
                    if ($readTotal -gt $entry.Length) { throw 'ZIP entry length mismatch.' }
                    $text = $tail + [Text.Encoding]::UTF8.GetString($buffer, 0, $read).Replace([string][char]0, '')
                    # Localization dictionaries contain translated <Password>
                    # labels, not saved credentials. Still scan them for keys
                    # and recognizable tokens; do not exempt configuration.
                    $pattern=if($normalized -match '^GameData/LunaMultiplayer/Localization/'){ $strongSecret }else{ $secret }
                    if ($text -match $pattern) { throw 'Secret content in public ZIP.' }
                    $tail = $text.Substring([Math]::Max(0, $text.Length - 1024))
                }
                if ($readTotal -ne $entry.Length) { throw 'ZIP entry length mismatch.' }
            } finally { $stream.Dispose() }
        }
        foreach ($name in @($seen.Keys)) {
            $parent = $name
            while ($parent.Contains('/')) {
                $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
                if ($seen.ContainsKey($parent) -and -not $seen[$parent]) { throw 'Unsafe ZIP entry.' }
            }
        }
    } catch {
        $reason=$_.Exception.Message
        if($reason -notin @('Invalid entry count.','Unsafe ZIP entry.','Ambiguous Windows ZIP entry.','Private ZIP entry.','Public ZIP exceeds the 512 MiB expanded safety bound.','Public ZIP exceeds the media expanded safety bound.','ZIP entry length mismatch.','Secret content in public ZIP.')){$reason='Invalid ZIP.'}
        $label=([regex]::Replace($checking,'[\x00-\x1f]','?'));if($label.Length-gt 200){$label=$label.Substring(0,200)}
        throw "Public ZIP safety guard rejected archive ($reason Entry: $label)."
    }
    finally { if ($zip) { $zip.Dispose() } }
}

function Assert-GhpExactFields($Value, [string[]]$Fields) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject] -or
        @($Value.PSObject.Properties).Count -ne $Fields.Count -or
        @($Value.PSObject.Properties | Where-Object Name -cnotin $Fields).Count) { throw 'Unsupported media reference schema.' }
}

function Assert-GhpCatalogMedia($Catalog, [string]$Name, [long]$Size, [string]$Sha256) {
    $matchesFound = 0
    foreach ($profileName in @('full','potato')) {
        $profile = $Catalog.$profileName
        if ($null -eq $profile -or $null -eq $profile.managedPackages) { throw 'Missing signed catalog package list.' }
        foreach ($package in @($profile.managedPackages)) {
            $uri = $null
            if (-not [Uri]::TryCreate([string]$package.archiveUrl,[UriKind]::Absolute,[ref]$uri)) { throw 'Invalid catalog media URL.' }
            $publicUrl = "https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/download/pack-media/$Name"
            $packageName = [Uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
            if ($packageName -ine $Name) { continue }
            # Catalog URLs identify packages, never the transport to fetch. Only
            # the fixed public release URL constructed below is downloaded.
            if ($package.archiveUrl -isnot [string] -or $packageName -cne $Name -or $package.archiveUrl.Contains('%') -or
                ($uri.AbsolutePath -cne ('/'+$Name) -and $package.archiveUrl -cne $publicUrl) -or
                $uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
                (Get-GhpUrlName ([string]$package.archiveUrl)) -cne $Name -or
                $package.sha256 -isnot [string] -or $package.sha256 -cne $Sha256 -or
                ($package.sizeBytes -isnot [int] -and $package.sizeBytes -isnot [long]) -or $package.sizeBytes -ne $Size) {
                throw 'Media reference differs from a referring signed catalog package.'
            }
            $matchesFound++
        }
    }
    if (-not $matchesFound) { throw 'Media reference has no matching signed catalog package.' }
}

function Assert-GhpMediaReferences($Document, $Catalog, [string]$Version) {
    Assert-GhpExactFields $Document @('schemaVersion','applicationVersion','repository','tag','assets')
    if (($Document.schemaVersion -isnot [int] -and $Document.schemaVersion -isnot [long]) -or $Document.schemaVersion -ne 1 -or
        $Document.applicationVersion -isnot [string] -or $Document.applicationVersion -cne $Version -or
        $Version -cnotmatch '^kesas-rc[1-9][0-9]{0,14}$' -or $Catalog.version -isnot [string] -or $Catalog.version -cne $Version -or
        $Document.repository -isnot [string] -or $Document.repository -cne 'esotericmartyristaken-bit/AETHERFRONT' -or
        $Document.tag -isnot [string] -or $Document.tag -cne 'pack-media' -or
        $Catalog.gitHub.enabled -isnot [bool] -or -not $Catalog.gitHub.enabled -or
        $Catalog.gitHub.repository -isnot [string] -or $Catalog.gitHub.repository -cne $Document.repository -or
        $Catalog.gitHub.mediaTag -isnot [string] -or $Catalog.gitHub.mediaTag -cne $Document.tag -or
        $Document.assets -isnot [array] -or $Document.assets.Count -lt 1 -or $Document.assets.Count -gt 1000) {
        throw 'Unsupported media reference authority, version, or bounds.'
    }
    $seen = @{}; [long]$total = 0
    foreach ($asset in $Document.assets) {
        Assert-GhpExactFields $asset @('name','sizeBytes','sha256')
        if ($asset.name -isnot [string] -or $asset.name.Length -gt 200 -or
            $asset.name -cnotmatch '^kesas-[a-z0-9-]+-([a-f0-9]{64})\.zip$' -or $Matches[1] -cne $asset.sha256 -or
            $asset.sha256 -isnot [string] -or $asset.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
            ($asset.sizeBytes -isnot [int] -and $asset.sizeBytes -isnot [long]) -or
            $asset.sizeBytes -le 0 -or $asset.sizeBytes -ge 2GB -or $seen.ContainsKey($asset.name)) {
            throw 'Invalid or duplicate immutable media reference.'
        }
        Assert-GhpPublicName $asset.name
        $seen[$asset.name] = $true; $total += $asset.sizeBytes
        if ($total -gt 8GB) { throw 'Referenced media batch exceeds 8 GiB.' }
        Assert-GhpCatalogMedia $Catalog $asset.name $asset.sizeBytes $asset.sha256
    }
}

function Save-GhpReferencedMedia($Asset, [string]$Destination) {
    # No token, default credentials, cookies, arbitrary URL, or automatic
    # redirects. The entire body is bounded, including chunked responses.
    Assert-GhpExactFields $Asset @('name','sizeBytes','sha256')
    if ($Asset.name -isnot [string] -or $Asset.name.Length -gt 200 -or
        $Asset.name -cnotmatch '^kesas-[a-z0-9-]+-([a-f0-9]{64})\.zip$' -or $Matches[1] -cne $Asset.sha256 -or
        $Asset.sha256 -isnot [string] -or $Asset.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
        ($Asset.sizeBytes -isnot [int] -and $Asset.sizeBytes -isnot [long]) -or $Asset.sizeBytes -le 0 -or $Asset.sizeBytes -ge 2GB) {
        throw 'Invalid immutable media download bounds.'
    }
    Assert-GhpPublicName $Asset.name
    Add-Type -AssemblyName System.Net.Http
    $target = Assert-UpdatePath $Destination
    $url = "https://github.com/esotericmartyristaken-bit/AETHERFRONT/releases/download/pack-media/$($Asset.name)"
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false; $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Aetherfront-Media/1')
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(30))
    $current = [Uri]$url
    try {
        for ($hop = 0; $hop -le 5; $hop++) {
            if ($current.Scheme -cne 'https' -or $current.Port -ne 443 -or $current.UserInfo -or $current.Fragment -or
                $current.IdnHost -cnotin @('github.com','release-assets.githubusercontent.com','objects.githubusercontent.com','github-releases.githubusercontent.com') -or
                ($current.IdnHost -ceq 'github.com' -and $current.AbsoluteUri -cne $url)) { throw 'Untrusted media redirect.' }
            $response = $client.GetAsync($current,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
                    if ($hop -eq 5 -or $null -eq $response.Headers.Location) { throw 'Media redirect limit.' }
                    $current = [Uri]::new($current,$response.Headers.Location); continue
                }
                if ([int]$response.StatusCode -ne 200 -or
                    ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $Asset.sizeBytes)) { throw 'Media response mismatch.' }
                $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $outputStream = [IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                    try {
                        $buffer = [byte[]]::new(1MB); [long]$total = 0
                        while (($count = $inputStream.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                            if ($count -gt $Asset.sizeBytes-$total) { throw 'Media exceeds declared size.' }
                            $total += $count; $outputStream.Write($buffer,0,$count)
                        }
                        if ($total -ne $Asset.sizeBytes) { throw 'Truncated media.' }
                        $outputStream.Flush($true)
                    } finally { $outputStream.Dispose() }
                } finally { $inputStream.Dispose() }
                if ((Get-GhpSha256 $target) -cne $Asset.sha256) { throw 'Media SHA256 mismatch.' }
                return
            } finally { $response.Dispose() }
        }
    } catch { throw 'Public media download failed size/SHA256/transport verification; no media replaced.' }
    finally { $deadline.Dispose(); $client.Dispose() }
}

function Publish-GhpReferencedMedia([string]$CatalogPath, [string[]]$AssetPaths, [string]$OutputPath) {
    # Controller-only helper: uploads pack-media assets, never application
    # releases, deployment channels, signing keys, or existing asset contents.
    $catalogFile = Assert-GhpPath $CatalogPath
    $text = Read-UpdateText $catalogFile 1MB; Assert-ReleaseJsonUnique $text
    $catalog = $text | ConvertFrom-Json
    if ($OutputPath.StartsWith('\\')) { throw 'Reference output must be a local path.' }
    $output = Assert-UpdatePath $OutputPath
    if ([IO.Path]::GetFileName($output) -cne 'MediaReferences.json') { throw 'Output must be MediaReferences.json.' }
    $null = Assert-GhpPath ([IO.Path]::GetDirectoryName($output)) -Directory
    if ($AssetPaths.Count -lt 1 -or $AssetPaths.Count -gt 1000) { throw 'Invalid media asset count.' }
    $locks = [Collections.Generic.List[object]]::new()
    try {
        $assets = @(foreach ($path in $AssetPaths) {
            $full = Assert-GhpPath $path
            # Keep local bytes immutable across scan, hashing, and gh upload.
            $locks.Add([IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
            Get-GhpAsset $full 'media' 'pack-media' 'esotericmartyristaken-bit/AETHERFRONT'
        })
        $document = [pscustomobject][ordered]@{
            schemaVersion=1; applicationVersion=$catalog.version; repository='esotericmartyristaken-bit/AETHERFRONT'; tag='pack-media'
            assets=@(foreach ($asset in $assets) { [pscustomobject][ordered]@{name=$asset.Name;sizeBytes=$asset.Size;sha256=$asset.Sha256} })
        }
        Assert-GhpMediaReferences $document $catalog $catalog.version
        $json = $document | ConvertTo-Json -Depth 8
        if ([IO.File]::Exists($output) -and (Read-UpdateText $output 1MB).Trim() -cne $json.Trim()) { throw 'Reference output already exists with different intent.' }
        if ([IO.Directory]::Exists($output)) { throw 'Reference output is a directory.' }
        foreach ($asset in $assets) { Assert-GhpPublicZip $asset.Path -MaximumExpandedBytes 8GB }
        Assert-GhpRepository $document.repository
        $media = Ensure-GhpRelease $document.repository 'pack-media' $false
        if ($media.draft -or $media.prerelease) { throw 'pack-media must be a published stable media release.' }
        Sync-GhpAssets -Repository $document.repository -Release $media -Assets $assets
        if (-not [IO.File]::Exists($output)) {
            $temporary = $output+'.incoming-'+[Guid]::NewGuid().ToString('N')
            try {
                $stream = [IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $bytes=$UpdateUtf8.GetBytes($json+"`n"); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
                try { [IO.File]::Move($temporary,$output) } catch {
                    $null = Assert-UpdatePath $output
                    if (-not [IO.File]::Exists($output) -or (Read-UpdateText $output 1MB).Trim() -cne $json.Trim()) { throw }
                }
            } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        }
        if ((Read-UpdateText $output 1MB).Trim() -cne $json.Trim()) { throw 'Reference output changed during publication.' }
        return $document
    } finally { foreach ($handle in $locks) { $handle.Dispose() } }
}

function New-GitHubPublicationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [string]$MediaTag = 'pack-media',
        [Parameter(Mandatory)][string]$PublishedDirectory,
        [Parameter(Mandatory)][string]$BootstrapZip,
        [Parameter(Mandatory)][string]$CatalogPath,
        [Parameter(Mandatory)][string]$MediaDirectory
    )
    if ($Repository -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $Repository.EndsWith('.git') -or $MediaTag -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid GitHub repository or media tag.' }
    $published = Assert-GhpPath $PublishedDirectory -Directory
    $media = Assert-GhpPath $MediaDirectory -Directory
    $catalogFile = Assert-GhpPath $CatalogPath
    $bootstrap = Assert-GhpPath $BootstrapZip
    if ([IO.Path]::GetFileName($catalogFile) -cne 'install-catalog.json' -or $bootstrap -notmatch '(?i)\.zip$') { throw 'Unexpected catalog or bootstrap path.' }
    $feedAsset = Get-GhpAsset (Join-Path $published 'APFClientUpdate.json') 'feed' '' $Repository
    try {
        $feed = [IO.File]::ReadAllText($feedAsset.Path) | ConvertFrom-Json -ErrorAction Stop
        $catalog = [IO.File]::ReadAllText($catalogFile) | ConvertFrom-Json -ErrorAction Stop
    } catch { throw 'Invalid publication feed or catalog JSON.' }
    $version = [string]$feed.version
    if ($version -cnotmatch '^kesas-rc[1-9][0-9]*$' -or $version -ceq $MediaTag -or
        [string]::IsNullOrWhiteSpace([string]$feed.signature) -or $catalog.version -cne $version) { throw 'Invalid version, missing feed signature, or catalog version mismatch.' }
    $clientName = "APFClient-$version.zip"
    if ((Get-GhpUrlName ([string]$feed.packageUrl)) -cne $clientName) { throw 'Unexpected client archive name in feed.' }
    $clientAsset = Get-GhpAsset (Join-Path $published $clientName) 'client' $version $Repository
    Assert-GhpDeclaredAsset $clientAsset $feed
    Assert-GhpPublicZip $clientAsset.Path
    Assert-GhpPublicZip $bootstrap
    # The native caller verifies the signed feed first. Its real inventory is
    # authoritative: a bootstrap must not silently ship an older client.
    if($feed.PSObject.Properties['files'] -and @($feed.files).Count -gt 0){
        $catalogRecord=@($feed.files|Where-Object path -ceq 'GameData/LunaMultiplayer/Launcher/install-catalog.json')
        if($catalogRecord.Count-ne 1 -or (Get-FileHash -LiteralPath $catalogFile).Hash -ine $catalogRecord[0].sha256 -or ([IO.FileInfo]$catalogFile).Length -ne $catalogRecord[0].sizeBytes){throw 'Publication catalog differs from the signed client inventory.'}
        $bootstrapArchive=[IO.Compression.ZipFile]::OpenRead($bootstrap)
        try{
            foreach($file in $feed.files){
                $entry=$bootstrapArchive.GetEntry([string]$file.path)
                if(-not $entry -or $entry.Length -ne $file.sizeBytes){throw 'Bootstrap does not match the signed client inventory.'}
                $hash=[Security.Cryptography.SHA256]::Create();$stream=$entry.Open()
                try{$actual=[BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$hash.Dispose()}
                if($actual -cne $file.sha256){throw 'Bootstrap does not match the signed client inventory.'}
            }
        }finally{$bootstrapArchive.Dispose()}
    }
    $feedAsset.ReleaseTag = $version
    $feedAsset.Url = "https://github.com/$Repository/releases/download/$version/APFClientUpdate.json"
    $bootstrapAsset = Get-GhpAsset $bootstrap 'bootstrap' $version $Repository
    $releaseAssets = @($feedAsset, $clientAsset, $bootstrapAsset)
    if (@($releaseAssets.Name | Select-Object -Unique).Count -ne 3) { throw 'Colliding version asset names.' }
    $declared = @{}
    foreach ($profileName in @('full', 'potato')) {
        $profile = $catalog.$profileName
        if (-not $profile -or $null -eq $profile.managedPackages) { throw 'Catalog must include Full and Potato package lists.' }
        # ClientOnly entries are validation rules, not downloadable packages.
        foreach ($package in @($profile.managedPackages)) {
            if ($null -eq $package) { continue }
            $name = Get-GhpUrlName ([string]$package.archiveUrl)
            if ($declared.ContainsKey($name)) {
                $prior = $declared[$name]
                if ($prior.Name -cne $name -or $prior.Package.sha256 -ine $package.sha256 -or
                    [string]$prior.Package.sizeBytes -cne [string]$package.sizeBytes) { throw 'Conflicting catalog declarations for one media filename.' }
            } else { $declared[$name] = @{ Name = $name; Package = $package } }
        }
    }
    if ($declared.Count -gt 1000) { throw 'Media release exceeds 1000 assets.' }
    $mediaAssets = @(foreach ($record in @($declared.Values | Sort-Object Name)) {
        $asset = Get-GhpAsset (Join-Path $media $record.Name) 'media' $MediaTag $Repository
        Assert-GhpDeclaredAsset $asset $record.Package
        $asset
    })
    [pscustomobject]@{
        Repository = $Repository; MediaTag = $MediaTag; Version = $version
        PublishedDirectory = $published; BootstrapZip = $bootstrap; CatalogPath = $catalogFile; MediaDirectory = $media
        Assets = @($mediaAssets) + $releaseAssets; MediaAssets = $mediaAssets; ReleaseAssets = $releaseAssets
    }
}

function Assert-GhpPlan {
    param($Plan, [switch]$SnapshotOnly)
    if ($Plan.Repository -cnotmatch '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $Plan.Repository.EndsWith('.git') -or $Plan.MediaTag -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        $Plan.Version -cnotmatch '^kesas-rc[1-9][0-9]*$' -or $Plan.MediaTag -ceq $Plan.Version -or
        @($Plan.MediaAssets).Count -gt 1000 -or @($Plan.ReleaseAssets).Count -ne 3) { throw 'Invalid publication plan snapshot.' }
    $names = @{}
    foreach ($asset in @($Plan.MediaAssets) + @($Plan.ReleaseAssets)) {
        Assert-GhpPublicName ([string]$asset.Name)
        $size = [long]0
        if (-not [long]::TryParse([string]$asset.Size, [ref]$size) -or $size -le 0 -or $size -ge 2GB -or
            $asset.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid publication asset snapshot.' }
        $key = "$($asset.ReleaseTag)/$($asset.Name)"
        if ($names.ContainsKey($key)) { throw 'Duplicate publication asset snapshot.' }
        $names[$key] = $true
    }
    foreach ($asset in @($Plan.MediaAssets)) {
        if ($asset.Kind -cne 'media' -or $asset.ReleaseTag -cne $Plan.MediaTag -or $asset.Name -notmatch '(?i)\.zip$') { throw 'Invalid media asset snapshot.' }
    }
    foreach ($kind in @('feed', 'client', 'bootstrap')) {
        $selected = @($Plan.ReleaseAssets | Where-Object { $_.Kind -ceq $kind })
        if ($selected.Count -ne 1 -or $selected[0].ReleaseTag -cne $Plan.Version) { throw 'Invalid version asset snapshot.' }
        if (($kind -eq 'feed' -and $selected[0].Name -cne 'APFClientUpdate.json') -or
            ($kind -eq 'client' -and $selected[0].Name -cne "APFClient-$($Plan.Version).zip") -or
            ($kind -eq 'bootstrap' -and $selected[0].Name -notmatch '(?i)\.zip$')) { throw 'Unexpected version asset name.' }
    }
    if (($Plan.Assets | ConvertTo-Json -Depth 8 -Compress) -cne (@(@($Plan.MediaAssets) + @($Plan.ReleaseAssets)) | ConvertTo-Json -Depth 8 -Compress)) { throw 'Inconsistent publication asset snapshot.' }
    # Completion happens after the native caller moves staging to immutable storage.
    # It MUST NOT read any local file, including feed/catalog/bootstrap paths.
    if ($SnapshotOnly) { return $Plan }
    # Rebuild before every mutation phase: no stale files or caller-injected paths.
    $fresh = New-GitHubPublicationPlan -Repository $Plan.Repository -MediaTag $Plan.MediaTag -PublishedDirectory $Plan.PublishedDirectory -BootstrapZip $Plan.BootstrapZip -CatalogPath $Plan.CatalogPath -MediaDirectory $Plan.MediaDirectory
    foreach ($property in @('Version', 'Assets', 'MediaAssets', 'ReleaseAssets')) {
        if (($Plan.$property | ConvertTo-Json -Depth 8 -Compress) -cne ($fresh.$property | ConvertTo-Json -Depth 8 -Compress)) { throw 'Publication plan changed; rebuild and reverify the signed feed.' }
    }
    return $fresh
}

function Invoke-GhpApi {
    param([string]$Repository, [string]$Suffix = '', [string]$Method = 'GET', [string[]]$Fields = @())
    $arguments = @('api', "repos/$Repository$Suffix", '--hostname', 'github.com', '--method', $Method,
        '--header', 'Accept: application/vnd.github+json', '--header', 'X-GitHub-Api-Version: 2022-11-28') + $Fields
    try { return (Invoke-GitHubCli -Arguments $arguments -Context "API $Method publication metadata" | ConvertFrom-Json -ErrorAction Stop) }
    catch { throw "GitHub publication metadata request failed ($Method). Check local gh access and retry." }
}

function Get-GhpList {
    param([string]$Repository, [string]$Suffix)
    for ($page = 1; $page -le 100; $page++) {
        $items = @(Invoke-GhpApi $Repository "$($Suffix)?per_page=100&page=$page")
        foreach ($item in $items) { if ($null -ne $item) { $item } }
        if ($items.Count -lt 100) { return }
    }
    throw 'GitHub metadata pagination exceeded the safety bound.'
}

function Assert-GhpRepository {
    param([string]$Repository)
    $repo = Invoke-GhpApi $Repository
    if ($repo.full_name -ine $Repository -or $repo.private -isnot [bool] -or $repo.private -or
        $repo.visibility -cne 'public' -or $repo.permissions.push -isnot [bool] -or -not $repo.permissions.push) {
        throw 'Publication requires a public repository and local gh push permission.'
    }
}

function Find-GhpRelease {
    param([string]$Repository, [string]$Tag)
    $found = @(Get-GhpList $Repository '/releases' | Where-Object { $_.tag_name -ceq $Tag })
    if ($found.Count -gt 1) { throw 'Ambiguous GitHub release tag.' }
    if ($found.Count -eq 1) {
        $release = $found[0]
        if ([string]$release.id -notmatch '^[1-9][0-9]*$' -or $release.draft -isnot [bool] -or $release.prerelease -isnot [bool]) { throw 'Invalid GitHub release metadata.' }
        return $release
    }
}

function Get-GhpReleaseAssets {
    param([string]$Repository, $Release)
    $assets = @(Get-GhpList $Repository "/releases/$($Release.id)/assets")
    $seen = @{}
    foreach ($asset in $assets) {
        Assert-GhpPublicName ([string]$asset.name)
        if ($seen.ContainsKey([string]$asset.name)) { throw 'Duplicate remote asset names.' }
        $seen[[string]$asset.name] = $true
    }
    if ($assets.Count -gt 1000) { throw 'Remote release exceeds 1000 assets.' }
    return $assets
}

function Get-GhpLatestRelease {
    param([string]$Repository)
    try {
        $response = Invoke-GitHubCli -Arguments @('api', "repos/$Repository/releases/latest", '--hostname', 'github.com', '--method', 'GET',
            '--header', 'Accept: application/vnd.github+json', '--header', 'X-GitHub-Api-Version: 2022-11-28', '--include') -Context 'latest publication metadata' -AllowNotFound
        $http = [regex]::Match($response, '\AHTTP/[0-9.]+ (?<status>200|404)(?: [^\r\n]*)?\r?\n(?:[^\r\n]+\r?\n)*\r?\n(?<body>[\s\S]*)\z')
        if (-not $http.Success) { throw 'Invalid HTTP metadata.' }
        if ($http.Groups['status'].Value -ceq '404') { return $null }
        $latest = $http.Groups['body'].Value | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $latest -or $latest -is [array] -or [string]$latest.id -notmatch '^[1-9][0-9]*$' -or
            [string]::IsNullOrWhiteSpace([string]$latest.tag_name) -or $latest.draft -isnot [bool] -or $latest.draft -or
            $latest.prerelease -isnot [bool] -or $latest.prerelease) { throw 'Invalid latest metadata.' }
        return $latest
    } catch { throw 'GitHub latest publication metadata request failed (GET). Confirm readback before any publication write.' }
}

function Assert-GhpLatestOrder {
    param($Plan, $Media, $Release, $Latest)
    if ($null -eq $Latest) { return }
    if ($Latest.tag_name -ceq $Plan.Version) {
        if ($Latest.id -ne $Release.id) { throw 'Conflicting latest release identity; no publication write is safe.' }
        return
    }
    if ($Latest.id -eq $Release.id) { throw 'Conflicting latest release tag; no publication write is safe.' }
    # The initial non-latest media release may be the only published release.
    if ($Latest.tag_name -ceq $Plan.MediaTag -and $Latest.id -eq $Media.id) { return }
    if ($Latest.tag_name -cnotmatch '^kesas-rc[1-9][0-9]*$') { throw 'Conflicting latest release tag; no publication write is safe.' }
    $current = $Latest.tag_name.Substring(8); $target = $Plan.Version.Substring(8)
    if ($current.Length -gt $target.Length -or
        ($current.Length -eq $target.Length -and [string]::CompareOrdinal($current, $target) -ge 0)) {
        throw 'Newer or conflicting latest release; refusing to demote it.'
    }
}

function Assert-GhpRemoteAsset {
    param($Expected, $Actual)
    if ($null -eq $Actual) { throw 'Required release asset is missing. Run preparation before completing publication.' }
    if ($Actual.name -cne $Expected.Name -or $Actual.size -ne $Expected.Size -or
        $Actual.digest -ine "sha256:$($Expected.Sha256)" -or $Actual.state -cne 'uploaded') {
        throw 'Immutable asset name/size/SHA256 mismatch or missing digest. Use a new release version (or content-addressed media filename); no assets were overwritten.'
    }
}

function Test-GhpAssetSet {
    param([object[]]$Expected, [object[]]$Actual, [switch]$Exact, [switch]$RequireAll)
    if ($Exact) {
        foreach ($asset in $Actual) {
            if (@($Expected | Where-Object { $_.Name -ceq $asset.name }).Count -ne 1) { throw 'Unexpected asset in version release.' }
        }
    } else {
        foreach ($asset in $Actual) { if ($asset.name -notmatch '(?i)\.zip$') { throw 'Unexpected non-ZIP asset in media release.' } }
    }
    $missing = @()
    foreach ($asset in $Expected) {
        $match = @($Actual | Where-Object { $_.name -ieq $asset.Name })
        if ($match.Count -eq 0) {
            if ($RequireAll) { Assert-GhpRemoteAsset $asset $null }
            $missing += $asset
        } else { Assert-GhpRemoteAsset $asset $match[0] }
    }
    if ($Actual.Count + $missing.Count -gt 1000) { throw 'Release would exceed 1000 assets.' }
    return $missing
}

function Ensure-GhpRelease {
    param([string]$Repository, [string]$Tag, [bool]$Draft)
    $release = Find-GhpRelease $Repository $Tag
    if (-not $release) {
        try {
            $null = Invoke-GhpApi $Repository '/releases' 'POST' @('--raw-field', "tag_name=$Tag", '--raw-field', "name=$Tag",
                '--field', "draft=$($Draft.ToString().ToLowerInvariant())", '--field', 'prerelease=false', '--raw-field', 'make_latest=false')
        } catch { } # A timed-out create may have succeeded. Read back; never create twice blindly.
        # List endpoints can briefly lag a successful create. Repeat reads only;
        # never repeat the write when its outcome is uncertain.
        foreach($delay in @(0,1,2,4)){
            if($delay){Start-Sleep -Seconds $delay}
            $release = Find-GhpRelease $Repository $Tag
            if($release){break}
        }
        if (-not $release) { throw 'Could not create or recover the publication release. Retry safely.' }
    }
    return $release
}

function Sync-GhpAssets {
    param([string]$Repository, $Release, [object[]]$Assets, [switch]$Exact)
    $remote = @(Get-GhpReleaseAssets $Repository $Release)
    $missing = @(Test-GhpAssetSet -Expected $Assets -Actual $remote -Exact:$Exact)
    if ($Exact -and -not $Release.draft -and $missing.Count) { throw 'Published version release is incomplete; refusing to append assets.' }
    foreach ($asset in $missing) {
        $current = Get-GhpAsset $asset.Path $asset.Kind $asset.ReleaseTag $Repository
        if ($current.Size -ne $asset.Size -or $current.Sha256 -cne $asset.Sha256) { throw 'Local publication asset changed before upload; rebuild the plan.' }
        # One exact path per command. No globbing, batching, labels, clobber, or token arguments.
        try { $null = Invoke-GitHubCli -Arguments @('release', 'upload', $Release.tag_name, $asset.Path, '--repo', "github.com/$Repository") -Context 'sequential release asset upload' }
        catch { } # Resolve unknown upload outcome by digest readback.
        $remote = @(Get-GhpReleaseAssets $Repository $Release)
        $match = @($remote | Where-Object { $_.name -ceq $asset.Name })
        if ($match.Count -ne 1) { throw 'Release asset upload did not complete. Retry safely; no assets were overwritten.' }
        Assert-GhpRemoteAsset $asset $match[0]
    }
    $null = Test-GhpAssetSet -Expected $Assets -Actual @(Get-GhpReleaseAssets $Repository $Release) -Exact:$Exact -RequireAll
}

function Sync-GhpMediaRelease {
    param($plan)
    $media = Ensure-GhpRelease $plan.Repository $plan.MediaTag $false
    Sync-GhpAssets -Repository $plan.Repository -Release $media -Assets $plan.MediaAssets
    try { $null = Invoke-GhpApi $plan.Repository "/releases/$($media.id)" 'PATCH' @('--field', 'draft=false', '--field', 'prerelease=false', '--raw-field', 'make_latest=false') }
    catch { throw 'Could not mark the media release published and non-latest. Retry preparation safely.' }
    $media = Find-GhpRelease $plan.Repository $plan.MediaTag
    if (-not $media -or $media.draft -or $media.prerelease) { throw 'Media release is not published.' }
    return $media
}

function Prepare-GitHubMedia {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    $plan=Assert-GhpPlan $Plan
    Assert-GhpRepository $plan.Repository
    $media=Sync-GhpMediaRelease $plan
    [pscustomobject]@{Repository=$plan.Repository;MediaTag=$plan.MediaTag;MediaReleaseId=$media.id;Assets=@($plan.MediaAssets).Count;VersionReleaseCreated=$false}
}

function Prepare-GitHubPublication {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    $plan = Assert-GhpPlan $Plan
    Assert-GhpRepository $plan.Repository
    $media=Sync-GhpMediaRelease $plan
    $release = Ensure-GhpRelease $plan.Repository $plan.Version $true
    Sync-GhpAssets -Repository $plan.Repository -Release $release -Assets $plan.ReleaseAssets -Exact
    [pscustomobject]@{ Repository = $plan.Repository; Version = $plan.Version; MediaReleaseId = $media.id; ReleaseId = $release.id; Draft = $release.draft }
}

function Complete-GitHubPublication {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan)
    $plan = Assert-GhpPlan $Plan -SnapshotOnly
    Assert-GhpRepository $plan.Repository
    $media = Find-GhpRelease $plan.Repository $plan.MediaTag
    $release = Find-GhpRelease $plan.Repository $plan.Version
    if (-not $media -or $media.draft -or $media.prerelease -or -not $release) { throw 'Publication has not been prepared.' }
    $null = Test-GhpAssetSet -Expected $plan.MediaAssets -Actual @(Get-GhpReleaseAssets $plan.Repository $media) -RequireAll
    $null = Test-GhpAssetSet -Expected $plan.ReleaseAssets -Actual @(Get-GhpReleaseAssets $plan.Repository $release) -Exact -RequireAll
    $latest = Get-GhpLatestRelease $plan.Repository
    Assert-GhpLatestOrder $plan $media $release $latest
    $result = [pscustomobject]@{ Repository = $plan.Repository; Version = $plan.Version; ReleaseId = $release.id; Published = $true; Url = "https://github.com/$($plan.Repository)/releases/tag/$($plan.Version)" }
    if (-not $release.draft) {
        # A published target may be an earlier uncertain success. Never replay
        # its PATCH, even if latest metadata has not caught up or has changed.
        if ($release.prerelease -or $null -eq $latest -or $latest.tag_name -cne $plan.Version -or $latest.id -ne $release.id) {
            throw 'Publication is not confirmed as the latest stable version. Preserve completion state and retry readback.'
        }
        return $result
    }
    if ($release.prerelease -or ($null -ne $latest -and $latest.id -eq $release.id)) { throw 'Conflicting prepared draft publication state; no publication write is safe.' }
    if ($null -eq $latest -or $latest.tag_name -ceq $plan.MediaTag) {
        # Confirm a first version release, not an unknown latest read or a
        # disappeared channel with an already-published stable version.
        $stable = @(Get-GhpList $plan.Repository '/releases' | Where-Object {
            -not $_.draft -and -not $_.prerelease -and -not ($_.tag_name -ceq $plan.MediaTag -and $_.id -eq $media.id)
        })
        if ($stable.Count) { throw 'Latest stable publication is not confirmed despite published versions; no publication write is safe.' }
    }
    try {
        $null = Invoke-GhpApi $plan.Repository "/releases/$($release.id)" 'PATCH' @('--field', 'draft=false', '--field', 'prerelease=false', '--raw-field', 'make_latest=true')
    } catch { } # Always verify server state, including an unknown publish outcome.
    foreach ($delay in @(0,1,2,4)) {
        if ($delay) { Start-Sleep -Seconds $delay }
        try {
            $published = Find-GhpRelease $plan.Repository $plan.Version
            $latest = Get-GhpLatestRelease $plan.Repository
        } catch { continue } # Reads only: never repeat an uncertain PATCH.
        Assert-GhpLatestOrder $plan $media $release $latest
        if ($published -and $published.id -ne $release.id) { throw 'Conflicting published release identity; preserve completion state.' }
        if ($published -and -not $published.draft -and -not $published.prerelease -and $null -ne $latest -and
            $latest.tag_name -ceq $plan.Version -and $latest.id -eq $release.id) {
            try { $assets = @(Get-GhpReleaseAssets $plan.Repository $published) } catch { continue }
            $null = Test-GhpAssetSet -Expected $plan.ReleaseAssets -Actual $assets -Exact -RequireAll
            return $result
        }
    }
    throw 'Publication is not confirmed as the latest stable version. Preserve completion state and retry readback.'
}
