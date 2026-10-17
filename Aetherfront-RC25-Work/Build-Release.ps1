#requires -Version 7.0
[CmdletBinding()]
param([string]$ModFixStage = (Join-Path $PSScriptRoot 'ModFixes/Staged-RC25'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = [IO.Path]::GetFullPath($PSScriptRoot)
$root = [IO.Path]::GetFullPath((Join-Path $work '../KESAS-VPS-Server'))
. "$root/Update.Common.ps1"
$utf8 = [Text.UTF8Encoding]::new($false)
$version = 'kesas-rc25'
$baseVersion = 'kesas-rc24'
$release = Get-UpdateChildPath $work 'Release'
$candidate = Get-UpdateChildPath $work 'Candidate'
$releaseZip = Get-UpdateChildPath $work "Aetherfront-Release-$version.zip"
$resultPath = Get-UpdateChildPath $work 'BUILD-RESULT.json'
$stage = (Assert-UpdatePath $ModFixStage).TrimEnd('\')
$modFixRoot = Get-UpdateChildPath $work 'ModFixes'
if (!$stage.StartsWith($modFixRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $stage -ieq (Get-UpdateChildPath $modFixRoot 'Staged')) {
    throw 'Use final ModFixes/Staged-RC25, or an explicitly reviewed newer attempt. ModFixes/Staged is stale.'
}
foreach ($path in @($release, $candidate, $releaseZip, $resultPath)) {
    if (Test-Path -LiteralPath $path) { throw 'Immutable RC25 build output exists; preserve previous attempts.' }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json($Value) { ConvertTo-Json -InputObject $Value -Depth 60 -Compress }
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 60) + "`n", $utf8) }
function Assert-FileHash([string]$Path, [string]$Expected) {
    $null = Assert-UpdatePath $Path
    if ($Expected -cnotmatch '^[a-f0-9]{64}$' -or (Hash $Path) -cne $Expected) { throw "Build input hash mismatch: $Path" }
}
function Get-StageInventory([string]$Directory) {
    foreach ($relative in @('install-catalog.json', 'MODFIX-PLAN.json', 'ClientOverlay.zip')) {
        $file = Get-UpdateChildPath $Directory $relative
        [ordered]@{ path = $relative; sha256 = (Hash $file) }
    }
    foreach ($relative in @('ClientOverlay', 'Media')) {
        $tree = Get-UpdateChildPath $Directory $relative
        if (![IO.Directory]::Exists($tree)) { throw "Missing final mod-fix tree: $relative" }
        Assert-UpdateTree $tree
        foreach ($file in Get-ChildItem -LiteralPath $tree -Recurse -Force -File | Sort-Object FullName) {
            [ordered]@{ path = $file.FullName.Substring($Directory.Length + 1).Replace('\', '/'); sha256 = (Hash $file.FullName) }
        }
    }
}
$sourcePaths = @{
    core = (Get-UpdateChildPath $work 'Patched/ApfCore.dll')
    client = (Get-UpdateChildPath $work 'ClientPatcher/Patched-Verified/LmpClient.dll')
    clientAudit = (Get-UpdateChildPath $work 'ClientPatcher/Patched-Verified/audit.json')
    donor = (Get-UpdateChildPath $work 'ClientUi/bin/Release/net472/AetherfrontConnectSource.dll')
    engine = (Get-UpdateChildPath $work 'Engine/bin/Release/net472/KesasInstall.dll')
    helper = (Get-UpdateChildPath $work 'APFApplyUpdate.ps1')
    playerText = (Get-UpdateChildPath $work 'PLAYER-INSTALL.txt')
    template = (Get-UpdateChildPath $root 'ClientTemplate.zip')
    feed = (Get-UpdateChildPath $root 'Updates/APFClientUpdate.json')
    pack = (Get-UpdateChildPath $root 'Server/Config/APFPack.json')
    technology = (Get-UpdateChildPath $root 'Server/Config/AetherfrontTechnology.json')
    deployment = (Get-UpdateChildPath $root 'deployment.json')
}
$sourceHashes = @{}
foreach ($name in $sourcePaths.Keys) { $sourceHashes[$name] = Hash $sourcePaths[$name] }
$stageInventory = @(Get-StageInventory $stage)
$feed = Read-UpdateText $sourcePaths.feed | ConvertFrom-Json
if ($feed.version -cne $baseVersion) { throw 'Live release changed; review the RC25 base.' }
Invoke-UpdateTool $root @('--verify', '--manifest', $sourcePaths.feed, '--archive', "$root/Updates/$baseVersion/APFClient-$baseVersion.zip", '--public-key-xml', "$root/secrets/update-signing-public.xml")
Assert-UpdateKeyPair $root
$deployment = Get-UpdateDeployment $root
$audit = Read-UpdateText $sourcePaths.clientAudit | ConvertFrom-Json
$baselineCore = @($feed.files | Where-Object path -CEQ 'GameData/LunaMultiplayer/Plugins/ApfCore.dll')
$baselineClient = @($feed.files | Where-Object path -CEQ 'GameData/LunaMultiplayer/Plugins/LmpClient.dll')
if ($baselineCore.Count -ne 1 -or $baselineClient.Count -ne 1 -or $audit.version -cne $version -or
    $audit.baselineCoreSha256 -ine $baselineCore[0].sha256 -or $audit.baselineClientSha256 -ine $baselineClient[0].sha256 -or
    $audit.clientSha256 -ine $sourceHashes.client -or $audit.donorSha256 -ine $sourceHashes.donor -or
    $audit.requiredReadyTimeoutMilliseconds -ne 600000) { throw 'UI audit does not match final RC25 donor/client or authenticated RC24 baseline; regenerate the verified patch.' }

New-UpdateDirectory $release
New-UpdateDirectory $candidate
$snapshot = Get-UpdateChildPath $candidate 'ModFixes'
New-UpdateDirectory $snapshot
foreach ($file in $stageInventory) {
    $destination = Get-UpdateChildPath $snapshot $file.path
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy((Get-UpdateChildPath $stage $file.path), $destination, $false)
    Assert-FileHash $destination $file.sha256
}
$catalog = Read-UpdateText "$snapshot/install-catalog.json" | ConvertFrom-Json
$plan = Read-UpdateText "$snapshot/MODFIX-PLAN.json" | ConvertFrom-Json
if ($catalog.version -cne $version -or !$catalog.gitHub.enabled -or $plan.version -cne $version -or
    !$plan.notActivated -or @($plan.media).Count -ne 4 -or @($plan.waterfall).Count -ne 2) {
    throw 'Final mod-fix output must include both Waterfall fixes, Potato policy, common fixes and enabled GitHub catalog.'
}
Assert-FileHash "$snapshot/ClientOverlay.zip" $plan.overlay.sha256
if ((Hash "$snapshot/ClientOverlay/GameData/LunaMultiplayer/Launcher/install-catalog.json") -cne (Hash "$snapshot/install-catalog.json")) {
    throw 'Overlay/catalog snapshots disagree; finish mod-fix staging before building.'
}
foreach ($file in $plan.dependency.files) {
    if ($file.path -cnotmatch '^GameData/000_Harmony/[^/]+$') { throw 'Unexpected Harmony overlay path.' }
    Assert-FileHash (Get-UpdateChildPath "$snapshot/ClientOverlay" $file.path) $file.sha256
}
if (@($plan.dependency.files | Where-Object path -CEQ 'GameData/000_Harmony/HarmonyInstallChecker.dll').Count -ne 1) {
    throw 'Complete Harmony overlay, including HarmonyInstallChecker.dll, is required.'
}

$client = Get-UpdateChildPath $candidate 'Client'
Expand-UpdateArchive $sourcePaths.template $client
$catalogRelative = 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
$catalogPath = Get-UpdateChildPath $client $catalogRelative
$baselineCatalogRecord = @($feed.files | Where-Object path -CEQ $catalogRelative)
if ($baselineCatalogRecord.Count -ne 1) { throw 'Signed RC24 catalog entry is missing.' }
Assert-FileHash $catalogPath $baselineCatalogRecord[0].sha256
if ($plan.baseline.catalogSha256 -cne $baselineCatalogRecord[0].sha256) { throw 'Mod fixes were not staged from the active RC24 catalog.' }
$baselineCatalog = Read-UpdateText $catalogPath | ConvertFrom-Json
if ($baselineCatalog.version -cne $baseVersion -or (Json $catalog.gitHub) -cne (Json $baselineCatalog.gitHub)) {
    throw 'Unexpected baseline catalog or GitHub settings change.'
}
foreach ($pair in @(@('ApfCore.dll', $baselineCore[0].sha256), @('LmpClient.dll', $baselineClient[0].sha256))) {
    Assert-FileHash (Get-UpdateChildPath $client "GameData/LunaMultiplayer/Plugins/$($pair[0])") $pair[1]
}
# The RC24 starter already ships the runtime compression pair in Managed.
# Preserve those exact signed bytes; a smoke clone without the starter overlay
# is not representative of an installed client. No replacement package needed.
foreach ($name in @('System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')) {
    $relative = "KSP_x64_Data/Managed/$name"
    $record = @($feed.files | Where-Object path -CEQ $relative)
    if ($record.Count -ne 1) { throw 'Signed baseline runtime dependency missing.' }
    Assert-FileHash (Get-UpdateChildPath $client $relative) $record[0].sha256
}
$fixDeclarations = @{}
foreach ($media in $plan.media) {
    $package = $media.declaration
    if ($fixDeclarations.ContainsKey($package.id)) { throw 'Duplicate mod-fix package ID.' }
    $fixDeclarations[$package.id] = $package
}
foreach ($profileName in @('full', 'potato')) {
    $profile = $catalog.$profileName
    $baselineProfile = $baselineCatalog.$profileName
    if ($profile.packVersion -cne $version -or $profile.requiredClientVersion -cne $version) { throw 'Staged profile version mismatch.' }
    # No policy/profile drift apart from release versions and reviewed packages.
    $expected = (Json $baselineProfile) | ConvertFrom-Json
    $expected.packVersion = $version; $expected.requiredClientVersion = $version
    $expected.managedPackages = $profile.managedPackages
    if ((Json $expected) -cne (Json $profile)) { throw "Unexpected profile policy change: $profileName" }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $profile.managedPackages) {
        if (!$seen.Add($package.id) -or $package.stripPrefix -ceq '000_Harmony') { throw 'Duplicate package or managed Harmony root.' }
        $old = @($baselineProfile.managedPackages | Where-Object id -CEQ $package.id)
        if ($old.Count -eq 1 -and (Json $old[0]) -ceq (Json $package)) { continue }
        if (!$fixDeclarations.ContainsKey($package.id) -or (Json $package) -cne (Json $fixDeclarations[$package.id])) {
            throw "Unreviewed package change: $profileName / $($package.id)"
        }
    }
    foreach ($old in $baselineProfile.managedPackages) {
        if (!$seen.Contains($old.id)) { throw "Baseline package removed: $profileName / $($old.id)" }
        if ($old.stripPrefix -match 'KcalbelohTextures') {
            $next = @($profile.managedPackages | Where-Object id -CEQ $old.id)
            if ($next.Count -ne 1 -or (Json $old) -cne (Json $next[0])) { throw '4K/8K texture package declarations must remain unchanged.' }
        }
    }
    foreach ($prefix in @('Waterfall', 'ZZZ_KESASModFixes')) {
        $fixed = @($profile.managedPackages | Where-Object stripPrefix -CEQ $prefix)
        if ($fixed.Count -ne 1 -or !$fixDeclarations.ContainsKey($fixed[0].id)) { throw "Missing final fix: $profileName / $prefix" }
    }
}

Copy-UpdateTree "$snapshot/ClientOverlay" $client
# Only this fresh candidate loses the obsolete GUI payload. Validate the exact
# destination and every ancestor immediately before deleting a single file.
$oldUi = Get-UpdateChildPath $candidate 'Client/GameData/LunaMultiplayer/Launcher/AetherfrontInstallUI.bin'
$expectedOldUi = [IO.Path]::GetFullPath("$work/Candidate/Client/GameData/LunaMultiplayer/Launcher/AetherfrontInstallUI.bin")
if ($oldUi -cne $expectedOldUi -or !$oldUi.StartsWith($client + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Obsolete UI target is not the exact fresh candidate file.' }
if ([IO.Directory]::Exists($oldUi)) { throw 'Expected a file for the obsolete candidate UI.' }
if ([IO.File]::Exists($oldUi)) { $null = Assert-UpdatePath $oldUi; [IO.File]::Delete($oldUi) }
if (@(Get-ChildItem -LiteralPath $client -Recurse -File -Filter 'AetherfrontInstallUI.dll').Count) { throw 'No NativeUi DLL belongs in RC25.' }
foreach ($mapping in @(@('core', 'GameData/LunaMultiplayer/Plugins/ApfCore.dll'), @('client', 'GameData/LunaMultiplayer/Plugins/LmpClient.dll'),
    @('engine', 'GameData/LunaMultiplayer/Plugins/KesasInstall.dll'), @('helper', 'GameData/LunaMultiplayer/APFApplyUpdate.ps1'), @('playerText', 'KESAS/PLAYER-INSTALL.txt'))) {
    $destination = Get-UpdateChildPath $client $mapping[1]
    New-UpdateDirectory ([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($sourcePaths[$mapping[0]], $destination, $true)
    Assert-FileHash $destination $sourceHashes[$mapping[0]]
}
[IO.File]::Copy("$snapshot/install-catalog.json", $catalogPath, $true)
Write-Json (Get-UpdateChildPath $client 'GameData/LunaMultiplayer/Data/APFClient.json') ([ordered]@{ enabled = $true; clientVersion = $version })
$inventoryPath = Get-UpdateChildPath $client 'KESAS/PATCH-MANIFEST.json'
$inventory = Read-UpdateText $inventoryPath | ConvertFrom-Json
$inventory.version = $version
$inventory.files = @(foreach ($file in Get-ChildItem -LiteralPath $client -Force -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($client.Length + 1).Replace('\', '/')
    if ($relative -ceq 'KESAS/PATCH-MANIFEST.json') { continue }
    [ordered]@{ path = $relative; bytes = $file.Length; sha256 = (Hash $file.FullName) }
})
Write-Json $inventoryPath $inventory
[IO.Compression.ZipFile]::CreateFromDirectory($client, "$release/ClientTemplate.zip", [IO.Compression.CompressionLevel]::Optimal, $false)
$pack = Read-UpdateText $sourcePaths.pack | ConvertFrom-Json
if ($pack.requiredClientVersion -cne $baseVersion -or $pack.packVersion -cne $baseVersion -or @($pack.managedPackages).Count -ne 0) {
    throw 'Unexpected server pack baseline; preserve the empty managedPackages representation.'
}
# The ordinary handshake coordinator scans the server manifest too. Publishing
# Full managed packages here would impose Full on Potato. Preserve every
# existing clientOnly allowance and DLL pin; change only reviewed binary hashes.
$pack.requiredClientVersion = $version; $pack.packVersion = $version
$waterfall = @($plan.waterfall)
if (@($waterfall.profile | Sort-Object -Unique).Count -ne 2 -or
    @($waterfall.outputDllSha256 | Sort-Object -Unique).Count -ne 1 -or
    @($waterfall.inputDllSha256 | Sort-Object -Unique).Count -ne 1) { throw 'Server Waterfall pin must match BOTH staged profiles.' }
$waterfallPins = @($pack.clientOnly | ForEach-Object { $_.dlls } | Where-Object path -CEQ 'GameData/Waterfall/Plugins/Waterfall.dll')
if ($waterfallPins.Count -ne 1 -or $waterfallPins[0].sha256 -cne $waterfall[0].inputDllSha256) { throw 'Unexpected baseline server Waterfall DLL pin.' }
$waterfallPins[0].sha256 = $waterfall[0].outputDllSha256
$modFixPath = 'GameData/ZZZ_KESASModFixes'
if (@($pack.clientOnly | Where-Object { $modFixPath -cin @($_.pathRoots) }).Count -ne 0) { throw 'Common mod-fix root is already present in the server baseline.' }
$pack.clientOnly = @($pack.clientOnly) + @([ordered]@{ id = 'kesas-rc25-modfixes'; pathRoots = @($modFixPath); dlls = @() })
Write-Json "$release/APFPack-template.json" $pack
[IO.File]::Copy($sourcePaths.technology, "$release/AetherfrontTechnology.json", $false)
# Publisher accepts only these root files (plus explicitly allowed executable
# payload families). ServerPayload/Config is protected; root ZIP media is NOT
# imported. Keep media outside Release and describe its separate preparation.
$mediaDirectory = Get-UpdateChildPath $candidate 'Media'
New-UpdateDirectory $mediaDirectory
$mediaFiles = @(foreach ($media in $plan.media) {
    $package = $media.declaration
    $uri = [uri]$package.archiveUrl
    $name = [uri]::UnescapeDataString(($uri.AbsolutePath -split '/')[-1])
    $null = Get-UpdateRelativePath $name
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -cne 'https' -or $name.Contains('/') -or
        $name -cnotmatch ('^kesas-rc25-[a-z0-9-]+-' + [regex]::Escape($package.sha256) + '\.zip$')) { throw 'New media must retain its content-addressed ZIP filename.' }
    $source = Get-UpdateChildPath "$snapshot/Media" $name
    Assert-FileHash $source $package.sha256
    if (([IO.FileInfo]$source).Length -ne $package.sizeBytes) { throw 'Mod-fix media size mismatch.' }
    $declarations = @(@($catalog.full.managedPackages) + @($catalog.potato.managedPackages) | Where-Object id -CEQ $package.id)
    if (!$declarations.Count -or @($declarations | Where-Object { (Json $_) -cne (Json $package) }).Count) { throw 'Media/catalog declarations disagree.' }
    [IO.File]::Copy($source, "$mediaDirectory/$name", $false)
    Assert-FileHash "$mediaDirectory/$name" $package.sha256
    $livePath = Get-UpdateChildPath $root "Server/Config/$name"
    $present = [IO.File]::Exists($livePath)
    if ($present) { Assert-FileHash $livePath $package.sha256 }
    [ordered]@{ path = "Media/$name"; destination = "Server/Config/$name"; sha256 = $package.sha256; sizeBytes = $package.sizeBytes; alreadyPrepared = $present }
})
if (@(Get-ChildItem -LiteralPath "$snapshot/Media" -Recurse -Force -File).Count -ne $mediaFiles.Count) { throw 'Undeclared staged media file.' }
$preparation = [ordered]@{ version = $version; notExecuted = $true; requiredBeforePublication = $true; files = $mediaFiles;
    instructions = @('Before Publish-Release.ps1, separately prepare the four Candidate/Media ZIPs in KESAS-VPS-Server/Server/Config using the recorded exact filenames, SHA256 and sizes. Never overwrite a different existing content-addressed asset.',
        'This build does not perform that live preparation. The unchanged native publisher resolves ALL catalog media from Server/Config before GitHub draft preparation; it does not import release-root media or protected ServerPayload/Config files.',
        'Preserve all existing RC24 media. GitHub media publication/rewrite must use the same final catalog and assets. Do not edit the catalog after signing; any URL change requires a new candidate and signatures.',
        'Release/APFPack-template.json preserves empty managedPackages and all baseline clientOnly allowances/DLL pins, updating the shared Waterfall DLL pin and adding only the common mod-fix root. The signed client catalog carries both Full and Potato package manifests with the corrected Waterfall/policy archives.') }
Write-Json "$candidate/MEDIA-PREPARATION.json" $preparation
$names = @('ClientTemplate.zip', 'APFPack-template.json', 'AetherfrontTechnology.json')
$files = @(foreach ($name in $names) { [ordered]@{ path = $name; sha256 = (Hash "$release/$name") } })
Write-Json "$release/RELEASE.json" ([ordered]@{ version = $version; files = $files })
$zip = [IO.Compression.ZipFile]::Open($releaseZip, [IO.Compression.ZipArchiveMode]::Create)
try { foreach ($name in $names + @('RELEASE.json')) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$release/$name", $name, [IO.Compression.CompressionLevel]::Optimal) } }
finally { $zip.Dispose() }
New-UpdateDirectory "$candidate/Published"
Invoke-UpdateTool $root @('--root-overlay', '--source-root', $client, '--output', "$candidate/Published", '--version', $version,
    '--package-base-url', "$($deployment.packOrigin)/updates/$version/", '--private-key-xml', "$root/secrets/update-signing-private.xml",
    '--ksp-version', '1.12.5', '--release-notes', 'In-game preparation and explicit Restart & apply; hidden verified client/pack commit; approved mod removal is permanent. Harmony, Waterfall and Potato fixes.')
Invoke-UpdateTool $root @('--verify', '--manifest', "$candidate/Published/APFClientUpdate.json", '--archive', "$candidate/Published/APFClient-$version.zip", '--public-key-xml', "$root/secrets/update-signing-public.xml")
# Export-Client.ps1 enforces an output inside the live server and takes its lock.
# Produce the equivalent starter here, entirely inside this candidate instead.
$starter = Get-UpdateChildPath $candidate "Aetherfront-Starter-$version.zip"
[IO.File]::Copy("$release/ClientTemplate.zip", $starter, $false)
$public = Read-UpdateText "$root/secrets/update-signing-public.xml" 32768
$settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create([IO.StringReader]::new($public), $settings)
$key = [Xml.XmlDocument]::new()
try { $key.Load($reader) } finally { $reader.Dispose() }
$elements = @($key.DocumentElement.ChildNodes | Where-Object NodeType -EQ ([Xml.XmlNodeType]::Element))
if ($key.DocumentElement.Name -cne 'RSAKeyValue' -or $key.DocumentElement.Attributes.Count -ne 0 -or $elements.Count -ne 2 -or
    @($elements | Where-Object { $_.Name -cnotin @('Modulus', 'Exponent') -or $_.Attributes.Count -ne 0 }).Count -or
    @($elements | Where-Object Name -CEQ 'Modulus').Count -ne 1) { throw 'Public-only starter trust key required; private key export refused.' }
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
try { $rsa.PersistKeyInCsp = $false; $rsa.FromXmlString($public); if ($rsa.KeySize -lt 2048) { throw 'Public signing key too small.' }; $public = $rsa.ToXmlString($false) }
finally { $rsa.Dispose() }
$zip = [IO.Compression.ZipFile]::Open($starter, [IO.Compression.ZipArchiveMode]::Update)
try {
    $trust = @{
        'GameData/LunaMultiplayer/Data/APFServer.txt' = "$($deployment.address):$($deployment.gamePort)`n"
        'GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt' = "$($deployment.certificateSha256)`n"
        'GameData/LunaMultiplayer/Data/APFClientUpdateManifestUrl.txt' = "$($deployment.packOrigin)/updates/APFClientUpdate.json`n"
        'GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml' = $public
    }
    foreach ($entry in $trust.GetEnumerator()) {
        if ($zip.GetEntry($entry.Key)) { throw 'Client template already contains deployment-specific trust settings.' }
        $writer = [IO.StreamWriter]::new($zip.CreateEntry($entry.Key).Open(), $utf8)
        try { $writer.Write($entry.Value) } finally { $writer.Dispose() }
    }
} finally { $zip.Dispose() }
[IO.File]::WriteAllText("$starter.sha256", "$(Hash $starter)  $([IO.Path]::GetFileName($starter))`n", $utf8)
foreach ($name in $sourcePaths.Keys) { Assert-FileHash $sourcePaths[$name] $sourceHashes[$name] }
if ((Json @(Get-StageInventory $stage)) -cne (Json $stageInventory)) { throw 'Mod-fix source changed during candidate build; preserve and review this attempt.' }
$next = Read-UpdateText "$candidate/Published/APFClientUpdate.json" | ConvertFrom-Json
$report = [ordered]@{ version = $version; baseVersion = $baseVersion; createdAtUtc = [DateTime]::UtcNow.ToString('o');
    signedFiles = @($next.files).Count; modFixStage = $stage; sourceHashes = $sourceHashes; modFixInputFiles = $stageInventory;
    texture4K8KDeclarationsUnchanged = $true; unrelatedPackageDeclarationsUnchanged = $true;
    serverManagedPackagesPreservedEmpty = $true; serverClientOnlyDllPinsPreserved = $true;
    serverWaterfallMatchesBothProfiles = $true; profileManifestsInSignedCatalog = $true; nativeUiIncluded = $false;
    baselineCompressionRuntimePreserved = $true;
    releaseSha256 = (Hash $releaseZip); starterSha256 = (Hash $starter); mediaFiles = $mediaFiles;
    mediaPreparationRequired = @($mediaFiles | Where-Object { !$_.alreadyPrepared }).Count -gt 0;
    mediaPreparationPlan = 'Candidate/MEDIA-PREPARATION.json'; liveWrites = $false; notActivated = $true }
Write-Json $resultPath $report
Write-Warning 'Candidate only. Before publication, complete Candidate/MEDIA-PREPARATION.json separately; release ZIP does not import media.'
$report | ConvertTo-Json -Depth 60
