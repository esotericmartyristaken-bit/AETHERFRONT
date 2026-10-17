[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'GitHub.Publish.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$script:Passed = 0
$script:Failures = [Collections.Generic.List[string]]::new()
$testRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'APF-GitHubPublisher-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($testRoot)
$script:FixtureNumber = 0

function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $message = $null
    try { $null = & $Action } catch { $message = $_.Exception.Message }
    Assert-Test ($null -ne $message) 'Expected rejection.'
    Assert-Test ($message -match $Pattern) "Unexpected rejection: $message"
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:Passed++; Write-Output "PASS $Name" }
    catch { $script:Failures.Add("$Name : $($_.Exception.Message) at $($_.ScriptStackTrace)"); Write-Output "FAIL $Name : $($_.Exception.Message)" }
}
function Write-TestJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30), [Text.UTF8Encoding]::new($false))
}
function Write-TestZip([string]$Path, [hashtable]$Entries) {
    $file = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
        foreach ($name in $Entries.Keys) {
            $stream = $zip.CreateEntry($name).Open()
            try { $bytes = [Text.Encoding]::UTF8.GetBytes([string]$Entries[$name]); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
    } finally { if ($zip) { $zip.Dispose() }; $file.Dispose() }
}
function New-Fixture {
    $script:FixtureNumber++
    $root = Join-Path $testRoot "case $script:FixtureNumber"
    $published = Join-Path $root 'native signed stage'
    $media = Join-Path $root 'Server\Config'
    $null = [IO.Directory]::CreateDirectory($published)
    $null = [IO.Directory]::CreateDirectory($media)
    $packages = @(foreach ($name in @('core pack.zip', 'full visual.zip', 'potato only.zip')) {
        $path = Join-Path $media $name
        Write-TestZip $path @{ 'GameData/Fixture/readme.txt' = $name }
        [pscustomobject]@{ archiveUrl = "https://github.com/test/public/releases/download/pack-media/$([Uri]::EscapeDataString($name))"; sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant(); sizeBytes = ([IO.FileInfo]$path).Length }
    })
    $archive = Join-Path $published 'APFClient-kesas-rc22.zip'
    Write-TestZip $archive @{ 'GameData/LunaMultiplayer/Data/APFClient.json' = '{"clientVersion":"kesas-rc22"}' }
    $feed = [pscustomobject]@{ version = 'kesas-rc22'; packageUrl = 'https://github.com/test/public/releases/download/kesas-rc22/APFClient-kesas-rc22.zip'; sha256 = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant(); sizeBytes = ([IO.FileInfo]$archive).Length; signature = 'offline-native-verification-fixture' }
    $catalog = [pscustomobject]@{ version = 'kesas-rc22'; full = [pscustomobject]@{ managedPackages = @($packages[0], $packages[1]); clientOnly = @() }; potato = [pscustomobject]@{ managedPackages = @($packages[0], $packages[2]); clientOnly = @([pscustomobject]@{pathRoots=@('OptionalGraphics');optional=$true}) } }
    $bootstrap = Join-Path $root 'Public Bootstrap.zip'
    Write-TestZip $bootstrap @{ 'Start client.cmd' = '@echo off'; 'GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml' = '<RSAKeyValue><Modulus>public</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>' }
    $catalogPath = Join-Path $root 'install-catalog.json'
    Write-TestJson (Join-Path $published 'APFClientUpdate.json') $feed
    Write-TestJson $catalogPath $catalog
    [pscustomobject]@{ Root = $root; Published = $published; Media = $media; Bootstrap = $bootstrap; CatalogPath = $catalogPath; Feed = $feed; Catalog = $catalog; Packages = $packages }
}
function New-TestPlan($Fixture) {
    New-GitHubPublicationPlan -Repository 'test/public' -MediaTag 'pack-media' -PublishedDirectory $Fixture.Published -BootstrapZip $Fixture.Bootstrap -CatalogPath $Fixture.CatalogPath -MediaDirectory $Fixture.Media
}
function Reset-Fake {
    $script:Fake = @{
        Releases = [Collections.ArrayList]::new(); Calls = [Collections.ArrayList]::new(); NextId = 1
        Public = $true; Push = $true; Latest = ''; Uploads = 0; Writes = 0
        CreateUnknown = $false; UploadUnknown = $false; PublishUnknown = $false
        UploadNoWrite = $false; PublishNoWrite = $false; WrongLatest = $false
        HideReadsPerCreate=0; HiddenReads=0; HiddenTag=''; Delays=[Collections.ArrayList]::new()
        PublishAttempts=0; LatestReadFailure=$false; LatestReadFailuresAfterPublish=0
        HideReadsAfterPublish=0; StalePublishReads=0; PreviousLatest=''; LatestOverride=$null; LatestRawResponse=$null
    }
}
function Get-FakePage($Items, [int]$Page) { return @($Items | Select-Object -Skip (($Page - 1) * 100) -First 100) }
function Start-Sleep {
    param([int]$Seconds)
    Assert-Test ($Seconds -in @(1,2,4)) 'Bounded metadata-only backoff.'
    $null=$script:Fake.Delays.Add($Seconds)
}

# This shadows the production shim before any publication function is called.
# Unknown calls fail closed. No gh process, REST request, authentication, or upload runs.
function Invoke-GitHubCli {
    param([string[]]$Arguments, [string]$Context, [switch]$AllowNotFound)
    $null = $script:Fake.Calls.Add($Arguments.Clone())
    if ($Arguments[0] -eq 'release') {
        Assert-Test ($Arguments.Count -eq 6 -and $Arguments[1] -eq 'upload' -and $Arguments[4] -eq '--repo' -and $Arguments[5] -eq 'github.com/test/public') 'One literal upload path, fixed public host.'
        if ($script:Fake.UploadNoWrite) { throw 'fixture-private-cli-output' }
        $release = @($script:Fake.Releases | Where-Object { $_.tag_name -ceq $Arguments[2] })[0]
        $path = $Arguments[3]
        Assert-Test ([IO.File]::Exists($path)) 'Upload path preserves spaces.'
        $null = $release.assets.Add([pscustomobject]@{ name = [IO.Path]::GetFileName($path); size = ([IO.FileInfo]$path).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant(); state = 'uploaded' })
        $script:Fake.Uploads++; $script:Fake.Writes++
        if ($script:Fake.UploadUnknown) { throw 'fixture-private-cli-output' }
        return ''
    }
    Assert-Test ($Arguments[0] -eq 'api' -and $Arguments -contains '--hostname' -and $Arguments -contains 'github.com') 'Only fixed-host API calls.'
    $endpoint = $Arguments[1]
    $method = $Arguments[[Array]::IndexOf($Arguments, '--method') + 1]
    $fields = @{}
    for ($i = 0; $i -lt $Arguments.Count - 1; $i++) {
        if ($Arguments[$i] -in @('--raw-field', '--field')) { $pair = $Arguments[$i + 1] -split '=', 2; $fields[$pair[0]] = $pair[1] }
    }
    if ($endpoint -ceq 'repos/test/public' -and $method -ceq 'GET') {
        return (ConvertTo-Json -InputObject @{ full_name = 'test/public'; private = -not $script:Fake.Public; visibility = $(if ($script:Fake.Public) { 'public' } else { 'private' }); permissions = @{ push = $script:Fake.Push } })
    }
    if ($endpoint -match '^repos/test/public/releases\?per_page=100&page=(\d+)$' -and $method -eq 'GET') {
        $page=[int]$Matches[1];$visible=$script:Fake.Releases
        if($script:Fake.HiddenReads -gt 0){$script:Fake.HiddenReads--;$visible=@($visible|Where-Object tag_name -cne $script:Fake.HiddenTag)}
        if($script:Fake.StalePublishReads -gt 0){$visible=@($visible|ForEach-Object{if($_.tag_name -ceq 'kesas-rc22'){$stale=$_|Select-Object *;$stale.draft=$true;$stale}else{$_}})}
        return (ConvertTo-Json -InputObject @(Get-FakePage $visible $page) -Depth 8)
    }
    if ($endpoint -ceq 'repos/test/public/releases' -and $method -eq 'POST') {
        Assert-Test ($fields.make_latest -ceq 'false') 'New releases never automatically latest.'
        $release = [pscustomobject]@{ id = $script:Fake.NextId; tag_name = $fields.tag_name; name = $fields.name; draft = ($fields.draft -eq 'true'); prerelease = ($fields.prerelease -eq 'true'); assets = [Collections.ArrayList]::new() }
        $script:Fake.NextId++; $null = $script:Fake.Releases.Add($release); $script:Fake.Writes++
        $script:Fake.HiddenReads=$script:Fake.HideReadsPerCreate;$script:Fake.HiddenTag=$release.tag_name
        if ($script:Fake.CreateUnknown) { throw 'fixture-private-cli-output' }
        return (ConvertTo-Json -InputObject $release -Depth 8)
    }
    if ($endpoint -match '^repos/test/public/releases/(\d+)/assets\?per_page=100&page=(\d+)$' -and $method -eq 'GET') {
        $id = [int]$Matches[1]; $page = [int]$Matches[2]
        $release = @($script:Fake.Releases | Where-Object { $_.id -eq $id })[0]
        return (ConvertTo-Json -InputObject @(Get-FakePage $release.assets $page) -Depth 8)
    }
    if ($endpoint -match '^repos/test/public/releases/(\d+)$' -and $method -eq 'PATCH') {
        $id = [int]$Matches[1]
        $release = @($script:Fake.Releases | Where-Object { $_.id -eq $id })[0]
        if ($fields.make_latest -eq 'true') { $script:Fake.PublishAttempts++ }
        if ($fields.make_latest -eq 'true' -and $script:Fake.PublishNoWrite) { throw 'fixture-private-cli-output' }
        $release.draft = $fields.draft -eq 'true'; $release.prerelease = $fields.prerelease -eq 'true'; $script:Fake.Writes++
        if ($fields.make_latest -eq 'true') {
            $script:Fake.PreviousLatest=$script:Fake.Latest;$script:Fake.StalePublishReads=$script:Fake.HideReadsAfterPublish
            if (-not $script:Fake.WrongLatest) { $script:Fake.Latest = $release.tag_name }
            if ($script:Fake.PublishUnknown) { throw 'fixture-private-cli-output' }
        }
        return (ConvertTo-Json -InputObject $release -Depth 8)
    }
    if ($endpoint -ceq 'repos/test/public/releases/latest' -and $method -eq 'GET') {
        if ($script:Fake.LatestReadFailure) { throw 'fixture-private-cli-output' }
        if ($script:Fake.PublishAttempts -gt 0 -and $script:Fake.LatestReadFailuresAfterPublish -gt 0) { $script:Fake.LatestReadFailuresAfterPublish--; throw 'fixture-private-cli-output' }
        if ($null -ne $script:Fake.LatestRawResponse) { return $script:Fake.LatestRawResponse }
        $latest = @($script:Fake.Releases | Where-Object { $_.tag_name -ceq $script:Fake.Latest })
        if($script:Fake.StalePublishReads -gt 0){$script:Fake.StalePublishReads--;$latest=@($script:Fake.Releases|Where-Object tag_name -ceq $script:Fake.PreviousLatest)}
        if ($null -ne $script:Fake.LatestOverride) { $latest=@($script:Fake.LatestOverride) }
        if (-not $latest.Count) {
            if ($AllowNotFound -and $Arguments -contains '--include') { return "HTTP/2.0 404 Not Found`r`nContent-Type: application/json`r`n`r`n{`"message`":`"Not Found`"}" }
            throw 'fixture-private-cli-output'
        }
        if ($Arguments -contains '--include') { return ("HTTP/2.0 200 OK`r`nContent-Type: application/json`r`n`r`n" + (ConvertTo-Json -InputObject $latest[0] -Depth 8)) }
        return (ConvertTo-Json -InputObject $latest[0] -Depth 8)
    }
    throw 'Unexpected mock CLI invocation; real CLI is forbidden in tests.'
}
function Get-FakeRelease([string]$Tag) { return @($script:Fake.Releases | Where-Object { $_.tag_name -ceq $Tag })[0] }
function Add-FakeStable([string]$Tag) {
    $release=[pscustomobject]@{id=$script:Fake.NextId;tag_name=$Tag;draft=$false;prerelease=$false;assets=[Collections.ArrayList]::new()}
    $script:Fake.NextId++;$null=$script:Fake.Releases.Add($release);$script:Fake.Latest=$Tag
    return $release
}

try {
    Test-Case 'plan exact metadata, Full/Potato/clientOnly union, spaces, unrelated files excluded' {
        $f = New-Fixture
        [IO.File]::WriteAllText((Join-Path $f.Media 'universe.zip'), 'never publish')
        $p = New-TestPlan $f
        Assert-Test ($p.Version -ceq 'kesas-rc22' -and $p.MediaAssets.Count -eq 3 -and $p.Assets.Count -eq 6) 'Exact union.'
        foreach ($asset in $p.Assets) { Assert-Test ($asset.Size -eq ([IO.FileInfo]$asset.Path).Length -and $asset.Sha256 -ceq (Get-FileHash -LiteralPath $asset.Path).Hash.ToLowerInvariant()) 'Exact local metadata.' }
        Assert-Test (@($p.Assets | Where-Object Name -eq 'universe.zip').Count -eq 0) 'No directory-wide upload.'
    }
    foreach ($field in @('sha256', 'sizeBytes', 'signature', 'packageUrl')) {
        Test-Case "reject invalid feed $field" {
            $f = New-Fixture
            switch ($field) { sha256 { $f.Feed.sha256 = '0' * 64 } sizeBytes { $f.Feed.sizeBytes++ } signature { $f.Feed.signature = '' } packageUrl { $f.Feed.packageUrl = 'https://example.test/wrong.zip' } }
            Write-TestJson (Join-Path $f.Published 'APFClientUpdate.json') $f.Feed
            Assert-Throws { New-TestPlan $f } 'Declared|signature|Unexpected'
        }
    }
    foreach ($field in @('sha256', 'sizeBytes')) {
        Test-Case "reject catalog $field mismatch" {
            $f = New-Fixture
            if ($field -eq 'sha256') { $f.Packages[1].sha256 = 'a' * 64 } else { $f.Packages[1].sizeBytes++ }
            Write-TestJson $f.CatalogPath $f.Catalog
            Assert-Throws { New-TestPlan $f } 'Declared'
        }
    }
    Test-Case 'reject conflicting duplicate media declarations' {
        $f = New-Fixture; $duplicate = $f.Packages[0] | Select-Object *; $duplicate.sha256 = '0' * 64
        $f.Catalog.potato.managedPackages = @($duplicate); Write-TestJson $f.CatalogPath $f.Catalog
        Assert-Throws { New-TestPlan $f } 'Conflicting'
    }
    foreach ($url in @('https://example.test/%2e%2e/core.zip', 'https://example.test/core%2fpack.zip', 'https://user:pass@example.test/core.zip', 'https://example.test/core.zip?token=x', 'https://example.test/secret.key', 'https://example.test/universe.zip')) {
        Test-Case 'reject unsafe catalog URL or asset name' {
            $f = New-Fixture; $f.Packages[0].archiveUrl = $url; Write-TestJson $f.CatalogPath $f.Catalog
            Assert-Throws { New-TestPlan $f } 'Unsafe|Only ZIP'
        }
    }
    Test-Case 'reject 1001 catalog media assets before opening files' {
        $f = New-Fixture
        $f.Catalog.full.managedPackages = @(1..1001 | ForEach-Object { [pscustomobject]@{ archiveUrl = "https://example.test/pack-$_.zip"; sha256 = '0' * 64; sizeBytes = 1 } })
        $f.Catalog.potato.managedPackages = @(); $f.Catalog.potato.clientOnly = @(); Write-TestJson $f.CatalogPath $f.Catalog
        Assert-Throws { New-TestPlan $f } '1000'
    }
    Test-Case 'reject declared 2 GiB boundary' {
        Assert-Throws { Assert-GhpDeclaredAsset ([pscustomobject]@{ Size = 2GB; Sha256 = 'a' * 64 }) ([pscustomobject]@{ sizeBytes = 2GB; sha256 = 'a' * 64 }) } 'Declared'
    }
    foreach ($name in @('secrets/private.xml', 'GameData/LunaMultiplayer/Data/privatekey.txt', 'Universe/vessel.txt', 'state/runtime.json', 'saves/player.sfs', 'identity.key', '.env', '../escape.txt', '/absolute.txt', 'secrets./private.xml', 'readme/CON.txt', 'readme//file.txt', 'update-signing-private.xml')) {
        Test-Case 'reject bootstrap secret/state/traversal path' {
            $f = New-Fixture; Write-TestZip $f.Bootstrap @{ $name = 'fixture' }
            Assert-Throws { New-TestPlan $f } 'safety guard'
        }
    }
    foreach ($content in @((('x' * 65530) + '-----BEGIN PRIVATE KEY-----'), ('ghp_' + ('x' * 40)), '<RSAKeyValue><D>cHJpdmF0ZS1maXh0dXJlLW9ubHk=</D></RSAKeyValue>', '{"password":"fixture-not-public"}', 'password="fixture-not-public"', '<ServerPassword>fixture-not-public</ServerPassword>')) {
        Test-Case 'reject secret contents including block-boundary key' {
            $f = New-Fixture; Write-TestZip $f.Bootstrap @{ 'readme.txt' = $content }
            Assert-Throws { New-TestPlan $f } 'safety guard'
        }
    }
    Test-Case 'reject reparse ancestor directory' {
        $f = New-Fixture; $link = Join-Path $f.Root 'linked media'
        $null = New-Item -ItemType Junction -Path $link -Target $f.Media
        try { $f.Media = $link; Assert-Throws { New-TestPlan $f } 'reparse' }
        finally { [IO.Directory]::Delete($link) }
    }
    Test-Case 'prepare drafts, published non-latest media, sequential literal-path uploads, repeat skips' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake
        $prepared = Prepare-GitHubPublication $p
        Assert-Test ($prepared.Draft -and -not (Get-FakeRelease 'pack-media').draft -and $script:Fake.Uploads -eq 6 -and $script:Fake.Latest -eq '') 'Preparation must not publish version.'
        $null = Prepare-GitHubPublication $p
        Assert-Test ($script:Fake.Uploads -eq 6) 'Matching SHA256 assets skipped.'
    }
    foreach ($permission in @('Public', 'Push')) {
        Test-Case "reject missing $permission before external writes" {
            $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $script:Fake[$permission] = $false
            Assert-Throws { Prepare-GitHubPublication $p } 'public repository'
            Assert-Test ($script:Fake.Writes -eq 0) 'No writes without permission.'
        }
    }
    foreach ($problem in @('digest', 'missing digest', 'size', 'case')) {
        Test-Case "reject immutable media $problem mismatch" {
            $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
            $asset = (Get-FakeRelease 'pack-media').assets[0]
            switch ($problem) { digest { $asset.digest = 'sha256:' + ('0' * 64) } 'missing digest' { $asset.digest = $null } size { $asset.size++ } case { $asset.name = $asset.name.ToUpperInvariant() } }
            Assert-Throws { Prepare-GitHubPublication $p } 'Immutable.*new release version'
            Assert-Test ($script:Fake.Uploads -eq 6) 'No overwrite.'
        }
    }
    Test-Case 'reject unexpected version assets' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
        $null = (Get-FakeRelease 'kesas-rc22').assets.Add([pscustomobject]@{ name = 'extra.zip'; size = 1; digest = 'sha256:' + ('a' * 64); state = 'uploaded' })
        Assert-Throws { Complete-GitHubPublication $p } 'Unexpected asset'
        Assert-Test ((Get-FakeRelease 'kesas-rc22').draft) 'Rejected release remains draft.'
    }
    Test-Case 'paginated historical media preserved, existing second-page digest verified' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
        $release = Get-FakeRelease 'pack-media'; $original = @($release.assets); $release.assets.Clear()
        foreach ($i in 1..100) { $null = $release.assets.Add([pscustomobject]@{ name = "historical-$i.zip"; size = 1; digest = 'sha256:' + ('a' * 64); state = 'uploaded' }) }
        foreach ($asset in $original) { $null = $release.assets.Add($asset) }
        $null = Prepare-GitHubPublication $p
        Assert-Test ($script:Fake.Uploads -eq 6 -and $release.assets.Count -eq 103) 'Pagination does not duplicate uploads or remove history.'
        $original[0].digest = 'sha256:' + ('0' * 64)
        Assert-Throws { Complete-GitHubPublication $p } 'Immutable'
    }
    Test-Case 'unknown create/upload outcomes recovered without duplicate writes' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $script:Fake.CreateUnknown = $true; $script:Fake.UploadUnknown = $true
        $null = Prepare-GitHubPublication $p
        Assert-Test ($script:Fake.Releases.Count -eq 2 -and $script:Fake.Uploads -eq 6) 'Readback recovers uncertain operations.'
    }
    Test-Case 'failed upload safe error, no private CLI output' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $script:Fake.UploadNoWrite = $true
        Assert-Throws { Prepare-GitHubPublication $p } '^Release asset upload did not complete'
        Assert-Test ($script:Fake.Uploads -eq 0) 'No upload occurred.'
    }
    Test-Case 'completion needs preparation' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake
        Assert-Throws { Complete-GitHubPublication $p } 'not been prepared'
        Assert-Test ($script:Fake.Writes -eq 0) 'Completion cannot invent missing releases.'
    }
    Test-Case 'completion works after ALL local fixture paths move; uncertain publish and repeat recover' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
        $moved = $f.Root + ' immutable'; [IO.Directory]::Move($f.Root, $moved)
        try {
            $script:Fake.PublishUnknown = $true
            $done = Complete-GitHubPublication $p
            Assert-Test ($done.Published -and $script:Fake.Latest -ceq 'kesas-rc22' -and -not (Get-FakeRelease 'kesas-rc22').draft) 'Digest-only completion after staging relocation.'
            $null = Complete-GitHubPublication $p
            Assert-Test ($script:Fake.Uploads -eq 6) 'Idempotent completion never uploads.'
        } finally { [IO.Directory]::Move($moved, $f.Root) }
    }
    Test-Case 'completion refuses digest drift before publication' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
        (Get-FakeRelease 'kesas-rc22').assets[0].digest = 'sha256:' + ('0' * 64)
        $writes = $script:Fake.Writes
        Assert-Throws { Complete-GitHubPublication $p } 'Immutable'
        Assert-Test ($script:Fake.Writes -eq $writes) 'Verification precedes publish.'
    }
    Test-Case 'failed publication never reports success' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
        $script:Fake.PublishNoWrite = $true
        Assert-Throws { Complete-GitHubPublication $p } 'metadata request failed|not confirmed'
        Assert-Test ((Get-FakeRelease 'kesas-rc22').draft) 'Unpublished draft remains draft.'
    }
    Test-Case 'latest tag must match version and release ID' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p
        $script:Fake.Latest = 'pack-media'; $script:Fake.WrongLatest = $true
        Assert-Throws { Complete-GitHubPublication $p } 'not confirmed'
    }
    Test-Case 'prepare revalidates files; snapshot rejects caller-injected assets' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake
        [IO.File]::AppendAllText((Join-Path $f.Media 'core pack.zip'), 'changed')
        Assert-Throws { Prepare-GitHubPublication $p } 'Declared'
        Assert-Test ($script:Fake.Writes -eq 0) 'Changed files cannot reach GitHub.'
        $p.ReleaseAssets[0].Name = 'secrets.json'
        Assert-Throws { Complete-GitHubPublication $p } 'Unsafe'
    }
    Test-Case 'retry published tag with different bytes fails immutable, never clobbers' {
        $f = New-Fixture; $p = New-TestPlan $f; Reset-Fake; $null = Prepare-GitHubPublication $p; $null = Complete-GitHubPublication $p
        Write-TestZip $f.Bootstrap @{ 'readme.txt' = 'new public bytes, same version' }
        $newPlan = New-TestPlan $f
        Assert-Throws { Prepare-GitHubPublication $newPlan } 'Immutable.*new release version'
        Assert-Test ($script:Fake.Uploads -eq 6) 'Published bytes untouched.'
    }
    Test-Case 'localization labels are not credentials' {
        $f=New-Fixture
        Write-TestZip $f.Bootstrap @{'GameData/LunaMultiplayer/Localization/Czech/AdminWindowText.xml'='<Password>Heslo</Password>'}
        $null=New-TestPlan $f
    }
    Test-Case 'initial media preload never publishes or creates a client version' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake
        $done=Prepare-GitHubMedia $p
        Assert-Test ($done.Assets-eq 3 -and $script:Fake.Uploads-eq 3 -and @($script:Fake.Releases|Where-Object tag_name -ceq 'kesas-rc22').Count-eq 0 -and $script:Fake.Latest-eq '') 'Only immutable media is preloaded; active channel is unchanged.'
    }
    Test-Case 'localization still rejects recognizable private tokens' {
        $f=New-Fixture
        Write-TestZip $f.Bootstrap @{'GameData/LunaMultiplayer/Localization/Czech/AdminWindowText.xml'=('<Label>github_pat_'+('A'*30)+'</Label>')}
        Assert-Throws {New-TestPlan $f} 'safety guard'
    }
    Test-Case 'ClientOnly validation rules are never treated as package archives' {
        $f=New-Fixture;$p=New-TestPlan $f
        Assert-Test ($p.MediaAssets.Count-eq 3) 'Only the deduplicated ManagedPackages union is uploaded.'
    }
    Test-Case 'delayed release-list visibility retries reads without duplicate creation' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$script:Fake.HideReadsPerCreate=2
        $done=Prepare-GitHubPublication $p
        $creates=@($script:Fake.Calls|Where-Object {$_[0] -eq 'api' -and $_[1] -ceq 'repos/test/public/releases' -and $_ -contains 'POST'})
        Assert-Test ($done.Draft -and $creates.Count -eq 2 -and $script:Fake.Releases.Count -eq 2 -and $script:Fake.Uploads -eq 6 -and $script:Fake.Delays.Count -eq 4) 'One create per release; delayed reads recover; media and draft identities verified.'
    }
    Test-Case 'bounded missing release readback fails closed without another write' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$script:Fake.HideReadsPerCreate=20
        Assert-Throws {Prepare-GitHubPublication $p} 'Could not create or recover'
        Assert-Test ($script:Fake.Writes -eq 1 -and $script:Fake.Releases.Count -eq 1 -and $script:Fake.Uploads -eq 0 -and ($script:Fake.Delays -join ',') -ceq '1,2,4') 'Unknown create remains single; bounded reads only; no assets or native changes.'
    }
    Test-Case 'buffered SHA256 matches standard empty file hash' {
        $path=Join-Path $testRoot 'empty-hash.bin';[IO.File]::WriteAllBytes($path,[byte[]]@())
        Assert-Test ((Get-GhpSha256 $path) -ceq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) 'Empty SHA256 identical.'
    }
    Test-Case 'buffered SHA256 matches standard multi-buffer hash' {
        $path=Join-Path $testRoot 'large-hash.bin';$bytes=New-Object byte[] (3MB+17);([Random]::new(31337)).NextBytes($bytes);[IO.File]::WriteAllBytes($path,$bytes)
        Assert-Test ((Get-GhpSha256 $path) -ceq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()) 'Multi-buffer SHA256 identical.'
    }
    Test-Case 'buffered SHA256 detects same-size content mutation' {
        $path=Join-Path $testRoot 'changed-hash.bin';[IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3));$before=Get-GhpSha256 $path
        [IO.File]::WriteAllBytes($path,[byte[]]@(1,2,4));Assert-Test ((Get-GhpSha256 $path) -cne $before) 'No metadata-only hash reuse.'
    }
    Test-Case 'buffered SHA256 refuses an exclusively locked file' {
        $path=Join-Path $testRoot 'locked-hash.bin';[IO.File]::WriteAllBytes($path,[byte[]]@(1,2,3));$guard=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try{Assert-Throws {Get-GhpSha256 $path} 'used by another process|sharing violation'}finally{$guard.Dispose()}
    }
    Test-Case 'buffered SHA256 refuses a missing file' {
        Assert-Throws {Get-GhpSha256 (Join-Path $testRoot 'missing-hash.bin')} 'Could not find|cannot find|not found'
    }
    Test-Case 'unknown successful publication then retry never repeats PATCH' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p
        $script:Fake.PublishUnknown=$true;$script:Fake.LatestReadFailuresAfterPublish=4
        Assert-Throws {Complete-GitHubPublication $p} 'not confirmed'
        Assert-Test ($script:Fake.PublishAttempts -eq 1 -and -not (Get-FakeRelease 'kesas-rc22').draft -and ($script:Fake.Delays -join ',') -ceq '1,2,4') 'Unknown success performs one PATCH and bounded reads only.'
        $writes=$script:Fake.Writes;$done=@(Complete-GitHubPublication $p)
        Assert-Test ($done.Count -eq 1 -and $done[0].Published -and $script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 1) 'Exact completed publication returns one unchanged result with zero repeated writes.'
    }
    foreach($published in @($false,$true)){
        Test-Case "newer latest cannot be demoted by draft or completed retry published=$published" {
            $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p
            if($published){$null=Complete-GitHubPublication $p}
            $null=Add-FakeStable 'kesas-rc23';$writes=$script:Fake.Writes;$attempts=$script:Fake.PublishAttempts
            Assert-Throws {Complete-GitHubPublication $p} 'Newer or conflicting latest'
            Assert-Test ($script:Fake.Latest -ceq 'kesas-rc23' -and $script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq $attempts) 'Newer channel remains unchanged.'
        }
    }
    foreach($older in @('kesas-rc9','kesas-rc21')){
        Test-Case "prepared draft safely advances older stable $older" {
            $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Add-FakeStable $older
            $done=Complete-GitHubPublication $p
            Assert-Test ($done.Published -and $script:Fake.Latest -ceq 'kesas-rc22' -and $script:Fake.PublishAttempts -eq 1) 'Numeric version ordering permits one forward publication.'
        }
    }
    Test-Case 'explicit no-latest HTTP 404 allows first prepared version' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p
        Assert-Test ($null -eq (Get-GhpLatestRelease $p.Repository)) 'Only explicit HTTP 404 establishes no latest.'
        $done=Complete-GitHubPublication $p
        Assert-Test ($done.Published -and $script:Fake.PublishAttempts -eq 1) 'First version safely publishes after absence and stable inventory checks.'
    }
    Test-Case 'no-latest HTTP 404 with published versions is not a first release' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Add-FakeStable 'kesas-rc21';$script:Fake.Latest=''
        $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'not confirmed despite published versions'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 0) 'Disappeared latest cannot trigger a write.'
    }
    Test-Case 'unknown latest read failure before publication never writes' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$script:Fake.LatestReadFailure=$true
        $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'metadata request failed'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 0 -and $script:Fake.Delays.Count -eq 0) 'Unknown read is not absence and cannot authorize a PATCH.'
    }
    foreach($response in @('null',"HTTP/2.0 200 OK`n`nnull","HTTP/2.0 503 Unavailable`n`n{}",'HTTP 404 text without a response status')){
        Test-Case 'malformed or unknown latest response never authorizes publication' {
            $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$script:Fake.LatestRawResponse=$response
            $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'metadata request failed'
            Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 0) 'Malformed JSON, non-404 failure, or CLI text fails closed.'
        }
    }
    Test-Case 'conflicting latest tag cannot be replaced' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Add-FakeStable 'unrelated-stable'
        $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'Conflicting latest release tag'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 0) 'Unknown channel is preserved.'
    }
    Test-Case 'same latest tag with a conflicting release ID cannot be published' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p
        $script:Fake.LatestOverride=[pscustomobject]@{id=999;tag_name='kesas-rc22';draft=$false;prerelease=$false}
        $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'Conflicting latest release identity'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 0) 'Exact tag alone does not establish release identity.'
    }
    Test-Case 'published target with stale older latest is read-only' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Complete-GitHubPublication $p;$null=Add-FakeStable 'kesas-rc21'
        $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'not confirmed'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 1) 'Published targets are never promoted again on uncertain metadata.'
    }
    Test-Case 'delayed published visibility retries reads after a single PATCH' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Add-FakeStable 'kesas-rc21';$script:Fake.HideReadsAfterPublish=2
        $done=Complete-GitHubPublication $p
        Assert-Test ($done.Published -and $script:Fake.PublishAttempts -eq 1 -and ($script:Fake.Delays -join ',') -ceq '1,2') 'Stale draft and older latest converge through bounded reads only.'
    }
    Test-Case 'unconfirmed visibility exhausts bounded reads without another PATCH' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$script:Fake.HideReadsAfterPublish=20
        Assert-Throws {Complete-GitHubPublication $p} 'not confirmed'
        Assert-Test ($script:Fake.PublishAttempts -eq 1 -and ($script:Fake.Delays -join ',') -ceq '1,2,4') 'Readback exhaustion never replays the write.'
        $script:Fake.StalePublishReads=0;$writes=$script:Fake.Writes;$done=Complete-GitHubPublication $p
        Assert-Test ($done.Published -and $script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 1) 'Later visibility confirms the completed release without PATCH.'
    }
    Test-Case 'transient read failure after publication retries reads only' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$script:Fake.LatestReadFailuresAfterPublish=2
        $done=Complete-GitHubPublication $p
        Assert-Test ($done.Published -and $script:Fake.PublishAttempts -eq 1 -and ($script:Fake.Delays -join ',') -ceq '1,2') 'Post-write read failures do not trigger another write.'
    }
    Test-Case 'completed publication still rejects changed remote digest without writing' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Complete-GitHubPublication $p
        (Get-FakeRelease 'kesas-rc22').assets[0].digest='sha256:'+('0'*64);$writes=$script:Fake.Writes
        Assert-Throws {Complete-GitHubPublication $p} 'Immutable'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 1) 'No-write completion retains exact immutable digest validation.'
    }
    Test-Case 'initial media latest permits the first prepared version' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$script:Fake.Latest='pack-media'
        $done=Complete-GitHubPublication $p
        Assert-Test ($done.Published -and $script:Fake.PublishAttempts -eq 1 -and $script:Fake.Latest -ceq 'kesas-rc22') 'Known media placeholder permits the first stable version.'
    }
    Test-Case 'media latest cannot hide another published stable version' {
        $f=New-Fixture;$p=New-TestPlan $f;Reset-Fake;$null=Prepare-GitHubPublication $p;$null=Add-FakeStable 'kesas-rc23';$script:Fake.Latest='pack-media'
        $writes=$script:Fake.Writes;Assert-Throws {Complete-GitHubPublication $p} 'not confirmed despite published versions'
        Assert-Test ($script:Fake.Writes -eq $writes -and $script:Fake.PublishAttempts -eq 0) 'A media placeholder is not authority to replace an existing version channel.'
    }
} finally {
    # Only this invocation's explicit, validated temporary fixture root is removed.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^APF-GitHubPublisher-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($script:Failures.Count) { throw ($script:Failures -join "`n") }
Write-Output "PASS $script:Passed offline publisher tests; zero real GitHub calls. Temporary fixtures removed."
