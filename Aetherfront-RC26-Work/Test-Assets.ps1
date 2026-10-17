#requires -Version 7.3
[CmdletBinding()]
param(
    [string]$StageDirectory = (Join-Path $PSScriptRoot 'Assets/Staged'),
    [switch]$ValidateSourceOnly,
    [switch]$PassThru
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assets/Source/AssetStage.Common.ps1')

function Assert-StaticBindings([string]$Text) {
    $clean = [regex]::Replace($Text, '//[^\r\n]*', '')
    $pattern = '(?s)@STATIC\[(?<static>[^\]]+)\]:HAS\[#(?<marker>OSSMAT|TSCMAT)\[1\]\]:NEEDS\[KerbalKonstructs\]:FINAL\s*\{\s*MODULE\s*\{(?<fields>[^{}]*)\}\s*\}'
    $matches = [regex]::Matches($clean, $pattern)
    Assert-Asset ($matches.Count -eq 8 -and [regex]::Replace($clean, $pattern, '').Trim().Length -eq 0) 'Bindings must contain exactly eight precise STATIC patches and no other syntax.'
    $expected = @(
        'AtlasVIF|OSSMAT|model_pad_pipe|BUILTIN:/ksp_pad_pipes_diff|',
        'SmallRadomeOutpost|OSSMAT|model_roof_model|BUILTIN:/model_vab_exterior_tile_04_roof|',
        'TLC_34|TSCMAT|model-vab_interior_props|BUILTIN:/model_vab_interior_props|',
        'TLC_34|TSCMAT|ksp_pad_cylTank_diff_2|BUILTIN:/ksp_pad_cylTank_diff|',
        'TLC_36|TSCMAT|model_vab_exterio_tile_00|BUILTIN:/model_vab_exterior_tile_00|',
        'KSC-40A|TSCMAT|model_vab_exterior_tole_04|BUILTIN:/model_vab_exterior_tile_04_roof|',
        'KSC-40A|TSCMAT|model_vab_exterior_lines-01|BUILTIN:/model_vab_exterior_lines_01|',
        'KSC-40A|TSCMAT|model_vab_exterior_details|BUILTIN:/model_vab_exterior_details_01|BUILTIN:/model_vab_exterior_details_01_emissive'
    )
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($match in $matches) {
        $fields = @{}
        foreach ($line in $match.Groups['fields'].Value -split '\r?\n' | Where-Object { $_.Trim() }) {
            Assert-Asset ($line -cmatch '^\s*(name|namespace|transforms|_MainTex|_Emissive)\s*=\s*(\S+)\s*$') "Unapproved binding property: $line"
            $key = $Matches[1]; $value = $Matches[2]
            Assert-Asset (!$fields.ContainsKey($key)) "Duplicate binding property: $key"
            $fields[$key] = $value
        }
        Assert-Asset ($fields.name -ceq 'AdvancedTextures' -and $fields.namespace -ceq 'KerbalKonstructs' -and $fields.ContainsKey('transforms') -and $fields.ContainsKey('_MainTex')) 'Wrong binding module or missing property.'
        $signature = @($match.Groups['static'].Value, $match.Groups['marker'].Value, $fields.transforms, $fields._MainTex, $fields['_Emissive']) -join '|'
        Assert-Asset ($signature -cin $expected -and $seen.Add($signature)) "Unapproved or duplicate binding target: $signature"
    }
}

function Assert-AssetRejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-Asset $rejected "Negative test did not reject $Name"
}

$inputs = Read-AssetInputs $PSScriptRoot
$lock = $inputs.lock
Assert-Asset ($lock.sourceCatalogSha256 -ceq 'dc96d70b45804d5266f1b1c5f8575c55e7d448f1996721c509efc4b3c5b18f97') 'Unexpected catalog pin.'
$expectedParents = @(
    'ModularLaunchPads|kesas-modularlaunchpads-8feabdb7|edc997f01e4c591560c907f3a19cbe23daaf2dc6e2322c170921cc368e4682d7|69825739',
    'UmbraSpaceIndustries|kesas-umbraspaceindustries-2ed9115b|18010ae46eae10b8a819bbfc0058b7148ca4abe1fa858504fccd85a0728819b6|115850116',
    'TundraSpaceCenter|kesas-tundraspacecenter-7018b381|8a4b4a4ef108f77c8697dbd53d5b3361b3948b32183f9543d9bda44f89ced64b|34108881',
    'InterstellarTechnologies|kesas-interstellartechnologies-173d8c64|8e80ee9bdf0dd6f17afd0e9f3be3e726b861566884c87402d519a51149359b76|32422069',
    'ZZZ_KESASModFixes|kesas-rc25-modfixes|3b6268ae33660c64903813fdaf95eb896aa32b677bc223493622b7c625342698|520'
)
$expectedAliases = @(
    'ModularLaunchPads/Launch_Stands/LaunchStands01.dds|ModularLaunchPads/Launch_Plates/LaunchStands01.dds',
    'ModularLaunchPads/Launch_Stands/LaunchStands01.dds|ModularLaunchPads/Saturn_Shuttle/LaunchStands01.dds',
    'UmbraSpaceIndustries/FTT/Assets/SAS_512.png|UmbraSpaceIndustries/FTT/Assets/SAS_PrimaryMat_high_BaseColor.png',
    'UmbraSpaceIndustries/ExpPack/PackRat/Assets/PackRat_Seat.dds|UmbraSpaceIndustries/ExpPack/PackRat/Assets/model000.dds',
    'TundraSpaceCenter/Pads/TSC_39/LC40-tower.dds|TundraSpaceCenter/Pads/TSC_41/LC40-tower.dds',
    'InterstellarTechnologies/Parts/Utility/Deuteriumproductionplant/Textured-Stainless.png|InterstellarTechnologies/Parts/Utility/Deuteriumproductionplant/Textured-Stainless,png.png',
    'InterstellarTechnologies/Parts/Utility/Helium3productionplant/Textured-Stainless.png|InterstellarTechnologies/Parts/Utility/Helium3productionplant/Textured-Stainless,png.png'
)
$actualParents = @($lock.packages | ForEach-Object { @($_.root, $_.id, $_.sha256, $_.sizeBytes) -join '|' })
$actualAliases = @($lock.packages | ForEach-Object { $_.aliases } | ForEach-Object { @($_.sourceEntry, $_.addedEntry) -join '|' })
Assert-Asset (($actualParents -join "`n") -ceq ($expectedParents -join "`n")) 'Parent allowlist changed.'
Assert-Asset (($actualAliases -join "`n") -ceq ($expectedAliases -join "`n")) 'Seven-alias allowlist changed.'
$bindingPath = Join-Path $inputs.source 'ZZZ_KESASModFixes/StaticTextureBindings.cfg'
$bindingText = [IO.File]::ReadAllText($bindingPath)
Assert-StaticBindings $bindingText
$lockSignatures = @($lock.bindings | ForEach-Object { @($_.staticName, $_.marker, $_.transform, $_.mainTexture, $_.emissiveTexture) -join '|' })
Assert-Asset (($lockSignatures | Sort-Object -Unique).Count -eq 8) 'Binding proof matrix is not unique.'
foreach ($binding in $lock.bindings) {
    Assert-Asset ($bindingText.Contains("@STATIC[$($binding.staticName)]:HAS[#$($binding.marker)[1]]") -and $bindingText.Contains("transforms = $($binding.transform)") -and $bindingText.Contains("_MainTex = $($binding.mainTexture)")) 'Binding proof matrix does not match source.'
}
Assert-AssetRejected { Assert-StaticBindings ($bindingText.Replace('model_pad_pipe', 'model_pad_pipes')) } 'a guessed transform'
Assert-AssetRejected { Assert-StaticBindings ($bindingText + "`nKESAS_COMPAT:FOR[SmokeScreen] {}`n") } 'a fake FOR marker'
Assert-AssetRejected { Assert-StaticBindings ($bindingText + "`n@PART[*] {}`n") } 'a wildcard part patch'
Assert-AssetRejected { Assert-StaticBindings ($bindingText + $bindingText) } 'duplicate bindings'
Assert-AssetRejected { Assert-AssetEntryName 'ModularLaunchPads/../escape.png' 'ModularLaunchPads' } 'entry traversal'
Assert-AssetRejected { Assert-AssetEntryName 'OtherRoot/texture.png' 'ModularLaunchPads' } 'the wrong strip root'
$probeAdditions = @([pscustomobject]@{ name = 'Root/test.txt'; bytes = [Text.Encoding]::UTF8.GetBytes('deterministic') })
Assert-Asset ((Get-AssetBytesHash (New-AssetAdditionZip $probeAdditions)) -ceq (Get-AssetBytesHash (New-AssetAdditionZip $probeAdditions))) 'Addition ZIP generation is not deterministic.'
if ($ValidateSourceOnly) {
    $result = [ordered]@{ passed = $true; sourceOnly = $true; packages = 5; aliases = 7; bindings = 8; negativeTests = 6; deterministicAdditionZip = $true }
    if ($PassThru) { return [pscustomobject]$result }
    $result | ConvertTo-Json; return
}
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Assets'))
$stage = Resolve-AssetStage $assets $StageDirectory
$planPath = Join-Path $stage 'ASSET-PLAN.json'
$planHash = Get-AssetHash $planPath
Assert-Asset (([IO.File]::ReadAllText($planPath + '.sha256').Trim()) -ceq ($planHash + '  ASSET-PLAN.json')) 'Plan hash sidecar mismatch.'
$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
Assert-Asset ($plan.schemaVersion -eq 1 -and $plan.version -ceq 'kesas-rc26' -and $plan.media.Count -eq 5 -and !$plan.signed -and $plan.requiresReleaseOwnerSignature -and !$plan.published -and !$plan.installed) 'Invalid stage plan or false signing/publication claim.'
Assert-Asset ($plan.sourceCatalogPath -ceq $lock.sourceCatalogPath -and $plan.sourceCatalogSha256 -ceq $lock.sourceCatalogSha256) 'Plan source catalog mismatch.'
Assert-Asset ((ConvertTo-AssetJson $plan.bindings) -ceq (ConvertTo-AssetJson @($lock.bindings))) 'Plan binding proof matrix changed.'
foreach ($source in $plan.sourceFiles) {
    Assert-Asset ($source.path -cin @('Assets/Source/AssetInputs.lock.json', 'Assets/Source/AssetStage.Common.ps1', 'Assets/Source/ZZZ_KESASModFixes/StaticTextureBindings.cfg', 'Prepare-Assets.ps1', 'Test-Assets.ps1')) 'Unexpected source path in plan.'
    Assert-Asset ((Get-AssetHash (Join-Path $PSScriptRoot $source.path)) -ceq $source.sha256) "Prepared source changed: $($source.path)"
}
Assert-Asset (@($plan.sourceFiles.path | Sort-Object -Unique).Count -eq 5) 'Incomplete source provenance.'
$checks = [Collections.Generic.List[object]]::new()
$totalEntries = 0; $totalAliases = 0
foreach ($package in $lock.packages) {
    $changes = @($plan.media | Where-Object { $_.declaration.id -ceq $package.id })
    Assert-Asset ($changes.Count -eq 1) "Missing or duplicate stage replacement: $($package.id)"
    $change = $changes[0]; $decl = $change.declaration
    Assert-Asset (($change.profiles -join ',') -ceq 'full,potato' -and $change.oldSha256 -ceq $package.sha256 -and $change.oldSizeBytes -eq $package.sizeBytes -and $change.sourceArchivePath -ceq $package.archivePath) 'Wrong replacement provenance or profile applicability.'
    $oldDecl = @($inputs.catalog.full.managedPackages | Where-Object id -CEQ $package.id)[0]
    Assert-Asset ((ConvertTo-AssetJson $change.oldDeclaration) -ceq (ConvertTo-AssetJson $oldDecl)) 'Old declaration was not preserved exactly.'
    Assert-Asset ($decl.destination -ceq 'GameData' -and $decl.stripPrefix -ceq $package.root -and $decl.version -ceq ('sha-' + $decl.sha256.Substring(0, 16))) 'Changed package identity, destination, strip prefix, or content version.'
    Assert-Asset (($decl.PSObject.Properties.Name -join ',') -ceq ($oldDecl.PSObject.Properties.Name -join ',')) 'Declaration schema changed.'
    Assert-Asset ($change.filename -cmatch '^kesas-rc26-assets-[a-z]+-[0-9a-f]{64}\.zip$' -and $change.filename.EndsWith('-' + $decl.sha256 + '.zip', [StringComparison]::Ordinal)) 'Media filename is not fully content-addressed.'
    $uri = [uri]$decl.archiveUrl
    Assert-Asset ($uri.Scheme -ceq 'https' -and !$uri.UserInfo -and !$uri.Query -and !$uri.Fragment -and [IO.Path]::GetFileName($uri.AbsolutePath) -ceq $change.filename) 'Wrong planned media URL.'
    $parent = Resolve-AssetInput $inputs.workspace $package.archivePath
    $replacement = Join-Path $stage ('Media/' + $change.filename)
    Assert-AssetNoReparse $replacement
    Assert-Asset ([IO.FileInfo]::new($replacement).Length -eq $decl.sizeBytes -and (Get-AssetHash $replacement) -ceq $decl.sha256) 'Replacement hash or length mismatch.'
    Assert-Asset ((Get-AssetPrefixHash $replacement $package.sizeBytes) -ceq $package.sha256) 'Original archive bytes or metadata were changed.'
    $oldLayout = Get-AssetZipLayout $parent; $newLayout = Get-AssetZipLayout $replacement
    $preservedDirectory = [byte[]]::new($oldLayout.centralBytes.Length)
    [Array]::Copy($newLayout.centralBytes, 0, $preservedDirectory, 0, $preservedDirectory.Length)
    Assert-Asset ((Get-AssetBytesHash $preservedDirectory) -ceq (Get-AssetBytesHash $oldLayout.centralBytes)) 'Original central-directory metadata was changed.'
    Assert-Asset ((Get-AssetBytesHash $newLayout.comment) -ceq (Get-AssetBytesHash $oldLayout.comment)) 'Archive comment changed.'
    $oldZip = [IO.Compression.ZipFile]::OpenRead($parent); $newZip = [IO.Compression.ZipFile]::OpenRead($replacement)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $newZip.Entries) {
            Assert-AssetEntryName $entry.FullName $package.root
            Assert-Asset ($names.Add($entry.FullName)) 'Duplicate or case-colliding output entry.'
        }
        $expandedBefore = 0L
        for ($i = 0; $i -lt $oldZip.Entries.Count; $i++) {
            $before = $oldZip.Entries[$i]; $after = $newZip.Entries[$i]
            Assert-Asset ($before.FullName -ceq $after.FullName -and $before.Length -eq $after.Length -and $before.CompressedLength -eq $after.CompressedLength -and $before.LastWriteTime -eq $after.LastWriteTime -and $before.ExternalAttributes -eq $after.ExternalAttributes) "Old entry metadata changed: $($before.FullName)"
            Assert-Asset ((Get-AssetEntryHash $before) -ceq (Get-AssetEntryHash $after)) "Old entry bytes changed: $($before.FullName)"
            $expandedBefore += $before.Length; $totalEntries++
        }
        $expectedAdded = @($package.aliases | ForEach-Object addedEntry)
        if ($package.root -ceq 'ZZZ_KESASModFixes') { $expectedAdded = @('ZZZ_KESASModFixes/StaticTextureBindings.cfg') }
        Assert-Asset ($newZip.Entries.Count -eq $oldZip.Entries.Count + $expectedAdded.Count) 'Unapproved ZIP inventory delta.'
        $actualAdded = @($newZip.Entries | Select-Object -Skip $oldZip.Entries.Count | ForEach-Object FullName)
        Assert-Asset (($actualAdded -join "`n") -ceq ($expectedAdded -join "`n")) 'Unexpected or reordered additions.'
        Assert-Asset ($change.addedEntries.Count -eq $expectedAdded.Count -and ($change.addedEntries.addedEntry -join "`n") -ceq ($expectedAdded -join "`n")) 'Plan inventory does not match approved additions.'
        foreach ($alias in $package.aliases) {
            $sourceHash = Get-AssetEntryHash ($oldZip.GetEntry($alias.sourceEntry))
            Assert-Asset ($sourceHash -ceq $alias.sha256 -and (Get-AssetEntryHash ($newZip.GetEntry($alias.addedEntry))) -ceq $sourceHash) "Alias is not byte-identical: $($alias.addedEntry)"
            $proof = @($change.addedEntries | Where-Object addedEntry -CEQ $alias.addedEntry)[0]
            Assert-Asset ($proof.kind -ceq 'byte-identical-alias' -and $proof.sourceEntry -ceq $alias.sourceEntry -and $proof.sha256 -ceq $sourceHash -and $proof.sizeBytes -eq $oldZip.GetEntry($alias.sourceEntry).Length) 'Alias proof mismatch.'
            $totalAliases++
        }
        if ($package.root -ceq 'ZZZ_KESASModFixes') {
            Assert-Asset ((Get-AssetEntryHash ($newZip.GetEntry($package.preservedEntry))) -ceq $package.preservedEntrySha256) 'OptionalVisualModules.cfg was changed.'
            $bytes = Get-AssetEntryBytes $newZip 'ZZZ_KESASModFixes/StaticTextureBindings.cfg'
            Assert-Asset ((Get-AssetBytesHash $bytes) -ceq (Get-AssetHash $bindingPath)) 'Packaged bindings differ from owned source.'
            Assert-StaticBindings ([Text.Encoding]::UTF8.GetString($bytes))
        }
        $expandedAfter = [long]($newZip.Entries | Measure-Object Length -Sum).Sum
        Assert-Asset ($change.sourceEntryCount -eq $oldZip.Entries.Count -and $change.preservedWholeArchivePrefixBytes -eq $package.sizeBytes -and $change.expandedBytesBefore -eq $expandedBefore -and $change.expandedBytesAfter -eq $expandedAfter) 'Plan counts or expanded sizes mismatch.'
        $checks.Add([ordered]@{ id = $package.id; oldSha256 = $package.sha256; newSha256 = $decl.sha256; filename = $change.filename; sizeBytes = $decl.sizeBytes; originalEntriesVerified = $oldZip.Entries.Count; addedEntriesVerified = $expectedAdded.Count; allOriginalBytesAndMetadataUnchanged = $true; profiles = @('full', 'potato') })
    } finally { $newZip.Dispose(); $oldZip.Dispose() }
}
Assert-Asset ($totalAliases -eq 7) 'Expected seven aliases.'
Assert-Asset (@(Get-ChildItem -LiteralPath (Join-Path $stage 'Media') -Force).Count -eq 5) 'Unexpected staged media or pending files.'
Assert-Asset ($plan.profileChanges.Count -eq 2) 'Expected exactly two profile change sets.'
foreach ($profile in @('full', 'potato')) {
    $sets = @($plan.profileChanges | Where-Object profile -CEQ $profile)
    Assert-Asset ($sets.Count -eq 1) 'Missing or duplicate profile plan.'
    $set = $sets[0]
    $untouched = @($inputs.catalog.$profile.managedPackages | Where-Object { $_.id -cnotin @($lock.packages.id) })
    Assert-Asset ($set.unchangedPackageCount -eq $untouched.Count -and $set.unchangedDeclarationsSha256 -ceq (Get-AssetBytesHash ([Text.Encoding]::UTF8.GetBytes((ConvertTo-AssetJson $untouched))))) 'Untouched package declarations changed.'
    Assert-Asset ($set.changes.Count -eq 5) 'Unexpected profile replacement count.'
    foreach ($media in $plan.media) {
        $rows = @($set.changes | Where-Object packageId -CEQ $media.declaration.id)
        Assert-Asset ($rows.Count -eq 1) 'Missing or duplicate profile package mapping.'
        $row = $rows[0]
        Assert-Asset ($row.oldSha256 -ceq $media.oldSha256 -and $row.newSha256 -ceq $media.declaration.sha256 -and $row.sizeBytes -eq $media.declaration.sizeBytes -and $row.filename -ceq $media.filename) 'Profile package mapping mismatch.'
    }
}
Assert-Asset ((Get-AssetHash $inputs.catalogPath) -ceq $lock.sourceCatalogSha256) 'Source catalog changed during verification.'
$result = [ordered]@{
    schemaVersion = 1; version = 'kesas-rc26'; passed = $true; planFilename = 'ASSET-PLAN.json'; planSha256 = $planHash
    sourceCatalogSha256 = $lock.sourceCatalogSha256; packagesVerified = 5; aliasesVerified = 7; preciseBindingsVerified = 8
    originalEntriesVerified = $totalEntries; originalZipPrefixesPreserved = $true; originalCentralDirectoryRecordsPreserved = $true
    bothProfilesUseIdenticalSharedVariants = $true; untouchedPackagesPreserved = $true; negativeTestsPassed = 6; deterministicAdditionZip = $true
    unityRenderingVerified = $false; signed = $false; published = $false; packages = @($checks.ToArray())
}
if ($PassThru) { return [pscustomobject]$result }
$result | ConvertTo-Json -Depth 30
