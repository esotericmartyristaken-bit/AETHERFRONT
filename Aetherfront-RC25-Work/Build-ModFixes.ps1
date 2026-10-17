#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CatalogPath = (Join-Path $PSScriptRoot '../KESAS-VPS-Server/.github-prepared/kesas-rc24/install-catalog.json'),
    [string]$MediaDirectory = (Join-Path $PSScriptRoot '../KESAS-VPS-Server/Server/Config'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'ModFixes/Staged-RC25'),
    [string]$PackageBaseUrl = 'https://217.216.49.250:18866/',
    [string]$Version = 'kesas-rc25'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'ModFixes'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$catalogFile = (Resolve-Path -LiteralPath $CatalogPath).Path
$media = (Resolve-Path -LiteralPath $MediaDirectory).Path
$utf8 = [Text.UTF8Encoding]::new($false)
$cecilPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net472/Mono.Cecil.dll'))
Add-Type -Path $cecilPath
. (Join-Path $work 'Patch-Waterfall.ps1')
if (!$output.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'All output must stay inside Aetherfront-RC25-Work/ModFixes.'
}
if (Test-Path -LiteralPath $output) { throw 'Immutable mod-fix output exists; select a new output directory.' }
if ($Version -cne 'kesas-rc25') { throw 'This staging script is restricted to kesas-rc25.' }
$baseUri = [uri]$PackageBaseUrl
if (!$baseUri.IsAbsoluteUri -or $baseUri.Scheme -cne 'https' -or !$PackageBaseUrl.EndsWith('/')) {
    throw 'PackageBaseUrl must be an absolute HTTPS directory URL ending with /.'
}
foreach ($directory in @($work, [IO.Path]::GetDirectoryName($output))) {
    for ($probe = [IO.DirectoryInfo]::new($directory); $null -ne $probe; $probe = $probe.Parent) {
        if ($probe.Exists -and ($probe.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing reparse-point output ancestor: $($probe.FullName)"
        }
    }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 60) + "`n", $utf8)
}
function Read-ZipText($Zip, [string]$Name) {
    $entry = $Zip.GetEntry($Name)
    if (!$entry) { throw "Missing archive entry: $Name" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Write-DeterministicZip([string]$Source, [string]$Path) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse | Sort-Object FullName) {
            $relative = $file.FullName.Substring($Source.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $dest = $entry.Open(); $src = [IO.File]::OpenRead($file.FullName)
            try { $src.CopyTo($dest) } finally { $src.Dispose(); $dest.Dispose() }
        }
    } finally { $zip.Dispose() }
}
$catalogHash = Hash $catalogFile
$catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
if ($catalog.version -cne 'kesas-rc24') { throw 'Expected the actual RC24 baseline catalog.' }
$potato = @($catalog.potato.managedPackages | Where-Object stripPrefix -CEQ 'ZZZ_KESASPotato')
if ($potato.Count -ne 1) { throw 'Expected one RC24 potato policy package.' }
$oldPolicy = $potato[0]
$policyArchive = Join-Path $media ([IO.Path]::GetFileName(([uri]$oldPolicy.archiveUrl).AbsolutePath))
if ((Hash $policyArchive) -cne $oldPolicy.sha256) { throw 'RC24 policy archive hash mismatch.' }
$baselinePolicyHash = Hash $policyArchive
$zip = [IO.Compression.ZipFile]::OpenRead($policyArchive)
try {
    if (@($zip.Entries).Count -ne 1 -or !$zip.GetEntry('ZZZ_KESASPotato/DisableExhaust.cfg')) {
        throw 'Unexpected policy archive contents; do not discard unreviewed files.'
    }
    $oldPolicyText = Read-ZipText $zip 'ZZZ_KESASPotato/DisableExhaust.cfg'
} finally { $zip.Dispose() }
if ($oldPolicyText -notmatch 'FOR\[SmokeScreen\]' -or $oldPolicyText -notmatch 'FOR\[StockWaterfallEffects\]') {
    throw 'Expected RC24 fake visual-mod markers were not found.'
}
$lock = Get-Content -LiteralPath (Join-Path $work 'Dependencies.lock.json') -Raw | ConvertFrom-Json
$dep = $lock.harmony
$inputs = Join-Path $work 'Inputs'
[void][IO.Directory]::CreateDirectory($inputs)
$download = Join-Path $inputs ("HarmonyKSP-2.2.1.0-" + $dep.sha256 + '.zip')
if (!(Test-Path -LiteralPath $download)) {
    Invoke-WebRequest -Uri $dep.url -OutFile $download -TimeoutSec 60
}
if ((Get-Item -LiteralPath $download).Length -ne $dep.sizeBytes -or (Hash $download) -cne $dep.sha256) {
    throw 'Upstream Harmony release failed pinned CKAN SHA256/size verification; retain failed input for inspection.'
}
[void][IO.Directory]::CreateDirectory($output)
$overlay = Join-Path $output 'ClientOverlay'
$mediaOut = Join-Path $output 'Media'
$packageSource = Join-Path $output 'PackageSource'
foreach ($dir in @($overlay, $mediaOut, $packageSource)) { [void][IO.Directory]::CreateDirectory($dir) }
$zip = [IO.Compression.ZipFile]::OpenRead($download)
$harmonyFiles = @()
try {
    foreach ($required in $dep.requiredFiles) {
        if (!$zip.GetEntry($required)) { throw "Required upstream dependency file absent: $required" }
    }
    foreach ($entry in $zip.Entries) {
        if (!$entry.Name) { continue }
        if ($entry.FullName -notmatch '^GameData/000_Harmony/[^/]+$') { throw "Unexpected upstream archive path: $($entry.FullName)" }
        $relative = $entry.FullName
        $dest = Join-Path $overlay $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
        $source = $entry.Open(); $target = [IO.File]::Create($dest)
        try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        $harmonyFiles += [ordered]@{ path = $relative; bytes = $entry.Length; sha256 = (Hash $dest) }
    }
} finally { $zip.Dispose() }
$harmonyVersion = Get-Content -LiteralPath (Join-Path $overlay 'GameData/000_Harmony/Harmony.version') -Raw | ConvertFrom-Json
if ($harmonyVersion.VERSION.MAJOR -ne 2 -or $harmonyVersion.VERSION.MINOR -ne 2 -or $harmonyVersion.VERSION.PATCH -ne 1) {
    throw 'Unexpected Harmony version metadata.'
}
$packages = @()
$waterfallAudit = @()
foreach ($root in @('ZZZ_KESASPotato', 'ZZZ_KESASModFixes')) {
    $sourceRoot = Join-Path $work "Source/$root"
    $stagedRoot = Join-Path $packageSource $root
    [void][IO.Directory]::CreateDirectory($stagedRoot)
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        $dest = Join-Path $stagedRoot $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
        [IO.File]::Copy($file.FullName, $dest, $false)
    }
    $temporary = Join-Path $mediaOut "$root.zip"
    # Include the package root: installer strips exactly this prefix.
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stagedRoot -File -Recurse | Sort-Object FullName) {
            $relative = $file.FullName.Substring($packageSource.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $src = [IO.File]::OpenRead($file.FullName); $dst = $entry.Open()
            try { $src.CopyTo($dst) } finally { $dst.Dispose(); $src.Dispose() }
        }
    } finally { $zip.Dispose() }
    $hash = Hash $temporary
    $slug = if ($root -ceq 'ZZZ_KESASPotato') { 'potato-policy' } else { 'modfixes' }
    $name = "kesas-rc25-$slug-$hash.zip"
    $final = Join-Path $mediaOut $name
    [IO.File]::Move($temporary, $final)
    $id = if ($root -ceq 'ZZZ_KESASPotato') { $oldPolicy.id } else { 'kesas-rc25-modfixes' }
    $declaration = [pscustomobject][ordered]@{
        id = $id; version = ('sha-' + $hash.Substring(0, 16)); archiveUrl = ($PackageBaseUrl + $name)
        sha256 = $hash; sizeBytes = (Get-Item -LiteralPath $final).Length
        destination = 'GameData'; stripPrefix = $root
    }
    $expanded = [long](Get-ChildItem -LiteralPath $stagedRoot -File -Recurse | Measure-Object Length -Sum).Sum
    $packages += [pscustomobject]@{ declaration = $declaration; path = $final; expandedBytes = $expanded }
}
foreach ($profileName in @('full', 'potato')) {
    $originals = @($catalog.$profileName.managedPackages | Where-Object stripPrefix -CEQ 'Waterfall')
    if ($originals.Count -ne 1) { throw 'Expected one Waterfall package per profile.' }
    $original = $originals[0]
    $archive = Join-Path $media ([IO.Path]::GetFileName(([uri]$original.archiveUrl).AbsolutePath))
    if ((Hash $archive) -cne $original.sha256) { throw "Waterfall baseline hash mismatch: $profileName" }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    $temporary = Join-Path $mediaOut "Waterfall-$profileName.zip"
    $target = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    $expanded = [long]0
    try {
        $dlls = @($zip.Entries | Where-Object FullName -CEQ 'Waterfall/Plugins/Waterfall.dll')
        if ($dlls.Count -ne 1) { throw 'Waterfall assembly archive entry is not unique.' }
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            if ($entry.FullName -notmatch '^Waterfall/' -or $entry.FullName -match '(^|/)\.\.(/|$)|\\') { throw 'Unsafe Waterfall archive path.' }
            $read = $entry.Open(); $bytes = [IO.MemoryStream]::new()
            try { $read.CopyTo($bytes); $data = $bytes.ToArray() } finally { $bytes.Dispose(); $read.Dispose() }
            if ($entry.FullName -ceq 'Waterfall/Plugins/Waterfall.dll') {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $inputHash = [Convert]::ToHexString($sha.ComputeHash($data)).ToLowerInvariant() } finally { $sha.Dispose() }
                $data = New-WaterfallGameDataBinary $data
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $outputHash = [Convert]::ToHexString($sha.ComputeHash($data)).ToLowerInvariant() } finally { $sha.Dispose() }
                $waterfallAudit += [ordered]@{ profile = $profileName; sourceArchive = $archive; sourceSha256 = $original.sha256; inputDllSha256 = $inputHash; outputDllSha256 = $outputHash; changedMethods = @('Waterfall.WaterfallParticleLoader.LoadParticles', 'Waterfall.ShaderLoader.LoadShaders'); otherMethodsUnchanged = $true }
            }
            $expanded += $data.Length
            $newEntry = $target.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
            $newEntry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $write = $newEntry.Open()
            try { $write.Write($data, 0, $data.Length) } finally { $write.Dispose() }
        }
    } finally { $target.Dispose(); $zip.Dispose() }
    $hash = Hash $temporary
    $name = "kesas-rc25-waterfall-$profileName-$hash.zip"
    $final = Join-Path $mediaOut $name
    [IO.File]::Move($temporary, $final)
    $declaration = [pscustomobject][ordered]@{
        id = $original.id; version = ('sha-' + $hash.Substring(0, 16)); archiveUrl = ($PackageBaseUrl + $name)
        sha256 = $hash; sizeBytes = (Get-Item -LiteralPath $final).Length; destination = 'GameData'; stripPrefix = 'Waterfall'
    }
    $catalog.$profileName.managedPackages = @(foreach ($p in $catalog.$profileName.managedPackages) {
        if ($p.stripPrefix -ceq 'Waterfall') { $declaration } else { $p }
    })
    $packages += [pscustomobject]@{ declaration = $declaration; path = $final; expandedBytes = $expanded }
}
$newPolicy = ($packages | Where-Object { $_.declaration.stripPrefix -ceq 'ZZZ_KESASPotato' }).declaration
$common = ($packages | Where-Object { $_.declaration.stripPrefix -ceq 'ZZZ_KESASModFixes' }).declaration
$catalog.version = $Version
foreach ($profileName in @('full', 'potato')) {
    $profile = $catalog.$profileName
    if (@($profile.managedPackages | Where-Object stripPrefix -CEQ 'ZZZ_KESASModFixes').Count) { throw 'Common mod fixes already exist.' }
    $profile.packVersion = $Version; $profile.requiredClientVersion = $Version
    if ($profileName -ceq 'potato') {
        $profile.managedPackages = @(foreach ($p in $profile.managedPackages) {
            if ($p.stripPrefix -ceq 'ZZZ_KESASPotato') { $newPolicy } else { $p }
        })
    }
    $profile.managedPackages = @($profile.managedPackages) + @($common)
}
$changedIds = @($packages | ForEach-Object { $_.declaration.id })
$catalog.sizes = @($catalog.sizes | Where-Object { $_.id -cnotin $changedIds }) + @(
    foreach ($package in $packages) { [pscustomobject]@{ id = $package.declaration.id; expandedBytes = $package.expandedBytes } }
)
$catalogOverlayPath = Join-Path $overlay 'GameData/LunaMultiplayer/Launcher/install-catalog.json'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($catalogOverlayPath))
Write-Json $catalogOverlayPath $catalog
Write-Json (Join-Path $output 'install-catalog.json') $catalog
Write-DeterministicZip $overlay (Join-Path $output 'ClientOverlay.zip')
$plan = [ordered]@{
    schemaVersion = 1; version = $Version; notActivated = $true
    baseline = [ordered]@{ catalog = $catalogFile; catalogSha256 = $catalogHash; policyArchive = $policyArchive; policySha256 = $baselinePolicyHash }
    dependency = [ordered]@{ source = $dep.url; sha256 = $dep.sha256; checksumSource = $dep.checksumSource; files = $harmonyFiles }
    waterfall = $waterfallAudit
    patchTool = [ordered]@{ path = $cecilPath; sha256 = (Hash $cecilPath) }
    media = @($packages | ForEach-Object { [ordered]@{ path = $_.path; declaration = $_.declaration; expandedBytes = $_.expandedBytes } })
    overlay = [ordered]@{ directory = $overlay; archive = (Join-Path $output 'ClientOverlay.zip'); sha256 = (Hash (Join-Path $output 'ClientOverlay.zip')) }
    preserve = @('FarFutureTechnologies', 'NearFutureLaunchVehicles', 'WarpPlugin', 'InterstellarFuelSwitch', 'PersistentThrust', 'TweakScale', 'TSSafetyNet')
}
Write-Json (Join-Path $output 'MODFIX-PLAN.json') $plan
if ((Hash $catalogFile) -cne $catalogHash -or (Hash $policyArchive) -cne $baselinePolicyHash) { throw 'Read-only RC24 baseline changed during staging.' }
Write-Output ("Staged RC25 mod fixes: " + $output)
