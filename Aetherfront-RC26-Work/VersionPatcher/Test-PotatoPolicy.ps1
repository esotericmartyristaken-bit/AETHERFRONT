#requires -Version 7.0
[CmdletBinding()]
param()
. "$PSScriptRoot/Review.Common.ps1"
$baseline = Get-RC26Baseline
$originalWork = $RC26Work
$attempt = New-RC26Attempt 'Test'
$checks = 0
function Check([bool]$Value,[string]$Message) { if (!$Value) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action,[string]$Name) {
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Check $rejected "Accepted invalid policy case: $Name"
}
# All synthetic inputs live inside this Test attempt. A real Build-Release starts
# with the real work root and therefore cannot consume these synthetic plans.
$RC26Work = $attempt.pending
try {
    $stage = Get-RC26Output 'ModFixes/Staged-RC26'
    New-UpdateDirectory "$stage/Media"; New-UpdateDirectory "$stage/Source"
    $entryPath = 'ZZZ_KESASPotato/DisableExhaust.cfg'
    $old = @($baseline.catalog.potato.managedPackages | Where-Object id -CEQ 'kesas-potato-policy')[0]
    $oldName = [IO.Path]::GetFileName(([uri]$old.archiveUrl).AbsolutePath)
    $oldZip = Get-UpdateChildPath $RC26Root "Server/Config/$oldName"
    $oldInventory = Get-RC26ZipInventory $oldZip
    $sourcePath = "$stage/Source/DisableExhaust.cfg"
    $bytes = $RC26Utf8.GetBytes('// Synthetic fixture, NOT an approved production MM repair.' + "`n")
    [IO.File]::WriteAllBytes($sourcePath,$bytes)
    $sourceHash = Get-RC26Hash $sourcePath
    $tempArchive = "$stage/Media/fixture.zip"
    $zip = [IO.Compression.ZipFile]::Open($tempArchive,[IO.Compression.ZipArchiveMode]::Create)
    try { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$sourcePath,$entryPath,[IO.Compression.CompressionLevel]::Optimal) }
    finally { $zip.Dispose() }
    $archiveHash = Get-RC26Hash $tempArchive
    $filename = "kesas-rc26-potato-policy-$archiveHash.zip"
    $archive = Get-UpdateChildPath $stage "Media/$filename"
    [IO.File]::Move($tempArchive,$archive)
    $declaration = ConvertTo-RC26Json $old | ConvertFrom-Json
    $declaration.version = 'sha-' + $archiveHash.Substring(0,16)
    $declaration.archiveUrl = (Get-UpdateDeployment $RC26Root).packOrigin + '/' + $filename
    $declaration.sha256 = $archiveHash; $declaration.sizeBytes = ([IO.FileInfo]$archive).Length
    $record = @($baseline.feed.files | Where-Object path -CEQ $RC26CatalogRelative)[0]
    $proof = [ordered]@{ schemaVersion = 1; version = 'kesas-rc26'; profile = 'potato'; passed = $true
        sourceCatalogSha256 = $record.sha256; oldArchiveSha256 = $old.sha256; newArchiveSha256 = $archiveHash
        sourceSha256 = $sourceHash; unchangedOtherEntries = $true; fullProfileUnchanged = $true; fixturesOnly = $true }
    $proofPath = "$stage/POLICY-TEST-RESULT.json"
    [IO.File]::WriteAllText($proofPath,(ConvertTo-RC26Json $proof),$RC26Utf8)
    $plan = [ordered]@{ schemaVersion = 1; version = 'kesas-rc26'; profile = 'potato'; sourceCatalogSha256 = $record.sha256
        oldDeclaration = $old; declaration = $declaration
        archive = [ordered]@{ path = "Media/$filename"; sha256 = $archiveHash; sizeBytes = ([IO.FileInfo]$archive).Length }
        source = [ordered]@{ path = 'Source/DisableExhaust.cfg'; sha256 = $sourceHash; sizeBytes = $bytes.Length }
        changedEntry = [ordered]@{ path = $entryPath; oldSha256 = $oldInventory[$entryPath].sha256; oldSizeBytes = $oldInventory[$entryPath].sizeBytes; sha256 = $sourceHash; sizeBytes = $bytes.Length }
        testEvidence = [ordered]@{ path = 'POLICY-TEST-RESULT.json'; sha256 = Get-RC26Hash $proofPath } }
    $planPath = "$stage/POLICY-PLAN.json"
    function Save-Plan($Value) { [IO.File]::WriteAllText($planPath,(ConvertTo-RC26Json $Value),$RC26Utf8) }
    Save-Plan $plan
    $valid = Get-RC26PotatoPolicyPlan $planPath (Get-RC26Hash $planPath) $baseline.catalog
    Check ($valid.snapshotFiles.Count -eq 4 -and $valid.file.sha256 -ceq $archiveHash) 'Valid bounded policy plan rejected.'
    foreach ($case in @('profile','baseline','id','destination','archive-path','source-hash','old-entry-hash','entry-path')) {
        $bad = ConvertTo-RC26Json $plan | ConvertFrom-Json
        switch ($case) {
            profile { $bad.profile = 'full' }
            baseline { $bad.sourceCatalogSha256 = '0' * 64 }
            id { $bad.declaration.id = 'kesas-rc25-modfixes' }
            destination { $bad.declaration.destination = 'OtherData' }
            archive-path { $bad.archive.path = '../escape.zip' }
            source-hash { $bad.source.sha256 = '0' * 64 }
            old-entry-hash { $bad.changedEntry.oldSha256 = '0' * 64 }
            entry-path { $bad.changedEntry.path = 'ZZZ_KESASPotato/Other.cfg' }
        }
        Save-Plan $bad
        Reject { Get-RC26PotatoPolicyPlan $planPath (Get-RC26Hash $planPath) $baseline.catalog } $case
    }
    Save-Plan $plan
    Reject { Get-RC26PotatoPolicyPlan $planPath ('0' * 64) $baseline.catalog } 'unreviewed plan hash'
    $badProof = ConvertTo-RC26Json $proof | ConvertFrom-Json
    $badProof.fullProfileUnchanged = $false
    [IO.File]::WriteAllText($proofPath,(ConvertTo-RC26Json $badProof),$RC26Utf8)
    $bad = ConvertTo-RC26Json $plan | ConvertFrom-Json; $bad.testEvidence.sha256 = Get-RC26Hash $proofPath
    Save-Plan $bad
    Reject { Get-RC26PotatoPolicyPlan $planPath (Get-RC26Hash $planPath) $baseline.catalog } 'failing Full-preservation evidence'
    [IO.File]::WriteAllText($proofPath,(ConvertTo-RC26Json $proof),$RC26Utf8); Save-Plan $plan
    $badCatalog = ConvertTo-RC26Json $baseline.catalog | ConvertFrom-Json
    $badCatalog.full.managedPackages += $old
    Reject { Get-RC26PotatoPolicyPlan $planPath (Get-RC26Hash $planPath) $badCatalog } 'policy present in Full'
    $assetPlan = Read-UpdateText "$originalWork/Assets/Staged/ASSET-PLAN.json" | ConvertFrom-Json
    $before = Get-RC26VersionCatalog $baseline.catalog @($assetPlan.media.declaration)
    $after = Get-RC26VersionCatalog $baseline.catalog @($assetPlan.media.declaration) $valid.declaration
    Check ((ConvertTo-RC26Json $before.full) -ceq (ConvertTo-RC26Json $after.full)) 'Full changed when applying Potato policy.'
    $changes = @()
    foreach ($package in $before.potato.managedPackages) {
        $next = @($after.potato.managedPackages | Where-Object id -CEQ $package.id)
        Check ($next.Count -eq 1) 'Potato package missing/duplicated.'
        if ((ConvertTo-RC26Json $package) -cne (ConvertTo-RC26Json $next[0])) { $changes += $package.id }
    }
    Check ($changes.Count -eq 1 -and $changes[0] -ceq 'kesas-potato-policy') 'Policy step changed a second package.'
    Check (@($after.full.managedPackages).Count -eq 211 -and @($after.potato.managedPackages).Count -eq 177) 'Profile counts changed.'
    foreach ($name in @('full','potato')) {
        foreach ($id in Get-RC26RepairIds) {
            $previous = @($baseline.catalog.$name.managedPackages | Where-Object id -CEQ $id)[0]
            $next = @($after.$name.managedPackages | Where-Object id -CEQ $id)[0]
            Check ($next.version -ceq ($previous.version + '-repair26') -and $next.sha256 -ceq $previous.sha256 -and $next.archiveUrl -ceq $previous.archiveUrl -and $next.sizeBytes -eq $previous.sizeBytes) 'Repair archive/cache identity changed.'
        }
    }
    foreach ($declaration in $assetPlan.media.declaration) {
        foreach ($name in @('full','potato')) {
            $actual = @($after.$name.managedPackages | Where-Object id -CEQ $declaration.id)[0]
            Check ((ConvertTo-RC26Json $actual) -ceq (ConvertTo-RC26Json $declaration)) 'Original five-asset plan changed.'
        }
    }
    $left = @([ordered]@{ path = 'a'; sha256 = 'b'; sizeBytes = 1 })
    $right = @([pscustomobject]@{ sizeBytes = 1; sha256 = 'b'; path = 'a' })
    Check ((ConvertTo-RC26InventoryJson $left) -ceq (ConvertTo-RC26InventoryJson $right)) 'Main inventory canonicalization regressed.'
} finally { $RC26Work = $originalWork }
Write-RC26Json "$($attempt.pending)/TEST-RESULT.json" ([ordered]@{ passed = $true; checks = $checks; syntheticFixturesOnly = $true
    fullUnchanged = $true; onePotatoPolicyReplacement = $true; originalFiveAssetsPreserved = $true; fourRepairArchiveIdentitiesPreserved = $true
    inventoryCanonicalizationPreserved = $true; releaseBuilt = $false; runtimeBuilt = $false; liveWrites = $false })
Complete-RC26Attempt $attempt
Write-Output "PASS $checks Potato policy pipeline checks: $($attempt.complete)/TEST-RESULT.json"
