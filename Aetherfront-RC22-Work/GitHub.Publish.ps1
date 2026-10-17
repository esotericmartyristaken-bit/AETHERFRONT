# Dot-source only. The native caller MUST verify the feed signature before planning.
# Only gh's existing local authentication is used. Public download URLs need no token.

function Invoke-GitHubCli {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$Context)
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $output = & 'C:\Program Files\GitHub CLI\gh.exe' @Arguments 2>&1
        $code = $LASTEXITCODE
    } catch { throw "GitHub CLI failed ($Context). Check local gh installation and authentication." }
    # Never include CLI output, arguments, environment, or credentials in an error.
    if ($code -ne 0) { throw "GitHub CLI failed ($Context). Check local gh access and retry." }
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

function Get-GhpAsset {
    param([string]$Path, [string]$Kind, [string]$Tag, [string]$Repository)
    $full = Assert-GhpPath $Path
    $item = Get-Item -LiteralPath $full -ErrorAction Stop
    Assert-GhpPublicName $item.Name
    if ($item.Length -le 0 -or $item.Length -ge 2GB) { throw 'Public asset must be nonempty and smaller than 2 GiB.' }
    [pscustomobject]@{
        Path = $full; Name = $item.Name; Size = [long]$item.Length
        Sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
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
    param([string]$Path)
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
                $normalized -match '(^|/)\.\.?(/|$)' -or $seen.ContainsKey($normalized) -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                ($entry.ExternalAttributes -band 0x400)) { throw 'Unsafe ZIP entry.' }
            foreach ($component in $normalized.TrimEnd('/').Split('/')) {
                if ([string]::IsNullOrWhiteSpace($component) -or $component -match '[<>"|?*]' -or
                    $component.EndsWith('.') -or $component.EndsWith(' ') -or
                    $component -match '(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Ambiguous Windows ZIP entry.' }
            }
            $seen[$normalized] = $true
            if ($normalized -match '(?i)(^|/)(secrets?|state|universe|saves|\.git|\.ssh|\.aws|\.azure)(/|$)' -or
                $normalized -match '(?i)(^|/)(private[^/]*|credentials?[^/]*|deployment\.json|\.env(?:\.[^/]*)?|[^/]*\.(?:key|pem|pfx|p12|kdbx))$' -or
                $normalized -match '(?i)(^|[/._ -])(?:privatekey|private-key|signing-private|credentials)(?:[./_ -]|$)' -or
                $normalized -match '(?i)(^|/)(?:APFState|server-state|gh-token)(?:[./_-]|$)') { throw 'Private ZIP entry.' }
            $total += $entry.Length
            if ($total -gt 512MB) { throw 'Public ZIP exceeds the 512 MiB expanded safety bound.' }
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
    } catch {
        $reason=$_.Exception.Message
        if($reason -notin @('Invalid entry count.','Unsafe ZIP entry.','Ambiguous Windows ZIP entry.','Private ZIP entry.','Public ZIP exceeds the 512 MiB expanded safety bound.','ZIP entry length mismatch.','Secret content in public ZIP.')){$reason='Invalid ZIP.'}
        $label=([regex]::Replace($checking,'[\x00-\x1f]','?'));if($label.Length-gt 200){$label=$label.Substring(0,200)}
        throw "Public ZIP safety guard rejected archive ($reason Entry: $label)."
    }
    finally { if ($zip) { $zip.Dispose() } }
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
        $release = Find-GhpRelease $Repository $Tag
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
    try {
        $null = Invoke-GhpApi $plan.Repository "/releases/$($release.id)" 'PATCH' @('--field', 'draft=false', '--field', 'prerelease=false', '--raw-field', 'make_latest=true')
    } catch { } # Always verify server state, including an unknown publish outcome.
    $published = Find-GhpRelease $plan.Repository $plan.Version
    $latest = Invoke-GhpApi $plan.Repository '/releases/latest'
    if (-not $published -or $published.draft -or $published.prerelease -or $published.id -ne $release.id -or
        $latest.tag_name -cne $plan.Version -or $latest.id -ne $release.id -or $latest.draft -or $latest.prerelease) {
        throw 'Publication is not confirmed as the latest stable version. Retry completion safely.'
    }
    $null = Test-GhpAssetSet -Expected $plan.ReleaseAssets -Actual @(Get-GhpReleaseAssets $plan.Repository $published) -Exact -RequireAll
    [pscustomobject]@{ Repository = $plan.Repository; Version = $plan.Version; ReleaseId = $release.id; Published = $true; Url = "https://github.com/$($plan.Repository)/releases/tag/$($plan.Version)" }
}
