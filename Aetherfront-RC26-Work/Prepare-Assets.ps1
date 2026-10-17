#requires -Version 7.3
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Assets/Staged'),
    [string]$PackageBaseUrl = 'https://217.216.49.250:18866/'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assets/Source/AssetStage.Common.ps1')
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Assets'))
$stage = Resolve-AssetStage $assets $OutputDirectory
Assert-Asset (!(Test-Path -LiteralPath $stage)) 'Immutable asset stage already exists; select a new Assets/Staged-<name>.'
$uri = [uri]$PackageBaseUrl
Assert-Asset ($uri.IsAbsoluteUri -and $uri.Scheme -ceq 'https' -and $PackageBaseUrl.EndsWith('/') -and !$uri.UserInfo -and !$uri.Query -and !$uri.Fragment) 'PackageBaseUrl must be a credential-free HTTPS directory URL.'
# All validation is completed before creating any output directory.
$preflight = & (Join-Path $PSScriptRoot 'Test-Assets.ps1') -ValidateSourceOnly -PassThru
Assert-Asset ($preflight.passed) 'Source preflight did not pass.'
$inputs = Read-AssetInputs $PSScriptRoot
$lock = $inputs.lock
$bindingPath = Join-Path $inputs.source 'ZZZ_KESASModFixes/StaticTextureBindings.cfg'
$bindingBytes = [IO.File]::ReadAllBytes($bindingPath)
$mediaDirectory = Join-Path $stage 'Media'
[void][IO.Directory]::CreateDirectory($mediaDirectory)
$media = [Collections.Generic.List[object]]::new()
foreach ($package in $lock.packages) {
    $parent = Resolve-AssetInput $inputs.workspace $package.archivePath
    $archive = [IO.Compression.ZipFile]::OpenRead($parent)
    $additions = [Collections.Generic.List[object]]::new()
    $proofs = [Collections.Generic.List[object]]::new()
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            Assert-AssetEntryName $entry.FullName $package.root
            Assert-Asset ($names.Add($entry.FullName)) "Duplicate or case-colliding parent entry: $($entry.FullName)"
        }
        foreach ($alias in $package.aliases) {
            Assert-AssetEntryName $alias.addedEntry $package.root
            Assert-Asset ($names.Add($alias.addedEntry)) "Alias already exists or collides: $($alias.addedEntry)"
            $bytes = Get-AssetEntryBytes $archive $alias.sourceEntry
            Assert-Asset ((Get-AssetBytesHash $bytes) -ceq $alias.sha256) "Alias source hash mismatch: $($alias.sourceEntry)"
            $additions.Add([pscustomobject]@{ name = $alias.addedEntry; bytes = $bytes })
            $proofs.Add([ordered]@{ kind = 'byte-identical-alias'; sourceEntry = $alias.sourceEntry; addedEntry = $alias.addedEntry; sha256 = $alias.sha256; sizeBytes = $bytes.Length })
        }
        if ($package.root -ceq 'ZZZ_KESASModFixes') {
            $preserved = Get-AssetEntryBytes $archive $package.preservedEntry
            Assert-Asset ((Get-AssetBytesHash $preserved) -ceq $package.preservedEntrySha256) 'OptionalVisualModules.cfg changed.'
            $name = 'ZZZ_KESASModFixes/StaticTextureBindings.cfg'
            Assert-Asset ($names.Add($name)) 'The new binding file already exists in the parent.'
            $additions.Add([pscustomobject]@{ name = $name; bytes = $bindingBytes })
            $proofs.Add([ordered]@{ kind = 'owned-config'; sourcePath = 'Assets/Source/ZZZ_KESASModFixes/StaticTextureBindings.cfg'; addedEntry = $name; sha256 = (Get-AssetBytesHash $bindingBytes); sizeBytes = $bindingBytes.Length })
        }
        $expandedBefore = [long]($archive.Entries | Measure-Object Length -Sum).Sum
        $oldEntryCount = $archive.Entries.Count
    } finally { $archive.Dispose() }
    $temporary = Join-Path $mediaDirectory ($package.root.ToLowerInvariant() + '.pending.zip')
    New-AssetAppendedZip $parent $temporary $additions
    $hash = Get-AssetHash $temporary
    $slug = if ($package.root -ceq 'ZZZ_KESASModFixes') { 'modfixes' } else { $package.root.ToLowerInvariant() }
    $filename = "kesas-rc26-assets-$slug-$hash.zip"
    $final = Join-Path $mediaDirectory $filename
    [IO.File]::Move($temporary, $final)
    $declaration = [ordered]@{
        id = $package.id; version = ('sha-' + $hash.Substring(0, 16)); archiveUrl = ($PackageBaseUrl + $filename)
        sha256 = $hash; sizeBytes = [IO.FileInfo]::new($final).Length
        destination = 'GameData'; stripPrefix = $package.root
    }
    $oldDeclaration = @($inputs.catalog.full.managedPackages | Where-Object id -CEQ $package.id)[0]
    $media.Add([ordered]@{
        profiles = @('full', 'potato'); filename = $filename; declaration = $declaration
        oldDeclaration = $oldDeclaration; oldSha256 = $package.sha256; oldSizeBytes = $package.sizeBytes
        sourceArchivePath = $package.archivePath; sourceEntryCount = $oldEntryCount
        addedEntries = @($proofs.ToArray()); preservedWholeArchivePrefixBytes = $package.sizeBytes
        expandedBytesBefore = $expandedBefore
        expandedBytesAfter = $expandedBefore + [long]($additions | ForEach-Object { $_.bytes.Length } | Measure-Object -Sum).Sum
    })
}
$profileChanges = @()
$targetIds = @($lock.packages.id)
foreach ($profile in @('full', 'potato')) {
    $untouched = @($inputs.catalog.$profile.managedPackages | Where-Object { $_.id -cnotin $targetIds })
    $changes = @($media | ForEach-Object {
        [ordered]@{ packageId = $_.declaration.id; oldSha256 = $_.oldSha256; newSha256 = $_.declaration.sha256; sizeBytes = $_.declaration.sizeBytes; filename = $_.filename }
    })
    $profileChanges += [ordered]@{
        profile = $profile; changes = $changes; unchangedPackageCount = $untouched.Count
        unchangedDeclarationsSha256 = Get-AssetBytesHash ([Text.Encoding]::UTF8.GetBytes((ConvertTo-AssetJson $untouched)))
    }
}
$sources = @('Assets/Source/AssetInputs.lock.json', 'Assets/Source/AssetStage.Common.ps1', 'Assets/Source/ZZZ_KESASModFixes/StaticTextureBindings.cfg', 'Prepare-Assets.ps1', 'Test-Assets.ps1')
$sourceHashes = @($sources | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-AssetHash (Join-Path $PSScriptRoot $_)) } })
$plan = [ordered]@{
    schemaVersion = 1; version = 'kesas-rc26'; kind = 'signed-catalog-change-plan'
    signed = $false; requiresReleaseOwnerSignature = $true; published = $false; installed = $false
    signatureNote = 'This immutable local delta is input to the release owner. It is not a signed catalog and must not be trusted as a published catalog.'
    sourceCatalogPath = $lock.sourceCatalogPath; sourceCatalogSha256 = $lock.sourceCatalogSha256
    allowedDeclarationChanges = @('version', 'archiveUrl', 'sha256', 'sizeBytes')
    preservation = 'append-only-parent-prefix-and-exact-central-directory-records'
    media = @($media.ToArray()); profileChanges = $profileChanges; bindings = @($lock.bindings)
    sourceFiles = $sourceHashes
    residuals = @(
        'ExtraplanetaryLaunchpads Smelter_NRM remains absent; no guessed replacement.',
        'Interstellar white, Gray-4 and ThermalNozzleFusion Gray-Stainless remain unresolved.',
        'WarpPlugin commonparts and BFR_SPNRM remain unresolved.',
        'NewLab.tga SetPixels32 loader failure remains unresolved.',
        'KK CompileID, concave triggers and AttachFacilities warnings remain unverified.',
        'KerbalFoundries legacy APU and FreeIva SIMPLEX dormant-model classification remains incomplete.',
        'Laser drag-render animation failures remain unverified.',
        'Installation and Unity rendering of these staged fixes have not been tested.'
    )
}
$planPath = Join-Path $stage 'ASSET-PLAN.json'
Write-AssetNewText $planPath (($plan | ConvertTo-Json -Depth 60) + "`n")
$planHash = Get-AssetHash $planPath
Write-AssetNewText ($planPath + '.sha256') ($planHash + '  ASSET-PLAN.json' + "`n")
$result = & (Join-Path $PSScriptRoot 'Test-Assets.ps1') -StageDirectory $stage -PassThru
Assert-Asset ($result.passed) 'The prepared asset stage failed verification.'
$resultPath = Join-Path $stage 'ASSET-TEST-RESULT.json'
Write-AssetNewText $resultPath (($result | ConvertTo-Json -Depth 30) + "`n")
foreach ($path in @($planPath, ($planPath + '.sha256'), $resultPath) + @($media | ForEach-Object { Join-Path $mediaDirectory $_.filename })) {
    [IO.File]::SetAttributes($path, ([IO.File]::GetAttributes($path) -bor [IO.FileAttributes]::ReadOnly))
}
[ordered]@{ stage = $stage; planPath = $planPath; planSha256 = $planHash; testResultPath = $resultPath; testResultSha256 = (Get-AssetHash $resultPath); packages = 5; aliases = 7; bindings = 8; signed = $false; published = $false } | ConvertTo-Json
