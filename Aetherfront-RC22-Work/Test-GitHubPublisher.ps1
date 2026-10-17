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
    }
}
function Get-FakePage($Items, [int]$Page) { return @($Items | Select-Object -Skip (($Page - 1) * 100) -First 100) }

# This shadows the production shim before any publication function is called.
# Unknown calls fail closed. No gh process, REST request, authentication, or upload runs.
function Invoke-GitHubCli {
    param([string[]]$Arguments, [string]$Context)
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
        return (ConvertTo-Json -InputObject @(Get-FakePage $script:Fake.Releases ([int]$Matches[1])) -Depth 8)
    }
    if ($endpoint -ceq 'repos/test/public/releases' -and $method -eq 'POST') {
        Assert-Test ($fields.make_latest -ceq 'false') 'New releases never automatically latest.'
        $release = [pscustomobject]@{ id = $script:Fake.NextId; tag_name = $fields.tag_name; name = $fields.name; draft = ($fields.draft -eq 'true'); prerelease = ($fields.prerelease -eq 'true'); assets = [Collections.ArrayList]::new() }
        $script:Fake.NextId++; $null = $script:Fake.Releases.Add($release); $script:Fake.Writes++
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
        if ($fields.make_latest -eq 'true' -and $script:Fake.PublishNoWrite) { throw 'fixture-private-cli-output' }
        $release.draft = $fields.draft -eq 'true'; $release.prerelease = $fields.prerelease -eq 'true'; $script:Fake.Writes++
        if ($fields.make_latest -eq 'true') {
            if (-not $script:Fake.WrongLatest) { $script:Fake.Latest = $release.tag_name }
            if ($script:Fake.PublishUnknown) { throw 'fixture-private-cli-output' }
        }
        return (ConvertTo-Json -InputObject $release -Depth 8)
    }
    if ($endpoint -ceq 'repos/test/public/releases/latest' -and $method -eq 'GET') {
        $latest = @($script:Fake.Releases | Where-Object { $_.tag_name -ceq $script:Fake.Latest })
        if (-not $latest.Count) { throw 'fixture-private-cli-output' }
        return (ConvertTo-Json -InputObject $latest[0] -Depth 8)
    }
    throw 'Unexpected mock CLI invocation; real CLI is forbidden in tests.'
}
function Get-FakeRelease([string]$Tag) { return @($script:Fake.Releases | Where-Object { $_.tag_name -ceq $Tag })[0] }

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
