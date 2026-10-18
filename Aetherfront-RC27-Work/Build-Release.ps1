#requires -Version 7.0
<#
Local RC27 packaging only. Run AFTER the main-owned code freeze:
  ./Build-Release.ps1 -CodeResultPath <CODE-RESULT.json> -CodeResultSha256 <sha256> -CodeFrozen

Code-result schema: schemaVersion=1, version=kesas-rc27, completed=true,
baselineVersion=kesas-rc26, baselineFeedSha256, baselineArchiveSha256,
enginePath/engineSha256, clientPath/clientSha256, corePath/coreSha256,
auditPath/auditSha256. Relative paths resolve against the code-result directory;
all code inputs must stay inside this RC27 work directory. No code is compiled.
Catalog exception: only HideEmptyTechTreeNodes package.version gains -repair27
in both profiles; its archive identity and every other declaration stay exact.

Outputs: a unique BuildAttempts/Release-<GUID>/ containing Candidate/Client,
Candidate/Published (signed LOCAL preview), Candidate/Evidence, Release/,
Aetherfront-Release-kesas-rc27.zip and BUILD-RESULT.json. Incomplete attempts
retain their .incomplete- prefix and are never reused or automatically deleted.
The native publisher must later generate/publish its OWN exact signed bytes.
This script never invokes it or changes live/media/server/home state.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CodeResultPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$CodeResultSha256,
    [switch]$CodeFrozen
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$CodeFrozen) { throw 'Wait for the main-owned RC27 code freeze; packaging requires -CodeFrozen.' }
$work = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$root = [IO.Path]::GetFullPath((Join-Path $work '../KESAS-VPS-Server'))
$common = Join-Path $root 'Update.Common.ps1'
if ((Get-FileHash -LiteralPath $common -Algorithm SHA256).Hash -ine '52444126b6c4e0b845e6d4f895010ede22d727745f8cbaa495a41c21eaac79ba') {
    throw 'Reviewed native Update.Common.ps1 changed; review before packaging.'
}
. $common
$work = Assert-UpdatePath $work
$utf8 = [Text.UTF8Encoding]::new($false)
$version = 'kesas-rc27'; $baselineVersion = 'kesas-rc26'
$notes = 'Preserve package folder casing and restore affected models.'
$catalogRelative = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
function Hash([string]$Path) { (Get-FileHash -LiteralPath (Assert-UpdatePath $Path) -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Hash([string]$Path,[string]$Expected) {
    if ($Expected -cnotmatch '^[a-f0-9]{64}$' -or (Hash $Path) -cne $Expected) { throw "Input/output SHA256 mismatch: $Path" }
}
function Json($Value) { ConvertTo-Json -InputObject $Value -Depth 80 -Compress }
function Inventory-Json($Files) {
    $canonical = @($Files | ForEach-Object { [pscustomobject][ordered]@{ path=[string]$_.path; sha256=[string]$_.sha256; sizeBytes=[long]$_.sizeBytes } } | Sort-Object path)
    Json $canonical
}
function Owned([string]$Path) {
    $full = Assert-UpdatePath $Path
    if (!$full.StartsWith($work + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside RC27 work directory: $full" }
    return $full
}
function Resolve-CodePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Empty code/audit input path.' }
    if ([IO.Path]::IsPathRooted($Path)) { return (Owned $Path) }
    return (Owned (Get-UpdateChildPath ([IO.Path]::GetDirectoryName($CodeResultPath)) $Path))
}
function Write-Json([string]$Path,$Value) {
    $target = Owned $Path
    $stream = [IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes = $utf8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 80) + "`n"); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
function Assert-Inventory([string]$Directory,$Files) {
    Assert-UpdateTree $Directory
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) {
        if (!$seen.Add($file.path)) { throw 'Duplicate signed client path.' }
        $path = Get-UpdateChildPath $Directory $file.path
        Assert-Hash $path $file.sha256
        if (([IO.FileInfo]$path).Length -ne $file.sizeBytes) { throw "Signed file size mismatch: $($file.path)" }
    }
    if ($seen.Count -ne 464 -or @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File).Count -ne 464) { throw 'Expected the exact signed 464-file client inventory.' }
}
function New-ReleaseCatalog($BaselineCatalog) {
    $next = Json $BaselineCatalog | ConvertFrom-Json
    $next.version = $version
    $exceptions = @()
    foreach ($profile in @('full','potato')) {
        if ($next.$profile.packVersion -cne $baselineVersion -or $next.$profile.requiredClientVersion -cne $baselineVersion) { throw 'Unexpected baseline profile routing.' }
        $next.$profile.packVersion = $version; $next.$profile.requiredClientVersion = $version
        $old = @($BaselineCatalog.$profile.managedPackages | Where-Object stripPrefix -CEQ 'HideEmptyTechTreeNodes')
        $hettn = @($next.$profile.managedPackages | Where-Object stripPrefix -CEQ 'HideEmptyTechTreeNodes')
        if ($old.Count -ne 1 -or $hettn.Count -ne 1 -or [string]::IsNullOrWhiteSpace($old[0].id) -or
            @($next.$profile.managedPackages | Where-Object id -CEQ $old[0].id).Count -ne 1 -or
            $old[0].version -cnotmatch '^[A-Za-z0-9._-]{1,55}$' -or $old[0].version -cmatch '-repair27$') {
            throw 'Expected exactly one unmodified signed HETTN declaration per profile, with room for the repair27 suffix.'
        }
        # The old already-loaded helper must replace only this tiny cached
        # archive. The four casing-affected roots do NOT get version bumps.
        $hettn[0].version = $old[0].version + '-repair27'
        $restore = Json $hettn[0] | ConvertFrom-Json
        $restore.version = $old[0].version
        if ((Json $restore) -cne (Json $old[0])) { throw 'HETTN exception may change only package.version; archive/cache identity must remain exact.' }
        $exceptions += [pscustomobject][ordered]@{ profile = $profile; packageId = $old[0].id; stripPrefix = 'HideEmptyTechTreeNodes'; field = 'version'
            before = (Json $old[0] | ConvertFrom-Json); after = (Json $hettn[0] | ConvertFrom-Json); archiveIdentityPreserved = $true
            reason = 'Replace the tiny cached HETTN root for old-helper compatibility with generated Resources tech-tree files; no archive change or new media.' }
    }
    if ($exceptions[0].packageId -cne $exceptions[1].packageId) { throw 'Full and Potato must identify the same signed HETTN package.' }
    # Reverse ONLY revision routing and the explicit HETTN version exception.
    # Compare the ENTIRE catalog, including package ordering, repair26 versions,
    # clientOnly, protectedRoots, GitHub settings and the six reviewed archives.
    $reversed = Json $next | ConvertFrom-Json
    $reversed.version = $baselineVersion
    foreach ($exception in $exceptions) {
        $profile = $exception.profile
        $reversed.$profile.packVersion = $baselineVersion; $reversed.$profile.requiredClientVersion = $baselineVersion
        $entry = @($reversed.$profile.managedPackages | Where-Object id -CEQ $exception.packageId)
        $entry[0].version = $exception.before.version
    }
    if ((Json $reversed) -cne (Json $BaselineCatalog)) { throw 'Unreviewed catalog declaration/policy drift beyond the exact HETTN version exception.' }
    [pscustomobject]@{ catalog = $next; packageVersionExceptions = $exceptions }
}

# Every preflight is read-only. The code-result hash binds all three replacement
# DLLs and the main-owned audit to the CURRENT authenticated RC26 feed/archive.
$CodeResultPath = Owned $CodeResultPath
Assert-Hash $CodeResultPath $CodeResultSha256
$code = Read-UpdateText $CodeResultPath | ConvertFrom-Json
if ($code.schemaVersion -ne 1 -or $code.version -cne $version -or $code.completed -isnot [bool] -or !$code.completed -or $code.baselineVersion -cne $baselineVersion) {
    throw 'Use a completed schemaVersion 1 RC27 code result based on RC26.'
}
$source = @{}
foreach ($name in @('engine','client','core','audit')) {
    $source[$name] = Resolve-CodePath ([string]$code."$($name)Path")
    Assert-Hash $source[$name] ([string]$code."$($name)Sha256")
}
$null = Read-UpdateText $source.audit | ConvertFrom-Json
$feedPath = Get-UpdateChildPath $root 'Updates/APFClientUpdate.json'
$historicalFeed = Get-UpdateChildPath $root 'Updates/kesas-rc26/APFClientUpdate.json'
$archive = Get-UpdateChildPath $root 'Updates/kesas-rc26/APFClient-kesas-rc26.zip'
$publicKey = Get-UpdateChildPath $root 'secrets/update-signing-public.xml'
Assert-Hash $feedPath ([string]$code.baselineFeedSha256)
Assert-Hash $historicalFeed ([string]$code.baselineFeedSha256)
Assert-Hash $archive ([string]$code.baselineArchiveSha256)
Assert-Hash "$root/UpdateTool/ApfUpdateTool.exe" '215768322a2ff19b5388395531abd90e428bb6ac5b2ac932e9146411c76fad30'
Invoke-UpdateTool $root @('--verify','--manifest',$feedPath,'--archive',$archive,'--public-key-xml',$publicKey)
$feed = Read-UpdateText $feedPath | ConvertFrom-Json
$deployment = Get-UpdateDeployment $root
if ($feed.version -cne $baselineVersion -or @($feed.files).Count -ne 464 -or
    $feed.packageUrl -cne "$($deployment.packOrigin)/updates/kesas-rc26/APFClient-kesas-rc26.zip") { throw 'Current signed RC26 feed/version/URL/inventory changed.' }
$baselineCore = @($feed.files | Where-Object path -CEQ 'GameData/LunaMultiplayer/Plugins/ApfCore.dll')
if ($baselineCore.Count -ne 1 -or [string]$code.coreSha256 -cne [string]$baselineCore[0].sha256) {
    throw 'Final RC27 design preserves the exact signed RC26 core; only Engine and client code may change.'
}
$packPath = Get-UpdateChildPath $root 'Server/Config/APFPack.json'
$technologyPath = Get-UpdateChildPath $root 'Server/Config/AetherfrontTechnology.json'
$basePack = Read-UpdateText $packPath | ConvertFrom-Json
if ($basePack.packVersion -cne $baselineVersion -or $basePack.requiredClientVersion -cne $baselineVersion -or @($basePack.managedPackages).Count -ne 0) {
    throw 'Expected RC26 server policy with empty managedPackages; never force Full onto Potato.'
}
$inputPaths = @($PSCommandPath,$common,$CodeResultPath,$feedPath,$historicalFeed,$archive,$packPath,$technologyPath,
    "$root/deployment.json",$publicKey,"$root/UpdateTool/ApfUpdateTool.exe") + @($source.Values)
$inputs = @(foreach ($path in $inputPaths) { [ordered]@{ path = Assert-UpdatePath $path; sha256 = Hash $path } })

# A private GUID attempt is the only output target; no RC26 helper is sourced.
$attemptRoot = Get-UpdateChildPath $work 'BuildAttempts'
$name = 'Release-' + [Guid]::NewGuid().ToString('N')
$pending = Owned (Get-UpdateChildPath $attemptRoot ".incomplete-$name")
$complete = Owned (Get-UpdateChildPath $attemptRoot $name)
if ((Test-Path -LiteralPath $pending) -or (Test-Path -LiteralPath $complete)) { throw 'Immutable attempt exists.' }
New-UpdateDirectory $pending
$candidate = Get-UpdateChildPath $pending 'Candidate'
$client = Get-UpdateChildPath $candidate 'Client'
$release = Get-UpdateChildPath $pending 'Release'
New-UpdateDirectory $release
Expand-UpdateArchive $archive $client
Assert-Inventory $client $feed.files
$catalogPath = Get-UpdateChildPath $client $catalogRelative
$baselineCatalog = Read-UpdateText $catalogPath | ConvertFrom-Json
if ($baselineCatalog.version -cne $baselineVersion -or @($baselineCatalog.full.managedPackages).Count -ne 211 -or
    @($baselineCatalog.potato.managedPackages).Count -ne 177) { throw 'Unexpected signed RC26 catalog/profile counts.' }
$catalogReview = New-ReleaseCatalog $baselineCatalog
$catalog = $catalogReview.catalog
$packageVersionExceptions = $catalogReview.packageVersionExceptions
$replacements = @(@('engine','GameData/LunaMultiplayer/Plugins/KesasInstall.dll'),@('client','GameData/LunaMultiplayer/Plugins/LmpClient.dll'),@('core','GameData/LunaMultiplayer/Plugins/ApfCore.dll'))
foreach ($mapping in $replacements) {
    if (@($feed.files | Where-Object path -CEQ $mapping[1]).Count -ne 1) { throw 'Replacement target is not a unique signed baseline file.' }
    $target = Get-UpdateChildPath $client $mapping[1]
    [IO.File]::Copy($source[$mapping[0]],$target,$true)
    Assert-Hash $target ([string]$code."$($mapping[0])Sha256")
}
[IO.File]::WriteAllText($catalogPath,(ConvertTo-Json -InputObject $catalog -Depth 80) + "`n",$utf8)
$settingsPath = Get-UpdateChildPath $client 'GameData/LunaMultiplayer/Data/APFClient.json'
$settings = Read-UpdateText $settingsPath | ConvertFrom-Json
if ($settings.clientVersion -cne $baselineVersion -or !$settings.enabled) { throw 'Unexpected baseline client settings.' }
$settings.clientVersion = $version
[IO.File]::WriteAllText($settingsPath,(ConvertTo-Json -InputObject $settings -Depth 20) + "`n",$utf8)
$inventoryPath = Get-UpdateChildPath $client 'KESAS/PATCH-MANIFEST.json'
$inventory = Read-UpdateText $inventoryPath | ConvertFrom-Json
if ($inventory.version -cne $baselineVersion) { throw 'Unexpected patch inventory version.' }
$inventory.version = $version
$inventory.files = @(foreach ($file in Get-ChildItem -LiteralPath $client -Recurse -Force -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($client.Length + 1).Replace('\','/')
    if ($relative -ceq 'KESAS/PATCH-MANIFEST.json') { continue }
    [ordered]@{ path = $relative; bytes = $file.Length; sha256 = Hash $file.FullName }
})
[IO.File]::WriteAllText($inventoryPath,(ConvertTo-Json -InputObject $inventory -Depth 80) + "`n",$utf8)
$allowedChanges = @($replacements | Where-Object { $_[0] -cne 'core' } | ForEach-Object { $_[1] }) + @($catalogRelative,'GameData/LunaMultiplayer/Data/APFClient.json','KESAS/PATCH-MANIFEST.json')
$expectedFiles = @(foreach ($file in $feed.files) {
    $path = Get-UpdateChildPath $client $file.path
    if ($file.path -cnotin $allowedChanges) { Assert-Hash $path $file.sha256 }
    [ordered]@{ path = $file.path; sha256 = Hash $path; sizeBytes = ([IO.FileInfo]$path).Length }
})
Assert-Inventory $client $expectedFiles
$pack = Json $basePack | ConvertFrom-Json
$pack.packVersion = $version; $pack.requiredClientVersion = $version
$reversePack = Json $pack | ConvertFrom-Json
$reversePack.packVersion = $baselineVersion; $reversePack.requiredClientVersion = $baselineVersion
if ((Json $reversePack) -cne (Json $basePack)) { throw 'Unreviewed server pack rule/identity change.' }
[IO.Compression.ZipFile]::CreateFromDirectory($client,"$release/ClientTemplate.zip",[IO.Compression.CompressionLevel]::Optimal,$false)
Write-Json "$release/APFPack-template.json" $pack
[IO.File]::Copy($technologyPath,"$release/AetherfrontTechnology.json",$false)
Assert-Hash "$release/AetherfrontTechnology.json" (Hash $technologyPath)
$payloadNames = @('ClientTemplate.zip','APFPack-template.json','AetherfrontTechnology.json')
Write-Json "$release/RELEASE.json" ([ordered]@{ version = $version; files = @(foreach ($file in $payloadNames) { [ordered]@{ path = $file; sha256 = Hash "$release/$file" } }) })
$releaseNames = $payloadNames + @('RELEASE.json')
$releaseZip = "$pending/Aetherfront-Release-$version.zip"
$zip = [IO.Compression.ZipFile]::Open($releaseZip,[IO.Compression.ZipArchiveMode]::Create)
try { foreach ($file in $releaseNames) { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,"$release/$file",$file,[IO.Compression.CompressionLevel]::Optimal) } }
finally { $zip.Dispose() }
if (@(Get-ChildItem -LiteralPath $release -Recurse -Force -File).Count -ne 4) { throw 'Release directory must contain exactly four root files.' }
$zip = [IO.Compression.ZipFile]::OpenRead($releaseZip)
try {
    if ((Json @($zip.Entries.FullName | Sort-Object)) -cne (Json @($releaseNames | Sort-Object))) { throw 'Release ZIP violates the exact four-file native publisher contract.' }
} finally { $zip.Dispose() }
New-UpdateDirectory "$candidate/Published"
Invoke-UpdateTool $root @('--root-overlay','--source-root',$client,'--output',"$candidate/Published",'--version',$version,
    '--package-base-url',"$($deployment.packOrigin)/updates/$version/",'--private-key-xml',"$root/secrets/update-signing-private.xml",'--ksp-version','1.12.5','--release-notes',$notes)
Invoke-UpdateTool $root @('--verify','--manifest',"$candidate/Published/APFClientUpdate.json",'--archive',"$candidate/Published/APFClient-$version.zip",'--public-key-xml',$publicKey)
$signed = Read-UpdateText "$candidate/Published/APFClientUpdate.json" | ConvertFrom-Json
if ($signed.version -cne $version -or @($signed.files).Count -ne 464 -or (Inventory-Json $signed.files) -cne (Inventory-Json $expectedFiles)) { throw 'Signed RC27 candidate inventory differs from the reviewed 464 files.' }
Assert-Inventory $client $signed.files
New-UpdateDirectory "$candidate/Evidence"
[IO.File]::Copy($CodeResultPath,"$candidate/Evidence/CODE-RESULT.json",$false)
[IO.File]::Copy($source.audit,"$candidate/Evidence/audit.json",$false)
Assert-Hash "$candidate/Evidence/CODE-RESULT.json" $CodeResultSha256
Assert-Hash "$candidate/Evidence/audit.json" ([string]$code.auditSha256)
foreach ($inputFile in $inputs) { Assert-Hash $inputFile.path $inputFile.sha256 }
$outputs = @(foreach ($file in Get-ChildItem -LiteralPath $pending -Recurse -Force -File | Sort-Object FullName) {
    [ordered]@{ path = $file.FullName.Substring($pending.Length + 1).Replace('\','/'); sha256 = Hash $file.FullName; sizeBytes = $file.Length }
})
Write-Json "$pending/BUILD-RESULT.json" ([ordered]@{ schemaVersion = 1; version = $version; baselineVersion = $baselineVersion; completed = $true
    createdAtUtc = [DateTime]::UtcNow.ToString('o'); codeResultPath = $CodeResultPath; codeResultSha256 = $CodeResultSha256
    baselineFeedSha256 = $code.baselineFeedSha256; baselineArchiveSha256 = $code.baselineArchiveSha256; auditSha256 = $code.auditSha256
    signedFiles = 464; fullPackages = 211; potatoPackages = 177; inputFiles = $inputs; outputFiles = $outputs; notes = $notes; releaseNotes = $notes
    releaseZip = "$complete/Aetherfront-Release-$version.zip"; releaseSha256 = Hash $releaseZip; candidatePath = "$complete/Candidate"
    catalogSha256 = Hash $catalogPath; coreUnchangedFromSignedRC26 = $true; packageDeclarationsPreservedExactly = $false
    packageDeclarationsPreservedExceptHETTNVersion = $true; archiveDeclarationsPreservedExactly = $true; packageVersionExceptions = $packageVersionExceptions
    repair26VersionsPreserved = $true; profileRulesPreserved = $true
    serverManagedPackagesPreservedEmpty = $true; serverClientOnlyAndProtectedRulesPreserved = $true; technologyUnchanged = $true
    allowedClientChanges = $allowedChanges; newMediaCount = 0; mediaArchivesRehashed = $false; mediaPreparationRequired = $false
    nativePublisherMustGenerateOwnExactBytes = $true; runtimeCompiled = $false; nativeStagingExecuted = $false; liveWrites = $false; notActivated = $true })
$pending = Owned $pending; $complete = Owned $complete
if ([IO.Path]::GetDirectoryName($pending) -ine $attemptRoot -or [IO.Path]::GetDirectoryName($complete) -ine $attemptRoot -or (Test-Path -LiteralPath $complete)) { throw 'Invalid immutable completion paths.' }
Assert-UpdateTree $pending
[IO.Directory]::Move($pending,$complete)
Write-Output "BUILD-RESULT: $complete/BUILD-RESULT.json"
