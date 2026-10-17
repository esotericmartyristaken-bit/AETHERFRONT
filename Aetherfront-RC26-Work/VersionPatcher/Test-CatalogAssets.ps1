#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssetStage,
    [Parameter(Mandatory)][string]$AssetPlanSha256,
    [Parameter(Mandatory)][string]$AssetTestResultSha256
)
. "$PSScriptRoot/Review.Common.ps1"
$baseline = Get-RC26Baseline
$assets = Get-RC26AssetStage $AssetStage 'ASSET-PLAN.json' $AssetPlanSha256 (Join-Path $AssetStage 'ASSET-TEST-RESULT.json') $AssetTestResultSha256 $baseline.catalog
$catalog = Get-RC26VersionCatalog $baseline.catalog $assets.declarations
$checks = 0
function Check([bool]$Value,[string]$Message) { if (!$Value) { throw $Message }; $script:checks++ }
foreach ($profile in @('full','potato')) {
    $next = ConvertTo-RC26Json $catalog.$profile | ConvertFrom-Json
    Check (@($next.managedPackages).Count -eq @($baseline.catalog.$profile.managedPackages).Count) 'Profile package count changed.'
    $changed = @(); $repair = @(); $archive = @()
    foreach ($old in $baseline.catalog.$profile.managedPackages) {
        $rows = @($next.managedPackages | Where-Object id -CEQ $old.id)
        Check ($rows.Count -eq 1) 'Package missing or duplicated.'
        $package = $rows[0]
        if ((ConvertTo-RC26Json $package) -cne (ConvertTo-RC26Json $old)) { $changed += $old.id }
        if ($old.id -cin @(Get-RC26RepairIds)) {
            Check ($package.version -ceq ($old.version + '-repair26')) 'Migration package version was not bumped exactly once.'
            Check ($package.archiveUrl -ceq $old.archiveUrl -and $package.sha256 -ceq $old.sha256 -and $package.sizeBytes -eq $old.sizeBytes) 'Migration archive/cache identity changed.'
            $repair += $old.id; $package.version = $old.version
        } elseif ($old.id -cin $assets.declarations.id) {
            $expected = @($assets.declarations | Where-Object id -CEQ $old.id)
            Check ($expected.Count -eq 1 -and (ConvertTo-RC26Json $package) -ceq (ConvertTo-RC26Json $expected[0])) 'Reviewed asset declaration mismatch.'
            $archive += $old.id
            foreach ($property in @('version','archiveUrl','sha256','sizeBytes')) { $package.$property = $old.$property }
        }
        Check ((ConvertTo-RC26Json $package) -ceq (ConvertTo-RC26Json $old)) 'Unreviewed package field changed.'
    }
    Check ($changed.Count -eq 9 -and $repair.Count -eq 4 -and $archive.Count -eq 5) 'Exactly four repairs plus five asset replacements required.'
    $next.packVersion = 'kesas-rc25'; $next.requiredClientVersion = 'kesas-rc25'
    Check ((ConvertTo-RC26Json $next) -ceq (ConvertTo-RC26Json $baseline.catalog.$profile)) 'Profile policy changed outside revision routing.'
}
Check ($catalog.version -ceq 'kesas-rc26' -and @($catalog.full.managedPackages).Count -eq 211 -and @($catalog.potato.managedPackages).Count -eq 177) 'Catalog identity/counts mismatch.'
Check ((ConvertTo-RC26Json $catalog.gitHub) -ceq (ConvertTo-RC26Json $baseline.catalog.gitHub)) 'GitHub routing changed.'
$attempt = New-RC26Attempt 'Test'
Write-RC26Json "$($attempt.pending)/TEST-RESULT.json" ([ordered]@{ passed = $true; checks = $checks; assetPlanSha256 = $AssetPlanSha256
    assetTestResultSha256 = $AssetTestResultSha256; fullPackages = 211; potatoPackages = 177; repairsPerProfile = 4; assetReplacementsPerProfile = 5
    unchangedArchiveCacheIdentityForRepairs = $true; profilePolicyUnchanged = $true; stageZipDeltasVerified = $true
    engineBuilt = $false; runtimeCopied = $false; releaseBuilt = $false; liveWrites = $false })
Complete-RC26Attempt $attempt
Write-Output "PASS $checks real catalog/asset integration checks: $($attempt.complete)/TEST-RESULT.json"
